namespace AIrhythm.BasicPlugin;

internal sealed record AIrhythmSettings(int Limit = 20, string Preferred = "", string Excluded = "");
internal enum AIrhythmExternalEvidenceNeedReason
{
    None = 0,
    NumericParenthesizedSuffix,
    LeadingContainerCandidate,
    DerivedProgramMarker
}
internal enum AIrhythmDerivedProgramRelation
{
    None = 0,
    Promo,
    Preview,
    Recap
}
internal enum AIrhythmNumericParenthesizedClass
{
    None = 0,
    LikelyYear,
    LocalSequence,
    Ambiguous
}
internal readonly record struct AIrhythmNumericParenthesizedProbeContext(
    int Value,
    int DistinctLocalValues,
    int NearestLocalDistance);
internal readonly record struct AIrhythmLeadingContainerParts(
    string NormalizedTitle,
    string Container,
    string Remainder,
    string WorkCandidate,
    string EmbeddedTopic);
internal enum AIrhythmLeadingContainerEvidenceLevel
{
    None = 0,
    Repeated,
    Diversified
}
internal readonly record struct AIrhythmLeadingContainerLocalContext(
    int EventCount,
    int DistinctWorkCount,
    AIrhythmLeadingContainerEvidenceLevel EvidenceLevel)
{
    internal bool IsRepeated => EvidenceLevel is AIrhythmLeadingContainerEvidenceLevel.Repeated or AIrhythmLeadingContainerEvidenceLevel.Diversified;
    internal bool IsDiversified => EvidenceLevel == AIrhythmLeadingContainerEvidenceLevel.Diversified;
}
internal readonly record struct AIrhythmExternalEvidenceNeed(
    bool Needed,
    AIrhythmExternalEvidenceNeedReason Reason);
internal readonly record struct AIrhythmExternalEvidenceNeedSummary(
    int Total,
    int Needed,
    IReadOnlyDictionary<AIrhythmExternalEvidenceNeedReason, int> Reasons);
internal readonly record struct AIrhythmExternalUserIntentSignal(
    int AutomaticReservationCount,
    int ManualReservationCount,
    int RecordingCount)
{
    internal bool HasEvidence => AutomaticReservationCount > 0 || ManualReservationCount > 0 || RecordingCount > 0;

    // 外部情報 — External lookup priority reflects accumulated user actions, with per-source bounds.
    internal int Priority =>
        Math.Min(AutomaticReservationCount, 16) * 320
        + Math.Min(ManualReservationCount, 16) * 360
        + Math.Min(RecordingCount, 32) * 140;
}
internal sealed record AIrhythmExternalEvidenceResult(
    bool Success,
    TvAirExternalLookupResultCode Code,
    string ProviderId,
    string Operation,
    string Body);
internal enum AIrhythmExternalEvidenceVerdict
{
    Unresolved = 0,
    Supported,
    Conflicting
}

internal enum AIrhythmExternalEvidenceVerdictReason
{
    InsufficientEvidence = 0,
    CanonicalTitleMatch,
    AliasMatch,
    ProviderConfidence,
    EpisodeNumberMatch,
    EpisodeNumberConflict,
    NotApplicable
}

internal enum AIrhythmExternalEvidenceSummaryStatus
{
    NoEvidence = 0,
    UnresolvedOnly,
    Supported,
    Conflicting
}

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
[Flags]
internal enum AIrhythmExternalSupportedReachability
{
    None = 0,
    SearchConfirmed = 1,
    AliasFollowupAvailable = 2,
    EpisodeFollowupAvailable = 4,
    NoProviderEvidence = 8,
    UnresolvedNoFollowupPath = 16
}
#endif


[Flags]
internal enum AIrhythmExternalEvidenceEvaluationFlags
{
    None = 0,
    Unresolved = 1,
    Supported = 2,
    Conflicting = 4
}

internal enum AIrhythmExternalEvidenceConfidenceGate
{
    NotApplicable = 0,
    Neutral,
    Allowed,
    Blocked
}

