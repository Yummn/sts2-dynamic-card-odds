using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using System.Reflection;
using System.Text.Json;

namespace DynamicCardOdds;

internal static class DcoAdaptiveOdds
{
    private const string RuntimeFolderName = "DynamicCardOdds";
    private const string StateFileName = "DynamicCardOdds.state.dat";
    private const string BetterDefectFolderName = "BetterDefect";
    private const string LegacyBetterDefectState = "BetterDefect.DynamicOdds.weights.dat";
    private const string LegacyBetterDefectJson = "BetterDefect.DynamicOdds.weights.json";
    private const double DecayPerNewRun = 0.90;
    private const double CardPriorStrength = 8.0;
    private const double PairPriorStrength = 6.0;
    private const double DefaultPickRate = 1.0 / 3.0;
    private const int MaxPairEntries = 12000;

    private static readonly object StateLock = new();
    private static readonly object TypeCacheLock = new();
    private static readonly Dictionary<Type, bool> DefectTypeCache = new();
    private static readonly Dictionary<Type, string> CardKeyCache = new();
    private static DcoState? _persisted;
    private static DcoState? _activeSnapshot;
    private static object? _activeRunObject;
    private static HashSet<string> _activeDisabled = new(StringComparer.Ordinal);

    public static void InitializeStorage()
    {
        lock (StateLock)
        {
            _persisted = LoadState();
            _persisted.Normalize();
            SaveState(_persisted);
            MainFile.Logger.Info($"[DynamicCardOdds] state ready: {GetStatePath()}; cards={_persisted.Cards.Count}; pairs={_persisted.Pairs.Count}; disabled={_persisted.DisabledCards.Count}; migrated={_persisted.MigratedBetterDefectDisabledCards}.");
        }
    }

