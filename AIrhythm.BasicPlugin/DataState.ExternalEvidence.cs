namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmDataState
{
    private static AIrhythmExternalEvidence[] InterpretExternalEvidence(
        IReadOnlyList<AIrhythmExternalEvidence> evidence,
        AIrhythmExternalEvidenceNeedReason reason,
        AIrhythmLeadingContainerParts containerParts,
        AIrhythmLeadingContainerLocalContext containerContext,
        AIrhythmDerivedProgramRelation derivedRelation,
        AIrhythmNumericParenthesizedClass numericClass,
        string queryTitle = "",
        int numericValue = 0)
    {
        if (evidence.Count == 0)
            return Array.Empty<AIrhythmExternalEvidence>();

        string? relation = null;
        if (reason == AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate
            && !string.IsNullOrWhiteSpace(containerParts.WorkCandidate))
            relation = GetLeadingContainerExternalEvidenceRelation(containerContext);
        else if (reason == AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker)
            relation = GetDerivedProgramExternalEvidenceRelation(derivedRelation);
        else if (reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix)
            relation = GetNumericParenthesizedExternalEvidenceRelation(numericClass);

        return evidence
            .Select(item =>
            {
                var verdict = EvaluateExternalEvidenceVerdict(item, reason, queryTitle, numericValue);
                return item with
                {
                    Relation = string.IsNullOrWhiteSpace(relation) ? item.Relation : relation,
                    Verdict = verdict.Verdict,
                    VerdictReason = verdict.Reason
                };
            })
            .ToArray();
    }

    private static (AIrhythmExternalEvidenceVerdict Verdict, AIrhythmExternalEvidenceVerdictReason Reason) EvaluateExternalEvidenceVerdict(
        AIrhythmExternalEvidence evidence,
        AIrhythmExternalEvidenceNeedReason reason,
        string queryTitle,
        int numericValue)
    {
        if (reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix
            && numericValue > 0
            && evidence.Episode is int externalEpisode
            && externalEpisode > 0)
        {
            return externalEpisode == numericValue
                ? (AIrhythmExternalEvidenceVerdict.Supported, AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch)
                : (AIrhythmExternalEvidenceVerdict.Conflicting, AIrhythmExternalEvidenceVerdictReason.EpisodeNumberConflict);
        }

        if (AIrhythmExternalLookupAdapter.GetTitleSimilarity(queryTitle, evidence.CanonicalTitle) > 0d)
            return (AIrhythmExternalEvidenceVerdict.Supported, AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch);

        if (evidence.Aliases.Count > 0
            && evidence.Aliases.Any(alias => AIrhythmExternalLookupAdapter.GetTitleSimilarity(queryTitle, alias) > 0d))
            return (AIrhythmExternalEvidenceVerdict.Supported, AIrhythmExternalEvidenceVerdictReason.AliasMatch);

        // 外部情報 — Normal user-intent Work lookups have no structural ambiguity relation to justify a looser
        // provider-confidence verdict. Require an actual canonical-title or alias match.
        if (reason == AIrhythmExternalEvidenceNeedReason.None)
            return (AIrhythmExternalEvidenceVerdict.Unresolved, AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence);

        // 外部情報 — Alias follow-up is identity evidence only when an alias actually matches the local query. Never
        // turn the mere existence of provider aliases into generic ProviderConfidence support.
        if (!string.Equals(evidence.MediaType, "alias", StringComparison.OrdinalIgnoreCase)
            && evidence.Confidence >= 0.5d)
            return (AIrhythmExternalEvidenceVerdict.Supported, AIrhythmExternalEvidenceVerdictReason.ProviderConfidence);

        return (AIrhythmExternalEvidenceVerdict.Unresolved, AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence);
    }

    private static AIrhythmExternalEvidenceSummary SummarizeExternalEvidence(IReadOnlyList<AIrhythmExternalEvidence> evidence)
    {
        if (evidence.Count == 0)
            return new AIrhythmExternalEvidenceSummary(AIrhythmExternalEvidenceSummaryStatus.NoEvidence, 0, 0, 0, 0);

        var supported = evidence.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported);
        var unresolved = evidence.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Unresolved);
        var conflicting = evidence.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Conflicting);
        var status = conflicting > 0
            ? AIrhythmExternalEvidenceSummaryStatus.Conflicting
            : supported > 0
                ? AIrhythmExternalEvidenceSummaryStatus.Supported
                : AIrhythmExternalEvidenceSummaryStatus.UnresolvedOnly;

        return new AIrhythmExternalEvidenceSummary(status, evidence.Count, supported, unresolved, conflicting);
    }

    private static string GetLeadingContainerExternalEvidenceRelation(AIrhythmLeadingContainerLocalContext context)
        => context.EvidenceLevel switch
        {
            AIrhythmLeadingContainerEvidenceLevel.Diversified => "candidate:container_work_diversified",
            AIrhythmLeadingContainerEvidenceLevel.Repeated => "candidate:container_work_repeated",
            _ => "candidate:container_work_unconfirmed"
        };

    private static string GetDerivedProgramExternalEvidenceRelation(AIrhythmDerivedProgramRelation relation)
        => relation switch
        {
            AIrhythmDerivedProgramRelation.Promo => "related:promo",
            AIrhythmDerivedProgramRelation.Preview => "related:preview",
            AIrhythmDerivedProgramRelation.Recap => "related:recap",
            _ => "related:derived_unconfirmed"
        };

    private static string GetNumericParenthesizedExternalEvidenceRelation(AIrhythmNumericParenthesizedClass numericClass)
        => numericClass switch
        {
            AIrhythmNumericParenthesizedClass.LikelyYear => "candidate:numeric_likely_year",
            AIrhythmNumericParenthesizedClass.LocalSequence => "candidate:numeric_local_sequence",
            AIrhythmNumericParenthesizedClass.Ambiguous => "candidate:numeric_ambiguous",
            _ => "candidate:numeric_unconfirmed"
        };

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
    private static AIrhythmExternalSupportedReachability ClassifyExternalSupportedReachability(
        IReadOnlyList<AIrhythmExternalEvidence> normalized,
        IReadOnlyList<AIrhythmExternalEvidence> interpreted,
        bool aliasFollowupAvailable,
        bool episodeFollowupAvailable,
        int episodeCandidate,
        bool showIdPresent)
    {
        if (interpreted.Any(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported))
            return AIrhythmExternalSupportedReachability.SearchConfirmed;
        if (normalized.Count == 0)
            return AIrhythmExternalSupportedReachability.NoProviderEvidence;

        var reachability = AIrhythmExternalSupportedReachability.None;
        if (interpreted.Any(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Unresolved))
        {
            if (aliasFollowupAvailable && showIdPresent)
                reachability |= AIrhythmExternalSupportedReachability.AliasFollowupAvailable;
            if (episodeFollowupAvailable && showIdPresent && episodeCandidate > 0)
                reachability |= AIrhythmExternalSupportedReachability.EpisodeFollowupAvailable;
        }

        return reachability == AIrhythmExternalSupportedReachability.None
            ? AIrhythmExternalSupportedReachability.UnresolvedNoFollowupPath
            : reachability;
    }

    private static void RunExternalEvidenceInterpretationSelfValidation()
    {
        // 保存・読込 — Exercise the exact production interpretation function without provider/network input. This
        // proves Relation attachment on the evidence object itself; it never enters cache, scoring, identity, or
        // the external lookup budget.
        var source = new[]
        {
            new AIrhythmExternalEvidence(
                "diagnostic",
                "selftest:relation",
                "SelfTest Work",
                Array.Empty<string>(),
                "tv",
                null,
                7,
                null,
                "provider:original",
                1.0,
                DateTimeOffset.UnixEpoch)
        };
        var parts = new AIrhythmLeadingContainerParts(
            "【SelfTest Container】SelfTest Work",
            "SelfTest Container",
            "SelfTest Work",
            "SelfTest Work",
            string.Empty);

        static string RelationOf(AIrhythmExternalEvidence[] items)
            => items.Length == 1 ? items[0].Relation : $"count:{items.Length}";
        static string VerdictOf(AIrhythmExternalEvidence[] items)
            => items.Length == 1 ? items[0].Verdict.ToString() : $"count:{items.Length}";
        static string VerdictReasonOf(AIrhythmExternalEvidence[] items)
            => items.Length == 1 ? items[0].VerdictReason.ToString() : $"count:{items.Length}";

        var diversified = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate, parts,
            new AIrhythmLeadingContainerLocalContext(2, 2, AIrhythmLeadingContainerEvidenceLevel.Diversified),
            AIrhythmDerivedProgramRelation.None, AIrhythmNumericParenthesizedClass.None);
        var repeated = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate, parts,
            new AIrhythmLeadingContainerLocalContext(2, 1, AIrhythmLeadingContainerEvidenceLevel.Repeated),
            AIrhythmDerivedProgramRelation.None, AIrhythmNumericParenthesizedClass.None);
        var unconfirmed = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate, parts,
            new AIrhythmLeadingContainerLocalContext(1, 1, AIrhythmLeadingContainerEvidenceLevel.None),
            AIrhythmDerivedProgramRelation.None, AIrhythmNumericParenthesizedClass.None);
        var promo = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker, default, default,
            AIrhythmDerivedProgramRelation.Promo, AIrhythmNumericParenthesizedClass.None);
        var preview = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker, default, default,
            AIrhythmDerivedProgramRelation.Preview, AIrhythmNumericParenthesizedClass.None);
        var recap = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker, default, default,
            AIrhythmDerivedProgramRelation.Recap, AIrhythmNumericParenthesizedClass.None);
        var numericLikelyYear = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix, default, default,
            AIrhythmDerivedProgramRelation.None, AIrhythmNumericParenthesizedClass.LikelyYear);
        var numericLocalSequence = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix, default, default,
            AIrhythmDerivedProgramRelation.None, AIrhythmNumericParenthesizedClass.LocalSequence, "SelfTest Work", 7);
        var numericAmbiguous = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix, default, default,
            AIrhythmDerivedProgramRelation.None, AIrhythmNumericParenthesizedClass.Ambiguous, "SelfTest Work", 8);
        var nonAmbiguous = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.None, parts,
            new AIrhythmLeadingContainerLocalContext(2, 2, AIrhythmLeadingContainerEvidenceLevel.Diversified),
            AIrhythmDerivedProgramRelation.Promo, AIrhythmNumericParenthesizedClass.Ambiguous);
        var aliasMatchEvidence = new[]
        {
            source[0] with
            {
                EntityId = string.Empty,
                CanonicalTitle = string.Empty,
                Aliases = new[] { "SelfTest Alias" },
                MediaType = "alias",
                Confidence = 1.0
            }
        };
        var aliasMismatchEvidence = new[]
        {
            aliasMatchEvidence[0] with { Aliases = new[] { "Unrelated Alias" } }
        };
        var aliasMatched = InterpretExternalEvidence(
            aliasMatchEvidence, AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker, default, default,
            AIrhythmDerivedProgramRelation.Promo, AIrhythmNumericParenthesizedClass.None, "SelfTest Alias");
        var aliasMismatched = InterpretExternalEvidence(
            aliasMismatchEvidence, AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker, default, default,
            AIrhythmDerivedProgramRelation.Promo, AIrhythmNumericParenthesizedClass.None, "SelfTest Alias");

        var diversifiedRelation = RelationOf(diversified);
        var repeatedRelation = RelationOf(repeated);
        var unconfirmedRelation = RelationOf(unconfirmed);
        var promoRelation = RelationOf(promo);
        var previewRelation = RelationOf(preview);
        var recapRelation = RelationOf(recap);
        var numericLikelyYearRelation = RelationOf(numericLikelyYear);
        var numericLocalSequenceRelation = RelationOf(numericLocalSequence);
        var numericAmbiguousRelation = RelationOf(numericAmbiguous);
        var nonAmbiguousRelation = RelationOf(nonAmbiguous);
        var supportedVerdict = VerdictOf(promo);
        var supportedReason = VerdictReasonOf(promo);
        var numericSupportedVerdict = VerdictOf(numericLocalSequence);
        var numericSupportedReason = VerdictReasonOf(numericLocalSequence);
        var numericConflictingVerdict = VerdictOf(numericAmbiguous);
        var numericConflictingReason = VerdictReasonOf(numericAmbiguous);
        var nonAmbiguousVerdict = VerdictOf(nonAmbiguous);
        var nonAmbiguousReason = VerdictReasonOf(nonAmbiguous);
        var aliasMatchedVerdict = VerdictOf(aliasMatched);
        var aliasMatchedReason = VerdictReasonOf(aliasMatched);
        var aliasMismatchedVerdict = VerdictOf(aliasMismatched);
        var aliasMismatchedReason = VerdictReasonOf(aliasMismatched);
        var summaryNoEvidence = SummarizeExternalEvidence(Array.Empty<AIrhythmExternalEvidence>());
        var summarySupported = SummarizeExternalEvidence(new[]
        {
            source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Supported },
            source[0] with { EntityId = "selftest:summary:unresolved", Verdict = AIrhythmExternalEvidenceVerdict.Unresolved }
        });
        var summaryUnresolved = SummarizeExternalEvidence(new[]
        {
            source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Unresolved }
        });
        var summaryConflicting = SummarizeExternalEvidence(new[]
        {
            source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Supported },
            source[0] with { EntityId = "selftest:summary:conflict", Verdict = AIrhythmExternalEvidenceVerdict.Conflicting }
        });
        var evaluationNoEvidence = AIrhythmRecommendationEngine.ToExternalEvidenceEvaluationFlags(summaryNoEvidence.Status);
        var evaluationSupported = AIrhythmRecommendationEngine.ToExternalEvidenceEvaluationFlags(summarySupported.Status);
        var evaluationUnresolved = AIrhythmRecommendationEngine.ToExternalEvidenceEvaluationFlags(summaryUnresolved.Status);
        var evaluationConflicting = AIrhythmRecommendationEngine.ToExternalEvidenceEvaluationFlags(summaryConflicting.Status);
        var gateNoEvidence = AIrhythmRecommendationEngine.ToExternalEvidenceConfidenceGate(evaluationNoEvidence);
        var gateSupported = AIrhythmRecommendationEngine.ToExternalEvidenceConfidenceGate(evaluationSupported);
        var gateUnresolved = AIrhythmRecommendationEngine.ToExternalEvidenceConfidenceGate(evaluationUnresolved);
        var gateConflicting = AIrhythmRecommendationEngine.ToExternalEvidenceConfidenceGate(evaluationConflicting);
        var adjustmentNoEvidence = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentCandidate(gateNoEvidence);
        var adjustmentSupported = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentCandidate(gateSupported);
        var adjustmentUnresolved = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentCandidate(gateUnresolved);
        var adjustmentConflicting = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentCandidate(gateConflicting);
        var adjustmentKindIdentity = DetermineExternalEvidenceAdjustmentKind(new[]
        {
            source[0] with
            {
                Verdict = AIrhythmExternalEvidenceVerdict.Supported,
                VerdictReason = AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch
            }
        });
        var adjustmentKindEpisode = DetermineExternalEvidenceAdjustmentKind(new[]
        {
            source[0] with
            {
                Verdict = AIrhythmExternalEvidenceVerdict.Supported,
                VerdictReason = AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch,
                Episode = 2
            }
        });
        var adjustmentKindUnresolved = DetermineExternalEvidenceAdjustmentKind(new[]
        {
            source[0] with
            {
                Verdict = AIrhythmExternalEvidenceVerdict.Unresolved,
                VerdictReason = AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence
            }
        });
        var normalizedKindBlocked = AIrhythmRecommendationEngine.NormalizeExternalEvidenceAdjustmentKind(adjustmentConflicting, adjustmentKindIdentity);
        var adjustmentStrengthIdentity = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentStrength(adjustmentSupported, adjustmentKindIdentity);
        var adjustmentStrengthEpisode = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentStrength(adjustmentSupported, adjustmentKindEpisode);
        var adjustmentStrengthUnresolved = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentStrength(adjustmentUnresolved, adjustmentKindUnresolved);
        var adjustmentStrengthBlocked = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentStrength(adjustmentConflicting, normalizedKindBlocked);
        var shadowAdjustmentValueIdentity = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentValue(adjustmentStrengthIdentity);
        var shadowAdjustmentValueEpisode = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentValue(adjustmentStrengthEpisode);
        var shadowAdjustmentValueUnresolved = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentValue(adjustmentStrengthUnresolved);
        var shadowAdjustmentValueBlocked = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentValue(adjustmentStrengthBlocked);
        var shadowImpactBase = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00", CultureInfo.InvariantCulture);
        AIrhythmRecommendation RawCoordinateItem(int index, int displayScore, double deviationRawScore, AIrhythmExternalEvidenceAdjustmentStrength strength = AIrhythmExternalEvidenceAdjustmentStrength.None)
            => new($"raw-shadow-{index}", "selftest", string.Empty, string.Empty, shadowImpactBase.AddMinutes(300 + index), displayScore, Array.Empty<string>(), $"raw-shadow-series-{index}", null, DeviationRawScore: deviationRawScore, ExternalEvidenceAdjustmentKind: strength == AIrhythmExternalEvidenceAdjustmentStrength.None ? AIrhythmExternalEvidenceAdjustmentKind.None : AIrhythmExternalEvidenceAdjustmentKind.IdentitySupport, ExternalEvidenceAdjustmentStrength: strength, ExternalEvidenceSupportingVerdictReason: strength == AIrhythmExternalEvidenceAdjustmentStrength.None ? AIrhythmExternalEvidenceVerdictReason.NotApplicable : AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch);
        var rawCoordinateItems = new[]
        {
            RawCoordinateItem(1, 38, 40.0d),
            RawCoordinateItem(2, 50, 50.0d, AIrhythmExternalEvidenceAdjustmentStrength.Weak),
            RawCoordinateItem(3, 62, 60.0d)
        };
        var rawCoordinateSweep = AIrhythmRecommendationEngine.AnalyzeExternalEvidenceRawCoordinateShadowSweep(rawCoordinateItems, 0.25d, 0.75d);
        var rawCoordinateDetail = AIrhythmRecommendationEngine.AnalyzeExternalEvidenceRawCoordinateShadowSweepDetails(rawCoordinateItems, 0.25d, 0.75d, 8);
        var reachabilitySearchConfirmed = ClassifyExternalSupportedReachability(
            source, new[] { source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Supported } }, true, true, 7, true);
        var reachabilityAlias = ClassifyExternalSupportedReachability(
            source, new[] { source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Unresolved } }, true, false, 0, true);
        var reachabilityEpisode = ClassifyExternalSupportedReachability(
            source, new[] { source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Unresolved } }, false, true, 7, true);
        var reachabilityNoEvidence = ClassifyExternalSupportedReachability(
            Array.Empty<AIrhythmExternalEvidence>(), Array.Empty<AIrhythmExternalEvidence>(), true, true, 7, false);
        var reachabilityNoFollowup = ClassifyExternalSupportedReachability(
            source, new[] { source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Unresolved } }, false, false, 0, true);
        var strongLocalSequenceSearchQuery = BuildExternalProviderSearchQuery(
            "税金で買った本(5)",
            AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix,
            AIrhythmNumericParenthesizedClass.LocalSequence,
            true);
        var ambiguousSearchQuery = BuildExternalProviderSearchQuery(
            "作品(5)",
            AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix,
            AIrhythmNumericParenthesizedClass.Ambiguous,
            false);
        var likelyYearSearchQuery = BuildExternalProviderSearchQuery(
            "作品(2020)",
            AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix,
            AIrhythmNumericParenthesizedClass.LikelyYear,
            false);
        var adjustmentValue = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentValue(adjustmentStrengthIdentity);
        var result = string.Equals(diversifiedRelation, "candidate:container_work_diversified", StringComparison.Ordinal)
            && string.Equals(repeatedRelation, "candidate:container_work_repeated", StringComparison.Ordinal)
            && string.Equals(unconfirmedRelation, "candidate:container_work_unconfirmed", StringComparison.Ordinal)
            && string.Equals(promoRelation, "related:promo", StringComparison.Ordinal)
            && string.Equals(previewRelation, "related:preview", StringComparison.Ordinal)
            && string.Equals(recapRelation, "related:recap", StringComparison.Ordinal)
            && string.Equals(numericLikelyYearRelation, "candidate:numeric_likely_year", StringComparison.Ordinal)
            && string.Equals(numericLocalSequenceRelation, "candidate:numeric_local_sequence", StringComparison.Ordinal)
            && string.Equals(numericAmbiguousRelation, "candidate:numeric_ambiguous", StringComparison.Ordinal)
            && string.Equals(nonAmbiguousRelation, "provider:original", StringComparison.Ordinal)
            && supportedVerdict == nameof(AIrhythmExternalEvidenceVerdict.Supported)
            && supportedReason == nameof(AIrhythmExternalEvidenceVerdictReason.ProviderConfidence)
            && numericSupportedVerdict == nameof(AIrhythmExternalEvidenceVerdict.Supported)
            && numericSupportedReason == nameof(AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch)
            && numericConflictingVerdict == nameof(AIrhythmExternalEvidenceVerdict.Conflicting)
            && numericConflictingReason == nameof(AIrhythmExternalEvidenceVerdictReason.EpisodeNumberConflict)
            && nonAmbiguousVerdict == nameof(AIrhythmExternalEvidenceVerdict.Unresolved)
            && nonAmbiguousReason == nameof(AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence)
            && summaryNoEvidence.Status == AIrhythmExternalEvidenceSummaryStatus.NoEvidence
            && summarySupported.Status == AIrhythmExternalEvidenceSummaryStatus.Supported
            && summarySupported.Total == 2 && summarySupported.Supported == 1 && summarySupported.Unresolved == 1 && summarySupported.Conflicting == 0
            && summaryUnresolved.Status == AIrhythmExternalEvidenceSummaryStatus.UnresolvedOnly
            && summaryConflicting.Status == AIrhythmExternalEvidenceSummaryStatus.Conflicting
            && summaryConflicting.Total == 2 && summaryConflicting.Supported == 1 && summaryConflicting.Conflicting == 1
            && evaluationNoEvidence == AIrhythmExternalEvidenceEvaluationFlags.None
            && evaluationSupported == AIrhythmExternalEvidenceEvaluationFlags.Supported
            && evaluationUnresolved == AIrhythmExternalEvidenceEvaluationFlags.Unresolved
            && evaluationConflicting == AIrhythmExternalEvidenceEvaluationFlags.Conflicting
            && gateNoEvidence == AIrhythmExternalEvidenceConfidenceGate.NotApplicable
            && gateSupported == AIrhythmExternalEvidenceConfidenceGate.Allowed
            && gateUnresolved == AIrhythmExternalEvidenceConfidenceGate.Neutral
            && gateConflicting == AIrhythmExternalEvidenceConfidenceGate.Blocked
            && adjustmentNoEvidence == AIrhythmExternalEvidenceAdjustmentCandidate.None
            && adjustmentSupported == AIrhythmExternalEvidenceAdjustmentCandidate.Eligible
            && adjustmentUnresolved == AIrhythmExternalEvidenceAdjustmentCandidate.None
            && adjustmentConflicting == AIrhythmExternalEvidenceAdjustmentCandidate.None
            && adjustmentKindIdentity == AIrhythmExternalEvidenceAdjustmentKind.IdentitySupport
            && adjustmentKindEpisode == AIrhythmExternalEvidenceAdjustmentKind.EpisodeSupport
            && adjustmentKindUnresolved == AIrhythmExternalEvidenceAdjustmentKind.None
            && normalizedKindBlocked == AIrhythmExternalEvidenceAdjustmentKind.None
            && adjustmentStrengthIdentity == AIrhythmExternalEvidenceAdjustmentStrength.Weak
            && adjustmentStrengthEpisode == AIrhythmExternalEvidenceAdjustmentStrength.Moderate
            && adjustmentStrengthUnresolved == AIrhythmExternalEvidenceAdjustmentStrength.None
            && adjustmentStrengthBlocked == AIrhythmExternalEvidenceAdjustmentStrength.None
            && Math.Abs(shadowAdjustmentValueIdentity - 0.25d) < 0.000001d
            && Math.Abs(shadowAdjustmentValueEpisode - 0.75d) < 0.000001d
            && Math.Abs(shadowAdjustmentValueUnresolved) < 0.000001d
            && Math.Abs(shadowAdjustmentValueBlocked) < 0.000001d
            && rawCoordinateSweep.NonZeroCandidates == 1
            && rawCoordinateDetail.Length == 1
            && rawCoordinateDetail[0].Title == "raw-shadow-2"
            && Math.Abs(rawCoordinateDetail[0].CurrentDeviationRawScore - 50.0d) < 0.000001d
            && Math.Abs(rawCoordinateDetail[0].ShadowDeviationRawScore - 50.25d) < 0.000001d
            && aliasMatchedVerdict == AIrhythmExternalEvidenceVerdict.Supported.ToString()
            && aliasMatchedReason == AIrhythmExternalEvidenceVerdictReason.AliasMatch.ToString()
            && aliasMismatchedVerdict == AIrhythmExternalEvidenceVerdict.Unresolved.ToString()
            && aliasMismatchedReason == AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence.ToString()
            && reachabilitySearchConfirmed == AIrhythmExternalSupportedReachability.SearchConfirmed
            && reachabilityAlias == AIrhythmExternalSupportedReachability.AliasFollowupAvailable
            && reachabilityEpisode == AIrhythmExternalSupportedReachability.EpisodeFollowupAvailable
            && reachabilityNoEvidence == AIrhythmExternalSupportedReachability.NoProviderEvidence
            && reachabilityNoFollowup == AIrhythmExternalSupportedReachability.UnresolvedNoFollowupPath
            && string.Equals(strongLocalSequenceSearchQuery, "税金で買った本", StringComparison.Ordinal)
            && string.Equals(ambiguousSearchQuery, "作品(5)", StringComparison.Ordinal)
            && string.Equals(likelyYearSearchQuery, "作品(2020)", StringComparison.Ordinal)
            && Math.Abs(adjustmentValue - 0.25d) < 0.000001d
            ? "PASS"
            : "FAIL";

        WriteDeveloperLog($"external evidence interpretation selftest result={result} diversified={diversifiedRelation} repeated={repeatedRelation} unconfirmed={unconfirmedRelation} promo={promoRelation} preview={previewRelation} recap={recapRelation} numericLikelyYear={numericLikelyYearRelation} numericLocalSequence={numericLocalSequenceRelation} numericAmbiguous={numericAmbiguousRelation} nonAmbiguous={nonAmbiguousRelation} verdictSupported={supportedVerdict} verdictSupportedReason={supportedReason} verdictNumericSupported={numericSupportedVerdict} verdictNumericSupportedReason={numericSupportedReason} verdictNumericConflict={numericConflictingVerdict} verdictNumericConflictReason={numericConflictingReason} verdictNonAmbiguous={nonAmbiguousVerdict} verdictNonAmbiguousReason={nonAmbiguousReason} aliasMatchedVerdict={aliasMatchedVerdict} aliasMatchedReason={aliasMatchedReason} aliasMismatchedVerdict={aliasMismatchedVerdict} aliasMismatchedReason={aliasMismatchedReason} summaryNoEvidence={summaryNoEvidence.Status} summarySupported={summarySupported.Status}:{summarySupported.Supported}/{summarySupported.Total} summaryUnresolved={summaryUnresolved.Status}:{summaryUnresolved.Unresolved}/{summaryUnresolved.Total} summaryConflicting={summaryConflicting.Status}:{summaryConflicting.Conflicting}/{summaryConflicting.Total} evaluationNoEvidence={evaluationNoEvidence} evaluationSupported={evaluationSupported} evaluationUnresolved={evaluationUnresolved} evaluationConflicting={evaluationConflicting} gateNoEvidence={gateNoEvidence} gateSupported={gateSupported} gateUnresolved={gateUnresolved} gateConflicting={gateConflicting} adjustmentNoEvidence={adjustmentNoEvidence} adjustmentSupported={adjustmentSupported} adjustmentUnresolved={adjustmentUnresolved} adjustmentConflicting={adjustmentConflicting} adjustmentKindIdentity={adjustmentKindIdentity} adjustmentKindEpisode={adjustmentKindEpisode} adjustmentKindUnresolved={adjustmentKindUnresolved} adjustmentKindBlockedNormalized={normalizedKindBlocked} adjustmentStrengthIdentity={adjustmentStrengthIdentity} adjustmentStrengthEpisode={adjustmentStrengthEpisode} adjustmentStrengthUnresolved={adjustmentStrengthUnresolved} adjustmentStrengthBlocked={adjustmentStrengthBlocked} shadowAdjustmentValueIdentity={shadowAdjustmentValueIdentity:0.00} shadowAdjustmentValueEpisode={shadowAdjustmentValueEpisode:0.00} shadowAdjustmentValueUnresolved={shadowAdjustmentValueUnresolved:0.0} shadowAdjustmentValueBlocked={shadowAdjustmentValueBlocked:0.0} rawCoordinateNonZero={rawCoordinateSweep.NonZeroCandidates} rawCoordinateDisplayChanged={rawCoordinateSweep.DisplayScoreChangedCandidates} rawCoordinateRankChanged={rawCoordinateSweep.RankChangedCandidates} rawCoordinateDetailCount={rawCoordinateDetail.Length} reachabilitySearchConfirmed={reachabilitySearchConfirmed} reachabilityAlias={reachabilityAlias} reachabilityEpisode={reachabilityEpisode} reachabilityNoEvidence={reachabilityNoEvidence} reachabilityNoFollowup={reachabilityNoFollowup} strongLocalSequenceSearchQuery={strongLocalSequenceSearchQuery} ambiguousSearchQuery={ambiguousSearchQuery} likelyYearSearchQuery={likelyYearSearchQuery} adjustmentStrengthPolicy=identity_weak_episode_moderate_relation_weak_reserved shadowAdjustmentValuePolicy=raw_coordinate_applied_weak_0.25_moderate_0.75_supported_only adjustmentRelationPolicy=reserved_until_relation_specific_supported_reason adjustmentValue={adjustmentValue:0.00} cacheWrite=False recommendationMutation=True identityRewrite=False scoreRewrite=True reasonRewrite=False orderingRewrite=score_derived network=False budgetDelta=0");
    }

    private static void LogExternalEvidenceCacheRelationState(string phase)
    {
        AIrhythmExternalEvidence[] snapshot;
        AIrhythmExternalEvidenceSummaryProjection[] projectionSnapshot;
        lock (Gate)
        {
            snapshot = ExternalEvidenceCache.Values.ToArray();
            projectionSnapshot = ExternalEvidenceSummaryByEvent.Values.ToArray();
        }

        var leading = snapshot
            .Where(item => item.Relation.StartsWith("candidate:container_work_", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var diversified = leading.Count(item => string.Equals(item.Relation, "candidate:container_work_diversified", StringComparison.OrdinalIgnoreCase));
        var repeated = leading.Count(item => string.Equals(item.Relation, "candidate:container_work_repeated", StringComparison.OrdinalIgnoreCase));
        var unconfirmed = leading.Count(item => string.Equals(item.Relation, "candidate:container_work_unconfirmed", StringComparison.OrdinalIgnoreCase));
        var derived = snapshot
            .Where(item => item.Relation.StartsWith("related:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var promo = derived.Count(item => string.Equals(item.Relation, "related:promo", StringComparison.OrdinalIgnoreCase));
        var preview = derived.Count(item => string.Equals(item.Relation, "related:preview", StringComparison.OrdinalIgnoreCase));
        var recap = derived.Count(item => string.Equals(item.Relation, "related:recap", StringComparison.OrdinalIgnoreCase));
        var numeric = snapshot
            .Where(item => item.Relation.StartsWith("candidate:numeric_", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var numericLikelyYear = numeric.Count(item => string.Equals(item.Relation, "candidate:numeric_likely_year", StringComparison.OrdinalIgnoreCase));
        var numericLocalSequence = numeric.Count(item => string.Equals(item.Relation, "candidate:numeric_local_sequence", StringComparison.OrdinalIgnoreCase));
        var numericAmbiguous = numeric.Count(item => string.Equals(item.Relation, "candidate:numeric_ambiguous", StringComparison.OrdinalIgnoreCase));
        var numericUnconfirmed = numeric.Count(item => string.Equals(item.Relation, "candidate:numeric_unconfirmed", StringComparison.OrdinalIgnoreCase));
        var verdictSupported = snapshot.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported);
        var verdictUnresolved = snapshot.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Unresolved);
        var verdictConflicting = snapshot.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Conflicting);
        var verdictReasonCanonicalTitle = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch);
        var verdictReasonAlias = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.AliasMatch);
        var verdictReasonProviderConfidence = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.ProviderConfidence);
        var verdictReasonEpisodeMatch = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch);
        var verdictReasonEpisodeConflict = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.EpisodeNumberConflict);
        var verdictReasonInsufficient = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence);
        var verdictReasonNotApplicable = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.NotApplicable);
        var sample = snapshot
            .Where(item => item.Relation.StartsWith("candidate:container_work_", StringComparison.OrdinalIgnoreCase)
                || item.Relation.StartsWith("candidate:numeric_", StringComparison.OrdinalIgnoreCase)
                || item.Relation.StartsWith("related:", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.AcquiredAt)
            .FirstOrDefault();
        var projectedNoEvidence = projectionSnapshot.Count(item => item.Summary.Status == AIrhythmExternalEvidenceSummaryStatus.NoEvidence);
        var projectedUnresolved = projectionSnapshot.Count(item => item.Summary.Status == AIrhythmExternalEvidenceSummaryStatus.UnresolvedOnly);
        var projectedSupported = projectionSnapshot.Count(item => item.Summary.Status == AIrhythmExternalEvidenceSummaryStatus.Supported);
        var projectedConflicting = projectionSnapshot.Count(item => item.Summary.Status == AIrhythmExternalEvidenceSummaryStatus.Conflicting);
        var projectedAdjustmentKindNone = projectionSnapshot.Count(item => item.AdjustmentKind == AIrhythmExternalEvidenceAdjustmentKind.None);
        var projectedAdjustmentKindIdentity = projectionSnapshot.Count(item => item.AdjustmentKind == AIrhythmExternalEvidenceAdjustmentKind.IdentitySupport);
        var projectedAdjustmentKindEpisode = projectionSnapshot.Count(item => item.AdjustmentKind == AIrhythmExternalEvidenceAdjustmentKind.EpisodeSupport);
        var projectedAdjustmentKindRelation = projectionSnapshot.Count(item => item.AdjustmentKind == AIrhythmExternalEvidenceAdjustmentKind.RelationSupport);

        WriteDeveloperLog($"external evidence cache relation phase={phase} total={snapshot.Length} leadingRelations={leading.Length} diversified={diversified} repeated={repeated} unconfirmed={unconfirmed} derivedRelations={derived.Length} promo={promo} preview={preview} recap={recap} numericRelations={numeric.Length} numericLikelyYear={numericLikelyYear} numericLocalSequence={numericLocalSequence} numericAmbiguous={numericAmbiguous} numericUnconfirmed={numericUnconfirmed} verdictSupported={verdictSupported} verdictUnresolved={verdictUnresolved} verdictConflicting={verdictConflicting} verdictReasonCanonicalTitle={verdictReasonCanonicalTitle} verdictReasonAlias={verdictReasonAlias} verdictReasonProviderConfidence={verdictReasonProviderConfidence} verdictReasonEpisodeMatch={verdictReasonEpisodeMatch} verdictReasonEpisodeConflict={verdictReasonEpisodeConflict} verdictReasonInsufficient={verdictReasonInsufficient} verdictReasonNotApplicable={verdictReasonNotApplicable} projectionTotal={projectionSnapshot.Length} projectionNoEvidence={projectedNoEvidence} projectionUnresolved={projectedUnresolved} projectionSupported={projectedSupported} projectionConflicting={projectedConflicting} projectionAdjustmentKindNone={projectedAdjustmentKindNone} projectionAdjustmentKindIdentity={projectedAdjustmentKindIdentity} projectionAdjustmentKindEpisode={projectedAdjustmentKindEpisode} projectionAdjustmentKindRelation={projectedAdjustmentKindRelation} sampleProvider={sample?.ProviderId ?? string.Empty} sampleEntity={sample?.EntityId ?? string.Empty} sampleTitle={sample?.CanonicalTitle ?? string.Empty} sampleRelation={sample?.Relation ?? string.Empty}");
    }

    private static void AddExternalLookupTrace(
        string sourceTitle,
        string queryTitle,
        AIrhythmExternalEvidenceResult result,
        IReadOnlyList<AIrhythmExternalEvidence> evidence)
    {
        var titles = evidence
            .Select(item => item.CanonicalTitle)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        lock (Gate)
        {
            ExternalLookupTraces.Insert(0, new AIrhythmExternalLookupTrace(
                DateTimeOffset.Now, sourceTitle, queryTitle, result.ProviderId, result.Operation, result.Code, titles));
            if (ExternalLookupTraces.Count > ExternalLookupTraceLimit)
                ExternalLookupTraces.RemoveRange(ExternalLookupTraceLimit, ExternalLookupTraces.Count - ExternalLookupTraceLimit);
        }
    }


