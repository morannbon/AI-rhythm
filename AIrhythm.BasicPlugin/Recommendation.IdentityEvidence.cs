namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmRecommendationEngine
{
    internal static AIrhythmEvidenceIdentityContext BuildEvidenceIdentityContext(AIrhythmRuntimeSnapshot snapshot)
    {
        var epgNumeric = BuildNumericParenthesizedLocalSequenceValuesByService(snapshot.Events);
        var numericEvidence = BuildNumericParenthesizedEvidenceValuesByService(snapshot);
        // 録画実績・復旧 — The EPG is a moving window. Successful recordings and established reservations are
        // independent historical corroboration of a same-service episode sequence. Retain both evidence sets
        // before resolving any Work identity; neither changes the fact count.
        var numeric = MergeNumericWorkSequenceEvidence(epgNumeric, numericEvidence);
        var aliases = BuildLocalWorkAliases(snapshot, numeric);
        var externalCanonicalTitles = AIrhythmDataState.GetExternalPreEvaluationCanonicalTitles();
        var normalizationPromotions = AIrhythmDataState.GetNormalizationPromotions();
        return new AIrhythmEvidenceIdentityContext(numeric, numericEvidence, aliases, externalCanonicalTitles, normalizationPromotions);
    }

    private static IReadOnlyDictionary<string, int[]> MergeNumericWorkSequenceEvidence(
        IReadOnlyDictionary<string, int[]> epg,
        IReadOnlyDictionary<string, int[]> historical)
    {
        var result = epg.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in historical)
        {
            if (result.TryGetValue(entry.Key, out var previous))
                result[entry.Key] = previous.Concat(entry.Value).Distinct().OrderBy(x => x).ToArray();
            else
                result[entry.Key] = entry.Value;
        }
        return result;
    }

    // 録画実績・復旧 — Pure title transforms are reused across render snapshots. Manual external-evidence refresh
    // invalidates the score snapshot, but it must not force the same title-only normalization and canonical
    // parsing work to allocate again for every EPG row. These caches contain no reservation/history/provider
    // decision state, so reuse cannot change recommendation semantics. The soft cap prevents long-running
    // hosts from retaining an unbounded catalogue of old titles.
    private const int SharedTitleTransformCacheLimit = 24576;
    private static readonly ConcurrentDictionary<string, string> SharedExternalPreEvaluationTitleKeys = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> SharedEvidenceSeriesKeys = new(StringComparer.Ordinal);

    private static string PreEvaluationTitle(string? title, AIrhythmEvidenceIdentityContext context)
    {
        var raw = (title ?? string.Empty).Trim();
        if (raw.Length == 0)
            return string.Empty;
        if (context.ExternalCanonicalTitles.Count == 0)
            return raw;
        // 外部情報 — External catalog aliases cannot turn a promotional broadcast into the advertised programme: its
        // derived identity is a local structural fact.
        if (ClassifyDerivedProgramRelation(raw) == AIrhythmDerivedProgramRelation.Promo)
            return raw;
        // 外部情報 — A composite title containing an on-air show and a different show's announcement is not an
        // unambiguous external catalogue alias.
        if (!string.Equals(AiredTitleEvidenceText(raw), raw, StringComparison.Ordinal))
            return raw;
        var key = ExternalPreEvaluationTitleKey(raw);
        return key.Length > 0 && context.ExternalCanonicalTitles.TryGetValue(key, out var canonical) && !string.IsNullOrWhiteSpace(canonical)
            ? canonical
            : raw;
    }

    internal static string ExternalPreEvaluationTitleKey(string? title)
    {
        var source = title ?? string.Empty;
        if (source.Length == 0)
            return string.Empty;
        if (SharedExternalPreEvaluationTitleKeys.TryGetValue(source, out var cached))
            return cached;

        var computed = new string(source.Normalize(NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        if (SharedExternalPreEvaluationTitleKeys.Count < SharedTitleTransformCacheLimit)
            SharedExternalPreEvaluationTitleKeys.TryAdd(source, computed);
        return computed;
    }

    private static bool LocalWorkKeyRequiresServiceScope(string value)
    {
        // 保存・読込 — LocalWorkKey is service-dependent only for the trailing-parenthesized-number path, where
        // same-service corroboration decides whether the numeric suffix belongs to Work. Every such title must
        // end in a closing parenthesis. Retain all closing-parenthesis titles service-scoped conservatively;
        // ordinary titles can safely share one snapshot cache entry across services without changing Work
        // identity semantics.
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(value[i]))
                continue;
            return value[i] is ')' or '）';
        }
        return false;
    }

    private static string[] TitleTokensCached(string analysisTitle, AIrhythmEvidenceIdentityContext context)
    {
        if (context.TitleTokens.TryGetValue(analysisTitle, out var tokens))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            context.TitleTokenCacheHits++;
#endif
            return tokens;
        }
        tokens = Tokens(analysisTitle).ToArray();
        context.TitleTokens.Add(analysisTitle, tokens);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        context.TitleTokenCacheMisses++;
#endif
        return tokens;
    }

    private static string PromotionLookupKeyCached(string analysisTitle, AIrhythmEvidenceIdentityContext context)
    {
        if (context.PromotionLookupKeys.TryGetValue(analysisTitle, out var key))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            context.PromotionKeyCacheHits++;
#endif
            return key;
        }
        key = CompactIdentity(NormalizationLearnerComparableTitle(AiredTitleEvidenceText(analysisTitle)));
        context.PromotionLookupKeys.Add(analysisTitle, key);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        context.PromotionKeyCacheMisses++;
