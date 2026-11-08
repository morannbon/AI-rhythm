namespace AIrhythm.BasicPlugin;

/// <summary>
/// 推薦・学習 — TvAIr RuntimeのAI-rhythmページ入口。データ取得・操作・外部EvidenceはHost公開契約へ統一する。
/// </summary>
internal static class AIrhythmIdentity
{
    public const string PluginId = "airhythm.basic";
    public const string DisplayName = "AI-rhythm";
    public static string Version { get; } = typeof(AIrhythmIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
    public const string Route = "airhythm";
}

internal static class AIrhythmHtml
{
    public static string Encode(string? value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}

internal sealed class AIrhythmRenderer
{
    public string Name => AIrhythmIdentity.DisplayName;
    public string Version => AIrhythmIdentity.Version;

    public string RenderHtml(RuntimeUiRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var assembly = typeof(AIrhythmRenderer).Assembly;
        var html = ReadResource(assembly, "AIrhythm.BasicPlugin.Assets.index.html");
        var css = ReadResource(assembly, "AIrhythm.BasicPlugin.Assets.app.css");
        var theme = BuildThemeProjection(context);
        var projectedCss = ResolveThemeCss(css, theme.Values);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog($"THEME_RENDER_CONTRACT result=OBSERVED hostThemeGeneration={context.HostThemeGeneration} hostThemeRevision={context.HostThemeRevision} selected={context.HostSelectedTheme} effective={context.HostEffectiveTheme} updateMode={context.ThemeUpdateMode} refreshContractEntries={context.ThemeRefreshContract?.Count ?? 0} updateContractEntries={context.ThemeUpdateContract?.Count ?? 0} source=host_runtime_ui_context handlerTransport=host_shell_declared_asset pluginPolling=False pluginThemeInference=False pluginForcedReload=False");
#endif
        var snapshot = AIrhythmDataState.Capture();
        var usageTotals = AIrhythmDataState.GetUsageTotals();
        var rhythmQuery = context.RequestQuery.TryGetValue("rhythm", out var rawRhythm)
            ? rawRhythm
            : string.Empty;
        var server = AIrhythmRecommendationEngine.Build(context, snapshot, rhythmQuery);

        html = html.Replace("<html lang=\"ja\">", $"<html lang=\"ja\" data-theme=\"{theme.Name}\" data-tvair-theme-selected=\"{AIrhythmHtml.Encode(context.HostSelectedTheme)}\">", StringComparison.Ordinal);
        html = html.Replace("<link rel=\"stylesheet\" href=\"app.css\">",
            $"<style id=\"airhythm-theme-schema\" type=\"text/plain\">{ThemeSchemaJson}</style>" +
            $"<style id=\"airhythm-style-template\" type=\"text/plain\">{css}</style>" +
            $"<style id=\"airhythm-theme-vars\">:root{{{theme.CssVariables}}}{projectedCss}</style>",
            StringComparison.Ordinal);
        html = html.Replace("<strong id=\"historyCount\">0</strong>", $"<strong id=\"historyCount\">{usageTotals.RecordingTotal}</strong>", StringComparison.Ordinal);
        html = html.Replace("<strong id=\"reservationCount\">0</strong>", $"<strong id=\"reservationCount\">{usageTotals.ReservationTotal}</strong>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"message\" class=\"message\" hidden></div>", server.MessageHtml, StringComparison.Ordinal);
        html = html.Replace("<div id=\"dashboardCharts\" class=\"dashboard-grid\"></div>", $"<div id=\"dashboardCharts\" class=\"dashboard-grid\">{ResolveThemeCss(server.ChartsHtml, theme.Values)}</div>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"discoveryHub\" class=\"discovery-hub\"></div>", $"<div id=\"discoveryHub\" class=\"discovery-hub\">{server.DiscoveryHtml}</div>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"rhythmSearch\" class=\"rhythm-search\"></div>", $"<div id=\"rhythmSearch\" class=\"rhythm-search\">{server.SearchHtml}</div>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"recommendations\" class=\"cards\"></div>", $"<div id=\"recommendations\" class=\"cards\">{server.CardsHtml}</div>", StringComparison.Ordinal);
        html = html.Replace("<p id=\"status\">番組情報を確認しています</p>", $"<p id=\"status\">{server.StatusText}</p>", StringComparison.Ordinal);
        html = html.Replace("<span id=\"appVersion\" class=\"app-version\">v0.0.0</span>", $"<span id=\"appVersion\" class=\"app-version\">v{AIrhythmHtml.Encode(AIrhythmIdentity.Version)}</span>", StringComparison.Ordinal);

        var saveAttributes = context.BuildPluginActionAttributes(
            new Dictionary<string, string?> { ["operation"] = "saveSettings" },
            new PluginActionFeedbackOptions
            {
                PendingLabel = "保存中",
                SuccessLabel = "保存済み",
                NoChangeLabel = "変更なし",
                FailureLabel = "保存",
                DisableWhileRunning = true,
                RestoreOnFailure = true
            },
            responseMode: "hostHandled",
            formCapture: "#airhythm-settings-form");
        var resetSettingsAttributes = context.BuildPluginActionAttributes(
            new Dictionary<string, string?> { ["operation"] = "resetSettings" },
            new PluginActionFeedbackOptions
            {
                PendingLabel = "初期状態へ戻しています",
                SuccessLabel = "初期状態に戻しました",
                NoChangeLabel = "初期状態です",
                FailureLabel = "おすすめリセット",
                DisableWhileRunning = true,
                RestoreOnFailure = true
            },
            responseMode: "hostHandled");
        var resetLearningAttributes = context.BuildPluginActionAttributes(
            new Dictionary<string, string?> { ["operation"] = "resetLearning" },
            new PluginActionFeedbackOptions
            {
                PendingLabel = "リセット中",
                SuccessLabel = "リセット済み",
                NoChangeLabel = "対象なし",
                FailureLabel = "学習データリセット",
                ConfirmationMessage = "録画実績は残したまま、学習データをリセットしますか？",
                DisableWhileRunning = true,
                RestoreOnFailure = true
            },
            responseMode: "hostHandled");

        var backupDataAttributes = context.BuildPluginActionAttributes(
            new Dictionary<string, string?> { ["operation"] = "backupData" },
            new PluginActionFeedbackOptions
            {
                PendingLabel = "保存先を選択中",
                SuccessLabel = "バックアップ済み",
                NoChangeLabel = "キャンセル",
                FailureLabel = "データをバックアップ",
                DisableWhileRunning = true,
                RestoreOnFailure = true
            },
            responseMode: "hostHandled");
        var restoreDataAttributes = context.BuildPluginActionAttributes(
            new Dictionary<string, string?> { ["operation"] = "restoreData" },
            new PluginActionFeedbackOptions
            {
                PendingLabel = "復元するファイルを選択中",
                SuccessLabel = "復元済み",
                NoChangeLabel = "キャンセル",
                FailureLabel = "バックアップから復元",
                ConfirmationMessage = "現在のAI-rhythmデータをバックアップの内容で置き換えます。復元しますか？",
                DisableWhileRunning = true,
                RestoreOnFailure = true
            },
            responseMode: "hostHandled");
        var resetAccumulatedDataAttributes = context.BuildPluginActionAttributes(
            new Dictionary<string, string?> { ["operation"] = "resetAccumulatedData" },
            new PluginActionFeedbackOptions
            {
                PendingLabel = "リセット中",
                SuccessLabel = "リセット済み",
                NoChangeLabel = "対象なし",
                FailureLabel = "完全リセット",
                ConfirmationMessage = "録画実績・検索履歴・興味情報・累積件数をすべて消し、今この時点から新しく蓄積を始めます。設定は残ります。完全リセットしますか？",
                DisableWhileRunning = true,
                RestoreOnFailure = true
            },
            responseMode: "hostHandled");

        var refreshAttributes = context.BuildPluginActionAttributes(
            new Dictionary<string, string?>
            {
                ["operation"] = "refresh",
                ["refreshAfter"] = "true",
                ["preserveScroll"] = "true"
            },
            new PluginActionFeedbackOptions
            {
                PendingLabel = "更新中…",
                SuccessLabel = "更新中…",
                FailureLabel = "更新",
                DisableWhileRunning = true,
                KeepDisabledOnSuccess = true,
                RestoreOnFailure = true
            },
            responseMode: "hostHandled");
        html = html.Replace("<button id=\"refresh\" type=\"button\" class=\"refresh-button\">更新</button>", $"<button id=\"refresh\" type=\"button\" class=\"refresh-button\" {refreshAttributes}>更新</button>", StringComparison.Ordinal);
        var revisionValue = snapshot.SettingsRevision?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        html = html.Replace("<section class=\"panel settings\"><h2>設定</h2>", $"<section class=\"panel settings\"><h2>設定</h2><form id=\"airhythm-settings-form\"><input type=\"hidden\" name=\"revision\" value=\"{AIrhythmHtml.Encode(revisionValue)}\">", StringComparison.Ordinal);
        html = html.Replace(
            "<textarea id=\"preferred\"></textarea>",
            $"<textarea id=\"preferred\" name=\"preferred\">{AIrhythmHtml.Encode(snapshot.Settings.Preferred)}</textarea>",
            StringComparison.Ordinal);
        html = html.Replace(
            "<textarea id=\"excluded\"></textarea>",
            $"<textarea id=\"excluded\" name=\"excluded\">{AIrhythmHtml.Encode(snapshot.Settings.Excluded)}</textarea>",
            StringComparison.Ordinal);
        var currentLimit = Math.Clamp(snapshot.Settings.Limit, 10, 30);
        var limitOptions = string.Join(string.Empty, new[] { 10, 20, 30 }.Select(value =>
            $"<option value=\"{value}\"{(value == currentLimit ? " selected" : string.Empty)}>{value}</option>"));
        html = html.Replace(
            "<select id=\"limit\"><option>10</option><option selected>20</option><option>30</option></select>",
            $"<select id=\"limit\" name=\"limit\" form=\"airhythm-settings-form\">{limitOptions}</select>",
            StringComparison.Ordinal);
        html = html.Replace("<button id=\"save\" type=\"button\">保存</button>", $"<button id=\"save\" type=\"button\" {saveAttributes}>保存</button>", StringComparison.Ordinal);
        html = html.Replace("<button id=\"reset\" type=\"button\" class=\"secondary\">おすすめリセット</button>", $"<button id=\"reset\" type=\"button\" class=\"secondary\" {resetSettingsAttributes}>おすすめリセット</button>", StringComparison.Ordinal);
        html = html.Replace("<button id=\"resetLearning\" type=\"button\" class=\"learning-reset\">学習データリセット</button>", $"<button id=\"resetLearning\" type=\"button\" class=\"learning-reset\" {resetLearningAttributes}>学習データリセット</button>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"settingsFormEnd\"></div>", "</form>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"resetAccumulatedDataAction\" class=\"complete-reset-action\"></div>", $"<div id=\"resetAccumulatedDataAction\" class=\"complete-reset-action\"><button id=\"resetAccumulatedData\" type=\"button\" class=\"learning-reset\" {resetAccumulatedDataAttributes}>完全リセット</button></div>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"dataMaintenanceActions\" class=\"data-maintenance-actions\"></div>", $"<div id=\"dataMaintenanceActions\" class=\"data-maintenance-actions\"><button id=\"backupData\" type=\"button\" class=\"secondary\" {backupDataAttributes}>データをバックアップ</button><button id=\"restoreData\" type=\"button\" class=\"secondary\" {restoreDataAttributes}>バックアップから復元</button></div>", StringComparison.Ordinal);

        return html;
    }

