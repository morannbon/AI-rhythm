namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmDataState
{
    private static bool TryReadUsageCounterState(ITvAirPluginRuntimeContext context, out AIrhythmUsageCounterState? stored)
    {
        stored = null;
        try
        {
            var result = context.Storage.Get("usage", "totals");
            if (!result.Succeeded)
                return !context.Storage.Exists("usage", "totals");
            var json = result.Value?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(json))
                return !context.Storage.Exists("usage", "totals");
            stored = JsonSerializer.Deserialize<AIrhythmUsageCounterState>(json, JsonOptions);
            return stored is not null
                && (stored.Version == 1 || stored.Version == 2 && stored.RecordingAccountingStart is not null
                    && stored.CountedRecordingKeys is not null
                    && stored.CountedRecordingKeys.All(x => x.Length == 64 && x.All(Uri.IsHexDigit)));
        }
        catch { return false; }
    }

    private static void PersistUsageCounterState(ITvAirPluginRuntimeContext context)
    {
        // 録画実績・復旧 — Concurrent recording/reservation notifications must not persist an older snapshot after a
        // newer total. The serialized writer always reads the latest in-memory totals, and verification failure
        // leaves a retry marker.
        lock (UsageTotalsPersistenceGate)
        {
            AIrhythmUsageTotals totals;
            DateTimeOffset accountingStart;
            string[] receiptKeys;
            long receiptRevision;
            lock (Gate)
            {
                if (!ReferenceEquals(_runtimeContext, context) || !_usageTotalsInitialized)
                    return;
                totals = _usageTotals;
                accountingStart = _usageRecordingAccountingStart;
                receiptRevision = _usageRecordingLedgerRevision;
                receiptKeys = UsageCountedRecordingKeys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }
            try
            {
                var json = JsonSerializer.Serialize(new AIrhythmUsageCounterState(
                    totals.RecordingTotal, totals.ReservationTotal, Version: 2, RecordingAccountingStart: accountingStart,
                    CountedRecordingKeys: receiptKeys), JsonOptions);
                var written = context.Storage.Set("usage", "totals", json, expectedRevision: null);
                if (!written.Succeeded)
                    throw new InvalidOperationException("Usage totals write failed");
                var readback = context.Storage.Get("usage", "totals");
                if (!readback.Succeeded || !string.Equals(readback.Value?.Value?.ToString(), json, StringComparison.Ordinal))
                    throw new InvalidOperationException("Usage totals verification failed");
                lock (Gate) _usageTotalsDirty = !_usageTotals.Equals(totals)
                    || _usageRecordingLedgerRevision != receiptRevision;
            }
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
            #else
            catch (Exception)
            #endif
            {
                lock (Gate) _usageTotalsDirty = true;
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"usage totals persistence deferred type={ex.GetType().Name} action=retry_on_read");
                #endif
            }
        }
    }

    private static bool IsRecordingFailureForRecovery(TvAirRecordingHistoryDto item)
    {
        var state = $"{item.Result} {item.EndReason}";
        if (state.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            || state.Contains("abort", StringComparison.OrdinalIgnoreCase)
            || state.Contains("取消", StringComparison.OrdinalIgnoreCase)
            || state.Contains("中止", StringComparison.OrdinalIgnoreCase)) return false;
        return state.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || state.Contains("error", StringComparison.OrdinalIgnoreCase)
            || state.Contains("失敗", StringComparison.OrdinalIgnoreCase)
            || item.FileCreated == false;
    }

    // 録画実績・復旧 — Host recording IDs can be reused across reservation lifecycles. Persisted facts, recovery
    // observations, and deduplication use the scoped recording identity derived from service, broadcast
    // slot, and attempt discriminator, rather than treating the raw Host ID as globally unique.
    private static string RecordingIdentity(
        string? recordingId, string? reservationId,
        int networkId, int transportStreamId, int serviceId, int eventId,
        DateTimeOffset scheduledStart, DateTimeOffset start)
    {
        var broadcastStart = scheduledStart == default ? start : scheduledStart;
        var scope = $"recording:v2:{networkId}:{transportStreamId}:{serviceId}:{broadcastStart.UtcDateTime.Ticks}";
        var recording = recordingId?.Trim() ?? string.Empty;
        if (recording.Length != 0)
            return $"{scope}:id:{recording.Length}:{recording}";
        var reservation = reservationId?.Trim() ?? string.Empty;
        if (reservation.Length != 0)
            return $"{scope}:reservation:{reservation.Length}:{reservation}";
        // 作品識別 — Without either Host ID, the event and scheduled start still identify the broadcast. Never use
        // the changeable End/ActualEnd as identity.
        return $"{scope}:event:{eventId}:{start.UtcDateTime.Ticks}";
    }

    private static string UsageRecordingIdentity(TvAirRecordingHistoryDto item)
        => RecordingIdentity(item.RecordingId, item.ReservationId,
            item.NetworkId, item.TransportStreamId, item.ServiceId, item.EventId,
            item.ScheduledStartTime, item.Start);

    // 録画実績・復旧 — Recording and its originating reservation represent one preference occurrence, even if the
    // recording has several physical file segments. Reused Host reservation IDs from other broadcast slots
    // are NOT the same occurrence. Never use this key to rewrite persisted user-model IDs.
    internal static string RecordingEvidenceCausalIdentity(TvAirRecordingHistoryDto item)
        => string.IsNullOrWhiteSpace(item.ReservationId)
            ? UsageRecordingIdentity(item)
            : RecordingIdentity(null, item.ReservationId, item.NetworkId,
                item.TransportStreamId, item.ServiceId, item.EventId,
                item.ScheduledStartTime, item.Start);

    internal static string ReservationEvidenceCausalIdentity(TvAirReservationDto item)
        => RecordingIdentity(null, item.ReservationId, item.NetworkId,
            item.TransportStreamId, item.ServiceId, item.EventNumber,
            item.ScheduledStart ?? item.Start, item.Start);

    internal static bool RecordingBelongsToReservationOccurrence(
        TvAirRecordingHistoryDto recording, TvAirReservationDto reservation)
        => !string.IsNullOrWhiteSpace(recording.ReservationId)
            && string.Equals(recording.ReservationId, reservation.ReservationId, StringComparison.OrdinalIgnoreCase)
            && recording.NetworkId == reservation.NetworkId
            && recording.TransportStreamId == reservation.TransportStreamId
            && recording.ServiceId == reservation.ServiceId
            && (recording.ScheduledStartTime == default ? recording.Start : recording.ScheduledStartTime)
                == (reservation.ScheduledStart ?? reservation.Start);

    private static string CanonicalRecordingFactIdentity(AIrhythmRecordingFact fact)
        => RecordingIdentity(fact.RecordingId, fact.ReservationId,
            fact.NetworkId, fact.TransportStreamId, fact.ServiceId, fact.EventId,
            fact.ScheduledStartTime, fact.Start);

    // 録画実績・復旧 — Read-only bridge for historical recovery journals. Existing journal rows are never discarded
    // just because their IDs predate the scoped identity.
    private static string LegacyRecoveryIdentity(TvAirRecordingHistoryDto item)
    {
        if (!string.IsNullOrWhiteSpace(item.RecordingId))
            return item.RecordingId.Trim();
        if (!string.IsNullOrWhiteSpace(item.ReservationId))
            return $"reservation:{item.ReservationId.Trim()}:{item.Start.UtcDateTime.Ticks}:{item.End.UtcDateTime.Ticks}";
        return $"recording:{item.NetworkId}:{item.TransportStreamId}:{item.ServiceId}:{item.EventId}:{item.Start.UtcDateTime.Ticks}:{item.End.UtcDateTime.Ticks}";
    }

    private static bool IsSameLegacyRecoveryAttempt(AIrhythmRecoveryAttempt old, TvAirRecordingHistoryDto current)
        => string.Equals(old.Identity, LegacyRecoveryIdentity(current), StringComparison.OrdinalIgnoreCase)
            && old.NetworkId == current.NetworkId
            && old.TransportStreamId == current.TransportStreamId
            && old.ServiceId == current.ServiceId
            && old.ScheduledStartTime == current.ScheduledStartTime
            && old.Start == current.Start;

    // 録画実績・復旧 — Legacy recovery rows hold no separate RecordingId field. For a full fallback load, derive
    // the new key from their original identity and slot. Retain all distinct attempts even when old Host IDs
    // collide across months.
    private static string CanonicalRecoveryAttemptIdentity(AIrhythmRecoveryAttempt row)
    {
        if (row.Identity.StartsWith("recording:v2:", StringComparison.OrdinalIgnoreCase))
            return row.Identity;
        if (row.Identity.StartsWith("recording:", StringComparison.OrdinalIgnoreCase))
            return RecordingIdentity(null, null, row.NetworkId, row.TransportStreamId,
                row.ServiceId, row.EventId, row.ScheduledStartTime, row.Start);
        if (row.Identity.StartsWith("reservation:", StringComparison.OrdinalIgnoreCase))
        {
            var endSeparator = row.Identity.LastIndexOf(':');
            var beginSeparator = endSeparator <= 0 ? -1 : row.Identity.LastIndexOf(':', endSeparator - 1);
            var id = beginSeparator > "reservation:".Length
                ? row.Identity["reservation:".Length..beginSeparator]
                : row.Identity["reservation:".Length..];
            return RecordingIdentity(null, id, row.NetworkId, row.TransportStreamId,
                row.ServiceId, row.EventId, row.ScheduledStartTime, row.Start);
        }
        return RecordingIdentity(row.Identity, null, row.NetworkId, row.TransportStreamId,
            row.ServiceId, row.EventId, row.ScheduledStartTime, row.Start);
    }

    private static AIrhythmRecordingFact ToRecordingFact(
        TvAirRecordingHistoryDto item, TvAirReservationDto? matchingReservation = null)
        => new(
            UsageRecordingIdentity(item),
            item.ReservationId ?? string.Empty,
            item.RecordingId ?? string.Empty,
            item.ServiceName ?? string.Empty,
            item.ProgramTitle ?? string.Empty,
            item.NetworkId, item.TransportStreamId, item.ServiceId, item.EventId,
            item.ScheduledStartTime,
            item.Genre ?? string.Empty,
            item.GenreCodes ?? string.Empty,
            item.Start, item.End, item.ActualStart, item.ActualEnd,
            item.Result ?? string.Empty,
            item.EndReason ?? string.Empty,
            item.FileCreated,
            item.ResultFinalized,
            item.DropCount, item.ErrorCount, item.ScrambleCount,
            item.QualityDataAvailable, item.QualityCompleteness ?? string.Empty,
            item.QualitySource ?? string.Empty,
            matchingReservation is not null,
            matchingReservation?.Intent ?? TvAirReservationIntent.Unspecified,
            matchingReservation?.Source ?? string.Empty);

    private static TvAirRecordingHistoryDto FromRecordingFact(AIrhythmRecordingFact fact)
        => new()
        {
            ReservationId = fact.ReservationId,
            RecordingId = fact.RecordingId,
            ServiceName = fact.ServiceName,
            ProgramTitle = fact.ProgramTitle,
            NetworkId = fact.NetworkId,
            TransportStreamId = fact.TransportStreamId,
            ServiceId = fact.ServiceId,
            EventId = fact.EventId,
            ScheduledStartTime = fact.ScheduledStartTime,
            Genre = fact.Genre,
            GenreCodes = fact.GenreCodes,
            Start = fact.Start,
            End = fact.End,
            ActualStart = fact.ActualStart,
            ActualEnd = fact.ActualEnd,
            Result = fact.Result,
            EndReason = fact.EndReason,
            FileCreated = fact.FileCreated,
            ResultFinalized = fact.ResultFinalized,
            DropCount = fact.DropCount,
            ErrorCount = fact.ErrorCount,
            ScrambleCount = fact.ScrambleCount,
            QualityDataAvailable = fact.QualityDataAvailable,
            QualityCompleteness = fact.QualityCompleteness,
            QualitySource = fact.QualitySource
        };

    // 録画実績・復旧 — A finalized recording fact represents the original broadcast. A subsequent Host history row
    // with a matching scoped key is not sufficient to rewrite programme metadata when title or event
    // evidence conflicts; leave the persisted fact unchanged.
    private static bool HasRecordingFactMetadataConflict(AIrhythmRecordingFact saved, AIrhythmRecordingFact incoming)
        => (!string.IsNullOrWhiteSpace(saved.ServiceName)
            && !string.IsNullOrWhiteSpace(incoming.ServiceName)
            && !string.Equals(saved.ServiceName.Trim(), incoming.ServiceName.Trim(), StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(saved.ProgramTitle)
                && !string.IsNullOrWhiteSpace(incoming.ProgramTitle)
                && !string.Equals(saved.ProgramTitle.Trim(), incoming.ProgramTitle.Trim(), StringComparison.Ordinal))
            || (saved.EventId != 0 && incoming.EventId != 0 && saved.EventId != incoming.EventId)
            || (!string.IsNullOrWhiteSpace(saved.ReservationId)
                && !string.IsNullOrWhiteSpace(incoming.ReservationId)
                && !string.Equals(saved.ReservationId.Trim(), incoming.ReservationId.Trim(), StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(saved.RecordingId)
                && !string.IsNullOrWhiteSpace(incoming.RecordingId)
                && !string.Equals(saved.RecordingId.Trim(), incoming.RecordingId.Trim(), StringComparison.OrdinalIgnoreCase))
            || (saved.ScheduledStartTime != default && incoming.ScheduledStartTime != default
                && saved.ScheduledStartTime != incoming.ScheduledStartTime)
            || (saved.Start != default && incoming.Start != default && saved.Start != incoming.Start);

    // 録画実績・復旧 — Snapshot refresh and finalization ingestion share one additive recording-fact merge.
    // Previously established metadata remains authoritative unless independent confirming evidence permits
    // correction; append missing quality once without changing unrelated histories or learner data.
    private static AIrhythmRecordingFact MergeRecordingFactMetadata(
        AIrhythmRecordingFact saved, AIrhythmRecordingFact incoming)
    {
        var fillQuality = !saved.QualityDataAvailable && incoming.QualityDataAvailable;
        return saved with
        {
            ServiceName = string.IsNullOrWhiteSpace(saved.ServiceName) ? incoming.ServiceName : saved.ServiceName,
            ProgramTitle = string.IsNullOrWhiteSpace(saved.ProgramTitle) ? incoming.ProgramTitle : saved.ProgramTitle,
            EventId = saved.EventId == 0 ? incoming.EventId : saved.EventId,
            Genre = string.IsNullOrWhiteSpace(saved.Genre) ? incoming.Genre : saved.Genre,
            GenreCodes = string.IsNullOrWhiteSpace(saved.GenreCodes) ? incoming.GenreCodes : saved.GenreCodes,
            ActualEnd = saved.ActualEnd ?? incoming.ActualEnd,
            Result = string.IsNullOrWhiteSpace(saved.Result) ? incoming.Result : saved.Result,
            EndReason = string.IsNullOrWhiteSpace(saved.EndReason) ? incoming.EndReason : saved.EndReason,
            FileCreated = saved.FileCreated ?? incoming.FileCreated,
            ResultFinalized = saved.ResultFinalized || incoming.ResultFinalized,
            DropCount = fillQuality ? incoming.DropCount : saved.DropCount,
            ErrorCount = fillQuality ? incoming.ErrorCount : saved.ErrorCount,
            ScrambleCount = fillQuality ? incoming.ScrambleCount : saved.ScrambleCount,
            QualityDataAvailable = saved.QualityDataAvailable || incoming.QualityDataAvailable,
            QualityCompleteness = fillQuality ? incoming.QualityCompleteness : saved.QualityCompleteness,
            QualitySource = fillQuality ? incoming.QualitySource : saved.QualitySource,
            // Only the same broadcast occurrence may supply previously missing provenance. Keep an established
            // origin on later Host replays; an unverified replay cannot erase it.
            ReservationOriginKnown = saved.ReservationOriginKnown || incoming.ReservationOriginKnown,
            ReservationIntent = saved.ReservationOriginKnown ? saved.ReservationIntent : incoming.ReservationIntent,
            ReservationSource = saved.ReservationOriginKnown ? saved.ReservationSource : incoming.ReservationSource
        };
    }

    private static string RecordingFactMonthKey(AIrhythmRecordingFact fact)
        => (fact.ActualStart ?? fact.Start).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static DateTimeOffset? GetDataResetCutoff(ITvAirPluginRuntimeContext context)
    {
        lock (Gate)
        {
            if (_dataResetCutoffLoaded)
                return _dataResetCutoff;
        }

        DateTimeOffset? cutoff = null;
        try
        {
            var result = context.Storage.Get(DataLifecycleStorageNamespace, DataResetCutoffStorageKey);
            var json = result.Succeeded ? result.Value?.Value?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(json))
                cutoff = JsonSerializer.Deserialize<AIrhythmDataResetMarker>(json, JsonOptions)?.ResetAt;
        }
        catch { }

        lock (Gate)
        {
            if (!_dataResetCutoffLoaded)
            {
                _dataResetCutoff = cutoff;
                _dataResetCutoffLoaded = true;
            }
            return _dataResetCutoff;
        }
    }

    private static void SetDataResetCutoffCache(DateTimeOffset? cutoff)
    {
        lock (Gate)
        {
            _dataResetCutoff = cutoff;
            _dataResetCutoffLoaded = true;
        }
    }

    private static bool EnsureRecordingFactsLoaded(ITvAirPluginRuntimeContext context)
    {
        lock (Gate)
        {
            if (_recordingFactsLoaded)
                return true;
        }

        var loaded = new Dictionary<string, AIrhythmRecordingFact>(StringComparer.OrdinalIgnoreCase);
        var revisions = new Dictionary<string, long>(StringComparer.Ordinal);
        var migratedMonths = new HashSet<string>(StringComparer.Ordinal);
        var resetCutoff = GetDataResetCutoff(context);
        try
        {
            foreach (var key in context.Storage.ListKeys(RecordingFactStorageNamespace))
            {
                var entry = context.Storage.Get(RecordingFactStorageNamespace, key);
                if (!entry.Succeeded || entry.Value is null)
                    throw new InvalidOperationException("Recording fact chunk read failed");
                var json = entry.Value.Value?.ToString();
                if (string.IsNullOrWhiteSpace(json))
                    throw new InvalidOperationException("Recording fact chunk missing");
                revisions.Add(key, entry.Value.Revision);
                var facts = JsonSerializer.Deserialize<AIrhythmRecordingFact[]>(json, JsonOptions)
                    ?? throw new InvalidOperationException("Recording fact chunk invalid");
                foreach (var fact in facts)
                {
                    if (string.IsNullOrWhiteSpace(fact.Identity))
                        throw new InvalidOperationException("Recording fact identity missing");
                    if (RecordingFactMonthKey(fact) != key)
                        throw new InvalidOperationException("Recording fact stored in wrong month");
                    if (resetCutoff is not null && (fact.ActualStart ?? fact.Start) < resetCutoff.Value)
                        continue;
                    var identity = CanonicalRecordingFactIdentity(fact);
                    var canonical = fact.Identity == identity ? fact : fact with { Identity = identity };
                    if (!loaded.TryAdd(identity, canonical))
                        throw new InvalidOperationException("Distinct recording rows share a canonical identity");
                    if (fact.Identity != identity)
                        migratedMonths.Add(key);
                }
            }
        }
        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
        #else
        catch (Exception)
        #endif
        {
            // 保存・読込 — Never promote an incomplete read into the persisted source of truth. The next normal snapshot
            // may safely retry loading the entire set.
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"recording fact store load failed type={ex.GetType().Name} action=defer_without_overwrite");
            #endif
            return false;
        }

        lock (Gate)
        {
            if (!_recordingFactsLoaded)
            {
                foreach (var pair in loaded)
                    RecordingFacts[pair.Key] = pair.Value;
                foreach (var pair in revisions)
                    RecordingFactChunkRevisions[pair.Key] = pair.Value;
                RecordingFactsDirtyMonths.UnionWith(migratedMonths);
                _recordingFactsLoaded = true;
            }
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"recording fact store loaded facts={loaded.Count} chunks={revisions.Count} identityMigratedMonths={migratedMonths.Count} identityVersion=2 policy=append_success_terminal_no_host_deletion");
#endif
        return true;
    }

    private static void PersistRecordingFactChunks(ITvAirPluginRuntimeContext context, IReadOnlyCollection<string> monthKeys)
    {
        lock (Gate)
        {
            RecordingFactsDirtyMonths.UnionWith(monthKeys);
        }

        // 保存・読込 — Serialize writes to the same month. Never clear the dirty marker when an event has updated the
        // source facts while a previous snapshot was written.
        lock (RecordingFactsPersistenceGate)
        {
            string[] months;
            lock (Gate) months = RecordingFactsDirtyMonths.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            foreach (var month in months)
            {
                AIrhythmRecordingFact[] rows;
                long generation;
                long? expectedRevision;
                lock (Gate)
                {
                    RecordingFactChunkRevisions.TryGetValue(month, out var knownRevision);
                    expectedRevision = RecordingFactChunkRevisions.ContainsKey(month) ? knownRevision : null;
                    generation = _recordingFactsGeneration;
                    rows = RecordingFacts.Values
                        .Where(x => RecordingFactMonthKey(x) == month)
                        .OrderBy(x => x.ActualStart ?? x.Start)
                        .ThenBy(x => x.Identity, StringComparer.Ordinal)
                        .ToArray();
                }
                try
                {
                    var json = JsonSerializer.Serialize(rows, JsonOptions);
                    // 処理条件 — A failed or stale read cannot authorize replacing a complete historical month. Bound updates to
                    // the loaded source revision.
                    if (expectedRevision is null && context.Storage.Exists(RecordingFactStorageNamespace, month))
                        throw new InvalidOperationException("Recording fact month changed since load");
                    var written = context.Storage.Set(RecordingFactStorageNamespace, month, json, expectedRevision: expectedRevision);
                    if (!written.Succeeded)
                        throw new InvalidOperationException("Recording fact chunk write failed");
                    var readback = context.Storage.Get(RecordingFactStorageNamespace, month);
                    if (!readback.Succeeded || readback.Value is null ||
                        !string.Equals(readback.Value.Value?.ToString(), json, StringComparison.Ordinal))
                        throw new InvalidOperationException("Recording fact chunk verification failed");
                    lock (Gate)
                    {
                        RecordingFactChunkRevisions[month] = readback.Value.Revision;
                        if (_recordingFactsGeneration == generation)
                            RecordingFactsDirtyMonths.Remove(month);
                    }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                    WriteDeveloperLog($"RECORDING_QUALITY_CHUNK_READBACK result=MATCH month={month} rows={rows.Length} bytes={json.Length} mutation=none");
#endif
                }
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                catch (Exception ex)
                #else
                catch (Exception)
                #endif
                {
                    // Host連携 — Retain dirty months and retry during subsequent normal reads/events.
                    #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                    WriteDeveloperLog($"recording fact store persist deferred chunk={month} type={ex.GetType().Name}");
                    #endif
                }
            }
        }
    }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
    // 録画実績・復旧 — Snapshot-only diagnostics: no new timers, no additional full-history storage scan. Never log
    // recording IDs or absolute file paths.
    private static void ReportRecordingQualityPersistence(
        IReadOnlyList<TvAirRecordingHistoryDto> persisted,
        IReadOnlyList<TvAirRecordingHistoryDto> host,
        IReadOnlyList<TvAirRecordingHistoryDto> recovery)
    {
        var available = 0;
        var abnormal = 0;
        var unavailable = 0;
        var samples = new List<TvAirRecordingHistoryDto>(6);
        foreach (var fact in persisted)
        {
            if (!fact.QualityDataAvailable) { unavailable++; continue; }
            available++;
            if ((fact.DropCount ?? 0) <= 0 && (fact.ErrorCount ?? 0) <= 0 && (fact.ScrambleCount ?? 0) <= 0)
                continue;
            abnormal++;
            // 文字列解析 — Bounded recent samples, not a title-specific watch list.
            if (samples.Count < 6) samples.Add(fact);
        }
        var hostAvailable = host.Count(x => x.QualityDataAvailable);
        WriteDeveloperLog($"RECORDING_QUALITY_PERSISTENCE result=OBSERVED persisted={persisted.Count} qualityAvailable={available} qualityUnknown={unavailable} abnormal={abnormal} host={host.Count} hostQualityAvailable={hostAvailable} recoveryCandidates={recovery.Count} sampling=first_six_recent_abnormal action=diagnostic_only");
        foreach (var item in samples)
        {
            var date = (item.ActualStart ?? item.Start).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            WriteDeveloperLog($"RECORDING_QUALITY_PERSISTENCE_SAMPLE station={item.ServiceName} programme={item.ProgramTitle} started={date} drop={item.DropCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} cc={item.ErrorCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} scramble={item.ScrambleCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} source={item.QualitySource} action=diagnostic_only");
        }
    }