#endif
        return key;
    }

    private static string LocalWorkKeyCached(
        string? title,
        AIrhythmServiceIdentity service,
        AIrhythmEvidenceIdentityContext context)
    {
        var source = title ?? string.Empty;
        var cacheService = LocalWorkKeyRequiresServiceScope(source) ? service : default;
        var cacheKey = new AIrhythmLocalWorkCacheKey(source, cacheService);
        if (context.LocalWorkKeys.TryGetValue(cacheKey, out var cached))
        {
            context.LocalWorkKeyCacheHits++;
            return cached;
        }

        var computed = LocalWorkKey(source, service, context.NumericLocalValuesByService);
        context.LocalWorkKeys[cacheKey] = computed;
        context.LocalWorkKeyCacheMisses++;
        return computed;
    }

    internal static string CanonicalWorkKey(string? title, AIrhythmServiceIdentity service, AIrhythmEvidenceIdentityContext context)
    {
        var analysisTitle = PreEvaluationTitle(title, context);
        return CanonicalWorkIdentity(analysisTitle, service, context);
    }

    // 録画実績・復旧 — Recordings, reservations, events, and title lookups resolve the same Work identity.
    private static string CanonicalWorkIdentity(
        string analysisTitle,
        AIrhythmServiceIdentity service,
        AIrhythmEvidenceIdentityContext context)
    {
        return CanonicalWorkIdentity(analysisTitle, service, context, LocalWorkKeyCached(analysisTitle, service, context));
    }

    // 保存・読込 — When canonical facts already computed the service-scoped local key, pass it through. The
    // output cache is limited to this immutable evidence snapshot: it never retains past EPG titles or
    // persists any promoted/alias decision across a refresh.
    private static string CanonicalWorkIdentity(
        string analysisTitle,
        AIrhythmServiceIdentity service,
        AIrhythmEvidenceIdentityContext context,
        string localWorkKey)
    {
        var resolutionKey = new AIrhythmLocalWorkCacheKey(analysisTitle, service);
        if (context.CanonicalWorkResolutions.TryGetValue(resolutionKey, out var resolved))
            return resolved;

        // 作品識別 — Promotion broadcasts never borrow the underlying programme's evidence, regardless of saved
        // aliases or learner proposals. Both have different canonical Work identities even when their quoted
        // advertised title matches.
        if (ClassifyDerivedProgramRelation(analysisTitle) == AIrhythmDerivedProgramRelation.Promo)
        {
            context.CanonicalWorkResolutions[resolutionKey] = localWorkKey;
            return localWorkKey;
        }

        var canonical = CanonicalizeLocalWorkKey(localWorkKey, service, context.WorkAliases);
        // 作品識別 — Both evidence-backed aliases and learner promotions must respect the identical complete-Work
        // boundary. A learner must not reinstate a shorter prefix that a locally parsed, structurally
        // inseparable work rejected. Parse only when a shortening candidate actually exists.
        CanonicalTitleParts parsed = default;
        var parsedReady = false;
        bool WouldDiscardCompleteWork(string candidate)
        {
            if (candidate.Length < 3 || candidate.Length >= localWorkKey.Length
                || localWorkKey.Length - candidate.Length < 3
                || !localWorkKey.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!parsedReady)
            {
                parsed = ParseCanonicalTitleParts(analysisTitle);
                parsedReady = true;
            }
            return string.Equals(CompactIdentity(parsed.Work), localWorkKey, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(parsed.Episode)
                && string.IsNullOrWhiteSpace(parsed.Part)
                && string.IsNullOrWhiteSpace(parsed.Subtitle)
                && string.IsNullOrWhiteSpace(parsed.Event)
                && string.IsNullOrWhiteSpace(parsed.Stage)
                && string.IsNullOrWhiteSpace(parsed.BroadcastAttribute);
        }
        if (WouldDiscardCompleteWork(canonical))
            canonical = localWorkKey;
        // 作品識別 — A verified same-service episode/work alias is stronger than a learned population-level prefix.
        // Never undo that evidence in the promotion path.
        var localEvidenceAliasConfirmed = !string.Equals(canonical, localWorkKey, StringComparison.OrdinalIgnoreCase)
            && context.WorkAliases.ContainsKey(new AIrhythmLocalWorkAliasKey(localWorkKey, service));

        var promotionKey = context.NormalizationPromotions.Count == 0
            ? string.Empty
            : PromotionLookupKeyCached(analysisTitle, context);
        if (promotionKey.Length > 0
            && context.NormalizationPromotions.TryGetValue(promotionKey, out var promoted)
            && !string.IsNullOrWhiteSpace(promoted))
        {
            // 作品識別 — A same-service parenthesized episode sequence takes precedence. Otherwise a structurally
            // complete Work must not be reduced merely because the learned prefix has survived across observation
            // days.
            if (!(canonical.Length > 0
                && !string.Equals(canonical, promoted, StringComparison.OrdinalIgnoreCase)
                && (localEvidenceAliasConfirmed || IsCorroboratedParenthesizedWorkSequence(
                    analysisTitle, service, context.NumericLocalValuesByService)))
                && !WouldDiscardCompleteWork(promoted))
                canonical = promoted;
        }
        context.CanonicalWorkResolutions[resolutionKey] = canonical;
        return canonical;
    }

    internal static string CanonicalWorkKey(TvAirProgramEventDto item, AIrhythmEvidenceIdentityContext context)
        => CanonicalEvidenceFacts(item, context).WorkKey;

    internal static string CanonicalWorkKey(TvAirReservationDto item, AIrhythmEvidenceIdentityContext context)
        => CanonicalEvidenceFacts(item, context).WorkKey;

    internal static string CanonicalWorkKey(TvAirRecordingHistoryDto item, AIrhythmEvidenceIdentityContext context)
        => CanonicalEvidenceFacts(item, context).WorkKey;

    internal sealed record AIrhythmCanonicalEvidenceFacts(
        string WorkKey,
        string GenreKey,
        AIrhythmServiceIdentity ServiceIdentity,
        int Hour,
        string TimeBand,
        IReadOnlyList<string> Terms,
        bool IsReplay,
        bool UsesExternalCanonicalTitle);

    internal sealed record AIrhythmCanonicalRecordingWorkAggregate(
        string GenreKey,
        string WorkKey,
        string DisplayTitle,
        int RecordingCount,
        int DistinctServiceCount,
        IReadOnlyDictionary<AIrhythmServiceIdentity, int> ServiceRecordingCounts);

    internal sealed record AIrhythmCanonicalRecordingGenreAggregate(
        string GenreKey,
        int RecordingCount,
        IReadOnlyList<AIrhythmCanonicalRecordingWorkAggregate> Works);

    internal sealed record AIrhythmCanonicalRecordingTotalAggregate(
        string WorkKey,
        string DisplayTitle,
        int RecordingCount);

    private static string CanonicalTimeBand(int hour)
        => hour switch
        {
            >= 5 and < 10 => "朝",
            >= 10 and < 17 => "昼",
            >= 17 and < 20 => "夕方",
            >= 20 => "夜",
            _ => "深夜"
        };

    private static AIrhythmCanonicalEvidenceFacts CanonicalEvidenceFacts(
        string? title,
        string? genre,
        AIrhythmServiceIdentity service,
        DateTimeOffset start,
        AIrhythmEvidenceIdentityContext context)
    {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalDetailStart = GC.GetAllocatedBytesForCurrentThread();
#endif
        var rawTitle = (title ?? string.Empty).Trim();
        var analysisTitle = PreEvaluationTitle(rawTitle, context);
        var usesExternalCanonicalTitle = !string.Equals(analysisTitle, rawTitle, StringComparison.Ordinal);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterPreEvaluation = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalPreEvaluationTitleBytes += canonicalAfterPreEvaluation - canonicalDetailStart;
#endif
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var workKeyMissesBefore = context.LocalWorkKeyCacheMisses;
#endif
        var localWorkKey = LocalWorkKeyCached(analysisTitle, service, context);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterLocalWorkKey = GC.GetAllocatedBytesForCurrentThread();
        var localWorkAllocation = canonicalAfterLocalWorkKey - canonicalAfterPreEvaluation;
        context.CanonicalLocalWorkKeyBytes += localWorkAllocation;
        if (context.LocalWorkKeyCacheMisses != workKeyMissesBefore)
        {
            context.CanonicalLocalWorkKeyMissCalls++;
            context.CanonicalLocalWorkKeyMissBytes += localWorkAllocation;
        }
        else
        {
            context.CanonicalLocalWorkKeyHitCalls++;
            context.CanonicalLocalWorkKeyHitBytes += localWorkAllocation;
        }
#endif
        var canonicalWorkKey = CanonicalWorkIdentity(analysisTitle, service, context, localWorkKey);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterAliasResolution = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalAliasResolutionBytes += canonicalAfterAliasResolution - canonicalAfterLocalWorkKey;
#endif
        var genreKey = NormalizeGenre(genre);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterGenre = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalGenreBytes += canonicalAfterGenre - canonicalAfterAliasResolution;
#endif
        var terms = TitleTokensCached(AiredTitleEvidenceText(analysisTitle), context);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterTokens = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalTokensBytes += canonicalAfterTokens - canonicalAfterGenre;
#endif
        var isReplay = IsReplayTitle(title);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterReplay = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalReplayBytes += canonicalAfterReplay - canonicalAfterTokens;
#endif
        return new AIrhythmCanonicalEvidenceFacts(
            canonicalWorkKey,
            genreKey,
            service,
            start.Hour,
            CanonicalTimeBand(start.Hour),
            terms,
            isReplay,
            usesExternalCanonicalTitle);
    }

    private static AIrhythmCanonicalEvidenceFacts CanonicalEvidenceFacts(TvAirProgramEventDto item, AIrhythmEvidenceIdentityContext context)
    {
        if (context.ProgramFacts.TryGetValue(item, out var cached))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            context.CanonicalFactsHits++;
#endif
            return cached;
        }
        var facts = CanonicalEvidenceFacts(item.Title, item.Genre, ServiceIdentityOf(item), item.Start, context);
        context.ProgramFacts[item] = facts;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        context.CanonicalFactsMisses++;
#endif
        return facts;
    }

    private static AIrhythmCanonicalEvidenceFacts CanonicalEvidenceFacts(TvAirReservationDto item, AIrhythmEvidenceIdentityContext context)
    {
        if (context.ReservationFacts.TryGetValue(item, out var cached))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            context.CanonicalFactsHits++;
#endif
            return cached;
        }
        var facts = CanonicalEvidenceFacts(item.ProgramTitle, string.Empty, ServiceIdentityOf(item), item.Start, context);
        context.ReservationFacts[item] = facts;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        context.CanonicalFactsMisses++;
#endif
        return facts;
    }

    private static AIrhythmCanonicalEvidenceFacts CanonicalEvidenceFacts(TvAirRecordingHistoryDto item, AIrhythmEvidenceIdentityContext context)
    {
        if (context.HistoryFacts.TryGetValue(item, out var cached))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            context.CanonicalFactsHits++;
#endif
            return cached;
        }
        var start = item.ActualStart ?? item.Start;
        var facts = CanonicalEvidenceFacts(item.ProgramTitle, item.Genre, ServiceIdentityOf(item), start, context);
        context.HistoryFacts[item] = facts;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        context.CanonicalFactsMisses++;