    public static CardModel TryReplaceRewardCard(
        Player player,
        IEnumerable<CardModel> blacklist,
        CardCreationOptions options,
        CardModel vanillaCard)
    {
        if (!IsDefectRun(player) || !IsDefectCard(vanillaCard) || IsUnsupportedRarity(vanillaCard.Rarity))
            return vanillaCard;

        try
        {
            EnsureActiveSnapshot(player);
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var card in blacklist ?? Array.Empty<CardModel>())
                if (card is not null)
                    blocked.Add(CardKey(card));

            var candidatesByKey = new Dictionary<string, CardModel>(StringComparer.Ordinal);
            foreach (var candidate in options.GetPossibleCards(player))
            {
                if (candidate is null || candidate.Rarity != vanillaCard.Rarity || !IsDefectCard(candidate))
                    continue;
                var key = CardKey(candidate);
                if (blocked.Contains(key) || _activeDisabled.Contains(key))
                    continue;
                candidatesByKey.TryAdd(key, candidate);
            }

            var candidates = candidatesByKey.Values.ToList();
            if (candidates.Count == 0)
                return vanillaCard;

            var deckCounts = GetDeckCounts(player);
            var raw = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
                raw[CardKey(candidate)] = ComputeRawWeight(candidate, deckCounts, _activeSnapshot ?? new DcoState());

            var average = raw.Count == 0 ? 1.0 : raw.Values.Average();
            if (average <= 0.000001 || double.IsNaN(average))
                average = 1.0;
            var normalized = raw.ToDictionary(
                pair => pair.Key,
                pair => (float)Math.Clamp(pair.Value / average, 0.55, 1.65),
                StringComparer.Ordinal);

            var vanillaKey = CardKey(vanillaCard);
            var vanillaDisabled = _activeDisabled.Contains(vanillaKey);
            if (!vanillaDisabled && candidates.Count > 1)
            {
                var min = normalized.Values.Min();
                var max = normalized.Values.Max();
                if (max - min < 0.0001f)
                    return vanillaCard;
            }

            var rng = options.RngOverride ?? player.PlayerRng.Rewards;
            var selected = candidates.Count == 1
                ? candidates[0]
                : rng.WeightedNextItem(candidates, card => normalized[CardKey(card)]);
            if (selected is null || (!vanillaDisabled && CardKey(selected) == vanillaKey))
                return vanillaCard;

            var replacement = player.RunState.CreateCard(selected, player);
            MainFile.Logger.Debug($"[DynamicCardOdds] reward {SafeId(vanillaCard)} -> {SafeId(replacement)}; rarity={vanillaCard.Rarity}; weight={normalized[CardKey(selected)]:0.000}.");
            return replacement;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[DynamicCardOdds] reward weighting skipped: {ex.GetType().Name}: {ex.Message}");
            return vanillaCard;
        }
    }

    public static void RecordRewardChoice(
        IReadOnlyList<CardModel> offeredCards,
        IReadOnlyList<CardModel> pickedCards,
        IReadOnlyDictionary<string, int> deckCountsBeforeChoice)
    {
        try
        {
            var offered = offeredCards.Where(card => card is not null)
                .GroupBy(CardKey, StringComparer.Ordinal).Select(group => group.First()).ToList();
            var picked = pickedCards.Where(card => card is not null)
                .GroupBy(CardKey, StringComparer.Ordinal).Select(group => group.First()).ToList();

            // Only a normal, exactly-three-card Defect reward contributes. This
            // excludes colourless, mixed-colour and four-card special rewards.
            if (offered.Count != 3 || offered.Any(card => !IsDefectCard(card)) || picked.Count > 1)
                return;
            if (offered.Any(card => IsUnsupportedRarity(card.Rarity)))
                return;
            if (picked.Any(card => offered.All(option => CardKey(option) != CardKey(card))))
                return;

            lock (StateLock)
            {
                var state = _persisted ??= LoadState();
                var observationWeight = picked.Count == 0 ? 0.5 : 1.0;
                var pickedKey = picked.Count == 1 ? CardKey(picked[0]) : null;
                var deckContext = deckCountsBeforeChoice
                    .Where(pair => pair.Value > 0 && !string.IsNullOrWhiteSpace(pair.Key))
                    .Select(pair => pair.Key)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                foreach (var card in offered)
                {
                    var key = CardKey(card);
                    var bucket = BucketFor(deckCountsBeforeChoice.TryGetValue(key, out var copies) ? copies : 0);
                    var stats = state.GetOrCreateCard(key, card.Rarity.ToString());
                    var bucketStats = stats.Buckets[bucket];
                    bucketStats.Offers += observationWeight;
                    if (string.Equals(key, pickedKey, StringComparison.Ordinal))
                        bucketStats.Picks += 1.0;
                    stats.LastObservedRun = state.CompletedRunTransitions;

                    foreach (var deckKey in deckContext)
                    {
                        if (string.Equals(deckKey, key, StringComparison.Ordinal))
                            continue;
                        var pair = state.GetOrCreatePair(PairKey(key, deckKey));
                        pair.Offers += observationWeight;
                        if (string.Equals(key, pickedKey, StringComparison.Ordinal))
                            pair.Picks += 1.0;
                        pair.LastObservedRun = state.CompletedRunTransitions;
                    }
                }

                state.TotalRewardObservations++;
                state.TotalPicks += picked.Count;
                if (picked.Count == 0)
                    state.TotalSkips++;
                state.LastUpdatedUtc = DateTimeOffset.UtcNow;
                state.PrunePairs();
                SaveState(state);
                MainFile.Logger.Debug($"[DynamicCardOdds] learned ordinary Defect reward: picked={(pickedKey ?? "<skip>")}; offered={string.Join(',', offered.Select(SafeId))}; context={deckContext.Length}.");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[DynamicCardOdds] failed to learn reward: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static bool IsCardDisabled(CardModel? card)
    {
        if (card is null || !IsDefectCard(card))
            return false;
        lock (StateLock)
        {
            var state = _persisted ??= LoadState();
            return state.DisabledCards.Contains(CardKey(card), StringComparer.Ordinal);
        }
    }

    public static bool ToggleCardDisabled(CardModel? card)
    {
        if (card is null || !IsDefectCard(card))
            return false;
        try
        {
            var key = CardKey(card);
            lock (StateLock)
            {
                var state = _persisted ??= LoadState();
                if (state.DisabledCards.Contains(key, StringComparer.Ordinal))
                {
                    state.DisabledCards.RemoveAll(value => string.Equals(value, key, StringComparison.Ordinal));
                    _activeDisabled.Remove(key);
                    MainFile.Logger.Info($"[DynamicCardOdds] enabled {SafeId(card)}; no point cost.");
                }
                else
                {
                    state.DisabledCards.Add(key);
                    _activeDisabled.Add(key);
                    MainFile.Logger.Info($"[DynamicCardOdds] disabled {SafeId(card)}; reward weight=0; no point cost or limit.");
                }
                state.Normalize();
                state.LastUpdatedUtc = DateTimeOffset.UtcNow;
                SaveState(state);
                return true;
            }
        }
        catch { return false; }
    }

    public static int GetDisabledCount()
    {
        lock (StateLock)
        {
            var state = _persisted ??= LoadState();
            state.Normalize();
            return state.DisabledCards.Count;
        }
    }

    public static bool ShouldShowCard(CardModel? card) => card is not null && IsDefectCard(card) && !IsUnsupportedRarity(card.Rarity);

    public static DcoDisplayOdds GetDisplayOdds(CardModel card)
    {
        lock (StateLock)
        {
            var state = _persisted ??= LoadState();
            var key = CardKey(card);
            var disabled = state.DisabledCards.Contains(key, StringComparer.Ordinal);
            var deck = new Dictionary<string, int>(StringComparer.Ordinal);
            var factors = new double[3];
            for (var bucket = 0; bucket < 3; bucket++)
            {
                deck[key] = bucket;
                factors[bucket] = ComputeRawWeight(card, deck, state, forcedBucket: bucket, includePairContext: false);
            }
            var mean = Math.Max(0.0001, factors.Average());
            for (var i = 0; i < factors.Length; i++)
                factors[i] = Math.Clamp(factors[i] / mean, 0.55, 1.65);
            return new DcoDisplayOdds(factors[0], factors[1], factors[2], disabled);
        }
    }

    public static string GetDisplayLine(CardModel card)
    {
        var odds = GetDisplayOdds(card);
        var suffix = odds.Disabled ? "  [color=#D86A5A]已禁用（0x）[/color]" : string.Empty;
        return $"[color=#E6C15A]动态出率：首张{odds.FirstCopy:0.00}x · 第2张{odds.SecondCopy:0.00}x · 3张+{odds.ThirdOrMore:0.00}x[/color]{suffix}";
    }

    internal static string CardKeyForPatch(CardModel card) => CardKey(card);

    internal static Dictionary<string, int> CaptureDeckCounts(IEnumerable<CardModel>? cards)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (cards is null)
            return result;
        foreach (var card in cards)
        {
            if (card is null || !IsDefectCard(card) || IsUnsupportedRarity(card.Rarity))
                continue;
            var key = CardKey(card);
            result[key] = result.TryGetValue(key, out var count) ? count + 1 : 1;
        }
        return result;
    }

    private static void EnsureActiveSnapshot(Player player)
    {
        if (ReferenceEquals(_activeRunObject, player.RunState))
            return;
        lock (StateLock)
        {
            if (ReferenceEquals(_activeRunObject, player.RunState))
                return;
            var state = _persisted = LoadState();
            var runKey = GetRunKey(player);
            if (!string.IsNullOrWhiteSpace(state.LastDecayRunKey) && !string.Equals(state.LastDecayRunKey, runKey, StringComparison.Ordinal))
            {
                state.ApplyDecay(DecayPerNewRun);
                state.CompletedRunTransitions++;
            }
            state.LastDecayRunKey = runKey;
            state.LastUpdatedUtc = DateTimeOffset.UtcNow;
            SaveState(state);
            _activeSnapshot = state.CloneForRun();
            _activeDisabled = new HashSet<string>(state.DisabledCards, StringComparer.Ordinal);
            _activeRunObject = player.RunState;
            MainFile.Logger.Info($"[DynamicCardOdds] run snapshot loaded: run={runKey}; cards={state.Cards.Count}; pairs={state.Pairs.Count}; disabled={state.DisabledCards.Count}; decayTransitions={state.CompletedRunTransitions}.");
        }
    }

    private static string GetRunKey(Player player)
    {
        try { return player.RunState.Rng.StringSeed; }
        catch
        {
            try { return player.RunState.Rng.Seed.ToString(); }
            catch { return player.RunState.GetHashCode().ToString(); }
        }
    }

    private static Dictionary<string, int> GetDeckCounts(Player player)
    {
        try { return CaptureDeckCounts(player.Deck.Cards); }
        catch { return new Dictionary<string, int>(StringComparer.Ordinal); }
    }

    private static double ComputeRawWeight(
        CardModel card,
        IReadOnlyDictionary<string, int> deckCounts,
        DcoState state,
        int? forcedBucket = null,
        bool includePairContext = true)
    {
        var key = CardKey(card);
        if (state.DisabledCards.Contains(key, StringComparer.Ordinal))
            return 0.0;
        var copies = deckCounts.TryGetValue(key, out var count) ? count : 0;
        var bucket = forcedBucket ?? BucketFor(copies);
        var rarity = card.Rarity.ToString();
        var mean = GetRarityBucketMean(state, rarity, bucket);
        var bucketStats = state.Cards.TryGetValue(key, out var cardStats) ? cardStats.Buckets[bucket] : new DcoBucketStats();
        var posterior = (bucketStats.Picks + CardPriorStrength * mean) / (bucketStats.Offers + CardPriorStrength);
        var preference = Math.Exp(0.35 * (Logit(posterior) - Logit(mean)));
        preference = Math.Clamp(preference, 0.85, 1.20);

        // Low-exposure cards get a modest exploration lift. Rarity-local
        // normalization later makes this relative, so it does not inflate the
        // overall common/uncommon/rare roll rates.
        var exploration = 1.0 + 0.12 / Math.Sqrt(bucketStats.Offers + 1.0);
        var context = includePairContext ? ComputePairContextMultiplier(state, key, posterior, deckCounts) : 1.0;
        return Math.Clamp(preference * exploration * context, 0.65, 1.45);
    }

    private static double ComputePairContextMultiplier(
        DcoState state,
        string candidateKey,
        double candidateRate,
        IReadOnlyDictionary<string, int> deckCounts)
    {
        var signals = new List<double>();
        foreach (var deckCard in deckCounts)
        {
            if (deckCard.Value <= 0 || string.Equals(deckCard.Key, candidateKey, StringComparison.Ordinal))
                continue;
            if (!state.Pairs.TryGetValue(PairKey(candidateKey, deckCard.Key), out var pair) || pair.Offers < 0.25)
                continue;
            var conditional = (pair.Picks + PairPriorStrength * candidateRate) / (pair.Offers + PairPriorStrength);
            var confidence = pair.Offers / (pair.Offers + PairPriorStrength);
            var signal = (Logit(conditional) - Logit(candidateRate)) * confidence;
            // Presence matters more than raw duplicate count; the copy-bucket
            // model already handles duplicate demand for the candidate itself.
            signals.Add(signal);
        }
        if (signals.Count == 0)
            return 1.0;
        var strongest = signals.OrderByDescending(value => Math.Abs(value)).Take(4).ToArray();
        var aggregate = strongest.Sum() / Math.Sqrt(strongest.Length);
        return Math.Clamp(Math.Exp(0.10 * aggregate), 0.90, 1.18);
    }

    private static double GetRarityBucketMean(DcoState state, string rarity, int bucket)
    {
        double offers = 0;
        double picks = 0;
        foreach (var stats in state.Cards.Values)
        {
            if (!string.Equals(stats.Rarity, rarity, StringComparison.OrdinalIgnoreCase))
                continue;
            offers += stats.Buckets[bucket].Offers;
            picks += stats.Buckets[bucket].Picks;
        }
        if (offers < 0.5)
            return DefaultPickRate;
        return Math.Clamp((picks + 3.0 * DefaultPickRate) / (offers + 3.0), 0.03, 0.97);
    }

    private static int BucketFor(int copies) => copies <= 0 ? 0 : copies == 1 ? 1 : 2;

    private static double Logit(double value)
    {
        value = Math.Clamp(value, 0.02, 0.98);
        return Math.Log(value / (1.0 - value));
    }

    private static string PairKey(string candidate, string deckCard) => candidate + "||" + deckCard;

    private static bool IsDefectRun(Player player)
    {
        try { return player.Character.CardPool is DefectCardPool; }
        catch { return player.Character.CardPool.GetType().Name.Contains("Defect", StringComparison.OrdinalIgnoreCase); }
    }

    public static bool IsDefectCard(CardModel card)
    {
        var type = card.GetType();
        lock (TypeCacheLock)
            if (DefectTypeCache.TryGetValue(type, out var cached))
                return cached;
        bool result;
        try { result = card.Pool is DefectCardPool || card.Pool.GetType().Name.Contains("Defect", StringComparison.OrdinalIgnoreCase); }
        catch { result = type.Namespace?.Contains("Defect", StringComparison.OrdinalIgnoreCase) == true; }
        lock (TypeCacheLock)
            DefectTypeCache[type] = result;
        return result;
    }

    private static bool IsUnsupportedRarity(CardRarity rarity) => rarity is
        CardRarity.Basic or CardRarity.Ancient or CardRarity.Event or CardRarity.Token or
        CardRarity.None or CardRarity.Curse or CardRarity.Status;

    private static string CardKey(CardModel card)
    {
        var type = card.GetType();
        lock (TypeCacheLock)
            if (CardKeyCache.TryGetValue(type, out var cached))
                return cached;
        string key;
        try { key = card.CanonicalInstance.Id.ToString(); }
        catch
        {
            try { key = card.Id.ToString(); }
            catch { key = type.FullName ?? type.Name; }
        }
        lock (TypeCacheLock)
            CardKeyCache[type] = key;
        return key;
    }

    private static string SafeId(CardModel card)
    {
        try { return card.Id.ToString(); }
        catch { return card.GetType().Name; }
    }

    private static DcoState LoadState()
    {
        var path = GetStatePath();
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<DcoState>(File.ReadAllText(path), JsonOptions) ?? new DcoState();
                loaded.Normalize();
                return loaded;
            }

            var created = new DcoState();
            MigrateBetterDefectDisabledCards(created);
            created.Normalize();
            SaveState(created);
            return created;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[DynamicCardOdds] state load failed '{path}': {ex.GetType().Name}: {ex.Message}");
            return new DcoState();
        }
    }

    private static void MigrateBetterDefectDisabledCards(DcoState state)
    {
        foreach (var path in GetLegacyBetterDefectPaths())
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (TryGetProperty(root, "DisabledCards", out var disabled) && disabled.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in disabled.EnumerateArray())
                    {
                        var key = item.GetString();
                        if (!string.IsNullOrWhiteSpace(key))
                            state.DisabledCards.Add(key);
                    }
                }
                if (TryGetProperty(root, "EnabledStates", out var enabledStates) && enabledStates.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in enabledStates.EnumerateObject())
                        if (property.Value.ValueKind == JsonValueKind.False)
                            state.DisabledCards.Add(property.Name);
                }
                state.MigratedBetterDefectDisabledCards = true;
                state.MigrationSource = path;
                MainFile.Logger.Info($"[DynamicCardOdds] migrated {state.DisabledCards.Distinct(StringComparer.Ordinal).Count()} disabled cards from BetterDefect: {path}.");
                return;
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[DynamicCardOdds] legacy disabled-card migration skipped '{path}': {ex.Message}");
            }
        }
        state.MigratedBetterDefectDisabledCards = true;
        state.MigrationSource = "no legacy state found";
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static void SaveState(DcoState state)
    {
        state.Normalize();
        var path = GetStatePath();
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temporary, path);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[DynamicCardOdds] state save failed '{path}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string GetStatePath() => Path.Combine(GetDataDirectory(RuntimeFolderName), StateFileName);

    private static IEnumerable<string> GetLegacyBetterDefectPaths()
    {
        var paths = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                paths.Add(path);
        }
        try
        {
            var data = GetDataDirectory(BetterDefectFolderName);
            Add(Path.Combine(data, LegacyBetterDefectState));
            Add(Path.Combine(data, LegacyBetterDefectJson));
        }
        catch { }
        try
        {
            var modDirectory = GetModDirectory();
            var modsDirectory = Directory.GetParent(modDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(modsDirectory))
            {
                Add(Path.Combine(modsDirectory, BetterDefectFolderName, LegacyBetterDefectState));
                Add(Path.Combine(modsDirectory, BetterDefectFolderName, LegacyBetterDefectJson));
            }
        }
        catch { }
        return paths;
    }

    private static string GetDataDirectory(string child)
    {
        try
        {
            var userData = OS.GetUserDataDir();
            if (!string.IsNullOrWhiteSpace(userData))
                return Path.Combine(userData, child);
        }
        catch { }
        try
        {
            var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
                return Path.Combine(appData, "SlayTheSpire2", child);
        }
        catch { }
        return Path.Combine(GetModDirectory(), "Data", "Runtime", child);
    }

    private static string GetModDirectory()
    {
        try
        {
            var location = Assembly.GetExecutingAssembly().Location;
            var directory = string.IsNullOrWhiteSpace(location) ? null : Path.GetDirectoryName(location);
            if (!string.IsNullOrWhiteSpace(directory))
                return directory;
        }
        catch { }
        return AppContext.BaseDirectory;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}

