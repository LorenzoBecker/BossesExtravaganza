# Bosses Extravaganza 1.0.6

Server-side SPT 4.0.13 mod that adds configurable extra vanilla bosses to raids without replacing vanilla boss spawns.

## What it does

- Adds extra boss spawns at local raid generation.
- Preserves vanilla bosses and other map boss entries.
- Lets each map define per-boss spawn chances.
- Caps extra bosses with `maximumAdditionalBossesPerRaid`.
- Avoids duplicate extra bosses when that boss is already present in the raid.
- Validates real spawnpoints before injecting a boss.
- Can promote eligible `Bot` / `BotPmc` spawnpoints to `Boss` when the zone is safe to use.
- Uses boss tiers:
  - `heavy`: Kaban, Glukhar
  - `medium`: Reshala, Shturman, Sanitar, Kollontay
  - `solo`: Killa, Tagilla, Partisan
  - `specialGoons`: Knight/Goons
- Supports strict/preferred zone rules and excluded zones.

## Tested behaviour

Validated in local SPT 4.0.13 testing:

- Woods: extra boss injection works; `ZoneDepo` is excluded by default to avoid the BTR/mines depot trap.
- Customs/Douanes: Kaban and Glukhar can be forced to spawn at `ZoneScavBase` / Fortress.
- Multiple bosses can coexist in the same stronghold zone.
- Bosses set to 100% are treated as forced priority candidates before normal weighted rolls.

## Install

This source package is buildable. Compile first:

```cmd
dotnet build .\BossesExtravaganza\BossesExtravaganza.csproj -c Release
```

Then create/install this folder in your SPT install:

```text
<SPT>/user/mods/BossesExtravaganza/
  BossesExtravaganza.dll
  config.json
```

Use the config template from:

```text
ReleaseTemplate/user/mods/BossesExtravaganza/config.json
```

The compiled DLL is generated at:

```text
BossesExtravaganza/bin/Release/net9.0/BossesExtravaganza.dll
```

## Requirements

- SPT 4.0.13
- .NET SDK able to build `net9.0`

## Notes

- `debugLogging` is disabled in the release config. Enable it only for troubleshooting.
- Labs and Labyrinth are disabled by default.
- Low-level Ground Zero (`sandbox`) is disabled by default; `sandbox_high` remains enabled.
- The mod is server-side. No client plugin is required.