    public Task<RuntimeUiActionResult> HandleActionAsync(RuntimeUiActionContext request, CancellationToken cancellationToken)
    {
        request.Payload.TryGetValue("operation", out var operation);
        operation = string.IsNullOrWhiteSpace(operation) ? request.ActionName : operation;

        if (string.Equals(operation, "reserve", StringComparison.OrdinalIgnoreCase)
            || string.Equals(operation, "reserveProgram", StringComparison.OrdinalIgnoreCase))
        {
            var reserveResult = AIrhythmDataState.ReserveProgram(request.Payload);
            return Task.FromResult(BuildFeedbackResult(
                reserveResult,
                successMessage: "予約しました",
                failureMessage: "予約できませんでした",
                successButtonLabel: "予約済み",
                failureButtonLabel: "予約する",
                keepDisabledOnSuccess: true));
        }

        if (string.Equals(operation, "cancelReservation", StringComparison.OrdinalIgnoreCase))
        {
            var cancelResult = AIrhythmDataState.CancelReservation(request.Payload);
            return Task.FromResult(BuildFeedbackResult(
                cancelResult,
                successMessage: "予約を取り消しました",
                noChangeMessage: "取消対象の予約はありません",
                failureMessage: "予約を取り消せませんでした",
                successButtonLabel: "予約する",
                failureButtonLabel: "予約取消",
                refreshRequested: cancelResult.Success));
        }

        if (string.Equals(operation, "setReservationEnabled", StringComparison.OrdinalIgnoreCase))
        {
            var enabledResult = AIrhythmDataState.SetReservationEnabled(request.Payload);
            request.Payload.TryGetValue("enabled", out var enabledText);
            var enabled = bool.TryParse(enabledText, out var parsedEnabled) && parsedEnabled;
            return Task.FromResult(BuildFeedbackResult(
                enabledResult,
                successMessage: enabled ? "予約を有効にしました" : "予約を無効にしました",
                noChangeMessage: enabled ? "すでに有効です" : "すでに無効です",
                failureMessage: enabled ? "予約を有効にできませんでした" : "予約を無効にできませんでした",
                successButtonLabel: enabled ? "無効" : "有効",
                failureButtonLabel: enabled ? "有効" : "無効",
                refreshRequested: enabledResult.Success));
        }

        if (string.Equals(operation, "addInterest", StringComparison.OrdinalIgnoreCase))
        {
            request.Payload.TryGetValue("eventId", out var eventId);
            var interestResult = AIrhythmDataState.RecordInterestSignal(AIrhythmDataState.Capture(), eventId ?? string.Empty);
            return Task.FromResult(BuildFeedbackResult(
                interestResult,
                successMessage: "『気になる』に追加しました",
                noChangeMessage: "すでに『気になる』へ追加されています",
                failureMessage: "『気になる』へ追加できませんでした",
                successButtonLabel: "追加済み",
                keepDisabledOnSuccess: true,
                refreshRequested: interestResult.Success));
        }

        if (string.Equals(operation, "removeInterest", StringComparison.OrdinalIgnoreCase))
        {
            request.Payload.TryGetValue("eventId", out var eventId);
            var removeInterestResult = AIrhythmDataState.RemoveInterestSignal(eventId ?? string.Empty);
            return Task.FromResult(BuildFeedbackResult(
                removeInterestResult,
                successMessage: "『気になる』から解除しました",
                noChangeMessage: "解除対象はありません",
                failureMessage: "『気になる』を解除できませんでした",
                refreshRequested: removeInterestResult.Success));
        }

        if (string.Equals(operation, "refresh", StringComparison.OrdinalIgnoreCase))
            return RefreshWithExternalEvidenceAsync(cancellationToken);

        if (string.Equals(operation, "backupData", StringComparison.OrdinalIgnoreCase))
            return BackupDataAsync(request.CurrentWindowId, cancellationToken);

        if (string.Equals(operation, "restoreData", StringComparison.OrdinalIgnoreCase))
            return RestoreDataAsync(request.CurrentWindowId, cancellationToken);

        if (string.Equals(operation, "resetAccumulatedData", StringComparison.OrdinalIgnoreCase))
        {
            var resetAllResult = AIrhythmDataState.ResetAccumulatedData();
            return Task.FromResult(BuildFeedbackResult(
                resetAllResult,
                successMessage: "完全リセットしました",
                noChangeMessage: "リセットする情報はありません",
                failureMessage: "完全リセットできませんでした",
                refreshRequested: resetAllResult.Success));
        }

        if (string.Equals(operation, "resetLearning", StringComparison.OrdinalIgnoreCase))
        {
            var resetResult = AIrhythmDataState.ResetLearningInformation();
            return Task.FromResult(BuildFeedbackResult(
                resetResult,
                successMessage: "学習データをリセットしました",
                noChangeMessage: "リセットする学習データはありません",
                failureMessage: "学習データをリセットできませんでした"));
        }


        if (string.Equals(operation, "resetSettings", StringComparison.OrdinalIgnoreCase))
        {
            var settingsBeforeReset = AIrhythmDataState.Capture().Settings;
            var resetSettingsResult = AIrhythmDataState.SaveSettings(settingsBeforeReset with { Preferred = string.Empty, Excluded = string.Empty }, expectedRevision: null);
            return Task.FromResult(BuildFeedbackResult(
                resetSettingsResult,
                successMessage: "おすすめをリセットしました",
                noChangeMessage: "おすすめ調整はすでに初期状態です",
                failureMessage: "おすすめをリセットできませんでした"));
        }

        if (!string.Equals(operation, "saveSettings", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(BuildFeedbackResult(new AIrhythmSaveResult(false, "操作を実行できません"), failureMessage: "操作を実行できませんでした"));

        request.Payload.TryGetValue("preferred", out var preferred);
        request.Payload.TryGetValue("excluded", out var excluded);
        request.Payload.TryGetValue("limit", out var limitText);
        request.Payload.TryGetValue("revision", out var revision);
        var settingsBeforeSave = AIrhythmDataState.Capture().Settings;
        var limit = int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLimit)
            ? Math.Clamp(parsedLimit, 10, 30)
            : settingsBeforeSave.Limit;
        var result = AIrhythmDataState.SaveSettings(settingsBeforeSave with
        {
            Limit = limit,
            Preferred = preferred ?? string.Empty,
            Excluded = excluded ?? string.Empty
        }, revision);
        return Task.FromResult(BuildFeedbackResult(
            result,
            successMessage: "保存しました",
            noChangeMessage: "変更はありません",
            failureMessage: "保存できませんでした"));
    }


    private static async Task<RuntimeUiActionResult> BackupDataAsync(string currentWindowId, CancellationToken cancellationToken)
    {
        var result = await AIrhythmDataState.BackupPersistentDataAsync(currentWindowId, cancellationToken).ConfigureAwait(false);
        return BuildFeedbackResult(
            result,
            successMessage: string.IsNullOrWhiteSpace(result.Message) ? "バックアップしました" : result.Message,
            noChangeMessage: "バックアップをキャンセルしました",
            failureMessage: string.IsNullOrWhiteSpace(result.Message) ? "バックアップできませんでした" : result.Message);
    }

    private static async Task<RuntimeUiActionResult> RestoreDataAsync(string currentWindowId, CancellationToken cancellationToken)
    {
        var result = await AIrhythmDataState.RestorePersistentDataAsync(currentWindowId, cancellationToken).ConfigureAwait(false);
        return BuildFeedbackResult(
            result,
            successMessage: string.IsNullOrWhiteSpace(result.Message) ? "バックアップから復元しました" : result.Message,
            noChangeMessage: "復元をキャンセルしました",
            failureMessage: string.IsNullOrWhiteSpace(result.Message) ? "復元できませんでした" : result.Message,
            refreshRequested: result.Success && result.Changed);
    }

    private static async Task<RuntimeUiActionResult> RefreshWithExternalEvidenceAsync(CancellationToken cancellationToken)
    {
        await AIrhythmDataState.RefreshExternalEvidenceAsync(cancellationToken).ConfigureAwait(false);
        AIrhythmDataState.InvalidateForAction("ManualRefresh");
        return new RuntimeUiActionResult
        {
            Succeeded = true,
            RefreshRequested = true,
            RefreshTarget = "content",
            PreserveScroll = true,
            ContentRoute = AIrhythmIdentity.Route
        };
    }

    private static RuntimeUiActionResult BuildFeedbackResult(
        AIrhythmSaveResult result,
        string successMessage = "処理しました",
        string noChangeMessage = "変更はありません",
        string failureMessage = "処理できませんでした",
        string successButtonLabel = "",
        string failureButtonLabel = "",
        bool keepDisabledOnSuccess = false,
        bool refreshRequested = false)
    {
        var phase = !result.Success
            ? PluginActionFeedbackPhase.Failed
            : result.Changed
                ? PluginActionFeedbackPhase.Succeeded
                : PluginActionFeedbackPhase.NoChange;
        // 保存・読込 — 利用者向け通知には内部Storage/SQL例外を出さず、操作ごとの日本語標準文を使用する。 詳細診断はPlugin/Hostログ側で保持する。
        var message = !result.Success
            ? failureMessage
            : result.Changed
                ? successMessage
                : noChangeMessage;
        return new RuntimeUiActionResult
        {
            Succeeded = result.Success,
            Message = message,
            RefreshRequested = refreshRequested && result.Success,
            RefreshTarget = refreshRequested && result.Success ? "content" : string.Empty,
            PreserveScroll = refreshRequested && result.Success,
            ContentRoute = refreshRequested && result.Success ? AIrhythmIdentity.Route : string.Empty,
            Feedback = new PluginActionFeedback
            {
                Phase = phase,
                Kind = !result.Success
                    ? PluginActionFeedbackKind.Error
                    : result.Changed
                        ? PluginActionFeedbackKind.Success
                        : PluginActionFeedbackKind.Information,
                Message = message,
                ButtonLabel = !result.Success ? failureButtonLabel : successButtonLabel,
                KeepDisabled = result.Success && result.Changed && keepDisabledOnSuccess
            }
        };
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource was not found: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // 表示・テーマ — Both server rendering and runtime theme updates project values from the same embedded schema.
    // The generated stylesheet contains resolved values for WebBrowser engines without CSS variable support.
    private static readonly Regex ThemeCssVariable = new(@"var\(--([a-z0-9-]+)\)", RegexOptions.CultureInvariant);
    private static string ResolveThemeCss(string template, IReadOnlyDictionary<string, string> values)
        => ThemeCssVariable.Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);

    private static readonly string ThemeSchemaJson = ReadResource(
        typeof(AIrhythmRenderer).Assembly, "AIrhythm.BasicPlugin.Assets.theme-spec.json");
    private static readonly JsonDocument ThemeSchema = JsonDocument.Parse(ThemeSchemaJson);

    private static (string Name, string CssVariables, IReadOnlyDictionary<string, string> Values) BuildThemeProjection(RuntimeUiRenderContext context)
    {
        var mode = string.Equals(context.HostEffectiveTheme, "dark", StringComparison.OrdinalIgnoreCase)
            ? "dark" : "light";
        var contract = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (context.ThemeContract is not null)
            foreach (var pair in context.ThemeContract)
                contract[pair.Key] = pair.Value;
        var specification = ThemeSchema.RootElement;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var css = new StringBuilder();

        void Set(string name, string value)
        {
            values.Add(name, value);
            css.Append("--").Append(name).Append(':').Append(value).Append(';');
        }

        foreach (var role in specification.GetProperty("roles").EnumerateArray())
        {
            var name = role.GetProperty("name").GetString()!;
            var fallback = role.TryGetProperty("fallback", out var fallbackRole)
                ? values[fallbackRole.GetString()!]
                : role.GetProperty(mode).GetString()!;
            var value = fallback;
            foreach (var key in role.GetProperty("keys").EnumerateArray())
            {
                if (contract.TryGetValue(key.GetString()!, out var candidate)
                    && IsSafeCssValue(candidate))
                {
                    value = candidate.Trim();
                    break;
                }
            }
            Set(name, value);
        }
        foreach (var color in specification.GetProperty("modes").GetProperty(mode).EnumerateObject())
            Set(color.Name, color.Value.GetString()!);
        foreach (var color in specification.GetProperty("palette").EnumerateObject())
            Set(color.Name, color.Value.GetString()!);
        foreach (var dimension in specification.GetProperty("layout").EnumerateObject())
            Set(dimension.Name, dimension.Value.GetString()!);

        return (mode, css.ToString(), values);
    }

    private static readonly Regex UnsafeThemeCssFunction = new(
        @"(?:^|[^a-z])(?:expression|url|behavior|binding)\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static bool IsSafeCssValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 96 || UnsafeThemeCssFunction.IsMatch(value))
            return false;
        foreach (var ch in value)
        {
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                or '#' or '(' or ')' or ',' or '.' or '%' or '-' or '_' or ' ' or '/')
                continue;
            return false;
        }
        return true;
    }


    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public sealed class AIrhythmRuntimePlugin : ITvAirRuntimeCapabilityPlugin, ITvAirRuntimeUiPlugin, ITvAirRuntimeLifecyclePlugin
{
    public TvAirPluginRuntimeDescriptor Descriptor { get; } = new()
    {
        PluginId = AIrhythmIdentity.PluginId,
        DisplayName = AIrhythmIdentity.DisplayName,
        Version = AIrhythmIdentity.Version,
        SdkContractVersion = TvAIrPluginSdkContract.SdkVersion,
        RequiredCapabilities = new[]
        {
            TvAirRuntimeCapabilities.DataSnapshotRead,
            TvAirRuntimeCapabilities.StorageRead,
            TvAirRuntimeCapabilities.StorageWrite
        },
        RequiredPermissions = new[]
        {
            PluginPermission.ShowUi,
            PluginPermission.OpenPage,
            PluginPermission.UseActionApi,
            PluginPermission.UseAssetApi,
            PluginPermission.UseSafeEvent,
            PluginPermission.ReadTheme,
            PluginPermission.ReadProgramGuideProjection,
            PluginPermission.ReadChannels,
            PluginPermission.ReadReservations,
            PluginPermission.ReadKeywordRules,
            PluginPermission.PreviewAllocation,
            PluginPermission.WriteReservations,
            PluginPermission.ReadRecordingHistory,
            PluginPermission.ReadRecordingStatus,
            PluginPermission.ReadTunerStatus,
            PluginPermission.WriteLogs,
            PluginPermission.ReadPluginStorage,
            PluginPermission.WritePluginStorage,
            PluginPermission.UsePathPicker,
            PluginPermission.UseExternalLookup
        },
        Assets = new[]
        {
            new PluginAssetDefinition
            {
                LogicalPath = "theme-hot-apply.js",
                ResourceName = "AIrhythm.BasicPlugin.Assets.theme-hot-apply.js",
                ContentType = "text/javascript; charset=utf-8"
            }
        },
        Surfaces = new[]
        {
            new TvAIrPlugin.Surfaces.PluginSurfaceDefinition
            {
                SurfaceDefinitionId = "main.web", Kind = TvAIrPlugin.Surfaces.PluginSurfaceKind.Web,
                EntryPoint = AIrhythmIdentity.Route
            }
        },
        UiDefinitions = new[]
        {
            new RuntimeUiDefinition
            {
                UiDefinitionId = "main", Route = AIrhythmIdentity.Route, Kind = RuntimeUiKind.Page,
                SurfaceDefinitionId = "main.web",
                ThemeUpdateMode = RuntimeUiThemeUpdateMode.HotApply,
                ThemeHotApplyScriptPath = "theme-hot-apply.js"
            }
        },
        MenuActions = new[]
        {
            new PluginMenuActionDefinition
            {
                ActionId = "open",
                Label = "AI-rhythm",
                Kind = PluginMenuActionKind.Page,
                Priority = 300,
                Route = AIrhythmIdentity.Route
            }
        },
        Lifecycle = new PluginLifecycleDefinition()
    };

    private readonly AIrhythmRenderer _ui = new();

    public void Initialize(ITvAirPluginRuntimeContext context) => AIrhythmDataState.Initialize(context);

    public string RenderHtml(RuntimeUiRenderContext context) => _ui.RenderHtml(context);

    public Task<RuntimeUiActionResult> HandleActionAsync(RuntimeUiActionContext context, CancellationToken cancellationToken)
        => _ui.HandleActionAsync(context, cancellationToken);
    public void OnStart() => AIrhythmDataState.Start();
    public void OnStop() => AIrhythmDataState.Stop();
}