internal readonly record struct DcoDisplayOdds(
    double FirstCopy,
    double SecondCopy,
    double ThirdOrMore,
    bool Disabled);

internal sealed class DcoState
{
    public int Version { get; set; } = 1;
    public List<string> DisabledCards { get; set; } = new();
    public Dictionary<string, DcoCardStats> Cards { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DcoPairStats> Pairs { get; set; } = new(StringComparer.Ordinal);
    public string LastDecayRunKey { get; set; } = string.Empty;
    public int CompletedRunTransitions { get; set; }
    public long TotalRewardObservations { get; set; }
    public long TotalPicks { get; set; }
    public long TotalSkips { get; set; }
    public bool MigratedBetterDefectDisabledCards { get; set; }
    public string MigrationSource { get; set; } = string.Empty;
    public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DcoCardStats GetOrCreateCard(string key, string rarity)
    {
        if (!Cards.TryGetValue(key, out var stats))
        {
            stats = new DcoCardStats { Rarity = rarity };
            Cards[key] = stats;
        }
        stats.Rarity = rarity;
        stats.Normalize();
        return stats;
    }

    public DcoPairStats GetOrCreatePair(string key)
    {
        if (!Pairs.TryGetValue(key, out var stats))
        {
            stats = new DcoPairStats();
            Pairs[key] = stats;
        }
        return stats;
    }

    public void ApplyDecay(double factor)
    {
        foreach (var card in Cards.Values)
            foreach (var bucket in card.Buckets)
            {
                bucket.Offers *= factor;
                bucket.Picks *= factor;
            }
        foreach (var pair in Pairs.Values)
        {
            pair.Offers *= factor;
            pair.Picks *= factor;
        }
        PrunePairs();
    }

    public void PrunePairs()
    {
        foreach (var key in Pairs.Where(pair => pair.Value.Offers < 0.05).Select(pair => pair.Key).ToArray())
            Pairs.Remove(key);
        if (Pairs.Count <= 12000)
            return;
        var keep = Pairs.OrderByDescending(pair => pair.Value.Offers).Take(12000).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Pairs = keep;
    }

    public DcoState CloneForRun()
    {
        var clone = new DcoState
        {
            Version = Version,
            DisabledCards = DisabledCards.ToList(),
            LastDecayRunKey = LastDecayRunKey,
            CompletedRunTransitions = CompletedRunTransitions,
            TotalRewardObservations = TotalRewardObservations,
            TotalPicks = TotalPicks,
            TotalSkips = TotalSkips,
            MigratedBetterDefectDisabledCards = MigratedBetterDefectDisabledCards,
            MigrationSource = MigrationSource,
            LastUpdatedUtc = LastUpdatedUtc,
        };
        foreach (var card in Cards)
            clone.Cards[card.Key] = card.Value.Clone();
        foreach (var pair in Pairs)
            clone.Pairs[pair.Key] = pair.Value.Clone();
        return clone;
    }

    public void Normalize()
    {
        Version = Math.Max(Version, 1);
        DisabledCards ??= new List<string>();
        DisabledCards = DisabledCards.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList();
        Cards = new Dictionary<string, DcoCardStats>(Cards ?? new Dictionary<string, DcoCardStats>(), StringComparer.Ordinal);
        Pairs = new Dictionary<string, DcoPairStats>(Pairs ?? new Dictionary<string, DcoPairStats>(), StringComparer.Ordinal);
        foreach (var card in Cards.Values)
            card.Normalize();
        foreach (var pair in Pairs.Values)
            pair.Normalize();
        PrunePairs();
    }
}

internal sealed class DcoCardStats
{
    public string Rarity { get; set; } = string.Empty;
    public DcoBucketStats[] Buckets { get; set; } = [new(), new(), new()];
    public int LastObservedRun { get; set; }

