namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmRecommendationEngine
{
    private static string BuildDiscovery(
        RuntimeUiRenderContext context,
        AIrhythmRuntimeSnapshot snapshot,
        IReadOnlyList<AIrhythmRecommendation> allRecommendations,
        IReadOnlyList<AIrhythmRecommendation> recommendations,
        AIrhythmEvidenceIdentityContext identityContext)
    {
        var now = DateTimeOffset.Now;
        var preferenceReservations = snapshot.Reservations
            .Where(x => !AIrhythmPreferenceOriginPolicy.IsExcludedReservation(x)).ToArray();
        var preferenceRecordings = PreferenceRecordingHistory(snapshot).ToArray();
        var reservedWorks = preferenceReservations
            .Select(x => CanonicalWorkKey(x, identityContext))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var recordedWorks = preferenceRecordings
            .Select(x => CanonicalWorkKey(x, identityContext))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var automatedWorks = snapshot.Reservations
            .Where(x => x.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule)
            .Select(x => CanonicalWorkKey(x, identityContext))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var interestWorks = AIrhythmDataState.GetInterestSignals()
            .Select(x => x.SeriesKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var reserved = reservedWorks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recorded = recordedWorks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eventMap = snapshot.Events
            .GroupBy(EventIdentityOf)
            .ToDictionary(x => x.Key, x => x.First());

        TvAirProgramEventDto? EventOf(AIrhythmRecommendation item)
            => item.EventIdentity is not null && eventMap.TryGetValue(item.EventIdentity, out var value) ? value : null;

        bool ContainsAny(string text, params string[] markers)
            => markers.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));

        bool WorkSetContains(IReadOnlyList<string> works, string candidate)
            => works.Any(x => string.Equals(x, candidate, StringComparison.OrdinalIgnoreCase) || WorkMatches(x, candidate));

        // 保存・読込 — A render owns one immutable recommendation population. All shelf objectives and diagnostics
        // use this one evidence projection; no cross-render or persisted cache.
        var ruleTrackingByCandidate = new Dictionary<AIrhythmRecommendation, bool>(ReferenceEqualityComparer.Instance);
        var knownnessByCandidate = new Dictionary<AIrhythmRecommendation, double>(ReferenceEqualityComparer.Instance);
        var axesByCandidate = new Dictionary<AIrhythmRecommendation, (double Knownness, double Novelty, double Plausibility)>(ReferenceEqualityComparer.Instance);
        var fuzzyKnownnessByWork = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        bool IsTrackedByKeywordRule(AIrhythmRecommendation item)
        {
            if (ruleTrackingByCandidate.TryGetValue(item, out var tracked)) return tracked;
            tracked = EvaluateTrackedByKeywordRule(item);
            ruleTrackingByCandidate.Add(item, tracked);
            return tracked;
        }

        bool EvaluateTrackedByKeywordRule(AIrhythmRecommendation item)
        {
            var e = EventOf(item);
            if (e is null || snapshot.KeywordRules.Count == 0)
                return false;
            foreach (var rule in snapshot.KeywordRules)
            {
                if (!rule.Enabled || !RuleServiceAllowed(rule, e))
                    continue;
                var selected = new List<string>(4);
                if (rule.SearchTitle) selected.Add(e.Title);
                if (rule.SearchOutline && !string.IsNullOrWhiteSpace(e.Summary)) selected.Add(e.Summary!);
                if (rule.SearchDetail && !string.IsNullOrWhiteSpace(e.Detail)) selected.Add(e.Detail!);
                if (rule.SearchCast && !string.IsNullOrWhiteSpace(e.ExtendedItems)) selected.Add(e.ExtendedItems!);
                // 文字列解析 — Rules without explicit search fields match the title.
                if (selected.Count == 0) selected.Add(e.Title);
                var text = string.Join(" ", selected);
                if (!RuleTextMatch(text, rule.Pattern, rule.UseRegex))
                    continue;
                if (!string.IsNullOrWhiteSpace(rule.ExcludePattern)
                    && RuleTextMatch(text, rule.ExcludePattern, rule.UseRegex))
                    continue;
                return true;
            }
            return false;
        }

        double Knownness(AIrhythmRecommendation item)
        {
            if (knownnessByCandidate.TryGetValue(item, out var known)) return known;
            known = ComputeKnownness(item);
            knownnessByCandidate.Add(item, known);
            return known;
        }

        double ComputeKnownness(AIrhythmRecommendation item)
        {
            var work = item.SeriesKey;
            if (work.Length == 0) return 0.0d;
            if (recorded.Contains(work) || reserved.Contains(work)) return 1.0d;
            if (automatedWorks.Contains(work, StringComparer.OrdinalIgnoreCase)) return 1.0d;
            if (IsTrackedByKeywordRule(item)) return 1.0d;
            // 録画実績・復旧 — Fuzzy relations depend only on the canonical Work key and this render's captured
            // recording/reservation/interest population, never on an event.
            if (fuzzyKnownnessByWork.TryGetValue(work, out var related)) return related;
            related = WorkSetContains(recordedWorks, work) || WorkSetContains(reservedWorks, work)
                || WorkSetContains(automatedWorks, work) ? 0.90d
                : WorkSetContains(interestWorks, work) ? 0.85d : 0.0d;
            fuzzyKnownnessByWork.Add(work, related);
            return related;
        }

        var serviceCounts = preferenceReservations.Select(ServiceIdentityOf)
            .Concat(preferenceRecordings.Select(ServiceIdentityOf))
            .Where(x => x.IsValid)
            .GroupBy(x => x)
            .ToDictionary(x => x.Key, x => x.Count());
        var maxServiceCount = Math.Max(1, serviceCounts.Values.DefaultIfEmpty(0).Max());
        var genreCounts = preferenceRecordings.Select(x => NormalizeGenre(x.Genre))
            .Where(x => x.Length > 0)
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var maxGenreCount = Math.Max(1, genreCounts.Values.DefaultIfEmpty(0).Max());

        double ServiceNovelty(AIrhythmRecommendation item)
        {
            if (item.EventIdentity is null) return 0.50d;
            var service = ServiceIdentityOf(item.EventIdentity);
            if (!service.IsValid || !serviceCounts.TryGetValue(service, out var count)) return 1.0d;
            return Math.Clamp(1.0d - Math.Sqrt((double)count / maxServiceCount), 0.0d, 1.0d);
        }

        double GenreNovelty(AIrhythmRecommendation item)
        {
            var genre = NormalizeGenre(item.Genre);
            if (genre.Length == 0 || !genreCounts.TryGetValue(genre, out var count)) return 0.75d;
            return Math.Clamp(1.0d - Math.Sqrt((double)count / maxGenreCount), 0.0d, 1.0d);
        }

        double Plausibility(AIrhythmRecommendation item)
        {
            // 推薦・学習 — Discovery combines learned affinity with current evidence; rendering never teaches.
            var legacyEvidence = Math.Clamp((item.RawScore - 8.0d) / 62.0d, 0.0d, 1.0d);
            if (item.IsConvincing) legacyEvidence = Math.Max(legacyEvidence, 0.65d);
            else if (item.IsPlausibleDiscovery) legacyEvidence = Math.Max(legacyEvidence, 0.40d);
            var maturity = Math.Clamp(item.LearnedUserModelMaturity, 0.0d, 1.0d);
            return Math.Clamp(
                item.LearnedUserAffinity * maturity
                + legacyEvidence * (1.0d - maturity),
                0.0d,
                1.0d);
        }

        (double Knownness, double Novelty, double Plausibility) DiscoveryAxes(AIrhythmRecommendation item)
        {
            if (axesByCandidate.TryGetValue(item, out var axes)) return axes;
            var known = Knownness(item);
            var novelty = Math.Clamp(
                (1.0d - known) * 0.60d
                + ServiceNovelty(item) * 0.20d
                + GenreNovelty(item) * 0.20d,
                0.0d,
                1.0d);
            axes = (known, novelty, Plausibility(item));
            axesByCandidate.Add(item, axes);
            return axes;
        }

        // 録画実績・復旧 — Page-wide learned utility. Shelf semantics remain fixed (time window, replay identity,
        // recovery facts), while candidate ordering is learned from persisted behavior. The set-level selector
        // gives each shelf its own objective, and already-selected semantic neighbours reduce the marginal value
        // of another near-duplicate card. This is a generic Work/Genre/Terms/Service distance model, not a
        // title/category IF tree.
        double LearnedFit(AIrhythmRecommendation item)
        {
            var maturity = Math.Clamp(item.LearnedUserModelMaturity, 0.0d, 1.0d);
            var populationFit = Math.Clamp((item.DeviationRawScore + 2.5d) / 5.0d, 0.0d, 1.0d);
            return Math.Clamp(
                item.LearnedUserAffinity * maturity
                + populationFit * (1.0d - maturity),
                0.0d,
                1.0d);
        }

        double TonightObjective(AIrhythmRecommendation item)
        {
            var hours = Math.Max(0.0d, (item.Start - now).TotalHours);
            var immediacy = Math.Exp(-hours / 6.0d);
            return Math.Clamp(
                LearnedFit(item) * 0.70d
                + Plausibility(item) * 0.20d
                + immediacy * 0.10d,
                0.0d,
                1.0d);
        }

        double ReplayObjective(AIrhythmRecommendation item)
        {
            var axes = DiscoveryAxes(item);
            return Math.Clamp(
                LearnedFit(item) * 0.55d
                + axes.Plausibility * 0.25d
                + axes.Novelty * 0.20d,
                0.0d,
                1.0d);
        }

        double FirstSeenObjective(AIrhythmRecommendation item)
        {
            var axes = DiscoveryAxes(item);
            return Math.Clamp(
                axes.Plausibility * 0.55d
                + axes.Novelty * 0.30d
                + LearnedFit(item) * 0.15d,
                0.0d,
                1.0d);
        }

        double SurpriseObjective(AIrhythmRecommendation item)
        {
            var axes = DiscoveryAxes(item);
            var balancedDiscovery = Math.Min(axes.Novelty, axes.Plausibility);
            return Math.Clamp(
                balancedDiscovery * 0.50d
                + axes.Novelty * 0.25d
                + axes.Plausibility * 0.25d,
                0.0d,
                1.0d);
        }

        string SemanticCacheKey(AIrhythmRecommendation item)
            => item.EventIdentity is not null
                ? $"{item.EventIdentity.NetworkId}:{item.EventIdentity.TransportStreamId}:{item.EventIdentity.ServiceId}:{item.EventIdentity.EventNumber}:{item.EventIdentity.Start.UtcDateTime.Ticks}"
                : $"{item.SeriesKey}|{item.ServiceName}|{item.Start.UtcDateTime.Ticks}|{item.Title}";

        var semanticCache = new Dictionary<string, (string Work, string Genre, string Service, HashSet<string> Terms)>(StringComparer.Ordinal);
        (string Work, string Genre, string Service, HashSet<string> Terms) SemanticFeatures(AIrhythmRecommendation item)
        {
            var key = SemanticCacheKey(item);
            if (semanticCache.TryGetValue(key, out var cached))
                return cached;
            var features = (
                Work: item.SeriesKey ?? string.Empty,
                Genre: NormalizeGenre(item.Genre),
                Service: (item.ServiceName ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant(),
                Terms: Tokens($"{item.SeriesKey} {AiredTitleEvidenceText(item.Title)}").Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase));
            semanticCache[key] = features;
            return features;
        }

        double SemanticSimilarity(AIrhythmRecommendation left, AIrhythmRecommendation right)
        {
            if (ReferenceEquals(left, right)) return 1.0d;
            if (left.EventIdentity is not null && right.EventIdentity is not null && left.EventIdentity.Equals(right.EventIdentity))
                return 1.0d;

            var a = SemanticFeatures(left);
            var b = SemanticFeatures(right);
            var similarity = 0.0d;
            if (a.Work.Length > 0 && b.Work.Length > 0)
            {
                if (string.Equals(a.Work, b.Work, StringComparison.OrdinalIgnoreCase)) similarity += 0.58d;
                else if (WorkMatches(a.Work, b.Work)) similarity += 0.42d;
            }
            if (a.Genre.Length > 0 && b.Genre.Length > 0 && GenreMatches(a.Genre, b.Genre)) similarity += 0.16d;
            if (a.Service.Length > 0 && string.Equals(a.Service, b.Service, StringComparison.OrdinalIgnoreCase)) similarity += 0.08d;

            if (a.Terms.Count > 0 && b.Terms.Count > 0)
            {
                var intersection = a.Terms.Count <= b.Terms.Count
                    ? a.Terms.Count(b.Terms.Contains)
                    : b.Terms.Count(a.Terms.Contains);
                var union = a.Terms.Count + b.Terms.Count - intersection;
                if (union > 0) similarity += 0.30d * ((double)intersection / union);
            }
            return Math.Clamp(similarity, 0.0d, 1.0d);
        }

        bool SameEvent(AIrhythmRecommendation left, AIrhythmRecommendation right)
            => left.EventIdentity is not null && right.EventIdentity is not null && left.EventIdentity.Equals(right.EventIdentity);

        var pageLearnedSelections = new List<AIrhythmRecommendation>(24);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var setSelectionDiagnostics = new List<(string Shelf, AIrhythmRecommendation Item, double BaseUtility, double ShelfSimilarity, double PageSimilarity, double MarginalUtility)>();
#endif

        AIrhythmRecommendation[] SelectLearnedSet(
            string shelf,
            IEnumerable<AIrhythmRecommendation> source,
            Func<AIrhythmRecommendation, double> objective,
            int count)
        {
            var remaining = source
                .GroupBy(SemanticCacheKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            var selected = new List<AIrhythmRecommendation>(count);
            // 作品識別 — Objectives are pure within a render. Retain one result per candidate instead of recalculating
            // its Work/rule/novelty projection at every greedy round. Use the recommendation reference itself as the
            // key. An inferred ToDictionary key can widen to nullable object through the comparer, producing
            // CS8714/CS8621 and losing the declared model type.
            var baseUtilities = new Dictionary<AIrhythmRecommendation, double>(
                remaining.Count, ReferenceEqualityComparer.Instance);
            foreach (var candidate in remaining)
                baseUtilities.Add(candidate, objective(candidate));
            var shelfMaxSimilarities = new Dictionary<AIrhythmRecommendation, double>(ReferenceEqualityComparer.Instance);
            var pageMaxSimilarities = new Dictionary<AIrhythmRecommendation, double>(ReferenceEqualityComparer.Instance);
            foreach (var candidate in remaining)
            {
                var maximum = 0.0d;
                foreach (var previous in pageLearnedSelections)
                    maximum = Math.Max(maximum, SemanticSimilarity(candidate, previous));
                pageMaxSimilarities.Add(candidate, maximum);
            }
            var maturity = remaining.Select(x => x.LearnedUserModelMaturity).DefaultIfEmpty(0.0d).Average();
            // 推薦・学習 — More evidence allows the model to spend more of its utility budget on coverage instead of
            // repeatedly taking the nearest neighbour of an already-known preference cluster.
            var shelfRedundancyWeight = 0.20d + 0.25d * Math.Clamp(maturity, 0.0d, 1.0d);
            var pageRedundancyWeight = 0.08d + 0.17d * Math.Clamp(maturity, 0.0d, 1.0d);

            while (selected.Count < count && remaining.Count > 0)
            {
                var ranked = remaining
                    .Where(candidate => !pageLearnedSelections.Any(existing => SameEvent(existing, candidate)))
                    .Select(candidate =>
                    {
                        var baseUtility = baseUtilities[candidate];
                        var shelfSimilarity = shelfMaxSimilarities.TryGetValue(candidate, out var shelfMax) ? shelfMax : 0.0d;
                        var pageSimilarity = pageMaxSimilarities[candidate];
                        var marginal = baseUtility
                            * (1.0d - shelfRedundancyWeight * shelfSimilarity)
                            * (1.0d - pageRedundancyWeight * pageSimilarity);
                        return new { Candidate = candidate, BaseUtility = baseUtility, ShelfSimilarity = shelfSimilarity, PageSimilarity = pageSimilarity, Marginal = marginal };
                    })
                    .OrderByDescending(x => x.Marginal)
                    .ThenByDescending(x => x.BaseUtility)
                    .ThenByDescending(x => x.Candidate.Score)
                    .ThenBy(x => x.Candidate.Start)
                    .FirstOrDefault();
                if (ranked is null) break;
                selected.Add(ranked.Candidate);
                pageLearnedSelections.Add(ranked.Candidate);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                setSelectionDiagnostics.Add((shelf, ranked.Candidate, ranked.BaseUtility, ranked.ShelfSimilarity, ranked.PageSimilarity, ranked.Marginal));
#endif
                remaining.Remove(ranked.Candidate);
                // 処理条件 — The selected card joins both the shelf and the page. Update each remaining candidate's running
                // maximum once. Math.Max preserves the exact greedy similarity objective and original tie-break
                // ordering.
                foreach (var candidate in remaining)
                {
                    var similarity = SemanticSimilarity(candidate, ranked.Candidate);
                    if (!shelfMaxSimilarities.TryGetValue(candidate, out var previousShelf) || similarity > previousShelf)
                        shelfMaxSimilarities[candidate] = similarity;
                    if (similarity > pageMaxSimilarities[candidate])
                        pageMaxSimilarities[candidate] = similarity;
                }
            }
            return selected.ToArray();
        }

        string[] FirstSeenReasons(AIrhythmRecommendation item)
        {
            var reasons = new List<string> { "未録画・未予約・自動追跡外" };
            if (Plausibility(item) >= 0.55d) reasons.Add("好みとの接点が強い新規作品");
            else if (ServiceNovelty(item) >= 0.70d) reasons.Add("普段あまり選ばない局から発見");
            else reasons.Add("好みとの接点を残した新規候補");
            return reasons.ToArray();
        }

        string[] SurpriseReasons(AIrhythmRecommendation item)
        {
            var reasons = new List<string>();
            if (ServiceNovelty(item) >= 0.60d) reasons.Add("普段あまり選ばない局");
            if (GenreNovelty(item) >= 0.60d) reasons.Add("普段少ないジャンル");
            if (reasons.Count == 0) reasons.Add("普段とは違う作品傾向");
            reasons.Add("好みとの接点を残した発見");
            return reasons.Take(2).ToArray();
        }

        // 作品識別 — Discovery uses three independent coordinates: Knownness = whether the user already
        // follows/records/reserves this Work, Novelty = distance from familiar Work/service/genre, Plausibility
        // = local evidence that the candidate can still plausibly fit. These coordinates affect only discovery
        // shelf selection and never preference learning.
        var discoveryAxisRows = recommendations
            .Select(item => new { Item = item, Axes = DiscoveryAxes(item), RuleTracked = IsTrackedByKeywordRule(item) })
            .ToArray();

        // 処理条件 — Selection order is intentional: the imminent shelf claims the strongest current-context
        // candidates first, then replay/discovery shelves maximize additional information value instead of
        // repeating the same semantic neighbourhood elsewhere on the page.
        var tonightWindow = GetRecommendationTimeWindow(now, RecommendationTimeIntent.Tonight);
        var tonight = SelectLearnedSet(
            "tonight",
            recommendations.Where(x => tonightWindow.Contains(x.Start)),
            TonightObjective,
            6);

        var replayPool = recommendations.Where(x =>
        {
            var e = EventOf(x);
            var text = $"{x.Title} {e?.Summary} {e?.Detail}";
            var title = x.SeriesKey;
            return ContainsAny(text, "[再]", "【再】", "再放送", "アンコール", "一挙", "リピート")
                && !reserved.Contains(title) && !recorded.Contains(title);
        });
        var replayFinds = SelectLearnedSet("replay", replayPool, ReplayObjective, 6);

        // 推薦・学習 — Build() already applied the canonical promotional-only role to the recommendation input before
        // selecting any shelf. Recaps and replays remain eligible; the broader scored population remains
        // available for user search.
        var firstSeenPool = discoveryAxisRows
            .Where(x => x.Axes.Knownness <= 0.10d)
            .Where(x => x.Axes.Novelty >= 0.60d)
            .Where(x => x.Axes.Plausibility >= 0.20d)
            .Select(x => x.Item)
            .ToArray();
        var firstSeenItems = SelectLearnedSet("first_seen", firstSeenPool, FirstSeenObjective, 6);
        var firstSeen = firstSeenItems
            .Select(item => item with { Reasons = FirstSeenReasons(item) })
            .ToArray();
        var firstSeenIdentities = firstSeenItems
            .Select(x => x.EventIdentity)
            .Where(x => x is not null)
            .ToHashSet();

        var surprisePool = discoveryAxisRows
            .Where(x => x.Item.EventIdentity is null || !firstSeenIdentities.Contains(x.Item.EventIdentity))
            .Where(x => x.Axes.Knownness <= 0.45d)
            .Where(x => x.Axes.Novelty >= 0.58d)
            .Where(x => x.Axes.Plausibility >= 0.28d)
            .Select(x => x.Item)
            .ToArray();
        var surpriseItems = SelectLearnedSet("surprise", surprisePool, SurpriseObjective, 6);
        var surprise = surpriseItems
            .Select(item => item with { Reasons = SurpriseReasons(item) })
            .ToArray();

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var ruleTrackedCount = discoveryAxisRows.Count(x => x.RuleTracked);
        var highKnownCount = discoveryAxisRows.Count(x => x.Axes.Knownness >= 0.85d);
        AIrhythmDataState.WriteDeveloperLog(
            $"DISCOVERY_MODEL_SUMMARY candidates={discoveryAxisRows.Length} keywordRules={snapshot.KeywordRules.Count} ruleTracked={ruleTrackedCount} highKnown={highKnownCount} firstSeen={firstSeen.Length} surprise={surprise.Length} axes=knownness+novelty+learned_plausibility userModel=behavior_persisted_maturity_blend selector=set_level_semantic_diversity feedbackToPreference=False renderingDoesNotTeach=True layoutMutation=False");
        foreach (var item in firstSeenItems.Concat(surpriseItems).Take(12))
        {
            var axes = DiscoveryAxes(item);
            AIrhythmDataState.WriteDeveloperLog(
                $"DISCOVERY_MODEL_SAMPLE shelf={(firstSeenItems.Contains(item) ? "first_seen" : "surprise")} work={item.SeriesKey} knownness={axes.Knownness:0.000} novelty={axes.Novelty:0.000} plausibility={axes.Plausibility:0.000} learnedAffinity={item.LearnedUserAffinity:0.000} modelAdjustment={item.LearnedUserModelAdjustment:0.000} modelMaturity={item.LearnedUserModelMaturity:0.000} serviceNovelty={ServiceNovelty(item):0.000} genreNovelty={GenreNovelty(item):0.000} ruleTracked={IsTrackedByKeywordRule(item)} score={item.Score} rawScore={item.RawScore}");
        }

        // 保存・読込 — Evaluate the actual 24 shelf selections, not a second synthetic candidate ranking.
        // Developer-only: never changes selection, preferences, or persistent storage.
        var qualityShelves = new (string Name, IReadOnlyList<AIrhythmRecommendation> Items)[]
        {
            ("tonight", tonight), ("replay", replayFinds),
            ("first_seen", firstSeen), ("surprise", surprise)
        };
        var qualityTotal = 0;
        var qualityWeakPlausibility = 0;
        var qualityNoReasons = 0;
        var qualityRepeatedWorks = 0;
        var qualitySeenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var qualityWeakReasons = new List<string>();
        foreach (var group in qualityShelves)
        {
            var groupBelowThreshold = 0;
            foreach (var item in group.Items)
            {
                var axes = DiscoveryAxes(item);
                var minimumPlausibility = group.Name == "first_seen" ? 0.20d : group.Name == "surprise" ? 0.28d : 0.0d;
                var belowGate = minimumPlausibility > 0.0d && axes.Plausibility + 0.0000001d < minimumPlausibility;
                if (belowGate) { qualityWeakPlausibility++; groupBelowThreshold++; }
                if (item.Reasons.Count == 0) qualityNoReasons++;
                if (!string.IsNullOrWhiteSpace(item.SeriesKey) && !qualitySeenKeys.Add(item.SeriesKey)) qualityRepeatedWorks++;
                qualityTotal++;
                // 処理条件 — The real card reasons are reported alongside the model axes so a reviewer can distinguish
                // explainable behavior from merely high novelty or generic fallback text.
                AIrhythmDataState.WriteDeveloperLog(
                    $"RECOMMENDATION_QUALITY_SAMPLE shelf={group.Name} station={item.ServiceName} title={item.Title} start={item.Start:yyyy-MM-ddTHH:mm:sszzz} work={item.SeriesKey} knownness={axes.Knownness:0.000} novelty={axes.Novelty:0.000} plausibility={axes.Plausibility:0.000} affinity={item.LearnedUserAffinity:0.000} score={item.Score} reasons=[{string.Join(" / ", item.Reasons.Take(2))}] discoveryBelowGate={belowGate} observationOnly=True");
                if (belowGate && qualityWeakReasons.Count < 6)
                    qualityWeakReasons.Add($"{group.Name}:{item.ServiceName}/{item.Title}");
            }
            AIrhythmDataState.WriteDeveloperLog(
                $"RECOMMENDATION_QUALITY_SHELF shelf={group.Name} selected={group.Items.Count} belowPlausibilityGate={groupBelowThreshold} model=real_selected_cards observationOnly=True");
        }
        AIrhythmDataState.WriteDeveloperLog(
            $"RECOMMENDATION_QUALITY_SUMMARY selected={qualityTotal} uniqueWorks={qualitySeenKeys.Count} repeatedWorks={qualityRepeatedWorks} missingCardReasons={qualityNoReasons} discoveryBelowPlausibilityGate={qualityWeakPlausibility} weakSamples=[{string.Join(";", qualityWeakReasons)}] criteria=actual_selection_reason_trace_and_discovery_guardrails outcome=requires_human_relevance_review scoreMutation=False storageMutation=False");
        var learnedPage = pageLearnedSelections.ToArray();
        var pairSimilarities = new List<double>();
        for (var i = 0; i < learnedPage.Length; i++)
        for (var j = i + 1; j < learnedPage.Length; j++)
            pairSimilarities.Add(SemanticSimilarity(learnedPage[i], learnedPage[j]));
        var uniquePageWorks = learnedPage.Select(x => x.SeriesKey).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var uniquePageGenres = learnedPage.Select(x => NormalizeGenre(x.Genre)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var uniquePageServices = learnedPage.Select(x => x.ServiceName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        AIrhythmDataState.WriteDeveloperLog(
            $"AI_PAGE_SET_MODEL_SUMMARY selected={learnedPage.Length} uniqueWorks={uniquePageWorks} uniqueGenres={uniquePageGenres} uniqueServices={uniquePageServices} meanPairSimilarity={pairSimilarities.DefaultIfEmpty(0.0d).Average():0.000} maxPairSimilarity={pairSimilarities.DefaultIfEmpty(0.0d).Max():0.000} meanLearnedAffinity={learnedPage.Select(x => x.LearnedUserAffinity).DefaultIfEmpty(0.0d).Average():0.000} objective=shelf_specific_learned_utility_x_semantic_marginal_value features=work+genre+terms+service modelMaturity={learnedPage.Select(x => x.LearnedUserModelMaturity).DefaultIfEmpty(0.0d).Max():0.000} renderingDoesNotTeach=True titleSpecificRules=False");
        foreach (var row in setSelectionDiagnostics)
            AIrhythmDataState.WriteDeveloperLog(
                $"AI_PAGE_SET_MODEL_SAMPLE shelf={row.Shelf} work={row.Item.SeriesKey} baseUtility={row.BaseUtility:0.000} shelfSimilarity={row.ShelfSimilarity:0.000} pageSimilarity={row.PageSimilarity:0.000} marginalUtility={row.MarginalUtility:0.000} learnedAffinity={row.Item.LearnedUserAffinity:0.000} maturity={row.Item.LearnedUserModelMaturity:0.000}");

        AIrhythmDataState.WriteDeveloperLog(
            $"AI_PAGE_SHELF_MODEL_SUMMARY activeOwner=factual_runtime tonightOwner=learned_set_model recoveryOwner=factual_recording_recovery replayOwner=learned_set_model firstSeenOwner=learned_set_model surpriseOwner=learned_set_model modelObservations={recommendations.Select(x => x.LearnedUserModelMaturity).DefaultIfEmpty(0.0d).Max():0.000} shelfObjectives=tonight_context,replay_value,first_seen_discovery,surprise_balance pageDiversity=True renderingDoesNotTeach=True titleSpecificRules=False layoutMutation=False");
        foreach (var item in tonight.Take(6))
            AIrhythmDataState.WriteDeveloperLog($"AI_PAGE_SHELF_MODEL_SAMPLE shelf=tonight work={item.SeriesKey} learnedFit={LearnedFit(item):0.000} shelfUtility={TonightObjective(item):0.000} learnedAffinity={item.LearnedUserAffinity:0.000} modelAdjustment={item.LearnedUserModelAdjustment:0.000} maturity={item.LearnedUserModelMaturity:0.000}");
        foreach (var item in replayFinds.Take(6))
            AIrhythmDataState.WriteDeveloperLog($"AI_PAGE_SHELF_MODEL_SAMPLE shelf=replay work={item.SeriesKey} learnedFit={LearnedFit(item):0.000} shelfUtility={ReplayObjective(item):0.000} learnedAffinity={item.LearnedUserAffinity:0.000} modelAdjustment={item.LearnedUserModelAdjustment:0.000} maturity={item.LearnedUserModelMaturity:0.000}");
#endif
        string Shelf(string css, string icon, string title, string subtitle, IReadOnlyList<AIrhythmRecommendation> items, bool showScore = false)
        {
            if (items.Count == 0)
                return $"<section class='discovery-shelf discovery-shelf-empty {css}'><div class='shelf-title'><span class='shelf-accent' aria-hidden='true'></span><div><h3>{AIrhythmHtml.Encode(title)}</h3><p>{AIrhythmHtml.Encode(subtitle)}</p></div></div><div class='shelf-empty'>現在候補はありません</div></section>";
            var cards = string.Join(string.Empty, items.Select((x, index) =>
            {
                var scoreHtml = showScore ? $"<b>{x.Score}</b>" : string.Empty;
                return $"<article class='spark-card'><div class='spark-rank'>{index + 1}</div><div class='spark-body'>{ProgramTitleElement("strong", x.Title)}<span>{AIrhythmHtml.Encode(x.ServiceName)}・{x.Start:MM/dd HH:mm}</span><p>{AIrhythmHtml.Encode(x.Reasons.Count > 0 ? string.Join("・", x.Reasons.Take(2)) : "あなたの傾向から発見")}</p>{ReserveButton(context, x, snapshot, true)}</div>{scoreHtml}</article>";
            }));
            return $"<section class='discovery-shelf {css}'><div class='shelf-title'><span class='shelf-accent' aria-hidden='true'></span><div><h3>{AIrhythmHtml.Encode(title)}</h3><p>{AIrhythmHtml.Encode(subtitle)}</p></div></div><div class='spark-grid'>{cards}</div></section>";
        }

        var active = snapshot.Advanced.ActiveRecordings
            .Select(x => new AIrhythmRecommendation(x.ProgramTitle, x.ServiceName, string.Empty, "録画中", x.Start, 0, new[] { $"{x.TunerName}で録画中", $"終了予定 {x.ScheduledEnd:HH:mm}" }, CanonicalWorkKey(x.ProgramTitle, new AIrhythmServiceIdentity(x.NetworkId, x.TransportStreamId, x.ServiceId), identityContext), null))
            .Take(6).ToArray();

        var qualityIssues = snapshot.RecoveryHistory
            .Where(x => x.ResultFinalized)
            .Where(x => IsRecordingFailure(x)
                || (x.QualityDataAvailable && ((x.DropCount ?? 0) > 0 || (x.ErrorCount ?? 0) > 0 || (x.ScrambleCount ?? 0) > 0)))
            .Select(x => new
            {
                SeriesKey = CanonicalWorkKey(x, identityContext),
                Severity = IsRecordingFailure(x) ? long.MaxValue / 4 : QualitySeverity(x),
                Failure = IsRecordingFailure(x),
                x.ProgramTitle
            })
            .Where(x => x.SeriesKey.Length > 0)
            .ToArray();

        // 録画実績・復旧 — 取り直し候補は推薦スコア・嗜好学習とは別の録画リカバリー表示。 録画失敗または品質異常の事実と再放送候補の一致だけで提示する。
        var recovery = allRecommendations
            .Select(x => new
            {
                Item = x,
                Match = qualityIssues
                    .Where(q => WorkMatches(q.SeriesKey, x.SeriesKey))
                    .OrderByDescending(q => string.Equals(q.SeriesKey, x.SeriesKey, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(q => q.Severity)
                    .FirstOrDefault()
            })
            .Where(x => x.Match is not null)
            .Where(x =>
            {
                var text = $"{x.Item.Title} {EventOf(x.Item)?.Summary} {EventOf(x.Item)?.Detail}";
                return ContainsAny(text, "[再]", "【再】", "再放送", "アンコール", "リピート", "一挙");
            })
            .OrderByDescending(x => string.Equals(x.Match!.SeriesKey, x.Item.SeriesKey, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Match!.Severity)
            .ThenByDescending(x => x.Item.Score)
            .ThenBy(x => x.Item.Start)
            .Select(x => x.Item with { Reasons = x.Match!.Failure
                ? new[] { "録画できなかった番組", "同じ番組の再放送候補" }
                : new[] { "録画品質に問題があった番組", "同じ番組の再放送候補" } })
            .Take(6)
            .ToArray();

        return string.Concat(
            "<div class='discovery-group-heading'><strong>今</strong><span>録画中のもの、今夜の候補</span></div>",
            Shelf("shelf-active", "REC", "いま録画中", "現在進行中の録画セッション", active),
            Shelf("shelf-tonight", "✦", "今夜のおすすめ", "夜6時から翌朝4時までの候補", tonight),
            "<div class='discovery-group-heading'><strong>もう一度</strong><span>再放送や、録画し直せる候補</span></div>",
            Shelf("shelf-recovery", "FIX", "取り直し候補", "録画できなかった、または品質情報に問題があった番組", recovery),
            Shelf("shelf-replay", "↻", "再放送を見つける", "未予約・未録画の再放送候補", replayFinds),
            "<div class='discovery-group-heading'><strong>発見</strong><span>まだ録画していない作品や、いつもと少し違う候補</span></div>",
            Shelf("shelf-first-seen", "NEW", "はじめて見る作品", "未録画・未予約・自動追跡外から見つけた候補", firstSeen),
            Shelf("shelf-surprise", "!", "いつもと違う発見", "普段と違うが好みとの接点がある候補", surprise));
    }


}
