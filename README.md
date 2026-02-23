# ZombiePlagueOutstandingCS2-VIP

Standalone SwiftlyS2 CS2 plugin that reproduces the VIP perks from the classic
AMXX Zombie Plague `zm_vip.sma` plugin — **humans-only benefits, no VIP shop**.

Designed to run alongside [ZombiePlagueOutstandingCS2 (HanZombiePlagueS2)](https://github.com/DeadPoolCS2/ZombiePlagueOutstandingCS2)
and integrates with its ammo-pack system at runtime via reflection (no hard
compile-time dependency required).

---

## Features

| Perk | Config key | AMXX equivalent |
|---|---|---|
| **Armor on spawn** | `ArmorAmount` | `zp_vip_armor` |
| **Multi-jump** (extra mid-air jumps) | `ExtraJumps` | `zp_vip_extrajumps` |
| **No fall damage** | `NoFallDamage` | `zp_vip_falldamage` |
| **Damage multiplier** vs zombies | `DamageMultiplier` | `zp_vip_damage` |
| **Damage reward** (AP per X damage) | `DamageRewardThreshold` / `DamageRewardAmount` | `zp_vip_dmgreward_*` |
| **Kill reward** (AP per zombie kill) | `KillRewardAmount` | `zp_vip_killammo` |
| **Happy Hour** (bonus AP + frags, time-based) | `HappyHour*` | `zp_vip_happyhour_*` |
| **Infect reward** (optional, VIP-as-zombie) | `InfectRewardsEnabled` | `zp_vip_infectammo/health` |
| **Join announce** | `JoinAnnounceEnabled` | — |
| **`!vip` command** – SwiftlyS2 menu with configurable benefit lines | `BenefitLines` | — |
| **`!vips` command** – SwiftlyS2 menu listing online VIPs | `VipsListCommand` | — |

---

## Requirements

- **CS2** dedicated server with [SwiftlyS2](https://github.com/swiftly-solution/swiftly) loaded.
- **[ZombiePlagueOutstandingCS2 (HanZombiePlagueS2)](https://github.com/DeadPoolCS2/ZombiePlagueOutstandingCS2)**
  installed and running alongside this plugin.  
  The plugin functions without HZP (zombie detection falls back to team-side),
  but ammo-pack integration and infect rewards require HZP to be present at runtime.
- `.NET 10` SDK (for building from source).

---

## Installation

### Pre-built

1. Build the plugin (see *Building* below).
2. Copy the output files to your server:
   ```
   csgo/addons/swiftly/plugins/ZPOVIP/ZPOVIP.dll
   ```
3. Copy the default config:
   ```
   csgo/addons/swiftly/configs/ZPOVIP.jsonc
   ```
4. Edit `ZPOVIP.jsonc` to set your permission flag and tune the perks.
5. (Re)start or hot-reload the plugin: `sw_plugins load ZPOVIP`

### Building from source

```bash
git clone https://github.com/DeadPoolCS2/ZombiePlagueOutstandingCS2-VIP
cd ZombiePlagueOutstandingCS2-VIP
dotnet build src/ZPOVIP/ZPOVIP.csproj -c Release
```

> **HZP API reference (optional)**  
> To get compile-time IntelliSense for HZP types, uncomment one of the reference
> blocks at the bottom of `src/ZPOVIP/ZPOVIP.csproj` and adjust the path or
> `HZPApiDllPath` build property to point to `HanZombiePlagueAPI.dll` from the
> ZombiePlagueOutstandingCS2 build.  
> This is **not** required — the plugin uses reflection at runtime.

---

## Configuration

All settings live in `csgo/addons/swiftly/configs/ZPOVIP.jsonc`.
The file supports **hot-reload**: save it while the server runs and changes apply instantly.

### Customisable VIP Benefits Menu (`BenefitLines`)

The `!vip` command opens a **SwiftlyS2 menu** that lists your VIP benefits.
The lines shown are **completely customisable** from the config:

```jsonc
"BenefitLines": [
  "★ Armor on spawn (+100)",
  "★ Double Jump (1 extra mid-air jump)",
  "★ No Fall Damage",
  "★ x1.5 Damage dealt to Zombies",
  "★ +1 AP per 500 damage dealt to Zombies",
  "★ +2 AP per Zombie Kill",
  "★ Happy Hour: extra AP & frags (19:00-08:00)"
]
```

- You can add, remove, or reword any line.
- SwiftlyS2 HTML colour tags are supported.
- Setting `BenefitLines` to an **empty list** (`[]`) auto-generates the lines
  from the active perk values in the same config.

### Full Config Reference

```jsonc
{
  "ZPOVIP": {

    // VIP permission flags (comma-separated, any match = VIP).
    // Leave "" to make every player VIP.
    "VIPPermission": "@zpovip/vip",

    // Commands (without leading ! or /).
    "VipMenuCommand":  "vip",
    "VipsListCommand": "vips",

    // Title shown at the top of the !vip benefits menu.
    "VipMenuTitle": "VIP Benefits",

    // Customisable benefit lines shown in the !vip menu.
    // Set to [] to auto-generate from perk settings below.
    "BenefitLines": [ "★ ...", "★ ..." ],

    // Join announce: broadcast a chat msg on VIP's first spawn per round.
    "JoinAnnounceEnabled": true,
    "ChatPrefix": "[VIP]",

    // Armor: minimum armor on spawn (0 = disabled).
    "ArmorAmount": 100,

    // Multi-jump: extra mid-air jumps (0 = disabled).
    "ExtraJumps": 1,
    "JumpVelocity": 300.0,

    // Fall damage: true = VIP humans take no fall damage.
    "NoFallDamage": true,

    // Damage multiplier vs zombies (1.0 = no bonus).
    "DamageMultiplier": 1.5,
    "ExcludeHEGrenade": true,

    // Damage-based AP reward.
    "DamageRewardThreshold": 500,   // 0 = disabled
    "DamageRewardAmount": 1,

    // Kill reward: AP per zombie kill.
    "KillRewardAmount": 2,          // 0 = disabled
    "KillRewardHappyHourBonus": true,

    // Happy Hour time window (24-h, Start > End = wraps overnight).
    "HappyHourEnabled": true,
    "HappyHourStart": 19,
    "HappyHourEnd": 8,
    "HappyHourBonusAP": 2,
    "HappyHourBonusFrags": 1,

    // Infect reward (disabled by default; fires when VIP IS a zombie).
    "InfectRewardsEnabled": false,
    "InfectRewardAP": 1,
    "InfectRewardHealth": 500
  }
}
```

---

## Permissions

Add the permission flag to your Swiftly permissions config
(e.g. `csgo/addons/swiftly/configs/permissions.jsonc`):

```jsonc
{
  "Permissions": [
    {
      "SteamID": "STEAM_1:0:12345678",
      "Flags": ["@zpovip/vip"]
    }
  ]
}
```

You can use any flag string you like — just make sure it matches `VIPPermission`
in the plugin config.  Multiple flags may be listed (comma-separated in the plugin
config); a player matching **any** of them is treated as VIP.

---

## Commands

| Command | Description |
|---|---|
| `!vip` / `/vip` | Opens a **SwiftlyS2 menu** listing your VIP benefits (configurable via `BenefitLines`). Also shows your VIP status and whether Happy Hour is active. |
| `!vips` / `/vips` | Opens a **SwiftlyS2 menu** listing all VIP players currently online. |

Both command names are configurable from the config (`VipMenuCommand`, `VipsListCommand`).

---

## HZP Ammo-Pack Integration

The plugin integrates with HanZombiePlagueS2's ammo-pack system at **runtime**
via reflection — no hard dependency is needed at compile time.

When HZP is loaded, the plugin:
1. Retrieves the `IHanZombiePlagueAPI` shared interface object.
2. Navigates `HanZombiePlagueAPI._globals.AmmoPacks` to get the live dictionary.
3. Reads and writes AP values directly in that dictionary, so HZP's UI, database
   persistence, and extra-items shop all see the correct balances.

If HZP is **not** loaded, AP rewards are announced in chat but are not persisted.

---

## Perk Notes

- All perks (armor, multi-jump, fall damage, damage multiplier, kill/damage rewards)
  apply **only when the VIP player is on the human side**.
- The optional **infect reward** is the only perk that fires while VIP is a zombie
  (disabled by default; enable via `InfectRewardsEnabled`).
- **Happy Hour** uses the **server's local time** (`DateTime.Now`).
  Adjust `HappyHourStart` / `HappyHourEnd` for your timezone.
- The **damage accumulator** (for damage-based AP rewards) resets each round end
  and on disconnect.

---

## License

[MIT](LICENSE)