    public void Normalize()
    {
        if (Buckets is null || Buckets.Length != 3)
        {
            var old = Buckets ?? Array.Empty<DcoBucketStats>();
            Buckets = [new(), new(), new()];
            for (var index = 0; index < Math.Min(3, old.Length); index++)
                Buckets[index] = old[index] ?? new DcoBucketStats();
        }
        for (var index = 0; index < Buckets.Length; index++)
        {
            Buckets[index] ??= new DcoBucketStats();
            Buckets[index].Normalize();
        }
    }

    public DcoCardStats Clone() => new()
    {
        Rarity = Rarity,
        LastObservedRun = LastObservedRun,
        Buckets = Buckets.Select(bucket => bucket.Clone()).ToArray(),
    };
}

internal sealed class DcoBucketStats
{
    public double Offers { get; set; }
    public double Picks { get; set; }

    public void Normalize()
    {
        if (double.IsNaN(Offers) || Offers < 0) Offers = 0;
        if (double.IsNaN(Picks) || Picks < 0) Picks = 0;
        if (Picks > Offers + 0.0001) Picks = Offers;
    }

    public DcoBucketStats Clone() => new() { Offers = Offers, Picks = Picks };
}

internal sealed class DcoPairStats
{
    public double Offers { get; set; }
    public double Picks { get; set; }
    public int LastObservedRun { get; set; }

