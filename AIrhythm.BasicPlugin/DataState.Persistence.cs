namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmDataState
{
    public static void InvalidateForAction(string reason)
        => Invalidate(reason);

    public static async Task<AIrhythmSaveResult> BackupPersistentDataAsync(string currentWindowId, CancellationToken cancellationToken)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return new(false, "バックアップできませんでした");

        try
        {
            var picked = await context.PathPicker.PickFolderAsync(new PluginFolderPickerRequest
            {
                Title = "AI-rhythm バックアップの保存先を選択",
                OwnerWindowId = string.IsNullOrWhiteSpace(currentWindowId) ? null : currentWindowId
            }, cancellationToken).ConfigureAwait(false);
            if (picked.Cancelled)
                return new(true, string.Empty, Changed: false);
            if (!picked.Accepted || string.IsNullOrWhiteSpace(picked.SelectedPath))
                return new(false, picked.Message ?? "保存先を選択できませんでした");

            var payload = CapturePersistentStorage(context);
            var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson))).ToLowerInvariant();
            var envelope = new AIrhythmBackupEnvelope(
                BackupFormatVersion,
                AIrhythmIdentity.DisplayName,
                AIrhythmIdentity.Version,
                DateTimeOffset.Now,
                payloadJson,
                hash);
            var backupJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
            var fileName = $"AI-rhythm_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            var destination = Path.Combine(picked.SelectedPath, fileName);
            var temp = destination + ".tmp";
            await File.WriteAllTextAsync(temp, backupJson, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, destination, overwrite: true);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"persistent data backup result=OK file={Path.GetFileName(destination)} namespaces={payload.Storage.Count} items={payload.Storage.Sum(x => x.Value.Count)} format={BackupFormatVersion}");