#endif

    // 録画実績・復旧 — Recovery records remain immutable evidence of separate attempts, including after a later
    // clean replacement. Monthly storage writes touch changed buckets only. Episode-ambiguous titles are not
    // treated as confirmed episode matches and do not trigger automatic reservations. Indexed resolution
    // state remains a separate development item.
    private static bool TryRecoveryEpisodeKey(string? title, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(title)) return false;
        var normalized = title.Normalize(NormalizationForm.FormKC);
        // 録画実績・復旧 — A broadcast carrying several episodes is not a single-episode identity. In particular #1-12,
        // #1~12, #1,2, #1/#2 and 第1～3話 must not silently degrade to episode 1. Never expand ranges into separate
        // recovery matches: they require independent recording-quality evidence.
        if (Regex.IsMatch(normalized,
            @"(?:[#＃]\s*\d{1,4}|第\s*\d{1,4}\s*(?:話|章)?)\s*(?:[-~〜～–—－]|[,，、/／&＆+＋])\s*(?:[#＃]\s*|第\s*)?\d{1,4}"))
            return false;
        var matches = Regex.Matches(normalized,
            @"(?:[#＃]\s*(?<number>[0-9]{1,4})(?![0-9])|第\s*(?<number>[0-9]{1,4})\s*(?:話|章))");
        // 作品識別 — Multiple individually stated episodes (e.g. 第1話・第2話) are also ambiguous for an episode-specific
        // repair attempt.
        if (matches.Count != 1) return false;
        var match = matches[0];
        if (!int.TryParse(match.Groups["number"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var number) || number < 1)
            return false;
        var stem = normalized[..match.Index];
        // 作品識別 — Retain season and edition markers in the stem: never merge an episode from a different season
        // because its numeric episode happens to match.
        var compact = new string(stem.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (compact.Length < 4) return false;
        key = compact + "#" + number.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    // 録画実績・復旧 — A multi-episode EPG listing is a *coverage hint* only. It is never proof of episode-level
    // recording quality and cannot close or reserve a recovery case. Never expand arbitrary lists or titles
    // containing a second episode marker.
    private static bool TryRecoveryMultiEpisodeRange(string? title, out string stem, out int first, out int last)
    {
        stem = string.Empty;
        first = last = 0;
        if (string.IsNullOrWhiteSpace(title)) return false;
        var normalized = title.Normalize(NormalizationForm.FormKC);
        var range = Regex.Match(normalized,
            @"(?:[#＃]\s*(?<a>[0-9]{1,4})|第\s*(?<a>[0-9]{1,4})\s*(?:話|章)?)\s*[-~〜～–—－]\s*(?:[#＃]\s*|第\s*)?(?<b>[0-9]{1,4})(?:\s*(?:話|章))?");
        if (!range.Success || !int.TryParse(range.Groups["a"].Value, out first)
            || !int.TryParse(range.Groups["b"].Value, out last)
            || first < 1 || last <= first || last - first > 99) return false;
        // 作品識別 — If additional episode markers exist, the title is structurally ambiguous.
        var remainder = normalized.Remove(range.Index, range.Length);
        if (Regex.IsMatch(remainder, @"(?:[#＃]\s*\d{1,4}|第\s*\d{1,4}\s*(?:話|章))")) return false;
        stem = new string(normalized[..range.Index]
            .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return stem.Length >= 4;
    }

    // 録画実績・復旧 — Read-only recovery candidate detection. Construct a snapshot-local hash index of future EPG
    // events; never reserve, resolve, or mutate recovery evidence here. No extra I/O, timers, background
    // tasks, or network requests.
    private static void AuditRecoveryEpgCandidates(ITvAirPluginRuntimeContext context, IReadOnlyList<TvAirProgramEventDto> futureEvents)
    {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmRecoveryAttempt[] attempts = Array.Empty<AIrhythmRecoveryAttempt>();
        AIrhythmRecoveryLookupEntry[] lookup;
        bool usedVerifiedLookup;
        int journalCount;
        lock (Gate)
        {
            if (!_recoveryAttemptsLoaded) return;
            journalCount = _recoveryLazyIndexed ? _recoveryIndexedTotal + RecoveryAttempts.Values.Count(x => !_verifiedRecoveryLookup.Any(e => string.Equals(e.Identity, x.Identity, StringComparison.OrdinalIgnoreCase))) : RecoveryAttempts.Count;
            usedVerifiedLookup = _recoveryLookupChecked && _verifiedRecoveryLookup.Length == journalCount
                && (_recoveryLazyIndexed || _verifiedRecoveryLookup.All(e => RecoveryAttempts.ContainsKey(e.Identity)));
            lookup = usedVerifiedLookup ? _verifiedRecoveryLookup.ToArray() : Array.Empty<AIrhythmRecoveryLookupEntry>();
            if (!usedVerifiedLookup) attempts = RecoveryAttempts.Values.ToArray();
        }
        var byEpisode = new Dictionary<string, List<TvAirProgramEventDto>>(StringComparer.Ordinal);
        var indexedEvents = 0;
        foreach (var programme in futureEvents)
        {
            if (!TryRecoveryEpisodeKey(programme.Title, out var episode)) continue;
            if (!byEpisode.TryGetValue(episode, out var matches))
                byEpisode[episode] = matches = new List<TvAirProgramEventDto>(1);
            matches.Add(programme);
            indexedEvents++;
        }
        // 作品識別 — Narrow the verified index using only compact episode keys before materializing any canonical
        // record. A match with a range is only a coverage hint, not a repair.
        var ranges = new Dictionary<string, List<(TvAirProgramEventDto Event, int First, int Last)>>(StringComparer.Ordinal);
        foreach (var programme in futureEvents)
        {
            if (!TryRecoveryMultiEpisodeRange(programme.Title, out var stem, out var first, out var last)) continue;
            if (!ranges.TryGetValue(stem, out var collection))
                ranges[stem] = collection = new List<(TvAirProgramEventDto, int, int)>();
            collection.Add((programme, first, last));
        }
        var indexedIdentified = lookup.Count(e => e.EpisodeKey.Length != 0);
        var indexedUnresolved = lookup.Length - indexedIdentified;
        var fetchedMonths = 0;
        var fetchedRows = 0;
        if (usedVerifiedLookup)
        {
            var possibleRows = lookup.Where(e => e.EpisodeKey.Length != 0 &&
                ((byEpisode.TryGetValue(e.EpisodeKey, out var programmes) &&
                    programmes.Any(x => x.Start > e.ScheduledStartTime)) ||
                 (e.EpisodeKey.LastIndexOf('#') is var cut && cut > 0 &&
                    int.TryParse(e.EpisodeKey[(cut + 1)..], out var episodeNumber) &&
                    ranges.TryGetValue(e.EpisodeKey[..cut], out var coveredRanges) &&
                    coveredRanges.Any(x => episodeNumber >= x.First && episodeNumber <= x.Last &&
                        x.Event.Start > e.ScheduledStartTime)))).ToArray();
            if (possibleRows.Length != 0)
            {
                try
                {
                    // 作品識別 — Fetch only months containing possible matches; validate every requested identity against the
                    // already verified index. Never trust a sidecar alone.
                    var selected = new List<AIrhythmRecoveryAttempt>();
                    foreach (var group in possibleRows.GroupBy(x => x.Month, StringComparer.Ordinal))
                    {
                        var stored = context.Storage.Get(RecoveryAttemptStorageNamespace, group.Key);
                        if (!stored.Succeeded || stored.Value?.Value is null)
                            throw new InvalidOperationException("Recovery month selective read failed");
                        var rows = JsonSerializer.Deserialize<AIrhythmRecoveryAttempt[]>(
                            stored.Value.Value.ToString() ?? "", JsonOptions)
                            ?? throw new InvalidOperationException("Recovery month invalid");
                        fetchedMonths++;
                        fetchedRows += rows.Length;
                        var byId = rows.Where(x => !string.IsNullOrWhiteSpace(x.Identity))
                            .GroupBy(x => x.Identity, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
                        foreach (var entry in group)
                        {
                            if (!byId.TryGetValue(entry.Identity, out var record) ||
                                record.ScheduledStartTime != entry.ScheduledStartTime ||
                                !string.Equals(TryRecoveryEpisodeKey(record.ProgramTitle, out var key) ? key : "",
                                    entry.EpisodeKey, StringComparison.Ordinal))
                                throw new InvalidOperationException("Recovery selective identity mismatch");
                            selected.Add(record);
                        }
                    }
                    attempts = selected.ToArray();
                }
                catch (Exception ex)
                {
                    // 処理条件 — A partial month cannot make an old attempt disappear.
                    usedVerifiedLookup = false;
                    lock (Gate) attempts = RecoveryAttempts.Values.ToArray();
                    // 処理条件 — A failed lazy read cannot silently hide old attempts. Disable this diagnostic pass until the
                    // full canonical path is available.
                    if (_recoveryLazyIndexed) return;
                    WriteDeveloperLog($"RECOVERY_MONTH_SELECTIVE result=FALLBACK type={ex.GetType().Name} selection=canonical_all");
                }
            }
        }
        WriteDeveloperLog($"RECOVERY_MONTH_SELECTIVE result=OBSERVED selection={(usedVerifiedLookup ? "verified_filtered" : "canonical_fallback")} indexedAttempts={journalCount} prefilterCandidates={(usedVerifiedLookup ? attempts.Length : -1)} loadedMonths={fetchedMonths} loadedRows={fetchedRows} canonicalResident={(!_recoveryLazyIndexed).ToString()} noReservation=True noResolution=True");
        var identified = usedVerifiedLookup ? indexedIdentified : 0;
        var unresolved = usedVerifiedLookup ? indexedUnresolved : 0;
        var possible = 0;
        var distinctCandidates = new HashSet<string>(StringComparer.Ordinal);
        var reported = 0;
        foreach (var attempt in attempts)
        {
            if (!TryRecoveryEpisodeKey(attempt.ProgramTitle, out var episode))
            {
                if (!usedVerifiedLookup) unresolved++;
                continue;
            }
            if (!usedVerifiedLookup) identified++;
            if (!byEpisode.TryGetValue(episode, out var matches)) continue;
            foreach (var candidate in matches)
            {
                // 処理条件 — Never infer a replacement from the original broadcast or an earlier airing.
                if (candidate.Start <= attempt.ScheduledStartTime) continue;
                var unique = episode + "|" + candidate.NetworkId + ":" + candidate.TransportStreamId
                    + ":" + candidate.ServiceId + ":" + candidate.EventNumber + ":" + candidate.Start.UtcTicks;
                if (!distinctCandidates.Add(unique)) continue;
                possible++;
                if (reported++ < 6)
                    WriteDeveloperLog($"RECOVERY_EPG_CANDIDATE sourceStation={attempt.ServiceName} sourceProgramme={attempt.ProgramTitle} sourceScheduled={attempt.ScheduledStartTime:yyyy-MM-dd HH:mm} candidateStation={candidate.ServiceName} candidateProgramme={candidate.Title} candidateStart={candidate.Start:yyyy-MM-dd HH:mm} identity={episode} status=UNVERIFIED noReservation=True noResolution=True");
            }
        }
        // 録画実績・復旧 — Separate coverage-only hints from exact single-episode candidates. This scan is bounded to
        // the already loaded future EPG snapshot and does not cross-join the full recording history with each
        // programme.
        var covered = 0;
        var coverageSamples = 0;
        var coveredUnique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            if (!TryRecoveryEpisodeKey(attempt.ProgramTitle, out var episode)) continue;
            var cut = episode.LastIndexOf('#');
            if (cut <= 0 || !int.TryParse(episode[(cut + 1)..], out var number)
                || !ranges.TryGetValue(episode[..cut], out var collection)) continue;
            foreach (var range in collection)
            {
                if (number < range.First || number > range.Last || range.Event.Start <= attempt.ScheduledStartTime) continue;
                var unique = episode + "|" + range.Event.NetworkId + ":" + range.Event.TransportStreamId
                    + ":" + range.Event.ServiceId + ":" + range.Event.EventNumber + ":" + range.Event.Start.UtcTicks;
                if (!coveredUnique.Add(unique)) continue;
                covered++;
                if (coverageSamples++ < 6)
                    WriteDeveloperLog($"RECOVERY_EPG_MULTIPART_SAMPLE sourceStation={attempt.ServiceName} sourceProgramme={attempt.ProgramTitle} sourceScheduled={attempt.ScheduledStartTime:yyyy-MM-dd HH:mm} candidateStation={range.Event.ServiceName} candidateProgramme={range.Event.Title} candidateStart={range.Event.Start:yyyy-MM-dd HH:mm} includedEpisode={number} status=COVERAGE_HINT_ONLY qualityPerEpisode=UNKNOWN noReservation=True noResolution=True");
            }
        }
        WriteDeveloperLog($"RECOVERY_EPG_MULTIPART result=OBSERVED indexedRangeProgrammes={ranges.Values.Sum(x => x.Count)} indexedStems={ranges.Count} coverageHints={covered} action=diagnostic_only automaticReservation=False recoveryMutation=False");
        WriteDeveloperLog($"RECOVERY_EPG_INDEX result=OBSERVED recoveryAttempts={journalCount} lookupSelection={(usedVerifiedLookup ? "verified" : "canonical_fallback")} identified={identified} unresolved={unresolved} futureEvents={futureEvents.Count} indexedEvents={indexedEvents} indexedEpisodeKeys={byEpisode.Count} possibleCandidates={possible} sampleLimit=6 scope=snapshot_only matching=exact_episode_stem_number ambiguity=defer automaticReservation=False recoveryMutation=False preferenceMutation=False");
#endif
    }

    // 処理条件 — Index-only startup: all canonical month bytes are SHA-256-checked before accepting an index; no
    // old month is deserialized into a long-lived object. Any mismatch returns to the complete canonical
    // loader (which is still the authoritative path).
    private static bool TryLoadVerifiedRecoveryManifest(ITvAirPluginRuntimeContext context)
    {
        try
        {
            var result = context.Storage.Get(RecoveryLookupStorageNamespace, RecoveryLookupStorageKey);
            if (!result.Succeeded || result.Value?.Value is null) return false;
            var manifest = JsonSerializer.Deserialize<AIrhythmRecoveryLookupManifest>(result.Value.Value.ToString() ?? "", JsonOptions);
            if (manifest?.SchemaVersion != 2 || manifest.Entries is null || manifest.Months is null) return false;
            var allMonths = context.Storage.ListKeys(RecoveryAttemptStorageNamespace).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var expectedMonths = manifest.Months.OrderBy(x => x.Month, StringComparer.Ordinal).ToArray();
            if (allMonths.Length != expectedMonths.Length ||
                !allMonths.SequenceEqual(expectedMonths.Select(x => x.Month), StringComparer.Ordinal)) return false;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Identity) || !ids.Add(entry.Identity) ||
                    !allMonths.Contains(entry.Month, StringComparer.Ordinal)) return false;
            }
            if (manifest.Entries.Length != expectedMonths.Sum(x => x.RowCount)) return false;
            foreach (var month in expectedMonths)
            {
                if (month.RowCount < 0 || month.Sha256?.Length != 64 ||
                    manifest.Entries.Count(e => e.Month == month.Month) != month.RowCount) return false;
                var fetched = context.Storage.Get(RecoveryAttemptStorageNamespace, month.Month);
                if (!fetched.Succeeded || fetched.Value?.Value is null) return false;
                var raw = fetched.Value.Value.ToString() ?? "";
                var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
                if (!string.Equals(actual, month.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                // 録画実績・復旧 — A fingerprint only authenticates the canonical bytes named by the manifest.  It does NOT
                // authenticate the manifest's own entry IDs. Check IDs against the authoritative month without retaining
                // the recording-attempt objects after startup.
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
                var canonicalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object ||
                        !row.TryGetProperty("identity", out var identityValue) ||
                        identityValue.ValueKind != JsonValueKind.String) return false;
                    var identity = identityValue.GetString();
                    if (string.IsNullOrWhiteSpace(identity) || !canonicalIds.Add(identity)) return false;
                }
                if (canonicalIds.Count != month.RowCount ||
                    manifest.Entries.Where(e => e.Month == month.Month)
                        .Any(e => !canonicalIds.Contains(e.Identity))) return false;
            }
            _verifiedRecoveryLookup = manifest.Entries;
            _recoveryLookupChecked = true;
            _recoveryLazyIndexed = true;
            _recoveryIndexedTotal = manifest.Entries.Length;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"RECOVERY_LAZY_STARTUP result=VERIFIED_INDEX_ONLY indexedAttempts={manifest.Entries.Length} fingerprintMonths={expectedMonths.Length} canonicalObjects=0 hash=SHA256 fallback=all_months_on_index_failure");
#endif
            return true;
        }
        catch (Exception) { return false; }
    }

    private static Dictionary<string, AIrhythmRecoveryAttempt> ReadRecoveryMonth(ITvAirPluginRuntimeContext context, string month)
    {
        var fetched = context.Storage.Get(RecoveryAttemptStorageNamespace, month);
        if (!fetched.Succeeded || fetched.Value?.Value is null)
            throw new InvalidOperationException("Recovery month read failed");
        var rows = JsonSerializer.Deserialize<AIrhythmRecoveryAttempt[]>(fetched.Value.Value.ToString() ?? "", JsonOptions)
            ?? throw new InvalidOperationException("Recovery month invalid");
        var bucket = new Dictionary<string, AIrhythmRecoveryAttempt>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Identity) || !bucket.TryAdd(row.Identity, row))
                throw new InvalidOperationException("Recovery month contains invalid or duplicate identity");
        }
        return bucket;
    }

    private static void MergeRecoveryAttempts(ITvAirPluginRuntimeContext context,
        IReadOnlyList<TvAirRecordingHistoryDto> hostHistory,
        IReadOnlyList<TvAirRecordingHistoryDto> durableHistory)
    {
        var previouslyPersisted = 0;
        lock (Gate)
        {
            if (!_recoveryAttemptsLoaded)
            {
                // 保存・読込 — Mark loaded only after trying the entire namespace; errors must not silently replace persisted
                // journal records with an empty cache.
                try
                {
                    if (TryLoadVerifiedRecoveryManifest(context))
                    {
                        _recoveryAttemptsLoaded = true;
                        previouslyPersisted = _recoveryIndexedTotal;
                    }
                    else
                    {
                    foreach (var month in context.Storage.ListKeys(RecoveryAttemptStorageNamespace))
                    {
                        var result = context.Storage.Get(RecoveryAttemptStorageNamespace, month);
                        if (!result.Succeeded || result.Value is null)
                            throw new InvalidOperationException("Recovery journal read failed");
                        RecoveryMonthSourceRevisions.Add(month, result.Value.Revision);
                        var rows = JsonSerializer.Deserialize<AIrhythmRecoveryAttempt[]>(result.Value.Value?.ToString() ?? "", JsonOptions)
                            ?? throw new InvalidOperationException("Recovery journal invalid");
                        foreach (var row in rows)
                        {
                            if (string.IsNullOrWhiteSpace(row.Identity))
                                throw new InvalidOperationException("Recovery journal identity missing");
                            var rowMonth = row.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                            if (rowMonth != month)
                                throw new InvalidOperationException("Recovery journal row stored in wrong month");
                            var identity = CanonicalRecoveryAttemptIdentity(row);
                            var canonical = row.Identity == identity ? row : row with { Identity = identity };
                            // Host連携 — Never replace a historical attempt when an old Host ID happens to recur in another
                            // broadcast/month.
                            if (!RecoveryAttempts.TryAdd(identity, canonical))
                                throw new InvalidOperationException("Distinct recovery attempts share a canonical identity");
                            if (!RecoveryAttemptsByMonth.TryGetValue(rowMonth, out var bucket))
                                RecoveryAttemptsByMonth[rowMonth] = bucket = new Dictionary<string, AIrhythmRecoveryAttempt>(StringComparer.OrdinalIgnoreCase);
                            bucket.Add(identity, canonical);
                            if (row.Identity != identity)
                                _recoveryDirtyMonths.Add(rowMonth);
                        }
                    }
                    _recoveryAttemptsLoaded = true;
                    previouslyPersisted = RecoveryAttempts.Count;
                    }
                }
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                catch (Exception ex)
                #else
                catch (Exception)
                #endif
                {
                    RecoveryAttempts.Clear();
                    RecoveryAttemptsByMonth.Clear();
                    RecoveryMonthSourceRevisions.Clear();
                    VerifiedLegacyRecoveryAliases.Clear();
                    _recoveryDirtyMonths.Clear();
                    #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                    WriteDeveloperLog($"RECOVERY_JOURNAL result=LOAD_ERROR type={ex.GetType().Name} action=skip_mutation");
                    #endif
                    return;
                }
            }
        }
        var updatedMonths = new HashSet<string>(StringComparer.Ordinal);
        lock (Gate) updatedMonths.UnionWith(_recoveryDirtyMonths);
        var added = 0;
        // 推薦・学習 — Durable success-quality facts and still-visible unsuccessful Host records. Never write
        // failed/cancelled attempts into positive preference evidence.
        var candidates = durableHistory.Where(x => x.ResultFinalized && x.QualityDataAvailable &&
            ((x.DropCount ?? 0) > 0 || (x.ErrorCount ?? 0) > 0 || (x.ScrambleCount ?? 0) > 0))
            .Concat(hostHistory.Where(x => x.ResultFinalized && IsRecordingFailureForRecovery(x)));
        lock (Gate)
        {
            foreach (var item in candidates)
            {
                if (string.IsNullOrWhiteSpace(item.ProgramTitle)) continue;
                var identity = UsageRecordingIdentity(item);
                if (string.IsNullOrWhiteSpace(identity)) continue;
                var row = new AIrhythmRecoveryAttempt(identity, item.ServiceName ?? "", item.ProgramTitle ?? "",
                    item.NetworkId, item.TransportStreamId, item.ServiceId, item.EventId,
                    item.ScheduledStartTime, item.Start, item.End,
                    item.Result ?? "", item.EndReason ?? "", item.FileCreated,
                    item.DropCount, item.ErrorCount, item.ScrambleCount,
                    item.QualityDataAvailable, item.QualitySource ?? "");
                if (RecoveryAttempts.ContainsKey(identity) ||
                    (_recoveryLazyIndexed && (VerifiedLegacyRecoveryAliases.Contains(identity) ||
                        _verifiedRecoveryLookup.Any(e => string.Equals(e.Identity, identity, StringComparison.OrdinalIgnoreCase))))) continue;
                var rowMonth = row.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                if (_recoveryLazyIndexed && !RecoveryAttemptsByMonth.ContainsKey(rowMonth))
                {
                    try
                    {
                        var months = context.Storage.ListKeys(RecoveryAttemptStorageNamespace);
                        var historical = months.Contains(rowMonth, StringComparer.Ordinal)
                            ? ReadRecoveryMonth(context, rowMonth)
                            : new Dictionary<string, AIrhythmRecoveryAttempt>(StringComparer.OrdinalIgnoreCase);
                        var expected = _verifiedRecoveryLookup.Where(e => e.Month == rowMonth)
                            .Select(e => e.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (historical.Count != expected.Count ||
                            historical.Keys.Any(id => !expected.Contains(id)) ||
                            historical.Values.Any(r => r.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture) != rowMonth))
                            throw new InvalidOperationException("Recovery write-month content differs from verified index");
                        RecoveryAttemptsByMonth[rowMonth] = historical;
                    }
                    #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                    catch (Exception ex)
                    #else
                    catch (Exception)
                    #endif
                    {
                        // 処理条件 — A failed read is NOT a dirty writable month. Retrying by writing an absent bucket would replace
                        // historical records with [].
                        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                        WriteDeveloperLog($"RECOVERY_JOURNAL result=LOAD_ERROR month={rowMonth} type={ex.GetType().Name} action=skip_month_preserve_pending");
                        #endif
                        // 録画実績・復旧 — Continue processing other months and pending dirty writes. A read failure for one month must
                        // not discard unrelated recording facts.
                        continue;
                    }
                }
                // 録画実績・復旧 — Historic recovery months can still hold an unscoped ID. Never double-count the same
                // failed/quality-impaired recording; unlike the ID alone, a different broadcast slot is distinct.
                var legacyIdentity = LegacyRecoveryIdentity(item);
                if (RecoveryAttemptsByMonth.TryGetValue(rowMonth, out var legacyBucket)
                    && legacyBucket.TryGetValue(legacyIdentity, out var legacyAttempt)
                    && IsSameLegacyRecoveryAttempt(legacyAttempt, item))
                {
                    // 処理条件 — The original month was fully read and verified against the authoritative manifest. Never
                    // rehydrate it every refresh.
                    if (_recoveryLazyIndexed)
                        VerifiedLegacyRecoveryAliases.Add(identity);
                    continue;
                }
                RecoveryAttempts.Add(identity, row);
                if (!RecoveryAttemptsByMonth.TryGetValue(rowMonth, out var bucket))
                    RecoveryAttemptsByMonth[rowMonth] = bucket = new Dictionary<string, AIrhythmRecoveryAttempt>(StringComparer.OrdinalIgnoreCase);
                bucket.Add(identity, row);
                updatedMonths.Add(rowMonth);
                added++;
            }
        }
        var verified = 0;
        foreach (var month in updatedMonths)
        {
            AIrhythmRecoveryAttempt[] rows;
            lock (Gate)
            {
                // 処理条件 — A retry must never synthesize an empty replacement for an unreadable month; only an explicitly
                // loaded bucket is writable.
                if (!RecoveryAttemptsByMonth.TryGetValue(month, out var bucket))
                {
                    #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                    WriteDeveloperLog($"RECOVERY_JOURNAL result=WRITE_DEFERRED month={month} reason=canonical_bucket_not_loaded action=preserve_original");
                    #endif
                    continue;
                }
                rows = bucket.Values.OrderBy(x => x.Start).ThenBy(x => x.Identity, StringComparer.Ordinal).ToArray();
            }
            try
            {
                var payload = JsonSerializer.Serialize(rows, JsonOptions);
                var alreadyCommitted = false;
                long? expectedCanonicalRevision = null;
                if (_recoveryLazyIndexed)
                {
                    var old = context.Storage.Get(RecoveryLookupStorageNamespace, RecoveryLookupStorageKey);
                    var baseManifest = old.Succeeded && old.Value?.Value is not null
                        ? JsonSerializer.Deserialize<AIrhythmRecoveryLookupManifest>(old.Value.Value.ToString() ?? "", JsonOptions)
                        : null;
                    if (baseManifest?.SchemaVersion != 2 || baseManifest.Months is null)
                        throw new InvalidOperationException("Recovery prewrite manifest unreadable");
                    var oldFingerprint = baseManifest.Months.FirstOrDefault(x => x.Month == month);
                    var canonical = context.Storage.Get(RecoveryAttemptStorageNamespace, month);
                    if (oldFingerprint is null)
                    {
                        if (context.Storage.Exists(RecoveryAttemptStorageNamespace, month))
                        {
                            // 処理条件 — A concurrent month must match our intended payload byte-for-byte.
                            if (!canonical.Succeeded || canonical.Value?.Value is null ||
                                !string.Equals(canonical.Value.Value.ToString(), payload, StringComparison.Ordinal))
                                throw new InvalidOperationException("Recovery prewrite unexpected existing month");
                            alreadyCommitted = true;
                        }
                    }
                    else
                    {
                        if (!canonical.Succeeded || canonical.Value?.Value is null)
                            throw new InvalidOperationException("Recovery prewrite canonical unreadable");
                        // 保存・読込 — Bind a read/modify/write to the storage revision. A checksum validated BEFORE Set alone cannot
                        // prevent a concurrent update.
                        expectedCanonicalRevision = canonical.Value.Revision;
                        var canonicalText = canonical.Value.Value.ToString() ?? "";
                        var canonicalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText)));
                        if (!string.Equals(canonicalHash, oldFingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            // 外部情報 — The previous pass may have committed this month before another month failed, leaving only the
                            // expendable manifest out of date. Never treat arbitrary external mutations as successful writes.
                            if (!string.Equals(canonicalText, payload, StringComparison.Ordinal))
                                throw new InvalidOperationException("Recovery prewrite canonical fingerprint changed");
                            alreadyCommitted = true;
                        }
                    }
                }
                if (!_recoveryLazyIndexed)
                {
                    if (RecoveryMonthSourceRevisions.TryGetValue(month, out var recordedRevision))
                    {
                        var currentMonth = context.Storage.Get(RecoveryAttemptStorageNamespace, month);
                        if (!currentMonth.Succeeded || currentMonth.Value is null || currentMonth.Value.Revision != recordedRevision)
                            throw new InvalidOperationException("Recovery month changed since load");
                        expectedCanonicalRevision = recordedRevision;
                    }
                    else if (context.Storage.Exists(RecoveryAttemptStorageNamespace, month))
                        throw new InvalidOperationException("Unloaded recovery month cannot be overwritten");
                }
                if (!alreadyCommitted)
                {
                var written = context.Storage.Set(RecoveryAttemptStorageNamespace, month, payload, expectedRevision: expectedCanonicalRevision);
                if (!written.Succeeded) throw new InvalidOperationException("Recovery journal write failed");
// 保存・読込 — Persistence safety is a production invariant, not a Developer-only diagnostic. A successful
// Set without verified readback must never retire a dirty month or allow the lookup to claim that the
// write was committed.
                var checkedValue = context.Storage.Get(RecoveryAttemptStorageNamespace, month);
                if (!checkedValue.Succeeded || checkedValue.Value is null ||
                    !string.Equals(checkedValue.Value.Value?.ToString(), payload, StringComparison.Ordinal))
                    throw new InvalidOperationException("Recovery journal readback mismatch");
                if (!_recoveryLazyIndexed)
                    RecoveryMonthSourceRevisions[month] = checkedValue.Value.Revision;
                }
                verified++;
                lock (Gate) _recoveryDirtyMonths.Remove(month);
            }
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            catch (Exception ex)
            #else
            catch (Exception)
            #endif
            {
                #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"RECOVERY_JOURNAL result=WRITE_ERROR month={month} type={ex.GetType().Name} action=keep_memory_retry_on_next_snapshot");
                #endif
                // 保存・読込 — Retain candidates in memory. Next snapshot retries this month.
                lock (Gate) _recoveryDirtyMonths.Add(month);
            }
        }
        // 外部情報 — Sidecar is diagnostic-only and expendable. Never delete or rewrite an original journal based on
        // a missing, damaged or obsolete lookup. Rebuild from authoritative month buckets. The sidecar must
        // never claim facts that failed to commit to the authoritative months.
        bool recoveryJournalClean;
        lock (Gate) recoveryJournalClean = _recoveryDirtyMonths.Count == 0;
        if (recoveryJournalClean && (!_recoveryLookupChecked || updatedMonths.Count != 0))
            VerifyRecoveryLookupSidecar(context, updatedMonths.Count != 0);
        if (_recoveryLazyIndexed && recoveryJournalClean && _recoveryLookupChecked)
        {
            lock (Gate)
            {
                // 作品識別 — The verified sidecar now contains every committed identity. Retire materialized month objects;
                // keep canonical bytes on disk.
                RecoveryAttemptsByMonth.Clear();
                RecoveryAttempts.Clear();
            }
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        int total;
        lock (Gate) total = _recoveryLazyIndexed ? _recoveryIndexedTotal + RecoveryAttempts.Values.Count(x => !_verifiedRecoveryLookup.Any(e => string.Equals(e.Identity, x.Identity, StringComparison.OrdinalIgnoreCase))) : RecoveryAttempts.Count;
        if (previouslyPersisted > 0 || added > 0)
        {
        var identified = 0;
        var unidentified = 0;
        var distinctEpisodeKeys = new HashSet<string>(StringComparer.Ordinal);
        AIrhythmRecoveryAttempt[] recentSamples;
        lock (Gate)
        {
            // 処理条件 — Sample only a bounded number; never log or scan an ever-growing full journal for each rendered
            // page. This first-load audit is deliberately Developer-only.
            recentSamples = RecoveryAttempts.Values.OrderByDescending(x => x.Start).Take(6).ToArray();
        }
        foreach (var sample in recentSamples)
        {
            var safe = TryRecoveryEpisodeKey(sample.ProgramTitle, out var episodeKey);
            if (safe && distinctEpisodeKeys.Add(episodeKey)) identified++; else unidentified++;
            WriteDeveloperLog($"RECOVERY_JOURNAL_SAMPLE station={sample.ServiceName} programme={sample.ProgramTitle} scheduled={sample.ScheduledStartTime:yyyy-MM-dd HH:mm} episodeIdentity={(safe ? episodeKey : "unresolved")} drop={sample.DropCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} cc={sample.ErrorCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} action=observe_only");
        }
        WriteDeveloperLog($"RECOVERY_JOURNAL_MONTH_INDEX result=OBSERVED months={(_recoveryLazyIndexed ? _verifiedRecoveryLookup.Select(x => x.Month).Distinct(StringComparer.Ordinal).Count() : RecoveryAttemptsByMonth.Count)} attempts={total} writeSelection=changed_month_bucket_only initialLoad={(_recoveryLazyIndexed ? "verified_index_only" : "all_months_fallback")} memoryOptimization={(_recoveryLazyIndexed ? "canonical_objects_on_demand" : "partial")} diskSchema=unchanged");
        WriteDeveloperLog($"RECOVERY_JOURNAL result=OBSERVED total={total} loadedOnThisCapture={previouslyPersisted} added={added} changedMonths={updatedMonths.Count} verifiedMonths={verified} sampleEpisodeIdentified={identified} sampleUnresolvedOrDuplicate={unidentified} retention=indefinite match=diagnostic_only automaticReservation=False preferenceMutation=False");
        }
#endif
    }

    private static void VerifyRecoveryLookupSidecar(ITvAirPluginRuntimeContext context, bool journalMonthChanged)
    {
        AIrhythmRecoveryLookupEntry[] entries;
        lock (Gate)
        {
            if (!_recoveryAttemptsLoaded) return;
            var savedEntries = _recoveryLazyIndexed ? _verifiedRecoveryLookup : Array.Empty<AIrhythmRecoveryLookupEntry>();
            var changedKeys = RecoveryAttemptsByMonth.Keys.ToHashSet(StringComparer.Ordinal);
            entries = savedEntries.Where(e => !changedKeys.Contains(e.Month))
                .Concat(RecoveryAttemptsByMonth
                    .SelectMany(pair => pair.Value.Values.Select(row =>
                        new AIrhythmRecoveryLookupEntry(row.Identity, pair.Key,
                            TryRecoveryEpisodeKey(row.ProgramTitle, out var key) ? key : string.Empty,
                            row.ScheduledStartTime))))
                .OrderBy(row => row.Month, StringComparer.Ordinal)
                .ThenBy(row => row.Identity, StringComparer.Ordinal).ToArray();
        }
        // 外部情報 — Schema 2 binds every monthly canonical payload to the expendable lookup. Checksum verification
        // is observational only: never repair or overwrite a source month. A future lazy loader must verify
        // these fingerprints before trusting index-only reads.
        AIrhythmRecoveryMonthFingerprint[] fingerprints;
        try
        {
            var monthGroups = entries.GroupBy(e => e.Month, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
            var priorManifestResult = _recoveryLazyIndexed
                ? context.Storage.Get(RecoveryLookupStorageNamespace, RecoveryLookupStorageKey)
                : null;
            var priorFingerprints = _recoveryLazyIndexed && priorManifestResult?.Succeeded == true
                ? JsonSerializer.Deserialize<AIrhythmRecoveryLookupManifest>(priorManifestResult.Value?.Value?.ToString() ?? "", JsonOptions)?.Months ?? Array.Empty<AIrhythmRecoveryMonthFingerprint>()
                : Array.Empty<AIrhythmRecoveryMonthFingerprint>();
            var checkedMonths = new List<AIrhythmRecoveryMonthFingerprint>(monthGroups.Length);
            foreach (var group in monthGroups)
            {
                var fetched = context.Storage.Get(RecoveryAttemptStorageNamespace, group.Key);
                if (!fetched.Succeeded || fetched.Value?.Value is null)
                    throw new InvalidOperationException("Recovery original month unreadable for fingerprint");
                var raw = fetched.Value.Value.ToString() ?? string.Empty;
                // 処理条件 — Match the canonical on-disk IDs, not merely the index's count.
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
                if (_recoveryLazyIndexed && !RecoveryAttemptsByMonth.ContainsKey(group.Key))
                {
                    var prior = priorFingerprints.FirstOrDefault(x => x.Month == group.Key);
                    if (prior is null || prior.RowCount != group.Count() ||
                        !string.Equals(prior.Sha256, digest, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Untouched recovery month fingerprint mismatch");
                    checkedMonths.Add(new AIrhythmRecoveryMonthFingerprint(group.Key, group.Count(), digest));
                    continue;
                }
                var persisted = JsonSerializer.Deserialize<AIrhythmRecoveryAttempt[]>(raw, JsonOptions)
                    ?? throw new InvalidOperationException("Recovery original month invalid for fingerprint");
                var persistedIds = persisted.Select(r => r.Identity)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (persisted.Length != group.Count() || persistedIds.Count != group.Count() ||
                    group.Any(e => !persistedIds.Contains(e.Identity)))
                    throw new InvalidOperationException("Recovery month differs from sidecar identity set");
                checkedMonths.Add(new AIrhythmRecoveryMonthFingerprint(group.Key, persisted.Length, digest));
            }
            fingerprints = checkedMonths.ToArray();
        }
        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
        #else
        catch (Exception)
        #endif
        {
            lock (Gate) { _recoveryLookupChecked = false; }
            #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"RECOVERY_LOOKUP_FINGERPRINT result=DEFERRED type={ex.GetType().Name} action=preserve_canonical");
            #endif
            return;
        }
        var expected = JsonSerializer.Serialize(new AIrhythmRecoveryLookupManifest(2, entries, fingerprints), JsonOptions);
        var status = "matched";
        try
        {
            var existing = context.Storage.Get(RecoveryLookupStorageNamespace, RecoveryLookupStorageKey);
            if (existing.Succeeded && string.Equals(existing.Value?.Value?.ToString(), expected, StringComparison.Ordinal))
            {
                lock (Gate) { _verifiedRecoveryLookup = entries; _recoveryLookupChecked = true; _recoveryIndexedTotal = entries.Length; }
            }
            else
            {
                // 処理条件 — A read error is not a missing key: never overwrite a possibly intact sidecar.
                if (!existing.Succeeded && context.Storage.Exists(RecoveryLookupStorageNamespace, RecoveryLookupStorageKey))
                    throw new InvalidOperationException("Lookup sidecar read failed");
                // 処理条件 — The sidecar is expendable but a stale rebuild must not overwrite a concurrent newer index.
                // Reuse the revision read immediately above. An absent key has no SDK-guaranteed create-if-absent
                // operation.
                var expectedLookupRevision = existing.Succeeded && existing.Value is not null
                    ? existing.Value.Revision : (long?)null;
                var written = context.Storage.Set(RecoveryLookupStorageNamespace, RecoveryLookupStorageKey,
                    expected, expectedRevision: expectedLookupRevision);
                if (!written.Succeeded) throw new InvalidOperationException("Lookup sidecar revision conflict or write failed");
                var readback = context.Storage.Get(RecoveryLookupStorageNamespace, RecoveryLookupStorageKey);
                if (!readback.Succeeded || !string.Equals(readback.Value?.Value?.ToString(), expected, StringComparison.Ordinal))
                    throw new InvalidOperationException("Lookup sidecar readback failed");
                status = "rebuilt_verified";
                lock (Gate) { _verifiedRecoveryLookup = entries; _recoveryLookupChecked = true; _recoveryIndexedTotal = entries.Length; }
            }
        }
        catch (Exception ex)
        {
            status = "deferred_" + ex.GetType().Name;
            lock (Gate) { _recoveryLookupChecked = false; }
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"RECOVERY_LOOKUP_FINGERPRINT result={(status == "matched" || status == "rebuilt_verified" ? "VERIFIED" : "DEFERRED")} months={fingerprints.Length} rows={fingerprints.Sum(x => x.RowCount)} hash=SHA256 canonicalMutation=False lazyLoad={_recoveryLazyIndexed}");
        WriteDeveloperLog($"RECOVERY_LOOKUP_SIDECAR result={status} schema=2 indexedAttempts={entries.Length} identified={entries.Count(x => x.EpisodeKey.Length != 0)} unresolved={entries.Count(x => x.EpisodeKey.Length == 0)} months={entries.Select(x => x.Month).Distinct(StringComparer.Ordinal).Count()} source=authoritative_recovery_months changedJournalMonth={journalMonthChanged} journalMutation=False reservationMutation=False lazyLoad={(_recoveryLazyIndexed ? "verified_index_only" : "fallback_all_months")} epgSelection=verified_sidecar_identity_or_full_fallback");
#endif
    }

    private static int MergeUsefulRecordingFacts(
        ITvAirPluginRuntimeContext context,
        IEnumerable<TvAirRecordingHistoryDto> source,
        IReadOnlyList<TvAirReservationDto>? reservationRecords = null)
    {
        if (!EnsureRecordingFactsLoaded(context))
            return 0;
        var resetCutoff = GetDataResetCutoff(context);
        // Reservation IDs are not globally unique. Verify the original service and broadcast slot before
        // attaching any source; a same-ID programme from a later week is not a match.
        var reservationsById = (reservationRecords ?? Array.Empty<TvAirReservationDto>())
            .Where(x => !string.IsNullOrWhiteSpace(x.ReservationId))
            .GroupBy(x => x.ReservationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var changedMonths = new HashSet<string>(StringComparer.Ordinal);
        var changed = 0;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var metadataConflicts = 0;
        AIrhythmRecordingFact? firstProtectedFact = null;
        AIrhythmRecordingFact? firstRejectedFact = null;
#endif
        lock (Gate)
        {
            foreach (var item in source)
            {
                if (!IsUsefulHistory(item))
                    continue;
                if (resetCutoff is not null && (item.ActualStart ?? item.Start) < resetCutoff.Value)
                    continue;
                TvAirReservationDto? matchingReservation = null;
                if (!string.IsNullOrWhiteSpace(item.ReservationId)
                    && reservationsById.TryGetValue(item.ReservationId, out var candidates))
                    matchingReservation = candidates.FirstOrDefault(x => RecordingBelongsToReservationOccurrence(item, x));
                var fact = ToRecordingFact(item, matchingReservation);
                if (fact.Identity.Length == 0)
                    continue;
                RecordingFacts.TryGetValue(fact.Identity, out var existing);
                if (existing is not null)
                {
                    if (HasRecordingFactMetadataConflict(existing, fact))
                    {
                        // 録画実績・復旧 — Never import an apparently unrelated programme into the month, quality history or
                        // recommendation evidence.
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                        metadataConflicts++;
                        firstProtectedFact ??= existing;
                        firstRejectedFact ??= fact;
#endif
                        continue;
                    }
                    fact = MergeRecordingFactMetadata(existing, fact);
                }
                if (existing == fact)
                    continue;
                if (existing is not null)
                    changedMonths.Add(RecordingFactMonthKey(existing));
                RecordingFacts[fact.Identity] = fact;
                _recordingFactsGeneration++;
                changedMonths.Add(RecordingFactMonthKey(fact));
                changed++;
            }
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        if (metadataConflicts > 0 && firstProtectedFact is not null && firstRejectedFact is not null)
            WriteDeveloperLog($"RECORDING_FACT_METADATA_CONFLICT result=PROTECTED count={metadataConflicts} savedStation={firstProtectedFact.ServiceName} savedProgramme={firstProtectedFact.ProgramTitle} incomingStation={firstRejectedFact.ServiceName} incomingProgramme={firstRejectedFact.ProgramTitle} scheduled={firstProtectedFact.ScheduledStartTime:yyyy-MM-dd HH:mm} storageMutation=blocked recommendationMutation=blocked policy=preserve_first_persisted_fact");
#endif
        PersistRecordingFactChunks(context, changedMonths);
        return changed;
    }

    internal static bool TryGetRecordedPreferenceOrigin(
        TvAirRecordingHistoryDto recording, out TvAirReservationIntent intent, out string source)
    {
        intent = TvAirReservationIntent.Unspecified;
        source = string.Empty;
        lock (Gate)
        {
            if (!RecordingFacts.TryGetValue(UsageRecordingIdentity(recording), out var fact)
                || !fact.ReservationOriginKnown)
                return false;
            intent = fact.ReservationIntent;
            source = fact.ReservationSource;
            return true;
        }
    }

    private static IReadOnlyList<TvAirRecordingHistoryDto> GetRecordingFactHistory(
        ITvAirPluginRuntimeContext context,
        IEnumerable<TvAirRecordingHistoryDto> hostHistory,
        IReadOnlyList<TvAirReservationDto> reservationRecords,
        out int seeded)
    {
        var latest = hostHistory.ToArray();
        seeded = MergeUsefulRecordingFacts(context, latest, reservationRecords);
        bool loaded;
        TvAirRecordingHistoryDto[] facts;
        lock (Gate)
        {
            loaded = _recordingFactsLoaded;
            facts = loaded ? RecordingFacts.Values.Select(FromRecordingFact).ToArray() : Array.Empty<TvAirRecordingHistoryDto>();
        }
        if (loaded)
        {
            // 録画実績・復旧 — Retry an incomplete save even when no new recording facts have arrived.
            PersistRecordingFactChunks(context, Array.Empty<string>());
            ReconcileFinalizedRecordingUsage(context, latest.Concat(facts), "history_snapshot");
            return facts.OrderByDescending(x => x.ActualStart ?? x.Start).ToArray();
        }
        ReconcileFinalizedRecordingUsage(context, latest, "history_snapshot");
        return latest.Where(IsUsefulHistory).OrderByDescending(x => x.ActualStart ?? x.Start).ToArray();
    }

    private static bool RememberUsageIdentity(HashSet<string> set, Queue<string> order, string identity)
    {
        if (string.IsNullOrWhiteSpace(identity) || !set.Add(identity))
            return false;
        order.Enqueue(identity);
        while (order.Count > UsageRecentIdentityLimit)
        {
            var removed = order.Dequeue();
            set.Remove(removed);
        }
        return true;
    }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
    internal static void ReportEvidenceCalibrationMemory()
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null) return;
        AIrhythmEvidenceCalibrationState state = new(EvidenceCalibrationSchemaVersion, 0, DateTimeOffset.MinValue, Array.Empty<string>(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        try
        {
            var stored = context.Storage.Get(LearnedUserModelStorageNamespace, EvidenceCalibrationStorageKey);
            if (stored.Succeeded && stored.Value is not null)
            {
                var json = stored.Value.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(json))
                    state = JsonSerializer.Deserialize<AIrhythmEvidenceCalibrationState>(json, JsonOptions) ?? state;
            }
        }
        catch { }
        if (state.SchemaVersion != EvidenceCalibrationSchemaVersion)
            state = new(EvidenceCalibrationSchemaVersion, 0, DateTimeOffset.MinValue, Array.Empty<string>(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        static double Mean(double total, long count) => count <= 0 ? 0.0d : total / count;
        var n = state.SampleCount;
        WriteDeveloperLog($"EVIDENCE_CALIBRATION_MEMORY result=OK samples={n} meanSelectedStrength=[identity:{Mean(state.IdentitySelectedStrengthTotal,n):0.000},content:{Mean(state.ContentSelectedStrengthTotal,n):0.000},context:{Mean(state.ContextSelectedStrengthTotal,n):0.000},explicit:{Mean(state.ExplicitSelectedStrengthTotal,n):0.000}] meanBackgroundStrength=[identity:{Mean(state.IdentityBackgroundStrengthTotal,n):0.000},content:{Mean(state.ContentBackgroundStrengthTotal,n):0.000},context:{Mean(state.ContextBackgroundStrengthTotal,n):0.000},explicit:{Mean(state.ExplicitBackgroundStrengthTotal,n):0.000}] meanDiscriminativeLift=[identity:{Mean(state.IdentitySelectedStrengthTotal-state.IdentityBackgroundStrengthTotal,n):0.000},content:{Mean(state.ContentSelectedStrengthTotal-state.ContentBackgroundStrengthTotal,n):0.000},context:{Mean(state.ContextSelectedStrengthTotal-state.ContextBackgroundStrengthTotal,n):0.000},explicit:{Mean(state.ExplicitSelectedStrengthTotal-state.ExplicitBackgroundStrengthTotal,n):0.000}] meanConfidence=[identity:{Mean(state.IdentityConfidenceTotal,n):0.000},content:{Mean(state.ContentConfidenceTotal,n):0.000},context:{Mean(state.ContextConfidenceTotal,n):0.000},explicit:{Mean(state.ExplicitConfidenceTotal,n):0.000}] target=positive_reservation_prechoice_only availabilityBaseline=current_scored_population targetLeakagePrevented=True negativePreferenceInference=False persistence=plugin_storage shadowOnly=True productionMutation=False");
    }

    private static void ObserveEvidenceCalibrationFromRuntimeEvent(ITvAirPluginRuntimeContext context, string eventType, PluginEventEnvelope envelope, TvAirReservationDto? resolvedAddedReservation)
    {
        if (!string.Equals(eventType, "ReservationAdded", StringComparison.OrdinalIgnoreCase))
            return;
        var reservation = resolvedAddedReservation;
        if (reservation is null || string.IsNullOrWhiteSpace(reservation.ReservationId))
        {
            WriteDeveloperLog($"EVIDENCE_CALIBRATION_SAMPLE result=SKIPPED observation={envelope.EntityId ?? "-"} reason=reservation_payload_unresolved targetLeakagePrevented=True nonSelectionNegative=False scoreMutation=False");
            return;
        }
        var observationId = $"reservation:{reservation.ReservationId.Trim()}";
        if (!AIrhythmRecommendationEngine.TryGetPreChoiceCalibrationCandidate(reservation, out var candidate))
        {
            WriteDeveloperLog($"EVIDENCE_CALIBRATION_SAMPLE result=SKIPPED observation={observationId} reason=no_prechoice_scored_candidate targetLeakagePrevented=True nonSelectionNegative=False scoreMutation=False");
            return;
        }

        AIrhythmEvidenceCalibrationState state = new(
            EvidenceCalibrationSchemaVersion, 0, DateTimeOffset.MinValue, Array.Empty<string>(),
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        try
        {
            var stored = context.Storage.Get(LearnedUserModelStorageNamespace, EvidenceCalibrationStorageKey);
            if (stored.Succeeded && stored.Value is not null)
            {
                var json = stored.Value.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(json))
                    state = JsonSerializer.Deserialize<AIrhythmEvidenceCalibrationState>(json, JsonOptions) ?? state;
            }
        }
        catch { }
        if (state.SchemaVersion != EvidenceCalibrationSchemaVersion)
            state = new(EvidenceCalibrationSchemaVersion, 0, DateTimeOffset.MinValue, Array.Empty<string>(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var seen = new HashSet<string>(state.SeenObservationIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (!seen.Add(observationId))
        {
            WriteDeveloperLog($"EVIDENCE_CALIBRATION_SAMPLE result=SKIPPED observation={observationId} reason=already_seen targetLeakagePrevented=True nonSelectionNegative=False scoreMutation=False");
            return;
        }
        var now = DateTimeOffset.Now;
        var groups = candidate.Groups;
        var nextSeen = seen.TakeLast(EvidenceCalibrationSeenLimit).ToArray();
        var next = state with
        {
            SampleCount = state.SampleCount + 1,
            UpdatedAt = now,
            SeenObservationIds = nextSeen,
            IdentitySelectedStrengthTotal = state.IdentitySelectedStrengthTotal + groups.IdentityStrength,
            ContentSelectedStrengthTotal = state.ContentSelectedStrengthTotal + groups.ContentStrength,
            ContextSelectedStrengthTotal = state.ContextSelectedStrengthTotal + groups.ContextStrength,
            ExplicitSelectedStrengthTotal = state.ExplicitSelectedStrengthTotal + groups.ExplicitStrength,
            IdentityConfidenceTotal = state.IdentityConfidenceTotal + groups.IdentityConfidence,
            ContentConfidenceTotal = state.ContentConfidenceTotal + groups.ContentConfidence,
            ContextConfidenceTotal = state.ContextConfidenceTotal + groups.ContextConfidence,
            ExplicitConfidenceTotal = state.ExplicitConfidenceTotal + groups.ExplicitConfidence,
            IdentityBackgroundStrengthTotal = state.IdentityBackgroundStrengthTotal + candidate.BackgroundIdentityStrength,
            ContentBackgroundStrengthTotal = state.ContentBackgroundStrengthTotal + candidate.BackgroundContentStrength,
            ContextBackgroundStrengthTotal = state.ContextBackgroundStrengthTotal + candidate.BackgroundContextStrength,
            ExplicitBackgroundStrengthTotal = state.ExplicitBackgroundStrengthTotal + candidate.BackgroundExplicitStrength
        };
        try
        {
            var written = context.Storage.Set(LearnedUserModelStorageNamespace, EvidenceCalibrationStorageKey, JsonSerializer.Serialize(next, JsonOptions), expectedRevision: null);
            if (!written.Succeeded)
            {
                WriteDeveloperLog($"EVIDENCE_CALIBRATION_SAMPLE result=SAVE_ERROR observation={observationId} samples={state.SampleCount} targetLeakagePrevented=True scoreMutation=False");
                return;
            }
        }
        catch (Exception ex)
        {
            WriteDeveloperLog($"EVIDENCE_CALIBRATION_SAMPLE result=SAVE_ERROR observation={observationId} type={ex.GetType().Name} samples={state.SampleCount} targetLeakagePrevented=True scoreMutation=False");
            return;
        }
        static double Mean(double total, long count) => count <= 0 ? 0.0d : total / count;
        var n = next.SampleCount;
        WriteDeveloperLog($"EVIDENCE_CALIBRATION_SAMPLE result=OBSERVED observation={observationId} candidates={candidate.CandidateCount} selectedStrength=[identity:{groups.IdentityStrength:0.000},content:{groups.ContentStrength:0.000},context:{groups.ContextStrength:0.000},explicit:{groups.ExplicitStrength:0.000}] confidence=[identity:{groups.IdentityConfidence:0.000},content:{groups.ContentConfidence:0.000},context:{groups.ContextConfidence:0.000},explicit:{groups.ExplicitConfidence:0.000}] backgroundStrength=[identity:{candidate.BackgroundIdentityStrength:0.000},content:{candidate.BackgroundContentStrength:0.000},context:{candidate.BackgroundContextStrength:0.000},explicit:{candidate.BackgroundExplicitStrength:0.000}] lift=[identity:{groups.IdentityStrength-candidate.BackgroundIdentityStrength:0.000},content:{groups.ContentStrength-candidate.BackgroundContentStrength:0.000},context:{groups.ContextStrength-candidate.BackgroundContextStrength:0.000},explicit:{groups.ExplicitStrength-candidate.BackgroundExplicitStrength:0.000}] provenance=[identity:{groups.IdentityProvenance},content:{groups.ContentProvenance},context:{groups.ContextProvenance},explicit:{groups.ExplicitProvenance}] targetLeakagePrevented=prechoice_score_cache nonSelectionNegative=False scoreMutation=False");
        WriteDeveloperLog($"EVIDENCE_CALIBRATION_MEMORY result=OK samples={n} meanSelectedStrength=[identity:{Mean(next.IdentitySelectedStrengthTotal,n):0.000},content:{Mean(next.ContentSelectedStrengthTotal,n):0.000},context:{Mean(next.ContextSelectedStrengthTotal,n):0.000},explicit:{Mean(next.ExplicitSelectedStrengthTotal,n):0.000}] meanBackgroundStrength=[identity:{Mean(next.IdentityBackgroundStrengthTotal,n):0.000},content:{Mean(next.ContentBackgroundStrengthTotal,n):0.000},context:{Mean(next.ContextBackgroundStrengthTotal,n):0.000},explicit:{Mean(next.ExplicitBackgroundStrengthTotal,n):0.000}] meanDiscriminativeLift=[identity:{Mean(next.IdentitySelectedStrengthTotal-next.IdentityBackgroundStrengthTotal,n):0.000},content:{Mean(next.ContentSelectedStrengthTotal-next.ContentBackgroundStrengthTotal,n):0.000},context:{Mean(next.ContextSelectedStrengthTotal-next.ContextBackgroundStrengthTotal,n):0.000},explicit:{Mean(next.ExplicitSelectedStrengthTotal-next.ExplicitBackgroundStrengthTotal,n):0.000}] meanConfidence=[identity:{Mean(next.IdentityConfidenceTotal,n):0.000},content:{Mean(next.ContentConfidenceTotal,n):0.000},context:{Mean(next.ContextConfidenceTotal,n):0.000},explicit:{Mean(next.ExplicitConfidenceTotal,n):0.000}] interpretation=availability_adjusted_positive_selection_shadow negativePreferenceInference=False productionMutation=False");
    }
#endif

}