    public void Normalize()
    {
        if (double.IsNaN(Offers) || Offers < 0) Offers = 0;
        if (double.IsNaN(Picks) || Picks < 0) Picks = 0;
        if (Picks > Offers + 0.0001) Picks = Offers;
    }

    public DcoPairStats Clone() => new() { Offers = Offers, Picks = Picks, LastObservedRun = LastObservedRun };
}

[HarmonyPatch(typeof(CardFactory), "CreateForReward", [typeof(Player), typeof(IEnumerable<CardModel>), typeof(CardCreationOptions)])]
internal static class DcoRewardOddsPatch
{
    private static void Postfix(Player player, IEnumerable<CardModel> blacklist, CardCreationOptions options, ref CardModel __result)
        => __result = DcoAdaptiveOdds.TryReplaceRewardCard(player, blacklist, options, __result);
}

internal sealed class DcoRewardChoiceState
{
    public CardReward Reward { get; }
    public List<CardModel> Offered { get; }
    public Dictionary<string, int> DeckCountsBeforeChoice { get; }

    private DcoRewardChoiceState(CardReward reward, List<CardModel> offered, Dictionary<string, int> deckCounts)
    {
        Reward = reward;
        Offered = offered;
        DeckCountsBeforeChoice = deckCounts;
    }

