namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmDataState
{
    // 作品識別 — Reversible scoring-only quarantine for unverified historical contributor identities. Never rewrite
    // the stored contributor map or observation IDs: the original evidence remains recoverable if a later,
    // stronger source evidence can establish the identity.
    private static bool IsStructurallyAmbiguousPersistedContributor(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("tracked|", StringComparison.OrdinalIgnoreCase))
            return false;
        var separator = key.IndexOf('|');
        if (separator <= 0 || separator >= key.Length - 1) return false;
        var entity = key[(separator + 1)..];
        // The new parser and saved-score projection share one entity gate. Historical facts and
        // observation IDs remain intact; only the scoring copy omits unverified identities.
        var role = key[..separator];
        return AIrhythmText.IsUnsupportedContributorIdentity(entity, role);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly object Gate = new();
    private static readonly object CaptureGate = new();
    private static ITvAirPluginRuntimeContext? _runtimeContext;
    private static readonly List<IDisposable> EventSubscriptions = new();
    private static AIrhythmRuntimeSnapshot? _cachedSnapshot;
    // 保存・読込 — Retain the last fully successful runtime snapshot across ordinary invalidations. It is used
    // only as a short-lived display fallback when Host snapshot capture races with an authoritative data
    // update; it is never cached as the new generation.
    private static AIrhythmRuntimeSnapshot? _lastSuccessfulSnapshot;
    private static TvAirExternalLookupCapabilityDto? _externalLookupCapability;
    private static readonly Dictionary<string, AIrhythmExternalEvidence> ExternalEvidenceCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AIrhythmExternalEvidenceSummaryProjection> ExternalEvidenceSummaryByEvent = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (string CanonicalTitle, DateTimeOffset UpdatedAt)> ExternalCanonicalTitleBySource = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> ExternalLookupAttempts = new(StringComparer.OrdinalIgnoreCase);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
    private static readonly List<AIrhythmExternalLookupTrace> ExternalLookupTraces = new();
    private const int ExternalLookupTraceLimit = 12;
#endif
    private static long _cacheGeneration;
    private static long _externalEvidenceGeneration;
    private const int ExternalEvidenceCacheLimit = 256;
    private static readonly TimeSpan ExternalEvidenceTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan ExternalLookupAttemptTtl = TimeSpan.FromMinutes(30);
    private const int ExternalLookupProbeLimit = 32;
    private static IReadOnlyList<TvAirReservationDto> _verifiedReservationHistory = Array.Empty<TvAirReservationDto>();
    private static bool _verifiedReservationHistoryLoaded;
    private static readonly HashSet<string> UsageCountedReservationKeys = new(StringComparer.Ordinal);
    private static AIrhythmUsageTotals _usageTotals;
    private static bool _usageTotalsInitialized;
    private static bool _usageTotalsDirty;
    private static readonly object UsageTotalsPersistenceGate = new();
    // 録画累計はHost確定済み履歴で計上する。加算済み識別子と累計は usage/totals に原子的に保存する。
    // 通知のファイル識別子をHost履歴の録画IDと同一視しない。表示更新・再起動時も履歴から補完する。
    private static readonly HashSet<string> UsageCountedRecordingKeys = new(StringComparer.Ordinal);
    private static DateTimeOffset _usageRecordingAccountingStart;
    private static long _usageRecordingLedgerRevision;
    private const string RecordingFactStorageNamespace = "recordingFacts";
    private const string RecoveryAttemptStorageNamespace = "recoveryAttempts";
    private const string RecoveryLookupStorageNamespace = "recoveryLookup";
    private const string RecoveryLookupStorageKey = "manifest-v1";
    private static bool _recoveryLookupChecked;
    private static bool _recoveryLazyIndexed;
    private static int _recoveryIndexedTotal;
    // 保存・読込 — A verified snapshot of the expendable lookup; never authoritative for persistence.
    private static AIrhythmRecoveryLookupEntry[] _verifiedRecoveryLookup = Array.Empty<AIrhythmRecoveryLookupEntry>();
    private static readonly Dictionary<string, AIrhythmRecoveryAttempt> RecoveryAttempts = new(StringComparer.OrdinalIgnoreCase);
    // 保存・読込 — Non-authoritative month buckets: accelerate changed-month persistence without rescanning all
    // historical attempts. The original identity dictionary and on-disk JSON are unchanged.
    private static readonly Dictionary<string, Dictionary<string, AIrhythmRecoveryAttempt>> RecoveryAttemptsByMonth = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> RecoveryMonthSourceRevisions = new(StringComparer.Ordinal);
    // Host連携 — Verified against a materialized legacy month once per runtime session.
    private static readonly HashSet<string> VerifiedLegacyRecoveryAliases = new(StringComparer.OrdinalIgnoreCase);
    private static bool _recoveryAttemptsLoaded;
    private static readonly HashSet<string> _recoveryDirtyMonths = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, AIrhythmRecordingFact> RecordingFacts = new(StringComparer.OrdinalIgnoreCase);
    private static bool _recordingFactsLoaded;
    private static readonly object RecordingFactsPersistenceGate = new();
    private static readonly HashSet<string> RecordingFactsDirtyMonths = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> RecordingFactChunkRevisions = new(StringComparer.Ordinal);
    private static long _recordingFactsGeneration;
    private static DateTimeOffset? _dataResetCutoff;
    private static bool _dataResetCutoffLoaded;
    private const string DataLifecycleStorageNamespace = "dataLifecycle";
    private const string DataResetCutoffStorageKey = "resetCutoff";
    internal const string ExternalLookupRuntimeStorageNamespace = "externalLookupRuntime";
    private const string ExternalManualRefreshDateStorageKey = "lastManualRefreshDate";
    private const string ExternalProviderOutcomeStorageKey = "providerOutcome-v1";
    private const int ExternalProviderOutcomeSchemaVersion = 1;
    private const int ExternalProviderOutcomeLimit = 2048;
    private static DateOnly? _lastManualExternalRefreshDate;
    private static bool _lastManualExternalRefreshDateLoaded;
    private const int BackupFormatVersion = 1;
    private const string LearnedUserModelStorageNamespace = "rhythmSearch";
    private const string LearnedUserModelStorageKey = "userModel-v1";
    // 保存・読込 — Reuse JSON deserialization ONLY after a fresh authoritative Storage.Get returns identical
    // revision AND payload. Retain one bounded immutable read generation; each observation pass still copies
    // all mutable maps before applying new facts.
    private const int LearnedUserModelReadCacheMaxCharacters = 8 * 1024 * 1024;
    private static readonly object LearnedUserModelReadCacheGate = new();
    private static string? _learnedUserModelReadJson;
    private static long? _learnedUserModelReadRevision;
    private static AIrhythmLearnedUserModelState? _learnedUserModelReadState;

    private static void ClearLearnedUserModelReadCache()
    {
        lock (LearnedUserModelReadCacheGate)
        {
            _learnedUserModelReadJson = null;
            _learnedUserModelReadRevision = null;
            _learnedUserModelReadState = null;
        }
    }

    private static AIrhythmLearnedUserModelState? DeserializeLearnedUserModel(string json, long revision)
    {
        if (json.Length <= LearnedUserModelReadCacheMaxCharacters)
        {
            lock (LearnedUserModelReadCacheGate)
            {
                if (_learnedUserModelReadState is not null
                    && _learnedUserModelReadRevision == revision
                    && string.Equals(_learnedUserModelReadJson, json, StringComparison.Ordinal))
                    return _learnedUserModelReadState;
            }
        }
        var loaded = JsonSerializer.Deserialize<AIrhythmLearnedUserModelState>(json, JsonOptions);
        // 保存・読込 — Unknown schemas and invalid data must not enter the reusable valid-state cache.
        if (loaded is not null && loaded.SchemaVersion == LearnedUserModelSchemaVersion
            && json.Length <= LearnedUserModelReadCacheMaxCharacters)
        {
            lock (LearnedUserModelReadCacheGate)
            {
                _learnedUserModelReadJson = json;
                _learnedUserModelReadRevision = revision;
                _learnedUserModelReadState = loaded;
            }
        }
        return loaded;
    }
    private const int LearnedUserModelSchemaVersion = 1;
    private const int ContributorLogicVersion = 7;
    private const int BasePreferenceLogicVersion = 5;
    private const string EvidenceCalibrationStorageKey = "evidenceCalibration-v1";
    private const int EvidenceCalibrationSchemaVersion = 2;
    private const int EvidenceCalibrationSeenLimit = 5000;
    private const string NormalizationLearnerStorageNamespace = "normalizationLearnerShadow";
    private const string NormalizationLearnerStorageKey = "memory-v1";
    private const int NormalizationLearnerMemorySchemaVersion = 1;
    private const int NormalizationLearnerMemoryLimit = 30000;
    private const string NormalizationLearnerHypothesisStorageKey = "hypotheses-v1";
    private const int NormalizationLearnerHypothesisSchemaVersion = 1;
    private const int NormalizationLearnerHypothesisLimit = 12000;
    private const string NormalizationLearnerLongTermStorageKey = "longterm-features-v1";
    private const int NormalizationLearnerLongTermSchemaVersion = 1;
    private const int NormalizationLearnerLongTermLimit = 12000;
    private const string NormalizationLearnerExternalStructuralStorageKey = "external-structural-v1";
    private const int NormalizationLearnerExternalStructuralSchemaVersion = 1;
    private const int NormalizationLearnerExternalStructuralLimit = 4096;

    private sealed record NormalizationLearnerMemoryState(
        int SchemaVersion,
        string LogicVersion,
        DateTimeOffset UpdatedAt,
        NormalizationLearnerMemoryObservation[] Observations);

    private sealed record NormalizationLearnerMemoryObservation(
        string ComparableTitle,
        string FirstObservedDate,
        string LastObservedDate,
        int DistinctObservationDays);

    private sealed record NormalizationLearnerLongTermState(
        int SchemaVersion,
        string LogicVersion,
        DateTimeOffset UpdatedAt,
        NormalizationLearnerLongTermObservation[] Observations);

    private sealed record NormalizationLearnerLongTermObservation(
        string HypothesisKey,
        string Kind,
        string LearnedWork,
        string FirstObservedDate,
        string LastObservedDate,
        int DistinctObservationDays,
        double MeanConfidence,
        double M2Confidence,
        double MeanRoleFitness,
        double M2RoleFitness,
        double MeanPopulationQuality,
        double M2PopulationQuality,
        double MeanCollisionRate,
        double MeanBoundaryIntrusion,
        double MeanExternalStructuralSupport,
        double MeanExternalProviderReliability,
        int LastExternalProviderCount,
        int LastExternalEvidenceCount,
        int RelationChangeDays,
        int QualityIssueChangeDays,
        string LastRelation,
        string LastQualityIssue);

    private sealed record NormalizationLearnerExternalStructuralState(
        int SchemaVersion,
        string LogicVersion,
        DateTimeOffset UpdatedAt,
        NormalizationLearnerExternalStructuralObservation[] Observations);

    private sealed record NormalizationLearnerExternalStructuralObservation(
        string EvidenceKey,
        string ProviderId,
        string EntityId,
        string CanonicalTitle,
        string[] Aliases,
        string MediaType,
        int? Season,
        int? Episode,
        double Confidence,
        string Verdict,
        string VerdictReason,
        DateTimeOffset FirstAcquiredAt,
        DateTimeOffset LastAcquiredAt);

    private sealed record AIrhythmExternalProviderOutcomeState(
        int SchemaVersion,
        DateTimeOffset UpdatedAt,
        AIrhythmExternalProviderOutcomeObservation[] Observations);

    private sealed record AIrhythmExternalProviderOutcomeObservation(
        string Query,
        int Attempts,
        int EvidenceCount,
        int SupportedCount,
        int UnresolvedCount,
        int ConflictingCount,
        int NoEvidenceCount,
        DateTimeOffset LastAttemptAt);

    private sealed record NormalizationLearnerHypothesisState(
        int SchemaVersion,
        string LogicVersion,
        DateTimeOffset UpdatedAt,
        NormalizationLearnerHypothesisObservation[] Observations);

    private sealed record NormalizationLearnerHypothesisObservation(
        string HypothesisKey,
        string Kind,
        string LearnedWork,
        string FirstObservedDate,
        string LastObservedDate,
        int DistinctObservationDays,
        int ValidatedDays,
        int DeferredDays,
        int RejectedDays,
        double ConfidenceEwma,
        double RoleFitnessEwma,
        string LastDecision,
        string LastRelation,
        int MaxDistinctCanonicalWorks);

    private const string NormalizationPromotionStorageNamespace = "normalizationPromotion";
    private const string NormalizationPromotionStorageKey = "state-v1";
    private const int NormalizationPromotionSchemaVersion = 1;
    private const string NormalizationPromotionLogicVersion = "normalization_promotion_v1_strict_consensus";

    public static void Initialize(ITvAirPluginRuntimeContext context)
    {
        lock (Gate)
        {
            foreach (var subscription in EventSubscriptions)
            {
                try { subscription.Dispose(); } catch { }
            }
            EventSubscriptions.Clear();
            _runtimeContext = context;
            ClearLearnedUserModelReadCache();
            AIrhythmRecommendationEngine.ClearContributorMetadataCache();
            AIrhythmRecommendationEngine.ClearNormalizationStructuralCandidates();
            _cachedSnapshot = null;
            _lastSuccessfulSnapshot = null;
            _externalLookupCapability = null;
            _usageTotals = default;
            _usageTotalsInitialized = false;
            _usageTotalsDirty = false;
            UsageCountedRecordingKeys.Clear();
            UsageCountedReservationKeys.Clear();
            _verifiedReservationHistory = Array.Empty<TvAirReservationDto>();
            _verifiedReservationHistoryLoaded = false;
            _usageRecordingAccountingStart = default;
            _usageRecordingLedgerRevision = 0;
            RecordingFacts.Clear();
            RecordingFactsDirtyMonths.Clear();
            RecordingFactChunkRevisions.Clear();
            _recordingFactsGeneration = 0;
            RecoveryAttempts.Clear();
            RecoveryAttemptsByMonth.Clear();
            RecoveryMonthSourceRevisions.Clear();
            VerifiedLegacyRecoveryAliases.Clear();
            _recoveryLazyIndexed = false;
            _recoveryIndexedTotal = 0;
            _recoveryAttemptsLoaded = false;
            _recoveryLookupChecked = false;
            _verifiedRecoveryLookup = Array.Empty<AIrhythmRecoveryLookupEntry>();
            _recoveryDirtyMonths.Clear();
            _recordingFactsLoaded = false;
            _dataResetCutoff = null;
            _dataResetCutoffLoaded = false;
            _lastManualExternalRefreshDate = null;
            _lastManualExternalRefreshDateLoaded = false;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            ExternalLookupTraces.Clear();
#endif
            _cacheGeneration++;
            _externalEvidenceGeneration++;

            var available = new HashSet<string>(context.Events.ListEventTypes(), StringComparer.OrdinalIgnoreCase);
            foreach (var eventType in RefreshEventTypes)
            {
                if (!available.Contains(eventType))
                    continue;
                EventSubscriptions.Add(context.Events.Subscribe(eventType, envelope => HandleRuntimeEvent(eventType, envelope)));
            }
        }
    }

    public static void Start()
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate)
        {
            context = _runtimeContext;
            if (context is null) return;
            if (EventSubscriptions.Count == 0)
                Initialize(context);
        }
        EnsureUsageTotalsInitialized(context);
        RefreshExternalLookupCapability();
    }

    public static void Stop()
    {
        lock (Gate)
        {
            foreach (var subscription in EventSubscriptions)
            {
                try { subscription.Dispose(); } catch { }
            }
            EventSubscriptions.Clear();
            _runtimeContext = null;
            ClearLearnedUserModelReadCache();
            AIrhythmRecommendationEngine.ClearContributorMetadataCache();
            AIrhythmRecommendationEngine.ClearNormalizationStructuralCandidates();
            _cachedSnapshot = null;
            _lastSuccessfulSnapshot = null;
            _externalLookupCapability = null;
            ExternalEvidenceCache.Clear();
            ExternalEvidenceSummaryByEvent.Clear();
            ExternalCanonicalTitleBySource.Clear();
            ExternalLookupAttempts.Clear();
            _usageTotals = default;
            _usageTotalsInitialized = false;
            _usageTotalsDirty = false;
            UsageCountedRecordingKeys.Clear();
            UsageCountedReservationKeys.Clear();
            _verifiedReservationHistory = Array.Empty<TvAirReservationDto>();
            _verifiedReservationHistoryLoaded = false;
            _usageRecordingAccountingStart = default;
            _usageRecordingLedgerRevision = 0;
            RecordingFacts.Clear();
            RecordingFactsDirtyMonths.Clear();
            RecordingFactChunkRevisions.Clear();
            _recordingFactsGeneration = 0;
            RecoveryAttempts.Clear();
            RecoveryAttemptsByMonth.Clear();
            RecoveryMonthSourceRevisions.Clear();
            VerifiedLegacyRecoveryAliases.Clear();
            _recoveryLazyIndexed = false;
            _recoveryIndexedTotal = 0;
            _recoveryAttemptsLoaded = false;
            _recoveryLookupChecked = false;
            _verifiedRecoveryLookup = Array.Empty<AIrhythmRecoveryLookupEntry>();
            _recoveryDirtyMonths.Clear();
            _recordingFactsLoaded = false;
            _dataResetCutoff = null;
            _dataResetCutoffLoaded = false;
            _lastManualExternalRefreshDate = null;
            _lastManualExternalRefreshDateLoaded = false;
            _cacheGeneration++;
            _externalEvidenceGeneration++;
        }
    }

    public static AIrhythmUsageTotals GetUsageTotals()
    {
        ITvAirPluginRuntimeContext? context;
        bool needsInitialization;
        lock (Gate)
        {
            context = _runtimeContext;
            needsInitialization = !_usageTotalsInitialized;
        }
        if (needsInitialization && context is not null)
            EnsureUsageTotalsInitialized(context);
        if (context is not null)
        {
            bool retry;
            lock (Gate) retry = _usageTotalsDirty;
            if (retry) PersistUsageCounterState(context);
        }
        lock (Gate) return _usageTotals;
    }

    private static void EnsureUsageTotalsInitialized(ITvAirPluginRuntimeContext context)
    {
        lock (Gate)
            if (_usageTotalsInitialized || !ReferenceEquals(_runtimeContext, context)) return;

        if (!TryReadUsageCounterState(context, out var stored)) return;
        var cutoff = GetDataResetCutoff(context);
        var isCurrent = stored?.Version == 3;
        var existingCounted = new HashSet<string>(StringComparer.Ordinal);
        var existingReservations = new HashSet<string>(StringComparer.Ordinal);
        var recordingTotal = isCurrent ? stored!.RecordingTotal : 0L;
        var reservationTotal = isCurrent ? stored!.ReservationTotal : 0L;
        var accountingStart = isCurrent ? stored!.RecordingAccountingStart!.Value
            : cutoff ?? DateTimeOffset.MinValue;

        if (isCurrent)
        {
            existingCounted.UnionWith(stored!.CountedRecordingKeys ?? Array.Empty<string>());
            existingReservations.UnionWith(stored.CountedReservationKeys ?? Array.Empty<string>());
        }
        else
        {
            // Historical totals v1/v2 contain program-time-slot bookings without attribution.
            // They must not be carried into v3. Rebuild only from verified origin and original
            // recording occurrence. Unknown legacy rows stay preserved but contribute zero.
            IReadOnlyList<TvAirReservationDto> active;
            IReadOnlyList<TvAirReservationDto> allReservations;
            IReadOnlyList<TvAirRecordingHistoryDto> hostHistory;
            try
            {
                active = context.Reservations.List(new TvAirReservationQueryDto { IncludeSystemEntries = false });
                allReservations = ReadReservationOrigins(context, active);
                lock (Gate) if (!_verifiedReservationHistoryLoaded) return;
                hostHistory = context.Recordings.ListHistory(new TvAirRecordingHistoryQueryDto
                {
                    IncludeSystemEntries = false, Limit = 10000
                });
            }
            catch { return; }
            if (!EnsureRecordingFactsLoaded(context)) return;
            MergeFinalizedRecordingFacts(context, hostHistory, allReservations);
            ReconcileRecordingOrigins(context, allReservations);
            lock (Gate)
            {
                foreach (var fact in RecordingFacts.Values.Where(IsAdmittedRecordingFact))
                    existingCounted.Add(UsageRecordingLedgerKey(fact.Identity));
            }
            foreach (var reservation in allReservations)
            {
                if (!AIrhythmPreferenceOriginPolicy.AllowsReservation(reservation)
                    || cutoff is not null && reservation.CreatedAt < cutoff.Value)
                    continue;
                existingReservations.Add(UsageReservationLedgerKey(reservation));
            }
            recordingTotal = existingCounted.Count;
            reservationTotal = existingReservations.Count;
        }

        lock (Gate)
        {
            if (!ReferenceEquals(_runtimeContext, context) || _usageTotalsInitialized) return;
            _usageTotals = new AIrhythmUsageTotals(recordingTotal, reservationTotal);
            _usageRecordingAccountingStart = accountingStart;
            UsageCountedRecordingKeys.Clear();
            UsageCountedRecordingKeys.UnionWith(existingCounted);
            UsageCountedReservationKeys.Clear();
            UsageCountedReservationKeys.UnionWith(existingReservations);
            _usageRecordingLedgerRevision = 0;
            _usageTotalsInitialized = true;
        }
        PersistUsageCounterState(context);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"usage totals initialized recordingTotal={recordingTotal} reservationTotal={reservationTotal} policy=verified_non_program_origins_only version=3 previousVersion={stored?.Version ?? 0} unverifiedLegacyExcluded=True");
#endif
    }

}
