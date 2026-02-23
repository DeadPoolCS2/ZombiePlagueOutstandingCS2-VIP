using System.Drawing;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ZPOVIP;

[PluginMetadata(
    Id = "ZPOVIP",
    Version = "1.0.0",
    Name = "ZombiePlagueOutstandingCS2 VIP",
    Author = "DeadPoolCS2",
    Description = "Standalone VIP perks for HanZombiePlagueS2 (humans-only benefits). No VIP shop.")]
public class ZPOVIPPlugin(ISwiftlyCore core) : BasePlugin(core)
{
    private ILogger<ZPOVIPPlugin> _logger = null!;
    private ZPOVIPConfig _config = null!;
    private ServiceProvider? _sp;

    // HZP API object obtained at runtime via reflection (no compile-time dependency).
    private object? _hzpApiObj;
    // Cached reflection handle for the hot-path HZP_IsZombie call.
    private MethodInfo? _hzpIsZombieMethod;
    // Live reference to HZPGlobals.AmmoPacks obtained via reflection once at load time.
    private Dictionary<int, int>? _hzpAmmoPacks;

    // ── Per-round player state ────────────────────────────────────────────────
    // Damage accumulated toward the next AP reward, keyed by PlayerID.
    private readonly Dictionary<int, int> _damageAccumulator = new();
    // Remaining extra jumps this airborne sequence, keyed by PlayerID.
    private readonly Dictionary<int, int> _extraJumpsRemaining = new();
    // Whether the jump button was pressed in the previous tick (rising-edge detection).
    private readonly Dictionary<int, bool> _prevJumpPressed = new();
    // Players for whom the join-announce has already been broadcast this round.
    private readonly HashSet<int> _announcedThisRound = new();

    // ── Plugin lifecycle ──────────────────────────────────────────────────────

    public override void Load(bool hotReload)
    {
        // Load configuration from ZPOVIP.jsonc.
        Core.Configuration
            .InitializeJsonWithModel<ZPOVIPConfig>("ZPOVIP.jsonc", "ZPOVIP")
            .Configure(builder => builder.AddJsonFile("ZPOVIP.jsonc", false, true));

        var services = new ServiceCollection();
        services.AddSwiftly(Core);
        services.AddSingleton<ISwiftlyCore>(Core);
        services.AddOptionsWithValidateOnStart<ZPOVIPConfig>().BindConfiguration("ZPOVIP");

        _sp = services.BuildServiceProvider();
        _logger = _sp.GetRequiredService<ILogger<ZPOVIPPlugin>>();

        var monitor = _sp.GetRequiredService<IOptionsMonitor<ZPOVIPConfig>>();
        _config = monitor.CurrentValue;
        monitor.OnChange(cfg =>
        {
            _config = cfg;
            _logger.LogInformation("[ZPOVIP] Configuration reloaded.");
        });

        // Try to connect to HZP at runtime (best-effort; gracefully degraded if absent).
        _hzpApiObj = TryGetSharedInterfaceObject("HanZombiePlague");
        if (_hzpApiObj != null)
        {
            _hzpIsZombieMethod = _hzpApiObj.GetType().GetMethod("HZP_IsZombie");
            _hzpAmmoPacks = TryGetHZPAmmoPacksDict();
            _logger.LogInformation("[ZPOVIP] HZP API connected. AmmoPacks bridge: {b}",
                _hzpAmmoPacks != null ? "active" : "unavailable (chat-only rewards)");
            TrySubscribeHZPInfectEvent();
        }
        else
        {
            _logger.LogWarning("[ZPOVIP] HZP API not found. Zombie state falls back to team check (T = zombie).");
        }

        // Game event hooks.
        Core.GameEvent.HookPre<EventPlayerSpawn>(OnPlayerSpawn);
        Core.GameEvent.HookPre<EventPlayerDeath>(OnPlayerDeath);
        Core.GameEvent.HookPre<EventRoundEnd>(OnRoundEnd);
        Core.Event.OnEntityTakeDamage += OnEntityTakeDamage;
        Core.Event.OnTick += OnTick;
        Core.Event.OnClientDisconnected += OnClientDisconnected;

        // Register chat commands from config.
        Core.Command.RegisterCommand(_config.VipMenuCommand, VipMenuCommand, true);
        Core.Command.RegisterCommand(_config.VipsListCommand, VipsListCommand, true);

        _logger.LogInformation("[ZPOVIP] Loaded. Permission: '{p}', Commands: !{v} / !{vs}",
            _config.VIPPermission, _config.VipMenuCommand, _config.VipsListCommand);
    }