#endif

    private static int GetLeadingContainerProbePriority(
        AIrhythmExternalEvidenceNeedReason reason,
        string query,
        AIrhythmLeadingContainerParts parts,
        AIrhythmLeadingContainerLocalContext localContext)
    {
        if (reason != AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate)
            return 0;

        // 外部情報 — Retain the local evidence two-level: Repeated means the same leading container recurs on the
        // service, while Diversified additionally proves that it wraps multiple distinct work candidates. Only
        // the stronger Diversified evidence may raise bounded external-probe priority. Repeated-only remains
        // diagnostic/downstream evidence and never rewrites identity.
        var repetitionBoost = localContext.IsDiversified ? 25 : 0;

        if (!string.IsNullOrWhiteSpace(parts.WorkCandidate)
            && string.Equals(query, parts.WorkCandidate, StringComparison.OrdinalIgnoreCase))
            return 100 + repetitionBoost;

        if (!string.IsNullOrWhiteSpace(parts.EmbeddedTopic)
            && string.Equals(query, parts.EmbeddedTopic, StringComparison.OrdinalIgnoreCase))
            return -40 + repetitionBoost;

        return repetitionBoost;
    }

    private static IReadOnlyDictionary<string, AIrhythmLeadingContainerLocalContext> BuildLeadingContainerLocalContexts(
        IReadOnlyList<TvAirProgramEventDto> events)
    {
        var groups = new Dictionary<string, (int EventCount, HashSet<string> Works)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events)
        {
            var service = new AIrhythmServiceIdentity(item.NetworkId, item.TransportStreamId, item.ServiceId);
            if (!service.IsValid)
                continue;

            var parts = AIrhythmRecommendationEngine.ParseLeadingContainerParts(item.Title);
            if (string.IsNullOrWhiteSpace(parts.Container) || string.IsNullOrWhiteSpace(parts.WorkCandidate))
                continue;

            var key = $"{service}|{parts.Container}";
            if (!groups.TryGetValue(key, out var group))
                group = (0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            group.EventCount++;
            group.Works.Add(parts.WorkCandidate);
            groups[key] = group;
        }

        return groups.ToDictionary(
            pair => pair.Key,
            pair => new AIrhythmLeadingContainerLocalContext(
                pair.Value.EventCount,
                pair.Value.Works.Count,
                pair.Value.EventCount >= 2 && pair.Value.Works.Count >= 2
                    ? AIrhythmLeadingContainerEvidenceLevel.Diversified
                    : pair.Value.EventCount >= 2
                        ? AIrhythmLeadingContainerEvidenceLevel.Repeated
                        : AIrhythmLeadingContainerEvidenceLevel.None),
            StringComparer.OrdinalIgnoreCase);
    }

    private static AIrhythmLeadingContainerLocalContext GetLeadingContainerLocalContext(
        TvAirProgramEventDto item,
        IReadOnlyDictionary<string, AIrhythmLeadingContainerLocalContext> contexts)
    {
        var service = new AIrhythmServiceIdentity(item.NetworkId, item.TransportStreamId, item.ServiceId);
        var parts = AIrhythmRecommendationEngine.ParseLeadingContainerParts(item.Title);
        if (!service.IsValid || string.IsNullOrWhiteSpace(parts.Container))
            return default;

        return contexts.TryGetValue($"{service}|{parts.Container}", out var context) ? context : default;
    }

    private static string BuildLeadingContainerProbeKey(string? sourceTitle)
    {
        if (string.IsNullOrWhiteSpace(sourceTitle))
            return string.Empty;

        // 外部情報 — Use the same title-preprocessing boundary as EvaluateExternalEvidenceNeed. Pure ARIB/broadcast
        // attributes (e.g. [映][初]) may legally precede a leading container. They must not make the grouping key
        // disagree with the ambiguity detector and explode one container class into per-title probes.
        var normalized = AIrhythmRecommendationEngine.StripNonIdentityBroadcastAnnotations(
            sourceTitle.Normalize(NormalizationForm.FormKC)).Trim();
        var match = Regex.Match(normalized, @"^【([^【】]{2,})】\s*\S+");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static int GetNumericParenthesizedProbePriority(
        AIrhythmExternalEvidenceNeedReason reason,
        AIrhythmNumericParenthesizedClass numericClass,
        AIrhythmNumericParenthesizedProbeContext context)
    {
        if (reason != AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix)
            return 0;

        // 外部情報 — External lookup is bounded, so rank by information value instead of treating every unresolved
        // parenthesized number alike. This does not decide identity: local siblings only make a provider answer
        // more useful for resolving the relation. A lone number remains ambiguous, but should not monopolize the
        // four-call budget merely for being lone.
        var score = numericClass switch
        {
            AIrhythmNumericParenthesizedClass.LikelyYear => 20,
            AIrhythmNumericParenthesizedClass.LocalSequence => 55,
            _ => 65
        };

        if (numericClass == AIrhythmNumericParenthesizedClass.Ambiguous)
        {
            if (context.DistinctLocalValues >= 2)
                score += 30;
            else
                score -= 20;

            if (context.NearestLocalDistance is > 0 and <= 2)
                score += 15;

            if (context.Value is > 0 and <= 99)
                score += 10;
            else if (context.Value > 999)
                score -= 15;
        }

        return score;
    }

    private static int GetExternalLookupProbePriority(string? sourceTitle, string query)
    {
        var source = (sourceTitle ?? string.Empty).Normalize(NormalizationForm.FormKC);
        var value = (query ?? string.Empty).Trim();
        if (value.Length < 2)
            return int.MinValue;

        var score = 0;

        // 作品識別 — Japanese broadcast containers often quote the actual work title.
        if (Regex.IsMatch(source, @"[「『][^」』]{2,80}[」』]")
            && !Regex.IsMatch(value, @"[「『」』]"))
            score += 120;

        // 外部情報 — Compact work-level strings are useful broad probes for external catalogues.
        if (value.Length <= 12)
            score += 60;
        else if (value.Length <= 24)
            score += 40;
        else if (value.Length <= 48)
            score += 15;

        // 処理条件 — International/romanized catalogue names are often indexed as-is.
        if (Regex.IsMatch(value, @"[A-Za-z]"))
            score += 15;

        // 処理条件 — Prefer cleaned forms over strings that still contain broadcast wrappers.
        if (!Regex.IsMatch(value, @"^【") && !Regex.IsMatch(value, @"(?:PR|ＰＲ|予告|みどころ|総集編)$", RegexOptions.IgnoreCase))
            score += 20;

        // 作品識別 — Episode markers are useful as source evidence, but search itself should start at work level.
        if (Regex.IsMatch(value, @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃]\s*[0-9０-９]+)", RegexOptions.IgnoreCase))
            score -= 25;

        return score;
    }

    private static (bool ShouldAttempt, string SkipReason, long AgeSeconds) GetExternalLookupAttemptDecision(string queryTitle)
    {
        lock (Gate)
        {
            if (!ExternalLookupAttempts.TryGetValue(queryTitle, out var attemptedAt))
                return (true, "none", -1);

            var age = DateTimeOffset.Now - attemptedAt;
            if (age >= ExternalLookupAttemptTtl)
                return (true, "ttl_expired", Math.Max(0L, (long)age.TotalSeconds));

            return (false, "attempt_ttl", Math.Max(0L, (long)age.TotalSeconds));
        }
    }

    private static void MarkExternalLookupAttempt(string queryTitle, DateTimeOffset attemptedAt)
    {
        lock (Gate)
        {
            ExternalLookupAttempts[queryTitle] = attemptedAt;
            if (ExternalLookupAttempts.Count <= ExternalEvidenceCacheLimit)
                return;
            foreach (var key in ExternalLookupAttempts.OrderBy(pair => pair.Value).Take(ExternalLookupAttempts.Count - ExternalEvidenceCacheLimit).Select(pair => pair.Key).ToArray())
                ExternalLookupAttempts.Remove(key);
        }
    }

    internal static AIrhythmExternalEvidenceAdjustmentKind DetermineExternalEvidenceAdjustmentKind(IReadOnlyList<AIrhythmExternalEvidence> evidence)
    {
        var supported = evidence
            .Where(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported)
            .ToArray();
        if (supported.Length == 0)
            return AIrhythmExternalEvidenceAdjustmentKind.None;

        if (supported.Any(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch))
            return AIrhythmExternalEvidenceAdjustmentKind.EpisodeSupport;

        if (supported.Any(item => item.VerdictReason is AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch
            or AIrhythmExternalEvidenceVerdictReason.AliasMatch
            or AIrhythmExternalEvidenceVerdictReason.ProviderConfidence))
            return AIrhythmExternalEvidenceAdjustmentKind.IdentitySupport;

        // 外部情報 — RelationSupport is reserved for a future relation-specific Supported reason. Existing
        // title/alias/provider support must not be reinterpreted as proof of the relation itself.
        return AIrhythmExternalEvidenceAdjustmentKind.None;
    }

    internal static AIrhythmExternalEvidenceVerdictReason DetermineExternalEvidenceSupportingVerdictReason(IReadOnlyList<AIrhythmExternalEvidence> evidence)
    {
        var supportedReasons = evidence
            .Where(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported)
            .Select(item => item.VerdictReason)
            .ToArray();
        if (supportedReasons.Contains(AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch))
            return AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch;
        if (supportedReasons.Contains(AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch))
            return AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch;
        if (supportedReasons.Contains(AIrhythmExternalEvidenceVerdictReason.AliasMatch))
            return AIrhythmExternalEvidenceVerdictReason.AliasMatch;
        if (supportedReasons.Contains(AIrhythmExternalEvidenceVerdictReason.ProviderConfidence))
            return AIrhythmExternalEvidenceVerdictReason.ProviderConfidence;
        return AIrhythmExternalEvidenceVerdictReason.NotApplicable;
    }

    public static long GetExternalEvidenceGeneration()
    {
        lock (Gate) return _externalEvidenceGeneration;
    }

    public static IReadOnlyDictionary<string, string> GetExternalPreEvaluationCanonicalTitles()
    {
        var now = DateTimeOffset.Now;
        lock (Gate)
        {
            var expiredKeys = ExternalCanonicalTitleBySource
                .Where(pair => now - pair.Value.UpdatedAt >= ExternalEvidenceTtl)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in expiredKeys)
                ExternalCanonicalTitleBySource.Remove(key);
            if (expiredKeys.Length > 0)
                _externalEvidenceGeneration++;

            return ExternalCanonicalTitleBySource.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.CanonicalTitle,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public static (AIrhythmExternalEvidenceSummaryStatus Summary, AIrhythmExternalEvidenceAdjustmentKind AdjustmentKind, AIrhythmExternalEvidenceVerdictReason SupportingVerdictReason) GetExternalEvidenceProjection(TvAirProgramEventDto item)
    {
        var key = ExternalEvidenceEventKey(item.NetworkId, item.TransportStreamId, item.ServiceId, item.EventNumber, item.Start);
        lock (Gate)
        {
            if (!ExternalEvidenceSummaryByEvent.TryGetValue(key, out var projection))
                return (AIrhythmExternalEvidenceSummaryStatus.NoEvidence, AIrhythmExternalEvidenceAdjustmentKind.None, AIrhythmExternalEvidenceVerdictReason.NotApplicable);
            if (DateTimeOffset.Now - projection.UpdatedAt >= ExternalEvidenceTtl)
            {
                ExternalEvidenceSummaryByEvent.Remove(key);
                _externalEvidenceGeneration++;
                return (AIrhythmExternalEvidenceSummaryStatus.NoEvidence, AIrhythmExternalEvidenceAdjustmentKind.None, AIrhythmExternalEvidenceVerdictReason.NotApplicable);
            }
            return (projection.Summary.Status, projection.AdjustmentKind, projection.SupportingVerdictReason);
        }
    }

    private static void SetExternalEvidenceSummaryProjection(
        TvAirProgramEventDto item,
        AIrhythmExternalEvidenceSummary summary,
        AIrhythmExternalEvidenceAdjustmentKind adjustmentKind,
        AIrhythmExternalEvidenceVerdictReason supportingVerdictReason,
        DateTimeOffset now,
        IReadOnlyList<AIrhythmExternalEvidence>? evidence = null)
    {
        var key = ExternalEvidenceEventKey(item.NetworkId, item.TransportStreamId, item.ServiceId, item.EventNumber, item.Start);
        lock (Gate)
        {
            ExternalEvidenceSummaryByEvent[key] = new AIrhythmExternalEvidenceSummaryProjection(summary, adjustmentKind, supportingVerdictReason, now);
            if (summary.Status == AIrhythmExternalEvidenceSummaryStatus.Supported && evidence is not null)
            {
                var supportedTitles = evidence
                    .Where(x => x.Verdict == AIrhythmExternalEvidenceVerdict.Supported && !string.IsNullOrWhiteSpace(x.CanonicalTitle))
                    .Select(x => x.CanonicalTitle.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (supportedTitles.Length == 1)
                {
                    var sourceKey = AIrhythmRecommendationEngine.ExternalPreEvaluationTitleKey(item.Title);
                    if (sourceKey.Length > 0)
                        ExternalCanonicalTitleBySource[sourceKey] = (supportedTitles[0], now);
                }
                if (ExternalCanonicalTitleBySource.Count > ExternalEvidenceCacheLimit)
                {
                    foreach (var staleKey in ExternalCanonicalTitleBySource
                        .OrderBy(pair => pair.Value.UpdatedAt)
                        .Take(ExternalCanonicalTitleBySource.Count - ExternalEvidenceCacheLimit)
                        .Select(pair => pair.Key)
                        .ToArray())
                        ExternalCanonicalTitleBySource.Remove(staleKey);
                }
            }
            if (ExternalEvidenceSummaryByEvent.Count > ExternalEvidenceCacheLimit)
            {
                foreach (var staleKey in ExternalEvidenceSummaryByEvent
                    .OrderBy(pair => pair.Value.UpdatedAt)
                    .Take(ExternalEvidenceSummaryByEvent.Count - ExternalEvidenceCacheLimit)
                    .Select(pair => pair.Key)
                    .ToArray())
                    ExternalEvidenceSummaryByEvent.Remove(staleKey);
            }
            _externalEvidenceGeneration++;
        }
    }

    private static string ExternalEvidenceEventKey(int networkId, int transportStreamId, int serviceId, int eventNumber, DateTimeOffset start)
        => $"{networkId}:{transportStreamId}:{serviceId}:{eventNumber}:{start.UtcDateTime.Ticks}";

    private static void AddExternalEvidence(IEnumerable<AIrhythmExternalEvidence> items)
    {
        var materialized = items?.Where(x => x is not null).ToArray() ?? Array.Empty<AIrhythmExternalEvidence>();
        lock (Gate)
        {
            foreach (var item in materialized)
            {
                var key = $"{item.ProviderId}|{item.EntityId}|{item.Season?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{item.Episode?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{item.CanonicalTitle}";
                ExternalEvidenceCache[key] = item;
            }
            if (ExternalEvidenceCache.Count > ExternalEvidenceCacheLimit)
            {
                foreach (var key in ExternalEvidenceCache.OrderBy(pair => pair.Value.AcquiredAt).Take(ExternalEvidenceCache.Count - ExternalEvidenceCacheLimit).Select(pair => pair.Key).ToArray())
                    ExternalEvidenceCache.Remove(key);
            }
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        // 外部情報 — Reuse lookup results that were already obtained through the Host-managed/manual refresh path.
        // This adds no network traffic; it only turns transient provider evidence into long-lived learner input.
        PersistNormalizationLearnerExternalStructuralEvidence(materialized);
#endif
    }

    private static void PruneExternalEvidenceState(DateTimeOffset now)
    {
        lock (Gate)
        {
            foreach (var key in ExternalEvidenceCache.Where(pair => now - pair.Value.AcquiredAt >= ExternalEvidenceTtl).Select(pair => pair.Key).ToArray())
                ExternalEvidenceCache.Remove(key);
            var expiredProjectionKeys = ExternalEvidenceSummaryByEvent.Where(pair => now - pair.Value.UpdatedAt >= ExternalEvidenceTtl).Select(pair => pair.Key).ToArray();
            foreach (var key in expiredProjectionKeys)
                ExternalEvidenceSummaryByEvent.Remove(key);
            var expiredCanonicalTitleKeys = ExternalCanonicalTitleBySource.Where(pair => now - pair.Value.UpdatedAt >= ExternalEvidenceTtl).Select(pair => pair.Key).ToArray();
            foreach (var key in expiredCanonicalTitleKeys)
                ExternalCanonicalTitleBySource.Remove(key);
            foreach (var key in ExternalLookupAttempts.Where(pair => now - pair.Value >= ExternalLookupAttemptTtl).Select(pair => pair.Key).ToArray())
                ExternalLookupAttempts.Remove(key);
            if (expiredProjectionKeys.Length > 0 || expiredCanonicalTitleKeys.Length > 0)
                _externalEvidenceGeneration++;
        }
    }



    internal static bool IsUsefulHistory(TvAirRecordingHistoryDto item)
    {
        if (string.IsNullOrWhiteSpace(item.ProgramTitle) || !item.ResultFinalized)
            return false;

        // 録画実績・復旧 — Preferenceが見るのは「録画が成立したか／取消・失敗したか」まで。 OutputFilePath / FileCreated / Drop / Error / Scramble
        // / 再生状況など、 録画成立後のデータは一切Evidenceへ入れない。
        var state = $"{item.Result} {item.EndReason}";
        return !ContainsAny(state, "fail", "error", "cancel", "abort", "失敗", "取消", "中止");
    }


    private static AIrhythmSettings ReadSettings(ITvAirPluginRuntimeContext context, out long? revision)
    {
        revision = null;
        try
        {
            var result = context.Storage.Get("settings", "main");
            if (!result.Succeeded || result.Value is null)
                return new();
            revision = result.Value.Revision;
            var json = result.Value.Value?.ToString();
            if (string.IsNullOrWhiteSpace(json))
                return new();

            var settings = JsonSerializer.Deserialize<AIrhythmSettings>(json, JsonOptions) ?? new();
            return new(Math.Clamp(settings.Limit, 10, 30), settings.Preferred ?? string.Empty, settings.Excluded ?? string.Empty);
        }
        catch
        {
            return new();
        }
    }

    private static void ReportSnapshot(ITvAirPluginRuntimeContext context, AIrhythmRuntimeSnapshot snapshot)
    {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        ReportDeveloperSnapshot(context, snapshot);
#endif
    }

    private static void ReportFailure(ITvAirPluginRuntimeContext context, string phase, Exception exception)
    {
        try
        {
            context.Logs.Write(new TvAirLogWriteDto
            {
                Level = "Info",
                Category = "AI-rhythm",
                Message = $"{phase} result=ERROR type={exception.GetType().Name} message={exception.Message}"
            });
        }
        catch { }
    }

    private static void Invalidate(string eventType)
    {
        lock (Gate)
        {
            _cachedSnapshot = null;
            _cacheGeneration++;
        }
        // 保存・読込 — Release the previous snapshot/recommendation graph immediately. Do this outside Gate to keep
        // the lock order one-way (Score cache -> DataState Gate) and avoid deadlock.
        AIrhythmRecommendationEngine.InvalidateScoreResultCache();
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        LogSnapshotInvalidated(eventType);
#endif
    }

    // 表示・テーマ — Runtime data changes invalidate the snapshot cache. Theme synchronization is
    // presentation-only: Host-contract HotApply dispatches Host-owned Theme generation/revision/contract
    // through the declared Host-shell handler. Theme changes are intentionally excluded from Content Render
    // invalidation semantics.
    private static readonly string[] RefreshEventTypes =
    {
        "ProgramGuideUpdated",
        "ReservationAdded",
        "ReservationUpdated",
        "ReservationRemoved",
        "ReservationEnabled",
        "ReservationDisabled",
        "ReservationConflictChanged",
        "RecordingStarted",
        "RecordingCompleted",
        "RecordingFailed",
        "RecordingResultFinalized",
        "SettingsChanged",
        "PluginPermissionChanged"
    };

    private static AIrhythmRuntimeSnapshot Empty(
        string error,
        AIrhythmSettings? settings = null,
        long? revision = null
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        , AIrhythmRuntimeDiagnostics? diagnostics = null
#endif
        )
        => new(
            false,
            error,
            Array.Empty<TvAirProgramEventDto>(),
            Array.Empty<TvAirReservationDto>(),
            Array.Empty<TvAirReservationDto>(),
            Array.Empty<TvAirKeywordRuleDto>(),
            Array.Empty<TvAirRecordingHistoryDto>(),
            Array.Empty<TvAirRecordingHistoryDto>(),
            Array.Empty<TvAirServiceDto>(),
            Array.Empty<TvAirTunerStatusDto>(),
            new AIrhythmAdvancedSnapshot(
                Array.Empty<TvAirRecordingSessionDto>()),
            settings ?? new(),
            revision
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            , diagnostics ?? new AIrhythmRuntimeDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<string>())
#endif
            );
}