#endif
            return new(true, $"バックアップしました: {Path.GetFileName(destination)}", Changed: true);
        }
        catch (OperationCanceledException)
        {
            return new(true, string.Empty, Changed: false);
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
#else
        catch (Exception)
#endif
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"persistent data backup result=ERROR type={ex.GetType().Name}");
#endif
            return new(false, "バックアップできませんでした");
        }
    }

    public static async Task<AIrhythmSaveResult> RestorePersistentDataAsync(string currentWindowId, CancellationToken cancellationToken)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return new(false, "復元できませんでした");

        try
        {
            var picked = await context.PathPicker.PickFileAsync(new PluginFilePickerRequest
            {
                Title = "AI-rhythm バックアップを選択",
                OwnerWindowId = string.IsNullOrWhiteSpace(currentWindowId) ? null : currentWindowId,
                Filters = new[]
                {
                    new PluginFileFilter { Label = "AI-rhythm バックアップ", Patterns = new[] { "AI-rhythm_backup_*.json", "*.json" } }
                }
            }, cancellationToken).ConfigureAwait(false);
            if (picked.Cancelled)
                return new(true, string.Empty, Changed: false);
            if (!picked.Accepted || string.IsNullOrWhiteSpace(picked.SelectedPath))
                return new(false, picked.Message ?? "バックアップを選択できませんでした");

            var json = await File.ReadAllTextAsync(picked.SelectedPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<AIrhythmBackupEnvelope>(json, JsonOptions);
            if (envelope is null
                || envelope.FormatVersion != BackupFormatVersion
                || !string.Equals(envelope.Product, AIrhythmIdentity.DisplayName, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(envelope.PayloadJson)
                || string.IsNullOrWhiteSpace(envelope.PayloadSha256))
                return new(false, "AI-rhythmの対応するバックアップではありません");

            var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.PayloadJson))).ToLowerInvariant();
            if (!string.Equals(actualHash, envelope.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                return new(false, "バックアップの整合性を確認できませんでした");

            var payload = JsonSerializer.Deserialize<AIrhythmBackupPayload>(envelope.PayloadJson, JsonOptions);
            if (payload?.Storage is null || !ValidateBackupPayload(payload))
                return new(false, "バックアップの内容を確認できませんでした");

            if (!ReplacePersistentStorage(context, payload, out var restoreError))
                return new(false, string.IsNullOrWhiteSpace(restoreError) ? "復元できませんでした" : restoreError);

            ResetPersistentCachesAfterStorageChange();
            Invalidate("PersistentDataRestored");
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"persistent data restore result=OK file={Path.GetFileName(picked.SelectedPath)} namespaces={payload.Storage.Count} items={payload.Storage.Sum(x => x.Value.Count)} format={envelope.FormatVersion} sourceVersion={envelope.ProductVersion}");
#endif
            return new(true, "バックアップから復元しました", Changed: true);
        }
        catch (OperationCanceledException)
        {
            return new(true, string.Empty, Changed: false);
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
#else
        catch (Exception)
#endif
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"persistent data restore result=ERROR type={ex.GetType().Name}");
#endif
            return new(false, "復元できませんでした");
        }
    }

    public static AIrhythmSaveResult ResetAccumulatedData()
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return new(false, "蓄積データをリセットできませんでした");

        try
        {
            var resetAt = DateTimeOffset.Now;
            var previous = CapturePersistentStorage(context);
            var empty = JsonSerializer.Serialize(Array.Empty<object>(), JsonOptions);
            var emptyUserModel = JsonSerializer.Serialize(new AIrhythmLearnedUserModelState(
                LearnedUserModelSchemaVersion, 0, resetAt, Array.Empty<string>(),
                new Dictionary<string, double>(), new Dictionary<string, double>(), new Dictionary<string, double>(),
                new Dictionary<string, double>(), new Dictionary<string, double>(), new Dictionary<string, double>(),
                Array.Empty<string>(), ContributorLogicVersion, BasePreferenceLogicVersion), JsonOptions);
            var emptyCalibration = JsonSerializer.Serialize(new AIrhythmEvidenceCalibrationState(
                EvidenceCalibrationSchemaVersion, 0, resetAt, Array.Empty<string>(),
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), JsonOptions);
            var zeroUsage = JsonSerializer.Serialize(new AIrhythmUsageCounterState(0, 0, Version: 2, RecordingAccountingStart: resetAt,
                CountedRecordingKeys: Array.Empty<string>()), JsonOptions);
            var marker = JsonSerializer.Serialize(new AIrhythmDataResetMarker(resetAt), JsonOptions);

            foreach (var storageNamespace in new[] { RecordingFactStorageNamespace, RecoveryAttemptStorageNamespace, RecoveryLookupStorageNamespace, NormalizationLearnerStorageNamespace, NormalizationPromotionStorageNamespace })
            {
                foreach (var key in context.Storage.ListKeys(storageNamespace).ToArray())
                {
                    var deleted = context.Storage.Delete(storageNamespace, key, expectedRevision: null);
                    if (!deleted.Succeeded)
                    {
                        ReplacePersistentStorage(context, previous, out _);
                        return new(false, "蓄積データをリセットできませんでした");
                    }
                }
            }
            foreach (var key in new[] { ExternalManualRefreshDateStorageKey, ExternalProviderOutcomeStorageKey, AIrhythmExternalLookupAdapter.OversizedEpisodeStorageKey })
            {
                if (!context.Storage.Exists(ExternalLookupRuntimeStorageNamespace, key)) continue;
                if (context.Storage.Delete(ExternalLookupRuntimeStorageNamespace, key, expectedRevision: null).Succeeded) continue;
                ReplacePersistentStorage(context, previous, out _);
                return new(false, "蓄積データをリセットできませんでした");
            }

            var writes = new[]
            {
                context.Storage.Set("rhythmSearch", "recent", empty, expectedRevision: null),
                context.Storage.Set("rhythmSearch", "interestSignals", empty, expectedRevision: null),
                context.Storage.Set(LearnedUserModelStorageNamespace, LearnedUserModelStorageKey, emptyUserModel, expectedRevision: null),
                context.Storage.Set(LearnedUserModelStorageNamespace, EvidenceCalibrationStorageKey, emptyCalibration, expectedRevision: null),
                context.Storage.Set("usage", "totals", zeroUsage, expectedRevision: null),
                context.Storage.Set(DataLifecycleStorageNamespace, DataResetCutoffStorageKey, marker, expectedRevision: null)
            };
            if (writes.Any(x => !x.Succeeded))
            {
                ReplacePersistentStorage(context, previous, out _);
                return new(false, "蓄積データをリセットできませんでした");
            }

            ResetPersistentCachesAfterStorageChange();
            SetDataResetCutoffCache(resetAt);
            lock (Gate)
            {
                _usageTotals = default;
                _usageRecordingAccountingStart = resetAt;
                UsageCountedRecordingKeys.Clear();
                _usageRecordingLedgerRevision = 0;
                _usageTotalsInitialized = true;
                _usageTotalsDirty = false;
            }
            Invalidate("AccumulatedDataReset");
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"persistent accumulated data reset result=OK resetAt={resetAt:O} policy=start_new_epoch_settings_preserved");
#endif
            return new(true, "今この時点から新しく蓄積を開始します", Changed: true);
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
#else
        catch (Exception)
