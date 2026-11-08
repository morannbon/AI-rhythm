namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmDataState
{
    private static TvAirReservationDto? ResolveAddedReservation(ITvAirPluginRuntimeContext context, PluginEventEnvelope envelope)
    {
        var entityId = envelope.EntityId?.Trim() ?? string.Empty;
        var matchedReservation = envelope.Payload as TvAirReservationDto;
        try
        {
            var userReservations = context.Reservations.List(new TvAirReservationQueryDto { IncludeSystemEntries = false });
            if (matchedReservation is null || !userReservations.Any(x => string.Equals(x.ReservationId, matchedReservation.ReservationId, StringComparison.OrdinalIgnoreCase)))
            {
                matchedReservation = userReservations.FirstOrDefault(x =>
                    string.Equals(x.ReservationId, entityId, StringComparison.OrdinalIgnoreCase)
                    || (entityId.Length > 0 && entityId.EndsWith($":{x.ReservationId}", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch { return null; }
        return matchedReservation;
    }

    private static bool FinalizedRecordingMatchesEntity(TvAirRecordingHistoryDto item, string entity, bool allowReservationFallback = true)
    {
        if (!item.ResultFinalized || string.IsNullOrWhiteSpace(item.ProgramTitle) || string.IsNullOrWhiteSpace(entity))
            return false;
        var colon = entity.IndexOf(':');
        var kind = colon < 0 ? string.Empty : entity[..colon];
        var id = colon < 0 ? entity : entity[(colon + 1)..];
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (string.Equals(item.RecordingId, id, StringComparison.OrdinalIgnoreCase)) return true;
        return allowReservationFallback && (string.IsNullOrWhiteSpace(kind)
            || string.Equals(kind, "reservation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "recording", StringComparison.OrdinalIgnoreCase))
            && string.Equals(item.ReservationId, id, StringComparison.OrdinalIgnoreCase);
    }

    private static TvAirRecordingHistoryDto? FindFinalizedEventRecording(
        IEnumerable<TvAirRecordingHistoryDto> history, string entity, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(entity)) return null;
        var candidates = history.Where(item => FinalizedRecordingMatchesEntity(item, entity))
            .OrderBy(item => Math.Abs(((item.ActualEnd ?? item.End) - occurredAt).TotalSeconds))
            .ToArray();
        if (candidates.Length == 0) return null;
        // 録画実績・復旧 — The matcher first checks the exact Host recording ID. Reservation-ID fallback is limited to
        // a finalized history row near the notification time; a file-identity notification does not
        // automatically become a history identity.
        var suffix = entity.Contains(':') ? entity[(entity.IndexOf(':') + 1)..] : entity;
        return candidates.FirstOrDefault(item => string.Equals(item.RecordingId, suffix, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(item => Math.Abs(((item.ActualEnd ?? item.End) - occurredAt).TotalMinutes) <= 30);
    }

    // 録画実績・累計 — 同じHost確定済み録画は通知経由でも履歴経由でも一度だけ加算する。
    // 長期経過・再起動・再表示に耐えるよう、全計上済み識別子を累計と同じ保存値に保持する。
    private static string UsageRecordingLedgerKey(string recordingIdentity)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recordingIdentity)));

    private static void ReconcileFinalizedRecordingUsage(
        ITvAirPluginRuntimeContext context, IEnumerable<TvAirRecordingHistoryDto> history, string trigger)
    {
        EnsureUsageTotalsInitialized(context);
        var newCount = 0;
        AIrhythmUsageTotals totals;
        lock (Gate)
        {
            if (!_usageTotalsInitialized || !ReferenceEquals(_runtimeContext, context)) return;
            foreach (var item in history)
            {
                if (!item.ResultFinalized || (item.ActualEnd ?? item.End) < _usageRecordingAccountingStart)
                    continue;
                var recordingIdentity = UsageRecordingIdentity(item);
                if (string.IsNullOrWhiteSpace(recordingIdentity)) continue;
                if (!UsageCountedRecordingKeys.Add(UsageRecordingLedgerKey(recordingIdentity))) continue;
                newCount++;
            }
            if (newCount == 0) return;
            totals = _usageTotals with { RecordingTotal = _usageTotals.RecordingTotal + newCount };
            _usageTotals = totals;
            _usageRecordingLedgerRevision++;
        }
        PersistUsageCounterState(context);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"usage total increment kind=recording newCount={newCount} total={totals.RecordingTotal} trigger={trigger} provenance=confirmed_host_recording ledger=persisted");
#endif
    }

    private static void UpdateUsageTotalsFromRuntimeEvent(ITvAirPluginRuntimeContext context, string eventType, PluginEventEnvelope envelope, TvAirReservationDto? resolvedAddedReservation)
    {
        EnsureUsageTotalsInitialized(context);
        bool counterReady;
        lock (Gate) counterReady = _usageTotalsInitialized;
        if (!counterReady) return;
        if (string.Equals(eventType, "ReservationAdded", StringComparison.OrdinalIgnoreCase))
        {
            var matchedReservation = resolvedAddedReservation;
            var reservationIdentity = matchedReservation?.ReservationId?.Trim() ?? string.Empty;
            if (reservationIdentity.Length == 0)
                return;

            AIrhythmUsageTotals next;
            lock (Gate)
            {
                if (!RememberUsageIdentity(UsageRecentReservationIds, UsageRecentReservationOrder, reservationIdentity))
                    return;
                next = _usageTotals with { ReservationTotal = _usageTotals.ReservationTotal + 1 };
                _usageTotals = next;
            }
            PersistUsageCounterState(context);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"usage total increment kind=reservation entity={reservationIdentity} total={next.ReservationTotal} trigger=ReservationAdded");
#endif
            return;
        }

        if (!string.Equals(eventType, "RecordingResultFinalized", StringComparison.OrdinalIgnoreCase))
            return;

        var recordingEntity = envelope.EntityId?.Trim() ?? string.Empty;
        var matched = envelope.Payload as TvAirRecordingHistoryDto;
        IReadOnlyList<TvAirRecordingHistoryDto> recent;
        try
        {
            recent = context.Recordings.ListHistory(new TvAirRecordingHistoryQueryDto
            {
                IncludeSystemEntries = false,
                Limit = 1000
            });
        }
        catch { recent = Array.Empty<TvAirRecordingHistoryDto>(); }
        if (matched is null || !matched.ResultFinalized)
            matched = FindFinalizedEventRecording(recent, recordingEntity, envelope.OccurredAt);

        // Hostのファイル識別子と録画履歴IDが一致しなくても、履歴内の確定録画を直接計上する。
        // 履歴投影が後着の場合は次の通常スナップショットで同一経路を再実行する。
        ReconcileFinalizedRecordingUsage(context, recent, "RecordingResultFinalized_history");
        if (matched is null)
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog("recording finalization history projection pending action=retry_on_snapshot");
#endif
            return;
        }

        if (IsUsefulHistory(matched))
        {
            TvAirReservationDto? origin = null;
            if (!string.IsNullOrWhiteSpace(matched.ReservationId))
            {
                try
                {
                    var candidate = context.Reservations.Get(matched.ReservationId);
                    if (candidate is not null && RecordingBelongsToReservationOccurrence(matched, candidate))
                        origin = candidate;
                }
                catch { /* The finalized recording is still saved, but its unverified origin grants no preference. */ }
            }
            var verifiedOrigin = origin is null ? Array.Empty<TvAirReservationDto>() : new[] { origin };
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var mergedFacts = MergeUsefulRecordingFacts(context, new[] { matched }, verifiedOrigin);
            if (mergedFacts > 0)
                WriteDeveloperLog($"recording fact appended trigger=RecordingResultFinalized identity={UsageRecordingIdentity(matched)} totalFacts={GetRecordingFactCount()}");
#else
            _ = MergeUsefulRecordingFacts(context, new[] { matched }, verifiedOrigin);
#endif
        }
        ReconcileFinalizedRecordingUsage(context, new[] { matched }, "RecordingResultFinalized_payload");
    }

    public static AIrhythmSaveResult CancelReservation(IReadOnlyDictionary<string, string> payload)
    {
        if (!payload.TryGetValue("reservationId", out var reservationId) || string.IsNullOrWhiteSpace(reservationId))
            return new AIrhythmSaveResult(false, "予約を確認できませんでした");

        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return new AIrhythmSaveResult(false, "予約を取り消せませんでした");

        try
        {
            var current = context.Reservations.Get(reservationId);
            if (current is null)
                return new AIrhythmSaveResult(true, "取消対象の予約はありません");
            if (ContainsAny(current.Status, "recording", "録画中"))
                return new AIrhythmSaveResult(false, "録画中の番組はここから取り消せません");

            var result = context.Reservations.Delete(new TvAirReservationDeleteDto
            {
                ReservationId = reservationId,
                Force = false
            });
            if (!result.Success)
                return new AIrhythmSaveResult(false, string.IsNullOrWhiteSpace(result.Message) ? "予約を取り消せませんでした" : result.Message);

            Invalidate("ReservationRemoved");
            return new AIrhythmSaveResult(true, "予約を取り消しました");
        }
        catch (Exception ex)
        {
            ReportFailure(context, "cancelReservation", ex);
            return new AIrhythmSaveResult(false, "予約を取り消せませんでした");
        }
    }

    public static AIrhythmSaveResult SetReservationEnabled(IReadOnlyDictionary<string, string> payload)
    {
        if (!payload.TryGetValue("reservationId", out var reservationId) || string.IsNullOrWhiteSpace(reservationId)
            || !payload.TryGetValue("enabled", out var enabledText) || !bool.TryParse(enabledText, out var enabled))
            return new AIrhythmSaveResult(false, "予約を確認できませんでした");

        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return new AIrhythmSaveResult(false, enabled ? "予約を有効にできませんでした" : "予約を無効にできませんでした");

        try
        {
            var current = context.Reservations.Get(reservationId);
            if (current is null)
                return new AIrhythmSaveResult(false, "予約を確認できませんでした");
            if (ContainsAny(current.Status, "recording", "録画中"))
                return new AIrhythmSaveResult(false, "録画中の番組はここから変更できません");
            if (current.IsEnabled == enabled)
                return new AIrhythmSaveResult(true, enabled ? "すでに有効です" : "すでに無効です");

            var result = context.Reservations.Update(new TvAirReservationUpdateDto
            {
                ReservationId = reservationId,
                Enabled = enabled
            });
            if (!result.Success)
                return new AIrhythmSaveResult(false, string.IsNullOrWhiteSpace(result.Message)
                    ? (enabled ? "予約を有効にできませんでした" : "予約を無効にできませんでした")
                    : result.Message);

            Invalidate(enabled ? "ReservationEnabled" : "ReservationDisabled");
            return new AIrhythmSaveResult(true, enabled ? "予約を有効にしました" : "予約を無効にしました");
        }
        catch (Exception ex)
        {
            ReportFailure(context, "setReservationEnabled", ex);
            return new AIrhythmSaveResult(false, enabled ? "予約を有効にできませんでした" : "予約を無効にできませんでした");
        }
    }

    public static AIrhythmSaveResult ReserveProgram(IReadOnlyDictionary<string, string> payload)
    {
        if (!TryReadIdentity(payload, out var networkId, out var transportStreamId, out var serviceId, out var eventNumber))
            return new AIrhythmSaveResult(false, "番組を確認できませんでした");

        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return new AIrhythmSaveResult(false, "予約できませんでした");

        try
        {
            payload.TryGetValue("safeEventInteractionId", out var interactionId);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            LogReserveRequest(interactionId, networkId, transportStreamId, serviceId, eventNumber);
#endif
            var preChoiceCalibrationStaged = AIrhythmRecommendationEngine.TryStagePreChoiceCalibrationCandidate(networkId, transportStreamId, serviceId, eventNumber);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"EVIDENCE_CALIBRATION_PRECHOICE result={(preChoiceCalibrationStaged ? "STAGED" : "SKIPPED")} event={networkId}:{transportStreamId}:{serviceId}:{eventNumber} reason={(preChoiceCalibrationStaged ? "reservation_request_before_invalidate" : "no_scored_candidate_before_request")} targetLeakagePrevented=True scoreMutation=False");
#endif
            Invalidate("ReservationRequested");
            var snapshot = Capture();
            var program = snapshot.Events.FirstOrDefault(x =>
                x.NetworkId == networkId &&
                x.TransportStreamId == transportStreamId &&
                x.ServiceId == serviceId &&
                x.EventNumber == eventNumber);
            if (program is null || program.End <= DateTimeOffset.Now)
                return new AIrhythmSaveResult(false, "この番組は予約できません");

            var result = context.Reservations.Add(new TvAirReservationCreateDto
            {
                NetworkId = program.NetworkId,
                TransportStreamId = program.TransportStreamId,
                ServiceId = program.ServiceId,
                EventId = program.EventNumber,
                ProgramTitle = program.Title,
                ServiceName = program.ServiceName,
                Start = program.Start,
                End = program.End,
                PreMarginMinutes = 0,
                PostMarginMinutes = 0,
                Intent = TvAirReservationIntent.InteractiveProgramEvent,
                ChannelArgument = null,
                AllowChain = false,
                ChainPreviousReservationId = null
            });
            if (!result.Success)
            {
                var failureMessage = string.IsNullOrWhiteSpace(result.Message) ? "予約できませんでした" : result.Message;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                LogReserveResult(interactionId, networkId, transportStreamId, serviceId, eventNumber, false, failureMessage);
#endif
                return new AIrhythmSaveResult(false, failureMessage);
            }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            LogReserveResult(interactionId, networkId, transportStreamId, serviceId, eventNumber, true, string.Empty);
#endif
            Invalidate("ReservationAdded");
            return new AIrhythmSaveResult(true, "予約しました");
        }
        catch (Exception ex)
        {
            ReportFailure(context, "reserveProgram", ex);
            return new AIrhythmSaveResult(false, "予約できませんでした");
        }
    }



    private static bool TryReadIdentity(IReadOnlyDictionary<string, string> payload, out int networkId, out int transportStreamId, out int serviceId, out int eventNumber)
    {
        networkId = transportStreamId = serviceId = eventNumber = 0;
        return payload.TryGetValue("networkId", out var nid) && int.TryParse(nid, NumberStyles.Integer, CultureInfo.InvariantCulture, out networkId) &&
               payload.TryGetValue("transportStreamId", out var tsid) && int.TryParse(tsid, NumberStyles.Integer, CultureInfo.InvariantCulture, out transportStreamId) &&
               payload.TryGetValue("serviceId", out var sid) && int.TryParse(sid, NumberStyles.Integer, CultureInfo.InvariantCulture, out serviceId) &&
               payload.TryGetValue("eventId", out var eid) && int.TryParse(eid, NumberStyles.Integer, CultureInfo.InvariantCulture, out eventNumber);
    }

    private static int GetRecordingFactCount()
    {
        lock (Gate) return RecordingFacts.Count;
    }

    public static AIrhythmRuntimeSnapshot Capture()
    {
        // 保存・読込 — Web画面とプラグインイベントが同時に再描画を要求しても、 同一の複数Source Snapshotを一度だけ取得する。
        lock (CaptureGate)
        {
            ITvAirPluginRuntimeContext? context;
            long captureGeneration;
            lock (Gate)
            {
                context = _runtimeContext;
                if (_cachedSnapshot is not null)
                    return _cachedSnapshot;
                captureGeneration = _cacheGeneration;
            }
            if (context is null)
                return Empty("番組情報を読み込めませんでした");

            var now = DateTimeOffset.Now;
            var errors = new List<string>();
            IReadOnlyList<TvAirProgramEventDto> events = Array.Empty<TvAirProgramEventDto>();
            IReadOnlyList<TvAirReservationDto> reservations = Array.Empty<TvAirReservationDto>();
            IReadOnlyList<TvAirReservationDto> reservationRecords = Array.Empty<TvAirReservationDto>();
            IReadOnlyList<TvAirKeywordRuleDto> keywordRules = Array.Empty<TvAirKeywordRuleDto>();
            IReadOnlyList<TvAirRecordingHistoryDto> history = Array.Empty<TvAirRecordingHistoryDto>();
            IReadOnlyList<TvAirRecordingHistoryDto> recoveryHistory = Array.Empty<TvAirRecordingHistoryDto>();
            IReadOnlyList<TvAirRecordingSessionDto> active = Array.Empty<TvAirRecordingSessionDto>();
            IReadOnlyList<TvAirServiceDto> channels = Array.Empty<TvAirServiceDto>();
            IReadOnlyList<TvAirTunerStatusDto> tuners = Array.Empty<TvAirTunerStatusDto>();
            var programRaw = 0;
            var reservationRaw = 0;
            var historyRaw = 0;
            var channelRaw = 0;

            var snapshotResult = ReadRuntimeSnapshot(context);
            AIrhythmRuntimeSnapshot? lastSuccessfulSnapshot = null;
            if (!snapshotResult.Success)
            {
                ReportFailure(context, "dataSnapshot", snapshotResult.Error ?? new InvalidOperationException("Snapshot could not be read."));
                lock (Gate)
                {
                    if (ReferenceEquals(_runtimeContext, context))
                        lastSuccessfulSnapshot = _lastSuccessfulSnapshot;
                }

                if (lastSuccessfulSnapshot is null)
                {
                    errors.Add("番組情報");
                    errors.Add("予約");
                    errors.Add("録画実績");
                    errors.Add("チャンネル");
                    errors.Add("チューナー状態");
                }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                else
                {
                    WriteDeveloperLog($"snapshot fallback result=USED reason=runtime_capture_conflict previousProgram={lastSuccessfulSnapshot.Events.Count} previousReservations={lastSuccessfulSnapshot.Reservations.Count} previousHistory={lastSuccessfulSnapshot.History.Count} cachePolicy=return_only_no_generation_cache retryNextCapture=True");
                }
#endif
            }
            else
            {
                var data = snapshotResult.Data!;
                programRaw = data.ProgramEvents.Count;
                reservationRaw = data.Reservations.Count;
                channelRaw = data.Channels.Count;

                var hostRecordingHistory = ReadCanonicalRecordingHistory(context, data.RecordingHistory, out var recordingHistorySource);
                reservationRecords = data.Reservations
                    .Where(x => x.NetworkId > 0 && x.TransportStreamId > 0 && x.ServiceId > 0 && x.EventNumber > 0)
                    .OrderBy(x => x.Start)
                    .ToArray();
                var recordingFactHistory = GetRecordingFactHistory(context, hostRecordingHistory, data.Reservations, out var seededRecordingFacts);
                historyRaw = recordingFactHistory.Count;

                events = data.ProgramEvents
                    .Where(x => !string.IsNullOrWhiteSpace(x.Title) && x.End > now && x.Start < now.AddDays(14))
                    .OrderBy(x => x.Start)
                    .ToArray();
                reservations = reservationRecords
                    .Where(IsUsefulReservation)
                    .ToArray();
                // 録画実績・復旧 — A completed recording and its quality observations must remain available after the Host
                // history window moves on. Failed Host rows participate in recovery facts, not positive preference
                // learning. This projection alone does not authorize episode-exact re-recording.
                var recoveryFacts = recordingFactHistory
                    .Where(x => x.ResultFinalized && x.QualityDataAvailable
                        && ((x.DropCount ?? 0) > 0 || (x.ErrorCount ?? 0) > 0 || (x.ScrambleCount ?? 0) > 0));
                recoveryHistory = recoveryFacts
                    .Concat(hostRecordingHistory.Where(x => x.ResultFinalized && IsRecordingFailureForRecovery(x)))
                    .Where(x => (x.ActualStart ?? x.Start) <= now)
                    .Where(x => !string.IsNullOrWhiteSpace(x.ProgramTitle))
                    .GroupBy(UsageRecordingIdentity, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(x => x.QualityDataAvailable).First())
                    .OrderByDescending(x => x.ActualStart ?? x.Start)
                    .ToArray();
                MergeRecoveryAttempts(context, hostRecordingHistory, recordingFactHistory);
                AuditRecoveryEpgCandidates(context, events);
                history = recordingFactHistory;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                ReportRecordingQualityPersistence(recordingFactHistory, hostRecordingHistory, recoveryHistory);
#endif
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"canonical recording facts source=AIrhythm.PersistentRecordingFacts facts={history.Count} seeded={seededRecordingFacts} hostRaw={hostRecordingHistory.Count} hostSource={recordingHistorySource} snapshotRaw={data.RecordingHistory.Count} retention=append_success_terminal_no_host_deletion consumers=score,localEvidence,dashboard,selection,discovery,search,interest");
                #endif
                active = data.ActiveRecordings;
                channels = data.Channels
                    .Where(x => x.IsEnabled)
                    .OrderBy(x => x.DisplayOrder)
                    .ThenBy(x => x.ServiceName, StringComparer.Ordinal)
                    .ToArray();
                tuners = data.Tuners;
            }

            try
            {
                keywordRules = context.Rules.ListKeywordRules(new TvAirRuleQueryDto { Enabled = true, Limit = 10000 })
                    .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Pattern))
                    .OrderBy(x => x.SortOrder)
                    .ThenBy(x => x.RuleId)
                    .ToArray();
            }
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
            #else
            catch (Exception)
            #endif
            {
                // 録画実績・復旧 — Discovery can operate without rule metadata. Treat this as a degraded knownness signal
                // instead of a core snapshot failure; reservations/history remain authoritative.
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"DISCOVERY_KEYWORD_RULE_READ result=FAILED type={ex.GetType().Name} fallback=reservation_and_history_only");
                #endif
            }

            var advanced = new AIrhythmAdvancedSnapshot(active);

            var settings = ReadSettings(context, out var revision);
            if (!snapshotResult.Success && lastSuccessfulSnapshot is not null)
            {
                // 保存・読込 — Never cache the fallback as the current generation: the very next capture must attempt a fresh
                // Host snapshot again. Settings are refreshed independently so a transient data race cannot roll back a
                // user setting change in the rendered page.
                var fallback = lastSuccessfulSnapshot with
                {
                    Settings = settings,
                    SettingsRevision = revision
                };
                ReportSnapshot(context, fallback);
                return fallback;
            }

            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var diagnostics = new AIrhythmRuntimeDiagnostics(
                programRaw, events.Count, reservationRaw, reservations.Count, historyRaw, history.Count, channelRaw, channels.Count, errors.ToArray());
            #endif

            var coreFailureCount = errors.Count(x => x is "番組情報" or "予約" or "録画実績");
            var snapshot = coreFailureCount == 3
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                ? Empty("番組情報を読み込めませんでした", settings, revision, diagnostics)
#else
                ? Empty("番組情報を読み込めませんでした", settings, revision)
