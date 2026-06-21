using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using HarmonyLib;
using SemanticVersioning;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace AllBossesEverywhere;

public record AllBossesEverywhereMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.lorenzobecker.bossesextravaganza";
    public override string Name { get; init; } = "Bosses Extravaganza";
    public override string Author { get; init; } = "Lorenzo Becker";
    public override List<string>? Contributors { get; init; } = new() { "OpenAI" };
    public override Version Version { get; init; } = new("1.0.6");
    public override Range SptVersion { get; init; } = new("~4.0.13");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 999)]
public sealed class AllBossesEverywhereMod : IOnLoad
{
    private readonly ISptLogger<AllBossesEverywhereMod> _logger;
    private readonly DatabaseService _databaseService;

    internal static ISptLogger<AllBossesEverywhereMod>? Logger;
    internal static ModConfig Config = ModConfig.CreateDefault();
    internal static readonly Dictionary<string, BossLocationSpawn> Templates = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Random Rng = new();

    public AllBossesEverywhereMod(ISptLogger<AllBossesEverywhereMod> logger, DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;
    }

    public Task OnLoad()
    {
        Logger = _logger;
        LoadConfig();
        CacheBossTemplates();

        var harmony = new Harmony("com.lorenzobecker.bossesextravaganza.v1.0.6");
        var original = AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaid));
        var postfix = AccessTools.Method(typeof(StartLocalRaidPatch), nameof(StartLocalRaidPatch.Postfix));

        if (original is null || postfix is null)
        {
            _logger.Error("[Bosses Extravaganza] StartLocalRaid introuvable; patch non appliqué.");
            return Task.CompletedTask;
        }

        harmony.Patch(original, postfix: new HarmonyMethod(postfix));
        _logger.Success(
            $"[Bosses Extravaganza] v1.0.6 chargé. " +
            $"{Templates.Count} modèles disponibles. " +
            $"Maximum additionnel configurable: {Config.MaximumAdditionalBossesPerRaid}."
        );
        return Task.CompletedTask;
    }

    private void LoadConfig()
    {
        try
        {
            var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var path = Path.Combine(directory, "config.json");

            if (!File.Exists(path))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(ModConfig.CreateDefault(), JsonOptions.Write));
            }

            Config = JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(path), JsonOptions.Read)
                     ?? ModConfig.CreateDefault();
            Config.Normalize(_logger);
        }
        catch (Exception ex)
        {
            _logger.Error($"[Bosses Extravaganza] Config invalide, valeurs par défaut utilisées: {ex}");
            Config = ModConfig.CreateDefault();
        }
    }

    private void CacheBossTemplates()
    {
        Templates.Clear();

        foreach (var bossConfig in Config.Bosses.Where(x => x.Enabled))
        {
            var location = _databaseService.GetLocation(bossConfig.SourceMap);
            var locationBase = GetLocationBase(location);

            if (locationBase?.BossLocationSpawn is null)
            {
                _logger.Warning(
                    $"[Bosses Extravaganza] Carte source '{bossConfig.SourceMap}' indisponible pour {bossConfig.Role}."
                );
                continue;
            }

            var template = locationBase.BossLocationSpawn.FirstOrDefault(
                x => string.Equals(x.BossName, bossConfig.Role, StringComparison.OrdinalIgnoreCase)
            );

            if (template is null)
            {
                _logger.Warning(
                    $"[Bosses Extravaganza] Modèle '{bossConfig.Role}' introuvable sur '{bossConfig.SourceMap}'."
                );
                continue;
            }

            Templates[bossConfig.Role] = DeepClone(template);
        }
    }

    private static LocationBase? GetLocationBase(object? location)
    {
        if (location is null) return null;
        var property = location.GetType().GetProperty("Base", BindingFlags.Public | BindingFlags.Instance);
        return property?.GetValue(location) as LocationBase;
    }

    internal static BossLocationSpawn DeepClone(BossLocationSpawn source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions.Clone);
        return JsonSerializer.Deserialize<BossLocationSpawn>(json, JsonOptions.Clone)
               ?? throw new InvalidOperationException("Échec du clonage profond BossLocationSpawn.");
    }
}

