namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmDataState
{
    private static void RefreshExternalLookupCapability()
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return;

        TvAirExternalLookupCapabilityDto? capability = null;
        try { capability = context.ExternalLookup.GetCapability(); } catch { }
        lock (Gate) _externalLookupCapability = capability;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        LogExternalLookupCapability(capability);
#endif
    }

    private static string BuildExternalProviderSearchQuery(
        string query,
        AIrhythmExternalEvidenceNeedReason reason,
        AIrhythmNumericParenthesizedClass numericClass,
        bool strongLocalSequence)
    {
        if (reason != AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix
            || numericClass != AIrhythmNumericParenthesizedClass.LocalSequence
            || !strongLocalSequence
            || string.IsNullOrWhiteSpace(query))
            return query;

        // 作品識別 — SearchShow resolves the series/show identity; the local episode number is retained separately
        // for GetEpisodes verification. Remove only a terminal numeric parenthesis already classified as a
        // strong LocalSequence. Never strip years or arbitrary parenthesized identity from other ambiguity
        // classes.
        var stem = Regex.Replace(query, @"\s*[（(]\s*[0-9]+\s*[）)]\s*$", string.Empty).Trim();
        return string.IsNullOrWhiteSpace(stem) ? query : stem;
    }



    private static AIrhythmExternalUserIntentSignal GetExternalUserIntentSignal(
        TvAirProgramEventDto item,
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmRecommendationEngine.AIrhythmEvidenceIdentityContext identityContext)
    {
        var eventKey = AIrhythmRecommendationEngine.CanonicalWorkKey(item, identityContext);
        if (string.IsNullOrWhiteSpace(eventKey))
            return default;

        var automaticReservations = snapshot.Reservations.Count(reservation =>
            AIrhythmDataState.IsUsefulReservation(reservation)
            && reservation.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule
            && string.Equals(
                AIrhythmRecommendationEngine.CanonicalWorkKey(reservation, identityContext),
                eventKey,
                StringComparison.OrdinalIgnoreCase));
        var manualReservations = snapshot.Reservations.Count(reservation =>
            AIrhythmDataState.IsUsefulReservation(reservation)
            && reservation.Intent is not (TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule)
            && !AIrhythmPreferenceOriginPolicy.IsExcludedReservation(reservation)
            && string.Equals(
                AIrhythmRecommendationEngine.CanonicalWorkKey(reservation, identityContext),
                eventKey,
                StringComparison.OrdinalIgnoreCase));
        var recordings = snapshot.History.Count(history =>
            AIrhythmDataState.IsUsefulHistory(history)
            && AIrhythmDataState.TryGetRecordedPreferenceOrigin(history, out var historyIntent, out var historySource)
            && AIrhythmPreferenceOriginPolicy.AllowsRecordingPreference(true, historyIntent, historySource)
            && string.Equals(
                AIrhythmRecommendationEngine.CanonicalWorkKey(history, identityContext),
                eventKey,
                StringComparison.OrdinalIgnoreCase));

        return new AIrhythmExternalUserIntentSignal(automaticReservations, manualReservations, recordings);
    }

    private static string BuildPrimaryExternalWorkSearchQuery(
        TvAirProgramEventDto item,
        AIrhythmNumericParenthesizedClass numericClass,
        bool strongLocalSequence,
        AIrhythmLeadingContainerParts containerParts)
    {
        var normalized = AIrhythmRecommendationEngine.BuildExternalLookupQueryTitle(item.Title);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        // 外部情報 — Explicit #/第n話/EP markers are deterministic local episode structure, so a catalogue lookup
        // should use the work title instead of the episode-labelled broadcast title. A quoted token before that
        // episode marker is often the actual Work inside a broadcast slot wrapper; a quoted token after the
        // marker is more likely an episode subtitle and is deliberately not extracted (avoids short
        // subtitle-only lookups such as "約束").
        var episodeMarker = Regex.Match(normalized,
            @"(?:[#＃]\s*[0-9０-９]+(?:\s*[-~〜～]\s*[0-9０-９]+)?|(?:第\s*)?[0-9０-９]+\s*(?:話|回)|(?:episode|ep\.?)\s*[0-9０-９]+)",
            RegexOptions.IgnoreCase);
        var quotedWork = Regex.Match(normalized, @"[「『]([^」』]{2,80})[」』]");
        var work = quotedWork.Success && episodeMarker.Success && quotedWork.Index < episodeMarker.Index
            ? quotedWork.Groups[1].Value.Trim()
            : normalized;

        // 作品識別 — A bracketed block immediately before a deterministic episode marker behaves like an episode
        // subtitle instead of the Work. Strip only in that positional structure.
        work = Regex.Replace(work,
            @"(?:\s|　)*【[^【】]{2,100}】(?=(?:\s|　)*(?:[#＃]\s*[0-9０-９]+|(?:第\s*)?[0-9０-９]+\s*(?:話|回)|(?:episode|ep\.?)\s*[0-9０-９]+))",
            string.Empty, RegexOptions.IgnoreCase).Trim();
        work = Regex.Replace(work,
            @"(?:\s|　)*(?:[#＃]\s*[0-9０-９]+(?:\s*[-~〜～]\s*[0-9０-９]+)?|(?:第\s*)?[0-9０-９]+\s*(?:話|回)|(?:episode|ep\.?)\s*[0-9０-９]+)(?:\s*[/／].*)?$",
            string.Empty, RegexOptions.IgnoreCase).Trim();

        // 作品識別 — A leading broadcast container is not the work identity when a usable work candidate is already
        // structurally available. This remains generic and never names a programme.
        if (!string.IsNullOrWhiteSpace(containerParts.WorkCandidate)
            && !string.Equals(containerParts.WorkCandidate, work, StringComparison.OrdinalIgnoreCase)
            && work.StartsWith("【", StringComparison.Ordinal))
            work = containerParts.WorkCandidate.Trim();

        // 作品識別 — Parenthesized numbers are stripped only when local same-series evidence already says they
        // behave like episode numbering. Years and unsupported numeric identity stay intact. This runs after
        // container extraction so the WorkCandidate cannot reintroduce the episode.
        if (strongLocalSequence && numericClass is AIrhythmNumericParenthesizedClass.LocalSequence or AIrhythmNumericParenthesizedClass.Ambiguous)
            work = Regex.Replace(work, @"\s*[（(]\s*[0-9０-９]+\s*[）)]\s*$", string.Empty).Trim();

        return work.Length >= 2 ? work : normalized;
    }

    private static Dictionary<string, AIrhythmExternalProviderOutcomeObservation> LoadExternalProviderOutcomes(ITvAirPluginRuntimeContext context)
    {
        var result = new Dictionary<string, AIrhythmExternalProviderOutcomeObservation>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var stored = context.Storage.Get(ExternalLookupRuntimeStorageNamespace, ExternalProviderOutcomeStorageKey);
            var json = stored.Succeeded ? stored.Value?.Value?.ToString() : null;
            if (string.IsNullOrWhiteSpace(json))
                return result;

            var state = JsonSerializer.Deserialize<AIrhythmExternalProviderOutcomeState>(json, JsonOptions);
            if (state is null || state.SchemaVersion != ExternalProviderOutcomeSchemaVersion)
                return result;

            foreach (var item in state.Observations ?? Array.Empty<AIrhythmExternalProviderOutcomeObservation>())
            {
                var query = (item.Query ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
                if (query.Length < 2)
                    continue;
                result[query] = item with { Query = query };
            }
        }
        catch { }
        return result;
    }

    private static int GetExternalProviderOutcomePriorityAdjustment(
        string query,
        IReadOnlyDictionary<string, AIrhythmExternalProviderOutcomeObservation> outcomes)
    {
        var key = (query ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        if (key.Length < 2 || !outcomes.TryGetValue(key, out var outcome) || outcome.Attempts <= 0)
            return 180; // 作品識別 — retain a small exploration advantage for unseen Works

        // 外部情報 — Learn provider utility from actual results instead of hard-coding programme classes. Supported
        // catalogue identity is valuable, unresolved evidence is mildly useful, and repeated empty/conflicting
        // searches are deprioritized while never becoming impossible.
        var attempts = Math.Max(1, outcome.Attempts);
        var utility = outcome.SupportedCount * 900
            + outcome.UnresolvedCount * 120
            - outcome.NoEvidenceCount * 700
            - outcome.ConflictingCount * 900;
        var average = utility / attempts;
        return Math.Clamp(average, -2400, 1800);
    }

    private static void ObserveExternalProviderOutcome(
        IDictionary<string, AIrhythmExternalProviderOutcomeObservation> outcomes,
        string query,
        IReadOnlyList<AIrhythmExternalEvidence> interpreted,
        DateTimeOffset attemptedAt)
    {
        var key = (query ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        if (key.Length < 2)
            return;

        outcomes.TryGetValue(key, out var existing);
        existing ??= new AIrhythmExternalProviderOutcomeObservation(key, 0, 0, 0, 0, 0, 0, attemptedAt);
        var supported = interpreted.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported);
        var unresolved = interpreted.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Unresolved);
        var conflicting = interpreted.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Conflicting);
        outcomes[key] = existing with
        {
            Attempts = existing.Attempts + 1,
            EvidenceCount = existing.EvidenceCount + interpreted.Count,
            SupportedCount = existing.SupportedCount + supported,
            UnresolvedCount = existing.UnresolvedCount + unresolved,
            ConflictingCount = existing.ConflictingCount + conflicting,
            NoEvidenceCount = existing.NoEvidenceCount + (interpreted.Count == 0 ? 1 : 0),
            LastAttemptAt = attemptedAt
        };
    }

    private static void PersistExternalProviderOutcomes(
        ITvAirPluginRuntimeContext context,
        IReadOnlyDictionary<string, AIrhythmExternalProviderOutcomeObservation> outcomes)
    {
        try
        {
            var retained = outcomes.Values
                .OrderByDescending(item => item.LastAttemptAt)
                .ThenByDescending(item => item.Attempts)
                .Take(ExternalProviderOutcomeLimit)
                .ToArray();
            var state = new AIrhythmExternalProviderOutcomeState(
                ExternalProviderOutcomeSchemaVersion, DateTimeOffset.Now, retained);
            context.Storage.Set(ExternalLookupRuntimeStorageNamespace, ExternalProviderOutcomeStorageKey,
                JsonSerializer.Serialize(state, JsonOptions), expectedRevision: null);
        }
        catch { }
    }

    private static void EnsureManualExternalRefreshDateLoaded(ITvAirPluginRuntimeContext context)
    {
        lock (Gate)
        {
            if (_lastManualExternalRefreshDateLoaded)
                return;

            try
            {
                var stored = context.Storage.Get(ExternalLookupRuntimeStorageNamespace, ExternalManualRefreshDateStorageKey);
                var json = stored.Succeeded ? stored.Value?.Value?.ToString() : null;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("localDate", out var localDateElement)
                        && DateOnly.TryParseExact(localDateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                        _lastManualExternalRefreshDate = parsedDate;
                }
            }
            catch { }

            _lastManualExternalRefreshDateLoaded = true;
        }
    }

    private static (bool AlreadyRefreshed, string LocalDate) IsManualExternalRefreshAlreadyCompletedToday(ITvAirPluginRuntimeContext context, DateTimeOffset now)
    {
        EnsureManualExternalRefreshDateLoaded(context);
        var today = DateOnly.FromDateTime(now.LocalDateTime.Date);
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (Gate)
            return (_lastManualExternalRefreshDate == today, todayText);
    }

    private static (bool Claimed, string Reason, string LocalDate) TryClaimManualExternalRefreshDay(ITvAirPluginRuntimeContext context, DateTimeOffset now)
    {
        EnsureManualExternalRefreshDateLoaded(context);
        var today = DateOnly.FromDateTime(now.LocalDateTime.Date);
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (Gate)
        {
            if (_lastManualExternalRefreshDate == today)
                return (false, "already_refreshed_today", todayText);

            try
            {
                var json = JsonSerializer.Serialize(new { localDate = todayText }, JsonOptions);
                var result = context.Storage.Set(ExternalLookupRuntimeStorageNamespace, ExternalManualRefreshDateStorageKey, json, expectedRevision: null);
                if (!result.Succeeded)
                    return (false, "daily_gate_persist_failed", todayText);
            }
            catch
            {
                return (false, "daily_gate_persist_failed", todayText);
            }

            _lastManualExternalRefreshDate = today;
            return (true, "claimed", todayText);
        }
    }

    public static async Task RefreshExternalEvidenceAsync(CancellationToken cancellationToken)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return;

        var capability = AIrhythmExternalLookupAdapter.GetCapability(context);
        lock (Gate) _externalLookupCapability = capability;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        LogExternalLookupCapability(capability);
