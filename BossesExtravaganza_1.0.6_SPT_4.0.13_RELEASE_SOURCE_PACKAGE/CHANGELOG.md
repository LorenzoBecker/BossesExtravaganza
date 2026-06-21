# Changelog

## 1.0.6

Release candidate.

- Fixed weighted roll behaviour when total boss chances exceed 100%.
- Bosses set to `100` are now treated as forced priority candidates before normal slot rolls.
- Fixed Kaban/Glukhar strict heavy behaviour on Customs so `strict` means "stay inside allowed zones", not "native unused boss spawnpoint only".
- Confirmed Kaban and Glukhar can spawn together at Customs Fortress / `ZoneScavBase`.
- Kept Woods `ZoneDepo` exclusion to avoid the BTR/mines depot trap.
- Release defaults:
  - `debugLogging: false`
  - `maximumAdditionalBossesPerRaid: 3`
  - `sandbox` disabled
  - `sandbox_high` enabled
  - Labs/Labyrinth disabled by default

## 1.0.5

- Relaxed heavy strict zone logic so heavy bosses can use valid promoted or already-used spawnpoints inside strict zones.

## 1.0.4

- Added boss tiers: heavy, medium, solo, specialGoons.
- Added boss/tier zone rules.
- Added map-specific heavy rules, especially Customs `ZoneScavBase`.
- Added `BotZoneName` matching for spawnpoint validation.

## 1.0.3

- Added priority ordering for native boss zones versus promoted Bot/BotPmc zones.

## 1.0.2

- Fixed spawnpoint category promotion for SPT 4.0.13 where `Categories` is exposed as `IEnumerable<string>`.
- Added safer zone validation.

## 1.0.1

- Initial zone-safety experiment.

## 1.0.0

- Initial working build.