#endif
                : new AIrhythmRuntimeSnapshot(
                    true,
                    errors.Count == 0 ? string.Empty : $"{string.Join("・", errors.Distinct())}を読み込めませんでした",
                    events, reservations, reservationRecords, keywordRules, history, recoveryHistory, channels, tuners, advanced, settings
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                    , revision, diagnostics);
#else
                    , revision);
#endif

            ReportSnapshot(context, snapshot);
            lock (Gate)
            {
                if (ReferenceEquals(_runtimeContext, context) && snapshotResult.Success)
                    _lastSuccessfulSnapshot = snapshot;
                if (ReferenceEquals(_runtimeContext, context) && _cacheGeneration == captureGeneration)
                    _cachedSnapshot = snapshot;
            }
            return snapshot;
        }
    }

    private static IReadOnlyList<TvAirRecordingHistoryDto> ReadCanonicalRecordingHistory(
        ITvAirPluginRuntimeContext context,
        IReadOnlyList<TvAirRecordingHistoryDto> snapshotFallback,
        out string source)
    {
        try
        {
            var fullHistory = context.Recordings.ListHistory(new TvAirRecordingHistoryQueryDto
            {
                IncludeSystemEntries = false,
                Limit = 10000
            });

            if (fullHistory.Count > 0 || snapshotFallback.Count == 0)
            {
                source = "Recordings.ListHistory";
                return fullHistory;
            }
        }
        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
        #else
        catch (Exception)
        #endif
        {
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"canonical recording history primary read failed type={ex.GetType().Name} fallback=snapshot");
            #endif
        }

        source = "DataSnapshot.recording-history(fallback)";
        return snapshotFallback;
    }

    private static SnapshotReadResult ReadRuntimeSnapshot(ITvAirPluginRuntimeContext context)
    {
        const int maxAttempts = 3;
        SnapshotReadResult? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            last = ReadRuntimeSnapshotOnce(context);
            if (last.Success)
            {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                if (attempt > 1)
                    WriteDeveloperLog($"snapshot retry result=RECOVERED attempts={attempt} policy=full_snapshot_retry_on_transient_conflict");
#endif
                return last;
            }

            if (!IsTransientSnapshotConflict(last.Error) || attempt == maxAttempts)
                break;

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"snapshot retry result=RETRY attempt={attempt}/{maxAttempts} reason=transient_host_revision_change policy=full_snapshot_reopen");
#endif
            Thread.Sleep(attempt * 20);
        }

        return last ?? SnapshotReadResult.Fail(new InvalidOperationException("Snapshot could not be read."));
    }

    private static bool IsTransientSnapshotConflict(Exception? error)
    {
        if (error is null) return false;
        if (error is InvalidOperationException &&
            error.Message.Contains("Data changed while the snapshot was being captured", StringComparison.OrdinalIgnoreCase))
            return true;
        return error.Message.Contains("revision", StringComparison.OrdinalIgnoreCase) ||
               error.Message.Contains("changed while", StringComparison.OrdinalIgnoreCase);
    }

    private static SnapshotReadResult ReadRuntimeSnapshotOnce(ITvAirPluginRuntimeContext context)
    {
        var request = new TvAirSnapshotOpenRequest("multi")
        {
            SourceIds = new[]
            {
                "program-guide",
                "reservations",
                "recording-history",
                "recording-active",
                "channels",
                "tuners"
            }
        };

        TvAirOperationResult<TvAirSnapshotDescriptor>? opened = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            opened = context.Data.OpenSnapshot(request);
            if (opened.Succeeded && opened.Value?.SnapshotId is not null)
                break;
            if (opened.Error?.Code != TvAirErrorCode.RevisionConflict)
                break;
        }

        if (opened is null || !opened.Succeeded || string.IsNullOrWhiteSpace(opened.Value?.SnapshotId))
            return SnapshotReadResult.Fail(new InvalidOperationException(opened?.Error?.Message ?? "Snapshot could not be opened."));

        var snapshotId = opened.Value.SnapshotId!;
        try
        {
            var grouped = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
            string? cursor = null;
            do
            {
                var pageResult = context.Data.ReadSnapshot(new TvAirSnapshotReadRequest(snapshotId, 1000, cursor));
                if (!pageResult.Succeeded || pageResult.Value is null)
                    return SnapshotReadResult.Fail(new InvalidOperationException(pageResult.Error?.Message ?? "Snapshot could not be read."));

                foreach (var raw in pageResult.Value.Items)
                {
                    if (!TryReadSnapshotItem(raw, out var sourceId, out var value))
                        continue;
                    if (!grouped.TryGetValue(sourceId, out var list))
                    {
                        list = new List<object>();
                        grouped[sourceId] = list;
                    }
                    list.Add(value);
                }
                cursor = pageResult.Value.NextCursor;
            }
            while (!string.IsNullOrWhiteSpace(cursor));

            return SnapshotReadResult.Ok(new RuntimeSnapshotData(
                ConvertItems<TvAirProgramEventDto>(grouped, "program-guide"),
                ConvertItems<TvAirReservationDto>(grouped, "reservations"),
                ConvertItems<TvAirRecordingHistoryDto>(grouped, "recording-history"),
                ConvertItems<TvAirRecordingSessionDto>(grouped, "recording-active"),
                ConvertItems<TvAirServiceDto>(grouped, "channels"),
                ConvertItems<TvAirTunerStatusDto>(grouped, "tuners")));
        }
        catch (Exception ex)
        {
            return SnapshotReadResult.Fail(ex);
        }
        finally
        {
            try { context.Data.CloseSnapshot(snapshotId); } catch { }
        }
    }

    private static bool TryReadSnapshotItem(object raw, out string sourceId, out object value)
    {
        if (raw is TvAirSnapshotItem item)
        {
            sourceId = item.SourceId;
            value = item.Value;
            return true;
        }

        if (raw is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("sourceId", out var sourceProperty) &&
                element.TryGetProperty("value", out var valueProperty))
            {
                sourceId = sourceProperty.GetString() ?? string.Empty;
                value = valueProperty.Clone();
                return !string.IsNullOrWhiteSpace(sourceId);
            }
        }

        sourceId = string.Empty;
        value = raw;
        return false;
    }

    private static IReadOnlyList<T> ConvertItems<T>(IReadOnlyDictionary<string, List<object>> grouped, string sourceId)
    {
        if (!grouped.TryGetValue(sourceId, out var source))
            return Array.Empty<T>();
        var result = new List<T>(source.Count);
        foreach (var item in source)
        {
            if (item is T typed)
            {
                result.Add(typed);
                continue;
            }
            try
            {
                if (item is JsonElement element)
                {
                    var value = element.Deserialize<T>(JsonOptions);
                    if (value is not null) result.Add(value);
                }
                else
                {
                    var value = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(item, JsonOptions), JsonOptions);
                    if (value is not null) result.Add(value);
                }
            }
            catch
            {
                // 保存・読込 — 不正な1件だけを除外し、同じSnapshot内の残りを利用する。
            }
        }
        return result;
    }

    private sealed record RuntimeSnapshotData(
        IReadOnlyList<TvAirProgramEventDto> ProgramEvents,
        IReadOnlyList<TvAirReservationDto> Reservations,
        IReadOnlyList<TvAirRecordingHistoryDto> RecordingHistory,
        IReadOnlyList<TvAirRecordingSessionDto> ActiveRecordings,
        IReadOnlyList<TvAirServiceDto> Channels,
        IReadOnlyList<TvAirTunerStatusDto> Tuners);

    private sealed record SnapshotReadResult(bool Success, RuntimeSnapshotData? Data, Exception? Error)
    {
        public static SnapshotReadResult Ok(RuntimeSnapshotData data) => new(true, data, null);
        public static SnapshotReadResult Fail(Exception error) => new(false, null, error);
    }

    public static IReadOnlyList<string> GetRecentRhythmSearches()
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return Array.Empty<string>();
        try
        {
            var result = context.Storage.Get("rhythmSearch", "recent");
            if (!result.Succeeded || result.Value is null)
                return Array.Empty<string>();
            var json = result.Value.Value?.ToString();
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<string>();
            var values = JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? Array.Empty<string>();
            return values
                .Select(x => NormalizeStoredRhythmSearch(x))
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static void RememberRhythmSearch(string query)
    {
        var normalized = NormalizeStoredRhythmSearch(query);
        if (normalized.Length == 0)
            return;
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return;
        try
        {
            var current = GetRecentRhythmSearches();
            var next = new[] { normalized }
                .Concat(current.Where(x => !string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
                .Take(8)
                .ToArray();
            if (current.SequenceEqual(next, StringComparer.Ordinal))
                return;
            var json = JsonSerializer.Serialize(next, JsonOptions);
            context.Storage.Set("rhythmSearch", "recent", json, expectedRevision: null);
        }
        catch
        {
            // 保存・読込 — 検索履歴の保存失敗は検索結果の表示を妨げない。
        }
    }

    private static AIrhythmServiceIdentity ServiceIdentityOf(AIrhythmInterestSignal value)
        => new(value.NetworkId, value.TransportStreamId, value.ServiceId);

    private static AIrhythmServiceIdentity ServiceIdentityOf(TvAirProgramEventDto value)
        => new(value.NetworkId, value.TransportStreamId, value.ServiceId);

    private static AIrhythmInterestSignal UpgradeLegacyInterestSignal(ITvAirPluginRuntimeContext context, AIrhythmInterestSignal value)
    {
        if (ServiceIdentityOf(value).IsValid)
        {
            try
            {
                var current = context.Channels.ListServices(new TvAirServiceQueryDto { Enabled = true })
                    .FirstOrDefault(x => x.NetworkId == value.NetworkId
                        && x.TransportStreamId == value.TransportStreamId
                        && x.ServiceId == value.ServiceId)?.ServiceName;
                return string.IsNullOrWhiteSpace(current) || string.Equals(current, value.ServiceName, StringComparison.Ordinal)
                    ? value
                    : value with { ServiceName = current };
            }
            catch
            {
                return value;
            }
        }

        if (string.IsNullOrWhiteSpace(value.ServiceName)) return value;
        try
        {
            var matches = context.Channels.ListServices(new TvAirServiceQueryDto { Enabled = true })
                .Where(x => string.Equals(x.ServiceName, value.ServiceName, StringComparison.OrdinalIgnoreCase))
                .Select(x => new AIrhythmServiceIdentity(x.NetworkId, x.TransportStreamId, x.ServiceId))
                .Where(x => x.IsValid)
                .Distinct()
                .Take(2)
                .ToArray();
            if (matches.Length != 1) return value;
            var match = matches[0];
            return value with
            {
                NetworkId = match.NetworkId,
                TransportStreamId = match.TransportStreamId,
                ServiceId = match.ServiceId
            };
        }
        catch
        {
            return value;
        }
    }

    private static IReadOnlyList<AIrhythmInterestSignal> LoadInterestSignals(bool requireResolvedIdentity)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null) return Array.Empty<AIrhythmInterestSignal>();
        try
        {
            var result = context.Storage.Get("rhythmSearch", "interestSignals");
            if (!result.Succeeded || result.Value is null) return Array.Empty<AIrhythmInterestSignal>();
            var json = result.Value.Value?.ToString();
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<AIrhythmInterestSignal>();
            var values = JsonSerializer.Deserialize<AIrhythmInterestSignal[]>(json, JsonOptions) ?? Array.Empty<AIrhythmInterestSignal>();
            var cutoff = DateTimeOffset.Now.AddYears(-2);
            var normalized = values
                .Where(x => x is not null)
                .Where(x => !string.IsNullOrWhiteSpace(x.EventId) && x.EventId.Length <= 512)
                .Where(x => !string.IsNullOrWhiteSpace(x.SeriesKey) && x.SeriesKey.Length <= 160)
                .Where(x => (x.Genre?.Length ?? 0) <= 80)
                .Where(x => (x.ServiceName?.Length ?? 0) <= 120)
                .Where(x => x.SelectedAt >= cutoff && x.SelectedAt <= DateTimeOffset.Now.AddMinutes(5))
                .Select(x => UpgradeLegacyInterestSignal(context, x))
                .ToArray();

            if (requireResolvedIdentity)
            {
                return normalized
                    .Where(x => ServiceIdentityOf(x).IsValid)
                    .GroupBy(x => $"{x.SeriesKey}|{ServiceIdentityOf(x)}", StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.OrderByDescending(y => y.SelectedAt).First())
                    .OrderByDescending(x => x.SelectedAt)
                    .Take(24)
                    .ToArray();
            }

            // 保存・読込 — 旧形式でidentityを一意に解決できない項目は、推測せず保存上だけ保持する。 推薦・集計・表示の正本には使わない。
            return normalized
                .GroupBy(x => ServiceIdentityOf(x).IsValid
                    ? $"resolved:{x.SeriesKey}|{ServiceIdentityOf(x)}"
                    : $"legacy:{x.EventId}", StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(y => y.SelectedAt).First())
                .OrderByDescending(x => x.SelectedAt)
                .Take(24)
                .ToArray();
        }
        catch
        {
            return Array.Empty<AIrhythmInterestSignal>();
        }
    }

    // 保存・読込 — Contributor fact IDs are `observation:<opaque ID>|<role>|<entity>`; the opaque observation
    // portion may itself contain '|'. Never split on the first separator. Group candidate suffixes by their
    // LAST segment to avoid repeatedly scanning all contributors for every persisted observation. Retain
    // dictionary insertion order, and verify the FULL key boundary just as the original EndsWith check did.
    private sealed record ContributorFactSuffixCandidate(string Key, string FullSuffix);

    private static Dictionary<string, List<ContributorFactSuffixCandidate>> BuildContributorFactSuffixIndex(
        IEnumerable<string> contributorKeys)
    {
        var index = new Dictionary<string, List<ContributorFactSuffixCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in contributorKeys)
        {
            var separator = key.LastIndexOf('|');
            var finalSegment = key[(separator + 1)..];
            if (!index.TryGetValue(finalSegment, out var candidates))
                index[finalSegment] = candidates = new List<ContributorFactSuffixCandidate>(1);
            candidates.Add(new ContributorFactSuffixCandidate(key, "|" + key));
        }
        return index;
    }

    private static string? FindContributorFactSuffix(
        string factId,
        IReadOnlyDictionary<string, List<ContributorFactSuffixCandidate>> index)
    {
        var separator = factId.LastIndexOf('|');
        if (separator < 0 || !index.TryGetValue(factId[(separator + 1)..], out var candidates))
            return null;
        foreach (var candidate in candidates)
        {
            if (factId.EndsWith(candidate.FullSuffix, StringComparison.OrdinalIgnoreCase))
                return candidate.Key;
        }
        return null;
    }

    public static AIrhythmLearnedUserModelState ObserveLearnedUserModel(
        IEnumerable<AIrhythmUserModelObservation> source,
        IReadOnlyDictionary<string, string>? excludedReservationOrigins = null)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var userModelAllocStart = GC.GetAllocatedBytesForCurrentThread();
