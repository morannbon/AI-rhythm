namespace AIrhythm.BasicPlugin;

internal sealed record AIrhythmRecoveryAttempt(
    string Identity, string ServiceName, string ProgramTitle,
    ushort NetworkId, ushort TransportStreamId, ushort ServiceId, ushort EventId,
    DateTimeOffset ScheduledStartTime, DateTimeOffset Start, DateTimeOffset End,
    string Result, string EndReason, bool? FileCreated,
    long? DropCount, long? ErrorCount, long? ScrambleCount,
    bool QualityDataAvailable, string QualitySource,
    bool? Succeeded = null, bool? ResultFinalized = null,
    string QualityCompleteness = "", string Genre = "",
    string ReservationId = "", string RecordingId = "");

// 録画実績・復旧 — One factual admission policy for durable recording recovery evidence,
// the snapshot and the discovery shelf. Cancellation is not a failed recording.
// This policy never mutates preferences, reservations, journals, or recorded facts.
internal static class AIrhythmRecoveryEvidencePolicy
{
    internal static bool IsUserStopped(TvAirRecordingHistoryDto item)
    {
        var state = $"{item.Result} {item.EndReason}";
        // Explicit manual-stop provenance is not an unsuccessful recording. Never treat a generic
        // hardware/process "stop" as a user action without a user/manual qualifier.
        if (Regex.IsMatch(state,
                @"(?:^|[^a-z])(?:manual(?:ly)?|user)[\s_./:-]*(?:stop(?:ped)?|cancel(?:led)?|abort(?:ed)?)(?:$|[^a-z])",
                RegexOptions.IgnoreCase)
            || state.Contains("手動停止", StringComparison.Ordinal)
            || state.Contains("手動中断", StringComparison.Ordinal)
            || state.Contains("ユーザー停止", StringComparison.Ordinal)
            || state.Contains("ユーザーによる停止", StringComparison.Ordinal)
            || Regex.IsMatch(state,
                @"(?:^|[^a-z])stop(?:ped)?[\s_./:-]*by[\s_./:-]*user(?:$|[^a-z])",
                RegexOptions.IgnoreCase))
            return true;
        // Unknown cancellation is not evidence of a *user* action. Preserve it as uncertain.
        return false;
    }

    // One text/file evidence gate for typed Host results and historical stored records.
    private static bool HasExplicitRecordingFailureEvidence(string? result, string? endReason, bool? fileCreated)
    {
        var status = $"{result} {endReason}";
        return status.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || status.Contains("error", StringComparison.OrdinalIgnoreCase)
            || status.Contains("失敗", StringComparison.OrdinalIgnoreCase)
            || fileCreated == false;
    }

    internal static bool IsFailure(TvAirRecordingHistoryDto item)
    {
        if (IsUserStopped(item)) return false;
        if (HasExplicitRecordingFailureEvidence(item.Result, item.EndReason, item.FileCreated))
            return true;
        if (!item.ResultFinalized || item.Succeeded) return false;
        // A typed, finalized unsuccessful result is authoritative even when the
        // human-readable status does not say "failed". Generic cancellation and
        // old unknown statuses are not proof that a recording attempt failed.
        var result = (item.Result ?? string.Empty).Trim();
        if (result.Length == 0 || Regex.IsMatch(result,
                @"^(?:unknown|pending|cancel(?:led|ed)?|abort(?:ed)?|stop(?:ped)?|中止|取消|キャンセル|未確定|不明)$",
                RegexOptions.IgnoreCase))
            return false;
        return true;
    }

    // Legacy persisted rows have no nullable typed outcome. Their conversion to the SDK's
    // non-nullable Succeeded flag must not turn an absent value into a proven failure.
    // This same admission policy is used by both historical recording facts and recovery journal.
    internal static bool HasConcreteLegacyRecoveryEvidence(string? result, string? endReason,
        bool? fileCreated, bool qualityDataAvailable, long? dropCount, long? errorCount, long? scrambleCount)
    {
        return IsSuccessfulRecordingResult(result)
            || HasExplicitRecordingFailureEvidence(result, endReason, fileCreated)
            || (qualityDataAvailable
                && ((dropCount ?? 0) > 0 || (errorCount ?? 0) > 0 || (scrambleCount ?? 0) > 0));
    }