    public override void Unload()
    {
        _sp?.Dispose();
    }

    // ── VIP & zombie state helpers ────────────────────────────────────────────

    private bool IsVIP(IPlayer player)
    {
        if (player == null || !player.IsValid) return false;
        ulong steamId = player.SteamID;
        if (steamId == 0) return false;

        var permString = _config.VIPPermission;
        if (string.IsNullOrWhiteSpace(permString))
            return true; // Empty = everyone is VIP (testing mode).

        foreach (var perm in permString.Split(','))
        {
            var p = perm.Trim();
            if (p.Length > 0 && Core.Permission.PlayerHasPermission(steamId, p))
                return true;
        }
        return false;
    }

    private bool IsZombie(int playerId)
    {
        // Prefer the HZP API (accurate for all special roles: nemesis, survivor, etc.)
        if (_hzpIsZombieMethod != null && _hzpApiObj != null)
        {
            try
            {
                return (bool)(_hzpIsZombieMethod.Invoke(_hzpApiObj, [playerId]) ?? false);
            }
            catch { /* fall through to team fallback */ }
        }

        // Fallback: T-side = zombie in a standard HZP server setup.
        var player = Core.PlayerManager.GetPlayer(playerId);
        return player?.Controller?.Team == Team.T;
    }

    // ── Ammo-pack bridge (HZP integration via reflection) ────────────────────

    /// <summary>
    /// Writes directly to HZP's live AmmoPacks dictionary (via reflection).
    /// Falls back to a chat-only notification when the bridge is unavailable.
    /// </summary>
    private void AddAmmoPacks(int playerId, int amount, IPlayer? player = null)
    {
        if (amount <= 0) return;

        // Lazy re-init: HZP may have loaded after us.
        if (_hzpAmmoPacks == null && _hzpApiObj != null)
            _hzpAmmoPacks = TryGetHZPAmmoPacksDict();

        player ??= Core.PlayerManager.GetPlayer(playerId);
        if (player == null || !player.IsValid) return;

        if (_hzpAmmoPacks != null)
        {
            _hzpAmmoPacks.TryGetValue(playerId, out int current);
            _hzpAmmoPacks[playerId] = Math.Max(0, current + amount);
            int newTotal = _hzpAmmoPacks[playerId];
            SendChat(player, $"\x04+{amount}\x01 Ammo Packs (VIP) | Total: \x06{newTotal}");
        }
        else
        {
            // Bridge unavailable – notify via chat only (no persistent storage).
            SendChat(player, $"\x04+{amount}\x01 VIP AP reward (HZP bridge offline)");
        }
    }

    // ── Event: player spawn ───────────────────────────────────────────────────

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        var player = @event.UserIdPlayer;
        if (player == null || !player.IsValid) return HookResult.Continue;

        var controller = @event.UserIdController;
        if (controller == null || !controller.IsValid) return HookResult.Continue;

        int id = player.PlayerID;

        // Reset per-spawn jump state.
        _extraJumpsRemaining.Remove(id);
        _prevJumpPressed.Remove(id);

        // Defer one world-update tick so pawn state is fully initialised.
        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (player == null || !player.IsValid) return;
            if (IsZombie(id)) return;  // VIP perks apply to humans only.
            if (!IsVIP(player)) return;

            var pawn = player.PlayerPawn;
            if (pawn == null || !pawn.IsValid) return;

