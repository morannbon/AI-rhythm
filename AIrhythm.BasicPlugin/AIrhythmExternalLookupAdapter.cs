namespace AIrhythm.BasicPlugin;

internal static class AIrhythmExternalLookupAdapter
{
    public const string TvMazeProviderId = "tvmaze";

    public static TvAirExternalLookupCapabilityDto? GetCapability(ITvAirPluginRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try { return context.ExternalLookup.GetCapability(); }
        catch { return null; }
    }

    public static bool Supports(TvAirExternalLookupCapabilityDto? capability, string providerId, string operation)
        => capability is not null
            && capability.PluginDeclaredPermission
            && capability.UserAllowed
            && capability.Providers.Any(provider =>
                string.Equals(provider.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                && provider.Operations.Any(item => string.Equals(item, operation, StringComparison.OrdinalIgnoreCase)));

    internal sealed class ProviderRefreshState
    {
        private readonly ProviderCommunicationState _provider;

        internal ProviderRefreshState(
            ProviderCommunicationState provider,
            bool isOpen,
            int requestBudget)
        {
            _provider = provider;
            IsOpen = isOpen;
            RemainingRequestBudget = Math.Max(0, requestBudget);
        }

        internal bool IsOpen { get; private set; }
        internal int RemainingRequestBudget { get; private set; }
        internal string ProviderId => _provider.ProviderId;
        internal ProviderCommunicationState Provider => _provider;

        internal bool TryConsumeRequest()
        {
            if (!IsOpen || RemainingRequestBudget <= 0)
            {
                Stop();
                return false;
            }

            RemainingRequestBudget--;
            return true;
        }

        internal void Stop()
        {
            IsOpen = false;
        }
    }

    internal sealed record ExternalLookupRefreshState(ProviderRefreshState TvMaze);

    internal sealed class ProviderCommunicationState
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        internal ProviderCommunicationState(
            string providerId,
            TimeSpan minimumInterval,
            TimeSpan rateLimitCooldown,
            int requestBudgetPerRefresh)
        {
            ProviderId = providerId;
            MinimumInterval = minimumInterval;
            RateLimitCooldown = rateLimitCooldown;
            RequestBudgetPerRefresh = Math.Max(0, requestBudgetPerRefresh);
        }

        internal string ProviderId { get; }
        internal TimeSpan MinimumInterval { get; }
        internal TimeSpan RateLimitCooldown { get; }
        internal int RequestBudgetPerRefresh { get; }
        internal DateTimeOffset LastRequestAt { get; private set; } = DateTimeOffset.MinValue;
        internal DateTimeOffset NextAllowedAt { get; private set; } = DateTimeOffset.MinValue;
        internal DateTimeOffset CooldownUntilAt { get; private set; } = DateTimeOffset.MinValue;

        internal ProviderRefreshState BeginRefresh()
        {
            var now = DateTimeOffset.UtcNow;
            return new ProviderRefreshState(this, now >= CooldownUntilAt, RequestBudgetPerRefresh);
        }

        internal void StopAndEnterCooldown(ProviderRefreshState refreshState)
        {
            if (!ReferenceEquals(refreshState.Provider, this))
                throw new InvalidOperationException("External lookup refresh state/provider mismatch.");

            var now = DateTimeOffset.UtcNow;
            refreshState.Stop();
            CooldownUntilAt = now + RateLimitCooldown;
            if (CooldownUntilAt > NextAllowedAt)
                NextAllowedAt = CooldownUntilAt;
        }

        internal async Task<AIrhythmExternalEvidenceResult> ExecuteAsync(
            ProviderRefreshState refreshState,
            ITvAirPluginRuntimeContext context,
            TvAirExternalLookupCapabilityDto capability,
            string operation,
            IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken)
        {
            if (!ReferenceEquals(refreshState.Provider, this))
                throw new InvalidOperationException("External lookup refresh state/provider mismatch.");
            if (!refreshState.IsOpen)
                throw new InvalidOperationException($"External lookup provider circuit is closed for this refresh: {ProviderId}");

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now < CooldownUntilAt)
                {
                    refreshState.Stop();
                    throw new InvalidOperationException($"External lookup provider is cooling down: {ProviderId}");
                }

                var wait = NextAllowedAt - now;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

                if (!refreshState.TryConsumeRequest())
                    throw new InvalidOperationException($"External lookup provider request budget exhausted: {ProviderId}");

                var result = await LookupAsync(
                    context, capability, ProviderId, operation, parameters, cancellationToken).ConfigureAwait(false);

                LastRequestAt = DateTimeOffset.UtcNow;
                NextAllowedAt = LastRequestAt + MinimumInterval;
                if (result.Code == TvAirExternalLookupResultCode.RateLimited)
                {
                    refreshState.Stop();
                    CooldownUntilAt = LastRequestAt + RateLimitCooldown;
                    if (CooldownUntilAt > NextAllowedAt)
                        NextAllowedAt = CooldownUntilAt;
                }
                else if (refreshState.RemainingRequestBudget <= 0)
                {
                    refreshState.Stop();
                }

                return result;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    // 外部情報 — Provider communication policy is single-sourced here. Every operation for a provider shares the
    // same gate, timing state, refresh circuit, request budget and cross-refresh cooldown. External Evidence
    // is supplemental, so keep the existing safe pacing and bound total provider traffic well below the
    // candidate-selection count instead of tuning intervals toward an API limit.
    private static readonly ProviderCommunicationState TvMazeState = new(
        TvMazeProviderId,
        TimeSpan.FromMilliseconds(1250),
        TimeSpan.FromMinutes(15),
        requestBudgetPerRefresh: 16);
    internal static ExternalLookupRefreshState BeginRefresh()
        => new(TvMazeState.BeginRefresh());

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
    internal static TimeSpan TvMazeMinimumIntervalForDiagnostics => TvMazeState.MinimumInterval;
    internal static int TvMazeRequestBudgetForDiagnostics => TvMazeState.RequestBudgetPerRefresh;
    internal static TimeSpan TvMazeRateLimitCooldownForDiagnostics => TvMazeState.RateLimitCooldown;
#endif

    public static Task<AIrhythmExternalEvidenceResult> SearchTvMazeAsync(
        ProviderRefreshState refreshState,
        ITvAirPluginRuntimeContext context,
        TvAirExternalLookupCapabilityDto capability,
        string normalizedTitle,
        CancellationToken cancellationToken = default)
        => LookupProviderPacedAsync(refreshState, TvMazeState, context, capability, "SearchShow",
            new Dictionary<string, string> { ["query"] = NormalizeQuery(normalizedTitle, 160) }, cancellationToken);

    // 保存・読込 — GetEpisodes is an all-episodes response in the current Host contract. A show that exceeds the
    // Host body-size ceiling cannot become smaller by retrying the same request. Retain a bounded,
    // short-lived Host Storage backed suppression set; never alter Host response limits or use HTTP.
    // Serialize the check and call so overlapping refreshes cannot race through the same still-unknown
    // oversized show before its failure is remembered.
    private static readonly SemaphoreSlim EpisodesRequestGate = new(1, 1);
    private static readonly object OversizedEpisodesGate = new();
    private static readonly Dictionary<string, DateTimeOffset> OversizedEpisodeShows = new(StringComparer.Ordinal);
    private static readonly TimeSpan OversizedEpisodeSuppression = TimeSpan.FromDays(7);
    private const int OversizedEpisodeShowLimit = 256;
    private const string OversizedEpisodeStorageNamespace = AIrhythmDataState.ExternalLookupRuntimeStorageNamespace;
    internal const string OversizedEpisodeStorageKey = "OversizedTvMazeEpisodesV1";
    private static bool _oversizedEpisodeStoreLoaded;
    private static bool _oversizedEpisodeStoreWritable;
    private static int _oversizedEpisodeResetGeneration;

    // 保存・読込 — A failed storage read cannot authorize replacing saved suppression state.
    private static void LoadOversizedEpisodeShows(ITvAirPluginRuntimeContext context)
    {
        if (_oversizedEpisodeStoreLoaded) return;
        _oversizedEpisodeStoreLoaded = true;
        try
        {
            // 保存・読込 — A missing key is normal on first launch. Distinguish that from a failed read of an existing
            // key before allowing writes to this cache.
            if (!context.Storage.ListKeys(OversizedEpisodeStorageNamespace)
                .Contains(OversizedEpisodeStorageKey, StringComparer.Ordinal))
            {
                _oversizedEpisodeStoreWritable = true;
                return;
            }
            var stored = context.Storage.Get(OversizedEpisodeStorageNamespace, OversizedEpisodeStorageKey);
            if (!stored.Succeeded) return;
            var value = stored.Value?.Value?.ToString();
            var persisted = string.IsNullOrWhiteSpace(value)
                ? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(value);
            if (persisted is null) return;
            var now = DateTimeOffset.UtcNow;
            lock (OversizedEpisodesGate)
            {
                foreach (var item in persisted
                    .Where(item => item.Value > now && item.Key.Length > 0 && item.Key.Length <= 16
                        && item.Key.All(char.IsAsciiDigit))
                    .OrderByDescending(item => item.Value)
                    .Take(OversizedEpisodeShowLimit))
                    OversizedEpisodeShows[item.Key] = item.Value;
            }
            _oversizedEpisodeStoreWritable = true;
        }
        catch
        {
            // 保存・読込 — External evidence is optional. Retain safe local fallback and never write a replacement
            // suppression store when existing storage was unreadable.
        }
    }

    private static void PersistOversizedEpisodeShows(ITvAirPluginRuntimeContext context)
    {
        if (!_oversizedEpisodeStoreWritable) return;
        try
        {
            Dictionary<string, DateTimeOffset> snapshot;
            lock (OversizedEpisodesGate)
                snapshot = new Dictionary<string, DateTimeOffset>(OversizedEpisodeShows, StringComparer.Ordinal);
            context.Storage.Set(OversizedEpisodeStorageNamespace, OversizedEpisodeStorageKey,
                JsonSerializer.Serialize(snapshot), expectedRevision: null);
        }
        catch
        {
            // 保存・読込 — Runtime suppression remains valid even if optional persistence is unavailable.
        }
    }

    internal static void ResetOversizedEpisodeCache()
    {
        // Host連携 — A Host request in flight before a reset/restore must never repopulate the newly reset store
        // when it eventually returns.
        System.Threading.Interlocked.Increment(ref _oversizedEpisodeResetGeneration);
        lock (OversizedEpisodesGate)
        {
            OversizedEpisodeShows.Clear();
            _oversizedEpisodeStoreLoaded = false;
            _oversizedEpisodeStoreWritable = false;
        }
    }

    private static bool IsOversizedEpisodeShow(string showId)
    {
        lock (OversizedEpisodesGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in OversizedEpisodeShows.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
                OversizedEpisodeShows.Remove(expired);
            return OversizedEpisodeShows.ContainsKey(showId);
        }
    }

    private static void RememberOversizedEpisodeShow(string showId)
    {
        lock (OversizedEpisodesGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in OversizedEpisodeShows.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
                OversizedEpisodeShows.Remove(expired);
            if (OversizedEpisodeShows.Count >= OversizedEpisodeShowLimit && !OversizedEpisodeShows.ContainsKey(showId))
            {
                var oldest = OversizedEpisodeShows.OrderBy(item => item.Value).First().Key;
                OversizedEpisodeShows.Remove(oldest);
            }
            OversizedEpisodeShows[showId] = now + OversizedEpisodeSuppression;
        }
    }

    public static async Task<AIrhythmExternalEvidenceResult> GetTvMazeEpisodesAsync(
        ProviderRefreshState refreshState,
        ITvAirPluginRuntimeContext context,
        TvAirExternalLookupCapabilityDto capability,
        string showId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedShowId = NormalizePositiveInteger(showId);
        await EpisodesRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var observedResetGeneration = System.Threading.Volatile.Read(ref _oversizedEpisodeResetGeneration);
            LoadOversizedEpisodeShows(context);
            // 処理条件 — Recheck after acquiring the gate: another refresh or a previous process may already have
            // observed an oversized response for the same show.
            if (IsOversizedEpisodeShow(normalizedShowId))
                return new(false, TvAirExternalLookupResultCode.ResponseTooLarge, TvMazeProviderId, "GetEpisodes", string.Empty);

            var result = await LookupProviderPacedAsync(refreshState, TvMazeState, context, capability, "GetEpisodes",
                new Dictionary<string, string> { ["showId"] = normalizedShowId }, cancellationToken).ConfigureAwait(false);
            if (result.Code == TvAirExternalLookupResultCode.ResponseTooLarge
                && observedResetGeneration == System.Threading.Volatile.Read(ref _oversizedEpisodeResetGeneration))
            {
                RememberOversizedEpisodeShow(normalizedShowId);
                PersistOversizedEpisodeShows(context);
            }
            return result;
        }
        finally
        {
            EpisodesRequestGate.Release();
        }
    }

