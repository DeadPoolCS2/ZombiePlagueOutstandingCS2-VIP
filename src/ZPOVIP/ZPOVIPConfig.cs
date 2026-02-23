namespace ZPOVIP;

/// <summary>
/// Configuration model for the ZombiePlagueOutstandingCS2-VIP plugin.
/// Loaded from configs/ZPOVIP.jsonc (SwiftlyS2 config directory).
/// Hot-reload is supported: changes take effect without restarting the server.
/// </summary>
public class ZPOVIPConfig
{
    // ── VIP Detection ────────────────────────────────────────────────────────

    /// <summary>
    /// Comma-separated Swiftly permission flags that grant VIP status.
    /// A player matching ANY flag is treated as VIP.
    /// Leave empty ("") to make every player VIP (useful for testing).
    /// </summary>
    public string VIPPermission { get; set; } = "@zpovip/vip";

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Chat command that opens the VIP benefits menu (without leading slash/exclamation).
    /// Default: "vip"  →  players type !vip or /vip in chat.
    /// </summary>
    public string VipMenuCommand { get; set; } = "vip";

    /// <summary>
    /// Chat command that opens the online VIPs list menu.
    /// Default: "vips"  →  players type !vips or /vips in chat.
    /// </summary>
    public string VipsListCommand { get; set; } = "vips";

    // ── VIP Benefits Menu ────────────────────────────────────────────────────

    /// <summary>
    /// Title shown at the top of the VIP benefits menu.
    /// </summary>
    public string VipMenuTitle { get; set; } = "VIP Benefits";

    /// <summary>
    /// Lines displayed as non-selectable items in the !vip benefits menu.
    /// Fully customisable – server admins can write any text here.
    /// Supports basic HTML colour tags used by SwiftlyS2 menus.
    /// If the list is empty, the menu auto-generates lines from active perk settings.
    /// </summary>
    public List<string> BenefitLines { get; set; } =
    [
        "★ Armor on spawn",
        "★ Double Jump (extra mid-air jumps)",
        "★ No Fall Damage",
        "★ ×1.5 Damage vs Zombies",
        "★ AP reward every 500 damage dealt",
        "★ +2 AP per Zombie Kill",
        "★ Happy Hour: bonus AP & frags",
    ];

    // ── Announce ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When true, a chat message is broadcast to all players when a VIP
    /// spawns for the first time in a round.
    /// </summary>
    public bool JoinAnnounceEnabled { get; set; } = true;

    /// <summary>Prefix prepended to all ZPOVIP chat messages.</summary>
    public string ChatPrefix { get; set; } = "[VIP]";

    // ── Armor on Spawn ───────────────────────────────────────────────────────

    /// <summary>
    /// Minimum armor value guaranteed to a VIP human on spawn.
    /// Set to 0 to disable. Equivalent to AMXX cvar zp_vip_armor.
    /// </summary>
    public int ArmorAmount { get; set; } = 100;

    // ── Multi-Jump ───────────────────────────────────────────────────────────

    /// <summary>
    /// Number of extra mid-air jumps granted to VIP humans.
    /// 1 = double-jump, 2 = triple-jump, etc.  Set to 0 to disable.
    /// Equivalent to AMXX cvar zp_vip_extrajumps.
    /// </summary>
    public int ExtraJumps { get; set; } = 1;

    /// <summary>Upward velocity impulse per extra jump (units/s).</summary>
    public float JumpVelocity { get; set; } = 300f;

    // ── No Fall Damage ───────────────────────────────────────────────────────

    /// <summary>
    /// When true, VIP humans take no fall damage.
    /// Equivalent to AMXX cvar zp_vip_falldamage.
    /// </summary>
    public bool NoFallDamage { get; set; } = true;

    // ── Damage Multiplier ────────────────────────────────────────────────────

    /// <summary>
    /// Damage multiplier applied when a VIP human shoots a zombie.
    /// 1.0 = normal, 1.5 = 50 % bonus.  Set to 1.0 to disable.
    /// Equivalent to AMXX cvar zp_vip_damage.
    /// </summary>
    public float DamageMultiplier { get; set; } = 1.5f;

    /// <summary>When true, the multiplier is NOT applied to HE grenade damage.</summary>
    public bool ExcludeHEGrenade { get; set; } = true;

    // ── Damage Reward ────────────────────────────────────────────────────────

    /// <summary>
    /// Accumulated damage to zombies required to earn one reward batch.
    /// Set to 0 to disable. Equivalent to AMXX cvar zp_vip_dmgreward_threshold.
    /// </summary>
    public int DamageRewardThreshold { get; set; } = 500;

    /// <summary>Ammo packs awarded per threshold of damage dealt.</summary>
    public int DamageRewardAmount { get; set; } = 1;

    // ── Kill Reward ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ammo packs awarded to a VIP human for killing a zombie.
    /// Set to 0 to disable. Equivalent to AMXX cvar zp_vip_killammo.
    /// </summary>
    public int KillRewardAmount { get; set; } = 2;

    /// <summary>Whether the happy-hour AP bonus also applies to kill rewards.</summary>
    public bool KillRewardHappyHourBonus { get; set; } = true;

    // ── Happy Hour ───────────────────────────────────────────────────────────

    /// <summary>
    /// Enable happy hour (increased rewards during a configurable time window).
    /// Equivalent to AMXX cvar zp_vip_happyhour_enable.
    /// </summary>
    public bool HappyHourEnabled { get; set; } = true;

    /// <summary>
    /// Start hour of happy hour (24-h format, 0–23).
    /// Set Start > End to wrap overnight (e.g. Start=19, End=8 → 19:00–07:59).
    /// </summary>
    public int HappyHourStart { get; set; } = 19;

    /// <summary>End hour of happy hour (24-h format, 0–23).</summary>
    public int HappyHourEnd { get; set; } = 8;

    /// <summary>Extra ammo packs per zombie kill during happy hour.</summary>
    public int HappyHourBonusAP { get; set; } = 2;

    /// <summary>Extra score/frags per zombie kill during happy hour.</summary>
    public int HappyHourBonusFrags { get; set; } = 1;

    // ── Infect Reward ────────────────────────────────────────────────────────

    /// <summary>
    /// When true, a VIP player who infects a human (while playing as zombie)
    /// earns AP and/or a health bonus.  Disabled by default.
    /// Requires the HZP API to be present (subscribes to HZP_OnPlayerInfect).
    /// Equivalent to AMXX cvar zp_vip_infectammo / zp_vip_infecthealth.
    /// </summary>
    public bool InfectRewardsEnabled { get; set; } = false;

    /// <summary>Ammo packs awarded to VIP infector on successful infection.</summary>
    public int InfectRewardAP { get; set; } = 1;

    /// <summary>Health bonus awarded to VIP infector on infection. 0 = disabled.</summary>
    public int InfectRewardHealth { get; set; } = 500;
}