#endif
        return facts;
    }

    internal static string CanonicalWorkDisplayTitle(
        TvAirRecordingHistoryDto item,
        AIrhythmEvidenceIdentityContext context)
    {
        var service = ServiceIdentityOf(item);
        var analysisTitle = PreEvaluationTitle(item.ProgramTitle, context);
        var localWorkKey = LocalWorkKeyCached(analysisTitle, service, context);
        var canonicalWorkKey = CanonicalWorkIdentity(analysisTitle, service, context, localWorkKey);

        var displayTitle = SeriesDisplayTitle(item.ProgramTitle);
        if (IsCorroboratedParenthesizedWorkSequence(item.ProgramTitle, service, context.NumericLocalValuesByService)
            && TryParseTrailingParenthesizedNumber(item.ProgramTitle, out var stem, out _))
        {
            var sequenceLabel = SeriesDisplayTitle(stem);
            if (sequenceLabel.Length > 0)
                displayTitle = sequenceLabel;
        }

        // 録画実績・復旧 — The canonical Work may have separated a leading 【...】 broadcast container.  Dashboard labels
        // must project that same decision instead of re-attaching the container from the raw history title.
        // Accept the remainder only when it reproduces the already-decided canonical Work key exactly; this is
        // presentation projection, not a second identity rule.
        if (canonicalWorkKey.Length >= 3)
        {
            var containerParts = ParseLeadingContainerParts(displayTitle);
            if (!string.IsNullOrWhiteSpace(containerParts.Container))
            {
                var remainderDisplay = SeriesDisplayTitle(containerParts.Remainder);
                if (remainderDisplay.Length > 0
                    && string.Equals(CompactIdentity(remainderDisplay), canonicalWorkKey, StringComparison.OrdinalIgnoreCase))
                    return remainderDisplay;
            }
        }

        // 作品識別 — WorkAliases are part of the common canonical Work Identity.  Dashboard labels must not
        // re-expand an alias-resolved Work back to one evidence row's longer variant.  When the canonical key is
        // an evidence-backed prefix of the human-readable title, project that exact prefix as the display label.
        // This changes presentation only; Work grouping, Score, Continuity and all other canonical facts remain
        // owned by CanonicalWorkKey.
        if (canonicalWorkKey.Length >= 3
            && canonicalWorkKey.Length < localWorkKey.Length
            && TryProjectCanonicalWorkDisplayPrefix(displayTitle, canonicalWorkKey, out var canonicalDisplay))
            return canonicalDisplay;

        return displayTitle;
    }

    private static bool TryProjectCanonicalWorkDisplayPrefix(
        string displayTitle,
        string canonicalWorkKey,
        out string projected)
    {
        projected = string.Empty;
        if (string.IsNullOrWhiteSpace(displayTitle) || canonicalWorkKey.Length < 3)
            return false;

        var compactIndex = 0;
        for (var i = 0; i < displayTitle.Length; i++)
        {
            var ch = displayTitle[i];
            if (!char.IsLetterOrDigit(ch))
                continue;

            if (compactIndex >= canonicalWorkKey.Length
                || char.ToLowerInvariant(ch) != char.ToLowerInvariant(canonicalWorkKey[compactIndex]))
                return false;

            compactIndex++;
            if (compactIndex != canonicalWorkKey.Length)
                continue;

            var candidate = displayTitle[..(i + 1)]
                .Trim()
                .TrimEnd('～', '~', '・', '：', ':', '-', '－');
            if (candidate.Length == 0
                || !string.Equals(CompactIdentity(candidate), canonicalWorkKey, StringComparison.OrdinalIgnoreCase))
                return false;

            projected = candidate;
            return true;
        }

        return false;
    }

    internal static IReadOnlyList<AIrhythmCanonicalRecordingGenreAggregate> BuildCanonicalRecordingAggregates(
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmEvidenceIdentityContext context)
    {
        var rows = snapshot.History
            .Select(item =>
            {
                var facts = CanonicalEvidenceFacts(item, context);
                return new
                {
                    Facts = facts,
                    DisplayTitle = CanonicalWorkDisplayTitle(item, context)
                };
            })
            .Where(x => x.Facts.WorkKey.Length > 0 && x.Facts.GenreKey.Length > 0 && x.DisplayTitle.Length > 0)
            .ToArray();

        return rows
            .GroupBy(x => x.Facts.GenreKey, StringComparer.OrdinalIgnoreCase)
            .Select(genreGroup =>
            {
                var works = genreGroup
                    .GroupBy(x => x.Facts.WorkKey, StringComparer.OrdinalIgnoreCase)
                    .Select(workGroup =>
                    {
                        var label = workGroup
                            .GroupBy(x => x.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key.Length)
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key;
                        var serviceCounts = workGroup
                            .Where(x => x.Facts.ServiceIdentity.IsValid)
                            .GroupBy(x => x.Facts.ServiceIdentity)
                            .ToDictionary(x => x.Key, x => x.Count());
                        return new AIrhythmCanonicalRecordingWorkAggregate(
                            genreGroup.Key,
                            workGroup.Key,
                            label,
                            workGroup.Count(),
                            serviceCounts.Count,
                            serviceCounts);
                    })
                    .OrderByDescending(x => x.RecordingCount)
                    .ThenBy(x => x.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new AIrhythmCanonicalRecordingGenreAggregate(genreGroup.Key, genreGroup.Count(), works);
            })
            .OrderByDescending(x => x.RecordingCount)
            .ThenBy(x => x.GenreKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<AIrhythmCanonicalRecordingTotalAggregate> BuildCanonicalRecordingWorkTotals(
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmEvidenceIdentityContext context)
    {
        // 録画実績・復旧 — Dashboard total ranking is deliberately factual: one successful history row is one
        // recording. Station, genre and episode presentation differences never create extra ranking identities;
        // all rows are first projected through the common canonical Work/title evidence.
        var rows = snapshot.History
            .Select(item => new
            {
                WorkKey = CanonicalWorkKey(item, context),
                DisplayTitle = CanonicalWorkDisplayTitle(item, context)
            })
            .Where(x => x.WorkKey.Length > 0 && x.DisplayTitle.Length > 0)
            .ToArray();

        return rows
            .GroupBy(x => x.WorkKey, StringComparer.OrdinalIgnoreCase)
            .Select(workGroup =>
            {
                var label = workGroup
                    .GroupBy(x => x.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key.Length)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .First().Key;
                return new AIrhythmCanonicalRecordingTotalAggregate(workGroup.Key, label, workGroup.Count());
            })
            .OrderByDescending(x => x.RecordingCount)
            .ThenBy(x => x.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildEvidenceIdentityFingerprint(AIrhythmEvidenceIdentityContext context)
    {
        var numeric = string.Join(";", context.NumericLocalValuesByService
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key}:{string.Join(',', x.Value)}"));
        var aliases = string.Join(";", context.WorkAliases
            .OrderBy(x => x.Key.WorkKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key.ServiceIdentity.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key.WorkKey}|{x.Key.ServiceIdentity}->{x.Value}"));
        var externalTitles = string.Join(";", context.ExternalCanonicalTitles
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key}->{x.Value}"));
        return $"{numeric}#{aliases}#{externalTitles}";
    }

    internal readonly record struct AIrhythmLocalEpisodeIdentity(
        string WorkKey,
        int EpisodeNumber,
        bool IsReplay,
        AIrhythmServiceIdentity ServiceIdentity);

    private readonly record struct AIrhythmLocalContinuitySource(
        AIrhythmLocalEpisodeIdentity Identity,
        string SourceKind,
        DateTimeOffset SourceTime);

    private readonly record struct AIrhythmLocalContinuityEvaluation(
        string WorkKey,
        int EpisodeNumber,
        AIrhythmLocalEvidenceStrength Strength,
        bool DifferentService,
        int EpisodeDistance,
        bool Replay,
        string SourceKind);

    private readonly record struct AIrhythmLocalEvidenceShadowRow(
        string CandidateKey,
        string Work,
        int Episode,
        AIrhythmLocalEvidenceStrength Continuity,
        int RecordingCount,
        int ReservationCount,
        int ActiveDays,
        int ActiveWeeks,
        int LastSeenDays,
        AIrhythmLocalEvidenceStrength Interest,
        AIrhythmLocalEvidenceFamily DominantFamily,
        string ProposedAdjustment,
        bool DifferentService,
        int EpisodeDistance,
        bool Replay,
        string ContinuitySourceKind);

    private static bool IsReplayTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var normalized = title.Normalize(NormalizationForm.FormKC);
        return Regex.IsMatch(normalized, @"(?:\[\s*再\s*\]|【\s*再\s*】)", RegexOptions.IgnoreCase)
            || normalized.Contains("再放送", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("アンコール", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("リピート", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct AIrhythmExplicitEpisodeAnalysis(
        bool HasSingleEpisode,
        int EpisodeNumber,
        bool HasTrailingParenthesizedNumber,
        string ParenthesizedStem,
        int ParenthesizedValue);


    private static bool HasExplicitEpisodeMarkerSignal(string title)
    {
        // 作品識別 — Retain this prefilter exactly aligned with AnalyzeExplicitEpisode's accepted syntax. A generic
        // 'e'/'E' is not sufficient: the canonical regex only accepts episode/ep, while '#'/＃ and 話 cover the
        // other deterministic episode forms. Avoiding false positives here prevents ordinary Latin-script titles
        // from paying FormKC + regex costs.
        if (title.IndexOf('#') >= 0 || title.IndexOf('＃') >= 0 || title.IndexOf('話') >= 0)
            return true;

        for (var i = 0; i + 1 < title.Length; i++)
        {
            var first = title[i];
            if (first is not ('e' or 'E' or 'ｅ' or 'Ｅ'))
                continue;

            var second = title[i + 1];
            if (second is 'p' or 'P' or 'ｐ' or 'Ｐ')
                return true;
        }

        return false;
    }

    private static AIrhythmExplicitEpisodeAnalysis AnalyzeExplicitEpisode(
        string? title,
        bool includeTrailingParenthesizedStructure = false)
    {
        if (string.IsNullOrWhiteSpace(title))
            return default;

        var hasMarkerSignal = HasExplicitEpisodeMarkerSignal(title);
        var mayHaveTrailingParenthesizedNumber = includeTrailingParenthesizedStructure
            && (title.IndexOf('(') >= 0 || title.IndexOf('（') >= 0);
        if (!hasMarkerSignal && !mayHaveTrailingParenthesizedNumber)
            return default;

        // 作品識別 — Episode structure has one canonical parser. Normalize and strip broadcast-only annotations
        // once, then derive explicit episode and trailing-parenthesized structure from the same text. Evidence
        // corroboration remains a separate decision over this parsed structure; it must not reparse the title.
        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC));
        // 作品識別 — Episode numbers in a later announcement belong to a different Work. Apply the same
        // on-air/title-detail boundary as the canonical Work parser.
        if (TrySplitAiredTitleAndDetail(normalized, out var airedTitle, out _))
            normalized = airedTitle;

        var parenthesizedStem = string.Empty;
        var parenthesizedValue = 0;
        var hasTrailingParenthesizedNumber = false;
        if (mayHaveTrailingParenthesizedNumber)
        {
            var parenthesizedMatch = Regex.Match(normalized.Trim(), @"^(.*?)[（(]\s*([0-9]+)\s*[）)]\s*$");
            if (parenthesizedMatch.Success
                && int.TryParse(parenthesizedMatch.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parenthesizedValue))
            {
                parenthesizedStem = Regex.Replace(parenthesizedMatch.Groups[1].Value, @"\s+", " ").Trim();
                hasTrailingParenthesizedNumber = parenthesizedStem.Length >= 2;
            }
        }

        if (!hasMarkerSignal)
            return new AIrhythmExplicitEpisodeAnalysis(false, 0, hasTrailingParenthesizedNumber, parenthesizedStem, parenthesizedValue);

        var mayHaveRange = normalized.IndexOf('-') >= 0
            || normalized.IndexOf('~') >= 0
            || normalized.IndexOf('〜') >= 0
            || normalized.IndexOf('～') >= 0
            || normalized.IndexOf('–') >= 0
            || normalized.IndexOf('—') >= 0;
        if (mayHaveRange && Regex.IsMatch(
                normalized,
                @"(?:[#＃]\s*[0-9]+|(?:episode|ep\.?)\s*[0-9]+|第\s*[0-9]+\s*話|[0-9]+\s*話)\s*[-~〜～–—]\s*(?:(?:[#＃]|(?:episode|ep\.?)|第)\s*)?[0-9]+\s*(?:話)?",
                RegexOptions.IgnoreCase))
        {
            return new AIrhythmExplicitEpisodeAnalysis(false, 0, hasTrailingParenthesizedNumber, parenthesizedStem, parenthesizedValue);
        }

        // 作品識別 — `第N回` is structurally ambiguous: it can mean an episode number, but it is also widely used for
        // event/edition ordinals. Deterministic episode identity is limited here to 第N話 / #N / EP N / N話.
        var match = Regex.Match(
            normalized,
            @"(?:第\s*([0-9]+)\s*話|[#＃]\s*([0-9]+)|(?:episode|ep\.?)\s*([0-9]+)|([0-9]+)\s*話)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return new AIrhythmExplicitEpisodeAnalysis(false, 0, hasTrailingParenthesizedNumber, parenthesizedStem, parenthesizedValue);

        for (var groupIndex = 1; groupIndex < match.Groups.Count; groupIndex++)
        {
            var group = match.Groups[groupIndex];
            if (group.Success
                && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var episodeNumber)
                && episodeNumber > 0)
            {
                return new AIrhythmExplicitEpisodeAnalysis(true, episodeNumber, hasTrailingParenthesizedNumber, parenthesizedStem, parenthesizedValue);
            }
        }

        return new AIrhythmExplicitEpisodeAnalysis(false, 0, hasTrailingParenthesizedNumber, parenthesizedStem, parenthesizedValue);
    }


    private static bool TryGetAliasCorroboratedRepeatedEpisodeNumber(
        string? title,
        AIrhythmServiceIdentity service,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string>? workAliases,
        out string workKey,
        out int episodeNumber)
    {
        workKey = string.Empty;
        episodeNumber = 0;
        if (string.IsNullOrWhiteSpace(title) || !service.IsValid || workAliases is null)
            return false;

        // 作品識別 — Some broadcasters encode an episode number with programme-specific wording instead of a generic
        // #N / 第N話 marker. Never teach those words to the parser. Instead, only after useful user evidence has
        // already established a shorter Work alias on this same service, accept a numeric suffix when the same
        // non-year number is repeated at least twice in the alias remainder. Example shape: <canonical work> 14
        // ... 14. This is deliberately evidence-backed and cannot bootstrap its own alias because alias
        // construction calls the episode parser with workAliases=null.
        var fullKey = LocalWorkIdentityKey(title, preserveTrailingNumericDetail: true);
        if (fullKey.Length < 3)
            return false;

        var canonical = CanonicalizeLocalWorkKey(fullKey, service, workAliases);
        if (canonical.Length < 3
            || canonical.Length >= fullKey.Length
            || !fullKey.StartsWith(canonical, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = fullKey[canonical.Length..];
        var matches = Regex.Matches(remainder, @"[0-9]+");
        if (matches.Count < 2)
            return false;

        int? repeated = null;
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value <= 0
                || value > 999
                || IsLikelyCalendarYear(value))
                return false;

            if (repeated is null)
                repeated = value;
            else if (repeated.Value != value)
                return false;
        }

        if (repeated is null)
            return false;

        workKey = canonical;
        episodeNumber = repeated.Value;
        return true;
    }

    private static bool HasAtLeastTwoAsciiDigitRuns(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var runs = 0;
        var inRun = false;
        foreach (var ch in value)
        {
            var isDigit = ch is >= '0' and <= '9' or >= '０' and <= '９';
            if (isDigit)
            {
                if (!inRun && ++runs >= 2)
                    return true;
                inRun = true;
            }
            else
            {
                inRun = false;
            }
        }

        return false;
    }

    private static bool HasWorkBeforeExplicitEpisodeMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = StripNonIdentityBroadcastAnnotations(value.Normalize(NormalizationForm.FormKC));
        var marker = Regex.Match(
            normalized,
            @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)",
            RegexOptions.IgnoreCase);
        if (!marker.Success) return false;

        var prefix = normalized[..marker.Index].Trim();
        if (prefix.Length == 0) return false;

        // 作品識別 — A remainder such as "第2期 第7話" or "Season 2 EP7" contains an episode marker, but the text before
        // it is only hierarchy metadata. In that shape the leading 【...】 block can still be the work title
        // itself, so never classify it as a broadcast container.
        var withoutHierarchyOnly = Regex.Replace(
            prefix,
            @"(?:第\s*[0-9０-９]+\s*(?:期|シリーズ)|(?:season|series|part)\s*[0-9０-９]+|[0-9０-９]+(?:st|nd|rd|th)\s*season)",
            " ",
            RegexOptions.IgnoreCase);
        return withoutHierarchyOnly.Count(char.IsLetterOrDigit) >= 2;
    }

    private static string LocalWorkIdentityKey(
        string? title,
        bool preserveTrailingNumericDetail = false,
        bool allowLeadingContainerSeparation = false)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        // 作品識別 — ParseLeadingContainerParts normalizes and runs several regexes. A leading-container result is
        // impossible when the raw title contains no Japanese corner bracket at all, so ordinary titles go
        // straight to the canonical title parser.
        if (title.IndexOf('【') < 0)
            return EvidenceSeriesKey(title);

        var containerParts = ParseLeadingContainerParts(title);
        if (string.IsNullOrWhiteSpace(containerParts.Container))
            return EvidenceSeriesKey(title);

        // 作品識別 — A leading 【...】 block is ambiguous: it can be a broadcast container (e.g. a drama slot) or the
        // work title itself. Separate it only when the remainder independently contains a work before an
        // explicit episode marker, or when the caller has already corroborated a parenthesized Work sequence on
        // this exact service.
        var canSeparate = allowLeadingContainerSeparation
            || HasWorkBeforeExplicitEpisodeMarker(containerParts.Remainder);
        if (!canSeparate)
            return EvidenceSeriesKey(title);

        var work = preserveTrailingNumericDetail
            ? containerParts.Remainder
            : !string.IsNullOrWhiteSpace(containerParts.WorkCandidate)
                ? containerParts.WorkCandidate
                : containerParts.Remainder;
        return EvidenceSeriesKey(work);
    }

    private static string LocalWorkKey(
        string? title,
        AIrhythmServiceIdentity service,
        IReadOnlyDictionary<string, int[]> numericLocalValuesByService)
    {
        if (TryParseTrailingParenthesizedNumber(title, out var numericStem, out var numericValue)
            && numericValue > 0)
        {
            if (!IsLikelyCalendarYear(numericValue)
                && IsCorroboratedParenthesizedWorkSequence(title, service, numericLocalValuesByService))
            {
                var numericWorkKey = LocalWorkIdentityKey(numericStem, allowLeadingContainerSeparation: true);
                if (numericWorkKey.Length >= 3)
                    return numericWorkKey;
            }
            // 作品識別 — A year, installment number, or otherwise uncorroborated suffix remains part of local identity.
            // Work-stem collapse must remain corroborated on this exact service.
            return LocalWorkIdentityKey(title, preserveTrailingNumericDetail: true);
        }
        return LocalWorkIdentityKey(title);
    }

    // 作品識別 — Interpret only an Episode that the canonical title parser already split away from Work. This
    // does not decide if an arbitrary number is an episode.
    private static bool TryGetCanonicalQuotedEpisodeNumber(string episode, out int number)
    {
        number = 0;
        if (episode.Length < 3)
            return false;
        var digits = episode[0] == '(' && episode[^1] == ')'
            ? episode.AsSpan(1, episode.Length - 2)
            : episode.StartsWith("第", StringComparison.Ordinal) && episode.EndsWith("夜", StringComparison.Ordinal)
                ? episode.AsSpan(1, episode.Length - 2)
                : ReadOnlySpan<char>.Empty;
        return digits.Length is >= 1 and <= 3
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number > 0;
    }

    private static bool TryBuildLocalEpisodeIdentity(
        string? title,
        AIrhythmServiceIdentity service,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string>? workAliases,
        IReadOnlyDictionary<string, int[]>? numericEvidenceValuesByService,
        out AIrhythmLocalEpisodeIdentity identity)
    {
        var explicitEpisodeAnalysis = AnalyzeExplicitEpisode(
            title,
            includeTrailingParenthesizedStructure: numericEvidenceValuesByService is not null);
        var parenthesizedEpisodeCorroborated = !explicitEpisodeAnalysis.HasSingleEpisode
            && IsEvidenceCorroboratedParenthesizedEpisode(
                explicitEpisodeAnalysis, service, numericEvidenceValuesByService);
        return TryBuildLocalEpisodeIdentity(
            title,
            service,
            workAliases,
            explicitEpisodeAnalysis,
            parenthesizedEpisodeCorroborated,
            out identity);
    }

    private static bool TryBuildLocalEpisodeIdentity(
        string? title,
        AIrhythmServiceIdentity service,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string>? workAliases,
        AIrhythmExplicitEpisodeAnalysis explicitEpisodeAnalysis,
        bool parenthesizedEpisodeCorroborated,
        out AIrhythmLocalEpisodeIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var isReplay = IsReplayTitle(title);
        if (explicitEpisodeAnalysis.HasSingleEpisode)
        {
            var explicitEpisode = explicitEpisodeAnalysis.EpisodeNumber;
            var workKey = CanonicalizeLocalWorkKey(LocalWorkIdentityKey(title), service, workAliases);
            if (workKey.Length < 3)
                return false;

            identity = new AIrhythmLocalEpisodeIdentity(
                workKey, explicitEpisode, isReplay, service);
            return true;
        }

        // 作品識別 — The canonical title parser owns structurally explicit quoted-subtitle episodes, including a
        // typed (N) and a "第N夜" installment. Consume its Work/Episode projection for continuity, instead of
        // inferring another episode or Work boundary in the learner.
        if (title is not null && (title.IndexOf('「') >= 0 || title.IndexOf('『') >= 0))
        {
            var parsed = ParseCanonicalTitleParts(title);
            if (TryGetCanonicalQuotedEpisodeNumber(parsed.Episode, out var quotedEpisode))
            {
                var quotedWorkKey = CanonicalizeLocalWorkKey(
                    CompactIdentity(parsed.Work), service, workAliases);
                if (quotedWorkKey.Length >= 3)
                {
                    identity = new AIrhythmLocalEpisodeIdentity(
                        quotedWorkKey, quotedEpisode, isReplay, service);
                    return true;
                }
            }
        }

        if (TryGetAliasCorroboratedRepeatedEpisodeNumber(
                title, service, workAliases, out var corroboratedWorkKey, out var corroboratedEpisode))
        {
            identity = new AIrhythmLocalEpisodeIdentity(
                corroboratedWorkKey, corroboratedEpisode, isReplay, service);
            return true;
        }

        // 録画実績・復旧 — A trailing parenthesized number is not an episode by EPG shape alone. Promote it only when
        // useful recording/reservation evidence on this exact service already contains a neighboring number for
        // the same stable stem. This restores genuine episode sequences such as (9)/(10)/(11) without
        // reclassifying split sports broadcasts that have no user evidence.
        if (parenthesizedEpisodeCorroborated)
        {
            var numericWorkKey = CanonicalizeLocalWorkKey(
                LocalWorkIdentityKey(explicitEpisodeAnalysis.ParenthesizedStem, allowLeadingContainerSeparation: true),
                service,
                workAliases);
            if (numericWorkKey.Length >= 3)
            {
                identity = new AIrhythmLocalEpisodeIdentity(
                    numericWorkKey, explicitEpisodeAnalysis.ParenthesizedValue, isReplay, service);
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildLocalEpisodeIdentityForContinuityCached(
        string? title,
        AIrhythmServiceIdentity service,
        AIrhythmEvidenceIdentityContext context,
        out AIrhythmLocalEpisodeIdentity identity)
    {
        var source = title ?? string.Empty;
        var cacheKey = new AIrhythmLocalEpisodeCacheKey(source, service);
        if (context.LocalEpisodeIdentities.TryGetValue(cacheKey, out var cached))
        {
            context.LocalEpisodeIdentityCacheHits++;
            identity = cached.Identity;
            return cached.HasIdentity;
        }

        // 推薦・学習 — Score comparisons use the shared provenance boundary.
        var hasQuotedStructuralEpisodeCandidate = (source.IndexOf('「') >= 0 || source.IndexOf('『') >= 0)
            && (source.IndexOf('(') >= 0 || source.IndexOf('（') >= 0
                || (source.IndexOf('第') >= 0 && source.IndexOf('夜') >= 0));
        var mayNeedEpisodeAnalysis = hasQuotedStructuralEpisodeCandidate || source.IndexOf('#') >= 0
            || source.IndexOf('＃') >= 0
            || source.IndexOf('話') >= 0
            || source.IndexOf('回') >= 0
            || source.IndexOf('(') >= 0
            || source.IndexOf('（') >= 0
            || source.Contains("ep", StringComparison.OrdinalIgnoreCase)
            || HasAtLeastTwoAsciiDigitRuns(source);
        if (!mayNeedEpisodeAnalysis)
        {
            identity = default;
            context.LocalEpisodeIdentities[cacheKey] = new AIrhythmLocalEpisodeCacheValue(false, identity);
            context.LocalEpisodeIdentityCacheMisses++;
            return false;
        }

        // 保存・読込 — Only titles with an episode-shaped signal pay the full parser cost. Cache the negative result
        // as well so repeated EPG rows avoid both the prefilter and parser on later hits.
        var explicitEpisodeAnalysis = AnalyzeExplicitEpisode(source, includeTrailingParenthesizedStructure: true);
        var mayHaveAliasCorroboratedRepeatedEpisode = !explicitEpisodeAnalysis.HasSingleEpisode
            && HasAtLeastTwoAsciiDigitRuns(source);
        var mayHaveEvidenceCorroboratedParenthesizedEpisode = !explicitEpisodeAnalysis.HasSingleEpisode
            && IsEvidenceCorroboratedParenthesizedEpisode(
                explicitEpisodeAnalysis, service, context.NumericEvidenceValuesByService);
        if (!explicitEpisodeAnalysis.HasSingleEpisode
            && !mayHaveAliasCorroboratedRepeatedEpisode
            && !mayHaveEvidenceCorroboratedParenthesizedEpisode
            && !hasQuotedStructuralEpisodeCandidate)
        {
            identity = default;
            context.LocalEpisodeIdentities[cacheKey] = new AIrhythmLocalEpisodeCacheValue(false, identity);
            context.LocalEpisodeIdentityCacheMisses++;
            return false;
        }

        var hasIdentity = TryBuildLocalEpisodeIdentity(
            source,
            service,
            context.WorkAliases,
            explicitEpisodeAnalysis,
            mayHaveEvidenceCorroboratedParenthesizedEpisode,
            out identity);
        context.LocalEpisodeIdentities[cacheKey] = new AIrhythmLocalEpisodeCacheValue(hasIdentity, identity);
        context.LocalEpisodeIdentityCacheMisses++;
        return hasIdentity;
    }


    private static string CanonicalizeLocalWorkKey(
        string workKey,
        AIrhythmServiceIdentity service,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string>? workAliases)
    {
        if (workKey.Length == 0 || !service.IsValid || workAliases is null)
            return workKey;

        var current = workKey;
        while (workAliases.TryGetValue(new AIrhythmLocalWorkAliasKey(current, service), out var canonical)
            && canonical.Length >= 3
            && canonical.Length < current.Length)
        {
            current = canonical;
        }
        return current;
    }

    private static bool TryGetRecurringEvidencePrefix(string? title, out string prefixKey, out bool strongBoundary)
    {
        prefixKey = string.Empty;
        strongBoundary = false;
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC)).Trim();
        if (normalized.Length == 0)
            return false;

        // 作品識別 — TvAIr-managed weekday suffix is not programme identity.
        normalized = Regex.Replace(normalized, @"\s*[（(]\s*(?:月|火|水|木|金|土|日)\s*[）)]\s*$", " ").Trim();

        // 作品識別 — Explicit episode markers are already handled by Local Episode Identity. Never create a second
        // title-shortening path for those rows.
        if (Regex.IsMatch(
                normalized,
                @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)",
                RegexOptions.IgnoreCase))
            return false;

        var boundary = -1;
        foreach (var marker in new[] { '！', '!', '？', '?', '▼', '▽' })
        {
            var index = normalized.IndexOf(marker);
            if (index > 0 && (boundary < 0 || index < boundary))
            {
                boundary = index;
                strongBoundary = true;
            }
        }

        var whitespace = Regex.Match(normalized, @"[\s　]+", RegexOptions.None);
        if (whitespace.Success && whitespace.Index > 0 && (boundary < 0 || whitespace.Index < boundary))
        {
            boundary = whitespace.Index;
            strongBoundary = false;
        }

        if (boundary <= 0)
            return false;

        var prefix = normalized[..boundary].Trim().TrimEnd('～', '~', '・', '：', ':', '-', '－');
        var remainder = normalized[boundary..].Trim().TrimStart('！', '!', '？', '?', '▼', '▽', '～', '~', '・', '：', ':', '-', '－');
        if (prefix.Count(char.IsLetterOrDigit) < 5 || remainder.Count(char.IsLetterOrDigit) < 3)
            return false;

        prefixKey = EvidenceSeriesKey(prefix);
        var fullKey = EvidenceSeriesKey(normalized);
        if (prefixKey.Length < 5 || fullKey.Length - prefixKey.Length < 3)
        {
            prefixKey = string.Empty;
            strongBoundary = false;
            return false;
        }
        return true;
    }

    private static string LongestStableEvidencePrefix(IReadOnlyCollection<string> fullKeys, string initialPrefix)
    {
        if (fullKeys.Count < 2 || string.IsNullOrWhiteSpace(initialPrefix))
            return string.Empty;

        var ordered = fullKeys.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (ordered.Length < 2)
            return string.Empty;

        var common = ordered[0];
        for (var i = 1; i < ordered.Length && common.Length > 0; i++)
        {
            var other = ordered[i];
            var length = Math.Min(common.Length, other.Length);
            var j = 0;
            while (j < length && char.ToUpperInvariant(common[j]) == char.ToUpperInvariant(other[j]))
                j++;
            common = common[..j];
        }

        common = Regex.Replace(common, @"[0-9０-９]+$", string.Empty).Trim();

        // 作品識別 — A longest-common-prefix cut can stop immediately after the Japanese ordinal introducer when
        // every evidence-backed variant continues with a different number, e.g. "...第1部..." / "...第2部...".  A
        // bare terminal "第" is not work identity; remove it only when all observed full keys prove that the next
        // character is numeric.  This keeps the rule evidence-backed and generic instead of introducing a
        // title-specific exception.
        if (common.EndsWith("第", StringComparison.Ordinal)
            && ordered.All(fullKey => fullKey.Length > common.Length && char.IsDigit(fullKey[common.Length])))
        {
            common = common[..^1].TrimEnd();
        }

        // 作品識別 — A stable text prefix must never end inside a numbered chapter/episode marker. Kanji numerals
        // have shared leading characters (第二十章 / 第二十一章), so a character-level LCP can falsely learn "第二十" as
        // part of the programme identity. Accept the boundary only when every observed variant has a *complete*
        // ordinal followed by its explicit episode/chapter unit, not a series/edition label.
        var partialOrdinal = Regex.Match(common, @"第[一二三四五六七八九十百千零〇0-9]+$");
        if (partialOrdinal.Success
            && ordered.All(fullKey =>
                fullKey.Length > partialOrdinal.Index
                && Regex.IsMatch(fullKey[partialOrdinal.Index..],
                    @"^第[一二三四五六七八九十百千零〇0-9]+(?:章|話)(?=$|[^一二三四五六七八九十百千零〇0-9])")))
        {
            common = common[..partialOrdinal.Index].TrimEnd();
        }

        if (common.Length < initialPrefix.Length)
            return string.Empty;

        // 作品識別 — Never collapse to a bare scheduling/container prefix when the evidence-backed variants share a
        // longer stable programme identity.  This keeps e.g. a broadcast slot plus an actual work title intact,
        // without any title-specific deny list.
        return common.Length >= initialPrefix.Length + 4 ? common : initialPrefix;
    }

    private static IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string> BuildLocalWorkAliases(
        AIrhythmRuntimeSnapshot snapshot,
        IReadOnlyDictionary<string, int[]> numericLocalValuesByService)
    {
        var episodesByWorkAndService = new Dictionary<AIrhythmLocalWorkAliasKey, HashSet<int>>();

        void Observe(string? title, AIrhythmServiceIdentity service)
        {
            if (!TryBuildLocalEpisodeIdentity(
                    title,
                    service,
                    workAliases: null,
                    numericEvidenceValuesByService: null,
                    out var identity))
                return;

            if (!identity.ServiceIdentity.IsValid)
                return;

            var key = new AIrhythmLocalWorkAliasKey(identity.WorkKey, identity.ServiceIdentity);
            if (!episodesByWorkAndService.TryGetValue(key, out var episodes))
            {
                episodes = new HashSet<int>();
                episodesByWorkAndService[key] = episodes;
            }
            episodes.Add(identity.EpisodeNumber);
        }

        // 作品識別 — Alias candidates come only from useful user evidence. Parenthesized-number Episode
        // corroboration is intentionally not used while aliases are being learned, preventing a candidate from
        // bootstrapping its own Work alias.
        foreach (var item in snapshot.History)
        {
            if (AIrhythmDataState.IsUsefulHistory(item))
                Observe(item.ProgramTitle, ServiceIdentityOf(item));
        }
        foreach (var item in snapshot.Reservations)
        {
            if (AIrhythmDataState.IsUsefulReservation(item) && ReservationEvidenceWeight(item) > 0)
                Observe(item.ProgramTitle, ServiceIdentityOf(item));
        }

        var keys = episodesByWorkAndService.Keys
            .Where(key => key.WorkKey.Length >= 3 && key.ServiceIdentity.IsValid)
            .OrderByDescending(key => key.WorkKey.Length)
            .ToArray();
        var aliases = new Dictionary<AIrhythmLocalWorkAliasKey, string>();

        foreach (var longer in keys)
        {
            string? best = null;
            foreach (var shorter in keys)
            {
                if (shorter.WorkKey.Length >= longer.WorkKey.Length
                    || longer.WorkKey.Length - shorter.WorkKey.Length < 2)
                    continue;
                if (!longer.WorkKey.EndsWith(shorter.WorkKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 作品識別 — Never strip a leading phrase merely because the remaining title is a suffix. Require both forms
                // to be backed by useful user evidence and to carry neighboring episode numbers. Work identity itself
                // may cross services; service difference is handled later by Continuity confidence instead of splitting
                // the work identity.
                var hasNeighboringEpisode = episodesByWorkAndService[longer].Any(longEpisode =>
                    episodesByWorkAndService[shorter].Any(shortEpisode => Math.Abs(longEpisode - shortEpisode) <= 2));
                if (!hasNeighboringEpisode)
                    continue;

                if (best is null || shorter.WorkKey.Length > best.Length)
                    best = shorter.WorkKey;
            }

            if (!string.IsNullOrWhiteSpace(best))
                aliases[longer] = best;
        }

        // 推薦・学習 — For non-episodic recurring programmes, learn only from useful user evidence. The same service
        // must contain at least two distinct evidence-backed title variants that share a stable prefix. EPG
        // repetition alone can never create this alias.
        var recurringPrefixGroups = new Dictionary<(AIrhythmServiceIdentity Service, string Prefix, bool StrongBoundary), HashSet<string>>();
        var exactEvidenceKeysByService = new Dictionary<AIrhythmServiceIdentity, HashSet<string>>();

        void ObserveRecurringPrefix(string? title, AIrhythmServiceIdentity service)
        {
            if (!service.IsValid || string.IsNullOrWhiteSpace(title))
                return;

            var fullKey = EvidenceSeriesKey(title);
            if (fullKey.Length >= 5)
            {
                if (!exactEvidenceKeysByService.TryGetValue(service, out var exactKeys))
                {
                    exactKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    exactEvidenceKeysByService[service] = exactKeys;
                }
                exactKeys.Add(fullKey);
            }

            if (!TryGetRecurringEvidencePrefix(title, out var prefixKey, out var strongBoundary)
                || fullKey.Length <= prefixKey.Length)
                return;

            var groupKey = (service, prefixKey, strongBoundary);
            if (!recurringPrefixGroups.TryGetValue(groupKey, out var fullKeys))
            {
                fullKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                recurringPrefixGroups[groupKey] = fullKeys;
            }
            fullKeys.Add(fullKey);
        }

        foreach (var item in snapshot.History)
        {
            if (AIrhythmDataState.IsUsefulHistory(item))
                ObserveRecurringPrefix(item.ProgramTitle, ServiceIdentityOf(item));
        }
        foreach (var item in snapshot.Reservations)
        {
            if (AIrhythmDataState.IsUsefulReservation(item) && ReservationEvidenceWeight(item) > 0)
                ObserveRecurringPrefix(item.ProgramTitle, ServiceIdentityOf(item));
        }

        foreach (var pair in recurringPrefixGroups)
        {
            var exactShortTitleCorroborated =
                exactEvidenceKeysByService.TryGetValue(pair.Key.Service, out var exactKeys)
                && exactKeys.Contains(pair.Key.Prefix);
            if (pair.Value.Count < 2 && !exactShortTitleCorroborated)
                continue;

            // 作品識別 — An exact useful-evidence title on the same service is the strongest local proof that the same
            // text is a complete programme identity instead of only a scheduling prefix. This lets one longer
            // evidence-backed topic/subtitle variant collapse to that exact Work without requiring a second long
            // variant. Otherwise retain the established multi-variant evidence rule and its conservative
            // whitespace-prefix handling.
            var stablePrefix = exactShortTitleCorroborated
                ? pair.Key.Prefix
                : pair.Key.StrongBoundary
                    ? pair.Key.Prefix
                    : LongestStableEvidencePrefix(pair.Value, pair.Key.Prefix);
            if (stablePrefix.Length < pair.Key.Prefix.Length)
                continue;

            foreach (var fullKey in pair.Value)
            {
                if (fullKey.Length <= stablePrefix.Length)
                    continue;
                var aliasKey = new AIrhythmLocalWorkAliasKey(fullKey, pair.Key.Service);
                if (!aliases.ContainsKey(aliasKey))
                    aliases[aliasKey] = stablePrefix;
            }
        }

        // 作品識別 — A numbered episode can carry an unquoted story subtitle before its #N marker. A different
        // episode on the SAME service broadcasting the bare series name establishes the Work boundary without
        // any title-specific dictionary. Neither one-off EPG text nor mere string-prefix similarity is
        // sufficient.
        var numberedWorkEpisodes = new Dictionary<AIrhythmLocalWorkAliasKey, HashSet<int>>();
        var numberedWorkText = new Dictionary<AIrhythmLocalWorkAliasKey, string>();

        void ObserveNumberedEpisode(string? title, AIrhythmServiceIdentity service)
        {
            if (!service.IsValid || string.IsNullOrWhiteSpace(title)
                || (title.IndexOf('#') < 0 && title.IndexOf('＃') < 0)
                || ClassifyDerivedProgramRelation(title) == AIrhythmDerivedProgramRelation.Promo)
                return;
            var parts = ParseCanonicalTitleParts(title);
            var marker = Regex.Match(parts.Episode, @"^#\s*(?<number>[0-9]{1,3})$",
                RegexOptions.CultureInvariant);
            if (!marker.Success || !int.TryParse(marker.Groups["number"].Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out var episodeNumber)
                || episodeNumber <= 0 || episodeNumber > 999)
                return;
            var rawWork = NormalizeCanonicalWhitespace(parts.Work).Trim();
            if (rawWork.Count(char.IsLetterOrDigit) < 4) return;
            var workKey = CompactIdentity(rawWork);
            if (workKey.Length < 4) return;
            var key = new AIrhythmLocalWorkAliasKey(workKey, service);
            if (!numberedWorkEpisodes.TryGetValue(key, out var episodes))
                numberedWorkEpisodes[key] = episodes = new HashSet<int>();
            episodes.Add(episodeNumber);
            numberedWorkText.TryAdd(key, rawWork);
        }

        foreach (var item in snapshot.Events)
            ObserveNumberedEpisode(item.Title, ServiceIdentityOf(item));
        foreach (var item in snapshot.History)
            if (AIrhythmDataState.IsUsefulHistory(item))
                ObserveNumberedEpisode(item.ProgramTitle, ServiceIdentityOf(item));
        foreach (var item in snapshot.Reservations)
            if (AIrhythmDataState.IsUsefulReservation(item) && ReservationEvidenceWeight(item) > 0)
                ObserveNumberedEpisode(item.ProgramTitle, ServiceIdentityOf(item));

        foreach (var (longKey, longEpisodes) in numberedWorkEpisodes)
        {
            if (aliases.ContainsKey(longKey)) continue;
            var fullTitle = numberedWorkText[longKey];
            string? matchedShortKey = null;
            var ambiguous = false;
            for (var space = fullTitle.IndexOf(' '); space >= 0;
                 space = fullTitle.IndexOf(' ', space + 1))
            {
                var baseTitle = fullTitle[..space];
                var trailingText = fullTitle[(space + 1)..];
                if (baseTitle.Count(char.IsLetterOrDigit) < 4
                    || trailingText.Count(char.IsLetterOrDigit) < 8)
                    continue;
                var shortKey = CompactIdentity(baseTitle);
                if (shortKey.Length < 4 || shortKey.Length >= longKey.WorkKey.Length) continue;
                var shortAliasKey = new AIrhythmLocalWorkAliasKey(shortKey, longKey.ServiceIdentity);
                if (!numberedWorkEpisodes.TryGetValue(shortAliasKey, out var shortEpisodes)
                    || !shortEpisodes.Any(shortEpisode => longEpisodes.Any(longEpisode =>
                        longEpisode != shortEpisode && Math.Abs(longEpisode - shortEpisode) <= 2)))
                    continue;
                if (matchedShortKey is not null && !string.Equals(matchedShortKey, shortKey, StringComparison.OrdinalIgnoreCase))
                {
                    ambiguous = true;
                    break;
                }
                matchedShortKey = shortKey;
            }
            if (!ambiguous && matchedShortKey is not null)
                aliases[longKey] = matchedShortKey;
        }

        return aliases;
    }

    // 録画実績・復旧 — One scoring snapshot uses one observation of keyword-rule provenance and reservation
    // history. Never rebuild these indices separately for local continuity, base score and learned
    // preference: all projections must see the same matching source record.
    private readonly record struct AIrhythmScoringRuleIndices(
        IReadOnlyDictionary<int, TvAirKeywordRuleDto> EnabledById,
        IReadOnlyDictionary<int, TvAirKeywordRuleDto> SearchCastById);

    private static AIrhythmScoringRuleIndices BuildScoringRuleIndices(
        IReadOnlyList<TvAirKeywordRuleDto> rules)
    {
        var enabled = new Dictionary<int, TvAirKeywordRuleDto>();
        var searchCast = new Dictionary<int, TvAirKeywordRuleDto>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            // 推薦・学習 — The first enabled occurrence wins for base evidence; the first enabled SearchCast occurrence
            // independently wins for cast intent. Those can differ when duplicate rule IDs have different SearchCast
            // flags.
            enabled.TryAdd(rule.RuleId, rule);
            if (rule.SearchCast)
                searchCast.TryAdd(rule.RuleId, rule);
        }
        return new(enabled, searchCast);
    }

    private static Dictionary<string, TvAirReservationDto> BuildLatestReservationIndex(
        IReadOnlyList<TvAirReservationDto> records)
    {
        var index = new Dictionary<string, TvAirReservationDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var reservation in records)
        {
            if (string.IsNullOrWhiteSpace(reservation.ReservationId))
                continue;
            // 処理条件 — Strictly greater preserves the first row on equal timestamps, matching stable
            // OrderByDescending(...).First() within each case-insensitive group.
            if (!index.TryGetValue(reservation.ReservationId, out var current)
                || reservation.UpdatedAt > current.UpdatedAt)
                index[reservation.ReservationId] = reservation;
        }
        return index;
    }

    private readonly record struct AIrhythmLocalEvidenceState(
        IReadOnlyDictionary<string, AIrhythmLocalWorkAggregate> WorkAggregates,
        IReadOnlyDictionary<string, AIrhythmLocalContinuitySource[]> ContinuitySources);

    private static AIrhythmLocalEvidenceState BuildLocalEvidenceState(
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmEvidenceIdentityContext identityContext,
        IReadOnlyDictionary<int, TvAirKeywordRuleDto> rulesById,
        IReadOnlyDictionary<string, TvAirReservationDto> reservationById)
    {
        var aggregates = new Dictionary<string, AIrhythmLocalWorkAggregate>(StringComparer.OrdinalIgnoreCase);
        var continuitySources = new Dictionary<string, List<AIrhythmLocalContinuitySource>>(StringComparer.OrdinalIgnoreCase);
        var seenCausalWorkEvidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AIrhythmLocalWorkAggregate GetOrCreateAggregate(string key)
        {
            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                aggregate = new AIrhythmLocalWorkAggregate();
                aggregates[key] = aggregate;
            }
            return aggregate;
        }

        void AddContinuitySource(AIrhythmLocalEpisodeIdentity identity, string sourceKind, DateTimeOffset sourceTime)
        {
            if (!continuitySources.TryGetValue(identity.WorkKey, out var list))
            {
                list = new List<AIrhythmLocalContinuitySource>();
                continuitySources[identity.WorkKey] = list;
            }
            list.Add(new AIrhythmLocalContinuitySource(identity, sourceKind, sourceTime));
        }

        void AddEvidence(
            string? title,
            AIrhythmServiceIdentity service,
            DateTimeOffset when,
            AIrhythmCanonicalEvidenceFacts canonicalFacts,
            bool isRecording,
            bool isAutomatedReservation,
            bool allowWorkEvidence,
            string causalKey,
            string sourceKind)
        {
            var hasEpisodeSource = TryBuildLocalEpisodeIdentityForContinuityCached(
                title,
                service,
                identityContext,
                out var sourceIdentity);
            var workKey = hasEpisodeSource
                ? sourceIdentity.WorkKey
                : !canonicalFacts.UsesExternalCanonicalTitle
                    ? canonicalFacts.WorkKey
                    : CanonicalizeLocalWorkKey(
                        LocalWorkKeyCached(title, service, identityContext),
                        service,
                        identityContext.WorkAliases);

            if (!allowWorkEvidence || workKey.Length < 3)
                return;

            var firstCausalObservation = seenCausalWorkEvidence.Add(causalKey);
            if (firstCausalObservation)
            {
                var aggregate = GetOrCreateAggregate(workKey);
                if (isRecording)
                    aggregate.RecordingCount++;
                else
                {
                    aggregate.ReservationCount++;
                    if (!isAutomatedReservation)
                        aggregate.HasNonAutomatedReservation = true;
                }
                aggregate.AddActivity(when);
            }

            // 作品識別 — Continuity is an episode relation, not another preference vote. Retain all episode positions
            // for an axis that legitimately owns Work evidence, while counting the originating rule/chain only once
            // in the work aggregate above.
            if (hasEpisodeSource)
                AddContinuitySource(sourceIdentity, sourceKind, when);
        }

        foreach (var item in snapshot.History)
        {
            if (!AIrhythmDataState.IsUsefulHistory(item))
                continue;
            var facts = CanonicalEvidenceFacts(item, identityContext);
            var sourceReservation = FindOriginatingReservation(item, reservationById);
            var axes = BehaviorEvidenceAxesForRecording(item, reservationById, rulesById);
            var automated = sourceReservation is not null
                ? sourceReservation.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule
                : AIrhythmDataState.TryGetRecordedPreferenceOrigin(item, out var persistedIntent, out _)
                    && persistedIntent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule;
            var causalKey = AIrhythmDataState.RecordingEvidenceCausalIdentity(item);
            AddEvidence(
                item.ProgramTitle,
                facts.ServiceIdentity,
                item.ActualStart ?? item.Start,
                facts,
                isRecording: true,
                isAutomatedReservation: automated,
                allowWorkEvidence: axes.Work,
                causalKey: causalKey,
                sourceKind: automated ? "automated_recording" : "recording");
        }

        foreach (var item in snapshot.Reservations)
        {
            if (!AIrhythmDataState.IsUsefulReservation(item) || ReservationEvidenceWeight(item) <= 0)
                continue;
            var isAutomated = item.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule;
            var facts = CanonicalEvidenceFacts(item, identityContext);
            var axes = BehaviorEvidenceAxesForReservation(item, rulesById);
            var causalKey = AIrhythmDataState.ReservationEvidenceCausalIdentity(item);
            AddEvidence(
                item.ProgramTitle,
                facts.ServiceIdentity,
                item.Start,
                facts,
                isRecording: false,
                isAutomatedReservation: isAutomated,
                allowWorkEvidence: axes.Work,
                causalKey: causalKey,
                sourceKind: isAutomated ? "automated_reservation" : "reservation");
        }

        return new AIrhythmLocalEvidenceState(
            aggregates,
            continuitySources.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    private static AIrhythmLocalEvidenceStrength LowerLocalEvidenceStrength(AIrhythmLocalEvidenceStrength strength)
        => strength switch
        {
            AIrhythmLocalEvidenceStrength.Strong => AIrhythmLocalEvidenceStrength.Moderate,
            AIrhythmLocalEvidenceStrength.Moderate => AIrhythmLocalEvidenceStrength.Weak,
            AIrhythmLocalEvidenceStrength.Weak => AIrhythmLocalEvidenceStrength.None,
            _ => AIrhythmLocalEvidenceStrength.None
        };

    private static AIrhythmLocalEvidenceStrength ContinuityStrengthForDistance(int distance)
        => distance switch
        {
            1 => AIrhythmLocalEvidenceStrength.Strong,
            2 => AIrhythmLocalEvidenceStrength.Moderate,
            _ => AIrhythmLocalEvidenceStrength.None
        };

    private static AIrhythmLocalContinuityEvaluation EvaluateLocalContinuity(
        TvAirProgramEventDto candidate,
        IReadOnlyDictionary<string, int[]> numericLocalValuesByService,
        IReadOnlyDictionary<string, AIrhythmLocalContinuitySource[]> continuitySources,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string> localWorkAliases,
        IReadOnlyDictionary<string, int[]> numericEvidenceValuesByService)
    {
        var replay = IsReplayTitle(candidate.Title);
        var service = ServiceIdentityOf(candidate);
        var hasIdentity = TryBuildLocalEpisodeIdentity(
            candidate.Title,
            service,
            localWorkAliases,
            numericEvidenceValuesByService,
            out var candidateIdentity);
        var workKey = hasIdentity
            ? candidateIdentity.WorkKey
            : CanonicalizeLocalWorkKey(
                LocalWorkKey(candidate.Title, service, numericLocalValuesByService),
                service,
                localWorkAliases);

        if (replay || !hasIdentity)
            return new(workKey, hasIdentity ? candidateIdentity.EpisodeNumber : 0, AIrhythmLocalEvidenceStrength.None, false, 0, replay, string.Empty);

        var bestStrength = AIrhythmLocalEvidenceStrength.None;
        var bestDifferentService = false;
        var bestDistance = 0;
        var bestSourceKind = string.Empty;

        if (continuitySources.TryGetValue(candidateIdentity.WorkKey, out var sources))
        {
            foreach (var source in sources)
            {
                if (source.Identity.IsReplay || source.SourceTime >= candidate.Start)
                    continue;

                var distance = candidateIdentity.EpisodeNumber - source.Identity.EpisodeNumber;
                var sourceStrength = ContinuityStrengthForDistance(distance);
                if (sourceStrength == AIrhythmLocalEvidenceStrength.None)
                    continue;

                var sameService = candidateIdentity.ServiceIdentity.IsValid
                    && source.Identity.ServiceIdentity.IsValid
                    && candidateIdentity.ServiceIdentity.Equals(source.Identity.ServiceIdentity);
                if (!sameService)
                    sourceStrength = LowerLocalEvidenceStrength(sourceStrength);
                if ((int)sourceStrength <= (int)bestStrength)
                    continue;

                bestStrength = sourceStrength;
                bestDifferentService = !sameService;
                bestDistance = distance;
                bestSourceKind = source.SourceKind;
            }
        }

        return new(workKey, candidateIdentity.EpisodeNumber, bestStrength, bestDifferentService, bestDistance, replay, bestSourceKind);
    }

    private static AIrhythmLocalContinuityEvaluation EvaluateLocalContinuityFromCanonicalFacts(
        TvAirProgramEventDto candidate,
        IReadOnlyDictionary<string, AIrhythmLocalContinuitySource[]> continuitySources,
        AIrhythmEvidenceIdentityContext identityContext,
        AIrhythmCanonicalEvidenceFacts facts)
    {
        // 外部情報 — External canonical-title substitution can intentionally differ from the local continuity
        // identity, so those candidates use the full continuity evaluation path.
        if (facts.UsesExternalCanonicalTitle)
        {
            return EvaluateLocalContinuity(
                candidate,
                identityContext.NumericLocalValuesByService,
                continuitySources,
                identityContext.WorkAliases,
                identityContext.NumericEvidenceValuesByService);
        }

        // 保存・読込 — Episode parsing is invariant for the same title + service inside one immutable snapshot. Cache
        // both positive and negative parse results so repeated EPG rows never rerun the same normalization /
        // regex pipeline. Continuity itself remains time-sensitive and is evaluated below for every candidate
        // against source timestamps.
        if (!TryBuildLocalEpisodeIdentityForContinuityCached(
                candidate.Title,
                facts.ServiceIdentity,
                identityContext,
                out var candidateIdentity))
        {
            return new(facts.WorkKey, 0, AIrhythmLocalEvidenceStrength.None, false, 0, facts.IsReplay, string.Empty);
        }

        if (facts.IsReplay)
            return new(candidateIdentity.WorkKey, candidateIdentity.EpisodeNumber, AIrhythmLocalEvidenceStrength.None, false, 0, true, string.Empty);

        var bestStrength = AIrhythmLocalEvidenceStrength.None;
        var bestDifferentService = false;
        var bestDistance = 0;
        var bestSourceKind = string.Empty;

        if (continuitySources.TryGetValue(candidateIdentity.WorkKey, out var sources))
        {
            foreach (var source in sources)
            {
                if (source.Identity.IsReplay || source.SourceTime >= candidate.Start)
                    continue;

                var distance = candidateIdentity.EpisodeNumber - source.Identity.EpisodeNumber;
                var sourceStrength = ContinuityStrengthForDistance(distance);
                if (sourceStrength == AIrhythmLocalEvidenceStrength.None)
                    continue;

                var sameService = candidateIdentity.ServiceIdentity.IsValid
                    && source.Identity.ServiceIdentity.IsValid
                    && candidateIdentity.ServiceIdentity.Equals(source.Identity.ServiceIdentity);
                if (!sameService)
                    sourceStrength = LowerLocalEvidenceStrength(sourceStrength);
                if ((int)sourceStrength <= (int)bestStrength)
                    continue;

                bestStrength = sourceStrength;
                bestDifferentService = !sameService;
                bestDistance = distance;
                bestSourceKind = source.SourceKind;
            }
        }

        return new(candidateIdentity.WorkKey, candidateIdentity.EpisodeNumber, bestStrength, bestDifferentService, bestDistance, false, bestSourceKind);
    }

    private static double LocalContinuityAdjustmentValue(AIrhythmLocalEvidenceStrength strength)
        => strength switch
        {
            AIrhythmLocalEvidenceStrength.Strong => 0.50d,
            AIrhythmLocalEvidenceStrength.Moderate => 0.25d,
            _ => 0.0d
        };

    private static double LocalInterestAdjustmentValue(AIrhythmLocalEvidenceStrength strength)
        => strength switch
        {
            AIrhythmLocalEvidenceStrength.Strong => 0.25d,
            AIrhythmLocalEvidenceStrength.Moderate => 0.125d,
            // 録画実績・復旧 — Weak means only one useful recording/reservation occurrence. Retain it as evidence, but a
            // single occurrence does not move the product ranking.
            _ => 0.0d
        };

    private static AIrhythmLocalEvidenceStrength EvaluateInterestStrength(AIrhythmLocalWorkAggregate? aggregate)
    {
        if (aggregate is null || aggregate.RecordingCount + aggregate.ReservationCount <= 0)
            return AIrhythmLocalEvidenceStrength.None;

        var activeDays = aggregate.ActiveDays.Count;
        var activeWeeks = aggregate.ActiveWeeks.Count;
        AIrhythmLocalEvidenceStrength strength;
        if (activeWeeks >= 3
            || (activeWeeks >= 2 && aggregate.RecordingCount > 0 && aggregate.ReservationCount > 0))
            strength = AIrhythmLocalEvidenceStrength.Strong;
        else if (activeDays >= 2
            || activeWeeks >= 2)
            strength = AIrhythmLocalEvidenceStrength.Moderate;
        else
            strength = AIrhythmLocalEvidenceStrength.Weak;

        return strength;
    }

    private static AIrhythmLocalEvidenceFamily DominantLocalEvidenceFamily(
        AIrhythmLocalEvidenceStrength continuity,
        AIrhythmLocalEvidenceStrength interest)
    {
        if (continuity != AIrhythmLocalEvidenceStrength.None
            && (int)continuity >= (int)interest)
            return AIrhythmLocalEvidenceFamily.ContinuityFamily;
        if (interest != AIrhythmLocalEvidenceStrength.None)
            return AIrhythmLocalEvidenceFamily.ExplicitInterestFamily;
        return AIrhythmLocalEvidenceFamily.None;
    }

    private static double LocalEvidenceAdjustmentValue(
        AIrhythmLocalEvidenceStrength continuity,
        AIrhythmLocalEvidenceStrength interest,
        out AIrhythmLocalEvidenceFamily dominantFamily)
    {
        dominantFamily = DominantLocalEvidenceFamily(continuity, interest);
        return dominantFamily switch
        {
            AIrhythmLocalEvidenceFamily.ContinuityFamily => LocalContinuityAdjustmentValue(continuity),
            AIrhythmLocalEvidenceFamily.ExplicitInterestFamily => LocalInterestAdjustmentValue(interest),
            _ => 0.0d
        };
    }

    private static AIrhythmLocalEvidenceShadowRow EvaluateLocalEvidenceShadow(
        TvAirProgramEventDto candidate,
        AIrhythmLocalContinuityEvaluation continuityEvaluation,
        IReadOnlyDictionary<string, AIrhythmLocalWorkAggregate> aggregates,
        DateTimeOffset now)
    {
        var workKey = continuityEvaluation.WorkKey;
        aggregates.TryGetValue(workKey, out var aggregate);
        var interest = EvaluateInterestStrength(aggregate);
        var continuity = continuityEvaluation.Strength;

        var localAdjustmentValue = LocalEvidenceAdjustmentValue(continuity, interest, out var dominantFamily);
        var proposedAdjustment = dominantFamily switch
        {
            AIrhythmLocalEvidenceFamily.ContinuityFamily => $"continuity:{localAdjustmentValue:0.00}",
            AIrhythmLocalEvidenceFamily.ExplicitInterestFamily => $"interest:{localAdjustmentValue:0.000}",
            _ => "none"
        };

        var lastSeenDays = aggregate?.LastSeen is DateTimeOffset lastSeen
            ? Math.Max(0, (int)Math.Floor((now - lastSeen).TotalDays))
            : -1;

        return new(
            $"{candidate.NetworkId}:{candidate.TransportStreamId}:{candidate.ServiceId}:{candidate.EventNumber}:{candidate.Start.UtcDateTime.Ticks}",
            workKey,
            continuityEvaluation.EpisodeNumber,
            continuity,
            aggregate?.RecordingCount ?? 0,
            aggregate?.ReservationCount ?? 0,
            aggregate?.ActiveDays.Count ?? 0,
            aggregate?.ActiveWeeks.Count ?? 0,
            lastSeenDays,
            interest,
            dominantFamily,
            proposedAdjustment,
            continuityEvaluation.DifferentService,
            continuityEvaluation.EpisodeDistance,
            continuityEvaluation.Replay,
            continuityEvaluation.SourceKind);
    }

    internal static void InvalidateScoreResultCache()
    {
        lock (ScoreResultCacheGate)
        {
            CachedScoreSnapshot = null;
            CachedScoreExternalEvidenceGeneration = -1;
            CachedScoreRecommendations = null;
            CachedCalibrationCandidates = new();
        }
    }

    internal static bool TryStagePreChoiceCalibrationCandidate(int networkId, int transportStreamId, int serviceId, int eventNumber)
    {
        lock (ScoreResultCacheGate)
        {
            var now = DateTimeOffset.Now;
            foreach (var stale in PendingPreChoiceCalibrationCandidates
                         .Where(x => now - x.Value.CapturedAt > PendingPreChoiceCalibrationLifetime)
                         .Select(x => x.Key)
                         .ToArray())
                PendingPreChoiceCalibrationCandidates.Remove(stale);

            var scored = CachedScoreRecommendations;
            if (scored is null)
                return false;

            foreach (var recommendation in scored)
            {
                var identity = recommendation.EventIdentity;
                if (identity is null
                    || identity.NetworkId != networkId
                    || identity.TransportStreamId != transportStreamId
                    || identity.ServiceId != serviceId
                    || identity.EventNumber != eventNumber)
                    continue;

                var key = CalibrationEventKey(networkId, transportStreamId, serviceId, eventNumber, identity.Start);
                if (!CachedCalibrationCandidates.TryGetValue(key, out var candidate))
                    return false;

                PendingPreChoiceCalibrationCandidates[key] = (candidate, now);
                return true;
            }
            return false;
        }
    }

    internal static bool TryGetPreChoiceCalibrationCandidate(TvAirReservationDto reservation, out AIrhythmEvidenceCalibrationCandidate candidate)
    {
        candidate = default;
        var key = CalibrationEventKey(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId, reservation.EventNumber, reservation.Start);
        lock (ScoreResultCacheGate)
        {
            var now = DateTimeOffset.Now;
            foreach (var stale in PendingPreChoiceCalibrationCandidates
                         .Where(x => now - x.Value.CapturedAt > PendingPreChoiceCalibrationLifetime)
                         .Select(x => x.Key)
                         .ToArray())
                PendingPreChoiceCalibrationCandidates.Remove(stale);

            if (PendingPreChoiceCalibrationCandidates.Remove(key, out var staged)
                && now - staged.CapturedAt <= PendingPreChoiceCalibrationLifetime)
            {
                candidate = staged.Candidate;
                return true;
            }
            return CachedCalibrationCandidates.TryGetValue(key, out candidate);
        }
    }

    private static AIrhythmCalibrationEventKey CalibrationEventKey(int networkId, int transportStreamId, int serviceId, int eventNumber, DateTimeOffset start)
        => new(networkId, transportStreamId, serviceId, eventNumber, start.UtcDateTime.Ticks);

    private readonly record struct AIrhythmReservedWorkKey(string WorkKey, AIrhythmServiceIdentity ServiceIdentity)
    {
        public bool Equals(AIrhythmReservedWorkKey other)
            => ServiceIdentity.Equals(other.ServiceIdentity)
                && string.Equals(WorkKey, other.WorkKey, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode()
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(WorkKey ?? string.Empty), ServiceIdentity);
    }

}
