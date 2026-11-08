namespace AIrhythm.BasicPlugin;

internal sealed record AIrhythmRecoveryAttempt(
    string Identity, string ServiceName, string ProgramTitle,
    ushort NetworkId, ushort TransportStreamId, ushort ServiceId, ushort EventId,
    DateTimeOffset ScheduledStartTime, DateTimeOffset Start, DateTimeOffset End,
    string Result, string EndReason, bool? FileCreated,
    long? DropCount, long? ErrorCount, long? ScrambleCount,
    bool QualityDataAvailable, string QualitySource);

// 録画実績・復旧 — Non-authoritative compact lookup. The original recoveryAttempts monthly chunks remain the
// sole source of truth.
internal sealed record AIrhythmRecoveryLookupEntry(string Identity, string Month, string EpisodeKey, DateTimeOffset ScheduledStartTime);
internal sealed record AIrhythmRecoveryMonthFingerprint(string Month, int RowCount, string Sha256);
internal sealed record AIrhythmRecoveryLookupManifest(int SchemaVersion, AIrhythmRecoveryLookupEntry[] Entries, AIrhythmRecoveryMonthFingerprint[]? Months = null);

internal sealed record AIrhythmRecordingFact(
    string Identity,
    string ReservationId,
    string RecordingId,
    string ServiceName,
    string ProgramTitle,
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    ushort EventId,
    DateTimeOffset ScheduledStartTime,
    string Genre,
    string GenreCodes,
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    string Result,
    string EndReason,
    bool? FileCreated,
    bool ResultFinalized,
    long? DropCount = null,
    long? ErrorCount = null,
    long? ScrambleCount = null,
    bool QualityDataAvailable = false,
    string QualityCompleteness = "",
    string QualitySource = "",
    bool ReservationOriginKnown = false,
    TvAirReservationIntent ReservationIntent = TvAirReservationIntent.Unspecified,
    string ReservationSource = "");

internal sealed record NormalizationLearnerLongTermSignal(
    string HypothesisKey,
    string Kind,
    string LearnedWork,
    double Confidence,
    double RoleFitness,
    double PopulationQuality,
    double CollisionRate,
    double BoundaryIntrusionScore,
    string RelationToCurrent,
    string QualityIssue,
    double ExternalStructuralSupport,
    double ExternalProviderReliability,
    int ExternalProviderCount,
    int ExternalEvidenceCount);

internal sealed record NormalizationLearnerLongTermSnapshot(
    string HypothesisKey,
    int DistinctObservationDays,
    double MeanConfidence,
    double MeanRoleFitness,
    double MeanPopulationQuality,
    double MeanCollisionRate,
    double MeanBoundaryIntrusion,
    double Consistency,
    double Reliability,
    string Stage,
    string LastRelation,
    string LastQualityIssue,
    double MeanExternalStructuralSupport,
    double MeanExternalProviderReliability);

internal sealed record NormalizationLearnerExternalStructuralSnapshot(
    string HypothesisKey,
    double Support,
    double ProviderReliability,
    int ProviderCount,
    int EvidenceCount,
    string Stage);

internal sealed record NormalizationLearnerHypothesisSignal(
    string HypothesisKey,
    string Kind,
    string LearnedWork,
    string Decision,
    double Confidence,
    double RoleFitness,
    string RelationToCurrent,
    int DistinctCanonicalWorks);

internal sealed record NormalizationLearnerHypothesisSnapshot(
    string HypothesisKey,
    int DistinctObservationDays,
    int ValidatedDays,
    int DeferredDays,
    int RejectedDays,
    double ConfidenceEwma,
    double RoleFitnessEwma,
    double StabilityScore,
    string Stage,
    string LastDecision,
    string LastRelation);

internal sealed record AIrhythmNormalizationPromotionSignal(
    string AliasKey,
    string LearnedWork,
    string HypothesisKey,
    double Confidence,
    int ObservationDays,
    int LongTermDays);

internal sealed record AIrhythmNormalizationPromotionEntry(
    string AliasKey,
    string LearnedWork,
    string HypothesisKey,
    double Confidence,
    int ObservationDays,
    int LongTermDays,
    DateTimeOffset PromotedAt);

internal sealed record AIrhythmNormalizationPromotionState(
    int SchemaVersion,
    string LogicVersion,
    DateTimeOffset UpdatedAt,
    AIrhythmNormalizationPromotionEntry[] Entries);