#endif
        if (capability is null || !capability.PluginDeclaredPermission || !capability.UserAllowed)
            return;

        // 保存・読込 — The once-per-local-day gate belongs to external refresh work, not to local refresh. If today's
        // external refresh was already completed, exit before snapshot/probe/provider-plan construction; the
        // caller still invalidates the local snapshot and rerenders normally.
        var dailyPreflight = IsManualExternalRefreshAlreadyCompletedToday(context, DateTimeOffset.Now);
        if (dailyPreflight.AlreadyRefreshed)
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"external manual refresh daily gate result=SKIP reason=already_refreshed_today localDate={dailyPreflight.LocalDate} policy=manual_refresh_once_per_local_day stage=preflight");
#endif
            return;
        }

        var snapshot = Capture();
        if (!snapshot.Ready || snapshot.Events.Count == 0)
            return;

        PruneExternalEvidenceState(DateTimeOffset.Now);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        RunExternalEvidenceInterpretationSelfValidation();
        LogExternalEvidenceCacheRelationState("before_refresh");
#endif
var identityContext = AIrhythmRecommendationEngine.BuildEvidenceIdentityContext(snapshot);
                var numericLocalValues = AIrhythmRecommendationEngine.BuildNumericParenthesizedLocalSequenceValues(snapshot.Events);
        var numericLocalValuesByService = AIrhythmRecommendationEngine.BuildNumericParenthesizedLocalSequenceValuesByService(snapshot.Events);
        var numericLocalSequenceCounts = numericLocalValues.ToDictionary(
            pair => pair.Key, pair => pair.Value.Length, StringComparer.OrdinalIgnoreCase);
        var leadingContainerLocalContexts = BuildLeadingContainerLocalContexts(snapshot.Events);
        var representativeProbes = snapshot.Events
            .Select(item => new
            {
                Event = item,
                Need = AIrhythmRecommendationEngine.EvaluateExternalEvidenceNeed(item.Title),
                Queries = AIrhythmRecommendationEngine.BuildExternalLookupQueryTitles(item.Title),
                ContainerKey = BuildLeadingContainerProbeKey(item.Title),
                ContainerParts = AIrhythmRecommendationEngine.ParseLeadingContainerParts(item.Title),
                ContainerLocalContext = GetLeadingContainerLocalContext(item, leadingContainerLocalContexts),
                DerivedRelation = AIrhythmRecommendationEngine.ClassifyDerivedProgramRelation(item.Title),
                NumericClass = AIrhythmRecommendationEngine.ClassifyNumericParenthesizedSuffix(item.Title, numericLocalSequenceCounts),
                NumericContext = AIrhythmRecommendationEngine.GetNumericParenthesizedProbeContext(item.Title, numericLocalValues),
                StrongLocalSequence = AIrhythmRecommendationEngine.IsCorroboratedParenthesizedWorkSequence(item, numericLocalValuesByService),
                NumericDiagnostic = AIrhythmRecommendationEngine.GetNumericParenthesizedDiagnosticParts(item.Title)
            })
            .Where(item => item.Need.Needed && item.Queries.Count > 0)
            .SelectMany(item => item.Queries.Select(query => new
            {
                item.Event,
                item.Need,
                item.ContainerKey,
                item.ContainerParts,
                item.ContainerLocalContext,
                item.DerivedRelation,
                item.NumericClass,
                item.NumericContext,
                item.StrongLocalSequence,
                item.NumericDiagnostic,
                Query = query,
                SearchQuery = BuildExternalProviderSearchQuery(
                    query,
                    item.Need.Reason,
                    item.NumericClass,
                    item.StrongLocalSequence),
                Priority = GetExternalLookupProbePriority(item.Event.Title, query)
                    + GetNumericParenthesizedProbePriority(item.Need.Reason, item.NumericClass, item.NumericContext)
                    + GetLeadingContainerProbePriority(item.Need.Reason, query, item.ContainerParts, item.ContainerLocalContext)
                    + (item.Need.Reason == AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker ? 60 : 0)
            }))
            .Where(item => !string.IsNullOrWhiteSpace(item.Query) && !string.IsNullOrWhiteSpace(item.SearchQuery))
            // 外部情報 — A repeated leading 【...】 prefix is one ambiguity class, not hundreds of independent network
            // decisions. Retain one representative probe per prefix while preserving the original title locally
            // until evidence actually corroborates it.
            .GroupBy(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate
                    && !string.IsNullOrWhiteSpace(item.ContainerKey)
                ? $"container:{item.ContainerKey}"
                : $"query:{item.SearchQuery}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.Event.Start)
                .First())
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Event.Start)
            .ToArray();

        // 外部情報 — Different ambiguity classes can still collapse to the same normalized search query. Network
        // work is query-scoped, so dedupe once more by query before applying the per-refresh budget. This keeps
        // the plan count aligned with actual provider calls.
        var uniqueQueryProbes = representativeProbes
            .GroupBy(item => item.SearchQuery, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.Event.Start)
                .First())
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Event.Start)
            .ToArray();

        // 外部情報 — Primary provider candidates come from explicit user behaviour, not title oddities. Provider
        // utility is learned from prior real lookup outcomes so scarce daily requests move toward Works for
        // which the provider has actually supplied useful catalogue evidence. No title, genre, service or person
        // is hard-coded into this decision.
        var providerOutcomes = LoadExternalProviderOutcomes(context);
        var primaryWorkProbes = snapshot.Events
            .Select(item =>
            {
                var need = AIrhythmRecommendationEngine.EvaluateExternalEvidenceNeed(item.Title);
                var containerKey = BuildLeadingContainerProbeKey(item.Title);
                var containerParts = AIrhythmRecommendationEngine.ParseLeadingContainerParts(item.Title);
                var containerLocalContext = GetLeadingContainerLocalContext(item, leadingContainerLocalContexts);
                var derivedRelation = AIrhythmRecommendationEngine.ClassifyDerivedProgramRelation(item.Title);
                var numericClass = AIrhythmRecommendationEngine.ClassifyNumericParenthesizedSuffix(item.Title, numericLocalSequenceCounts);
                var numericContext = AIrhythmRecommendationEngine.GetNumericParenthesizedProbeContext(item.Title, numericLocalValues);
                var strongLocalSequence = AIrhythmRecommendationEngine.IsCorroboratedParenthesizedWorkSequence(item, numericLocalValuesByService);
                var numericDiagnostic = AIrhythmRecommendationEngine.GetNumericParenthesizedDiagnosticParts(item.Title);
                var signal = GetExternalUserIntentSignal(item, snapshot, identityContext);
                var searchQuery = BuildPrimaryExternalWorkSearchQuery(item, numericClass, strongLocalSequence, containerParts);
                return new
                {
                    Event = item,
                    Need = need,
                    ContainerKey = containerKey,
                    ContainerParts = containerParts,
                    ContainerLocalContext = containerLocalContext,
                    DerivedRelation = derivedRelation,
                    NumericClass = numericClass,
                    NumericContext = numericContext,
                    StrongLocalSequence = strongLocalSequence,
                    NumericDiagnostic = numericDiagnostic,
                    Query = searchQuery,
                    SearchQuery = searchQuery,
                    Priority = signal.Priority
                        + GetExternalProviderOutcomePriorityAdjustment(searchQuery, providerOutcomes)
                        + GetExternalLookupProbePriority(item.Title, searchQuery)
                };
            })
            .Where(item => item.Priority > 0 && !string.IsNullOrWhiteSpace(item.SearchQuery))
            .GroupBy(item => AIrhythmRecommendationEngine.CanonicalWorkKey(item.Event, identityContext), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.SearchQuery.Length)
                .ThenBy(item => item.Event.Start)
                .First())
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.SearchQuery.Length)
            .ThenBy(item => item.Event.Start)
            .ToArray();

        // 作品識別 — Retain a broad per-refresh batch, but spend the first numeric slots on the strongest
        // already-corroborated local sequence evidence first. This is probe selection only: same-service local
        // sequence evidence does not rewrite identity or make a verdict. Ambiguous siblings remain fallback
        // probes when no stronger LocalSequence candidate fills the numeric slots. Derived/container classes
        // retain one seed each.
        var derivedSeed = uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker)
            .Take(1)
            .ToArray();
        var containerSeed = uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate)
            .Take(1)
            .ToArray();
        var numericLocalSequencePreferred = uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix
                && item.NumericClass == AIrhythmNumericParenthesizedClass.LocalSequence
                && item.StrongLocalSequence)
            .Take(2)
            .ToArray();
        var numericAmbiguousCorroborated = uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix
                && item.NumericClass == AIrhythmNumericParenthesizedClass.Ambiguous
                && item.NumericContext.DistinctLocalValues >= 2
                && !numericLocalSequencePreferred.Any(selected => string.Equals(selected.SearchQuery, item.SearchQuery, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Max(0, 2 - numericLocalSequencePreferred.Length))
            .ToArray();
        var numericFallback = uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix
                && item.NumericClass == AIrhythmNumericParenthesizedClass.Ambiguous
                && item.NumericContext.DistinctLocalValues < 2
                && !numericLocalSequencePreferred.Any(selected => string.Equals(selected.SearchQuery, item.SearchQuery, StringComparison.OrdinalIgnoreCase))
                && !numericAmbiguousCorroborated.Any(selected => string.Equals(selected.SearchQuery, item.SearchQuery, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Max(0, 2 - numericLocalSequencePreferred.Length - numericAmbiguousCorroborated.Length))
            .ToArray();

        var seededProbes = derivedSeed
            .Concat(containerSeed)
            .Concat(numericLocalSequencePreferred)
            .Concat(numericAmbiguousCorroborated)
            .Concat(numericFallback)
            .GroupBy(item => item.SearchQuery, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(ExternalLookupProbeLimit)
            .ToArray();
        var probes = seededProbes
            .Concat(uniqueQueryProbes.Where(item => !seededProbes.Any(selected =>
                string.Equals(selected.SearchQuery, item.SearchQuery, StringComparison.OrdinalIgnoreCase))))
            .Take(ExternalLookupProbeLimit)
            .ToArray();

        // 外部情報 — TVmaze is the single external Evidence provider. User-intent Works are evaluated first;
        // structural-ambiguity probes are appended only when capacity remains. Provider Evidence is supplemental
        // and never replaces local identity or local-only operation.
        var tvMazePrimaryProbes = primaryWorkProbes.ToArray();
        var tvMazeProbes = tvMazePrimaryProbes
            .Concat(probes.Where(item => !tvMazePrimaryProbes.Any(primary =>
                string.Equals(primary.SearchQuery, item.SearchQuery, StringComparison.OrdinalIgnoreCase))))
            .GroupBy(item => item.SearchQuery, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(ExternalLookupProbeLimit)
            .ToArray();
        var tvMazeProbeQueries = new HashSet<string>(tvMazeProbes.Select(item => item.SearchQuery), StringComparer.OrdinalIgnoreCase);
        var executionProbes = tvMazeProbes;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var representativeReasons = string.Join(",", representativeProbes
            .GroupBy(item => item.Need.Reason)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}"));
        var uniqueQueryReasons = string.Join(",", uniqueQueryProbes
            .GroupBy(item => item.Need.Reason)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}"));
        var selectedReasons = string.Join(",", probes
            .GroupBy(item => item.Need.Reason)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}"));
        var numericClasses = string.Join(",", uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix)
            .GroupBy(item => item.NumericClass)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}"));
        var derivedRelations = string.Join(",", uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker)
            .GroupBy(item => item.DerivedRelation)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}"));
        WriteDeveloperLog($"external probe plan policy=local_sequence_stem_search_v3 representatives={representativeProbes.Length} uniqueQueries={uniqueQueryProbes.Length} candidateSelected={probes.Length} representativeReasons=[{representativeReasons}] uniqueQueryReasons=[{uniqueQueryReasons}] selectedReasons=[{selectedReasons}] numericClasses=[{numericClasses}] derivedRelations=[{derivedRelations}] numericStrongLocalSequenceCandidates={uniqueQueryProbes.Count(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix && item.NumericClass == AIrhythmNumericParenthesizedClass.LocalSequence && item.StrongLocalSequence)} numericStrongLocalSequenceSelected={numericLocalSequencePreferred.Length} numericAmbiguousCorroboratedCandidates={uniqueQueryProbes.Count(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix && item.NumericClass == AIrhythmNumericParenthesizedClass.Ambiguous && item.NumericContext.DistinctLocalValues >= 2)} numericAmbiguousCorroboratedSelected={numericAmbiguousCorroborated.Length} numericFallbackSelected={numericFallback.Length} containerDiversifiedCandidates={uniqueQueryProbes.Count(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate && item.ContainerLocalContext.IsDiversified)} containerRepeatedOnlyCandidates={uniqueQueryProbes.Count(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate && item.ContainerLocalContext.EvidenceLevel == AIrhythmLeadingContainerEvidenceLevel.Repeated)} containerSingletonCandidates={uniqueQueryProbes.Count(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate && item.ContainerLocalContext.EvidenceLevel == AIrhythmLeadingContainerEvidenceLevel.None)} candidateSelectionMax={ExternalLookupProbeLimit} tvMazeRequestBudget={AIrhythmExternalLookupAdapter.TvMazeRequestBudgetForDiagnostics}");
        var tvMazeSearchAvailable = AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "SearchShow");
        var tvMazeAliasesAvailable = AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "GetAliases");
        var tvMazeEpisodesAvailable = AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "GetEpisodes");
        var selectedEpisodeCandidates = tvMazeProbes.Count(item => AIrhythmRecommendationEngine.TryGetExternalEpisodeCandidate(item.Event.Title) is > 0);
        var tvMazePrimarySummary = string.Join(",", tvMazePrimaryProbes.Take(12).Select(item =>
        {
            var signal = GetExternalUserIntentSignal(item.Event, snapshot, identityContext);
            var providerAdjustment = GetExternalProviderOutcomePriorityAdjustment(item.SearchQuery, providerOutcomes);
            return $"{item.SearchQuery}:auto={signal.AutomaticReservationCount}/manual={signal.ManualReservationCount}/recording={signal.RecordingCount}/behaviour={signal.Priority}/provider={providerAdjustment}/total={item.Priority}";
        }));
        WriteDeveloperLog($"external provider plan policy=behaviour_plus_learned_provider_utility_v3 primaryWorks={primaryWorkProbes.Length} tvMazePrimary={tvMazePrimaryProbes.Length} tvMazeSelected={tvMazeProbes.Length} fallbackSpecialSelected={probes.Length} providerOutcomeQueries={providerOutcomes.Count} tvMazePrimarySample=[{tvMazePrimarySummary}] tvMazeSearchAvailable={tvMazeSearchAvailable} tvMazeAliasesAvailable={tvMazeAliasesAvailable} tvMazeEpisodesAvailable={tvMazeEpisodesAvailable} selectedLocalEpisodeCandidates={selectedEpisodeCandidates} fallbackPolicy=special_titles_only_after_user_intent providerRouting=tvmaze_only tvMazeRequestBudget={AIrhythmExternalLookupAdapter.TvMazeRequestBudgetForDiagnostics} tvMazeCooldownSeconds={(long)AIrhythmExternalLookupAdapter.TvMazeRateLimitCooldownForDiagnostics.TotalSeconds} followupNetwork=provider_budgeted");