internal enum AIrhythmExternalEvidenceAdjustmentCandidate
{
    None = 0,
    Eligible
}

internal enum AIrhythmExternalEvidenceAdjustmentKind
{
    None = 0,
    IdentitySupport,
    EpisodeSupport,
    RelationSupport
}

internal enum AIrhythmExternalEvidenceAdjustmentStrength
{
    None = 0,
    Weak,
    Moderate
}

internal readonly record struct AIrhythmExternalEvidenceSummary(
    AIrhythmExternalEvidenceSummaryStatus Status,
    int Total,
    int Supported,
    int Unresolved,
    int Conflicting);

internal readonly record struct AIrhythmExternalEvidenceSummaryProjection(
    AIrhythmExternalEvidenceSummary Summary,
    AIrhythmExternalEvidenceAdjustmentKind AdjustmentKind,
    // 保存・読込 — Retain the exact Supported provenance in the existing bounded projection; never create a
    // second evidence/detail cache for diagnostics or later scoring.
    AIrhythmExternalEvidenceVerdictReason SupportingVerdictReason,
    DateTimeOffset UpdatedAt);

internal sealed record AIrhythmExternalEvidence(
    string ProviderId,
    string EntityId,
    string CanonicalTitle,
    IReadOnlyList<string> Aliases,
    string MediaType,
    int? Season,
    int? Episode,
    DateTimeOffset? AirOrPremiereDate,
    string Relation,
    double Confidence,
    DateTimeOffset AcquiredAt,
    AIrhythmExternalEvidenceVerdict Verdict = AIrhythmExternalEvidenceVerdict.Unresolved,
    AIrhythmExternalEvidenceVerdictReason VerdictReason = AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
internal sealed record AIrhythmExternalLookupTrace(
    DateTimeOffset OccurredAt,
    string SourceTitle,
    string QueryTitle,
    string ProviderId,
    string Operation,
    TvAirExternalLookupResultCode Code,
    IReadOnlyList<string> CandidateTitles);
#endif
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
internal sealed record AIrhythmRuntimeDiagnostics(
    int ProgramRaw,
    int ProgramAccepted,
    int ReservationRaw,
    int ReservationAccepted,
    int HistoryRaw,
    int HistoryAccepted,
    int ChannelRaw,
    int ChannelAccepted,
    IReadOnlyList<string> FailedStages);
#endif

internal sealed record AIrhythmAdvancedSnapshot(
    IReadOnlyList<TvAirRecordingSessionDto> ActiveRecordings);

internal sealed record AIrhythmRuntimeSnapshot(
    bool Ready,
    string Error,
    IReadOnlyList<TvAirProgramEventDto> Events,
    IReadOnlyList<TvAirReservationDto> Reservations,
    IReadOnlyList<TvAirReservationDto> ReservationRecords,
    IReadOnlyList<TvAirKeywordRuleDto> KeywordRules,
    IReadOnlyList<TvAirRecordingHistoryDto> History,
    IReadOnlyList<TvAirRecordingHistoryDto> RecoveryHistory,
    IReadOnlyList<TvAirServiceDto> Channels,
    IReadOnlyList<TvAirTunerStatusDto> Tuners,
    AIrhythmAdvancedSnapshot Advanced,
    AIrhythmSettings Settings,
    long? SettingsRevision
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
    , AIrhythmRuntimeDiagnostics Diagnostics
#endif
    );
internal sealed record AIrhythmSaveResult(bool Success, string Message, bool Changed = true);
internal sealed record AIrhythmBackupEnvelope(
    int FormatVersion,
    string Product,
    string ProductVersion,
    DateTimeOffset CreatedAt,
    string PayloadJson,
    string PayloadSha256);
internal sealed record AIrhythmBackupPayload(Dictionary<string, Dictionary<string, string>> Storage);
internal sealed record AIrhythmDataResetMarker(DateTimeOffset ResetAt, int SchemaVersion = 1);
internal readonly record struct AIrhythmUsageTotals(long RecordingTotal, long ReservationTotal);
// 録画累計は件数と計上済みの放送枠識別子を同じ保存単位で保持する。
// Version 1 の保存値は後方互換で読み込み、過去の欠落を推定加算しない。
internal sealed record AIrhythmUsageCounterState(
    long RecordingTotal,
    long ReservationTotal,
    int Version = 1,
    DateTimeOffset? RecordingAccountingStart = null,
    string[]? CountedRecordingKeys = null);
internal readonly record struct AIrhythmServiceIdentity(int NetworkId, int TransportStreamId, int ServiceId)
{
    public bool IsValid => NetworkId > 0 && TransportStreamId > 0 && ServiceId > 0;
    public override string ToString() => $"{NetworkId}:{TransportStreamId}:{ServiceId}";
}
internal sealed record AIrhythmInterestSignal(
    string EventId,
    string SeriesKey,
    string Genre,
    string ServiceName,
    DateTimeOffset SelectedAt,
    int NetworkId = 0,
    int TransportStreamId = 0,
    int ServiceId = 0,
    string[]? ContributorKeys = null);

// 保存・読込 — Persisted, behavior-derived user model. The model never stores a hand-authored title rule. It
// accumulates one observation per explicit user choice and learns affinity distributions across Work /
// Genre / Terms / Service / Hour plus role-aware Contributor evidence. More observations increase model
// maturity; merely rendering a recommendation never teaches the model.
internal sealed record AIrhythmContributorObservation(string Key, double Weight);

internal sealed record AIrhythmUserModelObservation(
    string ObservationId,
    string WorkKey,
    string GenreKey,
    string ServiceKey,
    int? Hour,
    IReadOnlyList<string> Terms,
    IReadOnlyList<AIrhythmContributorObservation>? Contributors = null,
    bool LearnWork = true,
    bool LearnGenre = true,
    bool LearnTerms = true,
    bool LearnService = true,
    bool LearnHour = true,
    double WorkEvidenceWeight = 1.0d,
    double GenreEvidenceWeight = 1.0d,
    double TermsEvidenceWeight = 1.0d,
    double ServiceEvidenceWeight = 1.0d,
    double HourEvidenceWeight = 1.0d)
{
    internal bool HasBaseLearningAxis => LearnWork || LearnGenre || LearnTerms || LearnService || LearnHour;
}

internal readonly record struct AIrhythmBehaviorEvidenceAxes(
    bool Work,
    bool Genre,
    bool Terms,
    bool Service,
    bool Hour,
    bool Contributor)
{
    internal static readonly AIrhythmBehaviorEvidenceAxes None = new(false, false, false, false, false, false);
    internal static readonly AIrhythmBehaviorEvidenceAxes DirectSelection = new(true, true, true, true, true, true);
    internal bool HasBaseAxis => Work || Genre || Terms || Service || Hour;
}

// 処理条件 — Audit-only provenance for new weighted observations. Historical aggregate weights are
// deliberately not reverse-engineered from present-day keyword rules.
internal sealed record AIrhythmBaseEvidenceLedgerEntry(
    double Work, double Genre, double Terms, double Service, double Hour,
    string WorkKey, string GenreKey, string ServiceKey,
    string[]? TermKeys = null, string HourKey = "");

internal sealed record AIrhythmLearnedUserModelState(
    int SchemaVersion,
    long ObservationCount,
    DateTimeOffset UpdatedAt,
    string[] SeenObservationIds,
    Dictionary<string, double> WorkWeights,
    Dictionary<string, double> GenreWeights,
    Dictionary<string, double> TermWeights,
    Dictionary<string, double> ServiceWeights,
    Dictionary<string, double> HourWeights,
    Dictionary<string, double>? ContributorWeights = null,
    string[]? SeenContributorObservationIds = null,
    int ContributorLogicVersion = 0,
    int BasePreferenceLogicVersion = 0,
    Dictionary<string, string>? ContributorFactWorkKeys = null,
    Dictionary<string, AIrhythmBaseEvidenceLedgerEntry>? BaseEvidenceLedger = null)
{
    // Host連携 — Runtime-only baseline for same-candidate Developer comparison. Never serialized.
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, double>? PreCalibrationContributorWeights { get; init; }
}

// 保存・読込 — The recommendation engine keeps the factual local evidence axes separate until the final
// internal-strength composition. This is intentionally a value type so the hot candidate loop does not
// allocate one object per EPG event. The fields are the current scoring coordinates, not persisted user
// preference values; later logic can calibrate their contribution without losing which axis produced the
// signal.
internal readonly record struct AIrhythmEvidenceVector(
    double Genre,
    double Work,
    double Terms,
    double Service,
    double Hour,
    double Interest,
    double Preferred)
{
    // 推薦・学習 — Semantic families keep correlated evidence together without changing the established score.
    internal double IdentityStrength => Work;
    internal double ContentStrength => Genre + Terms;
    internal double ContextStrength => Service + Hour;
    internal double ExplicitBehaviorStrength => Interest + Preferred;
    internal double InternalStrength => IdentityStrength + ContentStrength + ContextStrength + ExplicitBehaviorStrength;
}

[Flags]
internal enum AIrhythmEvidenceProvenance
{
    None = 0,
    WorkHistory = 1 << 0,
    WorkReservation = 1 << 1,
    ContributorSearchCast = 1 << 2,
    ContributorMetadata = 1 << 3,
    GenreHistory = 1 << 4,
    TermHistory = 1 << 5,
    ServiceHistory = 1 << 6,
    HourHistory = 1 << 7,
    InterestSelection = 1 << 8,
    PreferredSetting = 1 << 9
}

// 作品識別 — Shadow-only normalized coordinates for future self-calibration. Strength and confidence are
// deliberately separate: strength says how strongly the candidate aligns, while confidence says how much
// behavioral support backs that coordinate. Provenance records which evidence families contributed. None
// of these values mutate production scoring.
internal readonly record struct AIrhythmNormalizedEvidenceGroups(
    double IdentityStrength,
    double IdentityConfidence,
    AIrhythmEvidenceProvenance IdentityProvenance,
    double ContentStrength,
    double ContentConfidence,
    AIrhythmEvidenceProvenance ContentProvenance,
    double ContextStrength,
    double ContextConfidence,
    AIrhythmEvidenceProvenance ContextProvenance,
    double ExplicitStrength,
    double ExplicitConfidence,
    AIrhythmEvidenceProvenance ExplicitProvenance);

internal readonly record struct AIrhythmEvidenceCalibrationCandidate(
    AIrhythmNormalizedEvidenceGroups Groups,
    double BackgroundIdentityStrength,
    double BackgroundContentStrength,
    double BackgroundContextStrength,
    double BackgroundExplicitStrength,
    int CandidateCount);

internal sealed record AIrhythmEvidenceCalibrationState(
    int SchemaVersion,
    long SampleCount,
    DateTimeOffset UpdatedAt,
    string[] SeenObservationIds,
    double IdentitySelectedStrengthTotal,
    double ContentSelectedStrengthTotal,
    double ContextSelectedStrengthTotal,
    double ExplicitSelectedStrengthTotal,
    double IdentityConfidenceTotal,
    double ContentConfidenceTotal,
    double ContextConfidenceTotal,
    double ExplicitConfidenceTotal,
    double IdentityBackgroundStrengthTotal,
    double ContentBackgroundStrengthTotal,
    double ContextBackgroundStrengthTotal,
    double ExplicitBackgroundStrengthTotal);

internal readonly record struct AIrhythmLearnedAffinityContext(
    AIrhythmLearnedUserModelState Model,
    double MaxWorkWeight,
    double MaxGenreWeight,
    double MaxTermWeight,
    double MaxServiceWeight,
    double MaxHourWeight,
    double MaxContributorWeight,
    IReadOnlyDictionary<AIrhythmServiceIdentity, double> ServiceWeightsByIdentity,
    IReadOnlyDictionary<int, double> HourWeightsByHour);

internal readonly record struct AIrhythmLearnedAffinityBreakdown(
    double Work,
    double Contributor,
    double Genre,
    bool HasGenre,
    double Terms,
    bool HasTerms,
    double Service,
    double Hour)
{
    internal double Identity => Math.Max(Work, Contributor);

    // 作品識別 — Scoring uses the mean of available identity, service, hour, genre, and term axes.
    internal double Overall
    {
        get
        {
            var sum = Service + Hour + Identity;
            var count = 3;
            if (HasGenre) { sum += Genre; count++; }
            if (HasTerms) { sum += Terms; count++; }
            return Math.Clamp(sum / count, 0.0d, 1.0d);
        }
    }
}
internal readonly record struct AIrhythmCalibrationEventKey(int NetworkId, int TransportStreamId, int ServiceId, int EventNumber, long StartUtcTicks);
internal sealed record AIrhythmEventIdentity(int NetworkId, int TransportStreamId, int ServiceId, int EventNumber, DateTimeOffset Start);
internal sealed record AIrhythmRecommendation(
    string Title,
    string ServiceName,
    string BroadcastType,
    string Genre,
    DateTimeOffset Start,
    int Score,
    IReadOnlyList<string> Reasons,
    string SeriesKey,
    AIrhythmEventIdentity? EventIdentity,
    bool IsConvincing = false,
    bool IsPlausibleDiscovery = false,
    int RawScore = 0,
    double DeviationRawScore = 0.0,
    AIrhythmExternalEvidenceSummaryStatus ExternalEvidenceSummary = AIrhythmExternalEvidenceSummaryStatus.NoEvidence,
    AIrhythmExternalEvidenceEvaluationFlags ExternalEvidenceEvaluationFlags = AIrhythmExternalEvidenceEvaluationFlags.None,
    AIrhythmExternalEvidenceConfidenceGate ExternalEvidenceConfidenceGate = AIrhythmExternalEvidenceConfidenceGate.NotApplicable,
    AIrhythmExternalEvidenceAdjustmentCandidate ExternalEvidenceAdjustmentCandidate = AIrhythmExternalEvidenceAdjustmentCandidate.None,
    AIrhythmExternalEvidenceAdjustmentKind ExternalEvidenceAdjustmentKind = AIrhythmExternalEvidenceAdjustmentKind.None,
    AIrhythmExternalEvidenceAdjustmentStrength ExternalEvidenceAdjustmentStrength = AIrhythmExternalEvidenceAdjustmentStrength.None,
    AIrhythmExternalEvidenceVerdictReason ExternalEvidenceSupportingVerdictReason = AIrhythmExternalEvidenceVerdictReason.NotApplicable,
    double ExternalEvidenceShadowAdjustmentValue = 0.0,
    double ExternalEvidenceAdjustmentValue = 0.0,
    double LearnedUserAffinity = 0.0,
    double LearnedUserModelAdjustment = 0.0,
    double LearnedUserModelMaturity = 0.0);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
internal readonly record struct AIrhythmRawCoordinateShadowMetrics(
    double WeakValue,
    double ModerateValue,
    int NonZeroCandidates,
    int DisplayScoreChangedCandidates,
    int RankChangedCandidates,
    int Top10Entrants,
    int Top10Exits,
    int Top20Entrants,
    int Top20Exits,
    int Top50Entrants,
    int Top50Exits,
    int MaxRankDelta,
    int MaxDisplayScoreDelta);
internal readonly record struct AIrhythmRawCoordinateShadowDetail(
    string Title,
    double WeakValue,
    double ModerateValue,
    int CurrentRank,
    int ShadowRank,
    double CurrentDeviationRawScore,
    double ShadowDeviationRawScore,
    int CurrentDisplayScore,
    int ShadowDisplayScore,
    AIrhythmExternalEvidenceAdjustmentKind Kind,
    AIrhythmExternalEvidenceAdjustmentStrength Strength,
    AIrhythmExternalEvidenceVerdictReason VerdictReason);
#endif
internal sealed record AIrhythmServerRender(string CardsHtml, string ChartsHtml, string DiscoveryHtml, string SearchHtml, string MessageHtml, string StatusText);

