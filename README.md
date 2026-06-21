# Bosses Extravaganza

Bosses Extravaganza is a lightweight server-side mod for SPT 4.0.13 that gives vanilla bosses a configurable chance to appear on maps where they normally do not spawn.

The goal is simple: make raids less predictable without turning every raid into a boss arena.

By default, the mod preserves all vanilla boss spawns and only adds a small chance for one additional boss encounter per raid. Boss escorts are preserved, so bosses such as Reshala, Sanitar, Glukhar, Kaban, Kollontay and the Goons keep their proper followers when selected.

## Features

- Server-side only.
- No client plugin required.
- Does not edit vanilla database files.
- Preserves vanilla boss spawns.
- Preserves boss escorts and followers.
- Preserves boss spawns added by other mods.
- Configurable chance per boss and per map.
- Configurable maximum number of extra bosses per raid.
- Optional custom spawn zones per map and per boss.
- Labs disabled by default.
- Debug logging for selected bosses and zones.

## How it works

When a local raid is generated, Bosses Extravaganza reads the raid's boss spawn data and may inject one or more additional boss spawn entries depending on the configuration.

The mod does not overwrite the base game location files. It works on the raid instance being generated, which makes it safer to use alongside other server-side mods.

By default, the maximum number of additional bosses is set to 1. This means the mod can add at most one extra boss group to a raid, while vanilla bosses and other modded boss spawns remain untouched.

Example:

- Customs can still spawn Reshala normally.
- Bosses Extravaganza may additionally roll Killa, Sanitar, Kaban, Glukhar, etc., depending on your config.
- If a boss is already present in the raid data, the mod avoids adding a duplicate of that same boss.

## Configuration

The config file is designed to be readable and direct.

Each map has its own boss chance matrix:

```json
"bigmap": {
  "enabled": true,
  "bosses": {
    "bossBully": 0,
    "bossKilla": 4,
    "bossTagilla": 4,
    "bossKojaniy": 4,
    "bossSanitar": 4,
    "bossKolontay": 4,
    "bossPartisan": 0,
    "bossGluhar": 2,
    "bossBoar": 2,
    "bossKnight": 2
  },
  "generalZones": [],
  "excludedZones": []
}
```

A value of `0` means Bosses Extravaganza will not add that boss on that map. It does not disable vanilla spawns.

You can also define custom zones for specific bosses:

```json
"bossZoneOverrides": {
  "bigmap": {
    "bossBoar": [
      "ZoneDormitory",
      "ZoneGasStation"
    ],
    "bossKilla": [
      "ZoneScavBase"
    ]
  }
}
```

Zone priority is:

1. Boss-specific zones for the current map.
2. General zones for the current map.
3. Valid OpenZones from the map.

## Default behavior

The default configuration is intentionally conservative:

- Maximum 1 additional boss group per raid.
- Solo/smaller bosses usually have a 4% chance.
- Large boss groups such as Glukhar, Kaban and the Goons usually have a 2% chance.
- Bosses are set to 0 on their main vanilla maps where appropriate, so the mod does not add unnecessary duplicate chances.
- Partisan is set to 0 on his vanilla maps.

These values are meant as a baseline. You can make the mod rare, chaotic, brutal, or almost invisible depending on your preferred SPT setup.

## Compatibility

Bosses Extravaganza should be compatible with most server-side mods that modify boss chances or add custom bosses, as long as they operate through standard boss spawn data.

The mod does not manage custom modded bosses by default. If another mod already adds its own boss, Bosses Extravaganza leaves it alone.

The configured maximum number of additional bosses only applies to bosses added by Bosses Extravaganza. Vanilla bosses and bosses added by other mods are not counted against that limit.

## Known limitations

- Custom modded bosses are not included in the default configuration.
- The mod does not disable vanilla boss spawns.
- The mod does not currently replace vanilla bosses with random boss rotations.
- If another mod modifies boss spawn data after this mod runs, the final result may differ.
- Extremely aggressive settings can produce very difficult raids, especially with bosses that have large escort groups.

## Installation

Drop the `BossesExtravaganza` folder into:

```text
SPT/user/mods/
```

Final path:

```text
SPT/user/mods/BossesExtravaganza/
```

Then start the SPT server.

## Recommended first test

Start the server and check for:

```text
Bosses Extravaganza version 1.0.0
```

Then launch a few raids with debug logging enabled. The server will report when an additional boss is selected and which zone was used.

## Source code

The mod is intentionally small and focused. The current source is included in the release package as `Source.cs` and can also be published directly in this repository for review.

I understand the code, the configuration structure, and the runtime behavior, and I am responsible for maintaining and fixing the mod.