#endif
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var episodeCandidateProbeCount = 0;
        var episodeCandidateSearchEvidenceCount = 0;
        var episodeCandidateShowIdResolvedCount = 0;
        var episodeFollowupExecutedCount = 0;
        var episodeFollowupSupportedCount = 0;
#endif

        var providerRefresh = AIrhythmExternalLookupAdapter.BeginRefresh();
        var tvMazeAvailableForRefresh = AIrhythmExternalLookupAdapter.Supports(
            capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "SearchShow");
        var dailyExternalRefreshClaimed = false;

        foreach (var probe in executionProbes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episodeCandidate = AIrhythmRecommendationEngine.TryGetExternalEpisodeCandidate(probe.Event.Title);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            if (episodeCandidate is > 0)
                episodeCandidateProbeCount++;
#endif

            var tvMazeProbeSearchAvailable = tvMazeAvailableForRefresh && providerRefresh.TvMaze.IsOpen && tvMazeProbeQueries.Contains(probe.SearchQuery);
            var attemptDecision = tvMazeProbeSearchAvailable
                ? GetExternalLookupAttemptDecision(probe.SearchQuery)
                : (ShouldAttempt: false, SkipReason: "provider_unavailable", AgeSeconds: -1L);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"external probe decision query={probe.Query} searchQuery={probe.SearchQuery} reason={probe.Need.Reason} rawTitle={probe.Event.Title ?? string.Empty} normalizedTitle={probe.NumericDiagnostic.NormalizedTitle} numericStem={probe.NumericDiagnostic.Stem} numericClass={probe.NumericClass} numericValue={probe.NumericContext.Value} localValues={probe.NumericContext.DistinctLocalValues} nearestDistance={probe.NumericContext.NearestLocalDistance} strongLocalSequence={probe.StrongLocalSequence} container={probe.ContainerParts.Container} containerWork={probe.ContainerParts.WorkCandidate} embeddedTopic={probe.ContainerParts.EmbeddedTopic} containerEvents={probe.ContainerLocalContext.EventCount} containerDistinctWorks={probe.ContainerLocalContext.DistinctWorkCount} containerEvidence={probe.ContainerLocalContext.EvidenceLevel} containerRepeated={probe.ContainerLocalContext.IsRepeated} containerDiversified={probe.ContainerLocalContext.IsDiversified} derivedRelation={probe.DerivedRelation} action={(attemptDecision.ShouldAttempt ? "execute" : "skip")} skipReason={attemptDecision.SkipReason} ageSeconds={attemptDecision.AgeSeconds} providerAvailable={tvMazeProbeSearchAvailable} provider=tvmaze");
