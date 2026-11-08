namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmDataState
{
    internal static Dictionary<string, int> ObserveNormalizationLearnerTitles(IEnumerable<string> currentComparableTitles)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var observations = new Dictionary<string, NormalizationLearnerMemoryObservation>(StringComparer.Ordinal);
        var loaded = 0;
        var schemaMismatch = false;

        if (context is not null)
        {
            try
            {
                var stored = context.Storage.Get(NormalizationLearnerStorageNamespace, NormalizationLearnerStorageKey);
                var json = stored.Succeeded ? stored.Value?.Value?.ToString() : null;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var state = JsonSerializer.Deserialize<NormalizationLearnerMemoryState>(json, JsonOptions);
                    if (state is not null && state.SchemaVersion == NormalizationLearnerMemorySchemaVersion)
                    {
                        foreach (var item in state.Observations ?? Array.Empty<NormalizationLearnerMemoryObservation>())
                        {
                            if (string.IsNullOrWhiteSpace(item.ComparableTitle) || item.DistinctObservationDays <= 0)
                                continue;
                            observations[item.ComparableTitle] = item;
                        }
                        loaded = observations.Count;
                    }
                    else if (state is not null)
                    {
                        schemaMismatch = true;
                    }
                }
            }
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
            #else
            catch (Exception)
            #endif
            {
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"NORMALIZATION_LEARNER_MEMORY result=LOAD_ERROR type={ex.GetType().Name} action=start_fresh_without_production_effect");
                #endif
            }
        }

        // 処理条件 — The cap can drop every tentative new item. Retain the prior canonical content so a no-op
        // truncation is not mistaken for a new learning day.
        var originalAtCapacity = observations.Count >= NormalizationLearnerMemoryLimit
            ? new Dictionary<string, NormalizationLearnerMemoryObservation>(observations, StringComparer.Ordinal)
            : null;
        var changed = false;
        foreach (var title in currentComparableTitles.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            if (observations.TryGetValue(title, out var existing))
            {
                if (!string.Equals(existing.LastObservedDate, todayText, StringComparison.Ordinal))
                {
                    observations[title] = existing with
                    {
                        LastObservedDate = todayText,
                        DistinctObservationDays = Math.Max(1, existing.DistinctObservationDays) + 1
                    };
                    changed = true;
                }
            }
            else
            {
                observations[title] = new NormalizationLearnerMemoryObservation(title, todayText, todayText, 1);
                changed = true;
            }
        }

        if (observations.Count > NormalizationLearnerMemoryLimit)
        {
            observations = observations.Values
                .OrderByDescending(x => x.DistinctObservationDays)
                .ThenByDescending(x => x.LastObservedDate, StringComparer.Ordinal)
                .ThenBy(x => x.ComparableTitle, StringComparer.Ordinal)
                .Take(NormalizationLearnerMemoryLimit)
                .ToDictionary(x => x.ComparableTitle, StringComparer.Ordinal);
            changed = true;
        }

        if (changed && originalAtCapacity is not null
            && !NormalizationModelContentChanged(originalAtCapacity, observations))
            changed = false;

        if (context is not null && (changed || schemaMismatch))
        {
            try
            {
                var state = new NormalizationLearnerMemoryState(
                    NormalizationLearnerMemorySchemaVersion,
                    "normalization_learner_v4_role_validation_temporal_memory",
                    DateTimeOffset.Now,
                    observations.Values
                        .OrderBy(x => x.ComparableTitle, StringComparer.Ordinal)
                        .ToArray());
                var json = JsonSerializer.Serialize(state, JsonOptions);
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                var written = context.Storage.Set(NormalizationLearnerStorageNamespace, NormalizationLearnerStorageKey, json, expectedRevision: null);
                if (!written.Succeeded)
                    WriteDeveloperLog($"NORMALIZATION_LEARNER_MEMORY result=WRITE_FAILED loaded={loaded} current={observations.Count} schemaMismatch={schemaMismatch} productionMutation=False");
                #else
                _ = context.Storage.Set(NormalizationLearnerStorageNamespace, NormalizationLearnerStorageKey, json, expectedRevision: null);
                #endif
            }
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
            #else
            catch (Exception)
            #endif
            {
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"NORMALIZATION_LEARNER_MEMORY result=WRITE_ERROR type={ex.GetType().Name} loaded={loaded} current={observations.Count} productionMutation=False");
                #endif
            }
        }

        var maxDays = observations.Count == 0 ? 0 : observations.Values.Max(x => x.DistinctObservationDays);
        var multiDay = observations.Values.Count(x => x.DistinctObservationDays >= 2);
        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"NORMALIZATION_LEARNER_MEMORY result=OK loaded={loaded} stored={observations.Count} multiDayTitles={multiDay} maxObservationDays={maxDays} changed={changed} schema={NormalizationLearnerMemorySchemaVersion} logic=normalization_learner_v4_role_validation_temporal_memory storage=plugin_storage shadowOnly=False promotionConsumer=True productionMutation=strict_promotion_only scoreMutation=via_canonical_identity evidenceMutation=via_canonical_identity");
        #endif

        return observations.ToDictionary(x => x.Key, x => Math.Max(1, x.Value.DistinctObservationDays), StringComparer.Ordinal);
    }

    internal static void PersistNormalizationLearnerExternalStructuralEvidence(IEnumerable<AIrhythmExternalEvidence> incoming)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return;

        var items = incoming
            .Where(x => x is not null
                && string.Equals(x.ProviderId, AIrhythmExternalLookupAdapter.TvMazeProviderId, StringComparison.OrdinalIgnoreCase)
                && (!string.IsNullOrWhiteSpace(x.CanonicalTitle) || x.Aliases.Count > 0))
            .ToArray();
        if (items.Length == 0)
            return;

        var observations = new Dictionary<string, NormalizationLearnerExternalStructuralObservation>(StringComparer.OrdinalIgnoreCase);
        var loaded = 0;
        var changed = false;
        try
        {
            var stored = context.Storage.Get(NormalizationLearnerStorageNamespace, NormalizationLearnerExternalStructuralStorageKey);
            var json = stored.Succeeded ? stored.Value?.Value?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                var state = JsonSerializer.Deserialize<NormalizationLearnerExternalStructuralState>(json, JsonOptions);
                if (state is not null && state.SchemaVersion == NormalizationLearnerExternalStructuralSchemaVersion)
                {
                    foreach (var item in state.Observations ?? Array.Empty<NormalizationLearnerExternalStructuralObservation>())
                    {
                        if (!string.Equals(item.ProviderId, AIrhythmExternalLookupAdapter.TvMazeProviderId, StringComparison.OrdinalIgnoreCase))
                        {
                            changed = true;
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(item.EvidenceKey))
                            observations[item.EvidenceKey] = item;
                    }
                    loaded = observations.Count;
                }
            }
        }
        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
        #else
        catch (Exception)
        #endif
        {
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"NORMALIZATION_LEARNER_EXTERNAL_STRUCTURAL_MEMORY result=LOAD_ERROR type={ex.GetType().Name} action=preserve_runtime_only networkRequestsAdded=False");
            #endif
        }

        foreach (var item in items)
        {
            var canonical = (item.CanonicalTitle ?? string.Empty).Trim();
            var aliases = (item.Aliases ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(64)
                .ToArray();
            var canonicalKey = AIrhythmRecommendationEngine.ExternalPreEvaluationTitleKey(canonical);
            var entityKey = string.IsNullOrWhiteSpace(item.EntityId) ? canonicalKey : item.EntityId.Trim();
            if (canonicalKey.Length == 0 && aliases.Length == 0)
                continue;

            var evidenceKey = $"{item.ProviderId}|{entityKey}|{item.Season?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{item.Episode?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{canonicalKey}";
            if (observations.TryGetValue(evidenceKey, out var existing))
            {
                var mergedAliases = existing.Aliases
                    .Concat(aliases)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(64)
                    .ToArray();
                var updated = existing with
                {
                    CanonicalTitle = canonical.Length > 0 ? canonical : existing.CanonicalTitle,
                    Aliases = mergedAliases,
                    MediaType = string.IsNullOrWhiteSpace(item.MediaType) ? existing.MediaType : item.MediaType,
                    Confidence = Math.Max(existing.Confidence, item.Confidence),
                    Verdict = item.Verdict.ToString(),
                    VerdictReason = item.VerdictReason.ToString(),
                    LastAcquiredAt = item.AcquiredAt > existing.LastAcquiredAt ? item.AcquiredAt : existing.LastAcquiredAt
                };
                if (!Equals(updated, existing))
                {
                    observations[evidenceKey] = updated;
                    changed = true;
                }
            }
            else
            {
                observations[evidenceKey] = new NormalizationLearnerExternalStructuralObservation(
                    evidenceKey, item.ProviderId ?? string.Empty, item.EntityId ?? string.Empty, canonical, aliases,
                    item.MediaType ?? string.Empty, item.Season, item.Episode, Math.Clamp(item.Confidence, 0.0d, 1.0d),
                    item.Verdict.ToString(), item.VerdictReason.ToString(), item.AcquiredAt, item.AcquiredAt);
                changed = true;
            }
        }

        if (observations.Count > NormalizationLearnerExternalStructuralLimit)
        {
            observations = observations.Values
                .OrderByDescending(x => x.LastAcquiredAt)
                .ThenByDescending(x => x.Confidence)
                .Take(NormalizationLearnerExternalStructuralLimit)
                .ToDictionary(x => x.EvidenceKey, StringComparer.OrdinalIgnoreCase);
            changed = true;
        }

        if (changed)
        {
            try
            {
                var state = new NormalizationLearnerExternalStructuralState(
                    NormalizationLearnerExternalStructuralSchemaVersion,
                    "normalization_learner_v11_external_structural_memory",
                    DateTimeOffset.Now,
                    observations.Values.OrderBy(x => x.EvidenceKey, StringComparer.OrdinalIgnoreCase).ToArray());
                var json = JsonSerializer.Serialize(state, JsonOptions);
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                var written = context.Storage.Set(NormalizationLearnerStorageNamespace, NormalizationLearnerExternalStructuralStorageKey, json, expectedRevision: null);
                if (!written.Succeeded)
                    WriteDeveloperLog($"NORMALIZATION_LEARNER_EXTERNAL_STRUCTURAL_MEMORY result=WRITE_FAILED loaded={loaded} stored={observations.Count} networkRequestsAdded=False");
                #else
                _ = context.Storage.Set(NormalizationLearnerStorageNamespace, NormalizationLearnerExternalStructuralStorageKey, json, expectedRevision: null);
                #endif
            }
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
            #else
            catch (Exception)
            #endif
            {
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"NORMALIZATION_LEARNER_EXTERNAL_STRUCTURAL_MEMORY result=WRITE_ERROR type={ex.GetType().Name} loaded={loaded} stored={observations.Count} networkRequestsAdded=False");
                #endif
            }
        }

        var supported = observations.Values.Count(x => string.Equals(x.Verdict, AIrhythmExternalEvidenceVerdict.Supported.ToString(), StringComparison.Ordinal));
        var conflicting = observations.Values.Count(x => string.Equals(x.Verdict, AIrhythmExternalEvidenceVerdict.Conflicting.ToString(), StringComparison.Ordinal));
        var unresolved = observations.Count - supported - conflicting;
        var providers = observations.Values.Select(x => x.ProviderId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"NORMALIZATION_LEARNER_EXTERNAL_STRUCTURAL_MEMORY result=OK loaded={loaded} stored={observations.Count} providers={providers} supported={supported} conflicting={conflicting} unresolved={unresolved} changed={changed} source=authorized_lookup_results persistence=plugin_storage networkRequestsAdded=False scoreMutation=False canonicalMutation=False decisionMutation=False");
        #endif
    }

    internal static Dictionary<string, NormalizationLearnerExternalStructuralSnapshot> GetNormalizationLearnerExternalStructuralSupport(
        IEnumerable<(string HypothesisKey, string LearnedWork)> hypotheses)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        var result = new Dictionary<string, NormalizationLearnerExternalStructuralSnapshot>(StringComparer.Ordinal);
        if (context is null)
            return result;

        NormalizationLearnerExternalStructuralObservation[] observations;
        try
        {
            var stored = context.Storage.Get(NormalizationLearnerStorageNamespace, NormalizationLearnerExternalStructuralStorageKey);
            var json = stored.Succeeded ? stored.Value?.Value?.ToString() : null;
            if (string.IsNullOrWhiteSpace(json))
                return result;
            var state = JsonSerializer.Deserialize<NormalizationLearnerExternalStructuralState>(json, JsonOptions);
            if (state is null || state.SchemaVersion != NormalizationLearnerExternalStructuralSchemaVersion)
                return result;
            observations = (state.Observations ?? Array.Empty<NormalizationLearnerExternalStructuralObservation>())
                .Where(x => string.Equals(x.ProviderId, AIrhythmExternalLookupAdapter.TvMazeProviderId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
        #else
        catch (Exception)
        #endif
        {
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"NORMALIZATION_LEARNER_EXTERNAL_STRUCTURAL_SUPPORT result=LOAD_ERROR type={ex.GetType().Name} networkRequestsAdded=False");
            #endif
            return result;
        }

        var providerReliability = observations
            .Where(x => !string.IsNullOrWhiteSpace(x.ProviderId))
            .GroupBy(x => x.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var supported = g.Count(x => string.Equals(x.Verdict, AIrhythmExternalEvidenceVerdict.Supported.ToString(), StringComparison.Ordinal));
                var conflicting = g.Count(x => string.Equals(x.Verdict, AIrhythmExternalEvidenceVerdict.Conflicting.ToString(), StringComparison.Ordinal));
                return (supported + 2.0d) / (supported + conflicting + 4.0d);
            }, StringComparer.OrdinalIgnoreCase);

        var indexed = observations
            .Select(x => new
            {
                Observation = x,
                Keys = new[] { AIrhythmRecommendationEngine.ExternalPreEvaluationTitleKey(x.CanonicalTitle) }
                    .Concat((x.Aliases ?? Array.Empty<string>()).Select(AIrhythmRecommendationEngine.ExternalPreEvaluationTitleKey))
                    .Where(k => k.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal)
            })
            .Where(x => x.Keys.Count > 0)
            .ToArray();

        var matchedHypotheses = 0;
        foreach (var hypothesis in hypotheses
            .Where(x => !string.IsNullOrWhiteSpace(x.HypothesisKey) && !string.IsNullOrWhiteSpace(x.LearnedWork))
            .GroupBy(x => x.HypothesisKey, StringComparer.Ordinal)
            .Select(g => g.First()))
        {
            var workKey = AIrhythmRecommendationEngine.ExternalPreEvaluationTitleKey(hypothesis.LearnedWork);
            if (workKey.Length == 0)
                continue;

            var matched = indexed
                .Where(x => x.Keys.Contains(workKey))
                .GroupBy(x => $"{x.Observation.ProviderId}|{x.Observation.EntityId}|{AIrhythmRecommendationEngine.ExternalPreEvaluationTitleKey(x.Observation.CanonicalTitle)}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.Observation.Confidence).First().Observation)
                .ToArray();
            if (matched.Length == 0)
                continue;

            matchedHypotheses++;
            var providers = matched.Select(x => x.ProviderId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var reliability = providers.Length == 0 ? 0.5d : providers.Average(x => providerReliability.TryGetValue(x, out var value) ? value : 0.5d);
            var residual = 1.0d;
            foreach (var item in matched)
            {
                var providerWeight = providerReliability.TryGetValue(item.ProviderId, out var value) ? value : 0.5d;
                var itemSupport = Math.Clamp(item.Confidence * (0.50d + 0.50d * providerWeight), 0.0d, 0.95d);
                residual *= 1.0d - itemSupport;
            }
            var support = Math.Clamp(1.0d - residual, 0.0d, 1.0d);
            var stage = providers.Length >= 2 ? "corroborated" : "single_provider";
            result[hypothesis.HypothesisKey] = new NormalizationLearnerExternalStructuralSnapshot(
                hypothesis.HypothesisKey, support, reliability, providers.Length, matched.Length, stage);
        }

        var providerSummary = string.Join(",", providerReliability.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value:0.000}"));
        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"NORMALIZATION_LEARNER_EXTERNAL_STRUCTURAL_SUPPORT result=OBSERVED persistedEvidence={observations.Length} matchedHypotheses={matchedHypotheses} providers=[{providerSummary}] matching=canonical_or_alias_exact_normalized providerReliability=learned_supported_vs_conflicting_beta_prior networkRequestsAdded=False decisionMutation=False canonicalMutation=False scoreMutation=False");
        #endif
        return result;
    }

    internal static Dictionary<string, NormalizationLearnerLongTermSnapshot> ObserveNormalizationLearnerLongTermFeatures(
        IEnumerable<NormalizationLearnerLongTermSignal> currentSignals)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;

        var todayText = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var observations = new Dictionary<string, NormalizationLearnerLongTermObservation>(StringComparer.Ordinal);
        var loaded = 0;
        var schemaMismatch = false;

        if (context is not null)
        {
            try
            {
                var stored = context.Storage.Get(NormalizationLearnerStorageNamespace, NormalizationLearnerLongTermStorageKey);
                var json = stored.Succeeded ? stored.Value?.Value?.ToString() : null;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var state = JsonSerializer.Deserialize<NormalizationLearnerLongTermState>(json, JsonOptions);
                    if (state is not null && state.SchemaVersion == NormalizationLearnerLongTermSchemaVersion)
                    {
                        foreach (var item in state.Observations ?? Array.Empty<NormalizationLearnerLongTermObservation>())
                        {
                            if (string.IsNullOrWhiteSpace(item.HypothesisKey) || item.DistinctObservationDays <= 0)
                                continue;
                            observations[item.HypothesisKey] = item;
                        }
                        loaded = observations.Count;
                    }
                    else if (state is not null)
                    {
                        schemaMismatch = true;
                    }
                }
            }
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
            #else
            catch (Exception)
            #endif
            {
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"NORMALIZATION_LEARNER_LONGTERM_MEMORY result=LOAD_ERROR type={ex.GetType().Name} action=start_fresh_without_production_effect");
                #endif
            }
        }

        // 処理条件 — The cap can drop every tentative new item. Retain the prior canonical content so a no-op
        // truncation is not mistaken for a new learning day.
        var originalAtCapacity = observations.Count >= NormalizationLearnerLongTermLimit
            ? new Dictionary<string, NormalizationLearnerLongTermObservation>(observations, StringComparer.Ordinal)
            : null;
        var changed = false;
        foreach (var signal in currentSignals
            .Where(x => !string.IsNullOrWhiteSpace(x.HypothesisKey) && !string.IsNullOrWhiteSpace(x.LearnedWork))
            .GroupBy(x => x.HypothesisKey, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(x => x.Confidence).ThenByDescending(x => x.RoleFitness).First()))
        {
            if (observations.TryGetValue(signal.HypothesisKey, out var existing))
            {
                if (!string.Equals(existing.LastObservedDate, todayText, StringComparison.Ordinal))
                {
                    var n0 = Math.Max(1, existing.DistinctObservationDays);
                    var n1 = n0 + 1;
                    static (double Mean, double M2) UpdateMeanM2(double mean, double m2, int oldCount, double value)
                    {
                        var delta = value - mean;
                        var nextMean = mean + delta / (oldCount + 1.0d);
                        var delta2 = value - nextMean;
                        return (nextMean, m2 + delta * delta2);
                    }

                    var confidence = UpdateMeanM2(existing.MeanConfidence, existing.M2Confidence, n0, signal.Confidence);
                    var role = UpdateMeanM2(existing.MeanRoleFitness, existing.M2RoleFitness, n0, signal.RoleFitness);
                    var quality = UpdateMeanM2(existing.MeanPopulationQuality, existing.M2PopulationQuality, n0, signal.PopulationQuality);
                    observations[signal.HypothesisKey] = existing with
                    {
                        LastObservedDate = todayText,
                        DistinctObservationDays = n1,
                        MeanConfidence = confidence.Mean,
                        M2Confidence = confidence.M2,
                        MeanRoleFitness = role.Mean,
                        M2RoleFitness = role.M2,
                        MeanPopulationQuality = quality.Mean,
                        M2PopulationQuality = quality.M2,
                        MeanCollisionRate = existing.MeanCollisionRate + (signal.CollisionRate - existing.MeanCollisionRate) / n1,
                        MeanBoundaryIntrusion = existing.MeanBoundaryIntrusion + (signal.BoundaryIntrusionScore - existing.MeanBoundaryIntrusion) / n1,
                        MeanExternalStructuralSupport = existing.MeanExternalStructuralSupport + (signal.ExternalStructuralSupport - existing.MeanExternalStructuralSupport) / n1,
                        MeanExternalProviderReliability = existing.MeanExternalProviderReliability + (signal.ExternalProviderReliability - existing.MeanExternalProviderReliability) / n1,
                        LastExternalProviderCount = signal.ExternalProviderCount,
                        LastExternalEvidenceCount = signal.ExternalEvidenceCount,
                        RelationChangeDays = existing.RelationChangeDays + (string.Equals(existing.LastRelation, signal.RelationToCurrent, StringComparison.Ordinal) ? 0 : 1),
                        QualityIssueChangeDays = existing.QualityIssueChangeDays + (string.Equals(existing.LastQualityIssue, signal.QualityIssue, StringComparison.Ordinal) ? 0 : 1),
                        LastRelation = signal.RelationToCurrent,
                        LastQualityIssue = signal.QualityIssue
                    };
                    changed = true;
                }
                // 保存・読込 — Same-day rerenders deliberately never update long-term aggregates. The persistent model
                // represents independent day-level evidence, not UI refresh count or render frequency.
            }
            else
            {
                observations[signal.HypothesisKey] = new NormalizationLearnerLongTermObservation(
                    signal.HypothesisKey, signal.Kind, signal.LearnedWork, todayText, todayText, 1,
                    signal.Confidence, 0.0d, signal.RoleFitness, 0.0d, signal.PopulationQuality, 0.0d,
                    signal.CollisionRate, signal.BoundaryIntrusionScore, signal.ExternalStructuralSupport, signal.ExternalProviderReliability,
                    signal.ExternalProviderCount, signal.ExternalEvidenceCount, 0, 0, signal.RelationToCurrent, signal.QualityIssue);
                changed = true;
            }
        }

        if (observations.Count > NormalizationLearnerLongTermLimit)
        {
            observations = observations.Values
                .OrderByDescending(x => x.DistinctObservationDays)
                .ThenByDescending(x => x.MeanPopulationQuality)
                .ThenByDescending(x => x.LastObservedDate, StringComparer.Ordinal)
                .Take(NormalizationLearnerLongTermLimit)
                .ToDictionary(x => x.HypothesisKey, StringComparer.Ordinal);
            changed = true;
        }

        if (changed && originalAtCapacity is not null
            && !NormalizationModelContentChanged(originalAtCapacity, observations))
            changed = false;

        if (context is not null && (changed || schemaMismatch))
        {
            try
            {
                var state = new NormalizationLearnerLongTermState(
                    NormalizationLearnerLongTermSchemaVersion,
                    "normalization_learner_v11_persistent_daily_features_external_structural_support",
                    DateTimeOffset.Now,
                    observations.Values.OrderBy(x => x.HypothesisKey, StringComparer.Ordinal).ToArray());
                var json = JsonSerializer.Serialize(state, JsonOptions);
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                var written = context.Storage.Set(NormalizationLearnerStorageNamespace, NormalizationLearnerLongTermStorageKey, json, expectedRevision: null);
                if (!written.Succeeded)
                    WriteDeveloperLog($"NORMALIZATION_LEARNER_LONGTERM_MEMORY result=WRITE_FAILED loaded={loaded} stored={observations.Count} schemaMismatch={schemaMismatch} productionMutation=False");
                #else
                _ = context.Storage.Set(NormalizationLearnerStorageNamespace, NormalizationLearnerLongTermStorageKey, json, expectedRevision: null);
                #endif
            }
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
            #else
            catch (Exception)
            #endif
            {
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"NORMALIZATION_LEARNER_LONGTERM_MEMORY result=WRITE_ERROR type={ex.GetType().Name} loaded={loaded} stored={observations.Count} productionMutation=False");
                #endif
            }
        }

        var snapshots = new Dictionary<string, NormalizationLearnerLongTermSnapshot>(StringComparer.Ordinal);
        foreach (var item in observations.Values)
        {
            var days = Math.Max(1, item.DistinctObservationDays);
            var divisor = Math.Max(1, days - 1);
            var confidenceStd = days <= 1 ? 0.0d : Math.Sqrt(Math.Max(0.0d, item.M2Confidence / divisor));
            var roleStd = days <= 1 ? 0.0d : Math.Sqrt(Math.Max(0.0d, item.M2RoleFitness / divisor));
            var qualityStd = days <= 1 ? 0.0d : Math.Sqrt(Math.Max(0.0d, item.M2PopulationQuality / divisor));
            var featureDrift = Math.Clamp((confidenceStd + roleStd + qualityStd) / 0.90d, 0.0d, 1.0d);
            var transitionBase = Math.Max(1, days - 1);
            var relationDrift = Math.Min(1.0d, item.RelationChangeDays / (double)transitionBase);
            var issueDrift = Math.Min(1.0d, item.QualityIssueChangeDays / (double)transitionBase);
            var consistency = Math.Clamp(1.0d - (featureDrift * 0.60d + relationDrift * 0.20d + issueDrift * 0.20d), 0.0d, 1.0d);
            var maturity = Math.Min(1.0d, Math.Sqrt(days / 7.0d));
            var structuralLevel = Math.Clamp((item.MeanConfidence + item.MeanRoleFitness + item.MeanPopulationQuality) / 3.0d, 0.0d, 1.0d);
            var externalSupportAvailable = item.MeanExternalStructuralSupport > 0.0d;
            var externalStructuralLevel = Math.Clamp(item.MeanExternalStructuralSupport * (0.50d + 0.50d * item.MeanExternalProviderReliability), 0.0d, 1.0d);
            var combinedStructuralLevel = externalSupportAvailable
                ? Math.Clamp(structuralLevel * 0.88d + externalStructuralLevel * 0.12d, 0.0d, 1.0d)
                : structuralLevel;
            var riskLevel = Math.Clamp((item.MeanCollisionRate + item.MeanBoundaryIntrusion) / 2.0d, 0.0d, 1.0d);
            var reliability = Math.Clamp((combinedStructuralLevel * 0.65d + consistency * 0.35d) * (0.45d + maturity * 0.55d) * (1.0d - riskLevel * 0.35d), 0.0d, 1.0d);
            var stage = days < 3 ? "collecting"
                : consistency < 0.55d ? "drifting"
                : reliability >= 0.72d ? "stable"
                : "observed";

            snapshots[item.HypothesisKey] = new NormalizationLearnerLongTermSnapshot(
                item.HypothesisKey, days, item.MeanConfidence, item.MeanRoleFitness, item.MeanPopulationQuality,
                item.MeanCollisionRate, item.MeanBoundaryIntrusion, consistency, reliability, stage,
                item.LastRelation, item.LastQualityIssue, item.MeanExternalStructuralSupport, item.MeanExternalProviderReliability);
        }

        var maxDays = snapshots.Count == 0 ? 0 : snapshots.Values.Max(x => x.DistinctObservationDays);
        var stable = snapshots.Values.Count(x => string.Equals(x.Stage, "stable", StringComparison.Ordinal));
        var drifting = snapshots.Values.Count(x => string.Equals(x.Stage, "drifting", StringComparison.Ordinal));
        var meanReliability = snapshots.Count == 0 ? 0.0d : snapshots.Values.Average(x => x.Reliability);
        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"NORMALIZATION_LEARNER_LONGTERM_MEMORY result=OK loaded={loaded} stored={snapshots.Count} maxDays={maxDays} stable={stable} drifting={drifting} meanReliability={meanReliability:0.000} changed={changed} schema={NormalizationLearnerLongTermSchemaVersion} logic=normalization_learner_v11_persistent_daily_features_external_structural_support sameDayInflation=False decisionMutation=False shadowOnly=False promotionConsumer=True productionMutation=strict_promotion_only canonicalMutation=eligible_aliases_only scoreMutation=via_canonical_identity evidenceMutation=via_canonical_identity");
        #endif
        return snapshots;
    }

    internal static Dictionary<string, NormalizationLearnerHypothesisSnapshot> ObserveNormalizationLearnerHypotheses(
        IEnumerable<NormalizationLearnerHypothesisSignal> currentSignals)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;

        var todayText = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var observations = new Dictionary<string, NormalizationLearnerHypothesisObservation>(StringComparer.Ordinal);
        var loaded = 0;
        var schemaMismatch = false;

        if (context is not null)
        {
            try
            {
                var stored = context.Storage.Get(NormalizationLearnerStorageNamespace, NormalizationLearnerHypothesisStorageKey);
                var json = stored.Succeeded ? stored.Value?.Value?.ToString() : null;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var state = JsonSerializer.Deserialize<NormalizationLearnerHypothesisState>(json, JsonOptions);
                    if (state is not null && state.SchemaVersion == NormalizationLearnerHypothesisSchemaVersion)
                    {
                        foreach (var item in state.Observations ?? Array.Empty<NormalizationLearnerHypothesisObservation>())
                        {
                            if (string.IsNullOrWhiteSpace(item.HypothesisKey) || item.DistinctObservationDays <= 0)
                                continue;
                            observations[item.HypothesisKey] = item;
                        }
                        loaded = observations.Count;
                    }
                    else if (state is not null)
                    {
                        schemaMismatch = true;
                    }
                }
            }
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
            #else
            catch (Exception)
            #endif
            {
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"NORMALIZATION_LEARNER_HYPOTHESIS_MEMORY result=LOAD_ERROR type={ex.GetType().Name} action=start_fresh_without_production_effect");
                #endif
            }
        }

        // 処理条件 — The cap can drop every tentative new item. Retain the prior canonical content so a no-op
        // truncation is not mistaken for a new learning day.
        var originalAtCapacity = observations.Count >= NormalizationLearnerHypothesisLimit
            ? new Dictionary<string, NormalizationLearnerHypothesisObservation>(observations, StringComparer.Ordinal)
            : null;
        var changed = false;
        foreach (var signal in currentSignals
            .Where(x => !string.IsNullOrWhiteSpace(x.HypothesisKey) && !string.IsNullOrWhiteSpace(x.LearnedWork))
            .GroupBy(x => x.HypothesisKey, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(x => x.Confidence).ThenByDescending(x => x.RoleFitness).First()))
        {
            var isValidated = string.Equals(signal.Decision, "validated", StringComparison.Ordinal);
            var isRejected = signal.Decision.StartsWith("rejected_", StringComparison.Ordinal);
            var isDeferred = !isValidated && !isRejected;

            if (observations.TryGetValue(signal.HypothesisKey, out var existing))
            {
                if (!string.Equals(existing.LastObservedDate, todayText, StringComparison.Ordinal))
                {
                    observations[signal.HypothesisKey] = existing with
                    {
                        LastObservedDate = todayText,
                        DistinctObservationDays = existing.DistinctObservationDays + 1,
                        ValidatedDays = existing.ValidatedDays + (isValidated ? 1 : 0),
                        DeferredDays = existing.DeferredDays + (isDeferred ? 1 : 0),
                        RejectedDays = existing.RejectedDays + (isRejected ? 1 : 0),
                        ConfidenceEwma = existing.ConfidenceEwma * 0.70d + signal.Confidence * 0.30d,
                        RoleFitnessEwma = existing.RoleFitnessEwma * 0.70d + signal.RoleFitness * 0.30d,
                        LastDecision = signal.Decision,
                        LastRelation = signal.RelationToCurrent,
                        MaxDistinctCanonicalWorks = Math.Max(existing.MaxDistinctCanonicalWorks, signal.DistinctCanonicalWorks)
                    };
                    changed = true;
                }
                else
                {
                    // 処理条件 — Same-day rerenders never fabricate additional learning days. Only the freshest same-day quality
                    // state is retained for tomorrow's comparison.
                    var updated = existing with
                    {
                        ConfidenceEwma = Math.Max(existing.ConfidenceEwma, signal.Confidence),
                        RoleFitnessEwma = Math.Max(existing.RoleFitnessEwma, signal.RoleFitness),
                        LastDecision = signal.Decision,
                        LastRelation = signal.RelationToCurrent,
                        MaxDistinctCanonicalWorks = Math.Max(existing.MaxDistinctCanonicalWorks, signal.DistinctCanonicalWorks)
                    };
                    if (!object.Equals(updated, existing))
                    {
                        observations[signal.HypothesisKey] = updated;
                        changed = true;
                    }
                }
            }
            else
            {
                observations[signal.HypothesisKey] = new NormalizationLearnerHypothesisObservation(
                    signal.HypothesisKey, signal.Kind, signal.LearnedWork, todayText, todayText, 1,
                    isValidated ? 1 : 0, isDeferred ? 1 : 0, isRejected ? 1 : 0,
                    signal.Confidence, signal.RoleFitness, signal.Decision, signal.RelationToCurrent,
                    signal.DistinctCanonicalWorks);
                changed = true;
            }
        }

        if (observations.Count > NormalizationLearnerHypothesisLimit)
        {
            observations = observations.Values
                .OrderByDescending(x => x.DistinctObservationDays)
                .ThenByDescending(x => x.ValidatedDays)
                .ThenByDescending(x => x.ConfidenceEwma)
                .ThenByDescending(x => x.LastObservedDate, StringComparer.Ordinal)
                .Take(NormalizationLearnerHypothesisLimit)
                .ToDictionary(x => x.HypothesisKey, StringComparer.Ordinal);
            changed = true;
        }

        if (changed && originalAtCapacity is not null
            && !NormalizationModelContentChanged(originalAtCapacity, observations))
            changed = false;

        if (context is not null && (changed || schemaMismatch))
        {
            try
            {
                var state = new NormalizationLearnerHypothesisState(
                    NormalizationLearnerHypothesisSchemaVersion,
                    "normalization_learner_v5_hypothesis_survival",
                    DateTimeOffset.Now,
                    observations.Values.OrderBy(x => x.HypothesisKey, StringComparer.Ordinal).ToArray());
                var json = JsonSerializer.Serialize(state, JsonOptions);
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                var written = context.Storage.Set(NormalizationLearnerStorageNamespace, NormalizationLearnerHypothesisStorageKey, json, expectedRevision: null);
                if (!written.Succeeded)
                    WriteDeveloperLog($"NORMALIZATION_LEARNER_HYPOTHESIS_MEMORY result=WRITE_FAILED loaded={loaded} stored={observations.Count} schemaMismatch={schemaMismatch} productionMutation=False");
                #else
                _ = context.Storage.Set(NormalizationLearnerStorageNamespace, NormalizationLearnerHypothesisStorageKey, json, expectedRevision: null);
                #endif
            }
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
            #else
            catch (Exception)
            #endif
            {
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"NORMALIZATION_LEARNER_HYPOTHESIS_MEMORY result=WRITE_ERROR type={ex.GetType().Name} loaded={loaded} stored={observations.Count} productionMutation=False");
                #endif
            }
        }

        var snapshots = new Dictionary<string, NormalizationLearnerHypothesisSnapshot>(StringComparer.Ordinal);
        foreach (var item in observations.Values)
        {
            var days = Math.Max(1, item.DistinctObservationDays);
            var validatedRatio = item.ValidatedDays / (double)days;
            var rejectedRatio = item.RejectedDays / (double)days;
            var decisionConsistency = Math.Max(item.ValidatedDays, Math.Max(item.DeferredDays, item.RejectedDays)) / (double)days;
            var relationPenalty = string.Equals(item.LastRelation, "conflict", StringComparison.Ordinal) ? 0.15d : 0.0d;
            var genericityPenalty = item.MaxDistinctCanonicalWorks >= 4 ? 0.18d : item.MaxDistinctCanonicalWorks >= 2 ? 0.06d : 0.0d;
            var stability = Math.Clamp(
                item.ConfidenceEwma * 0.30d
                + item.RoleFitnessEwma * 0.30d
                + decisionConsistency * 0.20d
                + validatedRatio * 0.20d
                - rejectedRatio * 0.25d
                - relationPenalty
                - genericityPenalty,
                0.0d, 1.0d);

            string stage;
            if (days >= 3 && rejectedRatio >= 0.50d)
                stage = "persistent_reject";
            else if (days >= 2 && item.ValidatedDays > 0 && item.RejectedDays > 0)
                stage = "contested";
            else if (days >= 3 && item.ValidatedDays >= 2 && validatedRatio >= 0.67d
                && item.RejectedDays == 0 && stability >= 0.72d)
                stage = "survivor";
            else
                stage = "collecting";

            snapshots[item.HypothesisKey] = new NormalizationLearnerHypothesisSnapshot(
                item.HypothesisKey, days, item.ValidatedDays, item.DeferredDays, item.RejectedDays,
                item.ConfidenceEwma, item.RoleFitnessEwma, stability, stage, item.LastDecision, item.LastRelation);
        }

        var maxDays = snapshots.Count == 0 ? 0 : snapshots.Values.Max(x => x.DistinctObservationDays);
        var survivor = snapshots.Values.Count(x => string.Equals(x.Stage, "survivor", StringComparison.Ordinal));
        var contested = snapshots.Values.Count(x => string.Equals(x.Stage, "contested", StringComparison.Ordinal));
        var persistentReject = snapshots.Values.Count(x => string.Equals(x.Stage, "persistent_reject", StringComparison.Ordinal));
        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"NORMALIZATION_LEARNER_HYPOTHESIS_MEMORY result=OK loaded={loaded} stored={snapshots.Count} maxDays={maxDays} survivors={survivor} contested={contested} persistentReject={persistentReject} changed={changed} schema={NormalizationLearnerHypothesisSchemaVersion} logic=normalization_learner_v5_hypothesis_survival sameDayInflation=False shadowOnly=False promotionConsumer=True productionMutation=strict_promotion_only canonicalMutation=eligible_aliases_only scoreMutation=via_canonical_identity evidenceMutation=via_canonical_identity");
        #endif

        return snapshots;
    }

    internal static IReadOnlyDictionary<string, string> GetNormalizationPromotions()
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var stored = context.Storage.Get(NormalizationPromotionStorageNamespace, NormalizationPromotionStorageKey);
            var json = stored.Succeeded ? stored.Value?.Value?.ToString() : null;
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>(StringComparer.Ordinal);
            var state = JsonSerializer.Deserialize<AIrhythmNormalizationPromotionState>(json, JsonOptions);
            if (state is null || state.SchemaVersion != NormalizationPromotionSchemaVersion
                || !string.Equals(state.LogicVersion, NormalizationPromotionLogicVersion, StringComparison.Ordinal))
                return new Dictionary<string, string>(StringComparer.Ordinal);
            return (state.Entries ?? Array.Empty<AIrhythmNormalizationPromotionEntry>())
                .Where(x => !string.IsNullOrWhiteSpace(x.AliasKey) && !string.IsNullOrWhiteSpace(x.LearnedWork))
                .GroupBy(x => x.AliasKey, StringComparer.Ordinal)
                .Where(g => g.Select(x => x.LearnedWork).Distinct(StringComparer.Ordinal).Count() == 1)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.PromotedAt).First().LearnedWork, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    internal static void UpdateNormalizationPromotions(IEnumerable<AIrhythmNormalizationPromotionSignal> signals)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null) return;
        var now = DateTimeOffset.Now;
        var entries = signals
            .Where(x => !string.IsNullOrWhiteSpace(x.AliasKey) && !string.IsNullOrWhiteSpace(x.LearnedWork))
            .GroupBy(x => x.AliasKey, StringComparer.Ordinal)
            .Where(g => g.Select(x => x.LearnedWork).Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(g => g.OrderByDescending(x => x.Confidence).First())
            .Select(x => new AIrhythmNormalizationPromotionEntry(
                x.AliasKey, x.LearnedWork, x.HypothesisKey, x.Confidence, x.ObservationDays, x.LongTermDays, now))
            .OrderBy(x => x.AliasKey, StringComparer.Ordinal)
            .ToArray();
        try
        {
            var state = new AIrhythmNormalizationPromotionState(
                NormalizationPromotionSchemaVersion, NormalizationPromotionLogicVersion, now, entries);
            var written = context.Storage.Set(NormalizationPromotionStorageNamespace, NormalizationPromotionStorageKey,
                JsonSerializer.Serialize(state, JsonOptions), expectedRevision: null);
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"NORMALIZATION_PROMOTION_MEMORY result={(written.Succeeded ? "OK" : "WRITE_FAILED")} active={entries.Length} schema={NormalizationPromotionSchemaVersion} logic={NormalizationPromotionLogicVersion} rollback=replace_with_current_strict_consensus provenance=True");
            #endif
        }
        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
        #else
        catch (Exception)
        #endif
        {
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"NORMALIZATION_PROMOTION_MEMORY result=WRITE_ERROR type={ex.GetType().Name}");
            #endif
        }
    }

}
