namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmRecommendationEngine
{
    internal static AIrhythmExternalEvidenceEvaluationFlags ToExternalEvidenceEvaluationFlags(AIrhythmExternalEvidenceSummaryStatus status)
        => status switch
        {
            AIrhythmExternalEvidenceSummaryStatus.UnresolvedOnly => AIrhythmExternalEvidenceEvaluationFlags.Unresolved,
            AIrhythmExternalEvidenceSummaryStatus.Supported => AIrhythmExternalEvidenceEvaluationFlags.Supported,
            AIrhythmExternalEvidenceSummaryStatus.Conflicting => AIrhythmExternalEvidenceEvaluationFlags.Conflicting,
            _ => AIrhythmExternalEvidenceEvaluationFlags.None
        };

    internal static AIrhythmExternalEvidenceConfidenceGate ToExternalEvidenceConfidenceGate(AIrhythmExternalEvidenceEvaluationFlags flags)
        => flags switch
        {
            AIrhythmExternalEvidenceEvaluationFlags.Supported => AIrhythmExternalEvidenceConfidenceGate.Allowed,
            AIrhythmExternalEvidenceEvaluationFlags.Unresolved => AIrhythmExternalEvidenceConfidenceGate.Neutral,
            AIrhythmExternalEvidenceEvaluationFlags.Conflicting => AIrhythmExternalEvidenceConfidenceGate.Blocked,
            _ => AIrhythmExternalEvidenceConfidenceGate.NotApplicable
        };

    internal static AIrhythmExternalEvidenceAdjustmentCandidate ToExternalEvidenceAdjustmentCandidate(AIrhythmExternalEvidenceConfidenceGate gate)
        => gate == AIrhythmExternalEvidenceConfidenceGate.Allowed
            ? AIrhythmExternalEvidenceAdjustmentCandidate.Eligible
            : AIrhythmExternalEvidenceAdjustmentCandidate.None;

    internal static AIrhythmExternalEvidenceAdjustmentKind NormalizeExternalEvidenceAdjustmentKind(
        AIrhythmExternalEvidenceAdjustmentCandidate candidate,
        AIrhythmExternalEvidenceAdjustmentKind projectedKind)
        => candidate == AIrhythmExternalEvidenceAdjustmentCandidate.Eligible
            ? projectedKind
            : AIrhythmExternalEvidenceAdjustmentKind.None;

    internal static AIrhythmExternalEvidenceAdjustmentStrength ToExternalEvidenceAdjustmentStrength(
        AIrhythmExternalEvidenceAdjustmentCandidate candidate,
        AIrhythmExternalEvidenceAdjustmentKind kind)
    {
        if (candidate != AIrhythmExternalEvidenceAdjustmentCandidate.Eligible)
            return AIrhythmExternalEvidenceAdjustmentStrength.None;

        return kind switch
        {
            AIrhythmExternalEvidenceAdjustmentKind.IdentitySupport => AIrhythmExternalEvidenceAdjustmentStrength.Weak,
            AIrhythmExternalEvidenceAdjustmentKind.EpisodeSupport => AIrhythmExternalEvidenceAdjustmentStrength.Moderate,
            AIrhythmExternalEvidenceAdjustmentKind.RelationSupport => AIrhythmExternalEvidenceAdjustmentStrength.Weak,
            _ => AIrhythmExternalEvidenceAdjustmentStrength.None
        };
    }

    private const double ExternalEvidenceWeakRawCoordinateValue = 0.25d;
    private const double ExternalEvidenceModerateRawCoordinateValue = 0.75d;

    internal static double ToExternalEvidenceAdjustmentValue(AIrhythmExternalEvidenceAdjustmentStrength strength)
        => strength switch
        {
            AIrhythmExternalEvidenceAdjustmentStrength.Weak => ExternalEvidenceWeakRawCoordinateValue,
            AIrhythmExternalEvidenceAdjustmentStrength.Moderate => ExternalEvidenceModerateRawCoordinateValue,
            _ => 0.0d
        };

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
    internal static AIrhythmRawCoordinateShadowMetrics AnalyzeExternalEvidenceRawCoordinateShadowSweep(IReadOnlyList<AIrhythmRecommendation> orderedCandidates, double weakValue, double moderateValue)
    {
        if (orderedCandidates.Count == 0)
            return new(weakValue, moderateValue, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        static double AdjustmentFor(AIrhythmRecommendation item, double weak, double moderate)
            => item.ExternalEvidenceAdjustmentStrength switch
            {
                AIrhythmExternalEvidenceAdjustmentStrength.Weak => weak,
                AIrhythmExternalEvidenceAdjustmentStrength.Moderate => moderate,
                _ => 0.0d
            };

        var adjustedRaw = orderedCandidates
            .Select(item => (Item: item, Raw: item.DeviationRawScore + AdjustmentFor(item, weakValue, moderateValue)))
            .ToArray();
        var mean = adjustedRaw.Average(x => x.Raw);
        var variance = adjustedRaw.Average(x =>
        {
            var delta = x.Raw - mean;
            return delta * delta;
        });
        var standardDeviation = Math.Sqrt(variance);
        int ShadowDisplay(double raw)
        {
            if (adjustedRaw.Length <= 1 || standardDeviation < 0.000001d)
                return 50;
            var deviation = (raw - mean) / standardDeviation;
            return RelativeDisplayScoreFromZ(deviation);
        }

        var baselineRank = orderedCandidates
            .Select((item, index) => (key: RecommendationIdentityKey(item), rank: index))
            .ToDictionary(x => x.key, x => x.rank, StringComparer.OrdinalIgnoreCase);
        var shadowRows = adjustedRaw
            .Select(x => (x.Item, x.Raw, DisplayScore: ShadowDisplay(x.Raw)))
            .ToArray();
        var shadowOrdered = shadowRows
            .OrderByDescending(x => x.DisplayScore)
            .ThenBy(x => x.Item.Start)
            .Select(x => x.Item)
            .ToArray();
        var shadowRank = shadowOrdered
            .Select((item, index) => (key: RecommendationIdentityKey(item), rank: index))
            .ToDictionary(x => x.key, x => x.rank, StringComparer.OrdinalIgnoreCase);

        var displayChanged = 0;
        var rankChanged = 0;
        var maxRankDelta = 0;
        var maxDisplayScoreDelta = 0;
        foreach (var row in shadowRows)
        {
            var displayDelta = Math.Abs(row.DisplayScore - row.Item.Score);
            if (displayDelta != 0)
                displayChanged++;
            maxDisplayScoreDelta = Math.Max(maxDisplayScoreDelta, displayDelta);

            var key = RecommendationIdentityKey(row.Item);
            if (!baselineRank.TryGetValue(key, out var before) || !shadowRank.TryGetValue(key, out var after) || before == after)
                continue;
            rankChanged++;
            maxRankDelta = Math.Max(maxRankDelta, Math.Abs(before - after));
        }

        static HashSet<string> TopKeys(IReadOnlyList<AIrhythmRecommendation> values, int count)
            => values.Take(Math.Min(count, values.Count)).Select(RecommendationIdentityKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        static (int entrants, int exits) Boundary(IReadOnlyList<AIrhythmRecommendation> baseline, IReadOnlyList<AIrhythmRecommendation> shadow, int count)
        {
            var before = TopKeys(baseline, count);
            var after = TopKeys(shadow, count);
            return (after.Except(before, StringComparer.OrdinalIgnoreCase).Count(), before.Except(after, StringComparer.OrdinalIgnoreCase).Count());
        }

        var top10 = Boundary(orderedCandidates, shadowOrdered, 10);
        var top20 = Boundary(orderedCandidates, shadowOrdered, 20);
        var top50 = Boundary(orderedCandidates, shadowOrdered, 50);
        return new(
            weakValue,
            moderateValue,
            orderedCandidates.Count(item => AdjustmentFor(item, weakValue, moderateValue) > 0.000001d),
            displayChanged,
            rankChanged,
            top10.entrants, top10.exits,
            top20.entrants, top20.exits,
            top50.entrants, top50.exits,
            maxRankDelta,
            maxDisplayScoreDelta);
    }

    internal static AIrhythmRawCoordinateShadowDetail[] AnalyzeExternalEvidenceRawCoordinateShadowSweepDetails(IReadOnlyList<AIrhythmRecommendation> orderedCandidates, double weakValue, double moderateValue, int limit = 8)
    {
        if (orderedCandidates.Count == 0 || limit <= 0)
            return Array.Empty<AIrhythmRawCoordinateShadowDetail>();

        static double AdjustmentFor(AIrhythmRecommendation item, double weak, double moderate)
            => item.ExternalEvidenceAdjustmentStrength switch
            {
                AIrhythmExternalEvidenceAdjustmentStrength.Weak => weak,
                AIrhythmExternalEvidenceAdjustmentStrength.Moderate => moderate,
                _ => 0.0d
            };

        var adjustedRaw = orderedCandidates
            .Select(item => (Item: item, Raw: item.DeviationRawScore + AdjustmentFor(item, weakValue, moderateValue)))
            .ToArray();
        var mean = adjustedRaw.Average(x => x.Raw);
        var variance = adjustedRaw.Average(x =>
        {
            var delta = x.Raw - mean;
            return delta * delta;
        });
        var standardDeviation = Math.Sqrt(variance);
        int ShadowDisplay(double raw)
        {
            if (adjustedRaw.Length <= 1 || standardDeviation < 0.000001d)
                return 50;
            var deviation = (raw - mean) / standardDeviation;
            return RelativeDisplayScoreFromZ(deviation);
        }

        var baselineRank = orderedCandidates
            .Select((item, index) => (key: RecommendationIdentityKey(item), rank: index + 1))
            .ToDictionary(x => x.key, x => x.rank, StringComparer.OrdinalIgnoreCase);
        var shadowRows = adjustedRaw
            .Select(x => (x.Item, x.Raw, DisplayScore: ShadowDisplay(x.Raw)))
            .ToArray();
        var shadowRank = shadowRows
            .OrderByDescending(x => x.DisplayScore)
            .ThenBy(x => x.Item.Start)
            .Select((x, index) => (key: RecommendationIdentityKey(x.Item), rank: index + 1))
            .ToDictionary(x => x.key, x => x.rank, StringComparer.OrdinalIgnoreCase);

        return shadowRows
            .Where(row => AdjustmentFor(row.Item, weakValue, moderateValue) > 0.000001d)
            .Select(row =>
            {
                var key = RecommendationIdentityKey(row.Item);
                return new AIrhythmRawCoordinateShadowDetail(
                    row.Item.Title, weakValue, moderateValue,
                    baselineRank.GetValueOrDefault(key), shadowRank.GetValueOrDefault(key),
                    row.Item.DeviationRawScore, row.Raw,
                    row.Item.Score, row.DisplayScore,
                    row.Item.ExternalEvidenceAdjustmentKind, row.Item.ExternalEvidenceAdjustmentStrength, row.Item.ExternalEvidenceSupportingVerdictReason);
            })
            .OrderByDescending(item => Math.Abs(item.CurrentRank - item.ShadowRank))
            .ThenBy(item => item.CurrentRank)
            .Take(Math.Min(limit, 8))
            .ToArray();
    }

#endif

    private static string RecommendationSelectionWorkKey(
        AIrhythmRecommendation item,
        AIrhythmEvidenceIdentityContext identityContext)
    {
        var service = item.EventIdentity is null ? default : ServiceIdentityOf(item.EventIdentity);
        var workKey = CanonicalWorkKey(item.Title, service, identityContext);
        return workKey.Length >= 3 ? workKey : RecommendationIdentityKey(item);
    }

    private static AIrhythmRecommendation[] InterleaveRecommendationsByWork(
        IReadOnlyList<AIrhythmRecommendation> orderedCandidates,
        int limit,
        AIrhythmEvidenceIdentityContext identityContext)
    {
        if (limit <= 0 || orderedCandidates.Count == 0)
            return Array.Empty<AIrhythmRecommendation>();

        var queues = new Dictionary<string, Queue<AIrhythmRecommendation>>(StringComparer.OrdinalIgnoreCase);
        var workOrder = new List<string>();
        foreach (var item in orderedCandidates)
        {
            var workKey = RecommendationSelectionWorkKey(item, identityContext);
            if (!queues.TryGetValue(workKey, out var queue))
            {
                queue = new Queue<AIrhythmRecommendation>();
                queues[workKey] = queue;
                workOrder.Add(workKey);
            }
            queue.Enqueue(item);
        }

        var selected = new List<AIrhythmRecommendation>(Math.Min(limit, orderedCandidates.Count));
        while (selected.Count < limit)
        {
            var added = false;
            foreach (var workKey in workOrder)
            {
                var queue = queues[workKey];
                if (queue.Count == 0) continue;
                selected.Add(queue.Dequeue());
                added = true;
                if (selected.Count >= limit) break;
            }
            if (!added) break;
        }
        return selected.ToArray();
    }

    private static AIrhythmRecommendation[] SelectRecommendations(IReadOnlyList<AIrhythmRecommendation> candidates, int limit, AIrhythmEvidenceIdentityContext identityContext)
    {
        var convincingPool = candidates.Where(x => x.IsConvincing).ToArray();
        var discoveryPool = candidates.Where(x => !x.IsConvincing && x.IsPlausibleDiscovery).ToArray();
        var convincingDensity = Math.Min(convincingPool.Length, limit);

        // 処理条件 — 「納得」が十分に成立してからだけ、説明できる意外性を少量混ぜる。 件数比率を固定せず、その時点の信頼できる候補密度を優先する。
        var discoveryAllowance = convincingDensity switch
        {
            < 4 => 0,
            < 8 => 1,
            < 14 => 2,
            _ => Math.Min(4, Math.Max(2, limit / 6))
        };
        discoveryAllowance = Math.Min(discoveryAllowance, discoveryPool.Length);

        // 作品識別 — Scoreそのものは変更せず、同一Workの第2候補以降だけを次ラウンドへ回す。 まず異なるWorkの最高順位候補を一巡し、候補不足時だけ2巡目・3巡目を戻す。
        // 固定件数上限ではないため、候補母集団が少ない場合は同一Workも通常どおり復帰する。
        var convincingTarget = Math.Max(0, limit - discoveryAllowance);
        var convincing = InterleaveRecommendationsByWork(convincingPool, convincingTarget, identityContext);
        var discovery = InterleaveRecommendationsByWork(discoveryPool, discoveryAllowance, identityContext);
        var combinedByScore = convincing
            .Concat(discovery)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Start)
            .ToArray();
        var selected = InterleaveRecommendationsByWork(combinedByScore, limit, identityContext).ToList();

        // 作品識別 — 選抜枠が不足した場合だけ、未選択の納得候補を同じWork-aware順で戻す。
        if (selected.Count < limit)
        {
            var selectedKeys = selected.Select(RecommendationIdentityKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remainingConvincing = convincingPool
                .Where(x => selectedKeys.Add(RecommendationIdentityKey(x)))
                .ToArray();
            if (remainingConvincing.Length > 0)
            {
                var refill = InterleaveRecommendationsByWork(remainingConvincing, limit - selected.Count, identityContext);
                selected.AddRange(refill);
                selected = InterleaveRecommendationsByWork(
                    selected.OrderByDescending(x => x.Score).ThenBy(x => x.Start).ToArray(),
                    limit,
                    identityContext).ToList();
            }
        }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var selectedOccupancy = selected
            .Select((item, index) => new
            {
                Work = RecommendationSelectionWorkKey(item, identityContext),
                Rank = index + 1
            })
            .GroupBy(x => x.Work, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Work = group.Key, Count = group.Count(), BestRank = group.Min(x => x.Rank) })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.BestRank)
            .ToArray();
        AIrhythmDataState.WriteDeveloperLog(
            $"LOCAL_WORK_SELECTION_INTERLEAVE selected={selected.Count} uniqueWorks={selectedOccupancy.Length} repeatedWorks={selectedOccupancy.Count(x => x.Count > 1)} maxPerWork={(selectedOccupancy.Length == 0 ? 0 : selectedOccupancy.Max(x => x.Count))} samples=[{string.Join(",", selectedOccupancy.Take(8).Select(x => $"{x.Work}:{x.Count}@{x.BestRank}"))}] policy=work_round_interleave_no_score_mutation");
#endif

        return selected.ToArray();
    }

    private static string RecommendationIdentityKey(AIrhythmRecommendation item)
        => item.EventIdentity is not null
            ? $"{item.EventIdentity.NetworkId}:{item.EventIdentity.TransportStreamId}:{item.EventIdentity.ServiceId}:{item.EventIdentity.EventNumber}:{item.EventIdentity.Start.UtcDateTime.Ticks}"
            : $"{item.SeriesKey}|unknown-service|{item.Start.UtcDateTime.Ticks}";

    private static double ReservationEvidenceWeight(TvAirReservationDto value)
        => value.Intent == TvAirReservationIntent.System ? 0.0d : 1.0d;

    private enum AIrhythmLocalEvidenceStrength
    {
        None,
        Weak,
        Moderate,
        Strong
    }

    private enum AIrhythmLocalEvidenceFamily
    {
        None,
        ContinuityFamily,
        ExplicitInterestFamily
    }

    private sealed class AIrhythmLocalWorkAggregate
    {
        public int RecordingCount { get; set; }
        public int ReservationCount { get; set; }
        public bool HasNonAutomatedReservation { get; set; }
        public HashSet<DateOnly> ActiveDays { get; } = new();
        public HashSet<int> ActiveWeeks { get; } = new();
        public DateTimeOffset? LastSeen { get; set; }

        public void AddActivity(DateTimeOffset when)
        {
            ActiveDays.Add(DateOnly.FromDateTime(when.LocalDateTime.Date));
            var localDate = when.LocalDateTime;
            ActiveWeeks.Add((ISOWeek.GetYear(localDate) * 100) + ISOWeek.GetWeekOfYear(localDate));
            if (LastSeen is null || when > LastSeen.Value)
                LastSeen = when;
        }
    }

    internal readonly record struct AIrhythmLocalWorkCacheKey(
        string Title,
        AIrhythmServiceIdentity Service);

    internal readonly record struct AIrhythmLocalEpisodeCacheKey(
        string Title,
        AIrhythmServiceIdentity Service);

    internal readonly record struct AIrhythmLocalEpisodeCacheValue(
        bool HasIdentity,
        AIrhythmLocalEpisodeIdentity Identity);

    internal readonly record struct AIrhythmLocalWorkAliasKey(
        string WorkKey,
        AIrhythmServiceIdentity ServiceIdentity);

    internal sealed class AIrhythmEvidenceIdentityContext
    {
        public AIrhythmEvidenceIdentityContext(
            IReadOnlyDictionary<string, int[]> numericLocalValuesByService,
            IReadOnlyDictionary<string, int[]> numericEvidenceValuesByService,
            IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string> workAliases,
            IReadOnlyDictionary<string, string> externalCanonicalTitles,
            IReadOnlyDictionary<string, string> normalizationPromotions)
        {
            NumericLocalValuesByService = numericLocalValuesByService;
            NumericEvidenceValuesByService = numericEvidenceValuesByService;
            WorkAliases = workAliases;
            ExternalCanonicalTitles = externalCanonicalTitles;
            NormalizationPromotions = normalizationPromotions;
        }

        public IReadOnlyDictionary<string, int[]> NumericLocalValuesByService { get; }
        public IReadOnlyDictionary<string, int[]> NumericEvidenceValuesByService { get; }
        public IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string> WorkAliases { get; }
        public IReadOnlyDictionary<string, string> ExternalCanonicalTitles { get; }
        public IReadOnlyDictionary<string, string> NormalizationPromotions { get; }

        // 保存・読込 — Build() creates one identity context per immutable runtime snapshot. Cache canonical facts by
        // DTO reference so Score / charts / discovery / search share the exact same canonical result instead of
        // repeating title normalization, tokenization and Work resolution. The cache is intentionally
        // snapshot-scoped; it never survives invalidation or refresh.
        internal Dictionary<TvAirProgramEventDto, AIrhythmCanonicalEvidenceFacts> ProgramFacts { get; }
            = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<TvAirReservationDto, AIrhythmCanonicalEvidenceFacts> ReservationFacts { get; }
            = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<TvAirRecordingHistoryDto, AIrhythmCanonicalEvidenceFacts> HistoryFacts { get; }
            = new(ReferenceEqualityComparer.Instance);
        // 保存・読込 — Title-only transforms are immutable within one snapshot. Reuse them when the same programme
        // name occurs across services/dates; never cache service-dependent work decisions.
        internal Dictionary<string, string[]> TitleTokens { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> PromotionLookupKeys { get; } = new(StringComparer.Ordinal);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        internal int TitleTokenCacheHits { get; set; }
        internal int TitleTokenCacheMisses { get; set; }
        internal int PromotionKeyCacheHits { get; set; }
        internal int PromotionKeyCacheMisses { get; set; }
#endif
        internal Dictionary<AIrhythmLocalWorkCacheKey, string> LocalWorkKeys { get; } = new();
        internal Dictionary<AIrhythmLocalWorkCacheKey, string> CanonicalWorkResolutions { get; } = new();
        internal int LocalWorkKeyCacheHits { get; set; }
        internal int LocalWorkKeyCacheMisses { get; set; }
        internal Dictionary<AIrhythmLocalEpisodeCacheKey, AIrhythmLocalEpisodeCacheValue> LocalEpisodeIdentities { get; } = new();
        internal int LocalEpisodeIdentityCacheHits { get; set; }
        internal int LocalEpisodeIdentityCacheMisses { get; set; }
        internal int ProgramFactsCount => ProgramFacts.Count;
        internal int ReservationFactsCount => ReservationFacts.Count;
        internal int HistoryFactsCount => HistoryFacts.Count;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        internal int CanonicalFactsHits { get; set; }
        internal int CanonicalFactsMisses { get; set; }
        internal long CanonicalPreEvaluationTitleBytes { get; set; }
        internal long CanonicalLocalWorkKeyBytes { get; set; }
        // 保存・読込 — Split the hot allocation scope by actual cache outcome. No extra GC probes:
        // CanonicalEvidenceFacts already measures the enclosing operation; the cache hit counter supplies its
        // branch result. These counters are diagnostic-only.
        internal long CanonicalLocalWorkKeyHitBytes { get; set; }
        internal long CanonicalLocalWorkKeyMissBytes { get; set; }
        internal long CanonicalLocalWorkKeyHitCalls { get; set; }
        internal long CanonicalLocalWorkKeyMissCalls { get; set; }
        internal long CanonicalAliasResolutionBytes { get; set; }
        internal long CanonicalGenreBytes { get; set; }
        internal long CanonicalTokensBytes { get; set; }
        internal long CanonicalReplayBytes { get; set; }

        internal void ResetCanonicalAllocationBreakdown()
        {
            CanonicalPreEvaluationTitleBytes = 0;
            CanonicalLocalWorkKeyBytes = 0;
            CanonicalLocalWorkKeyHitBytes = 0;
            CanonicalLocalWorkKeyMissBytes = 0;
            CanonicalLocalWorkKeyHitCalls = 0;
            CanonicalLocalWorkKeyMissCalls = 0;
            CanonicalAliasResolutionBytes = 0;
            CanonicalGenreBytes = 0;
            CanonicalTokensBytes = 0;
            CanonicalReplayBytes = 0;
        }
#endif
    }

}