    public static Task<AIrhythmExternalEvidenceResult> GetTvMazeAliasesAsync(
        ProviderRefreshState refreshState,
        ITvAirPluginRuntimeContext context,
        TvAirExternalLookupCapabilityDto capability,
        string showId,
        CancellationToken cancellationToken = default)
        => LookupProviderPacedAsync(refreshState, TvMazeState, context, capability, "GetAliases",
            new Dictionary<string, string> { ["showId"] = NormalizePositiveInteger(showId) }, cancellationToken);


    public static IReadOnlyList<AIrhythmExternalEvidence> Normalize(
        AIrhythmExternalEvidenceResult result,
        string queryTitle)
    {
        if (!result.Success || string.IsNullOrWhiteSpace(result.Body))
            return Array.Empty<AIrhythmExternalEvidence>();

        try
        {
            return result.ProviderId.ToLowerInvariant() switch
            {
                TvMazeProviderId => NormalizeTvMaze(result, queryTitle),
                _ => Array.Empty<AIrhythmExternalEvidence>()
            };
        }
        catch
        {
            return Array.Empty<AIrhythmExternalEvidence>();
        }
    }

    private static Task<AIrhythmExternalEvidenceResult> LookupProviderPacedAsync(
        ProviderRefreshState refreshState,
        ProviderCommunicationState providerState,
        ITvAirPluginRuntimeContext context,
        TvAirExternalLookupCapabilityDto capability,
        string operation,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
        => providerState.ExecuteAsync(
            refreshState, context, capability, operation, parameters, cancellationToken);

    private static async Task<AIrhythmExternalEvidenceResult> LookupAsync(
        ITvAirPluginRuntimeContext context,
        TvAirExternalLookupCapabilityDto capability,
        string providerId,
        string operation,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Supports(capability, providerId, operation))
        {
            var code = !capability.PluginDeclaredPermission || !capability.UserAllowed
                ? TvAirExternalLookupResultCode.PermissionDenied
                : capability.Providers.Any(provider => string.Equals(provider.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                    ? TvAirExternalLookupResultCode.OperationNotAllowed
                    : TvAirExternalLookupResultCode.ProviderNotAllowed;
            return new(false, code, providerId, operation, string.Empty);
        }

        if (parameters.Values.Any(string.IsNullOrWhiteSpace))
            return new(false, TvAirExternalLookupResultCode.InvalidRequest, providerId, operation, string.Empty);

        try
        {
            var response = await context.ExternalLookup.LookupAsync(new TvAirExternalLookupRequestDto
            {
                ProviderId = providerId,
                Operation = operation,
                Parameters = parameters
            }, cancellationToken).ConfigureAwait(false);
            return new(response.Success, response.Code, response.ProviderId, response.Operation, response.Body);
        }
        catch (OperationCanceledException)
        {
            return new(false, TvAirExternalLookupResultCode.Cancelled, providerId, operation, string.Empty);
        }
        catch
        {
            return new(false, TvAirExternalLookupResultCode.ConnectionFailure, providerId, operation, string.Empty);
        }
    }

    private static IReadOnlyList<AIrhythmExternalEvidence> NormalizeTvMaze(AIrhythmExternalEvidenceResult result, string queryTitle)
    {
        using var document = JsonDocument.Parse(result.Body);
        if (string.Equals(result.Operation, "SearchShow", StringComparison.OrdinalIgnoreCase))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<AIrhythmExternalEvidence>();
            var list = new List<AIrhythmExternalEvidence>();
            foreach (var row in document.RootElement.EnumerateArray().Take(5))
            {
                if (!row.TryGetProperty("show", out var show) || show.ValueKind != JsonValueKind.Object)
                    continue;
                var id = ReadScalar(show, "id");
                var title = ReadString(show, "name");
                if (id.Length == 0 || title.Length == 0)
                    continue;
                var score = row.TryGetProperty("score", out var scoreNode) && scoreNode.TryGetDouble(out var parsedScore)
                    ? Math.Clamp(parsedScore, 0d, 1d)
                    : TitleSimilarity(queryTitle, title);
                list.Add(new AIrhythmExternalEvidence(
                    TvMazeProviderId,
                    id,
                    title,
                    Array.Empty<string>(),
                    "tv",
                    null,
                    null,
                    ParseDate(ReadString(show, "premiered")),
                    "candidate",
                    score,
                    DateTimeOffset.Now));
            }
            return list;
        }

        if (string.Equals(result.Operation, "GetEpisodes", StringComparison.OrdinalIgnoreCase))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<AIrhythmExternalEvidence>();
            return document.RootElement.EnumerateArray().Take(200).Select(item => new AIrhythmExternalEvidence(
                TvMazeProviderId,
                ReadScalar(item, "id"),
                ReadString(item, "name"),
                Array.Empty<string>(),
                "episode",
                ReadInt(item, "season"),
                ReadInt(item, "number"),
                ParseDate(ReadString(item, "airdate")),
                "episode",
                1d,
                DateTimeOffset.Now)).Where(item => item.EntityId.Length > 0).ToArray();
        }

        if (string.Equals(result.Operation, "GetAliases", StringComparison.OrdinalIgnoreCase))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<AIrhythmExternalEvidence>();
            return document.RootElement.EnumerateArray().Take(100).Select(item => new AIrhythmExternalEvidence(
                TvMazeProviderId,
                string.Empty,
                string.Empty,
                string.IsNullOrWhiteSpace(ReadString(item, "name")) ? Array.Empty<string>() : new[] { ReadString(item, "name") },
                "alias",
                null,
                null,
                null,
                "alias",
                1d,
                DateTimeOffset.Now)).Where(item => item.Aliases.Count > 0).ToArray();
        }

        return Array.Empty<AIrhythmExternalEvidence>();
    }



    private static string NormalizeQuery(string value, int maxLength)
    {
        var normalized = Regex.Replace((value ?? string.Empty).Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim();
        if (normalized.Length > maxLength)
            normalized = normalized[..maxLength].Trim();
        return normalized;
    }

    private static string NormalizePositiveInteger(string value)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    private static string ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;

    private static string ReadScalar(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static int? ReadInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.TryGetInt32(out var parsed)
                ? parsed
                : null;

    private static DateTimeOffset? ParseDate(string value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) ? parsed : null;

    internal static double GetTitleSimilarity(string left, string right)
        => TitleSimilarity(left, right);

    private static double TitleSimilarity(string left, string right)
    {
        static string Compact(string value) => new(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var a = Compact(left ?? string.Empty);
        var b = Compact(right ?? string.Empty);
        if (a.Length == 0 || b.Length == 0) return 0d;
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1d;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
            return (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
        return 0d;
    }

}

// 録画実績・復旧 — Immutable recording-attempt evidence for indefinite recovery; never used for preferences.