internal static class StartLocalRaidPatch
{
    public static void Postfix(StartLocalRaidRequestData request, StartLocalRaidResponseData __result)
    {
        try
        {
            var config = AllBossesEverywhereMod.Config;
            if (!config.Enabled || config.MaximumAdditionalBossesPerRaid <= 0 || __result?.LocationLoot is null)
                return;

            var map = NormalizeMapId(request.Location ?? __result.LocationLoot.Id);

            if (!config.Maps.TryGetValue(map, out var activeMapConfig) || !activeMapConfig.Enabled)
                return;

            if (!config.EnableLabs && map.Equals("laboratory", StringComparison.OrdinalIgnoreCase))
                return;

            var spawns = __result.LocationLoot.BossLocationSpawn ??= new List<BossLocationSpawn>();

            var alreadyPresent = spawns
                .Where(x => (x.BossChance ?? 0) > 0 && !string.IsNullOrWhiteSpace(x.BossName))
                .Select(x => x.BossName!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = config.Bosses
                .Where(x => x.Enabled)
                .Where(x => !alreadyPresent.Contains(x.Role))
                .Where(x => AllBossesEverywhereMod.Templates.ContainsKey(x.Role))
                .Select(x => new Candidate(x, config.GetEffectiveChance(map, x)))
                .Where(x => x.Chance > 0)
                .ToList();

            if (candidates.Count == 0)
            {
                if (config.DebugLogging)
                    AllBossesEverywhereMod.Logger?.Info($"[Bosses Extravaganza] {map}: aucun candidat éligible.");
                return;
            }

            var maxToAdd = Math.Min(config.MaximumAdditionalBossesPerRaid, candidates.Count);
            var added = 0;

            // A chance of 100% is treated as a real forced-add attempt.
            // This makes test configs deterministic: bossGluhar=100 and bossBoar=100
            // should attempt both bosses before the normal weighted slot rolls.
            foreach (var forced in candidates.Where(x => x.Chance >= 100.0).ToList())
            {
                if (added >= maxToAdd)
                    break;

                if (TryAddBoss(__result.LocationLoot, spawns, map, forced, config, maxToAdd, ref added))
                {
                    alreadyPresent.Add(forced.Config.Role);
                }

                candidates.Remove(forced);
            }

            for (var slot = added; slot < maxToAdd && candidates.Count > 0; slot++)
            {
                var rawTotalWeight = candidates.Sum(x => x.Chance);
                var totalChance = Math.Min(100.0, rawTotalWeight);
                var spawnRoll = AllBossesEverywhereMod.Rng.NextDouble() * 100.0;

                if (spawnRoll >= totalChance)
                {
                    if (config.DebugLogging)
                    {
                        AllBossesEverywhereMod.Logger?.Info(
                            $"[Bosses Extravaganza] {map}: slot {slot + 1}, aucun boss " +
                            $"(jet {spawnRoll:F2} / {totalChance:F2}%)."
                        );
                    }
                    break;
                }

                // Important: selection uses the full raw weight, not the capped 0-100 roll range.
                // Otherwise, when total weight exceeds 100, later bosses in config order become impossible.
                var selectionRoll = AllBossesEverywhereMod.Rng.NextDouble() * rawTotalWeight;
                Candidate? selected = null;
                var cursor = 0.0;

                foreach (var candidate in candidates)
                {
                    cursor += candidate.Chance;
                    if (selectionRoll < cursor)
                    {
                        selected = candidate;
                        break;
                    }
                }

                if (selected is null)
                    break;

                if (TryAddBoss(__result.LocationLoot, spawns, map, selected, config, maxToAdd, ref added))
                {
                    alreadyPresent.Add(selected.Config.Role);
                }

                candidates.Remove(selected);

                if (added <= slot)
                    slot--;
            }

            if (config.DebugLogging)
            {
                AllBossesEverywhereMod.Logger?.Info(
                    $"[Bosses Extravaganza] {map}: {added} boss additionnel(s) ajouté(s), " +
                    $"maximum configuré {config.MaximumAdditionalBossesPerRaid}."
                );
            }
        }
        catch (Exception ex)
        {
            AllBossesEverywhereMod.Logger?.Error(
                $"[Bosses Extravaganza] Erreur pendant la génération du raid: {ex}"
            );
        }
    }

    private static bool TryAddBoss(
        LocationBase location,
        List<BossLocationSpawn> spawns,
        string map,
        Candidate selected,
        ModConfig config,
        int maxToAdd,
        ref int added)
    {
        var zones = BuildZonePool(location, spawns, map, selected.Config.Role, config);
        if (zones.Count == 0)
        {
            AllBossesEverywhereMod.Logger?.Warning(
                $"[Bosses Extravaganza] {map}: aucune zone valide pour {selected.Config.DisplayName}; candidat ignoré."
            );
            return false;
        }

        var zone = zones[AllBossesEverywhereMod.Rng.Next(zones.Count)];

        var boss = AllBossesEverywhereMod.DeepClone(
            AllBossesEverywhereMod.Templates[selected.Config.Role]
        );

        boss.BossChance = 100;
        boss.BossZone = zone;
        boss.Time = selected.Config.SpawnTime;
        boss.IsRandomTimeSpawn = selected.Config.RandomTimeSpawn;
        boss.ForceSpawn = selected.Config.ForceSpawn;
        boss.IgnoreMaxBots = selected.Config.IgnoreMaxBots;
        boss.SptId = $"abe_v1_0_6_{selected.Config.Role}_{Guid.NewGuid():N}";

        spawns.Add(boss);
        added++;

        AllBossesEverywhereMod.Logger?.Success(
            $"[Bosses Extravaganza] {map}: {selected.Config.DisplayName} ajouté " +
            $"(chance effective {selected.Chance:F1} %, zone '{zone}', slot {added}/{maxToAdd})."
        );

        return true;
    }

    private static List<string> BuildZonePool(
        LocationBase location,
        List<BossLocationSpawn> existingSpawns,
        string map,
        string role,
        ModConfig config)
    {
        var openZones = (location.OpenZones ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var usedBossZones = GetAlreadyUsedBossZones(existingSpawns);
        var effectiveTier = GetEffectiveBossTier(map, role, config);

        foreach (var request in GetZoneRequests(location, openZones, map, role, effectiveTier, config))
        {
            var rankedZones = RankZones(location, request.Zones, openZones, usedBossZones, map, role, config, request.Source);
            var selectedTier = SelectBestTier(rankedZones, config, effectiveTier, request.Rule);

            if (selectedTier.Count > 0)
            {
                PromoteSpawnPointsToBossInZones(location, selectedTier, map, config);

                if (config.DebugLogging)
                {
                    var nativeFree = rankedZones.Count(x => x.HasNativeBossSpawnPoint && !x.IsAlreadyUsedByBoss);
                    var nativeUsed = rankedZones.Count(x => x.HasNativeBossSpawnPoint && x.IsAlreadyUsedByBoss);
                    var promotedFree = rankedZones.Count(x => !x.HasNativeBossSpawnPoint && x.HasPromotableSpawnPoint && !x.IsAlreadyUsedByBoss);
                    var promotedUsed = rankedZones.Count(x => !x.HasNativeBossSpawnPoint && x.HasPromotableSpawnPoint && x.IsAlreadyUsedByBoss);
                    var selectedKind = DescribeSelectedTier(selectedTier);

                    AllBossesEverywhereMod.Logger?.Info(
                        $"[Bosses Extravaganza] {map}/{role}: tier={effectiveTier}, source={request.Source}, mode={request.Rule.Mode}, " +
                        $"nativeFree={nativeFree}, nativeUsed={nativeUsed}, " +
                        $"promotedFree={promotedFree}, promotedUsed={promotedUsed}, " +
                        $"selectedTier={selectedKind}, selectedCount={selectedTier.Count}."
                    );
                }

                return selectedTier.Select(x => x.Zone).ToList();
            }

            if (request.Rule.IsStrict)
            {
                if (config.DebugLogging)
                {
                    AllBossesEverywhereMod.Logger?.Warning(
                        $"[Bosses Extravaganza] {map}/{role}: règle stricte {request.Source} sans zone exploitable; boss ignoré."
                    );
                }
                return new List<string>();
            }

            if (config.DebugLogging)
            {
                AllBossesEverywhereMod.Logger?.Info(
                    $"[Bosses Extravaganza] {map}/{role}: aucune zone exploitable via {request.Source}; fallback autorisé."
                );
            }
        }

        if (config.DebugLogging)
        {
            AllBossesEverywhereMod.Logger?.Info(
                $"[Bosses Extravaganza] {map}/{role}: aucune zone retenue après toutes les règles."
            );
        }

        return new List<string>();
    }

    private static List<ZoneRequest> GetZoneRequests(
        LocationBase location,
        List<string> openZones,
        string map,
        string role,
        string effectiveTier,
        ModConfig config)
    {
        var result = new List<ZoneRequest>();

        // Priority 1: precise boss rule. This is the strongest tool for "Kaban only at Lexos" style rules.
        if (config.BossZoneRules.TryGetValue(map, out var bossRules)
            && bossRules.TryGetValue(role, out var bossRule)
            && bossRule.Zones.Count > 0)
        {
            result.Add(new ZoneRequest($"bossZoneRules/{role}", bossRule.Zones, bossRule));
        }

        // Priority 2: gameplay tier rule. Heavy/medium/solo behavior lives here.
        if (config.TierZoneOverrides.TryGetValue(map, out var tierRules)
            && tierRules.TryGetValue(effectiveTier, out var tierRule)
            && tierRule.Zones.Count > 0)
        {
            result.Add(new ZoneRequest($"tierZoneOverrides/{effectiveTier}", tierRule.Zones, tierRule));
        }

        // If Goons are on a non-vanilla map and no specialGoons rule exists, try solo rules as the roaming fallback.
        if (effectiveTier.Equals("specialGoons", StringComparison.OrdinalIgnoreCase)
            && config.TierZoneOverrides.TryGetValue(map, out var goonTierRules)
            && goonTierRules.TryGetValue("solo", out var soloFallbackRule)
            && soloFallbackRule.Zones.Count > 0)
        {
            result.Add(new ZoneRequest("tierZoneOverrides/soloGoonsFallback", soloFallbackRule.Zones, soloFallbackRule));
        }

        // Priority 3: legacy bossZoneOverrides, kept for compatibility. Prefer mode is safer for old configs.
        if (config.BossZoneOverrides.TryGetValue(map, out var mapBossZones)
            && mapBossZones.TryGetValue(role, out var legacySpecificZones)
            && legacySpecificZones.Count > 0)
        {
            result.Add(new ZoneRequest("bossZoneOverrides", legacySpecificZones, ZoneRuleConfig.Prefer()));
        }

        // Priority 4: map generalZones, also kept for compatibility.
        if (config.Maps.TryGetValue(map, out var mapConfig)
            && mapConfig.GeneralZones.Count > 0)
        {
            result.Add(new ZoneRequest("generalZones", mapConfig.GeneralZones, ZoneRuleConfig.Prefer()));
        }

        // Factory uses its canonical shared bot zone.
        if (map is "factory4_day" or "factory4_night")
        {
            result.Add(new ZoneRequest("factoryBotZone", new List<string> { "BotZone" }, ZoneRuleConfig.Prefer()));
        }

        // Final fallback: all valid OpenZones from the map.
        var excludedZones = mapConfig?.ExcludedZones ?? new List<string>();
        var openZonePool = openZones
            .Where(x => !config.ExcludedZoneNameFragments.Any(fragment =>
                !string.IsNullOrWhiteSpace(fragment)
                && x.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Where(x => !excludedZones.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (openZonePool.Count > 0)
        {
            result.Add(new ZoneRequest("OpenZones", openZonePool, ZoneRuleConfig.Prefer()));
        }

        return result;
    }

    private static string GetEffectiveBossTier(string map, string role, ModConfig config)
    {
        if (!config.BossProfiles.TryGetValue(role, out var profile))
            return "solo";

        var tier = string.IsNullOrWhiteSpace(profile.Tier)
            ? "solo"
            : profile.Tier.Trim();

        if (tier.Equals("specialGoons", StringComparison.OrdinalIgnoreCase)
            && profile.TreatAsSoloOnNonVanillaMaps
            && profile.VanillaMaps.Count > 0
            && !profile.VanillaMaps.Contains(map, StringComparer.OrdinalIgnoreCase))
        {
            return "solo";
        }

        return tier;
    }

    private static List<ZoneCandidate> RankZones(
        LocationBase location,
        IEnumerable<string> requestedZones,
        List<string> openZones,
        HashSet<string> usedBossZones,
        string map,
        string role,
        ModConfig config,
        string source)
    {
        var result = new List<ZoneCandidate>();
        var sourceCategories = config.ZoneSafety.PromotionSourceCategories ?? new List<string>();

        foreach (var zone in requestedZones
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var explicitlyExcluded = config.Maps.TryGetValue(map, out var mapConfig)
                                     && mapConfig.ExcludedZones.Contains(zone, StringComparer.OrdinalIgnoreCase);

            if (explicitlyExcluded)
            {
                AllBossesEverywhereMod.Logger?.Warning(
                    $"[Bosses Extravaganza] {map}/{role}: zone '{zone}' exclue par la configuration."
                );
                continue;
            }

            var factoryBotZone = map is "factory4_day" or "factory4_night"
                                 && zone.Equals("BotZone", StringComparison.OrdinalIgnoreCase);

            var zoneInOpenZones = openZones.Count == 0
                                  || openZones.Contains(zone, StringComparer.OrdinalIgnoreCase)
                                  || factoryBotZone;

            var hasNativeBossSpawnPoint = factoryBotZone || HasSpawnPointWithCategory(location, zone, "Boss");
            var hasPromotableSpawnPoint = !hasNativeBossSpawnPoint
                                          && config.ZoneSafety.PromoteBotSpawnPointsToBoss
                                          && HasSpawnPointWithAnyCategory(location, zone, sourceCategories);

            if (config.ZoneSafety.RequireSpawnPointInZone
                && !hasNativeBossSpawnPoint
                && !hasPromotableSpawnPoint)
            {
                if (config.DebugLogging)
                {
                    AllBossesEverywhereMod.Logger?.Info(
                        $"[Bosses Extravaganza] {map}/{role}: zone '{zone}' ignorée ({source}) " +
                        "car aucun SpawnPointParams Boss/Bot/BotPmc exploitable n'a été trouvé."
                    );
                }
                continue;
            }

            // If safety is disabled, keep old permissive behavior but still warn for explicit zones absent from OpenZones.
            if (!config.ZoneSafety.RequireSpawnPointInZone && !zoneInOpenZones)
            {
                AllBossesEverywhereMod.Logger?.Warning(
                    $"[Bosses Extravaganza] {map}/{role}: zone '{zone}' absente de OpenZones; elle est ignorée."
                );
                continue;
            }

            if (!zoneInOpenZones && config.DebugLogging)
            {
                AllBossesEverywhereMod.Logger?.Info(
                    $"[Bosses Extravaganza] {map}/{role}: zone '{zone}' absente de OpenZones " +
                    "mais conservée car des SpawnPointParams exploitables existent."
                );
            }

            result.Add(new ZoneCandidate(
                zone,
                hasNativeBossSpawnPoint,
                hasPromotableSpawnPoint,
                usedBossZones.Contains(zone)));
        }

        return result;
    }

    private static List<ZoneCandidate> SelectBestTier(
        List<ZoneCandidate> zones,
        ModConfig config,
        string effectiveTier,
        ZoneRuleConfig rule)
    {
        if (zones.Count == 0)
            return new List<ZoneCandidate>();

        var safety = config.ZoneSafety;
        var behavior = GetTierBehavior(config, effectiveTier);
        var candidates = zones;

        if (!behavior.AllowAlreadyUsedBossZones)
            candidates = candidates.Where(x => !x.IsAlreadyUsedByBoss).ToList();

        if (!behavior.AllowPromotedBotSpawnPoints)
            candidates = candidates.Where(x => x.HasNativeBossSpawnPoint).ToList();

        if (candidates.Count == 0)
            return new List<ZoneCandidate>();

        if (behavior.PreferNativeBossSpawnPoints && safety.PreferNativeBossSpawnPoints)
        {
            if (safety.AvoidAlreadyUsedBossZones)
            {
                var nativeFree = candidates.Where(x => x.HasNativeBossSpawnPoint && !x.IsAlreadyUsedByBoss).ToList();
                if (nativeFree.Count > 0)
                    return nativeFree;
            }

            var nativeAny = candidates.Where(x => x.HasNativeBossSpawnPoint).ToList();
            if (nativeAny.Count > 0)
                return nativeAny;
        }

        if (behavior.AllowPromotedBotSpawnPoints && safety.FallbackToPromotedBotSpawnPoints)
        {
            if (safety.AvoidAlreadyUsedBossZones)
            {
                var promotedFree = candidates
                    .Where(x => !x.HasNativeBossSpawnPoint && x.HasPromotableSpawnPoint && !x.IsAlreadyUsedByBoss)
                    .ToList();
                if (promotedFree.Count > 0)
                    return promotedFree;
            }

            var promotedAny = candidates
                .Where(x => !x.HasNativeBossSpawnPoint && x.HasPromotableSpawnPoint)
                .ToList();
            if (promotedAny.Count > 0)
                return promotedAny;
        }

        if (safety.AvoidAlreadyUsedBossZones)
        {
            var anyFree = candidates.Where(x => !x.IsAlreadyUsedByBoss).ToList();
            if (anyFree.Count > 0)
                return anyFree;
        }

        return candidates;
    }

    private static TierBehaviorConfig GetTierBehavior(ModConfig config, string tier)
    {
        if (config.TierBehaviors.TryGetValue(tier, out var behavior))
            return behavior;

        if (tier.Equals("heavy", StringComparison.OrdinalIgnoreCase))
        {
            return new TierBehaviorConfig
            {
                PreferNativeBossSpawnPoints = true,
                AllowPromotedBotSpawnPoints = false,
                AllowAlreadyUsedBossZones = false
            };
        }

        return new TierBehaviorConfig
        {
            PreferNativeBossSpawnPoints = true,
            AllowPromotedBotSpawnPoints = true,
            AllowAlreadyUsedBossZones = true
        };
    }

    private static string DescribeSelectedTier(List<ZoneCandidate> selectedTier)
    {
        if (selectedTier.Count == 0)
            return "none";

        var hasNative = selectedTier.Any(x => x.HasNativeBossSpawnPoint);
        var hasPromoted = selectedTier.Any(x => !x.HasNativeBossSpawnPoint && x.HasPromotableSpawnPoint);
        var allFree = selectedTier.All(x => !x.IsAlreadyUsedByBoss);

        if (hasNative && allFree)
            return "nativeBossFree";
        if (hasNative)
            return "nativeBossUsedFallback";
        if (hasPromoted && allFree)
            return "promotedBotFree";
        if (hasPromoted)
            return "promotedBotUsedFallback";

        return allFree ? "freeFallback" : "usedFallback";
    }

    private static HashSet<string> GetAlreadyUsedBossZones(IEnumerable<BossLocationSpawn> spawns)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var spawn in spawns)
        {
            if (spawn is null || (spawn.BossChance ?? 0) <= 0 || string.IsNullOrWhiteSpace(spawn.BossZone))
                continue;

            foreach (var zone in spawn.BossZone
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(zone))
                    result.Add(zone);
            }
        }

        return result;
    }

    private static void PromoteSpawnPointsToBossInZones(
        LocationBase location,
        IEnumerable<ZoneCandidate> selectedZones,
        string map,
        ModConfig config)
    {
        var zonesToPromote = selectedZones
            .Where(x => !x.HasNativeBossSpawnPoint && x.HasPromotableSpawnPoint)
            .Select(x => x.Zone)
            .ToList();

        if (zonesToPromote.Count == 0 || location.SpawnPointParams is null)
            return;

        var sourceCategories = config.ZoneSafety.PromotionSourceCategories ?? new List<string>();
        if (sourceCategories.Count == 0)
            return;

        var scanned = 0;
        var promoted = 0;

        foreach (var spawnPoint in location.SpawnPointParams)
        {
            if (spawnPoint?.Categories is null)
                continue;

            var zone = zonesToPromote.FirstOrDefault(x => IsSpawnPointInZone(spawnPoint, x));
            if (zone is null)
                continue;

            scanned++;
            var categories = spawnPoint.Categories.ToList();

            if (categories.Any(x => string.Equals(x, "Boss", StringComparison.OrdinalIgnoreCase)))
                continue;

            var hasPromotableCategory = categories.Any(category =>
                sourceCategories.Any(source =>
                    string.Equals(category, source, StringComparison.OrdinalIgnoreCase)));

            if (!hasPromotableCategory)
                continue;

            categories.Add("Boss");
            spawnPoint.Categories = categories;
            promoted++;
        }

        if (config.DebugLogging)
        {
            AllBossesEverywhereMod.Logger?.Info(
                $"[Bosses Extravaganza] {map}: promoted selected Bot/BotPmc spawnpoints to Boss: " +
                $"zones={string.Join(",", zonesToPromote)}, scanned={scanned}, promoted={promoted}."
            );
        }
    }

    private static bool HasSpawnPointWithCategory(LocationBase location, string zone, string category)
    {
        if (location.SpawnPointParams is null)
            return false;

        foreach (var spawnPoint in location.SpawnPointParams)
        {
            if (spawnPoint?.Categories is null)
                continue;

            if (!IsSpawnPointInZone(spawnPoint, zone))
                continue;

            if (spawnPoint.Categories.Any(x =>
                    string.Equals(x, category, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private static bool HasSpawnPointWithAnyCategory(
        LocationBase location,
        string zone,
        List<string> categories)
    {
        if (location.SpawnPointParams is null || categories.Count == 0)
            return false;

        foreach (var spawnPoint in location.SpawnPointParams)
        {
            if (spawnPoint?.Categories is null)
                continue;

            if (!IsSpawnPointInZone(spawnPoint, zone))
                continue;

            if (spawnPoint.Categories.Any(spawnCategory =>
                    categories.Any(sourceCategory =>
                        string.Equals(spawnCategory, sourceCategory, StringComparison.OrdinalIgnoreCase))))
                return true;
        }

        return false;
    }

    private static bool IsSpawnPointInZone(object spawnPoint, string zone)
    {
        if (spawnPoint is null || string.IsNullOrWhiteSpace(zone))
            return false;

        var id = GetStringProperty(spawnPoint, "Id");
        var botZoneName = GetStringProperty(spawnPoint, "BotZoneName");

        return IsZoneNameMatch(id, zone) || IsZoneNameMatch(botZoneName, zone);
    }

    private static string? GetStringProperty(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        return property?.GetValue(value) as string;
    }

    private static bool IsZoneNameMatch(string? value, string zone)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(zone))
            return false;

        if (string.Equals(value, zone, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(value, $"zone:{zone}", StringComparison.OrdinalIgnoreCase))
            return true;

        return value.Contains(zone, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMapId(string? value)
    {
        var map = (value ?? string.Empty).Trim().ToLowerInvariant();
        return map switch
        {
            "customs" => "bigmap",
            "factory" => "factory4_day",
            "reserve" => "rezervbase",
            "streets" => "tarkovstreets",
            "groundzero" => "sandbox",
            "labs" => "laboratory",
            _ => map
        };
    }

    private sealed record Candidate(BossConfig Config, double Chance);

    private sealed record ZoneRequest(
        string Source,
        List<string> Zones,
        ZoneRuleConfig Rule);

    private sealed record ZoneCandidate(
        string Zone,
        bool HasNativeBossSpawnPoint,
        bool HasPromotableSpawnPoint,
        bool IsAlreadyUsedByBoss);
}

public sealed class ModConfig
{
    public bool Enabled { get; set; } = true;
    public bool EnableLabs { get; set; } = false;
    public bool DebugLogging { get; set; } = false;

    // 0 désactive les ajouts. 1 reste le réglage prudent recommandé.
    public int MaximumAdditionalBossesPerRaid { get; set; } = 3;

    public List<string> ExcludedZoneNameFragments { get; set; } = new();

    public ZoneSafetyConfig ZoneSafety { get; set; } = new();

    // Matrice complète et lisible: carte -> boss -> chance.
    public Dictionary<string, MapConfig> Maps { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // Zones spécifiques: carte -> boss -> liste de zones.
    public Dictionary<string, Dictionary<string, List<string>>> BossZoneOverrides { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // 1.0.5: heavy strict zones may use promoted/used spawnpoints; strict means "do not leave listed zones".
    // 1.0.4: gameplay profiles. Heavy bosses are large groups; medium bosses have escorts; solo bosses stay mobile.
    public Dictionary<string, BossProfileConfig> BossProfiles { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // 1.0.4: map -> boss tier -> zone rule. Used before generic OpenZones.
    public Dictionary<string, Dictionary<string, ZoneRuleConfig>> TierZoneOverrides { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // 1.0.4: map -> boss role -> zone rule. Strongest override, above tier rules.
    public Dictionary<string, Dictionary<string, ZoneRuleConfig>> BossZoneRules { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, TierBehaviorConfig> TierBehaviors { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public List<BossConfig> Bosses { get; set; } = new();

    public double GetEffectiveChance(string map, BossConfig boss)
    {
        if (!Maps.TryGetValue(map, out var mapConfig) || !mapConfig.Enabled)
            return 0;

        return mapConfig.Bosses.TryGetValue(boss.Role, out var value)
            ? Math.Clamp(value, 0, 100)
            : 0;
    }

    public static ModConfig CreateDefault()
    {
        var roles = new[]
        {
            "bossBully", "bossKilla", "bossTagilla", "bossKojaniy", "bossSanitar",
            "bossKolontay", "bossPartisan", "bossGluhar", "bossBoar", "bossKnight"
        };

        var maps = new Dictionary<string, MapConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in new[]
                 {
                     "bigmap", "factory4_day", "factory4_night", "interchange", "lighthouse",
                     "rezervbase", "sandbox", "sandbox_high", "shoreline", "tarkovstreets",
                     "woods", "labyrinth"
                 })
        {
            var chances = roles.ToDictionary(
                role => role,
                role => role is "bossGluhar" or "bossBoar" or "bossKnight" ? 2.0 : 4.0,
                StringComparer.OrdinalIgnoreCase);

            maps[map] = new MapConfig { Enabled = true, Bosses = chances };
        }

        // Release default: keep low-level Ground Zero disabled; sandbox_high remains enabled.
        // Labs/Labyrinth are also disabled by default.
        if (maps.TryGetValue("sandbox", out var sandbox))
        {
            sandbox.Enabled = false;
            foreach (var role in roles) sandbox.Bosses[role] = 0;
        }

        if (maps.TryGetValue("labyrinth", out var labyrinth))
        {
            labyrinth.Enabled = false;
            foreach (var role in roles) labyrinth.Bosses[role] = 0;
        }

        return new ModConfig
        {
            Enabled = true,
            EnableLabs = false,
            DebugLogging = false,
            MaximumAdditionalBossesPerRaid = 3,
            ExcludedZoneNameFragments = new() { "snip", "sniper", "marksman" },
            ZoneSafety = new ZoneSafetyConfig
            {
                RequireSpawnPointInZone = true,
                PromoteBotSpawnPointsToBoss = true,
                PromotionSourceCategories = new() { "Bot", "BotPmc" },
                PreferNativeBossSpawnPoints = true,
                AvoidAlreadyUsedBossZones = true,
                FallbackToPromotedBotSpawnPoints = true
            },
            Maps = maps,
            BossZoneOverrides = new(StringComparer.OrdinalIgnoreCase),
            BossProfiles = new(StringComparer.OrdinalIgnoreCase)
            {
                ["bossBoar"] = new BossProfileConfig { Tier = "heavy" },
                ["bossGluhar"] = new BossProfileConfig { Tier = "heavy" },
                ["bossKolontay"] = new BossProfileConfig { Tier = "medium" },
                ["bossSanitar"] = new BossProfileConfig { Tier = "medium" },
                ["bossKojaniy"] = new BossProfileConfig { Tier = "medium" },
                ["bossBully"] = new BossProfileConfig { Tier = "medium" },
                ["bossKilla"] = new BossProfileConfig { Tier = "solo" },
                ["bossTagilla"] = new BossProfileConfig { Tier = "solo" },
                ["bossPartisan"] = new BossProfileConfig { Tier = "solo" },
                ["bossKnight"] = new BossProfileConfig
                {
                    Tier = "specialGoons",
                    TreatAsSoloOnNonVanillaMaps = true,
                    VanillaMaps = new() { "bigmap", "woods", "shoreline", "lighthouse" }
                }
            },
            TierBehaviors = new(StringComparer.OrdinalIgnoreCase)
            {
                ["heavy"] = new TierBehaviorConfig
                {
                    PreferNativeBossSpawnPoints = true,
                    AllowPromotedBotSpawnPoints = true,
                    AllowAlreadyUsedBossZones = true
                },
                ["medium"] = new TierBehaviorConfig
                {
                    PreferNativeBossSpawnPoints = true,
                    AllowPromotedBotSpawnPoints = true,
                    AllowAlreadyUsedBossZones = true
                },
                ["solo"] = new TierBehaviorConfig
                {
                    PreferNativeBossSpawnPoints = true,
                    AllowPromotedBotSpawnPoints = true,
                    AllowAlreadyUsedBossZones = true
                },
                ["specialGoons"] = new TierBehaviorConfig
                {
                    PreferNativeBossSpawnPoints = true,
                    AllowPromotedBotSpawnPoints = true,
                    AllowAlreadyUsedBossZones = true
                }
            },
            TierZoneOverrides = new(StringComparer.OrdinalIgnoreCase)
            {
                ["bigmap"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["heavy"] = new ZoneRuleConfig { Mode = "strict", Zones = new() { "ZoneScavBase" } },
                    ["medium"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneDormitory", "ZoneGasStation", "ZoneScavBase" } },
                    ["solo"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneDormitory", "ZoneGasStation", "ZoneScavBase", "ZoneFactorySide", "ZoneCustoms", "ZoneBlockPost", "ZoneCrossRoad", "ZoneBrige" } },
                },
                ["woods"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["heavy"] = new ZoneRuleConfig { Mode = "strict", Zones = new() { "ZoneWoodCutter", "ZoneScavBase2", "ZoneUsecBase", "ZoneBrokenVill", "ZoneClearVill" } },
                    ["medium"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneWoodCutter", "ZoneScavBase2", "ZoneMiniHouse", "ZoneBrokenVill", "ZoneClearVill" } },
                    ["solo"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneWoodCutter", "ZoneScavBase2", "ZoneUsecBase", "ZoneBrokenVill", "ZoneClearVill", "ZoneRoad", "ZoneStoneBunker" } },
                },
                ["shoreline"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["heavy"] = new ZoneRuleConfig { Mode = "strict", Zones = new() { "ZoneSanatorium1", "ZoneSanatorium2", "ZonePort", "ZonePowerStation", "ZoneMeteoStation" } },
                    ["medium"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneSanatorium1", "ZoneSanatorium2", "ZoneGreenHouses", "ZonePort", "ZonePowerStation", "ZoneMeteoStation" } },
                    ["solo"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneSanatorium1", "ZoneSanatorium2", "ZoneGreenHouses", "ZonePort", "ZonePowerStation", "ZoneMeteoStation", "ZoneForestGasStation", "ZoneSmuglers" } },
                },
                ["interchange"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["heavy"] = new ZoneRuleConfig { Mode = "strict", Zones = new() { "ZoneCenter", "ZoneCenterBot", "ZoneOLI", "ZoneIDEA", "ZoneGoshan" } },
                    ["medium"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneCenter", "ZoneCenterBot", "ZoneOLI", "ZoneIDEA", "ZoneGoshan", "ZoneIDEAPark", "ZoneOLIPark" } },
                    ["solo"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneCenter", "ZoneCenterBot", "ZoneOLI", "ZoneIDEA", "ZoneGoshan", "ZoneIDEAPark", "ZoneOLIPark", "ZoneTrucks" } },
                },
                ["rezervbase"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["heavy"] = new ZoneRuleConfig { Mode = "strict", Zones = new() { "ZoneRailStrorage", "ZonePTOR2", "ZoneBarrack", "ZoneSubStorage", "ZoneBunkerStorage", "ZoneSubCommand" } },
                    ["medium"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneRailStrorage", "ZonePTOR2", "ZoneBarrack", "ZoneSubStorage", "ZoneBunkerStorage", "ZoneSubCommand", "ZonePTOR1" } },
                    ["solo"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneRailStrorage", "ZonePTOR2", "ZoneBarrack", "ZoneSubStorage", "ZoneBunkerStorage", "ZoneSubCommand", "ZonePTOR1" } },
                },
                ["tarkovstreets"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["heavy"] = new ZoneRuleConfig { Mode = "strict", Zones = new() { "ZoneCarShowroom", "ZoneMvd", "ZoneConcordiaParking", "ZoneConcordia_1", "ZoneHotel_1", "ZoneHotel_2", "ZoneCinema" } },
                    ["medium"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneMvd", "ZoneClimova", "ZoneCarShowroom", "ZoneConcordiaParking", "ZoneConcordia_1", "ZoneHotel_1", "ZoneHotel_2", "ZoneCinema", "ZoneConstruction" } },
                    ["solo"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneCarShowroom", "ZoneMvd", "ZoneClimova", "ZoneConcordiaParking", "ZoneConcordia_1", "ZoneHotel_1", "ZoneHotel_2", "ZoneCinema", "ZoneConstruction", "ZoneFactory", "ZoneStilo" } },
                },
                ["lighthouse"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["heavy"] = new ZoneRuleConfig { Mode = "strict", Zones = new() { "Zone_TreatmentContainers", "Zone_Chalet", "Zone_Blockpost", "Zone_RoofContainers", "Zone_Hellicopter" } },
                    ["medium"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "Zone_TreatmentContainers", "Zone_Chalet", "Zone_Blockpost", "Zone_Village", "Zone_Hellicopter" } },
                    ["solo"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "Zone_TreatmentContainers", "Zone_Chalet", "Zone_Blockpost", "Zone_Village", "Zone_Hellicopter", "Zone_Rocks", "Zone_TreatmentBeach", "Zone_TreatmentRocks" } },
                },
                ["sandbox_high"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["heavy"] = new ZoneRuleConfig { Mode = "strict", Zones = new() { "ZoneSandbox" } },
                    ["medium"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneSandbox" } },
                    ["solo"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "ZoneSandbox" } },
                },
                ["factory4_day"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["heavy"] = new ZoneRuleConfig { Mode = "strict", Zones = new() { "BotZone" } },
                    ["medium"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "BotZone" } },
                    ["solo"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "BotZone" } },
                },
                ["factory4_night"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["heavy"] = new ZoneRuleConfig { Mode = "strict", Zones = new() { "BotZone" } },
                    ["medium"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "BotZone" } },
                    ["solo"] = new ZoneRuleConfig { Mode = "prefer", Zones = new() { "BotZone" } },
                },
            },
            BossZoneRules = new(StringComparer.OrdinalIgnoreCase)
            {
                ["bigmap"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["bossGluhar"] = new ZoneRuleConfig { Mode = "strict", Zones = new() { "ZoneScavBase" } },
                    ["bossBoar"] = new ZoneRuleConfig { Mode = "strict", Zones = new() { "ZoneScavBase" } },
                },
            },
            Bosses = new()
            {
                new("bossBully", "Reshala", "bigmap"),
                new("bossKilla", "Killa", "interchange"),
                new("bossTagilla", "Tagilla", "factory4_day"),
                new("bossKojaniy", "Shturman", "woods"),
                new("bossSanitar", "Sanitar", "shoreline"),
                new("bossKolontay", "Kollontay", "tarkovstreets"),
                new("bossPartisan", "Partisan", "bigmap"),
                new("bossGluhar", "Glukhar", "rezervbase"),
                new("bossBoar", "Kaban", "tarkovstreets"),
                new("bossKnight", "Goons", "lighthouse")
            }
        };
    }

    public void Normalize(ISptLogger<AllBossesEverywhereMod> logger)
    {
        ExcludedZoneNameFragments ??= new();
        ZoneSafety ??= new ZoneSafetyConfig();
        ZoneSafety.PromotionSourceCategories ??= new() { "Bot", "BotPmc" };
        Maps ??= new(StringComparer.OrdinalIgnoreCase);
        BossZoneOverrides ??= new(StringComparer.OrdinalIgnoreCase);
        BossProfiles ??= new(StringComparer.OrdinalIgnoreCase);
        TierBehaviors ??= new(StringComparer.OrdinalIgnoreCase);
        TierZoneOverrides ??= new(StringComparer.OrdinalIgnoreCase);
        BossZoneRules ??= new(StringComparer.OrdinalIgnoreCase);
        Bosses ??= new();

        MaximumAdditionalBossesPerRaid = Math.Clamp(MaximumAdditionalBossesPerRaid, 0, 20);

        var validRoles = Bosses
            .Where(x => !string.IsNullOrWhiteSpace(x.Role))
            .Select(x => x.Role)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var boss in Bosses)
        {
            if (string.IsNullOrWhiteSpace(boss.Role) || string.IsNullOrWhiteSpace(boss.SourceMap))
                logger.Warning("[Bosses Extravaganza] Entrée boss incomplète dans config.json.");
        }

        foreach (var mapKey in Maps.Keys.ToList())
        {
            Maps[mapKey] ??= new MapConfig();
            var mapConfig = Maps[mapKey];
            mapConfig.Bosses ??= new(StringComparer.OrdinalIgnoreCase);
            mapConfig.GeneralZones ??= new();
            mapConfig.ExcludedZones ??= new();

            foreach (var role in mapConfig.Bosses.Keys.ToList())
            {
                mapConfig.Bosses[role] = Math.Clamp(mapConfig.Bosses[role], 0, 100);

                if (!validRoles.Contains(role))
                {
                    logger.Warning(
                        $"[Bosses Extravaganza] {mapKey}: boss inconnu '{role}' dans la matrice."
                    );
                }
            }
        }

        foreach (var mapKey in BossZoneOverrides.Keys.ToList())
        {
            BossZoneOverrides[mapKey] ??= new(StringComparer.OrdinalIgnoreCase);
            var roleZones = BossZoneOverrides[mapKey];

            foreach (var role in roleZones.Keys.ToList())
            {
                if (!validRoles.Contains(role))
                {
                    logger.Warning(
                        $"[Bosses Extravaganza] {mapKey}: boss inconnu '{role}' dans bossZoneOverrides."
                    );
                }

                roleZones[role] ??= new();
            }
        }

        foreach (var role in BossProfiles.Keys.ToList())
        {
            BossProfiles[role] ??= new BossProfileConfig();

            if (!validRoles.Contains(role))
            {
                logger.Warning(
                    $"[Bosses Extravaganza] bossProfiles: boss inconnu '{role}'."
                );
            }

            BossProfiles[role].Tier = string.IsNullOrWhiteSpace(BossProfiles[role].Tier)
                ? "solo"
                : BossProfiles[role].Tier.Trim();

            BossProfiles[role].VanillaMaps ??= new();
        }

        foreach (var tier in TierBehaviors.Keys.ToList())
        {
            TierBehaviors[tier] ??= new TierBehaviorConfig();
        }

        NormalizeZoneRuleMap(TierZoneOverrides, logger, "tierZoneOverrides", validateRole: false, validRoles);
        NormalizeZoneRuleMap(BossZoneRules, logger, "bossZoneRules", validateRole: true, validRoles);
    }

    private static void NormalizeZoneRuleMap(
        Dictionary<string, Dictionary<string, ZoneRuleConfig>> mapRules,
        ISptLogger<AllBossesEverywhereMod> logger,
        string label,
        bool validateRole,
        HashSet<string> validRoles)
    {
        foreach (var mapKey in mapRules.Keys.ToList())
        {
            mapRules[mapKey] ??= new(StringComparer.OrdinalIgnoreCase);
            var rules = mapRules[mapKey];

            foreach (var key in rules.Keys.ToList())
            {
                rules[key] ??= new ZoneRuleConfig();
                rules[key].Normalize();

                if (validateRole && !validRoles.Contains(key))
                {
                    logger.Warning(
                        $"[Bosses Extravaganza] {label}/{mapKey}: boss inconnu '{key}'."
                    );
                }
            }
        }
    }
}

public sealed class ZoneSafetyConfig
{
    public bool RequireSpawnPointInZone { get; set; } = true;
    public bool PromoteBotSpawnPointsToBoss { get; set; } = true;
    public List<string> PromotionSourceCategories { get; set; } = new() { "Bot", "BotPmc" };

    // 1.0.3: prefer real Tarkov boss spawn points before promoted bot/PMC points.
    public bool PreferNativeBossSpawnPoints { get; set; } = true;

    // 1.0.3: avoid zones already used by vanilla/modded BossLocationSpawn entries when possible.
    public bool AvoidAlreadyUsedBossZones { get; set; } = true;

    // 1.0.3: only use promoted Bot/BotPmc points if no native Boss zone tier is available.
    public bool FallbackToPromotedBotSpawnPoints { get; set; } = true;
}

public sealed class BossProfileConfig
{
    public string Tier { get; set; } = "solo";
    public bool TreatAsSoloOnNonVanillaMaps { get; set; } = false;
    public List<string> VanillaMaps { get; set; } = new();
}

public sealed class TierBehaviorConfig
{
    // 1.0.5 note: for heavy bosses, AllowPromotedBotSpawnPoints/AllowAlreadyUsedBossZones can be true.
    // The ZoneRule mode still keeps them inside the configured strict zone list.
    public bool PreferNativeBossSpawnPoints { get; set; } = true;
    public bool AllowPromotedBotSpawnPoints { get; set; } = true;
    public bool AllowAlreadyUsedBossZones { get; set; } = true;
}

public sealed class ZoneRuleConfig
{
    public string Mode { get; set; } = "prefer";
    public List<string> Zones { get; set; } = new();

    [JsonIgnore]
    public bool IsStrict => string.Equals(Mode, "strict", StringComparison.OrdinalIgnoreCase);

    public void Normalize()
    {
        Mode = string.Equals(Mode, "strict", StringComparison.OrdinalIgnoreCase)
            ? "strict"
            : "prefer";

        Zones ??= new();
        Zones = Zones
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ZoneRuleConfig Prefer()
    {
        return new ZoneRuleConfig { Mode = "prefer", Zones = new() };
    }
}

public sealed class MapConfig
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Bosses { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public List<string> GeneralZones { get; set; } = new();
    public List<string> ExcludedZones { get; set; } = new();
}

public sealed class BossConfig
{
    public BossConfig() { }

    public BossConfig(
        string role,
        string displayName,
        string sourceMap)
    {
        Role = role;
        DisplayName = displayName;
        SourceMap = sourceMap;
    }

    public string Role { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SourceMap { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int SpawnTime { get; set; } = -1;
    public bool RandomTimeSpawn { get; set; } = false;
    public bool ForceSpawn { get; set; } = false;
    public bool IgnoreMaxBots { get; set; } = false;
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static readonly JsonSerializerOptions Clone = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true
    };
}
