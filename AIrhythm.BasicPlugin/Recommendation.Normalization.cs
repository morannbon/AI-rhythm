namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmRecommendationEngine
{
    private static readonly IReadOnlyDictionary<string, string> ContributorRoleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["出演"] = "cast", ["出演者"] = "cast", ["キャスト"] = "cast", ["主演"] = "cast",
        ["声の出演"] = "voice", ["声優"] = "voice",
        ["司会"] = "host", ["mc"] = "host", ["ＭＣ"] = "host", ["ゲスト"] = "guest",
        ["解説"] = "commentator", ["実況"] = "commentator",
        ["原作"] = "author", ["原作者"] = "author",
        ["監督"] = "director", ["脚本"] = "writer", ["演出"] = "director",
        ["プロデューサー"] = "producer", ["制作"] = "production", ["製作"] = "production",
        ["音楽"] = "music", ["アーティスト"] = "artist", ["歌"] = "artist"
    };
    private static readonly Regex ContributorMetadataRegex = new(
        $@"(?:^|[\r\n;；])\s*(?<role>{string.Join("|", ContributorRoleMap.Keys.OrderByDescending(x => x.Length).Select(Regex.Escape))})\s*[:：]\s*(?<value>[^\r\n;；]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ContributorNameSplitRegex = new(@"\s*(?:、|,|，|/|／|＆|&)\s*", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ContributorTrailingNoteRegex = new(@"\s*[（(][^）)]{0,24}[）)]\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ContributorNestedRolePrefixRegex = new(
        $@"^\s*(?:{string.Join("|", ContributorRoleMap.Keys.OrderByDescending(x => x.Length).Select(Regex.Escape))})\s*[:：]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> ContributorNonEntityTokens = new(
        ContributorRoleMap.Keys.Concat(new[] { "MC", "ＭＣ", "出演", "出演者", "キャスト", "主演", "声の出演", "声優", "司会", "ゲスト", "解説", "実況", "原作", "原作者", "監督", "脚本", "演出", "プロデューサー", "制作", "製作", "音楽", "アーティスト", "歌", "スケーター", "選手", "アナウンサー", "ほか", "他", "ほか多数", "その他" }),
        StringComparer.OrdinalIgnoreCase);
    // 作品識別 — Precompute identity-normalized role labels once, in the original longest-first order.
    // Contributor normalization runs for every candidate; sorting and Unicode normalization on each credit
    // allocates without adding any evidence.
    private static readonly string[] ContributorCompactRoleLabels = ContributorRoleMap.Keys
        .OrderByDescending(x => x.Length)
        .Select(ContributorEntityKey)
        .ToArray();
    private static readonly HashSet<string> ContributorCompactNonEntityTokens = new(
        ContributorNonEntityTokens.Select(ContributorEntityKey), StringComparer.OrdinalIgnoreCase);
    private static readonly object ScoreResultCacheGate = new();
    private static AIrhythmRuntimeSnapshot? CachedScoreSnapshot;
    private static long CachedScoreExternalEvidenceGeneration = -1;
    private static AIrhythmRecommendation[]? CachedScoreRecommendations;
    private static Dictionary<AIrhythmCalibrationEventKey, AIrhythmEvidenceCalibrationCandidate> CachedCalibrationCandidates = new();
    private static readonly Dictionary<AIrhythmCalibrationEventKey, (AIrhythmEvidenceCalibrationCandidate Candidate, DateTimeOffset CapturedAt)> PendingPreChoiceCalibrationCandidates = new();
    private static readonly TimeSpan PendingPreChoiceCalibrationLifetime = TimeSpan.FromMinutes(2);

    // 録画実績・復旧 — 録画履歴から作る正規化済みの特徴量は、番組表更新や予約変更では内容が変わらない。 現在の履歴と完全一致する1世代だけを保持し、タイトル分解・ジャンル正規化・シリーズ正規化の
    // 再実行を避ける。履歴が変われば全件照合で検出して置換するため、古い世代は蓄積しない。
    private static readonly object HistoryEvidenceGate = new();
    private static AIrhythmHistoryEvidenceSignature[] CachedHistoryEvidenceSignatures = Array.Empty<AIrhythmHistoryEvidenceSignature>();
    private static AIrhythmHistoryEvidenceItem[] CachedHistoryEvidence = Array.Empty<AIrhythmHistoryEvidenceItem>();
    private static string CachedHistoryEvidenceIdentityFingerprint = string.Empty;

    private readonly record struct AIrhythmHistoryEvidenceSignature(
        long StartUtcTicks,
        ushort NetworkId,
        ushort TransportStreamId,
        ushort ServiceId,
        string ReservationId,
        string RecordingId,
        long ScheduledStartUtcTicks,
        string ProgramTitle,
        string Genre);

    private sealed record AIrhythmHistoryEvidenceItem(
        DateTimeOffset Start,
        AIrhythmServiceIdentity ServiceIdentity,
        int Hour,
        string ReservationId,
        string CausalIdentity,
        string ProgramTitle,
        string SeriesKey,
        string GenreKey,
        IReadOnlyList<string> Terms);

    private static IReadOnlyList<AIrhythmHistoryEvidenceItem> GetHistoryEvidence(IReadOnlyList<TvAirRecordingHistoryDto> history, AIrhythmEvidenceIdentityContext identityContext)
    {
        var identityFingerprint = BuildEvidenceIdentityFingerprint(identityContext);
        lock (HistoryEvidenceGate)
        {
            var previousCount = CachedHistoryEvidence.Length;
            var hadCache = CachedHistoryEvidenceSignatures.Length > 0 || CachedHistoryEvidence.Length > 0;
            var missReason = hadCache ? "history_count_changed" : "initial";

            if (string.Equals(CachedHistoryEvidenceIdentityFingerprint, identityFingerprint, StringComparison.Ordinal)
                && CachedHistoryEvidenceSignatures.Length == history.Count
                && CachedHistoryEvidence.Length == history.Count)
            {
                var unchanged = true;
                for (var i = 0; i < history.Count; i++)
                {
                    if (!HistoryEvidenceMatches(CachedHistoryEvidenceSignatures[i], history[i]))
                    {
                        unchanged = false;
                        missReason = "history_changed";
                        break;
                    }
                }
                if (unchanged)
                {
                    return CachedHistoryEvidence;
                }
            }

            var signatures = new AIrhythmHistoryEvidenceSignature[history.Count];
            var evidence = new AIrhythmHistoryEvidenceItem[history.Count];
            for (var i = 0; i < history.Count; i++)
            {
                var item = history[i];
                var start = item.ActualStart ?? item.Start;
                signatures[i] = HistoryEvidenceSignatureOf(item, start);
                var facts = CanonicalEvidenceFacts(item, identityContext);
                evidence[i] = new AIrhythmHistoryEvidenceItem(
                    start,
                    facts.ServiceIdentity,
                    facts.Hour,
                    item.ReservationId ?? string.Empty,
                    AIrhythmDataState.RecordingEvidenceCausalIdentity(item),
                    item.ProgramTitle ?? string.Empty,
                    facts.WorkKey,
                    facts.GenreKey,
                    facts.Terms);
            }

            CachedHistoryEvidenceSignatures = signatures;
            CachedHistoryEvidence = evidence;
            CachedHistoryEvidenceIdentityFingerprint = identityFingerprint;
            return CachedHistoryEvidence;
        }
    }

    private static AIrhythmHistoryEvidenceSignature HistoryEvidenceSignatureOf(TvAirRecordingHistoryDto item, DateTimeOffset start)
        => new(
            start.UtcDateTime.Ticks,
            item.NetworkId,
            item.TransportStreamId,
            item.ServiceId,
            item.ReservationId ?? string.Empty,
            item.RecordingId ?? string.Empty,
            item.ScheduledStartTime.UtcDateTime.Ticks,
            item.ProgramTitle ?? string.Empty,
            item.Genre ?? string.Empty);

    private static bool HistoryEvidenceMatches(AIrhythmHistoryEvidenceSignature signature, TvAirRecordingHistoryDto item)
    {
        var start = item.ActualStart ?? item.Start;
        return signature.StartUtcTicks == start.UtcDateTime.Ticks
            && signature.NetworkId == item.NetworkId
            && signature.TransportStreamId == item.TransportStreamId
            && signature.ServiceId == item.ServiceId
            && string.Equals(signature.ReservationId, item.ReservationId ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(signature.RecordingId, item.RecordingId ?? string.Empty, StringComparison.Ordinal)
            && signature.ScheduledStartUtcTicks == item.ScheduledStartTime.UtcDateTime.Ticks
            && string.Equals(signature.ProgramTitle, item.ProgramTitle ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(signature.Genre, item.Genre ?? string.Empty, StringComparison.Ordinal);
    }

    private static string ProgramTitleElement(string tagName, string title, string? cssClass = null, int scrollThreshold = 18)
    {
        var safeTag = string.Equals(tagName, "span", StringComparison.OrdinalIgnoreCase) ? "span"
            : string.Equals(tagName, "b", StringComparison.OrdinalIgnoreCase) ? "b"
            : "strong";
        var encoded = AIrhythmHtml.Encode(title);
        var displayLength = Math.Max(0, title.EnumerateRunes().Count());
        var threshold = Math.Max(8, scrollThreshold);

        // 文字列解析 — Retain short titles byte-for-byte equivalent in structure to the release baseline. Long titles
        // use one canonical stable hover owner. Only the absolutely-positioned motion layer moves; the owner
        // never moves, so hover cannot invalidate itself.
        if (displayLength <= threshold)
        {
            var plainClass = string.IsNullOrWhiteSpace(cssClass) ? string.Empty : $" class=\"{AIrhythmHtml.Encode(cssClass)}\"";
            return $"<{safeTag}{plainClass}>{encoded}</{safeTag}>";
        }

        var distanceClass = displayLength >= threshold * 2
            ? " airhythm-title-scroll-far"
            : displayLength >= threshold * 3 / 2
                ? " airhythm-title-scroll-medium"
                : string.Empty;
        var classes = string.Join(" ", new[] { cssClass, "airhythm-title-scroll-owner" }.Where(x => !string.IsNullOrWhiteSpace(x))) + distanceClass;
        return $"<{safeTag} class=\"{AIrhythmHtml.Encode(classes)}\"><em class=\"airhythm-title-static\">{encoded}</em><em class=\"airhythm-title-motion\" aria-hidden=\"true\">{encoded}</em></{safeTag}>";
    }

    public static AIrhythmServerRender Build(RuntimeUiRenderContext context, AIrhythmRuntimeSnapshot snapshot, string? rawRhythmQuery)
    {
        // 作品識別 — The learner may promote canonical aliases. Build only the post-learner identity projection: the
        // pre-learner projection had no consumer except a diagnostic line.
        var normalizationLearnerShadow = RunNormalizationLearnerShadow(snapshot);
        var identityContext = BuildEvidenceIdentityContext(snapshot);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog($"COMMON_EVIDENCE_CANONICAL_CONTEXT numericServices={identityContext.NumericLocalValuesByService.Count} numericEvidenceServices={identityContext.NumericEvidenceValuesByService.Count} workAliases={identityContext.WorkAliases.Count} externalCanonicalTitles={identityContext.ExternalCanonicalTitles.Count} normalizationPromotions={identityContext.NormalizationPromotions.Count} consumers=score,localEvidence,selection,dashboard,discovery,search,interest,externalUserIntent workIdentity=canonical_single_source genre=NormalizeGenre terms=Tokens service=ServiceIdentity hour=canonical_hour timeBand=CanonicalTimeBand");
#endif
        var allRecommendations = GetOrBuildScoreResult(snapshot, identityContext);
        // 推薦・学習 — A promotional EPG event must remain searchable and recordable by the user, but it is not a
        // substantive programme recommendation. Apply the canonical derivative role *before* the common
        // selection for every recommendation shelf. No per-shelf PR rule and no loss of manually searchable
        // programme events.
        var recommendationEligible = allRecommendations
            .Where(item => ClassifyDerivedProgramRelation(item.Title) != AIrhythmDerivedProgramRelation.Promo)
            .ToArray();
        var recommendations = SelectRecommendations(recommendationEligible, Math.Clamp(snapshot.Settings.Limit, 10, 30), identityContext);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        LogNormalizationLearnerDisplayedRecognition(recommendations, normalizationLearnerShadow);
#endif
        var recommendationDiscoveryPool = recommendationEligible
            .Where(x => x.IsConvincing || x.IsPlausibleDiscovery)
            .ToArray();
        var cards = string.Join(string.Empty, recommendations.Select(item => RenderCard(context, item, snapshot)));
        var charts = BuildCharts(snapshot, identityContext);
        var discovery = BuildDiscovery(context, snapshot, recommendationEligible, recommendationDiscoveryPool, identityContext);
        var search = BuildRhythmSearch(context, snapshot, allRecommendations, rawRhythmQuery, identityContext);
        var message = recommendations.Length > 0
            ? "<div id=\"message\" class=\"message\" hidden></div>"
            : $"<div id=\"message\" class=\"message\">{AIrhythmHtml.Encode(snapshot.Ready ? "条件に合う候補がありません。" : snapshot.Error)}</div>";
        var status = snapshot.Ready
            ? $"おすすめ候補 {recommendationEligible.Length}件・{DateTimeOffset.Now:HH:mm} 更新"
            : AIrhythmHtml.Encode(snapshot.Error);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog($"CANONICAL_FACT_CACHE hits={identityContext.CanonicalFactsHits} misses={identityContext.CanonicalFactsMisses} programCached={identityContext.ProgramFactsCount} reservationCached={identityContext.ReservationFactsCount} historyCached={identityContext.HistoryFactsCount} scope=single_render_snapshot semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog($"LOCAL_WORK_KEY_CACHE hits={identityContext.LocalWorkKeyCacheHits} misses={identityContext.LocalWorkKeyCacheMisses} cached={identityContext.LocalWorkKeys.Count} scope=single_render_snapshot key=title_shared_except_trailing_parenthesis_service_scope semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog($"LOCAL_WORK_KEY_ALLOCATION_DETAIL canonicalHits={identityContext.CanonicalLocalWorkKeyHitCalls} canonicalHitBytes={identityContext.CanonicalLocalWorkKeyHitBytes} canonicalMisses={identityContext.CanonicalLocalWorkKeyMissCalls} canonicalMissBytes={identityContext.CanonicalLocalWorkKeyMissBytes} canonicalTotalBytes={identityContext.CanonicalLocalWorkKeyHitBytes + identityContext.CanonicalLocalWorkKeyMissBytes} scope=canonical_facts_across_render_snapshot allocationScope=current_thread_exact measurement=existing_outer_probe_no_extra_gc_probe cacheBranch=miss_counter_delta semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog($"LOCAL_EPISODE_IDENTITY_CACHE hits={identityContext.LocalEpisodeIdentityCacheHits} misses={identityContext.LocalEpisodeIdentityCacheMisses} cached={identityContext.LocalEpisodeIdentities.Count} scope=single_render_snapshot key=title_plus_service negativeResultsCached=True continuityTimeComparisonCached=False semantics=unchanged");
#endif
        return new(cards, charts, discovery, search, message, status);
    }

    private enum NormalizationLearnerProposalKind
    {
        Prefix,
        RecurringCore,
        NumericStableVariant
    }

    private sealed record NormalizationLearnerShadowModel(
        HashSet<string> ObservedComparableTitles,
        NormalizationLearnerShadowRow[] Rows);

    private sealed record NormalizationLearnerShadowRow(
        NormalizationLearnerProposalKind Kind,
        string LearnedWork,
        int Occurrences,
        int DistinctLeftContexts,
        int DistinctRightContexts,
        int NumericRightSlotVariants,
        double MeanCoverage,
        double BoundaryQuality,
        double Confidence,
        string RelationToCurrent,
        string CurrentWorkSample,
        string RawSample,
        double RoleFitness = 0.0d,
        int TemporalObservationDays = 0,
        int DistinctCanonicalWorks = 0,
        string Decision = "generated",
        int HypothesisObservationDays = 0,
        int HypothesisValidatedDays = 0,
        int HypothesisDeferredDays = 0,
        int HypothesisRejectedDays = 0,
        double HypothesisStability = 0.0d,
        string HypothesisStage = "new",
        double PopulationQuality = 0.0d,
        double CollisionRate = 0.0d,
        double NumericBoundaryResidueRate = 0.0d,
        double BoundaryIntrusionScore = 0.0d,
        double LearnedBoundaryTokenScore = 0.0d,
        int LearnedBoundaryLeftFamilies = 0,
        double BoundaryRoleContrast = 0.0d,
        double ShorterChallengerSupport = 0.0d,
        string QualityIssue = "none",
        int LongTermObservationDays = 0,
        double LongTermReliability = 0.0d,
        double LongTermConsistency = 0.0d,
        double LongTermMeanPopulationQuality = 0.0d,
        double LongTermMeanCollisionRate = 0.0d,
        double LongTermMeanBoundaryIntrusion = 0.0d,
        string LongTermStage = "collecting",
        double ExternalStructuralSupport = 0.0d,
        double ExternalProviderReliability = 0.0d,
        int ExternalProviderCount = 0,
        int ExternalEvidenceCount = 0,
        string ExternalStructuralStage = "none",
        double LongTermMeanExternalSupport = 0.0d,
        double LongTermMeanExternalProviderReliability = 0.0d);

    private readonly record struct NormalizationLearnerCoreCandidate(
        string Core,
        int Occurrences,
        int DistinctLeftContexts,
        int DistinctRightContexts,
        int NumericRightSlotVariants,
        double MeanCoverage,
        double BoundaryQuality,
        double Confidence,
        string RawSample);

    // 録画実績・復旧 — Normalization learner v12. CanonicalWorkKey remains the single production Work identity
    // source; only strict, multi-day consensus aliases promoted by this learner may enter that source. v11
    // introduced reuse of already-authorized External Lookup results as persistent structural evidence for
    // long-term learning. It performs no additional network request: canonical titles/aliases already
    // obtained by the manual refresh path are persisted with provider/verdict provenance, matched against
    // learned Work hypotheses, and accumulated as a separate external-support feature. Provider reliability
    // is learned from supported/conflicting evidence history. External support can modestly strengthen
    // long-term reliability but never changes the current decision, canonical identity, score, evidence
    // vector, selection, or lookup schedule by itself. v10 introduces a persistent long-term feature model.
    // It stores one structural feature observation per hypothesis per distinct day
    // (quality/collision/boundary/role/confidence) and learns temporal mean, drift and consistency without
    // programme-specific labels. It intentionally does not change current decisions; accumulated evidence is
    // exposed as a separate reliability layer for later promotion gating. v9 adds multi-view role contrast:
    // corpus boundary evidence must compete with exact identity agreement and independently generated
    // shorter challengers before it can be treated as terminal-token intrusion. No programme/token
    // dictionary is used; disagreement is measured between learner views and corpus behavior. v8 makes
    // corpus boundary learning directional: a terminal token is considered boundary-like only when it
    // generalizes across multiple independent left-side title families, instead of recurring many times
    // inside one programme family. This keeps the learner data-driven while separating "stable Work |
    // variable episode" from "Work+boundary-token | variable episode". v7 replaced the coarse "numeric
    // suffix means boundary residue" heuristic with a corpus-learned contrastive boundary model. The model
    // learns short terminal tokens that are disproportionately observed immediately before variable numeric
    // slots across the whole corpus, then uses that learned evidence to decide whether a proposal has
    // intruded into the right-side slot. No token/title dictionary is embedded here; the token scores are
    // rebuilt from the observed corpus on every evaluation. The learner combines three data-derived proposal
    // views, then validates their Work-role plausibility from corpus behavior and cross-day memory: (1)
    // repeated prefix families, and (2) recurring cores that may appear after changing prefixes/containers,
    // and (3) stable numeric variants whose numeric value repeats across multiple changing suffixes. The
    // third view lets the learner distinguish a stable numbered edition/season-like identity from a one-off
    // episode number without any title dictionary or semantic keyword list. Candidate boundaries are snapped
    // away from partial numeric/alphanumeric runs, and repeated right-side numeric variation remains
    // structural evidence instead of being blindly absorbed into Work. This is deliberately not a title
    // dictionary and has no programme/person/station-specific rules. Source-derived proposals and pure
    // corpus assessment may be reused only while the full title set and per-title observed-day counts are
    // identical. New daily evidence forces reassessment; external evidence, long-term memory, hypothesis
    // survival and promotions are always read/applied on each render.
    private sealed record NormalizationStructuralCandidates(
        string[] Source,
        NormalizationLearnerShadowRow[] Prefix,
        NormalizationLearnerShadowRow[] Recurring,
        NormalizationLearnerShadowRow[] Numeric,
        NormalizationLearnerShadowRow[] Combined);
    private static readonly object NormalizationStructuralCandidateGate = new();
    private static NormalizationStructuralCandidates? _normalizationStructuralCandidates;
    // 保存・読込 — Assessment depends on the exact corpus AND the number of observed days for each title. It does
    // not depend on external evidence, long-term/hypothesis storage or current promotions. Those mutable
    // inputs are always fetched and applied *after* assessment. One verified generation only; never cache
    // the resulting long-term decisions.
    private sealed record NormalizationAssessedCorpus(
        NormalizationStructuralCandidates Structural,
        Dictionary<string, int> ObservationDays,
        NormalizationLearnerShadowRow[] Rows);
    private static NormalizationAssessedCorpus? _normalizationAssessedCorpus;

    internal static void ClearNormalizationStructuralCandidates()
    {
        lock (NormalizationStructuralCandidateGate)
        {
            _normalizationStructuralCandidates = null;
            _normalizationAssessedCorpus = null;
        }
    }

    private static NormalizationStructuralCandidates GetNormalizationStructuralCandidates(string[] source)
    {
        NormalizationStructuralCandidates? cached = null;
        lock (NormalizationStructuralCandidateGate)
        {
            var candidate = _normalizationStructuralCandidates;
            if (candidate is not null && source.SequenceEqual(candidate.Source, StringComparer.Ordinal))
                cached = candidate;
        }
        if (cached is not null)
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            AIrhythmDataState.WriteDeveloperLog($"NORMALIZATION_STRUCTURAL_CACHE result=HIT titles={source.Length} proposals={cached.Combined.Length} scope=exact_sorted_source lifetime=single_generation");
#endif
            return cached;
        }

        var prefix = BuildNormalizationPrefixShadowRows(source).ToArray();
        var recurring = BuildNormalizationRecurringCoreShadowRows(source).ToArray();
        var numeric = BuildNormalizationNumericStableShadowRows(source).ToArray();
        var result = new NormalizationStructuralCandidates(
            source, prefix, recurring, numeric, prefix.Concat(recurring).Concat(numeric).ToArray());
        if (source.Length <= 50000)
        {
            lock (NormalizationStructuralCandidateGate)
                _normalizationStructuralCandidates = result;
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog($"NORMALIZATION_STRUCTURAL_CACHE result=MISS titles={source.Length} proposals={result.Combined.Length} scope=exact_sorted_source lifetime=single_generation");
#endif
        return result;
    }

    private static NormalizationLearnerShadowRow[] GetOrAssessNormalizationLearnerRows(
        NormalizationStructuralCandidates structural,
        IReadOnlyDictionary<string, int> observationDays)
    {
        lock (NormalizationStructuralCandidateGate)
        {
            var cached = _normalizationAssessedCorpus;
            if (cached is not null && ReferenceEquals(cached.Structural, structural)
                && cached.ObservationDays.Count == observationDays.Count)
            {
                var sameDays = true;
                foreach (var (title, days) in observationDays)
                {
                    if (!cached.ObservationDays.TryGetValue(title, out var previous) || previous != days)
                    {
                        sameDays = false;
                        break;
                    }
                }
                if (sameDays)
                {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                    AIrhythmDataState.WriteDeveloperLog($"NORMALIZATION_ASSESSMENT_CACHE result=HIT titles={structural.Source.Length} hypotheses={cached.Rows.Length} days={observationDays.Count} check=exact_all_values");
#endif
                    return cached.Rows;
                }
            }
        }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var assessed = AssessNormalizationLearnerRows(structural.Combined, structural.Source, observationDays);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog($"NORMALIZATION_LEARNER_ASSESSMENT elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} hypotheses={structural.Combined.Length} sourceTitles={structural.Source.Length} outputRows={assessed.Length} semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog($"NORMALIZATION_ASSESSMENT_CACHE result=MISS titles={structural.Source.Length} hypotheses={assessed.Length} days={observationDays.Count} check=exact_all_values");
#endif
        if (structural.Source.Length <= 50000)
        {
            var copy = new Dictionary<string, int>(observationDays.Count, StringComparer.Ordinal);
            foreach (var (title, days) in observationDays)
                copy[title] = days;
            lock (NormalizationStructuralCandidateGate)
                _normalizationAssessedCorpus = new NormalizationAssessedCorpus(structural, copy, assessed);
        }
        return assessed;
    }

    private static NormalizationLearnerShadowModel RunNormalizationLearnerShadow(AIrhythmRuntimeSnapshot snapshot)
    {
        var currentSource = snapshot.Events
            .Select(x => x.Title ?? string.Empty)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizationLearnerComparableTitle)
            .Where(x => x.Length >= 6)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var observationDays = AIrhythmDataState.ObserveNormalizationLearnerTitles(currentSource);
        var source = currentSource
            .Concat(observationDays.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        if (source.Length < 2)
        {
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            AIrhythmDataState.WriteDeveloperLog($"NORMALIZATION_LEARNER_SHADOW result=NO_DATA titles={source.Length} productionMutation=False persistence=False");
            #endif
            return new NormalizationLearnerShadowModel(currentSource.ToHashSet(StringComparer.Ordinal), Array.Empty<NormalizationLearnerShadowRow>());
        }

        var structural = GetNormalizationStructuralCandidates(source);
        var prefixRows = structural.Prefix;
        var coreRows = structural.Recurring;
        var numericStableRows = structural.Numeric;
        var generatedRows = structural.Combined;
        var assessedRows = GetOrAssessNormalizationLearnerRows(structural, observationDays);
        var externalStructuralHistory = AIrhythmDataState.GetNormalizationLearnerExternalStructuralSupport(
            assessedRows.Select(x => (HypothesisKey: $"{x.Kind}:{x.LearnedWork}", LearnedWork: x.LearnedWork)));
        var externallyAssessedRows = ApplyNormalizationLearnerExternalStructuralSupport(assessedRows, externalStructuralHistory);
        var longTermHistory = AIrhythmDataState.ObserveNormalizationLearnerLongTermFeatures(
            externallyAssessedRows.Select(x => new NormalizationLearnerLongTermSignal(
                $"{x.Kind}:{x.LearnedWork}", x.Kind.ToString(), x.LearnedWork, x.Confidence, x.RoleFitness,
                x.PopulationQuality, x.CollisionRate, x.BoundaryIntrusionScore, x.RelationToCurrent, x.QualityIssue,
                x.ExternalStructuralSupport, x.ExternalProviderReliability, x.ExternalProviderCount, x.ExternalEvidenceCount)));
        var longTermRows = ApplyNormalizationLearnerLongTermHistory(externallyAssessedRows, longTermHistory);
        var hypothesisHistory = AIrhythmDataState.ObserveNormalizationLearnerHypotheses(
            longTermRows.Select(x => new NormalizationLearnerHypothesisSignal(
                $"{x.Kind}:{x.LearnedWork}", x.Kind.ToString(), x.LearnedWork, x.Decision,
                x.Confidence, x.RoleFitness, x.RelationToCurrent, x.DistinctCanonicalWorks)));
        var rows = ApplyNormalizationLearnerHypothesisHistory(longTermRows, hypothesisHistory);

        var selected = rows
            .OrderByDescending(x => string.Equals(x.Decision, "validated", StringComparison.Ordinal))
            .ThenByDescending(x => x.Confidence)
            .ThenByDescending(x => x.Occurrences)
            .ThenByDescending(x => x.BoundaryQuality)
            .ThenBy(x => x.LearnedWork.Length)
            .Aggregate(new List<NormalizationLearnerShadowRow>(), (list, row) =>
            {
                if (list.Count >= 32)
                    return list;

                // 推薦・学習 — Suppress only near-identical diagnostics of the same proposal kind. Retain a prefix and
                // recurring-core proposal side by side when they disagree; that disagreement is useful structural
                // evidence for the next learner stage.
                if (list.Any(x => x.Kind == row.Kind
                    && (row.LearnedWork.StartsWith(x.LearnedWork, StringComparison.Ordinal)
                        || x.LearnedWork.StartsWith(row.LearnedWork, StringComparison.Ordinal))
                    && Math.Abs(row.LearnedWork.Length - x.LearnedWork.Length) <= 2))
                    return list;

                list.Add(row);
                return list;
            });

        var relationGroups = rows
            .GroupBy(x => x.RelationToCurrent, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key}:{x.Count()}")
            .ToArray();
        var highConfidenceConflicts = rows.Count(x => x.Confidence >= 0.85d && string.Equals(x.RelationToCurrent, "conflict", StringComparison.Ordinal));
        var numericSlotRows = rows.Count(x => x.NumericRightSlotVariants >= 2);
        var validatedRows = rows.Count(x => string.Equals(x.Decision, "validated", StringComparison.Ordinal));
        var rejectedRows = rows.Count(x => x.Decision.StartsWith("rejected_", StringComparison.Ordinal));
        var deferredRows = rows.Length - validatedRows - rejectedRows;
        var historicalOnlyTitles = Math.Max(0, source.Length - currentSource.Length);
        var maxObservationDays = observationDays.Count == 0 ? 0 : observationDays.Values.Max();
        var survivorHypotheses = rows.Count(x => string.Equals(x.HypothesisStage, "survivor", StringComparison.Ordinal));
        var contestedHypotheses = rows.Count(x => string.Equals(x.HypothesisStage, "contested", StringComparison.Ordinal));
        var collectingHypotheses = rows.Count(x => string.Equals(x.HypothesisStage, "collecting", StringComparison.Ordinal));
        var persistentRejectedHypotheses = rows.Count(x => string.Equals(x.HypothesisStage, "persistent_reject", StringComparison.Ordinal));
        var maxHypothesisDays = rows.Length == 0 ? 0 : rows.Max(x => x.HypothesisObservationDays);
        var meanPopulationQuality = rows.Length == 0 ? 0.0d : rows.Average(x => x.PopulationQuality);
        var qualityRiskRows = rows.Count(x => !string.Equals(x.QualityIssue, "none", StringComparison.Ordinal));
        var validatedQualityRiskRows = rows.Count(x => string.Equals(x.Decision, "validated", StringComparison.Ordinal)
            && !string.Equals(x.QualityIssue, "none", StringComparison.Ordinal));
        var qualityGroups = rows
            .GroupBy(x => x.QualityIssue, StringComparer.Ordinal)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key}:{x.Count()}")
            .ToArray();
        var longTermTracked = rows.Count(x => x.LongTermObservationDays > 0);
        var maxLongTermDays = rows.Length == 0 ? 0 : rows.Max(x => x.LongTermObservationDays);
        var meanLongTermReliability = longTermTracked == 0 ? 0.0d : rows.Where(x => x.LongTermObservationDays > 0).Average(x => x.LongTermReliability);
        var longTermStable = rows.Count(x => string.Equals(x.LongTermStage, "stable", StringComparison.Ordinal));
        var longTermDrifting = rows.Count(x => string.Equals(x.LongTermStage, "drifting", StringComparison.Ordinal));

        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog(
            $"NORMALIZATION_LEARNER_QUALITY_SUMMARY result=OBSERVED rows={rows.Length} meanQuality={meanPopulationQuality:0.000} riskRows={qualityRiskRows} validatedRiskRows={validatedQualityRiskRows} issueGroups=[{string.Join(",", qualityGroups)}] evaluator=population_structure_self_assessment feedback=roleFitness+confidence+decision hypothesisFeedback=True titleSpecificTracking=False productionMutation=False canonicalMutation=False scoreMutation=False evidenceMutation=False");
        #endif

        foreach (var row in rows
            .Where(x => !string.Equals(x.QualityIssue, "none", StringComparison.Ordinal))
            .OrderByDescending(x => x.Confidence)
            .ThenBy(x => x.PopulationQuality)
            .Take(24))
        {
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            AIrhythmDataState.WriteDeveloperLog(
                $"NORMALIZATION_LEARNER_QUALITY_SAMPLE issue={row.QualityIssue} kind={row.Kind} learnedWork={row.LearnedWork} populationQuality={row.PopulationQuality:0.000} collisionRate={row.CollisionRate:0.000} numericSuffixRate={row.NumericBoundaryResidueRate:0.000} boundaryIntrusionScore={row.BoundaryIntrusionScore:0.000} learnedBoundaryTokenScore={row.LearnedBoundaryTokenScore:0.000} boundaryLeftFamilies={row.LearnedBoundaryLeftFamilies} boundaryRoleContrast={row.BoundaryRoleContrast:0.000} shorterChallengerSupport={row.ShorterChallengerSupport:0.000} externalStructuralSupport={row.ExternalStructuralSupport:0.000} externalProviderReliability={row.ExternalProviderReliability:0.000} externalProviders={row.ExternalProviderCount} externalEvidence={row.ExternalEvidenceCount} externalStage={row.ExternalStructuralStage} longTermDays={row.LongTermObservationDays} longTermReliability={row.LongTermReliability:0.000} longTermConsistency={row.LongTermConsistency:0.000} longTermStage={row.LongTermStage} decision={row.Decision} relationToCurrent={row.RelationToCurrent} currentWorkSample={row.CurrentWorkSample} rawSample={row.RawSample} sampling=population_risk_top24_not_title_watch action=observe_only");
            #endif
        }

        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog(
            $"NORMALIZATION_LEARNER_SHADOW result=OBSERVED currentTitles={currentSource.Length} memoryTitles={observationDays.Count} historicalOnlyTitles={historicalOnlyTitles} titles={source.Length} prefixAccepted={prefixRows.Length} recurringCoreAccepted={coreRows.Length} numericStableAccepted={numericStableRows.Length} combined={rows.Length} validated={validatedRows} deferred={deferredRows} rejected={rejectedRows} reported={selected.Count} algorithm=ensemble_prefix_recurring_core_numeric_stable_population_quality_hypothesis_history temporalModel=distinct_observation_days hypothesisModel=multi_day_survival slotModel=variable_left_right_numeric boundaryModel=run_safe+corpus_learned_multiview_role_contrast longTermModel=persistent_daily_feature_statistics_plus_persisted_external_structural_support_no_decision_mutation confidence=structural_x_role_fitness_x_population_quality relationSummary=[{string.Join(",", relationGroups)}] numericSlotRows={numericSlotRows} highConfidenceConflicts={highConfidenceConflicts} maxObservationDays={maxObservationDays} hypothesisSurvivors={survivorHypotheses} hypothesisContested={contestedHypotheses} hypothesisCollecting={collectingHypotheses} hypothesisPersistentReject={persistentRejectedHypotheses} maxHypothesisDays={maxHypothesisDays} longTermTracked={longTermTracked} maxLongTermDays={maxLongTermDays} meanLongTermReliability={meanLongTermReliability:0.000} longTermStable={longTermStable} longTermDrifting={longTermDrifting} productionMutation=strict_promotion_only canonicalMutation=eligible_aliases_only scoreMutation=via_canonical_identity evidenceMutation=via_canonical_identity persistence=learner_plus_promotion_state");
        #endif

        foreach (var row in selected)
        {
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            AIrhythmDataState.WriteDeveloperLog(
                $"NORMALIZATION_LEARNER_CANDIDATE kind={row.Kind} learnedWork={row.LearnedWork} occurrences={row.Occurrences} leftContexts={row.DistinctLeftContexts} rightContexts={row.DistinctRightContexts} numericRightSlotVariants={row.NumericRightSlotVariants} meanCoverage={row.MeanCoverage:0.000} boundaryQuality={row.BoundaryQuality:0.000} roleFitness={row.RoleFitness:0.000} populationQuality={row.PopulationQuality:0.000} collisionRate={row.CollisionRate:0.000} numericSuffixRate={row.NumericBoundaryResidueRate:0.000} boundaryIntrusionScore={row.BoundaryIntrusionScore:0.000} learnedBoundaryTokenScore={row.LearnedBoundaryTokenScore:0.000} boundaryLeftFamilies={row.LearnedBoundaryLeftFamilies} boundaryRoleContrast={row.BoundaryRoleContrast:0.000} shorterChallengerSupport={row.ShorterChallengerSupport:0.000} qualityIssue={row.QualityIssue} longTermDays={row.LongTermObservationDays} longTermReliability={row.LongTermReliability:0.000} longTermConsistency={row.LongTermConsistency:0.000} longTermMeanQuality={row.LongTermMeanPopulationQuality:0.000} longTermMeanCollision={row.LongTermMeanCollisionRate:0.000} longTermMeanBoundaryIntrusion={row.LongTermMeanBoundaryIntrusion:0.000} longTermMeanExternalSupport={row.LongTermMeanExternalSupport:0.000} longTermMeanExternalProviderReliability={row.LongTermMeanExternalProviderReliability:0.000} longTermStage={row.LongTermStage} externalStructuralSupport={row.ExternalStructuralSupport:0.000} externalProviderReliability={row.ExternalProviderReliability:0.000} externalProviders={row.ExternalProviderCount} externalEvidence={row.ExternalEvidenceCount} externalStage={row.ExternalStructuralStage} temporalDays={row.TemporalObservationDays} distinctCanonicalWorks={row.DistinctCanonicalWorks} confidence={row.Confidence:0.000} decision={row.Decision} hypothesisDays={row.HypothesisObservationDays} hypothesisValidatedDays={row.HypothesisValidatedDays} hypothesisDeferredDays={row.HypothesisDeferredDays} hypothesisRejectedDays={row.HypothesisRejectedDays} hypothesisStability={row.HypothesisStability:0.000} hypothesisStage={row.HypothesisStage} relationToCurrent={row.RelationToCurrent} currentWorkSample={row.CurrentWorkSample} rawSample={row.RawSample} action={(IsNormalizationPromotionEligible(row) ? "promotion_eligible" : "observe_only")}");
            #endif
        }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var promotionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var promotionSignals = BuildNormalizationPromotionSignals(source, rows);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog($"NORMALIZATION_LEARNER_PROMOTION_INDEX elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(promotionStarted).TotalMilliseconds:0} sourceTitles={source.Length} eligible={rows.Count(IsNormalizationPromotionEligible)} signals={promotionSignals.Count} semantics=unchanged");
#endif
        AIrhythmDataState.UpdateNormalizationPromotions(promotionSignals);
        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog($"NORMALIZATION_PROMOTION result=EVALUATED signals={promotionSignals.Count} gate=validated+survivor+longterm_stable+quality_clean+collision_safe+consensus productionMutation=True canonicalMutation=True titleSpecificRules=False");
        #endif

        return new NormalizationLearnerShadowModel(currentSource.ToHashSet(StringComparer.Ordinal), rows);
    }

    private static bool IsNormalizationPromotionEligible(NormalizationLearnerShadowRow row)
        => string.Equals(row.Decision, "validated", StringComparison.Ordinal)
            && string.Equals(row.HypothesisStage, "survivor", StringComparison.Ordinal)
            && string.Equals(row.LongTermStage, "stable", StringComparison.Ordinal)
            && string.Equals(row.QualityIssue, "none", StringComparison.Ordinal)
            && !string.Equals(row.RelationToCurrent, "conflict", StringComparison.Ordinal)
            && row.Confidence >= 0.92d
            && row.RoleFitness >= 0.88d
            && row.PopulationQuality >= 0.88d
            && row.CollisionRate <= 0.01d
            && row.BoundaryIntrusionScore <= 0.05d
            && row.HypothesisObservationDays >= 5
            && row.HypothesisRejectedDays == 0
            && row.HypothesisStability >= 0.78d
            && row.LongTermObservationDays >= 5
            && row.LongTermReliability >= 0.82d
            && row.LongTermConsistency >= 0.80d;

    private sealed class NormalizationPromotionPrefixNode
    {
        public Dictionary<char, NormalizationPromotionPrefixNode> Children { get; } = new();
        public List<(int Ordinal, string LearnedKey, NormalizationLearnerShadowRow Row)> Rows { get; } = new();
    }

    private static IReadOnlyList<AIrhythmNormalizationPromotionSignal> BuildNormalizationPromotionSignals(
        IReadOnlyList<string> source,
        IReadOnlyList<NormalizationLearnerShadowRow> rows)
    {
        var eligible = rows.Where(IsNormalizationPromotionEligible).ToArray();
        if (eligible.Length == 0)
            return Array.Empty<AIrhythmNormalizationPromotionSignal>();

        // 文字列解析 — Index eligible canonical prefixes once. A title follows only its own character path instead of
        // checking every learner hypothesis. Matching rows are sorted back to the original eligible order, which
        // preserves stable tie-breaking.
        var prefixes = new NormalizationPromotionPrefixNode();
        for (var ordinal = 0; ordinal < eligible.Length; ordinal++)
        {
            var learnedKey = CompactIdentity(eligible[ordinal].LearnedWork);
            if (learnedKey.Length == 0)
                continue;
            var node = prefixes;
            foreach (var letter in learnedKey)
            {
                if (!node.Children.TryGetValue(letter, out var child))
                    node.Children[letter] = child = new NormalizationPromotionPrefixNode();
                node = child;
            }
            node.Rows.Add((ordinal, learnedKey, eligible[ordinal]));
        }

        var proposals = new Dictionary<string, List<NormalizationLearnerShadowRow>>(StringComparer.Ordinal);
        foreach (var title in source)
        {
            var aliasKey = CompactIdentity(title);
            if (aliasKey.Length == 0)
                continue;

            var candidates = new List<(int Ordinal, string LearnedKey, NormalizationLearnerShadowRow Row)>();
            var node = prefixes;
            foreach (var letter in aliasKey)
            {
                if (!node.Children.TryGetValue(letter, out var child))
                    break;
                node = child;
                candidates.AddRange(node.Rows);
            }
            if (candidates.Count == 0)
                continue;

            // 作品識別 — An anchored prefix must not swallow a distinct longer Work without an independently parsed
            // episode/content boundary. This is the existing gate.
            var parsed = ParseCanonicalTitleParts(title);
            var localParsedWork = CompactIdentity(parsed.Work);
            var recognizedBoundary = !string.IsNullOrWhiteSpace(parsed.Episode)
                || !string.IsNullOrWhiteSpace(parsed.Part)
                || !string.IsNullOrWhiteSpace(parsed.Subtitle)
                || !string.IsNullOrWhiteSpace(parsed.Event)
                || !string.IsNullOrWhiteSpace(parsed.Stage)
                || !string.IsNullOrWhiteSpace(parsed.BroadcastAttribute);
            var matching = candidates
                .Where(candidate => localParsedWork.Length == 0
                    || string.Equals(localParsedWork, candidate.LearnedKey, StringComparison.Ordinal)
                    || recognizedBoundary)
                .OrderBy(candidate => candidate.Ordinal)
                .Select(candidate => candidate.Row)
                .ToList();
            if (matching.Count > 0)
                proposals[aliasKey] = matching;
        }

        var signals = new List<AIrhythmNormalizationPromotionSignal>();
        foreach (var pair in proposals)
        {
            var works = pair.Value
                .Select(x => x.LearnedWork)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (works.Length != 1)
                continue;
            var row = pair.Value
                .Where(x => string.Equals(x.LearnedWork, works[0], StringComparison.Ordinal))
                .OrderByDescending(x => x.Confidence)
                .ThenByDescending(x => x.LongTermReliability)
                .First();
            signals.Add(new AIrhythmNormalizationPromotionSignal(
                pair.Key, row.LearnedWork, $"{row.Kind}:{row.LearnedWork}", row.Confidence,
                row.HypothesisObservationDays, row.LongTermObservationDays));
        }

        // 作品識別 — A learned Work must explain at least two exact observed title variants before any alias is
        // promoted.
        var supportedWorks = signals
            .GroupBy(x => x.LearnedWork, StringComparer.Ordinal)
            .Where(g => g.Select(x => x.AliasKey).Distinct(StringComparer.Ordinal).Count() >= 2)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        return signals.Where(x => supportedWorks.Contains(x.LearnedWork)).ToArray();
    }

    private static void LogNormalizationLearnerDisplayedRecognition(
        IReadOnlyList<AIrhythmRecommendation> recommendations,
        NormalizationLearnerShadowModel model)
    {
        var limit = Math.Min(24, recommendations.Count);
        var observed = 0;
        var proposal = 0;
        var deferred = 0;

        for (var i = 0; i < limit; i++)
        {
            var item = recommendations[i];
            var comparable = NormalizationLearnerComparableTitle(item.Title ?? string.Empty);
            var compact = CompactIdentity(comparable);
            var wasObserved = model.ObservedComparableTitles.Contains(comparable);
            if (wasObserved) observed++;

            var matches = model.Rows
                .Where(row => compact.StartsWith(row.LearnedWork, StringComparison.Ordinal)
                    || compact.Contains(row.LearnedWork, StringComparison.Ordinal))
                .OrderByDescending(row => string.Equals(row.Decision, "validated", StringComparison.Ordinal))
                .ThenBy(row => row.Decision.StartsWith("rejected_", StringComparison.Ordinal))
                .ThenByDescending(row => row.Confidence)
                .ThenByDescending(row => row.LearnedWork.Length)
                .ToArray();

            var best = matches.FirstOrDefault();
            var hasProposal = best is not null && !string.IsNullOrWhiteSpace(best.LearnedWork);
            if (hasProposal) proposal++; else deferred++;

            var state = !wasObserved ? "not_observed"
                : !hasProposal ? "deferred"
                : string.Equals(best!.Decision, "validated", StringComparison.Ordinal) ? "validated"
                : best.Decision.StartsWith("rejected_", StringComparison.Ordinal) ? "proposal_rejected"
                : "proposal_deferred";
            var reason = !wasObserved ? "not_in_current_learning_population"
                : !hasProposal ? "insufficient_recurrence_or_stable_structure"
                : best!.Decision;
            var learned = hasProposal ? best!.LearnedWork : "-";
            var confidence = hasProposal ? best!.Confidence.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "-";
            var roleFitness = hasProposal ? best!.RoleFitness.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "-";
            var temporalDays = hasProposal ? best!.TemporalObservationDays.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
            var hypothesisDays = hasProposal ? best!.HypothesisObservationDays.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
            var hypothesisStability = hasProposal ? best!.HypothesisStability.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "-";
            var hypothesisStage = hasProposal ? best!.HypothesisStage : "-";
            var kind = hasProposal ? best!.Kind.ToString() : "-";

            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            AIrhythmDataState.WriteDeveloperLog(
                $"NORMALIZATION_LEARNER_RECOGNITION rank={i + 1} observed={wasObserved} state={state} reason={reason} proposalKind={kind} learnedWork={learned} confidence={confidence} roleFitness={roleFitness} temporalDays={temporalDays} hypothesisDays={hypothesisDays} hypothesisStability={hypothesisStability} hypothesisStage={hypothesisStage} currentWork={item.SeriesKey} rawTitle={item.Title} action=observe_or_promote");
            #endif
        }

        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog(
            $"NORMALIZATION_LEARNER_RECOGNITION_SUMMARY displayed={limit} observed={observed} proposalGenerated={proposal} deferred={deferred} purpose=distinguish_not_observed_from_data_insufficient productionMutation=strict_promotion_only canonicalMutation=eligible_aliases_only scoreMutation=via_canonical_identity evidenceMutation=via_canonical_identity persistence=learner_plus_promotion_state");
        #endif
    }

    // 文字列解析 — The learner's source is sorted with StringComparer.Ordinal. A hypothesis prefix therefore
    // occupies one contiguous range. Locate that range instead of scanning the full historical title corpus
    // once per prefix. This preserves the exact members and original sort order.
    private static int NormalizationPrefixLowerBound(string[] sortedTitles, string prefix)
    {
        var low = 0;
        var high = sortedTitles.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (string.CompareOrdinal(sortedTitles[middle], prefix) < 0)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static List<NormalizationLearnerShadowRow> BuildNormalizationPrefixShadowRows(string[] source)
    {
        var proposed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < source.Length; i++)
        {
            var prefixLength = CommonPrefixLength(source[i - 1], source[i]);
            if (prefixLength < 4)
                continue;

            var rawPrefix = source[i][..prefixLength];
            rawPrefix = TrimPartialTrailingNumericRunIfNeeded(rawPrefix, new[]
            {
                (Title: source[i - 1], Index: 0),
                (Title: source[i], Index: 0)
            });
            var prefix = SnapLearnerBoundary(rawPrefix);
            if (prefix.Length >= 4)
                proposed.Add(prefix);
        }

        var rows = new List<NormalizationLearnerShadowRow>();
        foreach (var prefix in proposed)
        {
            var start = NormalizationPrefixLowerBound(source, prefix);
            var end = start;
            while (end < source.Length && source[end].StartsWith(prefix, StringComparison.Ordinal))
                end++;
            if (end - start < 3)
                continue;
            var matches = source[start..end];
            if (matches.Length < 3)
                continue;

            var rightContexts = matches
                .Select(x => x[prefix.Length..].Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (rightContexts.Length < 2)
                continue;

            var meanCoverage = matches.Average(x => (double)prefix.Length / Math.Max(1, x.Length));
            if (meanCoverage < 0.16d || meanCoverage > 0.92d)
                continue;

            var boundaryQuality = LearnerBoundaryQuality(matches, prefix, matchIndex: 0);
            var numericVariants = CountLeadingNumericSlotVariants(rightContexts);
            var recurrence = Math.Min(1.0d, Math.Log2(matches.Length + 1) / 4.0d);
            var diversity = Math.Min(1.0d, Math.Log2(rightContexts.Length + 1) / 3.0d);
            var coverage = 1.0d - Math.Min(1.0d, Math.Abs(meanCoverage - 0.55d) / 0.55d);
            var slotSupport = Math.Min(1.0d, numericVariants / 4.0d);
            var confidence = recurrence * 0.30d + diversity * 0.25d + coverage * 0.15d + boundaryQuality * 0.20d + slotSupport * 0.10d;
            if (confidence < 0.58d)
                continue;

            var rawSample = matches.OrderBy(x => x.Length).First();
            AddNormalizationLearnerRow(rows, NormalizationLearnerProposalKind.Prefix, prefix, matches.Length, 1,
                rightContexts.Length, numericVariants, meanCoverage, boundaryQuality, confidence, rawSample);
        }
        return rows;
    }

    private static List<NormalizationLearnerShadowRow> BuildNormalizationNumericStableShadowRows(string[] source)
    {
        // 作品識別 — Discover a numeric value that behaves like part of a stable identity strictly from recurrence:
        // the same lexical base + same number must occur repeatedly while text after that number varies. A
        // one-off episode number therefore does not qualify merely because it is numeric.
        var groups = new Dictionary<(string Base, string Number), List<(string Title, string Suffix)>>( );

        foreach (var title in source)
        {
            for (var i = 1; i < title.Length; i++)
            {
                if (!char.IsDigit(title[i]) || char.IsDigit(title[i - 1]))
                    continue;

                var end = i + 1;
                while (end < title.Length && char.IsDigit(title[end]))
                    end++;
                if (end >= title.Length)
                    continue;

                var rawBase = title[..i];
                var basePart = SnapLearnerBoundary(rawBase);
                if (basePart.Length < 4)
                    continue;

                var number = title[i..end];
                var suffix = title[end..].Trim();
                if (suffix.Length == 0)
                    continue;

                var key = (basePart, number);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<(string Title, string Suffix)>();
                    groups[key] = list;
                }
                list.Add((title, suffix));
            }
        }

        var baseVariantCounts = groups.Keys
            .GroupBy(x => x.Base, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(v => v.Number).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);

        var rows = new List<NormalizationLearnerShadowRow>();
        foreach (var pair in groups)
        {
            var occurrences = pair.Value.Select(x => x.Title).Distinct(StringComparer.Ordinal).Count();
            var rightContexts = pair.Value.Select(x => x.Suffix).Distinct(StringComparer.Ordinal).ToArray();
            if (occurrences < 3 || rightContexts.Length < 2)
                continue;

            var candidate = pair.Key.Base + pair.Key.Number;
            var meanCoverage = pair.Value.Average(x => (double)candidate.Length / Math.Max(1, x.Title.Length));
            if (meanCoverage < 0.18d || meanCoverage > 0.92d)
                continue;

            var boundaryQuality = pair.Value.Average(x => LearnerBoundaryQuality(x.Title, 0, candidate.Length));
            var recurrence = Math.Min(1.0d, Math.Log2(occurrences + 1) / 3.5d);
            var diversity = Math.Min(1.0d, Math.Log2(rightContexts.Length + 1) / 3.0d);
            var siblingNumericSupport = Math.Min(1.0d, baseVariantCounts[pair.Key.Base] / 3.0d);
            var coverage = 1.0d - Math.Min(1.0d, Math.Abs(meanCoverage - 0.55d) / 0.55d);
            var confidence = recurrence * 0.30d + diversity * 0.25d + boundaryQuality * 0.20d
                + siblingNumericSupport * 0.15d + coverage * 0.10d;
            if (confidence < 0.62d)
                continue;

            AddNormalizationLearnerRow(rows, NormalizationLearnerProposalKind.NumericStableVariant, candidate,
                occurrences, 1, rightContexts.Length, baseVariantCounts[pair.Key.Base], meanCoverage,
                boundaryQuality, confidence, pair.Value.OrderBy(x => x.Title.Length).First().Title);
        }

        return rows;
    }

    // 録画実績・復旧 — The learner's six UTF-16 code units are a retrieval key, never a persisted Work identity.
    // Represent them by value while building postings: allocating a substring for every overlapping window
    // of every EPG/history title dominated the corpus pass. Retain UTF-16 ordinal equality, encounter order
    // and the original posting cap.
    private readonly record struct NormalizationSixUnitKey(ulong FirstFour, uint LastTwo)
    {
        internal static NormalizationSixUnitKey From(ReadOnlySpan<char> units)
            => new(NormalizationCoreShingleKey(units), ((uint)units[4] << 16) | units[5]);

        internal string ToText()
        {
            Span<char> units = stackalloc char[6];
            units[0] = (char)(FirstFour >> 48);
            units[1] = (char)(FirstFour >> 32);
            units[2] = (char)(FirstFour >> 16);
            units[3] = (char)FirstFour;
            units[4] = (char)(LastTwo >> 16);
            units[5] = (char)LastTwo;
            return new string(units);
        }
    }

    private static List<NormalizationLearnerShadowRow> BuildNormalizationRecurringCoreShadowRows(string[] source)
    {
        // 作品識別 — Six-character shingles are only a retrieval index. They are expanded to the exact common
        // recurring core for each title family, so the learned identity is not a fixed-length n-gram.
        const int shingleLength = 6;
        const int maxPostingsPerShingle = 96;
        var postings = new Dictionary<NormalizationSixUnitKey, List<int>>();

        for (var index = 0; index < source.Length; index++)
        {
            var title = source[index];
            if (title.Length < shingleLength)
                continue;

            var seen = new HashSet<NormalizationSixUnitKey>();
            for (var i = 0; i <= title.Length - shingleLength; i++)
            {
                var shingle = title.AsSpan(i, shingleLength);
                var key = NormalizationSixUnitKey.From(shingle);
                if (!seen.Add(key))
                    continue;
                var allDigits = true;
                var allWhitespace = true;
                foreach (var unit in shingle)
                {
                    allDigits &= char.IsDigit(unit);
                    allWhitespace &= char.IsWhiteSpace(unit);
                }
                if (allDigits || allWhitespace)
                    continue;

                if (!postings.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    postings[key] = list;
                }
                if (list.Count < maxPostingsPerShingle)
                    list.Add(index);
            }
        }

        var dedupe = new Dictionary<string, NormalizationLearnerCoreCandidate>(StringComparer.Ordinal);
        foreach (var pair in postings)
        {
            var indexes = pair.Value;
            if (indexes.Count < 3 || indexes.Count >= maxPostingsPerShingle)
                continue;

            var matches = indexes.Select(i => source[i]).ToArray();
            var expanded = ExpandRecurringCore(matches, pair.Key.ToText());
            expanded = TrimPartialTrailingNumericRunIfNeeded(expanded, matches
                .Select(title => (Title: title, Index: title.IndexOf(expanded, StringComparison.Ordinal)))
                .Where(x => x.Index >= 0));
            var core = SnapLearnerBoundary(expanded);
            if (core.Length < 5)
                continue;

            var contexts = CollectCoreContexts(matches, core);
            if (contexts.Count < 3)
                continue;

            var distinctLeft = contexts.Select(x => x.Left).Distinct(StringComparer.Ordinal).Count();
            var distinctRight = contexts.Select(x => x.Right).Distinct(StringComparer.Ordinal).Count();
            if (distinctLeft < 2 && distinctRight < 2)
                continue;

            var meanCoverage = contexts.Average(x => (double)core.Length / Math.Max(1, x.Title.Length));
            if (meanCoverage < 0.14d || meanCoverage > 0.92d)
                continue;

            var boundaryQuality = contexts.Average(x => LearnerBoundaryQuality(x.Title, x.Index, core.Length));
            var numericVariants = CountLeadingNumericSlotVariants(contexts.Select(x => x.Right).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal));
            var recurrence = Math.Min(1.0d, Math.Log2(contexts.Count + 1) / 4.0d);
            var contextDiversity = Math.Min(1.0d, Math.Log2(distinctLeft + distinctRight + 1) / 4.0d);
            var coverage = 1.0d - Math.Min(1.0d, Math.Abs(meanCoverage - 0.52d) / 0.52d);
            var twoSided = distinctLeft >= 2 && distinctRight >= 2 ? 1.0d : 0.55d;
            var confidence = recurrence * 0.28d + contextDiversity * 0.24d + boundaryQuality * 0.24d + coverage * 0.14d + twoSided * 0.10d;
            if (confidence < 0.62d)
                continue;

            var candidate = new NormalizationLearnerCoreCandidate(core, contexts.Count, distinctLeft, distinctRight,
                numericVariants, meanCoverage, boundaryQuality, confidence, contexts.OrderBy(x => x.Title.Length).First().Title);

            var compact = CompactIdentity(core);
            if (compact.Length < 3)
                continue;

            if (!dedupe.TryGetValue(compact, out var existing)
                || candidate.Confidence > existing.Confidence
                || (Math.Abs(candidate.Confidence - existing.Confidence) < 0.0001d && candidate.Occurrences > existing.Occurrences))
                dedupe[compact] = candidate;
        }

        var rows = new List<NormalizationLearnerShadowRow>();
        foreach (var candidate in dedupe.Values)
        {
            AddNormalizationLearnerRow(rows, NormalizationLearnerProposalKind.RecurringCore, candidate.Core,
                candidate.Occurrences, candidate.DistinctLeftContexts, candidate.DistinctRightContexts,
                candidate.NumericRightSlotVariants, candidate.MeanCoverage, candidate.BoundaryQuality,
                candidate.Confidence, candidate.RawSample);
        }
        return rows;
    }

    // 処理条件 — A four-character key only retrieves potential substring matches. The final ordinal Contains
    // test below is authoritative. Each posting is added once per original row, in original corpus order, so
    // duplicates and first-match semantics are unchanged.
    private static ulong NormalizationCoreShingleKey(ReadOnlySpan<char> value)
        => ((ulong)value[0] << 48) | ((ulong)value[1] << 32)
           | ((ulong)value[2] << 16) | value[3];

    private static Dictionary<ulong, List<int>> BuildNormalizationRecurringCoreLookup(
        (string Title, string Compact, string CurrentWork)[] compactRows,
        IReadOnlyList<NormalizationLearnerShadowRow> generatedRows)
    {
        var postings = new Dictionary<ulong, List<int>>();
        foreach (var row in generatedRows)
        {
            if (row.Kind != NormalizationLearnerProposalKind.RecurringCore || row.LearnedWork.Length < 4)
                continue;
            var key = NormalizationCoreShingleKey(row.LearnedWork.AsSpan());
            if (!postings.ContainsKey(key))
                postings.Add(key, new List<int>());
        }
        if (postings.Count == 0)
            return postings;

        for (var index = 0; index < compactRows.Length; index++)
        {
            var compact = compactRows[index].Compact;
            for (var start = 0; start <= compact.Length - 4; start++)
            {
                var key = NormalizationCoreShingleKey(compact.AsSpan(start, 4));
                if (!postings.TryGetValue(key, out var matches))
                    continue;
                if (matches.Count == 0 || matches[^1] != index)
                    matches.Add(index);
            }
        }
        return postings;
    }

    private static NormalizationLearnerShadowRow[] AssessNormalizationLearnerRows(
        IReadOnlyList<NormalizationLearnerShadowRow> generatedRows,
        string[] source,
        IReadOnlyDictionary<string, int> observationDays)
    {
        if (generatedRows.Count == 0)
            return Array.Empty<NormalizationLearnerShadowRow>();

        var compactRows = source
            .Select(title => (Title: title, Compact: CompactIdentity(title), CurrentWork: EvidenceSeriesKey(title)))
            .Where(x => x.Compact.Length > 0)
            .ToArray();

        // 処理条件 — Both kinds use retrieval-only indexes. All candidates still pass the original exact ordinal
        // prefix/substring predicate, preserving the same corpus members.
        var compactPrefixIndex = compactRows
            .OrderBy(x => x.Compact, StringComparer.Ordinal)
            .ToArray();
        var shorterHypothesesByWork = generatedRows
            .ToLookup(x => x.LearnedWork, StringComparer.Ordinal);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var recurringIndexStarted = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var recurringCoreLookup = BuildNormalizationRecurringCoreLookup(compactRows, generatedRows);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog($"NORMALIZATION_LEARNER_RECURRING_INDEX keys={recurringCoreLookup.Count} postings={recurringCoreLookup.Values.Sum(x => x.Count)} buildMs={System.Diagnostics.Stopwatch.GetElapsedTime(recurringIndexStarted).TotalMilliseconds:0} scope=single_learner_pass semantic=exact_substring_filter");
#endif

        // 作品識別 — Learn right-slot boundary tokens from the corpus itself. A token becomes informative only when
        // it is repeatedly observed immediately before numeric runs and that placement is unusually precise
        // relative to the token's total corpus frequency. This prevents arbitrary title-ending characters from
        // becoming rules merely because episode punctuation disappeared during compact normalization.
        var boundaryTokenStats = LearnNormalizationNumericBoundaryTokenStats(compactRows.Select(x => x.Compact));

        var preliminary = new List<NormalizationLearnerShadowRow>(generatedRows.Count);
        foreach (var row in generatedRows)
        {
            (string Title, string Compact, string CurrentWork)[] matches;
            if (row.Kind == NormalizationLearnerProposalKind.RecurringCore)
            {
                if (row.LearnedWork.Length < 4)
                {
                    // 処理条件 — Malformed recurring-core hypotheses use the conservative substring fallback.
                    matches = compactRows
                        .Where(x => x.Compact.Contains(row.LearnedWork, StringComparison.Ordinal))
                        .ToArray();
                }
                else if (recurringCoreLookup.TryGetValue(
                    NormalizationCoreShingleKey(row.LearnedWork.AsSpan()), out var candidates))
                {
                    matches = candidates
                        .Where(index => compactRows[index].Compact.Contains(row.LearnedWork, StringComparison.Ordinal))
                        .Select(index => compactRows[index])
                        .ToArray();
                }
                else
                    matches = Array.Empty<(string Title, string Compact, string CurrentWork)>();
            }
            else
            {
                var low = 0;
                var high = compactPrefixIndex.Length;
                while (low < high)
                {
                    var middle = low + ((high - low) >> 1);
                    if (string.CompareOrdinal(compactPrefixIndex[middle].Compact, row.LearnedWork) < 0)
                        low = middle + 1;
                    else
                        high = middle;
                }
                var start = low;
                while (low < compactPrefixIndex.Length
                    && compactPrefixIndex[low].Compact.StartsWith(row.LearnedWork, StringComparison.Ordinal))
                    low++;
                matches = compactPrefixIndex[start..low];
            }
            if (matches.Length == 0)
            {
                preliminary.Add(row with { Decision = "deferred_no_family_rows" });
                continue;
            }

            // 作品識別 — The same work-family population drives both role fitness and collision assessment. Count it
            // once instead of grouping/sorting the same rows again.
            var workFamilyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var aligned = 0;
            var exact = 0;
            foreach (var match in matches)
            {
                if (match.CurrentWork.Length > 0)
                    workFamilyCounts[match.CurrentWork] = workFamilyCounts.TryGetValue(match.CurrentWork, out var count) ? count + 1 : 1;
                if (LearnerIdentityAlignment(row.LearnedWork, match.CurrentWork) >= 0.60d)
                    aligned++;
                if (string.Equals(row.LearnedWork, match.CurrentWork, StringComparison.Ordinal))
                    exact++;
            }
            var distinctWorks = workFamilyCounts.Count;
            var exactAgreementRate = exact / (double)matches.Length;
            var alignmentRate = Math.Max(exactAgreementRate, (double)aligned / matches.Length * 0.85d);
            var singleFamily = 1.0d / Math.Sqrt(Math.Max(1, distinctWorks));
            var sampleWorkLength = Math.Max(1, row.CurrentWorkSample.Length);
            var lengthFit = Math.Min(row.LearnedWork.Length, sampleWorkLength) / (double)Math.Max(row.LearnedWork.Length, sampleWorkLength);
            var temporalDays = matches
                .Select(x => observationDays.TryGetValue(x.Title, out var days) ? days : 1)
                .DefaultIfEmpty(1)
                .Max();
            var temporal = Math.Min(1.0d, temporalDays / 4.0d);
            var asciiLike = row.LearnedWork.Count(ch => ch <= 0x7f && (char.IsLetterOrDigit(ch) || ch is '_' or '-'));
            var fragmentPenalty = row.LearnedWork.Length <= 6 && asciiLike >= Math.Ceiling(row.LearnedWork.Length * 0.75d) ? 0.35d : 0.0d;
            var baseRoleFitness = Math.Clamp(
                alignmentRate * 0.36d
                + singleFamily * 0.22d
                + lengthFit * 0.20d
                + row.BoundaryQuality * 0.12d
                + temporal * 0.10d
                - fragmentPenalty,
                0.0d, 1.0d);

            // 文字列解析 — Population self-assessment. This does not know programme names. It asks whether the proposed
            // boundary behaves consistently across every matching title in the current+remembered corpus. The result
            // feeds back into role fitness/confidence/decision before multi-day hypothesis survival.
            var dominantWorkCount = 0;
            foreach (var count in workFamilyCounts.Values)
                if (count > dominantWorkCount) dominantWorkCount = count;
            var dominantWorkShare = workFamilyCounts.Count == 0 ? 0.0d : dominantWorkCount / (double)matches.Length;
            var collisionRate = Math.Clamp(1.0d - dominantWorkShare, 0.0d, 1.0d);

            // 文字列解析 — A suffix is used only to classify its first unit and its leading numeric run. Read the
            // canonical compact title span instead of allocating three intermediate suffix arrays for every
            // hypothesis and family.
            var nonEmptySuffixCount = 0;
            var numericSuffixCount = 0;
            var numericSlots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var match in matches)
            {
                var index = row.Kind == NormalizationLearnerProposalKind.RecurringCore
                    ? match.Compact.IndexOf(row.LearnedWork, StringComparison.Ordinal)
                    : (match.Compact.StartsWith(row.LearnedWork, StringComparison.Ordinal) ? 0 : -1);
                if (index < 0)
                    continue;
                var suffix = match.Compact.AsSpan(index + row.LearnedWork.Length);
                if (suffix.IsEmpty)
                    continue;
                nonEmptySuffixCount++;
                if (!char.IsDigit(suffix[0]))
                    continue;
                numericSuffixCount++;
                var digits = 0;
                while (digits < suffix.Length && char.IsDigit(suffix[digits]))
                    digits++;
                if (digits > 0)
                    numericSlots.Add(suffix[..digits].ToString());
            }
            var numericBoundaryResidueRate = nonEmptySuffixCount == 0 ? 0.0d
                : numericSuffixCount / (double)nonEmptySuffixCount;
            var numericSlotDiversity = numericSuffixCount == 0 ? 0.0d
                : Math.Min(1.0d, numericSlots.Count / 4.0d);

            // 推薦・学習 — Contrastive boundary learning: only non-numeric proposal endings are eligible for learned
            // terminal-token intrusion evidence. Stable numeric identities (season/edition-like numbers) therefore
            // are not punished merely because another numeric slot follows them.
            var learnedBoundaryTokenScore = 0.0d;
            var learnedBoundaryLeftFamilies = 0;
            if (row.LearnedWork.Length > 0 && !char.IsDigit(row.LearnedWork[^1]))
            {
                for (var n = 1; n <= 2 && n <= row.LearnedWork.Length; n++)
                {
                    var token = row.LearnedWork[^n..];
                    if (!boundaryTokenStats.TryGetValue(token, out var tokenStats))
                        continue;
                    if (tokenStats.Score > learnedBoundaryTokenScore)
                    {
                        learnedBoundaryTokenScore = tokenStats.Score;
                        learnedBoundaryLeftFamilies = tokenStats.DistinctLeftFamilies;
                    }
                }
            }
            // 作品識別 — Multi-view role contrast. Corpus token statistics are evidence, not a rule by themselves. If
            // the full proposal is repeatedly the exact identity seen by the independent canonical view, the token
            // is more likely attached to Work identity. An independently generated shorter learner hypothesis can
            // counter that attachment evidence. This lets the learner arbitrate between its own views instead of
            // hard-coding words such as season/edition/dai or individual programme names. A shorter prefix
            // challenger can only be the exact proposal without its last one or two characters. Indexing those two
            // keys is equivalent to searching every generated hypothesis for every row, including duplicate proposal
            // keys.
            var shorterChallengerSupport = 0.0d;
            for (var removed = 1; removed <= 2 && row.LearnedWork.Length - removed >= 3; removed++)
            {
                var shorterKey = row.LearnedWork[..^removed];
                foreach (var other in shorterHypothesesByWork[shorterKey])
                {
                    if (other.Occurrences < Math.Max(3, row.Occurrences / 3))
                        continue;
                    shorterChallengerSupport = Math.Max(shorterChallengerSupport,
                        Math.Clamp(other.Confidence * other.BoundaryQuality, 0.0d, 1.0d));
                }
            }
            var boundaryRoleContrast = Math.Clamp(
                0.25d + 0.75d * Math.Max(1.0d - exactAgreementRate, shorterChallengerSupport),
                0.0d, 1.0d);
            var boundaryIntrusionScore = numericBoundaryResidueRate * numericSlotDiversity * learnedBoundaryTokenScore * boundaryRoleContrast;

            var leadingWrapperRisk = string.Equals(row.RelationToCurrent, "learner_broader", StringComparison.Ordinal)
                && row.LearnedWork.Length >= row.CurrentWorkSample.Length + 3;
            var trailingIdentityLossRisk = string.Equals(row.RelationToCurrent, "possible_trailing_variant", StringComparison.Ordinal)
                && row.CurrentWorkSample.Length >= row.LearnedWork.Length + 2;
            var boundaryResidueRisk = numericSuffixCount >= 3 && boundaryIntrusionScore >= 0.52d;
            var collisionRisk = matches.Length >= 4 && collisionRate >= 0.55d && alignmentRate < 0.55d;
            var shortFragmentRisk = fragmentPenalty > 0.0d && distinctWorks >= 2;

            var qualityPenalty = 0.0d;
            if (leadingWrapperRisk) qualityPenalty += 0.16d;
            if (trailingIdentityLossRisk) qualityPenalty += 0.14d;
            if (boundaryResidueRisk) qualityPenalty += 0.20d;
            if (collisionRisk) qualityPenalty += 0.24d;
            qualityPenalty += collisionRate * 0.12d;
            var populationQuality = Math.Clamp(baseRoleFitness - qualityPenalty, 0.0d, 1.0d);
            var roleFitness = Math.Clamp(baseRoleFitness * 0.55d + populationQuality * 0.45d, 0.0d, 1.0d);

            var qualityIssue = shortFragmentRisk ? "short_cross_family_fragment"
                : collisionRisk ? "overmerge_collision"
                : boundaryResidueRisk ? "learned_boundary_intrusion"
                : leadingWrapperRisk ? "possible_unseparated_leading_wrapper"
                : trailingIdentityLossRisk ? "possible_trailing_identity_loss"
                : "none";

            // 処理条件 — Structural confidence says only that a repeated span exists. Final confidence additionally
            // incorporates the corpus-level self-assessment so repeated mistakes cannot become survivors merely by
            // recurring for several days.
            var finalConfidence = row.Confidence * (0.32d + roleFitness * 0.43d + populationQuality * 0.25d);
            var decision = shortFragmentRisk
                ? "rejected_short_cross_family_fragment"
                : collisionRisk
                    ? "rejected_population_overmerge_collision"
                    : boundaryResidueRisk
                        ? "deferred_population_boundary_intrusion"
                        : leadingWrapperRisk
                            ? "deferred_population_leading_wrapper"
                            : trailingIdentityLossRisk
                                ? "deferred_population_trailing_identity_loss"
                                : distinctWorks >= 4 && alignmentRate < 0.35d
                                    ? "rejected_cross_work_genericity"
                                    : roleFitness >= 0.72d && populationQuality >= 0.68d && finalConfidence >= 0.68d
                                        ? "validated"
                                        : "deferred_role_uncertain";

            preliminary.Add(row with
            {
                Confidence = finalConfidence,
                RoleFitness = roleFitness,
                PopulationQuality = populationQuality,
                CollisionRate = collisionRate,
                NumericBoundaryResidueRate = numericBoundaryResidueRate,
                BoundaryIntrusionScore = boundaryIntrusionScore,
                LearnedBoundaryTokenScore = learnedBoundaryTokenScore,
                LearnedBoundaryLeftFamilies = learnedBoundaryLeftFamilies,
                BoundaryRoleContrast = boundaryRoleContrast,
                ShorterChallengerSupport = shorterChallengerSupport,
                QualityIssue = qualityIssue,
                TemporalObservationDays = temporalDays,
                DistinctCanonicalWorks = distinctWorks,
                Decision = decision
            });
        }

        // 処理条件 — Self-competition uses current validated rows in role-fitness order. A deferred row cannot
        // compete again within the same assessment pass.
        var result = preliminary.ToArray();
        var validatedByRoleFitness = Enumerable.Range(0, result.Length)
            .Where(index => string.Equals(result[index].Decision, "validated", StringComparison.Ordinal))
            .OrderByDescending(index => result[index].RoleFitness)
            .ToArray();
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var ensembleStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        long ensembleComparisons = 0;
        var ensembleDeferred = 0;
#endif
        for (var i = 0; i < result.Length; i++)
        {
            var row = result[i];
            if (!string.Equals(row.Decision, "validated", StringComparison.Ordinal))
                continue;
            var minimumRoleFitness = row.RoleFitness + 0.10d;
            var minimumConfidence = row.Confidence - 0.03d;
            foreach (var index in validatedByRoleFitness)
            {
                var other = result[index];
                if (other.RoleFitness < minimumRoleFitness)
                    break;
                if (index == i || !string.Equals(other.Decision, "validated", StringComparison.Ordinal)
                    || !(other.RoleFitness >= minimumRoleFitness) || !(other.Confidence >= minimumConfidence))
                    continue;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                ensembleComparisons++;
#endif
                if (row.LearnedWork.Contains(other.LearnedWork, StringComparison.Ordinal)
                    || other.LearnedWork.Contains(row.LearnedWork, StringComparison.Ordinal))
                {
                    result[i] = row with { Decision = "deferred_ensemble_competition" };
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                    ensembleDeferred++;
#endif
                    break;
                }
            }
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog($"NORMALIZATION_ENSEMBLE_SELECTION elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(ensembleStarted).TotalMilliseconds:0} validated={validatedByRoleFitness.Length} checkedPairs={ensembleComparisons} deferred={ensembleDeferred} semantics=exact_dynamic_eligibility");
#endif
        return result;
    }

    private readonly record struct NormalizationBoundaryTokenStats(
        double Score,
        int DistinctTitles,
        int DistinctLeftFamilies,
        int DistinctNumericSlots);

    private static Dictionary<string, NormalizationBoundaryTokenStats> LearnNormalizationNumericBoundaryTokenStats(IEnumerable<string> compactTitles)
    {
        var total = new Dictionary<string, int>(StringComparer.Ordinal);
        var beforeNumeric = new Dictionary<string, int>(StringComparer.Ordinal);
        var boundaryTitles = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var boundaryLeftFamilies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var boundaryNumericSlots = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var titles = compactTitles.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).ToArray();

        for (var titleIndex = 0; titleIndex < titles.Length; titleIndex++)
        {
            var text = titles[titleIndex];
            for (var i = 0; i < text.Length; i++)
            {
                for (var n = 1; n <= 2 && i + n <= text.Length; n++)
                {
                    var token = text.Substring(i, n);
                    total[token] = total.TryGetValue(token, out var seen) ? seen + 1 : 1;
                }

                if (!char.IsDigit(text[i]) || (i > 0 && char.IsDigit(text[i - 1])))
                    continue;

                var numericSlot = LeadingDigitRun(text[i..]);
                for (var n = 1; n <= 2 && i - n >= 0; n++)
                {
                    var token = text.Substring(i - n, n);
                    beforeNumeric[token] = beforeNumeric.TryGetValue(token, out var seen) ? seen + 1 : 1;

                    if (!boundaryTitles.TryGetValue(token, out var titleSet))
                        boundaryTitles[token] = titleSet = new HashSet<int>();
                    titleSet.Add(titleIndex);

                    // 作品識別 — Directionality is learned from independent left-side families. Repeated episodes of one
                    // programme may provide many title observations, but they contribute only one family here. A true
                    // boundary token must generalize across different stems before that token.
                    var familyEnd = i - n;
                    var familyStart = Math.Max(0, familyEnd - 16);
                    var leftFamily = familyEnd <= 0 ? string.Empty : text[familyStart..familyEnd];
                    if (!boundaryLeftFamilies.TryGetValue(token, out var familySet))
                        boundaryLeftFamilies[token] = familySet = new HashSet<string>(StringComparer.Ordinal);
                    familySet.Add(leftFamily);

                    if (!boundaryNumericSlots.TryGetValue(token, out var numericSet))
                        boundaryNumericSlots[token] = numericSet = new HashSet<string>(StringComparer.Ordinal);
                    if (numericSlot.Length > 0)
                        numericSet.Add(numericSlot);
                }
            }
        }

        var stats = new Dictionary<string, NormalizationBoundaryTokenStats>(StringComparer.Ordinal);
        foreach (var pair in beforeNumeric)
        {
            if (!total.TryGetValue(pair.Key, out var totalCount) || totalCount <= 0)
                continue;

            var distinctTitles = boundaryTitles.TryGetValue(pair.Key, out var titleSet) ? titleSet.Count : 0;
            var distinctLeftFamilies = boundaryLeftFamilies.TryGetValue(pair.Key, out var familySet) ? familySet.Count : 0;
            var distinctNumericSlots = boundaryNumericSlots.TryGetValue(pair.Key, out var numericSet) ? numericSet.Count : 0;
            if (pair.Value < 3 || distinctTitles < 3 || distinctLeftFamilies < 2)
                continue;

            var precision = pair.Value / (double)totalCount;
            var titleSupport = 1.0d - Math.Exp(-distinctTitles / 6.0d);
            var familySupport = 1.0d - Math.Exp(-(distinctLeftFamilies - 1) / 3.0d);
            var slotSupport = 1.0d - Math.Exp(-Math.Max(0, distinctNumericSlots - 1) / 4.0d);

            // 作品識別 — Family support is the directional discriminator. A final character belonging to one stable Work
            // can recur before dozens of episode numbers, but it does not become a generic boundary unless the same
            // token appears after multiple independent left-side families.
            var score = Math.Clamp(precision * titleSupport * (0.70d * familySupport + 0.30d * slotSupport), 0.0d, 1.0d);
            if (score >= 0.30d)
                stats[pair.Key] = new NormalizationBoundaryTokenStats(score, distinctTitles, distinctLeftFamilies, distinctNumericSlots);
        }
        return stats;
    }

    private static string LeadingDigitRun(string value)
    {
        if (string.IsNullOrEmpty(value) || !char.IsDigit(value[0]))
            return string.Empty;
        var length = 1;
        while (length < value.Length && char.IsDigit(value[length]))
            length++;
        return value[..length];
    }

    private static NormalizationLearnerShadowRow[] ApplyNormalizationLearnerExternalStructuralSupport(
        IReadOnlyList<NormalizationLearnerShadowRow> rows,
        IReadOnlyDictionary<string, NormalizationLearnerExternalStructuralSnapshot> history)
    {
        if (rows.Count == 0 || history.Count == 0)
            return rows.ToArray();

        var result = new NormalizationLearnerShadowRow[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var key = $"{row.Kind}:{row.LearnedWork}";
            if (!history.TryGetValue(key, out var h))
            {
                result[i] = row;
                continue;
            }

            result[i] = row with
            {
                ExternalStructuralSupport = h.Support,
                ExternalProviderReliability = h.ProviderReliability,
                ExternalProviderCount = h.ProviderCount,
                ExternalEvidenceCount = h.EvidenceCount,
                ExternalStructuralStage = h.Stage
            };
        }
        return result;
    }

    private static NormalizationLearnerShadowRow[] ApplyNormalizationLearnerLongTermHistory(
        IReadOnlyList<NormalizationLearnerShadowRow> rows,
        IReadOnlyDictionary<string, NormalizationLearnerLongTermSnapshot> history)
    {
        if (rows.Count == 0 || history.Count == 0)
            return rows.ToArray();

        var result = new NormalizationLearnerShadowRow[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var key = $"{row.Kind}:{row.LearnedWork}";
            if (!history.TryGetValue(key, out var h))
            {
                result[i] = row;
                continue;
            }

            result[i] = row with
            {
                LongTermObservationDays = h.DistinctObservationDays,
                LongTermReliability = h.Reliability,
                LongTermConsistency = h.Consistency,
                LongTermMeanPopulationQuality = h.MeanPopulationQuality,
                LongTermMeanCollisionRate = h.MeanCollisionRate,
                LongTermMeanBoundaryIntrusion = h.MeanBoundaryIntrusion,
                LongTermStage = h.Stage,
                LongTermMeanExternalSupport = h.MeanExternalStructuralSupport,
                LongTermMeanExternalProviderReliability = h.MeanExternalProviderReliability
            };
        }
        return result;
    }

    private static NormalizationLearnerShadowRow[] ApplyNormalizationLearnerHypothesisHistory(
        IReadOnlyList<NormalizationLearnerShadowRow> rows,
        IReadOnlyDictionary<string, NormalizationLearnerHypothesisSnapshot> history)
    {
        if (rows.Count == 0 || history.Count == 0)
            return rows.ToArray();

        var result = new NormalizationLearnerShadowRow[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var key = $"{row.Kind}:{row.LearnedWork}";
            if (!history.TryGetValue(key, out var h))
            {
                result[i] = row;
                continue;
            }

            result[i] = row with
            {
                HypothesisObservationDays = h.DistinctObservationDays,
                HypothesisValidatedDays = h.ValidatedDays,
                HypothesisDeferredDays = h.DeferredDays,
                HypothesisRejectedDays = h.RejectedDays,
                HypothesisStability = h.StabilityScore,
                HypothesisStage = h.Stage
            };
        }
        return result;
    }

    private static double LearnerIdentityAlignment(string learnedWork, string currentWork)
    {
        if (string.IsNullOrWhiteSpace(learnedWork) || string.IsNullOrWhiteSpace(currentWork))
            return 0.0d;
        if (string.Equals(learnedWork, currentWork, StringComparison.Ordinal))
            return 1.0d;
        if (learnedWork.Contains(currentWork, StringComparison.Ordinal)
            || currentWork.Contains(learnedWork, StringComparison.Ordinal))
            return Math.Min(learnedWork.Length, currentWork.Length) / (double)Math.Max(learnedWork.Length, currentWork.Length);
        return 0.0d;
    }

    private static void AddNormalizationLearnerRow(
        List<NormalizationLearnerShadowRow> rows,
        NormalizationLearnerProposalKind kind,
        string candidate,
        int occurrences,
        int distinctLeftContexts,
        int distinctRightContexts,
        int numericRightSlotVariants,
        double meanCoverage,
        double boundaryQuality,
        double confidence,
        string rawSample)
    {
        var learnedWork = CompactIdentity(candidate);
        if (learnedWork.Length < 3)
            return;

        var currentWork = EvidenceSeriesKey(rawSample);
        var relation = NormalizationLearnerRelation(learnedWork, currentWork);
        rows.Add(new NormalizationLearnerShadowRow(kind, learnedWork, occurrences, distinctLeftContexts,
            distinctRightContexts, numericRightSlotVariants, meanCoverage, boundaryQuality, confidence,
            relation, currentWork, rawSample));
    }

    private static string NormalizationLearnerRelation(string learnedWork, string currentWork)
    {
        if (string.Equals(learnedWork, currentWork, StringComparison.Ordinal))
            return "agrees";
        if (currentWork.EndsWith(learnedWork, StringComparison.Ordinal) && learnedWork.Length >= 4)
            return "possible_leading_container";
        if (currentWork.StartsWith(learnedWork, StringComparison.Ordinal) && learnedWork.Length >= 4)
            return "possible_trailing_variant";
        if (learnedWork.Contains(currentWork, StringComparison.Ordinal) && currentWork.Length >= 4)
            return "learner_broader";
        if (currentWork.Contains(learnedWork, StringComparison.Ordinal) && learnedWork.Length >= 4)
            return "learner_narrower";
        return "conflict";
    }

    private readonly record struct LearnerCoreContext(string Title, int Index, string Left, string Right);

    private static List<LearnerCoreContext> CollectCoreContexts(IEnumerable<string> titles, string core)
    {
        var rows = new List<LearnerCoreContext>();
        foreach (var title in titles)
        {
            var index = title.IndexOf(core, StringComparison.Ordinal);
            if (index < 0)
                continue;
            rows.Add(new LearnerCoreContext(title, index, title[..index].Trim(), title[(index + core.Length)..].Trim()));
        }
        return rows;
    }

    private static string ExpandRecurringCore(string[] titles, string seed)
    {
        if (titles.Length == 0)
            return seed;

        var positions = new int[titles.Length];
        for (var i = 0; i < titles.Length; i++)
        {
            positions[i] = titles[i].IndexOf(seed, StringComparison.Ordinal);
            if (positions[i] < 0)
                return seed;
        }

        var left = 0;
        while (true)
        {
            char? expected = null;
            var valid = true;
            for (var i = 0; i < titles.Length; i++)
            {
                var pos = positions[i] - left - 1;
                if (pos < 0)
                {
                    valid = false;
                    break;
                }
                var ch = titles[i][pos];
                if (expected is null)
                    expected = ch;
                else if (expected.Value != ch)
                {
                    valid = false;
                    break;
                }
            }
            if (!valid)
                break;
            left++;
        }

        var right = 0;
        while (true)
        {
            char? expected = null;
            var valid = true;
            for (var i = 0; i < titles.Length; i++)
            {
                var pos = positions[i] + seed.Length + right;
                if (pos >= titles[i].Length)
                {
                    valid = false;
                    break;
                }
                var ch = titles[i][pos];
                if (expected is null)
                    expected = ch;
                else if (expected.Value != ch)
                {
                    valid = false;
                    break;
                }
            }
            if (!valid)
                break;
            right++;
        }

        var first = titles[0];
        return first.Substring(positions[0] - left, left + seed.Length + right);
    }

    private static int CountLeadingNumericSlotVariants(IEnumerable<string> suffixes)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var suffix in suffixes)
        {
            var trimmed = suffix.TrimStart();
            var i = 0;
            while (i < trimmed.Length && !char.IsDigit(trimmed[i]))
            {
                // 処理条件 — Only tolerate punctuation before the numeric run. If lexical text starts first, this is not an
                // immediate numeric slot.
                if (char.IsLetter(trimmed[i]))
                {
                    i = -1;
                    break;
                }
                i++;
                if (i > 4)
                {
                    i = -1;
                    break;
                }
            }
            if (i < 0 || i >= trimmed.Length || !char.IsDigit(trimmed[i]))
                continue;

            var start = i;
            while (i < trimmed.Length && char.IsDigit(trimmed[i]))
                i++;
            values.Add(trimmed[start..i]);
        }
        return values.Count;
    }

    private static string NormalizationLearnerComparableTitle(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        normalized = StripNonIdentityBroadcastAnnotations(normalized);
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        normalized = Regex.Replace(normalized, @"\s*[（(]\s*(?:月|火|水|木|金|土|日)\s*[）)]\s*$", " ").Trim();
        return normalized;
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var count = Math.Min(left.Length, right.Length);
        var i = 0;
        while (i < count && left[i] == right[i])
            i++;
        return i;
    }

    private static string TrimPartialTrailingNumericRunIfNeeded(
        string value,
        IEnumerable<(string Title, int Index)> contexts)
    {
        if (value.Length == 0 || !char.IsDigit(value[^1]))
            return value;

        // 処理条件 — Backtrack a numeric run only when the learned span demonstrably stops inside that run. A stable
        // edition/season number followed by a delimiter is preserved.
        var cutsInsideNumber = contexts.Any(x =>
        {
            var next = x.Index + value.Length;
            return x.Index >= 0 && next >= 0 && next < x.Title.Length && char.IsDigit(x.Title[next]);
        });
        if (!cutsInsideNumber)
            return value;

        var end = value.Length;
        while (end > 0 && char.IsDigit(value[end - 1]))
            end--;
        return value[..end];
    }

    private static string SnapLearnerBoundary(string value)
        => value.Trim().TrimEnd('・', '･', '-', '－', '―', '—', ':', '：', '/', '／', '|', '｜', '▼', '▽', '▶', '►', '>', '＞', '「', '『', '【', '(', '（', '#', '＃');

    private static double LearnerBoundaryQuality(string[] matches, string core, int matchIndex)
        => matches.Length == 0 ? 0.0d : matches.Average(title => LearnerBoundaryQuality(title, matchIndex, core.Length));

    private static double LearnerBoundaryQuality(string title, int index, int length)
    {
        if (index < 0 || length <= 0 || index + length > title.Length)
            return 0.0d;

        var startQuality = index == 0 ? 1.0d : LearnerBoundaryTransitionQuality(title[index - 1], title[index]);
        var end = index + length;
        var endQuality = end >= title.Length ? 1.0d : LearnerBoundaryTransitionQuality(title[end - 1], title[end]);
        return (startQuality + endQuality) / 2.0d;
    }

    private static double LearnerBoundaryTransitionQuality(char left, char right)
    {
        if (char.IsWhiteSpace(left) || char.IsWhiteSpace(right) || char.IsPunctuation(left) || char.IsPunctuation(right) || char.IsSymbol(left) || char.IsSymbol(right))
            return 1.0d;
        if (char.IsDigit(left) != char.IsDigit(right))
            return 0.85d;
        if (char.IsLetter(left) != char.IsLetter(right))
            return 0.75d;
        return 0.35d;
    }

}