#endif
            if (!attemptDecision.ShouldAttempt)
                continue;

            if (!dailyExternalRefreshClaimed)
            {
                var dailyGate = TryClaimManualExternalRefreshDay(context, DateTimeOffset.Now);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"external manual refresh daily gate result={(dailyGate.Claimed ? "CLAIMED" : "SKIP")} reason={dailyGate.Reason} localDate={dailyGate.LocalDate} policy=manual_refresh_once_per_local_day tvMazeBudget={AIrhythmExternalLookupAdapter.TvMazeRequestBudgetForDiagnostics}");
#endif
                if (!dailyGate.Claimed)
                    break;
                dailyExternalRefreshClaimed = true;
            }

            // 外部情報 — Host authorization is the network boundary. AI-rhythm issues only the fixed TVmaze
            // ExternalLookup operations and never owns raw HTTP transport.
            MarkExternalLookupAttempt(probe.SearchQuery, DateTimeOffset.Now);
            var searchResults = new List<(AIrhythmExternalEvidenceResult Result, string ProviderSearchQuery, string InterpretationQuery)>(1);
            if (tvMazeProbeSearchAvailable)
            {
                var tvMazeSearchResult = await AIrhythmExternalLookupAdapter.SearchTvMazeAsync(
                    providerRefresh.TvMaze, context, capability, probe.SearchQuery, cancellationToken).ConfigureAwait(false);
                searchResults.Add((tvMazeSearchResult, probe.SearchQuery, probe.Query));
                if (tvMazeSearchResult.Code == TvAirExternalLookupResultCode.RateLimited)
                {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                    WriteDeveloperLog($"external tvmaze refresh circuit action=stop_remaining reason=RateLimited query={probe.SearchQuery} minimumIntervalMs={(long)AIrhythmExternalLookupAdapter.TvMazeMinimumIntervalForDiagnostics.TotalMilliseconds}");
#endif
                }
            }
            if (searchResults.Count == 0)
                continue;

            var normalizedList = new List<AIrhythmExternalEvidence>();
            var interpretedList = new List<AIrhythmExternalEvidence>();
            foreach (var searchEntry in searchResults)
            {
                var searchResult = searchEntry.Result;
                var providerEvidence = AIrhythmExternalLookupAdapter.Normalize(searchResult, searchEntry.InterpretationQuery).ToArray();
                var providerInterpreted = InterpretExternalEvidence(
                    providerEvidence,
                    probe.Need.Reason,
                    probe.ContainerParts,
                    probe.ContainerLocalContext,
                    probe.DerivedRelation,
                    probe.NumericClass,
                    searchEntry.InterpretationQuery,
                    probe.NumericContext.Value);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"external lookup return query={probe.Query} searchQuery={searchEntry.ProviderSearchQuery} sourceSearchQuery={probe.SearchQuery} provider={searchResult.ProviderId} operation={searchResult.Operation} code={searchResult.Code} success={searchResult.Success} bodyLength={(searchResult.Body ?? string.Empty).Length} providerMode=host_authorized_tvmaze");