            // ── Armor ───────────────────────────────────────────────────────
            if (_config.ArmorAmount > 0 && pawn.ArmorValue < _config.ArmorAmount)
            {
                pawn.ArmorValue = _config.ArmorAmount;
                pawn.ArmorValueUpdated();
            }

            // ── Multi-jump allowance ────────────────────────────────────────
            if (_config.ExtraJumps > 0)
                _extraJumpsRemaining[id] = _config.ExtraJumps;

            // ── Join announce (once per round per VIP) ──────────────────────
            if (_config.JoinAnnounceEnabled && _announcedThisRound.Add(id))
            {
                string name = controller.PlayerName ?? player.Name ?? "Player";
                BroadcastChat($" \x04{_config.ChatPrefix}\x01 VIP \x06{name}\x01 has joined the game!");
            }
        });

        return HookResult.Continue;
    }

    // ── Event: player death ───────────────────────────────────────────────────

    private HookResult OnPlayerDeath(EventPlayerDeath @event)
    {
        var victim = @event.UserIdPlayer;
        if (victim == null || !victim.IsValid) return HookResult.Continue;

        int victimId = victim.PlayerID;
        _extraJumpsRemaining.Remove(victimId);
        _prevJumpPressed.Remove(victimId);

        // Kill reward only fires when a VIP human kills a zombie.
        if (!IsZombie(victimId)) return HookResult.Continue;

        var attacker = Core.PlayerManager.GetPlayer(@event.Attacker);
        if (attacker == null || !attacker.IsValid) return HookResult.Continue;
        if (IsZombie(attacker.PlayerID)) return HookResult.Continue;
        if (!IsVIP(attacker)) return HookResult.Continue;

        // Base kill reward.
        if (_config.KillRewardAmount > 0)
            AddAmmoPacks(attacker.PlayerID, _config.KillRewardAmount, attacker);

        // Happy-hour bonuses.
        if (IsHappyHour())
        {
            if (_config.KillRewardHappyHourBonus && _config.HappyHourBonusAP > 0)
                AddAmmoPacks(attacker.PlayerID, _config.HappyHourBonusAP, attacker);

            if (_config.HappyHourBonusFrags > 0)
            {
                var atkCtrl = attacker.Controller;
                if (atkCtrl != null && atkCtrl.IsValid)
                {
                    var ats = atkCtrl.ActionTrackingServices;
                    if (ats != null && ats.IsValid)
                    {
                        ats.MatchStats.Kills += _config.HappyHourBonusFrags;
                        atkCtrl.CompetitiveRankingPredicted_Win++;
                    }
                }
            }
        }

        return HookResult.Continue;
    }

    // ── Event: round end ──────────────────────────────────────────────────────

    private HookResult OnRoundEnd(EventRoundEnd @event)
    {
        _damageAccumulator.Clear();
        _extraJumpsRemaining.Clear();
        _prevJumpPressed.Clear();
        _announcedThisRound.Clear();
        return HookResult.Continue;
    }

    // ── Event: client disconnected ────────────────────────────────────────────

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        int id = @event.PlayerId;
        _damageAccumulator.Remove(id);
        _extraJumpsRemaining.Remove(id);
        _prevJumpPressed.Remove(id);
        _announcedThisRound.Remove(id);
    }

    // ── Event: entity take damage ─────────────────────────────────────────────

    private void OnEntityTakeDamage(IOnEntityTakeDamageEvent @event)
    {
        var victimEntity = @event.Entity;
        if (victimEntity == null || !victimEntity.IsValid) return;

        var victimPawn = victimEntity.As<CCSPlayerPawn>();
        if (victimPawn == null || !victimPawn.IsValid) return;

        var victimController = victimPawn.Controller.Value?.As<CCSPlayerController>();
        if (victimController == null || !victimController.IsValid) return;

        var victimPlayer = Core.PlayerManager.GetPlayer((int)(victimController.Index - 1));
        if (victimPlayer == null || !victimPlayer.IsValid) return;

        int victimId = victimPlayer.PlayerID;
        bool victimIsZombie = IsZombie(victimId);

        // ── Fall damage prevention (VIP human, no-weapon, self-inflicted) ───
        if (!victimIsZombie && IsVIP(victimPlayer) && _config.NoFallDamage
            && @event.Info.AmmoType == -1)
        {
            bool isSelfInflicted = false;
            var attackerEnt = @event.Info.Attacker.Value;

            if (attackerEnt == null || !attackerEnt.IsValid)
            {
                // Null / world attacker = environment damage (fall, trigger_hurt, etc.)
                isSelfInflicted = true;
            }
            else
            {
                var atkPawn = attackerEnt.As<CCSPlayerPawn>();
                if (atkPawn != null && atkPawn.IsValid)
                {
                    var atkCtrl = atkPawn.Controller.Value?.As<CCSPlayerController>();
                    // Same controller index → same player → self-inflicted fall damage.
                    isSelfInflicted = atkCtrl != null
                        && atkCtrl.IsValid
                        && atkCtrl.Index == victimController.Index;
                }
                else
                {
                    // Non-player inflictor (world brush, etc.) → treat as environmental.
                    isSelfInflicted = true;
                }
            }

            if (isSelfInflicted)
            {
                @event.Info.Damage = 0f;
                return;
            }
        }

        // ── Resolve attacker as a player ─────────────────────────────────────
        var attackerEntity = @event.Info.Attacker.Value;
        if (attackerEntity == null || !attackerEntity.IsValid) return;

        var attackerPawn = attackerEntity.As<CCSPlayerPawn>();
        if (attackerPawn == null || !attackerPawn.IsValid) return;

        var attackerController = attackerPawn.Controller.Value?.As<CCSPlayerController>();
        if (attackerController == null || !attackerController.IsValid) return;

        var attackerPlayer = Core.PlayerManager.GetPlayer((int)(attackerController.Index - 1));
        if (attackerPlayer == null || !attackerPlayer.IsValid) return;

        int attackerId = attackerPlayer.PlayerID;
        bool attackerIsZombie = IsZombie(attackerId);

        // Only process VIP human → zombie damage from this point on.
        if (attackerIsZombie || !victimIsZombie || !IsVIP(attackerPlayer)) return;

        // ── Damage multiplier ────────────────────────────────────────────────
        float mult = _config.DamageMultiplier;
        if (mult > 1.0f)
        {
            bool applyMult = true;
            if (_config.ExcludeHEGrenade)
            {
                var inflictor = @event.Info.Inflictor.Value;
                if (inflictor != null && inflictor.IsValid
                    && inflictor.DesignerName.Contains("hegrenade", StringComparison.OrdinalIgnoreCase))
                    applyMult = false;
            }
            if (applyMult)
                @event.Info.Damage *= mult;
        }

        // ── Damage-based AP reward ────────────────────────────────────────────
        int threshold = _config.DamageRewardThreshold;
        int rewardAmt = _config.DamageRewardAmount;
        if (threshold > 0 && rewardAmt > 0)
        {
            int dmg = (int)@event.Info.Damage;
            _damageAccumulator.TryGetValue(attackerId, out int acc);
            acc += dmg;
            int packs = acc / threshold;
            if (packs > 0)
            {
                acc -= packs * threshold;
                AddAmmoPacks(attackerId, packs * rewardAmt, attackerPlayer);
            }
            _damageAccumulator[attackerId] = acc;
        }
    }

    // ── Event: per-tick (multi-jump) ─────────────────────────────────────────

    private void OnTick()
    {
        if (_config.ExtraJumps <= 0) return;

        foreach (var player in Core.PlayerManager.GetAlive())
        {
            if (player == null || !player.IsValid) continue;

            int id = player.PlayerID;
            if (IsZombie(id) || !IsVIP(player)) continue;

            var pawn = player.PlayerPawn;
            if (pawn == null || !pawn.IsValid) continue;

            bool onGround = pawn.GroundEntity.IsValid;
            if (onGround)
            {
                // Replenish jump allowance whenever the player touches ground.
                _extraJumpsRemaining[id] = _config.ExtraJumps;
                _prevJumpPressed[id] = (player.PressedButtons & GameButtonFlags.Space) != 0;
                continue;
            }

            if (!_extraJumpsRemaining.TryGetValue(id, out int jumpsLeft) || jumpsLeft <= 0)
                continue;

            bool jumpNow = (player.PressedButtons & GameButtonFlags.Space) != 0;
            _prevJumpPressed.TryGetValue(id, out bool jumpPrev);
            _prevJumpPressed[id] = jumpNow;

            // Rising edge: button pressed this tick but not last tick.
            if (!jumpNow || jumpPrev) continue;

            // Consume one jump charge and apply upward impulse.
            _extraJumpsRemaining[id] = jumpsLeft - 1;
            var vel = pawn.AbsVelocity;
            pawn.Teleport(null, null,
                new SwiftlyS2.Shared.Natives.Vector(vel.X, vel.Y, _config.JumpVelocity));
        }
    }

    // ── HZP infect-reward callback ────────────────────────────────────────────

    /// <summary>
    /// Invoked via reflection when HZP fires HZP_OnPlayerInfect.
    /// Signature matches: Action&lt;IPlayer, IPlayer, bool, string&gt;
    /// </summary>
    private void OnHZPPlayerInfect(IPlayer attacker, IPlayer victim, bool grenade, string zombieClass)
    {
        if (!_config.InfectRewardsEnabled) return;
        if (attacker == null || !attacker.IsValid) return;
        if (!IsVIP(attacker)) return;

        int attackerId = attacker.PlayerID;

        if (_config.InfectRewardAP > 0)
            AddAmmoPacks(attackerId, _config.InfectRewardAP, attacker);

        if (_config.InfectRewardHealth > 0)
        {
            Core.Scheduler.NextWorldUpdate(() =>
            {
                if (attacker == null || !attacker.IsValid) return;
                var pawn = attacker.PlayerPawn;
                if (pawn == null || !pawn.IsValid) return;
                pawn.Health = Math.Min(pawn.Health + _config.InfectRewardHealth, pawn.MaxHealth);
                pawn.HealthUpdated();
            });
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>
    /// !vip – opens a SwiftlyS2 menu that lists VIP benefits.
    /// Benefit lines are fully customisable in the config (BenefitLines).
    /// </summary>
    private void VipMenuCommand(ICommandContext context)
    {
        var player = context.Sender;
        if (player == null || !player.IsValid) return;

        bool isVip   = IsVIP(player);
        bool happyNow = IsHappyHour();

        // Determine the lines to display.
        var lines = _config.BenefitLines;
        bool autoGenerate = lines == null || lines.Count == 0;

        // Build menu.
        MenuConfiguration cfg = new()
        {
            Title = HtmlGradient.GenerateGradientText(
                _config.VipMenuTitle,
                Color.Gold, Color.Orange),
            FreezePlayer = false,
            MaxVisibleItems = 8,
            PlaySound = false,
            AutoIncreaseVisibleItems = false,
            HideFooter = false
        };

        MenuKeybindOverrides keys = new()
        {
            Move     = KeyBind.S,
            MoveBack = KeyBind.W,
            Exit     = KeyBind.Shift,
            Select   = KeyBind.E
        };

        IMenuAPI menu = Core.MenusAPI.CreateMenu(cfg,
            keybindOverrides: keys,
            optionScrollStyle: MenuOptionScrollStyle.WaitingCenter);

        cfg.DefaultComment =
            HtmlGradient.GenerateGradientText("[W/S]", Color.Crimson) + " Navigate  " +
            HtmlGradient.GenerateGradientText("[SHIFT]", Color.Crimson) + " Close";

        if (autoGenerate)
        {
            // Auto-generate benefit lines from active settings.
            AddDisplayLine(menu, $"★ Armor on spawn: +{_config.ArmorAmount}");
            AddDisplayLine(menu, $"★ Extra jumps: {_config.ExtraJumps}");
            AddDisplayLine(menu, $"★ Fall damage: {(_config.NoFallDamage ? "Disabled" : "Normal")}");
            AddDisplayLine(menu, $"★ Damage multiplier: ×{_config.DamageMultiplier:F1} vs zombies");
            if (_config.KillRewardAmount > 0)
                AddDisplayLine(menu, $"★ Kill reward: +{_config.KillRewardAmount} AP per zombie kill");
            if (_config.DamageRewardThreshold > 0)
                AddDisplayLine(menu, $"★ Damage reward: +{_config.DamageRewardAmount} AP per {_config.DamageRewardThreshold} dmg");
            if (_config.HappyHourEnabled)
            {
                string hhStatus = happyNow ? " [ACTIVE]" : "";
                AddDisplayLine(menu, $"★ Happy Hour {_config.HappyHourStart:D2}:00–{_config.HappyHourEnd:D2}:00{hhStatus}");
                AddDisplayLine(menu, $"  +{_config.HappyHourBonusAP} AP & +{_config.HappyHourBonusFrags} frags per kill");
            }
        }
        else
        {
            // Show server-admin's custom lines from config.
            foreach (var line in lines!)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    AddDisplayLine(menu, line);
            }
        }

        // Status footer.
        AddDisplayLine(menu, "─────────────────────────");
        AddDisplayLine(menu, isVip
            ? "✓ You are a VIP player"
            : "✗ You are not a VIP player");
        if (_config.HappyHourEnabled && happyNow)
            AddDisplayLine(menu, "★ Happy Hour is ACTIVE now!");

        Core.MenusAPI.OpenMenuForPlayer(player, menu);
    }

    /// <summary>
    /// !vips – opens a SwiftlyS2 menu listing all VIP players currently online.
    /// </summary>
    private void VipsListCommand(ICommandContext context)
    {
        var caller = context.Sender;
        if (caller == null || !caller.IsValid) return;

        var vipNames = new List<string>();
        foreach (var p in Core.PlayerManager.GetAllPlayers())
        {
            if (p == null || !p.IsValid || p.IsFakeClient) continue;
            if (IsVIP(p))
                vipNames.Add(p.Controller?.PlayerName ?? p.Name ?? "?");
        }

        MenuConfiguration cfg = new()
        {
            Title = HtmlGradient.GenerateGradientText(
                $"Online VIPs ({vipNames.Count})",
                Color.Gold, Color.Orange),
            FreezePlayer = false,
            MaxVisibleItems = 8,
            PlaySound = false,
            AutoIncreaseVisibleItems = false,
            HideFooter = false
        };

        MenuKeybindOverrides keys = new()
        {
            Move     = KeyBind.S,
            MoveBack = KeyBind.W,
            Exit     = KeyBind.Shift,
            Select   = KeyBind.E
        };

        IMenuAPI menu = Core.MenusAPI.CreateMenu(cfg,
            keybindOverrides: keys,
            optionScrollStyle: MenuOptionScrollStyle.WaitingCenter);

        cfg.DefaultComment =
            HtmlGradient.GenerateGradientText("[W/S]", Color.Crimson) + " Navigate  " +
            HtmlGradient.GenerateGradientText("[SHIFT]", Color.Crimson) + " Close";

        if (vipNames.Count == 0)
        {
            AddDisplayLine(menu, HtmlGradient.GenerateGradientText(
                "No VIPs are online at the moment", Color.Gray));
        }
        else
        {
            foreach (var name in vipNames)
            {
                AddDisplayLine(menu, HtmlGradient.GenerateGradientText(
                    $"★ {name}", Color.Gold, Color.Orange));
            }
        }

        Core.MenusAPI.OpenMenuForPlayer(caller, menu);
    }

    // ── Menu helper ───────────────────────────────────────────────────────────

    /// <summary>Adds a non-selectable display line to <paramref name="menu"/>.</summary>
    private static void AddDisplayLine(IMenuAPI menu, string text)
        => menu.AddOption(new TextMenuOption(text));

    // ── Happy-hour helper ─────────────────────────────────────────────────────

    private bool IsHappyHour()
    {
        if (!_config.HappyHourEnabled) return false;
        int hour  = DateTime.Now.Hour;
        int start = _config.HappyHourStart;
        int end   = _config.HappyHourEnd;
        // Handles overnight ranges (e.g. start=19, end=8 → 19–23 and 0–7).
        return start <= end
            ? hour >= start && hour < end
            : hour >= start || hour < end;
    }

    // ── Chat helpers ──────────────────────────────────────────────────────────

    private void SendChat(IPlayer player, string msg)
        => player.SendMessage(MessageType.Chat, $" \x04{_config.ChatPrefix}\x01 {msg}");

    private void BroadcastChat(string msg)
    {
        foreach (var p in Core.PlayerManager.GetAllPlayers())
        {
            if (p == null || !p.IsValid || p.IsFakeClient) continue;
            p.SendMessage(MessageType.Chat, msg);
        }
    }

    // ── HZP API reflection helpers ────────────────────────────────────────────

    /// <summary>
    /// Retrieves the raw shared-interface object registered under <paramref name="name"/>
    /// without any compile-time reference to the provider's assembly.
    /// </summary>
    private object? TryGetSharedInterfaceObject(string name)
    {
        try
        {
            Type? t = Core.GetType();
            while (t != null)
            {
                var siProp = t.GetProperty("SharedInterface",
                    BindingFlags.Public | BindingFlags.Instance);
                if (siProp != null)
                {
                    var si = siProp.GetValue(Core);
                    if (si != null)
                    {
                        foreach (var m in si.GetType()
                                             .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                             .Where(m => m.Name == "GetSharedInterface"))
                        {
                            try
                            {
                                var concrete = m.IsGenericMethod
                                    ? m.MakeGenericMethod(typeof(object))
                                    : m;
                                var result = concrete.Invoke(si, [name]);
                                if (result != null) return result;
                            }
                            catch { }
                        }
                    }
                    break;
                }
                t = t.BaseType;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("[ZPOVIP] TryGetSharedInterfaceObject('{n}'): {m}", name, ex.Message);
        }
        return null;
    }

    /// <summary>
    /// Follows the chain HanZombiePlagueAPI._globals → HZPGlobals.AmmoPacks
    /// via reflection and returns the live dictionary, or null on failure.
    /// </summary>
    private Dictionary<int, int>? TryGetHZPAmmoPacksDict()
    {
        if (_hzpApiObj == null) return null;
        try
        {
            var gf = _hzpApiObj.GetType()
                .GetField("_globals", BindingFlags.NonPublic | BindingFlags.Instance);
            var globals = gf?.GetValue(_hzpApiObj);
            if (globals == null) return null;

            var af = globals.GetType()
                .GetField("AmmoPacks", BindingFlags.Public | BindingFlags.Instance);
            return af?.GetValue(globals) as Dictionary<int, int>;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("[ZPOVIP] AmmoPacks bridge: {m}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Subscribes <see cref="OnHZPPlayerInfect"/> to the HZP_OnPlayerInfect event
    /// using reflection (avoids compile-time dependency on HZP assemblies).
    /// </summary>
    private void TrySubscribeHZPInfectEvent()
    {
        if (_hzpApiObj == null) return;
        try
        {
            var ev = _hzpApiObj.GetType().GetEvent("HZP_OnPlayerInfect");
            if (ev == null) { _logger.LogDebug("[ZPOVIP] HZP_OnPlayerInfect not found."); return; }

            Action<IPlayer, IPlayer, bool, string> handler = OnHZPPlayerInfect;
            var delegateType = ev.EventHandlerType ?? typeof(Action<IPlayer, IPlayer, bool, string>);
            ev.AddEventHandler(_hzpApiObj,
                Delegate.CreateDelegate(delegateType, handler.Target, handler.Method));

            _logger.LogInformation("[ZPOVIP] Subscribed to HZP_OnPlayerInfect.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[ZPOVIP] HZP_OnPlayerInfect subscribe: {m}", ex.Message);
        }
    }
}