    public static DcoRewardChoiceState Capture(CardReward reward)
    {
        try
        {
            var offered = reward.Cards.Where(card => card is not null).ToList();
            var owner = offered.Select(card => card.Owner).FirstOrDefault(player => player is not null);
            var counts = owner is null
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : DcoAdaptiveOdds.CaptureDeckCounts(owner.Deck.Cards);
            return new DcoRewardChoiceState(reward, offered, counts);
        }
        catch { return new DcoRewardChoiceState(reward, new List<CardModel>(), new Dictionary<string, int>(StringComparer.Ordinal)); }
    }
}

[HarmonyPatch]
internal static class DcoCardRewardOnSelectPatch
{
    private static MethodBase? TargetMethod() => AccessTools.Method(typeof(CardReward), "OnSelect");
    private static void Prefix(CardReward __instance, out DcoRewardChoiceState __state) => __state = DcoRewardChoiceState.Capture(__instance);
    private static void Postfix(DcoRewardChoiceState __state, ref Task<bool> __result) => __result = AfterSelect(__result, __state);

    private static async Task<bool> AfterSelect(Task<bool> original, DcoRewardChoiceState state)
    {
        var complete = await original;
        if (!complete)
            return complete;
        try
        {
            var remaining = state.Reward.Cards.Select(DcoAdaptiveOdds.CardKeyForPatch).ToHashSet(StringComparer.Ordinal);
            var picked = state.Offered.Where(card => !remaining.Contains(DcoAdaptiveOdds.CardKeyForPatch(card))).ToList();
            DcoAdaptiveOdds.RecordRewardChoice(state.Offered, picked, state.DeckCountsBeforeChoice);
        }
        catch { }
        return complete;
    }
}

[HarmonyPatch(typeof(CardReward), nameof(CardReward.OnSkipped))]
internal static class DcoCardRewardOnSkippedPatch
{
    private static void Prefix(CardReward __instance, out DcoRewardChoiceState __state) => __state = DcoRewardChoiceState.Capture(__instance);
    private static void Postfix(DcoRewardChoiceState __state) => DcoAdaptiveOdds.RecordRewardChoice(__state.Offered, Array.Empty<CardModel>(), __state.DeckCountsBeforeChoice);
}

[HarmonyPatch(typeof(CardReward), nameof(CardReward.Reroll))]
internal static class DcoCardRewardRerollPatch
{
    private static void Prefix(CardReward __instance, out DcoRewardChoiceState __state) => __state = DcoRewardChoiceState.Capture(__instance);
    private static void Postfix(DcoRewardChoiceState __state) => DcoAdaptiveOdds.RecordRewardChoice(__state.Offered, Array.Empty<CardModel>(), __state.DeckCountsBeforeChoice);
}