#endif
                normalizedList.AddRange(providerEvidence);
                interpretedList.AddRange(providerInterpreted);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                AddExternalLookupTrace(probe.Event.Title ?? string.Empty, searchEntry.InterpretationQuery, searchResult, providerInterpreted);
                LogExternalLookupResult(searchResult.ProviderId, searchResult.Operation, searchResult.Code,
                    providerInterpreted.Length, localFallback: !searchResult.Success);
#endif
            }
            var normalized = normalizedList
                .GroupBy(item => $"{item.ProviderId}:{item.EntityId}:{item.CanonicalTitle}:{item.Relation}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var interpreted = interpretedList
                .GroupBy(item => $"{item.ProviderId}:{item.EntityId}:{item.CanonicalTitle}:{item.Relation}:{item.Verdict}:{item.VerdictReason}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            ObserveExternalProviderOutcome(providerOutcomes, probe.SearchQuery, interpreted, DateTimeOffset.Now);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var reachabilityEpisodeCandidate = episodeCandidate ?? 0;
            var reachabilityShowId = normalized.FirstOrDefault(item =>
                string.Equals(item.ProviderId, AIrhythmExternalLookupAdapter.TvMazeProviderId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase))?.EntityId;
            if (episodeCandidate is > 0 && normalized.Length > 0)
                episodeCandidateSearchEvidenceCount++;
            if (episodeCandidate is > 0 && !string.IsNullOrWhiteSpace(reachabilityShowId))
                episodeCandidateShowIdResolvedCount++;
            var reachabilityAliasesAvailable = tvMazeProbeSearchAvailable
                && AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "GetAliases");
            var reachabilityEpisodesAvailable = tvMazeProbeSearchAvailable
                && AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "GetEpisodes");
            var supportedReachability = ClassifyExternalSupportedReachability(
                normalized, interpreted, reachabilityAliasesAvailable, reachabilityEpisodesAvailable,
                reachabilityEpisodeCandidate, !string.IsNullOrWhiteSpace(reachabilityShowId));
            WriteDeveloperLog($"external supported reachability query={probe.Query} searchQuery={probe.SearchQuery} searchEvidence={normalized.Length} searchSupported={interpreted.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported)} searchUnresolved={interpreted.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Unresolved)} searchConflicting={interpreted.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Conflicting)} showIdPresent={!string.IsNullOrWhiteSpace(reachabilityShowId)} localEpisodeCandidate={reachabilityEpisodeCandidate} aliasesAvailable={reachabilityAliasesAvailable} episodesAvailable={reachabilityEpisodesAvailable} path={supportedReachability} aliasFollowupPlanned={supportedReachability.HasFlag(AIrhythmExternalSupportedReachability.AliasFollowupAvailable)} episodeFollowupPlanned={supportedReachability.HasFlag(AIrhythmExternalSupportedReachability.EpisodeFollowupAvailable)} criteriaUnchanged=True");