#endif

        static Dictionary<string, double> CopyMap(Dictionary<string, double>? sourceMap)
            => sourceMap is null
                ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(sourceMap, StringComparer.OrdinalIgnoreCase);

        var state = new AIrhythmLearnedUserModelState(
            LearnedUserModelSchemaVersion,
            0,
            DateTimeOffset.MinValue,
            Array.Empty<string>(),
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));
        var loadedObservations = 0L;
        // 保存・読込 — Fail closed: unreadable existing learning data must never be overwritten by an apparently
        // fresh in-memory model. The storage revision is the write CAS.
        var mayPersistUserModel = context is not null;
        long? observedUserModelRevision = null;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var loadedContributorMappings = 0;
        var loadedContributorFactIds = 0;
        var storedContributorSaveStatus = "not_required";
#endif

        if (context is not null)
        {
            try
            {
                var stored = context.Storage.Get(LearnedUserModelStorageNamespace, LearnedUserModelStorageKey);
                // 処理条件 — EntityNotFound is a genuine empty installation. Every other read failure (including permission,
                // disconnect, malformed data and a newer schema) must preserve the stored original without a write.
                if (!stored.Succeeded)
                    mayPersistUserModel = stored.Error?.Code == TvAirErrorCode.EntityNotFound;
                else if (stored.Value is null)
                    mayPersistUserModel = false;
                else
                {
                    observedUserModelRevision = stored.Value.Revision;
                    var json = stored.Value.Value?.ToString();
                    if (string.IsNullOrWhiteSpace(json))
                        mayPersistUserModel = false;
                    else
                    {
                        var loaded = DeserializeLearnedUserModel(json, stored.Value.Revision);
                        if (loaded is not null && loaded.SchemaVersion == LearnedUserModelSchemaVersion)
                        {
                        var basePreferenceCompatible = loaded.BasePreferenceLogicVersion == BasePreferenceLogicVersion;
                        state = loaded with
                        {
                            ObservationCount = basePreferenceCompatible ? loaded.ObservationCount : 0,
                            SeenObservationIds = basePreferenceCompatible
                                ? loaded.SeenObservationIds ?? Array.Empty<string>()
                                : Array.Empty<string>(),
                            WorkWeights = basePreferenceCompatible
                                ? CopyMap(loaded.WorkWeights)
                                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                            GenreWeights = basePreferenceCompatible
                                ? CopyMap(loaded.GenreWeights)
                                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                            TermWeights = basePreferenceCompatible
                                ? CopyMap(loaded.TermWeights)
                                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                            ServiceWeights = basePreferenceCompatible
                                ? CopyMap(loaded.ServiceWeights)
                                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                            HourWeights = basePreferenceCompatible
                                ? CopyMap(loaded.HourWeights)
                                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                            // 推薦・学習 — Contributor extraction is independently versioned so parser corrections can rebuild only this
                            // axis without discarding established contributor evidence.
                            ContributorWeights = loaded.ContributorLogicVersion == ContributorLogicVersion
                                ? CopyMap(loaded.ContributorWeights)
                                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                            SeenContributorObservationIds = loaded.ContributorLogicVersion == ContributorLogicVersion
                                ? loaded.SeenContributorObservationIds ?? Array.Empty<string>()
                                : Array.Empty<string>(),
                            ContributorLogicVersion = ContributorLogicVersion,
                            ContributorFactWorkKeys = loaded.ContributorLogicVersion == ContributorLogicVersion
                                ? new Dictionary<string, string>(loaded.ContributorFactWorkKeys ?? new Dictionary<string, string>(), StringComparer.Ordinal)
                                : new Dictionary<string, string>(StringComparer.Ordinal),
                            BasePreferenceLogicVersion = BasePreferenceLogicVersion,
                            // 処理条件 — Retain only recorded per-observation weights; never reconstruct historical intent from current
                            // keyword rule definitions.
                            BaseEvidenceLedger = new Dictionary<string, AIrhythmBaseEvidenceLedgerEntry>(
                                loaded.BaseEvidenceLedger ?? new Dictionary<string, AIrhythmBaseEvidenceLedgerEntry>(),
                                StringComparer.Ordinal)
                        };
                        loadedObservations = state.ObservationCount;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                        loadedContributorMappings = state.ContributorFactWorkKeys?.Count ?? 0;
                        loadedContributorFactIds = state.SeenContributorObservationIds?.Length ?? 0;
#endif
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                        if (!basePreferenceCompatible)
                            WriteDeveloperLog($"USER_MODEL_BASE_MIGRATION result=REBUILD previousLogic={loaded.BasePreferenceLogicVersion} nextLogic={BasePreferenceLogicVersion} previousObservations={loaded.ObservationCount} preservedContributorLogic={loaded.ContributorLogicVersion == ContributorLogicVersion} reason=causal_reservation_provenance");
#endif
                        }
                        else
                            mayPersistUserModel = false;
                    }
                }
            }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
#else
            catch (Exception)
#endif
            {
                mayPersistUserModel = false;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"USER_MODEL_MEMORY result=LOAD_ERROR type={ex.GetType().Name} action=preserve_existing_storage");
#endif
            }
        }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var userModelAfterLoad = GC.GetAllocatedBytesForCurrentThread();