#endif
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"persistent accumulated data reset result=ERROR type={ex.GetType().Name}");
#endif
            return new(false, "蓄積データをリセットできませんでした");
        }
    }

    private static AIrhythmBackupPayload CapturePersistentStorage(ITvAirPluginRuntimeContext context)
    {
        var storage = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        CaptureFixedStorage(context, storage, "settings", new[] { "main" });
        CaptureFixedStorage(context, storage, "usage", new[] { "totals" });
        CaptureFixedStorage(context, storage, "rhythmSearch", new[] { "recent", "interestSignals", LearnedUserModelStorageKey, EvidenceCalibrationStorageKey });
        CaptureFixedStorage(context, storage, DataLifecycleStorageNamespace, new[] { DataResetCutoffStorageKey });
        CaptureFixedStorage(context, storage, ExternalLookupRuntimeStorageNamespace,
            new[] { ExternalManualRefreshDateStorageKey, ExternalProviderOutcomeStorageKey, AIrhythmExternalLookupAdapter.OversizedEpisodeStorageKey });
        CaptureAllStorageKeys(context, storage, RecordingFactStorageNamespace);
        CaptureAllStorageKeys(context, storage, RecoveryAttemptStorageNamespace);
        CaptureFixedStorage(context, storage, RecoveryLookupStorageNamespace, new[] { RecoveryLookupStorageKey });
        CaptureAllStorageKeys(context, storage, NormalizationLearnerStorageNamespace);
        CaptureFixedStorage(context, storage, NormalizationPromotionStorageNamespace, new[] { NormalizationPromotionStorageKey });
        return new AIrhythmBackupPayload(storage);
    }

    private static void CaptureFixedStorage(ITvAirPluginRuntimeContext context, Dictionary<string, Dictionary<string, string>> target, string storageNamespace, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            var result = context.Storage.Get(storageNamespace, key);
            if (!result.Succeeded || result.Value?.Value is null)
                continue;
            if (!target.TryGetValue(storageNamespace, out var bucket))
                target[storageNamespace] = bucket = new Dictionary<string, string>(StringComparer.Ordinal);
            bucket[key] = result.Value.Value.ToString() ?? string.Empty;
        }
    }

    private static void CaptureAllStorageKeys(ITvAirPluginRuntimeContext context, Dictionary<string, Dictionary<string, string>> target, string storageNamespace)
    {
        foreach (var key in context.Storage.ListKeys(storageNamespace).OrderBy(x => x, StringComparer.Ordinal))
            CaptureFixedStorage(context, target, storageNamespace, new[] { key });
    }

    private static bool ValidateBackupPayload(AIrhythmBackupPayload payload)
    {
        var allowedNamespaces = new HashSet<string>(new[] { "settings", "usage", "rhythmSearch", RecordingFactStorageNamespace, RecoveryAttemptStorageNamespace, RecoveryLookupStorageNamespace, DataLifecycleStorageNamespace, ExternalLookupRuntimeStorageNamespace, NormalizationLearnerStorageNamespace, NormalizationPromotionStorageNamespace }, StringComparer.Ordinal);
        foreach (var pair in payload.Storage)
        {
            if (!allowedNamespaces.Contains(pair.Key) || pair.Value is null)
                return false;
            foreach (var item in pair.Value)
            {
                if (string.IsNullOrWhiteSpace(item.Key) || item.Value is null || item.Value.Length > 16 * 1024 * 1024)
                    return false;
                try { using var _ = JsonDocument.Parse(item.Value); } catch { return false; }
            }
        }
        return true;
    }

    private static bool ReplacePersistentStorage(ITvAirPluginRuntimeContext context, AIrhythmBackupPayload desired, out string error)
    {
        error = string.Empty;
        var before = CapturePersistentStorage(context);
        try
        {
            foreach (var storageNamespace in new[] { "settings", "usage", "rhythmSearch", RecordingFactStorageNamespace, RecoveryAttemptStorageNamespace, RecoveryLookupStorageNamespace, DataLifecycleStorageNamespace, ExternalLookupRuntimeStorageNamespace, NormalizationLearnerStorageNamespace, NormalizationPromotionStorageNamespace })
            {
                var desiredKeys = desired.Storage.TryGetValue(storageNamespace, out var desiredBucket)
                    ? new HashSet<string>(desiredBucket.Keys, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                IEnumerable<string> currentKeys = storageNamespace == RecordingFactStorageNamespace || storageNamespace == RecoveryAttemptStorageNamespace || storageNamespace == NormalizationLearnerStorageNamespace
                    ? context.Storage.ListKeys(storageNamespace)
                    : storageNamespace switch
                    {
                        "settings" => new[] { "main" }.Where(key => context.Storage.Exists(storageNamespace, key)),
                        "usage" => new[] { "totals" }.Where(key => context.Storage.Exists(storageNamespace, key)),
                        "rhythmSearch" => new[] { "recent", "interestSignals", LearnedUserModelStorageKey, EvidenceCalibrationStorageKey }.Where(key => context.Storage.Exists(storageNamespace, key)),
                        RecoveryLookupStorageNamespace => new[] { RecoveryLookupStorageKey }.Where(key => context.Storage.Exists(storageNamespace, key)),
                        NormalizationPromotionStorageNamespace => new[] { NormalizationPromotionStorageKey }.Where(key => context.Storage.Exists(storageNamespace, key)),
                        ExternalLookupRuntimeStorageNamespace => new[] { ExternalManualRefreshDateStorageKey, ExternalProviderOutcomeStorageKey, AIrhythmExternalLookupAdapter.OversizedEpisodeStorageKey }.Where(key => context.Storage.Exists(storageNamespace, key)),
                        _ => new[] { DataResetCutoffStorageKey }.Where(key => context.Storage.Exists(storageNamespace, key))
                    };
                foreach (var key in currentKeys.ToArray())
                {
                    if (desiredKeys.Contains(key))
                        continue;
                    var deleted = context.Storage.Delete(storageNamespace, key, expectedRevision: null);
                    if (!deleted.Succeeded)
                        throw new InvalidOperationException($"delete:{storageNamespace}/{key}");
                }
                if (desiredBucket is null)
                    continue;
                foreach (var item in desiredBucket)
                {
                    var written = context.Storage.Set(storageNamespace, item.Key, item.Value, expectedRevision: null);
                    if (!written.Succeeded)
                        throw new InvalidOperationException($"write:{storageNamespace}/{item.Key}");
                }
            }
            return true;
        }
        catch
        {
            try { RestoreStorageSnapshot(context, before); } catch { }
            error = "復元中に保存データを書き換えられなかったため、元の状態へ戻しました";
            return false;
        }
    }

    private static void RestoreStorageSnapshot(ITvAirPluginRuntimeContext context, AIrhythmBackupPayload snapshot)
    {
        foreach (var storageNamespace in new[] { "settings", "usage", "rhythmSearch", RecordingFactStorageNamespace, RecoveryAttemptStorageNamespace, RecoveryLookupStorageNamespace, DataLifecycleStorageNamespace, ExternalLookupRuntimeStorageNamespace, NormalizationLearnerStorageNamespace, NormalizationPromotionStorageNamespace })
        {
            IEnumerable<string> currentKeys = storageNamespace == RecordingFactStorageNamespace || storageNamespace == RecoveryAttemptStorageNamespace || storageNamespace == NormalizationLearnerStorageNamespace
                ? context.Storage.ListKeys(storageNamespace)
                : storageNamespace switch
                {
                    "settings" => new[] { "main" }.Where(key => context.Storage.Exists(storageNamespace, key)),
                    "usage" => new[] { "totals" }.Where(key => context.Storage.Exists(storageNamespace, key)),
                    "rhythmSearch" => new[] { "recent", "interestSignals", LearnedUserModelStorageKey, EvidenceCalibrationStorageKey }.Where(key => context.Storage.Exists(storageNamespace, key)),
                    RecoveryLookupStorageNamespace => new[] { RecoveryLookupStorageKey }.Where(key => context.Storage.Exists(storageNamespace, key)),
                        NormalizationPromotionStorageNamespace => new[] { NormalizationPromotionStorageKey }.Where(key => context.Storage.Exists(storageNamespace, key)),
                    ExternalLookupRuntimeStorageNamespace => new[] { ExternalManualRefreshDateStorageKey, ExternalProviderOutcomeStorageKey, AIrhythmExternalLookupAdapter.OversizedEpisodeStorageKey }.Where(key => context.Storage.Exists(storageNamespace, key)),
                    _ => new[] { DataResetCutoffStorageKey }.Where(key => context.Storage.Exists(storageNamespace, key))
                };
            foreach (var key in currentKeys.ToArray())
                context.Storage.Delete(storageNamespace, key, expectedRevision: null);
            if (!snapshot.Storage.TryGetValue(storageNamespace, out var bucket))
                continue;
            foreach (var item in bucket)
                context.Storage.Set(storageNamespace, item.Key, item.Value, expectedRevision: null);
        }
    }

    private static void ResetPersistentCachesAfterStorageChange()
    {
        ClearLearnedUserModelReadCache();
        AIrhythmExternalLookupAdapter.ResetOversizedEpisodeCache();
        AIrhythmRecommendationEngine.ClearContributorMetadataCache();
        AIrhythmRecommendationEngine.ClearNormalizationStructuralCandidates();
        lock (Gate)
        {
            _cachedSnapshot = null;
            _lastSuccessfulSnapshot = null;
            _usageTotals = default;
            _usageTotalsInitialized = false;
            _usageTotalsDirty = false;
            _lastManualExternalRefreshDate = null;
            _lastManualExternalRefreshDateLoaded = false;
            UsageRecentReservationIds.Clear();
            UsageRecentReservationOrder.Clear();
            UsageCountedRecordingKeys.Clear();
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
            _cacheGeneration++;
        }
    }

    public static AIrhythmSaveResult ResetLearningInformation()
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return new(false, "学習情報をリセットできませんでした");

        try
        {
            var recentBefore = context.Storage.Get("rhythmSearch", "recent");
            var interestsBefore = context.Storage.Get("rhythmSearch", "interestSignals");
            var userModelBefore = context.Storage.Get(LearnedUserModelStorageNamespace, LearnedUserModelStorageKey);
            var calibrationBefore = context.Storage.Get(LearnedUserModelStorageNamespace, EvidenceCalibrationStorageKey);
            var recentJson = recentBefore.Succeeded ? recentBefore.Value?.Value?.ToString() : null;
            var interestsJson = interestsBefore.Succeeded ? interestsBefore.Value?.Value?.ToString() : null;
            var userModelJson = userModelBefore.Succeeded ? userModelBefore.Value?.Value?.ToString() : null;
            var calibrationJson = calibrationBefore.Succeeded ? calibrationBefore.Value?.Value?.ToString() : null;
            var hasNormalizationLearning = context.Storage.ListKeys(NormalizationLearnerStorageNamespace).Any()
                || context.Storage.Exists(NormalizationPromotionStorageNamespace, NormalizationPromotionStorageKey);
            var hasProviderOutcomeLearning = context.Storage.Exists(ExternalLookupRuntimeStorageNamespace, ExternalProviderOutcomeStorageKey);
            var hasCalibrationLearning = !string.IsNullOrWhiteSpace(calibrationJson) && !calibrationJson.Contains("\"sampleCount\":0", StringComparison.OrdinalIgnoreCase);
            var hasLearningInformation = HasStoredArrayItems(recentJson) || HasStoredArrayItems(interestsJson) || HasLearnedUserModel(userModelJson) || hasCalibrationLearning || hasNormalizationLearning || hasProviderOutcomeLearning;
            if (!hasLearningInformation)
                return new(true, string.Empty, Changed: false);

            var empty = JsonSerializer.Serialize(Array.Empty<object>(), JsonOptions);
            var emptyUserModel = JsonSerializer.Serialize(new AIrhythmLearnedUserModelState(
                LearnedUserModelSchemaVersion, 0, DateTimeOffset.Now, Array.Empty<string>(),
                new Dictionary<string, double>(), new Dictionary<string, double>(), new Dictionary<string, double>(),
                new Dictionary<string, double>(), new Dictionary<string, double>(), new Dictionary<string, double>(),
                Array.Empty<string>(), ContributorLogicVersion, BasePreferenceLogicVersion), JsonOptions);
            var recent = context.Storage.Set("rhythmSearch", "recent", empty, expectedRevision: null);
            var interests = context.Storage.Set("rhythmSearch", "interestSignals", empty, expectedRevision: null);
            var userModel = context.Storage.Set(LearnedUserModelStorageNamespace, LearnedUserModelStorageKey, emptyUserModel, expectedRevision: null);
            var calibration = context.Storage.Set(LearnedUserModelStorageNamespace, EvidenceCalibrationStorageKey, JsonSerializer.Serialize(new AIrhythmEvidenceCalibrationState(EvidenceCalibrationSchemaVersion, 0, DateTimeOffset.Now, Array.Empty<string>(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), JsonOptions), expectedRevision: null);
            if (!recent.Succeeded || !interests.Succeeded || !userModel.Succeeded || !calibration.Succeeded)
                return new(false, recent.Error?.Message ?? interests.Error?.Message ?? userModel.Error?.Message ?? calibration.Error?.Message ?? "学習情報をリセットできませんでした");
            foreach (var storageNamespace in new[] { NormalizationLearnerStorageNamespace, NormalizationPromotionStorageNamespace })
                foreach (var key in context.Storage.ListKeys(storageNamespace).ToArray())
                    if (!context.Storage.Delete(storageNamespace, key, expectedRevision: null).Succeeded)
                        return new(false, "学習情報をリセットできませんでした");
            if (context.Storage.Exists(ExternalLookupRuntimeStorageNamespace, ExternalProviderOutcomeStorageKey)
                && !context.Storage.Delete(ExternalLookupRuntimeStorageNamespace, ExternalProviderOutcomeStorageKey, expectedRevision: null).Succeeded)
                return new(false, "学習情報をリセットできませんでした");

            ClearLearnedUserModelReadCache();
            Invalidate("LearningInformationReset");
            return new(true, string.Empty, Changed: true);
        }
        catch
        {
            return new(false, "学習情報をリセットできませんでした");
        }
    }

    private static bool HasLearnedUserModel(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var model = JsonSerializer.Deserialize<AIrhythmLearnedUserModelState>(json, JsonOptions);
            return model is not null && model.SchemaVersion == LearnedUserModelSchemaVersion && model.ObservationCount > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasStoredArrayItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0;
        }
        catch
        {
            return false;
        }
    }

    public static AIrhythmSaveResult SaveSettings(AIrhythmSettings settings, string? expectedRevision)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return new(false, "設定を保存できませんでした");

        var normalized = new AIrhythmSettings(
            Math.Clamp(settings.Limit, 10, 30),
            settings.Preferred?.Trim() ?? string.Empty,
            settings.Excluded?.Trim() ?? string.Empty);
        try
        {
            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            var current = context.Storage.Get("settings", "main");
            var currentJson = current.Succeeded ? current.Value?.Value?.ToString()?.Trim() : null;
            if (string.Equals(currentJson, json, StringComparison.Ordinal))
                return new(true, string.Empty, Changed: false);

            var expected = long.TryParse(expectedRevision, out var parsedRevision) ? parsedRevision : (long?)null;
            var result = context.Storage.Set("settings", "main", json, expected);
            if (result.Succeeded)
            {
                lock (Gate)
                {
                    _cachedSnapshot = null;
                    _cacheGeneration++;
                }
                return new(true, string.Empty, Changed: true);
            }
            return new(false, result.Error?.Message ?? "設定を保存できませんでした");
        }
        catch
        {
            return new(false, "設定を保存できませんでした");
        }
    }

    private static void HandleRuntimeEvent(string eventType, PluginEventEnvelope envelope)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is not null)
        {
            TvAirReservationDto? resolvedAddedReservation = null;
            if (string.Equals(eventType, "ReservationAdded", StringComparison.OrdinalIgnoreCase))
                resolvedAddedReservation = ResolveAddedReservation(context, envelope);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            ObserveEvidenceCalibrationFromRuntimeEvent(context, eventType, envelope, resolvedAddedReservation);
#endif
            UpdateUsageTotalsFromRuntimeEvent(context, eventType, envelope, resolvedAddedReservation);
        }
        if (string.Equals(eventType, "PluginPermissionChanged", StringComparison.OrdinalIgnoreCase))
            RefreshExternalLookupCapability();
        Invalidate(eventType);
    }

}