#endif
            AddExternalEvidence(interpreted);
            var projectedEvidence = interpreted.ToList();
            if (searchResults.Any(item => item.Result.Success))
                SetExternalEvidenceSummaryProjection(probe.Event, SummarizeExternalEvidence(projectedEvidence), DetermineExternalEvidenceAdjustmentKind(projectedEvidence), DetermineExternalEvidenceSupportingVerdictReason(projectedEvidence), DateTimeOffset.Now, projectedEvidence);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var expectedRelation = probe.Need.Reason switch
            {
                AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate => GetLeadingContainerExternalEvidenceRelation(probe.ContainerLocalContext),
                AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker => GetDerivedProgramExternalEvidenceRelation(probe.DerivedRelation),
                AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix => GetNumericParenthesizedExternalEvidenceRelation(probe.NumericClass),
                _ => "not_applicable"
            };
            var appliedRelations = interpreted
                .Where(item => item.Relation.StartsWith("candidate:container_work_", StringComparison.OrdinalIgnoreCase)
                    || item.Relation.StartsWith("candidate:numeric_", StringComparison.OrdinalIgnoreCase)
                    || item.Relation.StartsWith("related:", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var relationSample = appliedRelations.FirstOrDefault();
            var evidenceSummary = SummarizeExternalEvidence(interpreted);
            WriteDeveloperLog($"external evidence interpretation query={probe.Query} searchQuery={probe.SearchQuery} reason={probe.Need.Reason} input={normalized.Length} output={interpreted.Length} containerEvidence={probe.ContainerLocalContext.EvidenceLevel} derivedRelation={probe.DerivedRelation} numericRelationClass={probe.NumericClass} relation={expectedRelation} relationApplied={appliedRelations.Length} relationSampleEntity={relationSample?.EntityId ?? string.Empty} relationSampleTitle={relationSample?.CanonicalTitle ?? string.Empty} relationSampleValue={relationSample?.Relation ?? string.Empty} verdicts=[{string.Join(",", interpreted.GroupBy(item => item.Verdict).OrderBy(group => group.Key).Select(group => $"{group.Key}:{group.Count()}"))}] verdictReasons=[{string.Join(",", interpreted.GroupBy(item => item.VerdictReason).OrderBy(group => group.Key).Select(group => $"{group.Key}:{group.Count()}"))}] verdictSample={relationSample?.Verdict.ToString() ?? string.Empty} verdictReasonSample={relationSample?.VerdictReason.ToString() ?? string.Empty} evidenceSummary={evidenceSummary.Status} summaryTotal={evidenceSummary.Total} summarySupported={evidenceSummary.Supported} summaryUnresolved={evidenceSummary.Unresolved} summaryConflicting={evidenceSummary.Conflicting} identityRewrite=False scoreRewrite=False");
#endif

            var tvMazeNormalized = normalized
                .Where(item => string.Equals(item.ProviderId, AIrhythmExternalLookupAdapter.TvMazeProviderId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (tvMazeNormalized.Length == 0)
                continue;

            var showId = tvMazeNormalized.FirstOrDefault(item => string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase))?.EntityId;
            var searchNeedsIdentityFollowup = interpreted.Any(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Unresolved)
                && !interpreted.Any(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported)
                && !interpreted.Any(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Conflicting)
                && !string.IsNullOrWhiteSpace(showId);
            if (tvMazeAvailableForRefresh
                && providerRefresh.TvMaze.IsOpen
                && searchNeedsIdentityFollowup
                && AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "GetAliases"))
            {
                var aliasesResult = await AIrhythmExternalLookupAdapter.GetTvMazeAliasesAsync(providerRefresh.TvMaze, context, capability, showId!, cancellationToken).ConfigureAwait(false);
                var aliasEvidence = AIrhythmExternalLookupAdapter.Normalize(aliasesResult, probe.Query).Take(24).ToArray();
                var interpretedAliasEvidence = InterpretExternalEvidence(
                    aliasEvidence,
                    probe.Need.Reason,
                    probe.ContainerParts,
                    probe.ContainerLocalContext,
                    probe.DerivedRelation,
                    probe.NumericClass,
                    probe.Query,
                    probe.NumericContext.Value);
                AddExternalEvidence(interpretedAliasEvidence);
                if (aliasesResult.Success)
                {
                    projectedEvidence.AddRange(interpretedAliasEvidence);
                    SetExternalEvidenceSummaryProjection(probe.Event, SummarizeExternalEvidence(projectedEvidence), DetermineExternalEvidenceAdjustmentKind(projectedEvidence), DetermineExternalEvidenceSupportingVerdictReason(projectedEvidence), DateTimeOffset.Now, projectedEvidence);
                }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                var aliasSummary = SummarizeExternalEvidence(interpretedAliasEvidence);
                WriteDeveloperLog($"external alias followup query={probe.Query} searchQuery={probe.SearchQuery} showId={showId} result={aliasesResult.Code} evidence={interpretedAliasEvidence.Length} summary={aliasSummary.Status} supported={aliasSummary.Supported} unresolved={aliasSummary.Unresolved} conflicting={aliasSummary.Conflicting} criteriaUnchanged=True scoreRewrite=False orderingRewrite=False");
                AddExternalLookupTrace(probe.Event.Title ?? string.Empty, probe.Query, aliasesResult, interpretedAliasEvidence);
                LogExternalLookupResult(aliasesResult.ProviderId, aliasesResult.Operation, aliasesResult.Code, interpretedAliasEvidence.Length, localFallback: !aliasesResult.Success);
#endif
            }

            if (!tvMazeAvailableForRefresh || !providerRefresh.TvMaze.IsOpen || episodeCandidate is not > 0 || string.IsNullOrWhiteSpace(showId)
                || !AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "GetEpisodes"))
                continue;

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            episodeFollowupExecutedCount++;
#endif
            var episodesResult = await AIrhythmExternalLookupAdapter.GetTvMazeEpisodesAsync(providerRefresh.TvMaze, context, capability, showId, cancellationToken).ConfigureAwait(false);
            var episodeEvidence = AIrhythmExternalLookupAdapter.Normalize(episodesResult, probe.Query)
                .Where(item => item.Episode == episodeCandidate)
                .Take(8)
                .ToArray();
            var interpretedEpisodeEvidence = InterpretExternalEvidence(
                episodeEvidence,
                probe.Need.Reason,
                probe.ContainerParts,
                probe.ContainerLocalContext,
                probe.DerivedRelation,
                probe.NumericClass,
                probe.Query,
                probe.NumericContext.Value);
            AddExternalEvidence(interpretedEpisodeEvidence);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            if (interpretedEpisodeEvidence.Any(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported))
                episodeFollowupSupportedCount++;
#endif
            if (episodesResult.Success)
            {
                projectedEvidence.AddRange(interpretedEpisodeEvidence);
                SetExternalEvidenceSummaryProjection(probe.Event, SummarizeExternalEvidence(projectedEvidence), DetermineExternalEvidenceAdjustmentKind(projectedEvidence), DetermineExternalEvidenceSupportingVerdictReason(projectedEvidence), DateTimeOffset.Now, projectedEvidence);
            }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            AddExternalLookupTrace(probe.Event.Title ?? string.Empty, probe.Query, episodesResult, interpretedEpisodeEvidence);
            LogExternalLookupResult(episodesResult.ProviderId, episodesResult.Operation, episodesResult.Code, interpretedEpisodeEvidence.Length, localFallback: !episodesResult.Success);
#endif
        }
        if (dailyExternalRefreshClaimed)
            PersistExternalProviderOutcomes(context, providerOutcomes);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"external provider outcome memory result=OK storedQueries={providerOutcomes.Count} schema={ExternalProviderOutcomeSchemaVersion} policy=behaviour_plus_learned_provider_utility_v3 titleSpecificRules=False");
        WriteDeveloperLog($"external episode support reachability selectedEpisodeCandidates={episodeCandidateProbeCount} searchEvidence={episodeCandidateSearchEvidenceCount} showIdResolved={episodeCandidateShowIdResolvedCount} getEpisodesExecuted={episodeFollowupExecutedCount} episodeSupported={episodeFollowupSupportedCount} moderateCalibrationState={(episodeFollowupSupportedCount > 0 ? "runtime_evidence_available" : "awaiting_real_episode_support")} selectionPolicy=behaviour_plus_learned_provider_utility_v3 searchBudgetUnchanged=True");
        LogExternalEvidenceCacheRelationState("after_refresh");
#endif
    }

}