#endif
        var seen = new HashSet<string>(state.SeenObservationIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        var contributorSeen = new HashSet<string>(state.SeenContributorObservationIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        // 作品識別 — New provenance is attached only to observed identity pairs. Existing historical facts with no
        // accessible WorkKey stay unknown, never inferred from a current title.
        var contributorFactWorkKeys = new Dictionary<string, string>(state.ContributorFactWorkKeys ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var contributorProvenanceAdded = 0;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var contributorProvenanceReused = 0;
#endif
        // 保存・読込 — The load path above already normalizes all persisted maps into OrdinalIgnoreCase dictionaries.
        // They are local to this Observe call, so mutate that single canonical working set directly instead of
        // cloning all six maps a second time before applying observations.
        var baseEvidenceLedger = new Dictionary<string, AIrhythmBaseEvidenceLedgerEntry>(
            state.BaseEvidenceLedger ?? new Dictionary<string, AIrhythmBaseEvidenceLedgerEntry>(),
            StringComparer.Ordinal);
        var ledgerNewEntries = 0;
        var work = state.WorkWeights;
        var genre = state.GenreWeights;
        var terms = state.TermWeights;
        var service = state.ServiceWeights;
        var hour = state.HourWeights;
        var contributors = state.ContributorWeights
            ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var observationRows = source as IReadOnlyList<AIrhythmUserModelObservation> ?? source.ToArray();

        // 推薦・学習 — Correct only independently identified old Program/System reservation evidence.
        // The exact credited base terms/hours are held in the per-observation ledger. Contributor
        // observations from non-keyword Program/System reservations were always incidental (+0.25).
        // Do not guess a missing legacy fact or rewrite observations with a reused ID/different work.
        var excludedBaseRemoved = 0;
        var excludedContributorRemoved = 0;
        var excludedWithoutSafeProvenance = 0;
        var observationCount = Math.Max(0, state.ObservationCount);
        var excludedReservations = excludedReservationOrigins ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var excluded in excludedReservations)
        {
            var observationId = excluded.Key;
            var verifiedWork = excluded.Value;
            if (string.IsNullOrWhiteSpace(verifiedWork)) continue;
            if (seen.Contains(observationId))
            {
                if (baseEvidenceLedger.TryGetValue(observationId, out var ledger)
                    && string.Equals(ledger.WorkKey, verifiedWork, StringComparison.OrdinalIgnoreCase)
                    && (ledger.Terms <= 0 || ledger.TermKeys is not null)
                    && (ledger.Hour <= 0 || !string.IsNullOrWhiteSpace(ledger.HourKey)))
                {
                    Subtract(work, ledger.WorkKey, ledger.Work);
                    Subtract(genre, ledger.GenreKey, ledger.Genre);
                    Subtract(service, ledger.ServiceKey, ledger.Service);
                    if (ledger.TermKeys is not null)
                        foreach (var term in ledger.TermKeys) Subtract(terms, term, ledger.Terms);
                    if (!string.IsNullOrWhiteSpace(ledger.HourKey))
                        Subtract(hour, ledger.HourKey, ledger.Hour);
                    baseEvidenceLedger.Remove(observationId);
                    seen.Remove(observationId);
                    observationCount = Math.Max(0, observationCount - 1);
                    excludedBaseRemoved++;
                }
                else excludedWithoutSafeProvenance++;
            }

            var prefix = "observation:" + observationId + "|";
            foreach (var factId in contributorSeen.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                if (!contributorFactWorkKeys.TryGetValue(factId, out var recordedWork)
                    || !string.Equals(recordedWork, verifiedWork, StringComparison.OrdinalIgnoreCase))
                {
                    excludedWithoutSafeProvenance++;
                    continue;
                }
                var contributorKey = factId[prefix.Length..];
                Subtract(contributors, contributorKey, AIrhythmPreferenceOriginPolicy.IncidentalContributorWeight);
                contributorSeen.Remove(factId);
                contributorFactWorkKeys.Remove(factId);
                excludedContributorRemoved++;
            }
        }
        var excludedEvidenceChanged = excludedBaseRemoved > 0 || excludedContributorRemoved > 0;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        if (excludedReservations.Count > 0)
            WriteDeveloperLog($"USER_MODEL_PROGRAM_ORIGIN_GUARD knownExcluded={excludedReservations.Count} removedBase={excludedBaseRemoved} removedContributor={excludedContributorRemoved} unsafeLegacyRetained={excludedWithoutSafeProvenance} policy=verified_observation_only historicalUnknownNotInferred=True");
#endif

        // 作品識別 — Contributor facts are additive from the first occurrence. Metadata presence is one +1; a
        // matching positive SearchCast/keyword condition is a separate +1 under its tracked key. No minimum-work
        // gate is used here: a single fact is kept so later years can give it meaning.
        var newObservations = 0;
        var contributorBackfills = 0;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var contributorDiagnosticObservations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var contributorDiagnosticWorks = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
#endif

        static void Add(Dictionary<string, double> map, string? key, double value = 1.0d)
        {
            var normalized = (key ?? string.Empty).Trim();
            if (normalized.Length == 0) return;
            map[normalized] = map.TryGetValue(normalized, out var current) ? current + value : value;
        }

        static void Subtract(Dictionary<string, double> map, string? key, double weight)
        {
            var normalized = (key ?? string.Empty).Trim();
            if (normalized.Length == 0 || weight <= 0 || !map.TryGetValue(normalized, out var current)) return;
            var nextWeight = current - weight;
            if (nextWeight <= 0.0000001d) map.Remove(normalized);
            else map[normalized] = nextWeight;
        }

        var processedObservationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in observationRows)
        {
            if (string.IsNullOrWhiteSpace(observation.ObservationId) || !processedObservationIds.Add(observation.ObservationId))
                continue;

            var isNewBaseObservation = observation.HasBaseLearningAxis && seen.Add(observation.ObservationId);
            var contributorRows = observation.Contributors ?? Array.Empty<AIrhythmContributorObservation>();
            var uniqueContributors = new List<AIrhythmContributorObservation>(Math.Min(contributorRows.Count, 8));
            var contributorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < contributorRows.Count; i++)
            {
                var contributor = contributorRows[i];
                if (string.IsNullOrWhiteSpace(contributor.Key)
                    || contributor.Weight <= 0.0d
                    || !contributorKeys.Add(contributor.Key))
                    continue;
                uniqueContributors.Add(contributor);
            }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            foreach (var contributor in uniqueContributors)
            {
                contributorDiagnosticObservations[contributor.Key] = contributorDiagnosticObservations.TryGetValue(contributor.Key, out var currentCount)
                    ? currentCount + 1
                    : 1;
                if (!contributorDiagnosticWorks.TryGetValue(contributor.Key, out var worksForContributor))
                {
                    worksForContributor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    contributorDiagnosticWorks[contributor.Key] = worksForContributor;
                }
                if (!string.IsNullOrWhiteSpace(observation.WorkKey))
                    worksForContributor.Add(observation.WorkKey);
            }
#endif
            var newContributorRows = new List<AIrhythmContributorObservation>(uniqueContributors.Count);
            foreach (var contributor in uniqueContributors)
            {
                var contributorSeenId = $"observation:{observation.ObservationId}|{contributor.Key}";
                if (contributorSeen.Contains(contributorSeenId)
                    && !contributorFactWorkKeys.ContainsKey(contributorSeenId)
                    && !string.IsNullOrWhiteSpace(observation.WorkKey))
                {
                    contributorFactWorkKeys[contributorSeenId] = observation.WorkKey;
                    contributorProvenanceAdded++;
                }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                if (contributorSeen.Contains(contributorSeenId) && contributorFactWorkKeys.TryGetValue(contributorSeenId, out var reusedWork)
                    && string.Equals(reusedWork, observation.WorkKey, StringComparison.OrdinalIgnoreCase))
                    contributorProvenanceReused++;
#endif
                if (contributorSeen.Add(contributorSeenId))
                    newContributorRows.Add(contributor);
            }

            var isNewContributorObservation = newContributorRows.Count > 0;
            if (!isNewBaseObservation && !isNewContributorObservation)
                continue;

            if (isNewBaseObservation)
            {
                // 推薦・学習 — Retain exact, deduplicated term identities and the credited hour in the immutable fact ledger.
                // A coefficient alone cannot reconstruct a score.
                var creditedTerms = new List<string>(16);
                if (observation.LearnTerms)
                {
                    var termKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rawTerm in observation.Terms)
                    {
                        var term = (rawTerm ?? string.Empty).Trim();
                        if (term.Length == 0 || !termKeys.Add(term)) continue;
                        creditedTerms.Add(term);
                        if (creditedTerms.Count == 16) break;
                    }
                }
                var creditedHour = observation.LearnHour && observation.Hour is int h && h is >= 0 and <= 23
                    ? h.ToString(CultureInfo.InvariantCulture) : string.Empty;
                baseEvidenceLedger[observation.ObservationId] = new AIrhythmBaseEvidenceLedgerEntry(
                    observation.LearnWork ? observation.WorkEvidenceWeight : 0,
                    observation.LearnGenre ? observation.GenreEvidenceWeight : 0,
                    observation.LearnTerms ? observation.TermsEvidenceWeight : 0,
                    observation.LearnService ? observation.ServiceEvidenceWeight : 0,
                    observation.LearnHour ? observation.HourEvidenceWeight : 0,
                    observation.WorkKey ?? string.Empty,
                    observation.GenreKey ?? string.Empty,
                    observation.ServiceKey ?? string.Empty,
                    creditedTerms.ToArray(), creditedHour);
                ledgerNewEntries++;
                if (observation.LearnWork) Add(work, observation.WorkKey, observation.WorkEvidenceWeight);
                if (observation.LearnGenre) Add(genre, observation.GenreKey, observation.GenreEvidenceWeight);
                if (observation.LearnService) Add(service, observation.ServiceKey, observation.ServiceEvidenceWeight);
                if (creditedHour.Length > 0)
                    Add(hour, creditedHour, observation.HourEvidenceWeight);
                foreach (var token in creditedTerms)
                    Add(terms, token, observation.TermsEvidenceWeight);
                observationCount++;
                newObservations++;
            }

            if (isNewContributorObservation)
            {
                foreach (var contributor in newContributorRows)
                {
                    Add(contributors, contributor.Key, Math.Clamp(contributor.Weight, 0.05d, 1.0d));
                    var factId = $"observation:{observation.ObservationId}|{contributor.Key}";
                    if (!string.IsNullOrWhiteSpace(observation.WorkKey) && !contributorFactWorkKeys.ContainsKey(factId))
                    {
                        contributorFactWorkKeys[factId] = observation.WorkKey;
                        contributorProvenanceAdded++;
                    }
                }
                contributorBackfills++;
            }
        }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var userModelAfterObserve = GC.GetAllocatedBytesForCurrentThread();