    internal static bool HasAbnormalQuality(TvAirRecordingHistoryDto item)
        => !IsUserStopped(item) && item.QualityDataAvailable
            && ((item.DropCount ?? 0) > 0 || (item.ErrorCount ?? 0) > 0
                || (item.ScrambleCount ?? 0) > 0);

    // 録画実績・復旧 — Recording failure/quality must never depend on preference-learning admission.
    // Use this identical finalized-fact gate for the snapshot, journal and recovery display.
    internal static bool IsDefective(TvAirRecordingHistoryDto item)
        => item.ResultFinalized && (IsFailure(item) || HasAbnormalQuality(item));

    // 録画実績・復旧 — A missing failure label does not prove success (e.g. stopped, pending,
    // unknown). Do not allow such a recording to hide an earlier defective broadcast.
    internal static bool IsSuccessfulRecordingResult(string? result)
        => !string.IsNullOrWhiteSpace(result)
            && Regex.IsMatch(result.Trim(),
                @"^(?:success(?:ful)?|succeeded|complete(?:d)?|finished|ok|正常終了|録画完了|成功|完了)$",
                RegexOptions.IgnoreCase);

    // 録画実績・復旧 — A clean replacement is a finalized recording with quality evidence, not merely
    // the absence of a failed/aborted label. Unavailable quality never proves recovery.
    // FileCreated=null can mean that older Host versions did not report the field; it is not evidence
    // of a missing file. An explicit false always disqualifies the replacement.
    internal static bool IsVerifiedClean(TvAirRecordingHistoryDto item)
        => item.ResultFinalized && item.Succeeded && IsSuccessfulRecordingResult(item.Result)
            && item.QualityDataAvailable && HasVerifiedCompleteQuality(item)
            && item.FileCreated != false
            && !IsUserStopped(item) && !IsFailure(item) && !HasAbnormalQuality(item);

    // Never turn absent quality counters into zero for proof of a successful replacement.
    // Accept only an explicit completeness declaration from the recording result contract.
    internal static bool HasVerifiedCompleteQuality(TvAirRecordingHistoryDto item)
        => item.QualityDataAvailable
            && item.DropCount.HasValue && item.ErrorCount.HasValue && item.ScrambleCount.HasValue
            && Regex.IsMatch(item.QualityCompleteness?.Trim() ?? string.Empty,
                @"^(?:complete|completed|full|all|verified|完全|全項目)$", RegexOptions.IgnoreCase);

    internal static bool StillUnresolved(DateTimeOffset defectiveStart, DateTimeOffset? laterCleanStart)
        => laterCleanStart is null || laterCleanStart.Value <= defectiveStart;

    internal static bool IsLaterFutureBroadcast(DateTimeOffset defectiveStart,
        DateTimeOffset candidateStart, DateTimeOffset now)
        => candidateStart > defectiveStart && candidateStart > now;

    // 録画実績・復旧 — Quality counters are external long values. Saturate priority arithmetic
    // instead of overflowing into a negative score and silently de-prioritizing corrupt evidence.
    internal static long QualitySeverity(TvAirRecordingHistoryDto item)
    {
        var errors = Math.Max(0L, item.ErrorCount ?? 0L);
        var scrambled = Math.Max(0L, item.ScrambleCount ?? 0L);
        var drops = Math.Max(0L, item.DropCount ?? 0L);
        var available = long.MaxValue - drops;
        if (errors > available / 1_000_000L) return long.MaxValue;
        available -= errors * 1_000_000L;
        if (scrambled > available / 10_000L) return long.MaxValue;
        return errors * 1_000_000L + scrambled * 10_000L + drops;
    }
}

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
    string ReservationSource = "",
    bool? Succeeded = null);

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