#endif
        var next = new AIrhythmLearnedUserModelState(
            LearnedUserModelSchemaVersion,
            observationCount,
            newObservations > 0 || contributorBackfills > 0 || contributorProvenanceAdded > 0 || excludedEvidenceChanged ? DateTimeOffset.Now : state.UpdatedAt,
            seen.ToArray(),
            work,
            genre,
            terms,
            service,
            hour,
            contributors,
            contributorSeen.ToArray(),
            ContributorLogicVersion,
            BasePreferenceLogicVersion,
            contributorFactWorkKeys,
            baseEvidenceLedger);

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var baseLedgerReadback = "not_required";
        var baseLedgerIncomplete = baseEvidenceLedger.Values.Count(e =>
            (e.Terms > 0 && e.TermKeys is null) || (e.Hour > 0 && string.IsNullOrWhiteSpace(e.HourKey)));
#endif
        if (context is not null && mayPersistUserModel
            && (newObservations > 0 || contributorBackfills > 0 || contributorProvenanceAdded > 0 || excludedEvidenceChanged))
        {
            try
            {
                var json = JsonSerializer.Serialize(next, JsonOptions);
                var written = context.Storage.Set(LearnedUserModelStorageNamespace, LearnedUserModelStorageKey, json,
                    expectedRevision: observedUserModelRevision);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                storedContributorSaveStatus = written.Succeeded ? "succeeded" : "failed";
                if (written.Succeeded && ledgerNewEntries > 0)
                {
                    try
                    {
                        // Host連携 — A successful Set returns the committed entry, including its value and revision. Verify that
                        // response instead of immediately repeating a potentially large Get and allocating a second model-size
                        // payload. If a Host implementation does not return the value, retain an actual Get/readback fallback
                        // instead of silently weakening verification.
                        var committedJson = written.Value?.Value?.ToString();
                        if (committedJson is not null)
                            baseLedgerReadback = string.Equals(json, committedJson, StringComparison.Ordinal)
                                ? "write_response_matched" : "write_response_mismatch";
                        else
                        {
                            var readback = context.Storage.Get(LearnedUserModelStorageNamespace, LearnedUserModelStorageKey);
                            var readbackJson = readback.Succeeded ? readback.Value?.Value?.ToString() : null;
                            baseLedgerReadback = string.Equals(json, readbackJson, StringComparison.Ordinal)
                                ? "readback_matched" : "readback_mismatch";
                        }
                    }
                    catch (Exception readbackError)
                    {
                        baseLedgerReadback = "exception_" + readbackError.GetType().Name;
                    }
                }
                else if (!written.Succeeded && ledgerNewEntries > 0)
                    baseLedgerReadback = "write_failed";
#endif
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                if (!written.Succeeded)
                    WriteDeveloperLog($"USER_MODEL_MEMORY result=SAVE_ERROR newObservations={newObservations} contributorBackfills={contributorBackfills} loadedObservations={loadedObservations}");
#endif
            }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
#else
            catch (Exception)
#endif
            {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                storedContributorSaveStatus = "exception";
                WriteDeveloperLog($"USER_MODEL_MEMORY result=SAVE_ERROR type={ex.GetType().Name} newObservations={newObservations} contributorBackfills={contributorBackfills} loadedObservations={loadedObservations}");
#endif
            }
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        else if (!mayPersistUserModel && (newObservations > 0 || contributorBackfills > 0 || contributorProvenanceAdded > 0 || excludedEvidenceChanged))
        {
            storedContributorSaveStatus = "blocked_unreadable_store";
            WriteDeveloperLog("USER_MODEL_MEMORY result=SAVE_BLOCKED reason=stored_model_unreadable_or_incompatible action=preserve_original_data");
        }
#endif

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var userModelAfterPersistence = GC.GetAllocatedBytesForCurrentThread();
        var maturity = observationCount <= 0 ? 0.0d : observationCount / (observationCount + 100.0d);
        WriteDeveloperLog($"USER_MODEL_BASE_EVIDENCE_LEDGER result=OBSERVED persistedEntries={baseEvidenceLedger.Count} addedThisRun={ledgerNewEntries} legacyWithoutPerFactProvenance={Math.Max(0L, observationCount - baseEvidenceLedger.Count)} incompleteLegacyLedgerEntries={baseLedgerIncomplete} saveStatus={storedContributorSaveStatus} immediateReadback={baseLedgerReadback} termIdentity=exact_deduplicated_first16 hourIdentity=credited_hour policy=preserve_historical_aggregate_no_inferred_reweighting futureRecalibration=ledger_only storageMutation=append_on_new_base_fact");
        WriteDeveloperLog($"USER_MODEL_MEMORY result=OK loadedObservations={loadedObservations} storedObservations={observationCount} newObservations={newObservations} contributorBackfills={contributorBackfills} works={work.Count} genres={genre.Count} terms={terms.Count} services={service.Count} hours={hour.Count} contributors={contributors.Count} maturity={maturity:0.000} source=additive_natural_behavior automaticReservationLearning=causal_matching_axes_only searchCastContributorLearning=originating_rule_only repeatedRuleEpisodes=independent_programme_facts programTimeSlotLearning=zero notConditionLearning=zero renderingDoesNotTeach=True titleSpecificRules=False productionScoreMutation=True discoveryMutation=True");

        static (string Source, string Role, string Entity) ContributorDiagnosticIdentity(string key)
        {
            if (key.StartsWith("tracked|", StringComparison.OrdinalIgnoreCase))
                return ("SearchCast", "tracked", key["tracked|".Length..]);
            var separator = key.IndexOf('|');
            return separator <= 0 || separator >= key.Length - 1
                ? ("metadata", "unknown", key)
                : ("metadata", key[..separator], key[(separator + 1)..]);
        }

        static string ContributorDiagnosticToken(string value)
            => (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('|', '/')
                .Trim();

        AIrhythmRecommendationEngine.LogContributorExtractionDiagnostics();
        var maxContributorWeight = contributors.Values.DefaultIfEmpty(0.0d).Max();
        var trackedContributors = contributors.Keys.Count(x => x.StartsWith("tracked|", StringComparison.OrdinalIgnoreCase));
        var metadataContributors = Math.Max(0, contributors.Count - trackedContributors);
        WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_SUMMARY total={contributors.Count} tracked={trackedContributors} metadata={metadataContributors} maxWeight={maxContributorWeight:0.000} diagnostics=top_weighted_role_aware provenance=searchcast_or_labeled_extendeditems metadataGate=none_additive_from_first_fact negativeFromMissing=False titleSpecificRules=False");

        // 保存・読込 — The saved contributor weight aggregates historical observation facts; we cannot infer all-time
        // distinct works from current-snapshot WorkKey alone. This diagnostic explicitly limits its
        // concentration assessment to currently available observations.
        var snapshotContributorPairs = contributorDiagnosticObservations.Count;
        var snapshotRepeatedSingleWork = contributorDiagnosticObservations.Count(x =>
            x.Value > 1 && contributorDiagnosticWorks.TryGetValue(x.Key, out var works) && works.Count == 1);
        var snapshotNoWorkIdentity = contributorDiagnosticObservations.Count(x =>
            !contributorDiagnosticWorks.TryGetValue(x.Key, out var works) || works.Count == 0);
        var snapshotRepeatedObservations = contributorDiagnosticObservations
            .Where(x => x.Value > 1 && contributorDiagnosticWorks.TryGetValue(x.Key, out var works) && works.Count == 1)
            .Sum(x => x.Value);
        var snapshotTotalContributorObservations = contributorDiagnosticObservations.Values.Sum();
        var snapshotMultipleWorks = contributorDiagnosticWorks.Count(x => x.Value.Count > 1);
        var persistedKnownFactCount = contributorSeen.Count(contributorFactWorkKeys.ContainsKey);
        var persistedUnknownFactCount = Math.Max(0, contributorSeen.Count - persistedKnownFactCount);
        var knownContributorWorks = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var contributorSuffixIndex = BuildContributorFactSuffixIndex(contributors.Keys);
        foreach (var (factId, workKey) in contributorFactWorkKeys)
        {
            if (!contributorSeen.Contains(factId) || string.IsNullOrWhiteSpace(workKey)) continue;
            var key = FindContributorFactSuffix(factId, contributorSuffixIndex);
            if (key is null) continue;
            if (!knownContributorWorks.TryGetValue(key, out var uniqueWorks))
                knownContributorWorks[key] = uniqueWorks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            uniqueWorks.Add(workKey);
        }
        WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_PROVENANCE_PERSISTENCE result=OBSERVED loadedMappings={loadedContributorMappings} loadedFactIds={loadedContributorFactIds} reusedMappings={contributorProvenanceReused} newlyMapped={contributorProvenanceAdded} workingMappings={contributorFactWorkKeys.Count} saveStatus={storedContributorSaveStatus} verifiedOnRestart={(loadedContributorMappings > 0 && contributorProvenanceReused > 0 ? "observed" : "not_yet_observed")} persistedReadback=next_plugin_start_required historicalUnknownPreserved=True scoringMutation=False");
        WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_WORK_PROVENANCE result=OBSERVED knownFacts={persistedKnownFactCount} unknownHistoricalFacts={persistedUnknownFactCount} knownContributors={knownContributorWorks.Count} knownDistinctContributorWorkPairs={knownContributorWorks.Sum(x => x.Value.Count)} currentRunNewMappings={contributorProvenanceAdded} coverage=partial_if_unknown scoringMutation=False inferenceFromMissing=False");
        WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_PROVENANCE result=OBSERVED snapshotContributorObservations={snapshotTotalContributorObservations} repeatedSingleWorkObservations={snapshotRepeatedObservations} multipleWorkContributors={snapshotMultipleWorks} scope=current_snapshot_only persistedFactsHaveWorkProvenance=partial historicalDistinctWorks=partial_known_only unit=contributor_observation_pairs storageMutation=False scoreMutation=False");
        WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_CONCENTRATION result=OBSERVED snapshotContributors={snapshotContributorPairs} repeatedSingleWork={snapshotRepeatedSingleWork} missingWorkIdentity={snapshotNoWorkIdentity} persistedContributorCount={contributors.Count} metric=current_snapshot_observations_per_distinct_work historicalDistinctWorks=unavailable scoringMutation=False storageMutation=False");

        var contributorRank = 0;
        foreach (var contributor in contributors
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(24))
        {
            contributorRank++;
            var identity = ContributorDiagnosticIdentity(contributor.Key);
            var currentSnapshotObservations = contributorDiagnosticObservations.TryGetValue(contributor.Key, out var observedCount) ? observedCount : 0;
            var currentSnapshotDistinctWorks = contributorDiagnosticWorks.TryGetValue(contributor.Key, out var observedWorks) ? observedWorks.Count : 0;
            var contributorSeenSuffix = "|" + contributor.Key;
            var persistedFacts = contributorSeen.Count(x => x.EndsWith(contributorSeenSuffix, StringComparison.OrdinalIgnoreCase));
            var strength = maxContributorWeight <= 0.0d ? 0.0d : Math.Clamp(contributor.Value / maxContributorWeight, 0.0d, 1.0d);
            WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_SAMPLE rank={contributorRank} entity={ContributorDiagnosticToken(identity.Entity)} role={identity.Role} source={identity.Source} weight={contributor.Value:0.000} strength={strength:0.000} persistedFacts={persistedFacts} currentSnapshotObservations={currentSnapshotObservations} currentSnapshotDistinctWorks={currentSnapshotDistinctWorks} currentSnapshotObservationsPerWork={(currentSnapshotDistinctWorks == 0 ? 0.0d : (double)currentSnapshotObservations / currentSnapshotDistinctWorks):0.000} historicalDistinctWorks=unavailable persisted=True scoringAxis=contributor noNegativeFromAbsence=True diagnosticsScope=persisted_fact_count_plus_current_snapshot_work_span");
        }
#endif
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var userModelAfterDiagnostics = GC.GetAllocatedBytesForCurrentThread();
#endif
        // 推薦・学習 — Retain saved observations intact; exclude structurally ambiguous contributor keys from scoring
        // without migration or person-specific inference.
        var quarantinedKeys = contributors.Keys.Where(IsStructurallyAmbiguousPersistedContributor).ToArray();
        var scoringContributors = CopyMap(contributors);
        foreach (var key in quarantinedKeys)
            scoringContributors.Remove(key);

        // 保存・読込 — Score-only calibration. Unknown historical facts and their weight are preserved: only
        // explicitly mapped fact IDs can be grouped by Work. The first independent Work retains its full credit;
        // later observations of that SAME Work contribute with diminishing returns. Additional distinct Works
        // retain a full first credit. Never apply an inference about a missing Work, and never persist the
        // projection.
        var mappedFactsByContributor = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var scoringSuffixIndex = BuildContributorFactSuffixIndex(scoringContributors.Keys);
        foreach (var factId in contributorSeen)
        {
            if (!contributorFactWorkKeys.TryGetValue(factId, out var workKey) || string.IsNullOrWhiteSpace(workKey))
                continue;
            var contributorKey = FindContributorFactSuffix(factId, scoringSuffixIndex);
            if (contributorKey is null) continue;
            if (!mappedFactsByContributor.TryGetValue(contributorKey, out var byWork))
                mappedFactsByContributor[contributorKey] = byWork = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            byWork[workKey] = byWork.TryGetValue(workKey, out var count) ? count + 1 : 1;
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        // 推薦・学習 — Score comparisons use the shared provenance boundary.
        var preCalibrationContributorWeights = CopyMap(scoringContributors);
#endif
        var calibratedContributors = 0;
        var knownObservationCreditReduction = 0.0d;
        foreach (var (contributorKey, byWork) in mappedFactsByContributor)
        {
            // 処理条件 — Each mapped contributor fact currently contributes at most 1 to stored weight. With partial
            // provenance, never reduce by more than its provably mapped excess.
            var knownCount = byWork.Values.Sum();
            if (contributorKey.StartsWith("tracked|", StringComparison.OrdinalIgnoreCase)
                || knownCount <= byWork.Count || !scoringContributors.TryGetValue(contributorKey, out var originalWeight))
                continue;
            var adjustedKnownCredit = byWork.Values.Sum(count => 1.0d + Math.Log2(count));
            var reduction = Math.Min(Math.Max(0.0d, originalWeight - 1.0d), Math.Max(0.0d, knownCount - adjustedKnownCredit));
            if (reduction <= 0.0d) continue;
            scoringContributors[contributorKey] = originalWeight - reduction;
            knownObservationCreditReduction += reduction;
            calibratedContributors++;
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_WORK_CALIBRATION result=APPLIED mappedContributors={mappedFactsByContributor.Count} multiWorkContributors={mappedFactsByContributor.Count(x => x.Value.Count > 1)} calibratedContributors={calibratedContributors} knownObservationCreditReduction={knownObservationCreditReduction:0.000} unmappedHistoricalFactsPreserved=True explicitTrackedIntentUnchanged=True storageMutation=False scoreMutation=True model=per_work_log_diminishing_known_only");
        WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_LEGACY_QUARANTINE result=APPLIED candidates={contributors.Count} quarantined={quarantinedKeys.Length} retained={scoringContributors.Count} storageMutation=False observationDeletion=False scope=scoring_only revalidation=structural_unseparated_mixed_script");
#endif
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var userModelAfterProjection = GC.GetAllocatedBytesForCurrentThread();
        WriteDeveloperLog($"USER_MODEL_ALLOCATION_DETAIL loadBytes={userModelAfterLoad - userModelAllocStart} observationBytes={userModelAfterObserve - userModelAfterLoad} persistenceBytes={userModelAfterPersistence - userModelAfterObserve} diagnosticsBytes={userModelAfterDiagnostics - userModelAfterPersistence} projectionBytes={userModelAfterProjection - userModelAfterDiagnostics} accountedBytes={userModelAfterProjection - userModelAllocStart} scope=current_thread_exact noExtraStorageReads=True storageSchema=unchanged");
#endif
        return next with
        {
            ContributorWeights = scoringContributors,
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            PreCalibrationContributorWeights = preCalibrationContributorWeights
#endif
        };
    }

    public static IReadOnlyList<AIrhythmInterestSignal> GetInterestSignals()
        => LoadInterestSignals(requireResolvedIdentity: true);

    public static AIrhythmSaveResult RecordInterestSignal(AIrhythmRuntimeSnapshot snapshot, string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 512)
            return new(false, "追加対象を確認できませんでした。");
        var selected = snapshot.Events.FirstOrDefault(x => string.Equals(x.EventId, eventId, StringComparison.Ordinal));
        if (selected is null)
            return new(false, "追加対象を確認できませんでした。");
        var identityContext = AIrhythmRecommendationEngine.BuildEvidenceIdentityContext(snapshot);
        var series = AIrhythmRecommendationEngine.CanonicalWorkKey(selected, identityContext);
        if (series.Length == 0 || !ServiceIdentityOf(selected).IsValid)
            return new(false, "追加対象を確認できませんでした。");
        var current = GetInterestSignals();
        if (current.Any(x => string.Equals(x.SeriesKey, series, StringComparison.OrdinalIgnoreCase)
            && ServiceIdentityOf(x) == ServiceIdentityOf(selected)))
            return new(true, string.Empty, Changed: false);
        var signal = new AIrhythmInterestSignal(
            selected.EventId, series, selected.Genre ?? string.Empty, selected.ServiceName, DateTimeOffset.Now,
            selected.NetworkId, selected.TransportStreamId, selected.ServiceId,
            AIrhythmRecommendationEngine.ContributorKeysForInterest(selected));
        var preserved = LoadInterestSignals(requireResolvedIdentity: false);
        var next = new[] { signal }.Concat(preserved).Take(24).ToArray();
        return SaveInterestSignals(next)
            ? new(true, string.Empty, Changed: true)
            : new(false, "『気になる』を保存できませんでした。");
    }

    public static AIrhythmSaveResult RemoveInterestSignal(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 512)
            return new(false, "解除対象を確認できませんでした。");
        var current = LoadInterestSignals(requireResolvedIdentity: false);
        var next = current.Where(x => !string.Equals(x.EventId, eventId, StringComparison.Ordinal)).ToArray();
        if (next.Length == current.Count)
            return new(true, string.Empty, Changed: false);
        return SaveInterestSignals(next)
            ? new(true, string.Empty, Changed: true)
            : new(false, "『気になる』を解除できませんでした。");
    }

    private static bool SaveInterestSignals(IReadOnlyList<AIrhythmInterestSignal> values)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null) return false;
        try
        {
            var json = JsonSerializer.Serialize(values.Take(24).ToArray(), JsonOptions);
            var result = context.Storage.Set("rhythmSearch", "interestSignals", json, expectedRevision: null);
            if (result.Succeeded) Invalidate("InterestSignalChanged");
            return result.Succeeded;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeStoredRhythmSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        try
        {
            var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
            if (normalized.Length == 0
                || normalized.Length > 160
                || normalized.Any(ch =>
                {
                    var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                    return char.IsControl(ch)
                        || category is UnicodeCategory.Format
                            or UnicodeCategory.Surrogate
                            or UnicodeCategory.PrivateUse
                            or UnicodeCategory.OtherNotAssigned;
                }))
                return string.Empty;
            if (normalized.Contains('<') || normalized.Contains('>'))
                return string.Empty;
            if (ContainsAny(normalized, "javascript:", "vbscript:", "data:", "srcdoc=", "<script", "onload=", "onclick=", "onerror=", "onmouseover="))
                return string.Empty;
            return normalized;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static bool IsUsefulReservation(TvAirReservationDto item)
    {
        if (!item.IsEnabled || item.HasConflict || string.IsNullOrWhiteSpace(item.ProgramTitle))
            return false;
        var state = $"{item.Status} {item.Source} {item.Route}";
        return !ContainsAny(state, "cancel", "disabled", "removed", "取消", "無効", "削除");
    }

    private static bool NormalizationModelContentChanged<T>(
        IReadOnlyDictionary<string, T> before,
        IReadOnlyDictionary<string, T> after)
    {
        if (before.Count != after.Count)
            return true;
        foreach (var item in before)
        {
            if (!after.TryGetValue(item.Key, out var value)
                || !EqualityComparer<T>.Default.Equals(item.Value, value))
                return true;
        }
        return false;
    }

}
