using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TvAIrPlugin;
using TvAIrPlugin.Data;
using TvAIrPlugin.Events;
using TvAIrPlugin.Pickers;
using TvAIrPlugin.Runtime;

namespace AIrhythm.BasicPlugin;

/// <summary>
/// TvAIrのページ入口を所有し、データ取得はRuntime正規経路へ統一する。
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
        var js = ReadResource(assembly, "AIrhythm.BasicPlugin.Assets.app.js");
        var theme = BuildThemeProjection(context);
        var snapshot = AIrhythmDataState.Capture();
        var usageTotals = AIrhythmDataState.GetUsageTotals();
        var externalLookupState = AIrhythmDataState.GetExternalLookupState(snapshot.Settings.ExternalLookupEnabled);
        var bootstrap = new
        {
            settings = snapshot.Settings,
            externalLookup = externalLookupState
        };
        var bootstrapJson = JsonSerializer.Serialize(bootstrap, JsonOptions);
        var rhythmQuery = context.RequestQuery.TryGetValue("rhythm", out var rawRhythm)
            ? rawRhythm
            : string.Empty;
        var server = AIrhythmRecommendationEngine.Build(context, snapshot, rhythmQuery);

        html = html.Replace("<html lang=\"ja\">", $"<html lang=\"ja\" data-theme=\"{theme.Name}\" style=\"{theme.CssVariables}\">", StringComparison.Ordinal);
        html = html.Replace("<link rel=\"stylesheet\" href=\"app.css\">", $"<style>{css}</style>", StringComparison.Ordinal);
        html = html.Replace("<strong id=\"historyCount\">0</strong>", $"<strong id=\"historyCount\">{usageTotals.RecordingTotal}</strong>", StringComparison.Ordinal);
        html = html.Replace("<strong id=\"reservationCount\">0</strong>", $"<strong id=\"reservationCount\">{usageTotals.ReservationTotal}</strong>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"message\" class=\"message\" hidden></div>", server.MessageHtml, StringComparison.Ordinal);
        html = html.Replace("<div id=\"dashboardCharts\" class=\"dashboard-grid\"></div>", $"<div id=\"dashboardCharts\" class=\"dashboard-grid\">{server.ChartsHtml}</div>", StringComparison.Ordinal);
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

        var externalLookupEnableAttributes = context.BuildPluginActionAttributes(
            new Dictionary<string, string?> { ["operation"] = "enableExternalLookup" },
            new PluginActionFeedbackOptions
            {
                PendingLabel = "確認中",
                SuccessLabel = "ON",
                FailureLabel = "OFF",
                ConfirmationMessage = "AI-rhythmは、番組や作品をより正確に判別するため、TvAIrが許可した外部情報サービスを利用します。通信はTvAIrの管理下で行われ、録画ファイルやPC内のファイルは送信しません。外部情報の利用を許可しますか？",
                DisableWhileRunning = true,
                KeepDisabledOnSuccess = true,
                RestoreOnFailure = true
            },
            responseMode: "hostHandled");
        var externalLookupDisableAttributes = context.BuildPluginActionAttributes(
            new Dictionary<string, string?> { ["operation"] = "disableExternalLookup" },
            new PluginActionFeedbackOptions
            {
                PendingLabel = "OFFにしています",
                SuccessLabel = "OFF",
                FailureLabel = "ON",
                DisableWhileRunning = true,
                KeepDisabledOnSuccess = true,
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
        html = html.Replace("<textarea id=\"preferred\"", "<textarea id=\"preferred\" name=\"preferred\"", StringComparison.Ordinal);
        html = html.Replace("<input id=\"externalLookupEnabled\" type=\"hidden\" value=\"false\">", $"<input id=\"externalLookupEnabled\" name=\"externalLookupEnabled\" type=\"hidden\" value=\"{(snapshot.Settings.ExternalLookupEnabled ? "true" : "false")}\">", StringComparison.Ordinal);
        var externalLookupToggleAttributes = snapshot.Settings.ExternalLookupEnabled ? externalLookupDisableAttributes : externalLookupEnableAttributes;
        var externalLookupToggleLabel = snapshot.Settings.ExternalLookupEnabled ? "ON" : "OFF";
        var externalLookupCssState = snapshot.Settings.ExternalLookupEnabled ? "is-on" : "is-off";
        var externalLookupStatus = AIrhythmHtml.Encode(externalLookupState.StatusText);
        html = html.Replace("<button id=\"externalLookupToggle\" type=\"button\" class=\"external-lookup-toggle\">OFF</button><p id=\"externalLookupStatus\" class=\"external-lookup-status\"></p>", $"<button id=\"externalLookupToggle\" type=\"button\" class=\"external-lookup-toggle {externalLookupCssState}\" aria-pressed=\"{(snapshot.Settings.ExternalLookupEnabled ? "true" : "false")}\" {externalLookupToggleAttributes}>{externalLookupToggleLabel}</button><p id=\"externalLookupStatus\" class=\"external-lookup-status\">{externalLookupStatus}</p>", StringComparison.Ordinal);
        html = html.Replace("<textarea id=\"excluded\"", "<textarea id=\"excluded\" name=\"excluded\"", StringComparison.Ordinal);
        html = html.Replace("<select id=\"limit\">", "<select id=\"limit\" name=\"limit\">", StringComparison.Ordinal);
        html = html.Replace("<button id=\"save\" type=\"button\">保存</button>", $"<button id=\"save\" type=\"button\" {saveAttributes}>保存</button>", StringComparison.Ordinal);
        html = html.Replace("<button id=\"reset\" type=\"button\" class=\"secondary\">おすすめリセット</button>", $"<button id=\"reset\" type=\"button\" class=\"secondary\" {resetSettingsAttributes}>おすすめリセット</button>", StringComparison.Ordinal);
        html = html.Replace("<button id=\"resetLearning\" type=\"button\" class=\"learning-reset\">学習データリセット</button>", $"<button id=\"resetLearning\" type=\"button\" class=\"learning-reset\" {resetLearningAttributes}>学習データリセット</button>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"settingsFormEnd\"></div>", "</form>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"resetAccumulatedDataAction\" class=\"complete-reset-action\"></div>", $"<div id=\"resetAccumulatedDataAction\" class=\"complete-reset-action\"><button id=\"resetAccumulatedData\" type=\"button\" class=\"learning-reset\" {resetAccumulatedDataAttributes}>完全リセット</button></div>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"dataMaintenanceActions\" class=\"data-maintenance-actions\"></div>", $"<div id=\"dataMaintenanceActions\" class=\"data-maintenance-actions\"><button id=\"backupData\" type=\"button\" class=\"secondary\" {backupDataAttributes}>データをバックアップ</button><button id=\"restoreData\" type=\"button\" class=\"secondary\" {restoreDataAttributes}>バックアップから復元</button></div>", StringComparison.Ordinal);

        html = html.Replace("<script src=\"app.js\"></script>", $"<script>window.__AIRHYTHM_BOOTSTRAP__={bootstrapJson};</script><script>{js}</script>", StringComparison.Ordinal);
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

        if (string.Equals(operation, "enableExternalLookup", StringComparison.OrdinalIgnoreCase))
        {
            var enableResult = AIrhythmDataState.SetExternalLookupEnabled(true);
            return Task.FromResult(BuildSilentRefreshResult(enableResult));
        }

        if (string.Equals(operation, "disableExternalLookup", StringComparison.OrdinalIgnoreCase))
        {
            var disableResult = AIrhythmDataState.SetExternalLookupEnabled(false);
            return Task.FromResult(BuildSilentRefreshResult(disableResult));
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
        request.Payload.TryGetValue("revision", out var revision);
        var settingsBeforeSave = AIrhythmDataState.Capture().Settings;
        var result = AIrhythmDataState.SaveSettings(settingsBeforeSave with
        {
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

    private static RuntimeUiActionResult BuildSilentRefreshResult(AIrhythmSaveResult result)
        => new()
        {
            Succeeded = result.Success,
            RefreshRequested = result.Success,
            RefreshTarget = "content",
            PreserveScroll = true,
            ContentRoute = AIrhythmIdentity.Route,
            Feedback = new PluginActionFeedback
            {
                Phase = result.Success ? PluginActionFeedbackPhase.Succeeded : PluginActionFeedbackPhase.Failed,
                Kind = result.Success ? PluginActionFeedbackKind.Information : PluginActionFeedbackKind.Error,
                Message = result.Success ? string.Empty : (string.IsNullOrWhiteSpace(result.Message) ? "設定を変更できませんでした" : result.Message),
                ShowFloatingLabel = false,
                RefreshAfterFeedback = result.Success
            }
        };

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
        // 利用者向け通知には内部Storage/SQL例外を出さず、操作ごとの日本語標準文を使用する。
        // 詳細診断はPlugin/Hostログ側で保持する。
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

    private static (string Name, string CssVariables) BuildThemeProjection(RuntimeUiRenderContext context)
    {
        var dark = string.Equals(context.HostEffectiveTheme, "dark", StringComparison.OrdinalIgnoreCase);
        var contract = context.ThemeContract ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string Pick(string fallback, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (contract.TryGetValue(key, out var value) && IsSafeCssValue(value))
                    return value.Trim();
            }
            return fallback;
        }

        // ThemeContract is the color authority. Literal fallbacks are used only when the Host does
        // not publish compatible base roles. Semantic roles fall back only to other Host-provided
        // roles, never to a Plugin-owned semantic palette.
        var page = Pick(dark ? "#121212" : "#f5f6f7", "pageBackground", "background", "backgroundColor", "appBackground");
        var surface = Pick(dark ? "#1e1e1e" : "#ffffff", "surfaceBackground", "surface", "panelBackground", "cardBackground");
        var subtle = Pick(dark ? "#272727" : "#f0f1f2", "subtleBackground", "secondaryBackground", "controlBackground");
        var input = Pick(dark ? "#272727" : "#ffffff", "inputBackground", "fieldBackground", "controlBackground");
        var text = Pick(dark ? "#f2f2f2" : "#111111", "text", "foreground", "textColor", "foregroundColor");
        var muted = Pick(dark ? "#b7b7b7" : "#5f6368", "mutedText", "secondaryText", "mutedForeground");
        var line = Pick(dark ? "#4b4b4b" : "#d2d5d9", "border", "borderColor", "separator", "line");
        var accent = Pick(dark ? "#303030" : "#2f3337", "accent", "accentColor", "buttonBackground");
        var accentText = Pick("#ffffff", "accentText", "accentForeground", "buttonForeground");
        var focus = Pick(dark ? "#7ab8f5" : "#5b9dd9", "focus", "focusColor", "focusRing");

        var controlBackground = Pick(input, "controlBackground");
        var controlText = Pick(text, "controlText");
        var controlBorder = Pick(line, "controlBorder");
        var controlHoverBackground = Pick(controlBackground, "controlHoverBackground");
        var controlHoverText = Pick(controlText, "controlHoverText");
        var controlHoverBorder = Pick(controlBorder, "controlHoverBorder");

        var selectedBackground = Pick(accent, "selectedBackground");
        var selectedText = Pick(accentText, "selectedText");
        var selectedBorder = Pick(selectedBackground, "selectedBorder");
        var selectedHoverBackground = Pick(selectedBackground, "selectedHoverBackground");
        var selectedHoverText = Pick(selectedText, "selectedHoverText");
        var selectedHoverBorder = Pick(selectedBorder, "selectedHoverBorder");

        var disabledBackground = Pick(subtle, "disabledBackground");
        var disabledText = Pick(muted, "disabledText");
        var disabledBorder = Pick(line, "disabledBorder");

        var primaryBackground = Pick(accent, "primaryActionBackground");
        var primaryText = Pick(accentText, "primaryActionText");
        var primaryBorder = Pick(primaryBackground, "primaryActionBorder");
        var primaryHoverBackground = Pick(primaryBackground, "primaryActionHoverBackground");
        var primaryHoverText = Pick(primaryText, "primaryActionHoverText");
        var primaryHoverBorder = Pick(primaryBorder, "primaryActionHoverBorder");

        var secondaryBackground = Pick(subtle, "secondaryActionBackground");
        var secondaryText = Pick(text, "secondaryActionText");
        var secondaryBorder = Pick(line, "secondaryActionBorder");
        var secondaryHoverBackground = Pick(secondaryBackground, "secondaryActionHoverBackground");
        var secondaryHoverText = Pick(secondaryText, "secondaryActionHoverText");
        var secondaryHoverBorder = Pick(secondaryBorder, "secondaryActionHoverBorder");

        var dangerBackground = Pick(primaryBackground, "dangerActionBackground");
        var dangerText = Pick(primaryText, "dangerActionText");
        var dangerBorder = Pick(primaryBorder, "dangerActionBorder");
        var dangerHoverBackground = Pick(dangerBackground, "dangerActionHoverBackground");
        var dangerHoverText = Pick(dangerText, "dangerActionHoverText");
        var dangerHoverBorder = Pick(dangerBorder, "dangerActionHoverBorder");

        static string Pair(string name, string value) => $"--{name}:{value};";
        var variables = string.Concat(
            Pair("page-bg", page), Pair("surface-bg", surface), Pair("subtle-bg", subtle),
            Pair("input-bg", input), Pair("text", text), Pair("muted", muted), Pair("chart-text", muted),
            Pair("line", line), Pair("accent", accent), Pair("accent-text", accentText), Pair("focus", focus),
            Pair("control-bg", controlBackground), Pair("control-text", controlText), Pair("control-border", controlBorder),
            Pair("control-hover-bg", controlHoverBackground), Pair("control-hover-text", controlHoverText), Pair("control-hover-border", controlHoverBorder),
            Pair("selected-bg", selectedBackground), Pair("selected-text", selectedText), Pair("selected-border", selectedBorder),
            Pair("selected-hover-bg", selectedHoverBackground), Pair("selected-hover-text", selectedHoverText), Pair("selected-hover-border", selectedHoverBorder),
            Pair("disabled-bg", disabledBackground), Pair("disabled-text", disabledText), Pair("disabled-border", disabledBorder),
            Pair("primary-bg", primaryBackground), Pair("primary-text", primaryText), Pair("primary-border", primaryBorder),
            Pair("primary-hover-bg", primaryHoverBackground), Pair("primary-hover-text", primaryHoverText), Pair("primary-hover-border", primaryHoverBorder),
            Pair("secondary-bg", secondaryBackground), Pair("secondary-text", secondaryText), Pair("secondary-border", secondaryBorder),
            Pair("secondary-hover-bg", secondaryHoverBackground), Pair("secondary-hover-text", secondaryHoverText), Pair("secondary-hover-border", secondaryHoverBorder),
            Pair("danger-bg", dangerBackground), Pair("danger-text", dangerText), Pair("danger-border", dangerBorder),
            Pair("danger-hover-bg", dangerHoverBackground), Pair("danger-hover-text", dangerHoverText), Pair("danger-hover-border", dangerHoverBorder));
        return (dark ? "dark" : "light", variables);
    }

    private static bool IsSafeCssValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 96)
            return false;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '#' or '(' or ')' or ',' or '.' or '%' or '-' or '_' or ' ' or '/')
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
            PluginPermission.PreviewAllocation,
            PluginPermission.WriteReservations,
            PluginPermission.ReadRecordingHistory,
            PluginPermission.ReadRecordingStatus,
            PluginPermission.ReadTunerStatus,
            PluginPermission.ReadPlaybackProgress,
            PluginPermission.ReadMediaInsights,
            PluginPermission.ReadContentDiscovery,
            PluginPermission.WriteLogs,
            PluginPermission.ReadPluginStorage,
            PluginPermission.WritePluginStorage,
            PluginPermission.UsePathPicker,
            PluginPermission.UseExternalLookup
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
                SurfaceDefinitionId = "main.web"
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

internal sealed record AIrhythmSettings(int Limit = 20, string Preferred = "", string Excluded = "", bool ExternalLookupEnabled = false);
internal sealed record AIrhythmExternalLookupState(
    bool Requested,
    bool PluginDeclaredPermission,
    bool HostAllowed,
    bool Available,
    int ProviderCount,
    string StatusText);
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
    internal int Priority =>
        (AutomaticReservationCount > 0 ? 4000 : 0) + Math.Min(AutomaticReservationCount, 8) * 120
        + (ManualReservationCount > 0 ? 4000 : 0) + Math.Min(ManualReservationCount, 8) * 120
        + (RecordingCount > 0 ? 2500 : 0) + Math.Min(RecordingCount, 8) * 80;
}
internal sealed record AIrhythmExternalEvidenceQuery(
    string Title,
    string ServiceName,
    DateTimeOffset Start);
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
    // Preserve the exact Supported provenance in the existing bounded projection;
    // do not create a second evidence/detail cache for diagnostics or later scoring.
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

internal sealed record AIrhythmAdvancedSnapshot(
    IReadOnlyList<TvAirRecordingSessionDto> ActiveRecordings,
    IReadOnlyList<TvAirRecordingInspectionResultDto> Inspections);

internal sealed record AIrhythmRuntimeSnapshot(
    bool Ready,
    string Error,
    IReadOnlyList<TvAirProgramEventDto> Events,
    IReadOnlyList<TvAirReservationDto> Reservations,
    IReadOnlyList<TvAirReservationDto> ReservationRecords,
    IReadOnlyList<TvAirRecordingHistoryDto> History,
    IReadOnlyList<TvAirRecordingHistoryDto> RecoveryHistory,
    IReadOnlyList<TvAirServiceDto> Channels,
    IReadOnlyList<TvAirTunerStatusDto> Tuners,
    TvAirPlaybackProgressSnapshotDto PlaybackProgress,
    TvAirMediaContextSnapshotDto MediaInsights,
    TvAirContentDiscoveryResultDto ContentDiscovery,
    AIrhythmAdvancedSnapshot Advanced,
    AIrhythmSettings Settings,
    long? SettingsRevision,
    AIrhythmRuntimeDiagnostics Diagnostics);
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
internal sealed record AIrhythmUsageCounterState(long RecordingTotal, long ReservationTotal, int Version = 1);
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
    int ServiceId = 0);
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
    double ExternalEvidenceAdjustmentValue = 0.0);
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

internal static partial class AIrhythmRecommendationEngine
{
    private static readonly object ScoreResultCacheGate = new();
    private static AIrhythmRuntimeSnapshot? CachedScoreSnapshot;
    private static long CachedScoreExternalEvidenceGeneration = -1;
    private static AIrhythmRecommendation[]? CachedScoreRecommendations;

    // 録画履歴から作る正規化済みの特徴量は、番組表更新や予約変更では内容が変わらない。
    // 現在の履歴と完全一致する1世代だけを保持し、タイトル分解・ジャンル正規化・シリーズ正規化の
    // 再実行を避ける。履歴が変われば全件照合で検出して置換するため、古い世代は蓄積しない。
    private static readonly object HistoryEvidenceGate = new();
    private static AIrhythmHistoryEvidenceSignature[] CachedHistoryEvidenceSignatures = Array.Empty<AIrhythmHistoryEvidenceSignature>();
    private static AIrhythmHistoryEvidenceItem[] CachedHistoryEvidence = Array.Empty<AIrhythmHistoryEvidenceItem>();
    private static string CachedHistoryEvidenceIdentityFingerprint = string.Empty;

    private readonly record struct AIrhythmHistoryEvidenceSignature(
        long StartUtcTicks,
        ushort NetworkId,
        ushort TransportStreamId,
        ushort ServiceId,
        string ProgramTitle,
        string Genre);

    private sealed record AIrhythmHistoryEvidenceItem(
        DateTimeOffset Start,
        AIrhythmServiceIdentity ServiceIdentity,
        int Hour,
        string ProgramTitle,
        string SeriesKey,
        string GenreKey,
        IReadOnlyList<string> Terms);

    private static IReadOnlyList<AIrhythmHistoryEvidenceItem> GetHistoryEvidence(IReadOnlyList<TvAirRecordingHistoryDto> history, AIrhythmEvidenceIdentityContext identityContext)
    {
        var identityFingerprint = BuildEvidenceIdentityFingerprint(identityContext);
        lock (HistoryEvidenceGate)
        {
            var previousCount = CachedHistoryEvidence.Length;
            var hadCache = CachedHistoryEvidenceSignatures.Length > 0 || CachedHistoryEvidence.Length > 0;
            var missReason = hadCache ? "history_count_changed" : "initial";

            if (string.Equals(CachedHistoryEvidenceIdentityFingerprint, identityFingerprint, StringComparison.Ordinal)
                && CachedHistoryEvidenceSignatures.Length == history.Count
                && CachedHistoryEvidence.Length == history.Count)
            {
                var unchanged = true;
                for (var i = 0; i < history.Count; i++)
                {
                    if (!HistoryEvidenceMatches(CachedHistoryEvidenceSignatures[i], history[i]))
                    {
                        unchanged = false;
                        missReason = "history_changed";
                        break;
                    }
                }
                if (unchanged)
                {
                    return CachedHistoryEvidence;
                }
            }

            var signatures = new AIrhythmHistoryEvidenceSignature[history.Count];
            var evidence = new AIrhythmHistoryEvidenceItem[history.Count];
            for (var i = 0; i < history.Count; i++)
            {
                var item = history[i];
                var start = item.ActualStart ?? item.Start;
                signatures[i] = HistoryEvidenceSignatureOf(item, start);
                var facts = CanonicalEvidenceFacts(item, identityContext);
                evidence[i] = new AIrhythmHistoryEvidenceItem(
                    start,
                    facts.ServiceIdentity,
                    facts.Hour,
                    item.ProgramTitle ?? string.Empty,
                    facts.WorkKey,
                    facts.GenreKey,
                    facts.Terms);
            }

            CachedHistoryEvidenceSignatures = signatures;
            CachedHistoryEvidence = evidence;
            CachedHistoryEvidenceIdentityFingerprint = identityFingerprint;
            return CachedHistoryEvidence;
        }
    }

    private static AIrhythmHistoryEvidenceSignature HistoryEvidenceSignatureOf(TvAirRecordingHistoryDto item, DateTimeOffset start)
        => new(
            start.UtcDateTime.Ticks,
            item.NetworkId,
            item.TransportStreamId,
            item.ServiceId,
            item.ProgramTitle ?? string.Empty,
            item.Genre ?? string.Empty);

    private static bool HistoryEvidenceMatches(AIrhythmHistoryEvidenceSignature signature, TvAirRecordingHistoryDto item)
    {
        var start = item.ActualStart ?? item.Start;
        return signature.StartUtcTicks == start.UtcDateTime.Ticks
            && signature.NetworkId == item.NetworkId
            && signature.TransportStreamId == item.TransportStreamId
            && signature.ServiceId == item.ServiceId
            && string.Equals(signature.ProgramTitle, item.ProgramTitle ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(signature.Genre, item.Genre ?? string.Empty, StringComparison.Ordinal);
    }
    private static bool ContainsAny(string value, params string[] words)
        => words.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string ProgramTitleElement(string tagName, string title, string? cssClass = null, int scrollThreshold = 18)
    {
        var safeTag = string.Equals(tagName, "span", StringComparison.OrdinalIgnoreCase) ? "span"
            : string.Equals(tagName, "b", StringComparison.OrdinalIgnoreCase) ? "b"
            : "strong";
        var encoded = AIrhythmHtml.Encode(title);
        var displayLength = Math.Max(0, title.EnumerateRunes().Count());
        var threshold = Math.Max(8, scrollThreshold);

        // Keep short titles byte-for-byte equivalent in structure to the release baseline.
        // Long titles use one canonical stable hover owner. Only the absolutely-positioned
        // motion layer moves; the owner never moves, so hover cannot invalidate itself.
        if (displayLength <= threshold)
        {
            var plainClass = string.IsNullOrWhiteSpace(cssClass) ? string.Empty : $" class=\"{AIrhythmHtml.Encode(cssClass)}\"";
            return $"<{safeTag}{plainClass}>{encoded}</{safeTag}>";
        }

        var distanceClass = displayLength >= threshold * 2
            ? " airhythm-title-scroll-far"
            : displayLength >= threshold * 3 / 2
                ? " airhythm-title-scroll-medium"
                : string.Empty;
        var classes = string.Join(" ", new[] { cssClass, "airhythm-title-scroll-owner" }.Where(x => !string.IsNullOrWhiteSpace(x))) + distanceClass;
        return $"<{safeTag} class=\"{AIrhythmHtml.Encode(classes)}\"><em class=\"airhythm-title-static\">{encoded}</em><em class=\"airhythm-title-motion\" aria-hidden=\"true\">{encoded}</em></{safeTag}>";
    }

    public static AIrhythmServerRender Build(RuntimeUiRenderContext context, AIrhythmRuntimeSnapshot snapshot, string? rawRhythmQuery)
    {
        var identityContext = BuildEvidenceIdentityContext(snapshot);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog($"COMMON_EVIDENCE_CANONICAL_CONTEXT numericServices={identityContext.NumericLocalValuesByService.Count} workAliases={identityContext.WorkAliases.Count} externalCanonicalTitles={identityContext.ExternalCanonicalTitles.Count} consumers=score,localEvidence,selection,dashboard,discovery,search,interest,externalUserIntent workIdentity=canonical_single_source genre=NormalizeGenre terms=Tokens service=ServiceIdentity hour=canonical_hour timeBand=CanonicalTimeBand");
#endif
        var allRecommendations = GetOrBuildScoreResult(snapshot, identityContext);
        var recommendations = SelectRecommendations(allRecommendations, Math.Clamp(snapshot.Settings.Limit, 10, 30), identityContext);
        var recommendationDiscoveryPool = allRecommendations
            .Where(x => x.IsConvincing || x.IsPlausibleDiscovery)
            .ToArray();
        var cards = string.Join(string.Empty, recommendations.Select(item => RenderCard(context, item, snapshot)));
        var charts = BuildCharts(snapshot, identityContext);
        var discovery = BuildDiscovery(context, snapshot, allRecommendations, recommendationDiscoveryPool, identityContext);
        var search = BuildRhythmSearch(context, snapshot, allRecommendations, rawRhythmQuery, identityContext);
        var message = recommendations.Length > 0
            ? "<div id=\"message\" class=\"message\" hidden></div>"
            : $"<div id=\"message\" class=\"message\">{AIrhythmHtml.Encode(snapshot.Ready ? "条件に合う候補がありません。" : snapshot.Error)}</div>";
        var status = snapshot.Ready
            ? $"おすすめ候補 {allRecommendations.Length}件・{DateTimeOffset.Now:HH:mm} 更新"
            : AIrhythmHtml.Encode(snapshot.Error);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog($"CANONICAL_FACT_CACHE hits={identityContext.CanonicalFactsHits} misses={identityContext.CanonicalFactsMisses} programCached={identityContext.ProgramFactsCount} reservationCached={identityContext.ReservationFactsCount} historyCached={identityContext.HistoryFactsCount} scope=single_render_snapshot semantics=unchanged");
#endif
        return new(cards, charts, discovery, search, message, status);
    }

    internal static AIrhythmExternalEvidenceEvaluationFlags ToExternalEvidenceEvaluationFlags(AIrhythmExternalEvidenceSummaryStatus status)
        => status switch
        {
            AIrhythmExternalEvidenceSummaryStatus.UnresolvedOnly => AIrhythmExternalEvidenceEvaluationFlags.Unresolved,
            AIrhythmExternalEvidenceSummaryStatus.Supported => AIrhythmExternalEvidenceEvaluationFlags.Supported,
            AIrhythmExternalEvidenceSummaryStatus.Conflicting => AIrhythmExternalEvidenceEvaluationFlags.Conflicting,
            _ => AIrhythmExternalEvidenceEvaluationFlags.None
        };

    internal static AIrhythmExternalEvidenceConfidenceGate ToExternalEvidenceConfidenceGate(AIrhythmExternalEvidenceEvaluationFlags flags)
        => flags switch
        {
            AIrhythmExternalEvidenceEvaluationFlags.Supported => AIrhythmExternalEvidenceConfidenceGate.Allowed,
            AIrhythmExternalEvidenceEvaluationFlags.Unresolved => AIrhythmExternalEvidenceConfidenceGate.Neutral,
            AIrhythmExternalEvidenceEvaluationFlags.Conflicting => AIrhythmExternalEvidenceConfidenceGate.Blocked,
            _ => AIrhythmExternalEvidenceConfidenceGate.NotApplicable
        };

    internal static AIrhythmExternalEvidenceAdjustmentCandidate ToExternalEvidenceAdjustmentCandidate(AIrhythmExternalEvidenceConfidenceGate gate)
        => gate == AIrhythmExternalEvidenceConfidenceGate.Allowed
            ? AIrhythmExternalEvidenceAdjustmentCandidate.Eligible
            : AIrhythmExternalEvidenceAdjustmentCandidate.None;

    internal static AIrhythmExternalEvidenceAdjustmentKind NormalizeExternalEvidenceAdjustmentKind(
        AIrhythmExternalEvidenceAdjustmentCandidate candidate,
        AIrhythmExternalEvidenceAdjustmentKind projectedKind)
        => candidate == AIrhythmExternalEvidenceAdjustmentCandidate.Eligible
            ? projectedKind
            : AIrhythmExternalEvidenceAdjustmentKind.None;

    internal static AIrhythmExternalEvidenceAdjustmentStrength ToExternalEvidenceAdjustmentStrength(
        AIrhythmExternalEvidenceAdjustmentCandidate candidate,
        AIrhythmExternalEvidenceAdjustmentKind kind)
    {
        if (candidate != AIrhythmExternalEvidenceAdjustmentCandidate.Eligible)
            return AIrhythmExternalEvidenceAdjustmentStrength.None;

        return kind switch
        {
            AIrhythmExternalEvidenceAdjustmentKind.IdentitySupport => AIrhythmExternalEvidenceAdjustmentStrength.Weak,
            AIrhythmExternalEvidenceAdjustmentKind.EpisodeSupport => AIrhythmExternalEvidenceAdjustmentStrength.Moderate,
            AIrhythmExternalEvidenceAdjustmentKind.RelationSupport => AIrhythmExternalEvidenceAdjustmentStrength.Weak,
            _ => AIrhythmExternalEvidenceAdjustmentStrength.None
        };
    }

    private const double ExternalEvidenceWeakRawCoordinateValue = 0.25d;
    private const double ExternalEvidenceModerateRawCoordinateValue = 0.75d;

    internal static double ToExternalEvidenceAdjustmentValue(AIrhythmExternalEvidenceAdjustmentStrength strength)
        => strength switch
        {
            AIrhythmExternalEvidenceAdjustmentStrength.Weak => ExternalEvidenceWeakRawCoordinateValue,
            AIrhythmExternalEvidenceAdjustmentStrength.Moderate => ExternalEvidenceModerateRawCoordinateValue,
            _ => 0.0d
        };

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
    internal static AIrhythmRawCoordinateShadowMetrics AnalyzeExternalEvidenceRawCoordinateShadowSweep(IReadOnlyList<AIrhythmRecommendation> orderedCandidates, double weakValue, double moderateValue)
    {
        if (orderedCandidates.Count == 0)
            return new(weakValue, moderateValue, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        static double AdjustmentFor(AIrhythmRecommendation item, double weak, double moderate)
            => item.ExternalEvidenceAdjustmentStrength switch
            {
                AIrhythmExternalEvidenceAdjustmentStrength.Weak => weak,
                AIrhythmExternalEvidenceAdjustmentStrength.Moderate => moderate,
                _ => 0.0d
            };

        var adjustedRaw = orderedCandidates
            .Select(item => (Item: item, Raw: item.DeviationRawScore + AdjustmentFor(item, weakValue, moderateValue)))
            .ToArray();
        var mean = adjustedRaw.Average(x => x.Raw);
        var variance = adjustedRaw.Average(x =>
        {
            var delta = x.Raw - mean;
            return delta * delta;
        });
        var standardDeviation = Math.Sqrt(variance);
        int ShadowDisplay(double raw)
        {
            if (adjustedRaw.Length <= 1 || standardDeviation < 0.000001d)
                return 50;
            var deviation = (raw - mean) / standardDeviation;
            return Math.Clamp((int)Math.Round(50 + 10 * deviation), 0, 100);
        }

        var baselineRank = orderedCandidates
            .Select((item, index) => (key: RecommendationIdentityKey(item), rank: index))
            .ToDictionary(x => x.key, x => x.rank, StringComparer.OrdinalIgnoreCase);
        var shadowRows = adjustedRaw
            .Select(x => (x.Item, x.Raw, DisplayScore: ShadowDisplay(x.Raw)))
            .ToArray();
        var shadowOrdered = shadowRows
            .OrderByDescending(x => x.DisplayScore)
            .ThenBy(x => x.Item.Start)
            .Select(x => x.Item)
            .ToArray();
        var shadowRank = shadowOrdered
            .Select((item, index) => (key: RecommendationIdentityKey(item), rank: index))
            .ToDictionary(x => x.key, x => x.rank, StringComparer.OrdinalIgnoreCase);

        var displayChanged = 0;
        var rankChanged = 0;
        var maxRankDelta = 0;
        var maxDisplayScoreDelta = 0;
        foreach (var row in shadowRows)
        {
            var displayDelta = Math.Abs(row.DisplayScore - row.Item.Score);
            if (displayDelta != 0)
                displayChanged++;
            maxDisplayScoreDelta = Math.Max(maxDisplayScoreDelta, displayDelta);

            var key = RecommendationIdentityKey(row.Item);
            if (!baselineRank.TryGetValue(key, out var before) || !shadowRank.TryGetValue(key, out var after) || before == after)
                continue;
            rankChanged++;
            maxRankDelta = Math.Max(maxRankDelta, Math.Abs(before - after));
        }

        static HashSet<string> TopKeys(IReadOnlyList<AIrhythmRecommendation> values, int count)
            => values.Take(Math.Min(count, values.Count)).Select(RecommendationIdentityKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        static (int entrants, int exits) Boundary(IReadOnlyList<AIrhythmRecommendation> baseline, IReadOnlyList<AIrhythmRecommendation> shadow, int count)
        {
            var before = TopKeys(baseline, count);
            var after = TopKeys(shadow, count);
            return (after.Except(before, StringComparer.OrdinalIgnoreCase).Count(), before.Except(after, StringComparer.OrdinalIgnoreCase).Count());
        }

        var top10 = Boundary(orderedCandidates, shadowOrdered, 10);
        var top20 = Boundary(orderedCandidates, shadowOrdered, 20);
        var top50 = Boundary(orderedCandidates, shadowOrdered, 50);
        return new(
            weakValue,
            moderateValue,
            orderedCandidates.Count(item => AdjustmentFor(item, weakValue, moderateValue) > 0.000001d),
            displayChanged,
            rankChanged,
            top10.entrants, top10.exits,
            top20.entrants, top20.exits,
            top50.entrants, top50.exits,
            maxRankDelta,
            maxDisplayScoreDelta);
    }

    internal static AIrhythmRawCoordinateShadowDetail[] AnalyzeExternalEvidenceRawCoordinateShadowSweepDetails(IReadOnlyList<AIrhythmRecommendation> orderedCandidates, double weakValue, double moderateValue, int limit = 8)
    {
        if (orderedCandidates.Count == 0 || limit <= 0)
            return Array.Empty<AIrhythmRawCoordinateShadowDetail>();

        static double AdjustmentFor(AIrhythmRecommendation item, double weak, double moderate)
            => item.ExternalEvidenceAdjustmentStrength switch
            {
                AIrhythmExternalEvidenceAdjustmentStrength.Weak => weak,
                AIrhythmExternalEvidenceAdjustmentStrength.Moderate => moderate,
                _ => 0.0d
            };

        var adjustedRaw = orderedCandidates
            .Select(item => (Item: item, Raw: item.DeviationRawScore + AdjustmentFor(item, weakValue, moderateValue)))
            .ToArray();
        var mean = adjustedRaw.Average(x => x.Raw);
        var variance = adjustedRaw.Average(x =>
        {
            var delta = x.Raw - mean;
            return delta * delta;
        });
        var standardDeviation = Math.Sqrt(variance);
        int ShadowDisplay(double raw)
        {
            if (adjustedRaw.Length <= 1 || standardDeviation < 0.000001d)
                return 50;
            var deviation = (raw - mean) / standardDeviation;
            return Math.Clamp((int)Math.Round(50 + 10 * deviation), 0, 100);
        }

        var baselineRank = orderedCandidates
            .Select((item, index) => (key: RecommendationIdentityKey(item), rank: index + 1))
            .ToDictionary(x => x.key, x => x.rank, StringComparer.OrdinalIgnoreCase);
        var shadowRows = adjustedRaw
            .Select(x => (x.Item, x.Raw, DisplayScore: ShadowDisplay(x.Raw)))
            .ToArray();
        var shadowRank = shadowRows
            .OrderByDescending(x => x.DisplayScore)
            .ThenBy(x => x.Item.Start)
            .Select((x, index) => (key: RecommendationIdentityKey(x.Item), rank: index + 1))
            .ToDictionary(x => x.key, x => x.rank, StringComparer.OrdinalIgnoreCase);

        return shadowRows
            .Where(row => AdjustmentFor(row.Item, weakValue, moderateValue) > 0.000001d)
            .Select(row =>
            {
                var key = RecommendationIdentityKey(row.Item);
                return new AIrhythmRawCoordinateShadowDetail(
                    row.Item.Title, weakValue, moderateValue,
                    baselineRank.GetValueOrDefault(key), shadowRank.GetValueOrDefault(key),
                    row.Item.DeviationRawScore, row.Raw,
                    row.Item.Score, row.DisplayScore,
                    row.Item.ExternalEvidenceAdjustmentKind, row.Item.ExternalEvidenceAdjustmentStrength, row.Item.ExternalEvidenceSupportingVerdictReason);
            })
            .OrderByDescending(item => Math.Abs(item.CurrentRank - item.ShadowRank))
            .ThenBy(item => item.CurrentRank)
            .Take(Math.Min(limit, 8))
            .ToArray();
    }

    private static readonly (double Weak, double Moderate)[] ExternalEvidenceRawCoordinateCandidateScenarios =
    {
        (ExternalEvidenceWeakRawCoordinateValue, 0.50d),
        (ExternalEvidenceWeakRawCoordinateValue, ExternalEvidenceModerateRawCoordinateValue),
        (ExternalEvidenceWeakRawCoordinateValue, 1.00d)
    };

    private static void WriteExternalEvidenceShadowImpactDiagnostics(IReadOnlyList<AIrhythmRecommendation> orderedCandidates)
    {
        AIrhythmDataState.WriteDeveloperLog($"external evidence raw-coordinate applied policy coordinate=DeviationRawScore_post_confidence_cap_pre_deviation_normalization weak={ExternalEvidenceWeakRawCoordinateValue:0.00} moderate={ExternalEvidenceModerateRawCoordinateValue:0.00} weakState=applied moderateState=applied_with_runtime_episode_validation_pending displayedScoreAdditivePath=retired actualOrderingMutation=score_derived actualScoreMutation=raw_coordinate_supported_only");

        foreach (var scenario in ExternalEvidenceRawCoordinateCandidateScenarios)
        {
            var metrics = AnalyzeExternalEvidenceRawCoordinateShadowSweep(orderedCandidates, scenario.Weak, scenario.Moderate);
            AIrhythmDataState.WriteDeveloperLog($"external evidence raw-coordinate sensitivity sweep weak={scenario.Weak:0.00} moderate={scenario.Moderate:0.00} nonZeroCandidates={metrics.NonZeroCandidates} displayScoreChangedCandidates={metrics.DisplayScoreChangedCandidates} rankChangedCandidates={metrics.RankChangedCandidates} top10={metrics.Top10Entrants}/{metrics.Top10Exits} top20={metrics.Top20Entrants}/{metrics.Top20Exits} top50={metrics.Top50Entrants}/{metrics.Top50Exits} maxRankDelta={metrics.MaxRankDelta} maxDisplayScoreDelta={metrics.MaxDisplayScoreDelta} actualOrderingMutation=False actualScoreMutation=False");
            foreach (var rawDetail in AnalyzeExternalEvidenceRawCoordinateShadowSweepDetails(orderedCandidates, scenario.Weak, scenario.Moderate, 8))
                AIrhythmDataState.WriteDeveloperLog($"external evidence raw-coordinate sensitivity detail weak={scenario.Weak:0.00} moderate={scenario.Moderate:0.00} title={rawDetail.Title} currentRank={rawDetail.CurrentRank} shadowRank={rawDetail.ShadowRank} currentDeviationRawScore={rawDetail.CurrentDeviationRawScore:0.000} shadowDeviationRawScore={rawDetail.ShadowDeviationRawScore:0.000} currentDisplayScore={rawDetail.CurrentDisplayScore} shadowDisplayScore={rawDetail.ShadowDisplayScore} kind={rawDetail.Kind} strength={rawDetail.Strength} verdictReason={rawDetail.VerdictReason} actualOrderingMutation=False actualScoreMutation=False");
        }
    }
#endif

    private static string RecommendationSelectionWorkKey(
        AIrhythmRecommendation item,
        AIrhythmEvidenceIdentityContext identityContext)
    {
        var service = item.EventIdentity is null ? default : ServiceIdentityOf(item.EventIdentity);
        var workKey = CanonicalWorkKey(item.Title, service, identityContext);
        return workKey.Length >= 3 ? workKey : RecommendationIdentityKey(item);
    }

    private static AIrhythmRecommendation[] InterleaveRecommendationsByWork(
        IReadOnlyList<AIrhythmRecommendation> orderedCandidates,
        int limit,
        AIrhythmEvidenceIdentityContext identityContext)
    {
        if (limit <= 0 || orderedCandidates.Count == 0)
            return Array.Empty<AIrhythmRecommendation>();

        var queues = new Dictionary<string, Queue<AIrhythmRecommendation>>(StringComparer.OrdinalIgnoreCase);
        var workOrder = new List<string>();
        foreach (var item in orderedCandidates)
        {
            var workKey = RecommendationSelectionWorkKey(item, identityContext);
            if (!queues.TryGetValue(workKey, out var queue))
            {
                queue = new Queue<AIrhythmRecommendation>();
                queues[workKey] = queue;
                workOrder.Add(workKey);
            }
            queue.Enqueue(item);
        }

        var selected = new List<AIrhythmRecommendation>(Math.Min(limit, orderedCandidates.Count));
        while (selected.Count < limit)
        {
            var added = false;
            foreach (var workKey in workOrder)
            {
                var queue = queues[workKey];
                if (queue.Count == 0) continue;
                selected.Add(queue.Dequeue());
                added = true;
                if (selected.Count >= limit) break;
            }
            if (!added) break;
        }
        return selected.ToArray();
    }

    private static AIrhythmRecommendation[] SelectRecommendations(IReadOnlyList<AIrhythmRecommendation> candidates, int limit, AIrhythmEvidenceIdentityContext identityContext)
    {
        var convincingPool = candidates.Where(x => x.IsConvincing).ToArray();
        var discoveryPool = candidates.Where(x => !x.IsConvincing && x.IsPlausibleDiscovery).ToArray();
        var convincingDensity = Math.Min(convincingPool.Length, limit);

        // 「納得」が十分に成立してからだけ、説明できる意外性を少量混ぜる。
        // 件数比率を固定せず、その時点の信頼できる候補密度を優先する。
        var discoveryAllowance = convincingDensity switch
        {
            < 4 => 0,
            < 8 => 1,
            < 14 => 2,
            _ => Math.Min(4, Math.Max(2, limit / 6))
        };
        discoveryAllowance = Math.Min(discoveryAllowance, discoveryPool.Length);

        // Scoreそのものは変更せず、同一Workの第2候補以降だけを次ラウンドへ回す。
        // まず異なるWorkの最高順位候補を一巡し、候補不足時だけ2巡目・3巡目を戻す。
        // 固定件数上限ではないため、候補母集団が少ない場合は同一Workも通常どおり復帰する。
        var convincingTarget = Math.Max(0, limit - discoveryAllowance);
        var convincing = InterleaveRecommendationsByWork(convincingPool, convincingTarget, identityContext);
        var discovery = InterleaveRecommendationsByWork(discoveryPool, discoveryAllowance, identityContext);
        var combinedByScore = convincing
            .Concat(discovery)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Start)
            .ToArray();
        var selected = InterleaveRecommendationsByWork(combinedByScore, limit, identityContext).ToList();

        // 選抜枠が不足した場合だけ、未選択の納得候補を同じWork-aware順で戻す。
        if (selected.Count < limit)
        {
            var selectedKeys = selected.Select(RecommendationIdentityKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remainingConvincing = convincingPool
                .Where(x => selectedKeys.Add(RecommendationIdentityKey(x)))
                .ToArray();
            if (remainingConvincing.Length > 0)
            {
                var refill = InterleaveRecommendationsByWork(remainingConvincing, limit - selected.Count, identityContext);
                selected.AddRange(refill);
                selected = InterleaveRecommendationsByWork(
                    selected.OrderByDescending(x => x.Score).ThenBy(x => x.Start).ToArray(),
                    limit,
                    identityContext).ToList();
            }
        }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var selectedOccupancy = selected
            .Select((item, index) => new
            {
                Work = RecommendationSelectionWorkKey(item, identityContext),
                Rank = index + 1
            })
            .GroupBy(x => x.Work, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Work = group.Key, Count = group.Count(), BestRank = group.Min(x => x.Rank) })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.BestRank)
            .ToArray();
        AIrhythmDataState.WriteDeveloperLog(
            $"LOCAL_WORK_SELECTION_INTERLEAVE selected={selected.Count} uniqueWorks={selectedOccupancy.Length} repeatedWorks={selectedOccupancy.Count(x => x.Count > 1)} maxPerWork={(selectedOccupancy.Length == 0 ? 0 : selectedOccupancy.Max(x => x.Count))} samples=[{string.Join(",", selectedOccupancy.Take(8).Select(x => $"{x.Work}:{x.Count}@{x.BestRank}"))}] policy=work_round_interleave_no_score_mutation");
#endif

        return selected.ToArray();
    }

    private static string RecommendationIdentityKey(AIrhythmRecommendation item)
        => item.EventIdentity is not null
            ? $"{item.EventIdentity.NetworkId}:{item.EventIdentity.TransportStreamId}:{item.EventIdentity.ServiceId}:{item.EventIdentity.EventNumber}:{item.EventIdentity.Start.UtcDateTime.Ticks}"
            : $"{item.SeriesKey}|unknown-service|{item.Start.UtcDateTime.Ticks}";

    private static double ReservationEvidenceWeight(TvAirReservationDto value) => value.Intent switch
    {
        TvAirReservationIntent.System => 0.0,
        TvAirReservationIntent.ProgramTimeSlot => 0.35,
        TvAirReservationIntent.AutomaticSearch => 1.6,
        TvAirReservationIntent.KeywordRule => 1.6,
        TvAirReservationIntent.InteractiveProgramEvent => 1.0,
        _ => 0.75
    };

    private enum AIrhythmLocalEvidenceStrength
    {
        None,
        Weak,
        Moderate,
        Strong
    }

    private enum AIrhythmLocalEvidenceFamily
    {
        None,
        ContinuityFamily,
        ExplicitInterestFamily
    }

    private sealed class AIrhythmLocalWorkAggregate
    {
        public int RecordingCount { get; set; }
        public int ReservationCount { get; set; }
        public bool HasNonAutomatedReservation { get; set; }
        public HashSet<DateOnly> ActiveDays { get; } = new();
        public HashSet<int> ActiveWeeks { get; } = new();
        public DateTimeOffset? LastSeen { get; set; }

        public void AddActivity(DateTimeOffset when)
        {
            ActiveDays.Add(DateOnly.FromDateTime(when.LocalDateTime.Date));
            var localDate = when.LocalDateTime;
            ActiveWeeks.Add((ISOWeek.GetYear(localDate) * 100) + ISOWeek.GetWeekOfYear(localDate));
            if (LastSeen is null || when > LastSeen.Value)
                LastSeen = when;
        }
    }

    internal readonly record struct AIrhythmLocalWorkAliasKey(
        string WorkKey,
        AIrhythmServiceIdentity ServiceIdentity);

    internal sealed class AIrhythmEvidenceIdentityContext
    {
        public AIrhythmEvidenceIdentityContext(
            IReadOnlyDictionary<string, int[]> numericLocalValuesByService,
            IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string> workAliases,
            IReadOnlyDictionary<string, string> externalCanonicalTitles)
        {
            NumericLocalValuesByService = numericLocalValuesByService;
            WorkAliases = workAliases;
            ExternalCanonicalTitles = externalCanonicalTitles;
        }

        public IReadOnlyDictionary<string, int[]> NumericLocalValuesByService { get; }
        public IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string> WorkAliases { get; }
        public IReadOnlyDictionary<string, string> ExternalCanonicalTitles { get; }

        // Build() creates one identity context per immutable runtime snapshot. Cache canonical facts
        // by DTO reference so Score / charts / discovery / search share the exact same canonical
        // result instead of repeating title normalization, tokenization and Work resolution.
        // The cache is intentionally snapshot-scoped; it never survives invalidation or refresh.
        internal Dictionary<TvAirProgramEventDto, AIrhythmCanonicalEvidenceFacts> ProgramFacts { get; }
            = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<TvAirReservationDto, AIrhythmCanonicalEvidenceFacts> ReservationFacts { get; }
            = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<TvAirRecordingHistoryDto, AIrhythmCanonicalEvidenceFacts> HistoryFacts { get; }
            = new(ReferenceEqualityComparer.Instance);
        internal int ProgramFactsCount => ProgramFacts.Count;
        internal int ReservationFactsCount => ReservationFacts.Count;
        internal int HistoryFactsCount => HistoryFacts.Count;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        internal int CanonicalFactsHits { get; set; }
        internal int CanonicalFactsMisses { get; set; }
        internal long CanonicalPreEvaluationTitleBytes { get; set; }
        internal long CanonicalLocalWorkKeyBytes { get; set; }
        internal long CanonicalAliasResolutionBytes { get; set; }
        internal long CanonicalGenreBytes { get; set; }
        internal long CanonicalTokensBytes { get; set; }
        internal long CanonicalReplayBytes { get; set; }

        internal void ResetCanonicalAllocationBreakdown()
        {
            CanonicalPreEvaluationTitleBytes = 0;
            CanonicalLocalWorkKeyBytes = 0;
            CanonicalAliasResolutionBytes = 0;
            CanonicalGenreBytes = 0;
            CanonicalTokensBytes = 0;
            CanonicalReplayBytes = 0;
        }
#endif
    }

    internal static AIrhythmEvidenceIdentityContext BuildEvidenceIdentityContext(AIrhythmRuntimeSnapshot snapshot)
    {
        var numeric = BuildNumericParenthesizedLocalSequenceValuesByService(snapshot.Events);
        var aliases = BuildLocalWorkAliases(snapshot, numeric);
        var externalCanonicalTitles = AIrhythmDataState.GetExternalPreEvaluationCanonicalTitles();
        return new AIrhythmEvidenceIdentityContext(numeric, aliases, externalCanonicalTitles);
    }

    private static string PreEvaluationTitle(string? title, AIrhythmEvidenceIdentityContext context)
    {
        var raw = (title ?? string.Empty).Trim();
        if (raw.Length == 0)
            return string.Empty;
        var key = ExternalPreEvaluationTitleKey(raw);
        return key.Length > 0 && context.ExternalCanonicalTitles.TryGetValue(key, out var canonical) && !string.IsNullOrWhiteSpace(canonical)
            ? canonical
            : raw;
    }

    internal static string ExternalPreEvaluationTitleKey(string? title)
        => new((title ?? string.Empty).Normalize(NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    internal static string CanonicalWorkKey(string? title, AIrhythmServiceIdentity service, AIrhythmEvidenceIdentityContext context)
    {
        var analysisTitle = PreEvaluationTitle(title, context);
        return CanonicalizeLocalWorkKey(LocalWorkKey(analysisTitle, service, context.NumericLocalValuesByService), service, context.WorkAliases);
    }

    internal static string CanonicalWorkKey(TvAirProgramEventDto item, AIrhythmEvidenceIdentityContext context)
        => CanonicalEvidenceFacts(item, context).WorkKey;

    internal static string CanonicalWorkKey(TvAirReservationDto item, AIrhythmEvidenceIdentityContext context)
        => CanonicalEvidenceFacts(item, context).WorkKey;

    internal static string CanonicalWorkKey(TvAirRecordingHistoryDto item, AIrhythmEvidenceIdentityContext context)
        => CanonicalEvidenceFacts(item, context).WorkKey;

    internal sealed record AIrhythmCanonicalEvidenceFacts(
        string WorkKey,
        string GenreKey,
        AIrhythmServiceIdentity ServiceIdentity,
        int Hour,
        string TimeBand,
        IReadOnlyList<string> Terms,
        bool IsReplay,
        bool UsesExternalCanonicalTitle);

    internal sealed record AIrhythmCanonicalRecordingWorkAggregate(
        string GenreKey,
        string WorkKey,
        string DisplayTitle,
        int RecordingCount,
        int DistinctServiceCount,
        IReadOnlyDictionary<AIrhythmServiceIdentity, int> ServiceRecordingCounts);

    internal sealed record AIrhythmCanonicalRecordingGenreAggregate(
        string GenreKey,
        int RecordingCount,
        IReadOnlyList<AIrhythmCanonicalRecordingWorkAggregate> Works);

    internal sealed record AIrhythmCanonicalRecordingTotalAggregate(
        string WorkKey,
        string DisplayTitle,
        int RecordingCount);

    private static string CanonicalTimeBand(int hour)
        => hour switch
        {
            >= 5 and < 10 => "朝",
            >= 10 and < 17 => "昼",
            >= 17 and < 20 => "夕方",
            >= 20 => "夜",
            _ => "深夜"
        };

    private static AIrhythmCanonicalEvidenceFacts CanonicalEvidenceFacts(
        string? title,
        string? genre,
        AIrhythmServiceIdentity service,
        DateTimeOffset start,
        AIrhythmEvidenceIdentityContext context)
    {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalDetailStart = GC.GetAllocatedBytesForCurrentThread();
#endif
        var rawTitle = (title ?? string.Empty).Trim();
        var analysisTitle = PreEvaluationTitle(rawTitle, context);
        var usesExternalCanonicalTitle = !string.Equals(analysisTitle, rawTitle, StringComparison.Ordinal);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterPreEvaluation = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalPreEvaluationTitleBytes += canonicalAfterPreEvaluation - canonicalDetailStart;
#endif
        var localWorkKey = LocalWorkKey(analysisTitle, service, context.NumericLocalValuesByService);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterLocalWorkKey = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalLocalWorkKeyBytes += canonicalAfterLocalWorkKey - canonicalAfterPreEvaluation;
#endif
        var canonicalWorkKey = CanonicalizeLocalWorkKey(localWorkKey, service, context.WorkAliases);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterAliasResolution = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalAliasResolutionBytes += canonicalAfterAliasResolution - canonicalAfterLocalWorkKey;
#endif
        var genreKey = NormalizeGenre(genre);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterGenre = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalGenreBytes += canonicalAfterGenre - canonicalAfterAliasResolution;
#endif
        var terms = Tokens(analysisTitle).ToArray();
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterTokens = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalTokensBytes += canonicalAfterTokens - canonicalAfterGenre;
#endif
        var isReplay = IsReplayTitle(title);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterReplay = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalReplayBytes += canonicalAfterReplay - canonicalAfterTokens;
#endif
        return new AIrhythmCanonicalEvidenceFacts(
            canonicalWorkKey,
            genreKey,
            service,
            start.Hour,
            CanonicalTimeBand(start.Hour),
            terms,
            isReplay,
            usesExternalCanonicalTitle);
    }

    private static AIrhythmCanonicalEvidenceFacts CanonicalEvidenceFacts(TvAirProgramEventDto item, AIrhythmEvidenceIdentityContext context)
    {
        if (context.ProgramFacts.TryGetValue(item, out var cached))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            context.CanonicalFactsHits++;
#endif
            return cached;
        }
        var facts = CanonicalEvidenceFacts(item.Title, item.Genre, ServiceIdentityOf(item), item.Start, context);
        context.ProgramFacts[item] = facts;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        context.CanonicalFactsMisses++;
#endif
        return facts;
    }

    private static AIrhythmCanonicalEvidenceFacts CanonicalEvidenceFacts(TvAirReservationDto item, AIrhythmEvidenceIdentityContext context)
    {
        if (context.ReservationFacts.TryGetValue(item, out var cached))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            context.CanonicalFactsHits++;
#endif
            return cached;
        }
        var facts = CanonicalEvidenceFacts(item.ProgramTitle, string.Empty, ServiceIdentityOf(item), item.Start, context);
        context.ReservationFacts[item] = facts;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        context.CanonicalFactsMisses++;
#endif
        return facts;
    }

    private static AIrhythmCanonicalEvidenceFacts CanonicalEvidenceFacts(TvAirRecordingHistoryDto item, AIrhythmEvidenceIdentityContext context)
    {
        if (context.HistoryFacts.TryGetValue(item, out var cached))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            context.CanonicalFactsHits++;
#endif
            return cached;
        }
        var start = item.ActualStart ?? item.Start;
        var facts = CanonicalEvidenceFacts(item.ProgramTitle, item.Genre, ServiceIdentityOf(item), start, context);
        context.HistoryFacts[item] = facts;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        context.CanonicalFactsMisses++;
#endif
        return facts;
    }

    internal static string CanonicalWorkDisplayTitle(
        TvAirRecordingHistoryDto item,
        AIrhythmEvidenceIdentityContext context)
    {
        var service = ServiceIdentityOf(item);
        var analysisTitle = PreEvaluationTitle(item.ProgramTitle, context);
        var localWorkKey = LocalWorkKey(analysisTitle, service, context.NumericLocalValuesByService);
        var canonicalWorkKey = CanonicalizeLocalWorkKey(localWorkKey, service, context.WorkAliases);

        var displayTitle = SeriesDisplayTitle(item.ProgramTitle);
        if (IsProbableParenthesizedEpisodeSequence(item.ProgramTitle, service, context.NumericLocalValuesByService)
            && TryParseTrailingParenthesizedNumber(item.ProgramTitle, out var stem, out _))
        {
            var sequenceLabel = SeriesDisplayTitle(stem);
            if (sequenceLabel.Length > 0)
                displayTitle = sequenceLabel;
        }

        // WorkAliases are part of the common canonical Work Identity.  Dashboard labels must not
        // re-expand an alias-resolved Work back to one evidence row's longer variant.  When the
        // canonical key is an evidence-backed prefix of the human-readable title, project that
        // exact prefix as the display label.  This changes presentation only; Work grouping, Score,
        // Continuity and all other canonical facts remain owned by CanonicalWorkKey.
        if (canonicalWorkKey.Length >= 3
            && canonicalWorkKey.Length < localWorkKey.Length
            && TryProjectCanonicalWorkDisplayPrefix(displayTitle, canonicalWorkKey, out var canonicalDisplay))
            return canonicalDisplay;

        return displayTitle;
    }

    private static bool TryProjectCanonicalWorkDisplayPrefix(
        string displayTitle,
        string canonicalWorkKey,
        out string projected)
    {
        projected = string.Empty;
        if (string.IsNullOrWhiteSpace(displayTitle) || canonicalWorkKey.Length < 3)
            return false;

        var compactIndex = 0;
        for (var i = 0; i < displayTitle.Length; i++)
        {
            var ch = displayTitle[i];
            if (!char.IsLetterOrDigit(ch))
                continue;

            if (compactIndex >= canonicalWorkKey.Length
                || char.ToLowerInvariant(ch) != char.ToLowerInvariant(canonicalWorkKey[compactIndex]))
                return false;

            compactIndex++;
            if (compactIndex != canonicalWorkKey.Length)
                continue;

            var candidate = displayTitle[..(i + 1)]
                .Trim()
                .TrimEnd('～', '~', '・', '：', ':', '-', '－');
            if (candidate.Length == 0
                || !string.Equals(CompactIdentity(candidate), canonicalWorkKey, StringComparison.OrdinalIgnoreCase))
                return false;

            projected = candidate;
            return true;
        }

        return false;
    }

    internal static IReadOnlyList<AIrhythmCanonicalRecordingGenreAggregate> BuildCanonicalRecordingAggregates(
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmEvidenceIdentityContext context)
    {
        var rows = snapshot.History
            .Select(item =>
            {
                var facts = CanonicalEvidenceFacts(item, context);
                return new
                {
                    Facts = facts,
                    DisplayTitle = CanonicalWorkDisplayTitle(item, context)
                };
            })
            .Where(x => x.Facts.WorkKey.Length > 0 && x.Facts.GenreKey.Length > 0 && x.DisplayTitle.Length > 0)
            .ToArray();

        return rows
            .GroupBy(x => x.Facts.GenreKey, StringComparer.OrdinalIgnoreCase)
            .Select(genreGroup =>
            {
                var works = genreGroup
                    .GroupBy(x => x.Facts.WorkKey, StringComparer.OrdinalIgnoreCase)
                    .Select(workGroup =>
                    {
                        var label = workGroup
                            .GroupBy(x => x.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key.Length)
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key;
                        var serviceCounts = workGroup
                            .Where(x => x.Facts.ServiceIdentity.IsValid)
                            .GroupBy(x => x.Facts.ServiceIdentity)
                            .ToDictionary(x => x.Key, x => x.Count());
                        return new AIrhythmCanonicalRecordingWorkAggregate(
                            genreGroup.Key,
                            workGroup.Key,
                            label,
                            workGroup.Count(),
                            serviceCounts.Count,
                            serviceCounts);
                    })
                    .OrderByDescending(x => x.RecordingCount)
                    .ThenBy(x => x.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new AIrhythmCanonicalRecordingGenreAggregate(genreGroup.Key, genreGroup.Count(), works);
            })
            .OrderByDescending(x => x.RecordingCount)
            .ThenBy(x => x.GenreKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<AIrhythmCanonicalRecordingTotalAggregate> BuildCanonicalRecordingWorkTotals(
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmEvidenceIdentityContext context)
    {
        // Dashboard total ranking is deliberately factual: one successful history row is one recording.
        // Station, genre and episode presentation differences do not create extra ranking identities;
        // all rows are first projected through the common canonical Work/title evidence.
        var rows = snapshot.History
            .Select(item => new
            {
                WorkKey = CanonicalWorkKey(item, context),
                DisplayTitle = CanonicalWorkDisplayTitle(item, context)
            })
            .Where(x => x.WorkKey.Length > 0 && x.DisplayTitle.Length > 0)
            .ToArray();

        return rows
            .GroupBy(x => x.WorkKey, StringComparer.OrdinalIgnoreCase)
            .Select(workGroup =>
            {
                var label = workGroup
                    .GroupBy(x => x.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key.Length)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .First().Key;
                return new AIrhythmCanonicalRecordingTotalAggregate(workGroup.Key, label, workGroup.Count());
            })
            .OrderByDescending(x => x.RecordingCount)
            .ThenBy(x => x.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildEvidenceIdentityFingerprint(AIrhythmEvidenceIdentityContext context)
    {
        var numeric = string.Join(";", context.NumericLocalValuesByService
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key}:{string.Join(',', x.Value)}"));
        var aliases = string.Join(";", context.WorkAliases
            .OrderBy(x => x.Key.WorkKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key.ServiceIdentity.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key.WorkKey}|{x.Key.ServiceIdentity}->{x.Value}"));
        var externalTitles = string.Join(";", context.ExternalCanonicalTitles
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key}->{x.Value}"));
        return $"{numeric}#{aliases}#{externalTitles}";
    }

    private readonly record struct AIrhythmLocalEpisodeIdentity(
        string WorkKey,
        int EpisodeNumber,
        bool IsReplay,
        AIrhythmServiceIdentity ServiceIdentity);

    private readonly record struct AIrhythmLocalContinuitySource(
        AIrhythmLocalEpisodeIdentity Identity,
        string SourceKind,
        DateTimeOffset SourceTime);

    private readonly record struct AIrhythmLocalContinuityEvaluation(
        string WorkKey,
        int EpisodeNumber,
        AIrhythmLocalEvidenceStrength Strength,
        bool DifferentService,
        int EpisodeDistance,
        bool Replay,
        string SourceKind);

    private readonly record struct AIrhythmLocalEvidenceShadowRow(
        string CandidateKey,
        string Work,
        int Episode,
        AIrhythmLocalEvidenceStrength Continuity,
        int RecordingCount,
        int ReservationCount,
        int ActiveDays,
        int ActiveWeeks,
        int LastSeenDays,
        AIrhythmLocalEvidenceStrength Interest,
        AIrhythmLocalEvidenceFamily DominantFamily,
        string ProposedAdjustment,
        bool DifferentService,
        int EpisodeDistance,
        bool Replay,
        string ContinuitySourceKind);

    private static bool IsReplayTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var normalized = title.Normalize(NormalizationForm.FormKC);
        return Regex.IsMatch(normalized, @"(?:\[\s*再\s*\]|【\s*再\s*】)", RegexOptions.IgnoreCase)
            || normalized.Contains("再放送", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("アンコール", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("リピート", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExplicitEpisodeRange(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC));
        return Regex.IsMatch(
            normalized,
            @"(?:[#＃]\s*[0-9]+|(?:episode|ep\.?)\s*[0-9]+|第\s*[0-9]+\s*話|[0-9]+\s*話)\s*[-~〜～–—]\s*(?:(?:[#＃]|(?:episode|ep\.?)|第)\s*)?[0-9]+\s*(?:話)?",
            RegexOptions.IgnoreCase);
    }

    private static bool TryGetExplicitEpisodeNumber(string? title, out int episodeNumber)
    {
        episodeNumber = 0;
        if (string.IsNullOrWhiteSpace(title)) return false;
        // A broadcast covering an explicit episode range (for example #1-2 or 第1話～第2話)
        // is not one episode. Keep the range in Work/title presentation, but never collapse the
        // bundle to its first number for Local Episode Identity.
        if (HasExplicitEpisodeRange(title)) return false;
        if (title.IndexOf('#') < 0
            && title.IndexOf('＃') < 0
            && title.IndexOf('話') < 0
            && title.IndexOf('回') < 0
            && title.IndexOf('e') < 0
            && title.IndexOf('E') < 0
            && title.IndexOf('ｅ') < 0
            && title.IndexOf('Ｅ') < 0)
            return false;
        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC));
        // `第N回` is structurally ambiguous: it can mean an episode number, but it is also
        // widely used for event/edition ordinals (e.g. the Nth tournament or race). Keep it
        // available to Work normalization as a session/edition marker, but do not promote it
        // to Local Episode Identity without independent corroboration. Deterministic episode
        // identity is limited here to 第N話 / #N / EP N / N話.
        var match = Regex.Match(
            normalized,
            @"(?:第\s*([0-9]+)\s*話|[#＃]\s*([0-9]+)|(?:episode|ep\.?)\s*([0-9]+)|([0-9]+)\s*話)",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        foreach (Group group in match.Groups.Cast<Group>().Skip(1))
        {
            if (group.Success
                && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out episodeNumber)
                && episodeNumber > 0)
                return true;
        }
        episodeNumber = 0;
        return false;
    }


    private static bool TryGetAliasCorroboratedRepeatedEpisodeNumber(
        string? title,
        AIrhythmServiceIdentity service,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string>? workAliases,
        out string workKey,
        out int episodeNumber)
    {
        workKey = string.Empty;
        episodeNumber = 0;
        if (string.IsNullOrWhiteSpace(title) || !service.IsValid || workAliases is null)
            return false;

        // Some broadcasters encode an episode number with programme-specific wording rather than
        // a generic #N / 第N話 marker. Do not teach those words to the parser. Instead, only after
        // useful user evidence has already established a shorter Work alias on this same service,
        // accept a numeric suffix when the same non-year number is repeated at least twice in the
        // alias remainder. Example shape: <canonical work> 14 ... 14. This is deliberately
        // evidence-backed and cannot bootstrap its own alias because alias construction calls the
        // episode parser with workAliases=null.
        var fullKey = LocalWorkIdentityKey(title, preserveTrailingNumericDetail: true);
        if (fullKey.Length < 3)
            return false;

        var canonical = CanonicalizeLocalWorkKey(fullKey, service, workAliases);
        if (canonical.Length < 3
            || canonical.Length >= fullKey.Length
            || !fullKey.StartsWith(canonical, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = fullKey[canonical.Length..];
        var matches = Regex.Matches(remainder, @"[0-9]+");
        if (matches.Count < 2)
            return false;

        int? repeated = null;
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value <= 0
                || value > 999
                || IsLikelyCalendarYear(value))
                return false;

            if (repeated is null)
                repeated = value;
            else if (repeated.Value != value)
                return false;
        }

        if (repeated is null)
            return false;

        workKey = canonical;
        episodeNumber = repeated.Value;
        return true;
    }

    private static bool HasAtLeastTwoAsciiDigitRuns(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var runs = 0;
        var inRun = false;
        foreach (var ch in value)
        {
            var isDigit = ch is >= '0' and <= '9' or >= '０' and <= '９';
            if (isDigit)
            {
                if (!inRun && ++runs >= 2)
                    return true;
                inRun = true;
            }
            else
            {
                inRun = false;
            }
        }

        return false;
    }

    private static bool HasWorkBeforeExplicitEpisodeMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = StripNonIdentityBroadcastAnnotations(value.Normalize(NormalizationForm.FormKC));
        var marker = Regex.Match(
            normalized,
            @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)",
            RegexOptions.IgnoreCase);
        if (!marker.Success) return false;

        var prefix = normalized[..marker.Index].Trim();
        if (prefix.Length == 0) return false;

        // A remainder such as "第2期 第7話" or "Season 2 EP7" contains an episode marker,
        // but the text before it is only hierarchy metadata. In that shape the leading 【...】
        // block can still be the work title itself, so do not classify it as a broadcast container.
        var withoutHierarchyOnly = Regex.Replace(
            prefix,
            @"(?:第\s*[0-9０-９]+\s*(?:期|シリーズ)|(?:season|series|part)\s*[0-9０-９]+|[0-9０-９]+(?:st|nd|rd|th)\s*season)",
            " ",
            RegexOptions.IgnoreCase);
        return withoutHierarchyOnly.Count(char.IsLetterOrDigit) >= 2;
    }

    private static string LocalWorkIdentityKey(
        string? title,
        bool preserveTrailingNumericDetail = false,
        bool allowLeadingContainerSeparation = false)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        // ParseLeadingContainerParts normalizes and runs several regexes. A leading-container
        // result is impossible when the raw title contains no Japanese corner bracket at all,
        // so ordinary titles go straight to the canonical title parser.
        if (title.IndexOf('【') < 0)
            return EvidenceSeriesKey(title);

        var containerParts = ParseLeadingContainerParts(title);
        if (string.IsNullOrWhiteSpace(containerParts.Container))
            return EvidenceSeriesKey(title);

        // A leading 【...】 block is ambiguous: it can be a broadcast container (e.g. a drama
        // slot) or the work title itself. Separate it only when the remainder independently
        // contains a work before an explicit episode marker, or when the caller has already
        // corroborated a parenthesized episode sequence on this exact service.
        var canSeparate = allowLeadingContainerSeparation
            || HasWorkBeforeExplicitEpisodeMarker(containerParts.Remainder);
        if (!canSeparate)
            return EvidenceSeriesKey(title);

        var work = preserveTrailingNumericDetail
            ? containerParts.Remainder
            : !string.IsNullOrWhiteSpace(containerParts.WorkCandidate)
                ? containerParts.WorkCandidate
                : containerParts.Remainder;
        return EvidenceSeriesKey(work);
    }

    private static string LocalWorkKey(
        string? title,
        AIrhythmServiceIdentity service,
        IReadOnlyDictionary<string, int[]> numericLocalValuesByService)
    {
        if (TryParseTrailingParenthesizedNumber(title, out var numericStem, out var numericValue)
            && numericValue > 0)
        {
            if (!IsLikelyCalendarYear(numericValue)
                && IsProbableParenthesizedEpisodeSequence(title, service, numericLocalValuesByService))
            {
                var numericWorkKey = LocalWorkIdentityKey(numericStem, allowLeadingContainerSeparation: true);
                if (numericWorkKey.Length >= 3)
                    return numericWorkKey;
            }
            // A year, installment number, or otherwise uncorroborated suffix remains part of
            // local identity. The episode collapse must remain corroborated on this exact service.
            return LocalWorkIdentityKey(title, preserveTrailingNumericDetail: true);
        }
        return LocalWorkIdentityKey(title);
    }

    private static bool TryBuildLocalEpisodeIdentity(
        string? title,
        AIrhythmServiceIdentity service,
        bool parenthesizedSequenceCorroborated,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string>? workAliases,
        out AIrhythmLocalEpisodeIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var isReplay = IsReplayTitle(title);
        if (TryGetExplicitEpisodeNumber(title, out var explicitEpisode))
        {
            var workKey = CanonicalizeLocalWorkKey(LocalWorkIdentityKey(title), service, workAliases);
            if (workKey.Length < 3)
                return false;

            identity = new AIrhythmLocalEpisodeIdentity(
                workKey, explicitEpisode, isReplay, service);
            return true;
        }

        if (TryGetAliasCorroboratedRepeatedEpisodeNumber(
                title, service, workAliases, out var corroboratedWorkKey, out var corroboratedEpisode))
        {
            identity = new AIrhythmLocalEpisodeIdentity(
                corroboratedWorkKey, corroboratedEpisode, isReplay, service);
            return true;
        }

        if (!parenthesizedSequenceCorroborated
            || !TryParseTrailingParenthesizedNumber(title, out var numericStem, out var numericEpisode)
            || numericEpisode <= 0
            || IsLikelyCalendarYear(numericEpisode))
            return false;

        var numericWorkKey = CanonicalizeLocalWorkKey(
            LocalWorkIdentityKey(
                numericStem,
                allowLeadingContainerSeparation: true),
            service,
            workAliases);
        if (numericWorkKey.Length < 3)
            return false;

        identity = new AIrhythmLocalEpisodeIdentity(
            numericWorkKey, numericEpisode, isReplay, service);
        return true;
    }


    private static string CanonicalizeLocalWorkKey(
        string workKey,
        AIrhythmServiceIdentity service,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string>? workAliases)
    {
        if (workKey.Length == 0 || !service.IsValid || workAliases is null)
            return workKey;

        var current = workKey;
        while (workAliases.TryGetValue(new AIrhythmLocalWorkAliasKey(current, service), out var canonical)
            && canonical.Length >= 3
            && canonical.Length < current.Length)
        {
            current = canonical;
        }
        return current;
    }

    private static bool TryGetRecurringEvidencePrefix(string? title, out string prefixKey, out bool strongBoundary)
    {
        prefixKey = string.Empty;
        strongBoundary = false;
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC)).Trim();
        if (normalized.Length == 0)
            return false;

        // TvAIr-managed weekday suffix is not programme identity.
        normalized = Regex.Replace(normalized, @"\s*[（(]\s*(?:月|火|水|木|金|土|日)\s*[）)]\s*$", " ").Trim();

        // Explicit episode markers are already handled by Local Episode Identity. Do not create
        // a second title-shortening path for those rows.
        if (Regex.IsMatch(
                normalized,
                @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)",
                RegexOptions.IgnoreCase))
            return false;

        var boundary = -1;
        foreach (var marker in new[] { '！', '!', '？', '?', '▼', '▽' })
        {
            var index = normalized.IndexOf(marker);
            if (index > 0 && (boundary < 0 || index < boundary))
            {
                boundary = index;
                strongBoundary = true;
            }
        }

        var whitespace = Regex.Match(normalized, @"[\s　]+", RegexOptions.None);
        if (whitespace.Success && whitespace.Index > 0 && (boundary < 0 || whitespace.Index < boundary))
        {
            boundary = whitespace.Index;
            strongBoundary = false;
        }

        if (boundary <= 0)
            return false;

        var prefix = normalized[..boundary].Trim().TrimEnd('～', '~', '・', '：', ':', '-', '－');
        var remainder = normalized[boundary..].Trim().TrimStart('！', '!', '？', '?', '▼', '▽', '～', '~', '・', '：', ':', '-', '－');
        if (prefix.Count(char.IsLetterOrDigit) < 5 || remainder.Count(char.IsLetterOrDigit) < 3)
            return false;

        prefixKey = EvidenceSeriesKey(prefix);
        var fullKey = EvidenceSeriesKey(normalized);
        if (prefixKey.Length < 5 || fullKey.Length - prefixKey.Length < 3)
        {
            prefixKey = string.Empty;
            strongBoundary = false;
            return false;
        }
        return true;
    }

    private static string LongestStableEvidencePrefix(IReadOnlyCollection<string> fullKeys, string initialPrefix)
    {
        if (fullKeys.Count < 2 || string.IsNullOrWhiteSpace(initialPrefix))
            return string.Empty;

        var ordered = fullKeys.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (ordered.Length < 2)
            return string.Empty;

        var common = ordered[0];
        for (var i = 1; i < ordered.Length && common.Length > 0; i++)
        {
            var other = ordered[i];
            var length = Math.Min(common.Length, other.Length);
            var j = 0;
            while (j < length && char.ToUpperInvariant(common[j]) == char.ToUpperInvariant(other[j]))
                j++;
            common = common[..j];
        }

        common = Regex.Replace(common, @"[0-9０-９]+$", string.Empty).Trim();

        // A longest-common-prefix cut can stop immediately after the Japanese ordinal introducer
        // when every evidence-backed variant continues with a different number, e.g.
        // "...第1部..." / "...第2部...".  A bare terminal "第" is not work identity; remove it
        // only when all observed full keys prove that the next character is numeric.  This keeps
        // the rule evidence-backed and generic instead of introducing a title-specific exception.
        if (common.EndsWith("第", StringComparison.Ordinal)
            && ordered.All(fullKey => fullKey.Length > common.Length && char.IsDigit(fullKey[common.Length])))
        {
            common = common[..^1].TrimEnd();
        }

        if (common.Length < initialPrefix.Length)
            return string.Empty;

        // Never collapse to a bare scheduling/container prefix when the evidence-backed variants
        // share a longer stable programme identity.  This keeps e.g. a broadcast slot plus an
        // actual work title intact, without any title-specific deny list.
        return common.Length >= initialPrefix.Length + 4 ? common : initialPrefix;
    }

    private static IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string> BuildLocalWorkAliases(
        AIrhythmRuntimeSnapshot snapshot,
        IReadOnlyDictionary<string, int[]> numericLocalValuesByService)
    {
        var episodesByWorkAndService = new Dictionary<AIrhythmLocalWorkAliasKey, HashSet<int>>();

        void Observe(string? title, AIrhythmServiceIdentity service)
        {
            var parenthesizedSequenceCorroborated =
                IsProbableParenthesizedEpisodeSequence(title, service, numericLocalValuesByService);
            if (!TryBuildLocalEpisodeIdentity(
                    title,
                    service,
                    parenthesizedSequenceCorroborated,
                    workAliases: null,
                    out var identity))
                return;

            if (!identity.ServiceIdentity.IsValid)
                return;

            var key = new AIrhythmLocalWorkAliasKey(identity.WorkKey, identity.ServiceIdentity);
            if (!episodesByWorkAndService.TryGetValue(key, out var episodes))
            {
                episodes = new HashSet<int>();
                episodesByWorkAndService[key] = episodes;
            }
            episodes.Add(identity.EpisodeNumber);
        }

        // Alias candidates come only from useful user evidence. Parenthesized episode corroboration
        // still uses numericLocalValuesByService, whose source is the canonical EPG sequence map,
        // so there is no need to rescan the full EPG population here.
        foreach (var item in snapshot.History)
        {
            if (AIrhythmDataState.IsUsefulHistory(item))
                Observe(item.ProgramTitle, ServiceIdentityOf(item));
        }
        foreach (var item in snapshot.Reservations)
        {
            if (AIrhythmDataState.IsUsefulReservation(item) && ReservationEvidenceWeight(item) > 0)
                Observe(item.ProgramTitle, ServiceIdentityOf(item));
        }

        var keys = episodesByWorkAndService.Keys
            .Where(key => key.WorkKey.Length >= 3 && key.ServiceIdentity.IsValid)
            .OrderByDescending(key => key.WorkKey.Length)
            .ToArray();
        var aliases = new Dictionary<AIrhythmLocalWorkAliasKey, string>();

        foreach (var longer in keys)
        {
            string? best = null;
            foreach (var shorter in keys)
            {
                if (shorter.WorkKey.Length >= longer.WorkKey.Length
                    || longer.WorkKey.Length - shorter.WorkKey.Length < 2)
                    continue;
                if (!longer.WorkKey.EndsWith(shorter.WorkKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Do not strip a leading phrase merely because the remaining title is a suffix.
                // Require both forms to be backed by useful user evidence and to carry neighboring
                // episode numbers. Work identity itself may cross services; service difference is
                // handled later by Continuity confidence instead of splitting the work identity.
                var hasNeighboringEpisode = episodesByWorkAndService[longer].Any(longEpisode =>
                    episodesByWorkAndService[shorter].Any(shortEpisode => Math.Abs(longEpisode - shortEpisode) <= 2));
                if (!hasNeighboringEpisode)
                    continue;

                if (best is null || shorter.WorkKey.Length > best.Length)
                    best = shorter.WorkKey;
            }

            if (!string.IsNullOrWhiteSpace(best))
                aliases[longer] = best;
        }

        // For non-episodic recurring programmes, learn only from useful user evidence.
        // The same service must contain at least two distinct evidence-backed title variants
        // that share a stable prefix. EPG repetition alone can never create this alias.
        var recurringPrefixGroups = new Dictionary<(AIrhythmServiceIdentity Service, string Prefix, bool StrongBoundary), HashSet<string>>();
        var exactEvidenceKeysByService = new Dictionary<AIrhythmServiceIdentity, HashSet<string>>();

        void ObserveRecurringPrefix(string? title, AIrhythmServiceIdentity service)
        {
            if (!service.IsValid || string.IsNullOrWhiteSpace(title))
                return;

            var fullKey = EvidenceSeriesKey(title);
            if (fullKey.Length >= 5)
            {
                if (!exactEvidenceKeysByService.TryGetValue(service, out var exactKeys))
                {
                    exactKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    exactEvidenceKeysByService[service] = exactKeys;
                }
                exactKeys.Add(fullKey);
            }

            if (!TryGetRecurringEvidencePrefix(title, out var prefixKey, out var strongBoundary)
                || fullKey.Length <= prefixKey.Length)
                return;

            var groupKey = (service, prefixKey, strongBoundary);
            if (!recurringPrefixGroups.TryGetValue(groupKey, out var fullKeys))
            {
                fullKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                recurringPrefixGroups[groupKey] = fullKeys;
            }
            fullKeys.Add(fullKey);
        }

        foreach (var item in snapshot.History)
        {
            if (AIrhythmDataState.IsUsefulHistory(item))
                ObserveRecurringPrefix(item.ProgramTitle, ServiceIdentityOf(item));
        }
        foreach (var item in snapshot.Reservations)
        {
            if (AIrhythmDataState.IsUsefulReservation(item) && ReservationEvidenceWeight(item) > 0)
                ObserveRecurringPrefix(item.ProgramTitle, ServiceIdentityOf(item));
        }

        foreach (var pair in recurringPrefixGroups)
        {
            var exactShortTitleCorroborated =
                exactEvidenceKeysByService.TryGetValue(pair.Key.Service, out var exactKeys)
                && exactKeys.Contains(pair.Key.Prefix);
            if (pair.Value.Count < 2 && !exactShortTitleCorroborated)
                continue;

            // An exact useful-evidence title on the same service is the strongest local proof that
            // the same text is a complete programme identity rather than only a scheduling prefix.
            // This lets one longer evidence-backed topic/subtitle variant collapse to that exact Work
            // without requiring a second long variant. Otherwise retain the established multi-variant
            // evidence rule and its conservative whitespace-prefix handling.
            var stablePrefix = exactShortTitleCorroborated
                ? pair.Key.Prefix
                : pair.Key.StrongBoundary
                    ? pair.Key.Prefix
                    : LongestStableEvidencePrefix(pair.Value, pair.Key.Prefix);
            if (stablePrefix.Length < pair.Key.Prefix.Length)
                continue;

            foreach (var fullKey in pair.Value)
            {
                if (fullKey.Length <= stablePrefix.Length)
                    continue;
                var aliasKey = new AIrhythmLocalWorkAliasKey(fullKey, pair.Key.Service);
                if (!aliases.ContainsKey(aliasKey))
                    aliases[aliasKey] = stablePrefix;
            }
        }

        return aliases;
    }

    private readonly record struct AIrhythmLocalEvidenceState(
        IReadOnlyDictionary<string, AIrhythmLocalWorkAggregate> WorkAggregates,
        IReadOnlyDictionary<string, AIrhythmLocalContinuitySource[]> ContinuitySources);

    private static AIrhythmLocalEvidenceState BuildLocalEvidenceState(
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmEvidenceIdentityContext identityContext)
    {
        var aggregates = new Dictionary<string, AIrhythmLocalWorkAggregate>(StringComparer.OrdinalIgnoreCase);
        var continuitySources = new Dictionary<string, List<AIrhythmLocalContinuitySource>>(StringComparer.OrdinalIgnoreCase);

        AIrhythmLocalWorkAggregate GetOrCreateAggregate(string key)
        {
            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                aggregate = new AIrhythmLocalWorkAggregate();
                aggregates[key] = aggregate;
            }
            return aggregate;
        }

        void AddContinuitySource(AIrhythmLocalEpisodeIdentity identity, string sourceKind, DateTimeOffset sourceTime)
        {
            if (!continuitySources.TryGetValue(identity.WorkKey, out var list))
            {
                list = new List<AIrhythmLocalContinuitySource>();
                continuitySources[identity.WorkKey] = list;
            }
            list.Add(new AIrhythmLocalContinuitySource(identity, sourceKind, sourceTime));
        }

        void AddEvidence(
            string? title,
            AIrhythmServiceIdentity service,
            DateTimeOffset when,
            bool isRecording,
            bool isAutomatedReservation,
            string sourceKind)
        {
            var parenthesizedSequenceCorroborated =
                IsProbableParenthesizedEpisodeSequence(title, service, identityContext.NumericLocalValuesByService);
            var hasEpisodeSource = TryBuildLocalEpisodeIdentity(
                title,
                service,
                parenthesizedSequenceCorroborated,
                identityContext.WorkAliases,
                out var sourceIdentity);
            var workKey = hasEpisodeSource
                ? sourceIdentity.WorkKey
                : CanonicalizeLocalWorkKey(
                    LocalWorkKey(title, service, identityContext.NumericLocalValuesByService),
                    service,
                    identityContext.WorkAliases);

            if (workKey.Length >= 3)
            {
                var aggregate = GetOrCreateAggregate(workKey);
                if (isRecording)
                    aggregate.RecordingCount++;
                else
                {
                    aggregate.ReservationCount++;
                    if (!isAutomatedReservation)
                        aggregate.HasNonAutomatedReservation = true;
                }
                aggregate.AddActivity(when);
            }

            if (hasEpisodeSource)
                AddContinuitySource(sourceIdentity, sourceKind, when);
        }

        foreach (var item in snapshot.History)
        {
            if (!AIrhythmDataState.IsUsefulHistory(item))
                continue;
            AddEvidence(
                item.ProgramTitle,
                ServiceIdentityOf(item),
                item.ActualStart ?? item.Start,
                isRecording: true,
                isAutomatedReservation: false,
                sourceKind: "recording");
        }

        foreach (var item in snapshot.Reservations)
        {
            if (!AIrhythmDataState.IsUsefulReservation(item) || ReservationEvidenceWeight(item) <= 0)
                continue;
            var isAutomated = item.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule;
            AddEvidence(
                item.ProgramTitle,
                ServiceIdentityOf(item),
                item.Start,
                isRecording: false,
                isAutomatedReservation: isAutomated,
                sourceKind: isAutomated ? "automated_reservation" : "reservation");
        }

        return new AIrhythmLocalEvidenceState(
            aggregates,
            continuitySources.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    private static AIrhythmLocalEvidenceStrength LowerLocalEvidenceStrength(AIrhythmLocalEvidenceStrength strength)
        => strength switch
        {
            AIrhythmLocalEvidenceStrength.Strong => AIrhythmLocalEvidenceStrength.Moderate,
            AIrhythmLocalEvidenceStrength.Moderate => AIrhythmLocalEvidenceStrength.Weak,
            AIrhythmLocalEvidenceStrength.Weak => AIrhythmLocalEvidenceStrength.None,
            _ => AIrhythmLocalEvidenceStrength.None
        };

    private static AIrhythmLocalEvidenceStrength ContinuityStrengthForDistance(int distance)
        => distance switch
        {
            1 => AIrhythmLocalEvidenceStrength.Strong,
            2 => AIrhythmLocalEvidenceStrength.Moderate,
            _ => AIrhythmLocalEvidenceStrength.None
        };

    private static AIrhythmLocalContinuityEvaluation EvaluateLocalContinuity(
        TvAirProgramEventDto candidate,
        bool probableParenthesizedEpisodeSequence,
        IReadOnlyDictionary<string, int[]> numericLocalValuesByService,
        IReadOnlyDictionary<string, AIrhythmLocalContinuitySource[]> continuitySources,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string> localWorkAliases)
    {
        var replay = IsReplayTitle(candidate.Title);
        var service = ServiceIdentityOf(candidate);
        var hasIdentity = TryBuildLocalEpisodeIdentity(
            candidate.Title,
            service,
            probableParenthesizedEpisodeSequence,
            localWorkAliases,
            out var candidateIdentity);
        var workKey = hasIdentity
            ? candidateIdentity.WorkKey
            : CanonicalizeLocalWorkKey(
                LocalWorkKey(candidate.Title, service, numericLocalValuesByService),
                service,
                localWorkAliases);

        if (replay || !hasIdentity)
            return new(workKey, hasIdentity ? candidateIdentity.EpisodeNumber : 0, AIrhythmLocalEvidenceStrength.None, false, 0, replay, string.Empty);

        var bestStrength = AIrhythmLocalEvidenceStrength.None;
        var bestDifferentService = false;
        var bestDistance = 0;
        var bestSourceKind = string.Empty;

        if (continuitySources.TryGetValue(candidateIdentity.WorkKey, out var sources))
        {
            foreach (var source in sources)
            {
                if (source.Identity.IsReplay || source.SourceTime >= candidate.Start)
                    continue;

                var distance = candidateIdentity.EpisodeNumber - source.Identity.EpisodeNumber;
                var sourceStrength = ContinuityStrengthForDistance(distance);
                if (sourceStrength == AIrhythmLocalEvidenceStrength.None)
                    continue;

                var sameService = candidateIdentity.ServiceIdentity.IsValid
                    && source.Identity.ServiceIdentity.IsValid
                    && candidateIdentity.ServiceIdentity.Equals(source.Identity.ServiceIdentity);
                if (!sameService)
                    sourceStrength = LowerLocalEvidenceStrength(sourceStrength);
                if ((int)sourceStrength <= (int)bestStrength)
                    continue;

                bestStrength = sourceStrength;
                bestDifferentService = !sameService;
                bestDistance = distance;
                bestSourceKind = source.SourceKind;
            }
        }

        return new(workKey, candidateIdentity.EpisodeNumber, bestStrength, bestDifferentService, bestDistance, replay, bestSourceKind);
    }

    private static AIrhythmLocalContinuityEvaluation EvaluateLocalContinuityFromCanonicalFacts(
        TvAirProgramEventDto candidate,
        bool probableParenthesizedEpisodeSequence,
        IReadOnlyDictionary<string, int[]> numericLocalValuesByService,
        IReadOnlyDictionary<string, AIrhythmLocalContinuitySource[]> continuitySources,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string> localWorkAliases,
        AIrhythmCanonicalEvidenceFacts facts)
    {
        // External canonical-title substitution can intentionally differ from the local continuity
        // identity, so those candidates use the full continuity evaluation path.
        if (facts.UsesExternalCanonicalTitle)
        {
            return EvaluateLocalContinuity(
                candidate,
                probableParenthesizedEpisodeSequence,
                numericLocalValuesByService,
                continuitySources,
                localWorkAliases);
        }

        // For ordinary candidates Canonical Facts already computed the same local Work key and
        // replay flag. Avoid rebuilding both for every scored candidate. Only rows that can carry
        // an episode identity need the full episode parser.
        var hasExplicitEpisode = TryGetExplicitEpisodeNumber(candidate.Title, out _);
        var mayHaveAliasCorroboratedRepeatedEpisode = !hasExplicitEpisode
            && !probableParenthesizedEpisodeSequence
            && HasAtLeastTwoAsciiDigitRuns(candidate.Title);
        if (!hasExplicitEpisode
            && !probableParenthesizedEpisodeSequence
            && !mayHaveAliasCorroboratedRepeatedEpisode)
            return new(facts.WorkKey, 0, AIrhythmLocalEvidenceStrength.None, false, 0, facts.IsReplay, string.Empty);

        if (!TryBuildLocalEpisodeIdentity(
                candidate.Title,
                facts.ServiceIdentity,
                probableParenthesizedEpisodeSequence,
                localWorkAliases,
                out var candidateIdentity))
        {
            return new(facts.WorkKey, 0, AIrhythmLocalEvidenceStrength.None, false, 0, facts.IsReplay, string.Empty);
        }

        if (facts.IsReplay)
            return new(candidateIdentity.WorkKey, candidateIdentity.EpisodeNumber, AIrhythmLocalEvidenceStrength.None, false, 0, true, string.Empty);

        var bestStrength = AIrhythmLocalEvidenceStrength.None;
        var bestDifferentService = false;
        var bestDistance = 0;
        var bestSourceKind = string.Empty;

        if (continuitySources.TryGetValue(candidateIdentity.WorkKey, out var sources))
        {
            foreach (var source in sources)
            {
                if (source.Identity.IsReplay || source.SourceTime >= candidate.Start)
                    continue;

                var distance = candidateIdentity.EpisodeNumber - source.Identity.EpisodeNumber;
                var sourceStrength = ContinuityStrengthForDistance(distance);
                if (sourceStrength == AIrhythmLocalEvidenceStrength.None)
                    continue;

                var sameService = candidateIdentity.ServiceIdentity.IsValid
                    && source.Identity.ServiceIdentity.IsValid
                    && candidateIdentity.ServiceIdentity.Equals(source.Identity.ServiceIdentity);
                if (!sameService)
                    sourceStrength = LowerLocalEvidenceStrength(sourceStrength);
                if ((int)sourceStrength <= (int)bestStrength)
                    continue;

                bestStrength = sourceStrength;
                bestDifferentService = !sameService;
                bestDistance = distance;
                bestSourceKind = source.SourceKind;
            }
        }

        return new(candidateIdentity.WorkKey, candidateIdentity.EpisodeNumber, bestStrength, bestDifferentService, bestDistance, false, bestSourceKind);
    }

    private static double LocalContinuityAdjustmentValue(AIrhythmLocalEvidenceStrength strength)
        => strength switch
        {
            AIrhythmLocalEvidenceStrength.Strong => 0.50d,
            AIrhythmLocalEvidenceStrength.Moderate => 0.25d,
            _ => 0.0d
        };

    private static double LocalInterestAdjustmentValue(AIrhythmLocalEvidenceStrength strength)
        => strength switch
        {
            AIrhythmLocalEvidenceStrength.Strong => 0.25d,
            AIrhythmLocalEvidenceStrength.Moderate => 0.125d,
            // Weak means only one useful recording/reservation occurrence. Keep it as evidence,
            // but a single occurrence does not move the product ranking.
            _ => 0.0d
        };

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
    private static double LocalInterestProjectionValue(
        AIrhythmLocalEvidenceStrength strength,
        double strongRaw,
        double moderateRaw)
        => strength switch
        {
            AIrhythmLocalEvidenceStrength.Strong => strongRaw,
            AIrhythmLocalEvidenceStrength.Moderate => moderateRaw,
            _ => 0.0d
        };
#endif

    private static AIrhythmLocalEvidenceStrength EvaluateInterestStrength(AIrhythmLocalWorkAggregate? aggregate)
    {
        if (aggregate is null || aggregate.RecordingCount + aggregate.ReservationCount <= 0)
            return AIrhythmLocalEvidenceStrength.None;

        var activeDays = aggregate.ActiveDays.Count;
        var activeWeeks = aggregate.ActiveWeeks.Count;
        AIrhythmLocalEvidenceStrength strength;
        if (activeWeeks >= 3
            || (activeWeeks >= 2 && aggregate.RecordingCount > 0 && aggregate.ReservationCount > 0))
            strength = AIrhythmLocalEvidenceStrength.Strong;
        else if (activeDays >= 2
            || activeWeeks >= 2)
            strength = AIrhythmLocalEvidenceStrength.Moderate;
        else
            strength = AIrhythmLocalEvidenceStrength.Weak;

        // Automated reservations alone are intent declarations, not proof of sustained consumption.
        // They may establish local interest, but never Strong without an actual recording or a
        // non-automated reservation path.
        if (strength == AIrhythmLocalEvidenceStrength.Strong
            && aggregate.RecordingCount == 0
            && !aggregate.HasNonAutomatedReservation)
            strength = AIrhythmLocalEvidenceStrength.Moderate;

        return strength;
    }

    private static AIrhythmLocalEvidenceFamily DominantLocalEvidenceFamily(
        AIrhythmLocalEvidenceStrength continuity,
        AIrhythmLocalEvidenceStrength interest)
    {
        if (continuity != AIrhythmLocalEvidenceStrength.None
            && (int)continuity >= (int)interest)
            return AIrhythmLocalEvidenceFamily.ContinuityFamily;
        if (interest != AIrhythmLocalEvidenceStrength.None)
            return AIrhythmLocalEvidenceFamily.ExplicitInterestFamily;
        return AIrhythmLocalEvidenceFamily.None;
    }

    private static double LocalEvidenceAdjustmentValue(
        AIrhythmLocalEvidenceStrength continuity,
        AIrhythmLocalEvidenceStrength interest,
        out AIrhythmLocalEvidenceFamily dominantFamily)
    {
        dominantFamily = DominantLocalEvidenceFamily(continuity, interest);
        return dominantFamily switch
        {
            AIrhythmLocalEvidenceFamily.ContinuityFamily => LocalContinuityAdjustmentValue(continuity),
            AIrhythmLocalEvidenceFamily.ExplicitInterestFamily => LocalInterestAdjustmentValue(interest),
            _ => 0.0d
        };
    }

    private static AIrhythmLocalEvidenceShadowRow EvaluateLocalEvidenceShadow(
        TvAirProgramEventDto candidate,
        AIrhythmLocalContinuityEvaluation continuityEvaluation,
        IReadOnlyDictionary<string, AIrhythmLocalWorkAggregate> aggregates,
        DateTimeOffset now)
    {
        var workKey = continuityEvaluation.WorkKey;
        aggregates.TryGetValue(workKey, out var aggregate);
        var interest = EvaluateInterestStrength(aggregate);
        var continuity = continuityEvaluation.Strength;

        var localAdjustmentValue = LocalEvidenceAdjustmentValue(continuity, interest, out var dominantFamily);
        var proposedAdjustment = dominantFamily switch
        {
            AIrhythmLocalEvidenceFamily.ContinuityFamily => $"continuity:{localAdjustmentValue:0.00}",
            AIrhythmLocalEvidenceFamily.ExplicitInterestFamily => $"interest:{localAdjustmentValue:0.000}",
            _ => "none"
        };

        var lastSeenDays = aggregate?.LastSeen is DateTimeOffset lastSeen
            ? Math.Max(0, (int)Math.Floor((now - lastSeen).TotalDays))
            : -1;

        return new(
            $"{candidate.NetworkId}:{candidate.TransportStreamId}:{candidate.ServiceId}:{candidate.EventNumber}:{candidate.Start.UtcDateTime.Ticks}",
            workKey,
            continuityEvaluation.EpisodeNumber,
            continuity,
            aggregate?.RecordingCount ?? 0,
            aggregate?.ReservationCount ?? 0,
            aggregate?.ActiveDays.Count ?? 0,
            aggregate?.ActiveWeeks.Count ?? 0,
            lastSeenDays,
            interest,
            dominantFamily,
            proposedAdjustment,
            continuityEvaluation.DifferentService,
            continuityEvaluation.EpisodeDistance,
            continuityEvaluation.Replay,
            continuityEvaluation.SourceKind);
    }

    internal static void InvalidateScoreResultCache()
    {
        lock (ScoreResultCacheGate)
        {
            CachedScoreSnapshot = null;
            CachedScoreExternalEvidenceGeneration = -1;
            CachedScoreRecommendations = null;
        }
    }

    private static AIrhythmRecommendation[] GetOrBuildScoreResult(
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmEvidenceIdentityContext identityContext)
    {
        lock (ScoreResultCacheGate)
        {
            var externalEvidenceGeneration = AIrhythmDataState.GetExternalEvidenceGeneration();
            if (ReferenceEquals(CachedScoreSnapshot, snapshot)
                && CachedScoreExternalEvidenceGeneration == externalEvidenceGeneration
                && CachedScoreRecommendations is not null)
            {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                AIrhythmDataState.WriteDeveloperLog(
                    $"SCORE_RESULT_CACHE result=HIT scored={CachedScoreRecommendations.Length} externalEvidenceGeneration={externalEvidenceGeneration} scope=runtime_snapshot_exact semantics=unchanged");
#endif
                return CachedScoreRecommendations;
            }

            var scored = Score(snapshot, identityContext)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Start)
                .ToArray();
            var completedGeneration = AIrhythmDataState.GetExternalEvidenceGeneration();

            // External evidence can change while Score is running (manual provider refresh).
            // Cache only when the evidence generation stayed stable for the whole calculation.
            if (completedGeneration == externalEvidenceGeneration)
            {
                CachedScoreSnapshot = snapshot;
                CachedScoreExternalEvidenceGeneration = completedGeneration;
                CachedScoreRecommendations = scored;
            }
            else
            {
                CachedScoreSnapshot = null;
                CachedScoreExternalEvidenceGeneration = -1;
                CachedScoreRecommendations = null;
            }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            AIrhythmDataState.WriteDeveloperLog(
                $"SCORE_RESULT_CACHE result=MISS scored={scored.Length} externalEvidenceGeneration={externalEvidenceGeneration}->{completedGeneration} cached={(completedGeneration == externalEvidenceGeneration)} scope=runtime_snapshot_exact semantics=unchanged");
#endif
            return scored;
        }
    }

    private static IReadOnlyList<AIrhythmRecommendation> Score(AIrhythmRuntimeSnapshot snapshot, AIrhythmEvidenceIdentityContext identityContext)
    {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreAllocatedStart = GC.GetAllocatedBytesForCurrentThread();
#endif
        var serviceWeights = new Dictionary<AIrhythmServiceIdentity, double>();
        var termWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var hourWeights = new Dictionary<int, double>();
        var seriesHistoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seriesReservationWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var seriesAutomatedReservationWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        // Parenthesized episode candidates keep their canonical evidence key unchanged.
        // A separate local-series bridge is populated from history/reservations and is consulted
        // only when the current EPG supplies corroborating neighboring numbers. This avoids
        // globally collapsing titles such as work years or numbered installments.
        var numericServiceHistoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var numericServiceReservationWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var numericServiceAutomatedReservationWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var numericLocalValuesByService = identityContext.NumericLocalValuesByService;
        var localWorkAliases = identityContext.WorkAliases;
        var localEvidenceState = BuildLocalEvidenceState(snapshot, identityContext);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var localEvidenceShadowRows = new List<AIrhythmLocalEvidenceShadowRow>();
#endif
        var probableEpisodeSequenceCandidates = 0;
        var probableEpisodeHistoryBridges = 0;
        var probableEpisodeReservationBridges = 0;
        var numericHistoryBridgeSamples = new List<string>();
        var numericReservationBridgeSamples = new List<string>();
        var historyEvidence = GetHistoryEvidence(snapshot.History, identityContext);
        var genreCounts = historyEvidence
            .Select(x => x.GenreKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var genreTotal = Math.Max(1, genreCounts.Values.Sum());
        var interestSignals = AIrhythmDataState.GetInterestSignals();
        var now = DateTimeOffset.Now;
        // Interest signals are capped at 24. Normalize the stable signal side once per Score run and
        // reuse the same SeriesKey/GenreMatches semantics for all candidates.
        var preparedInterestSignals = interestSignals
            .Select(signal =>
            {
                var ageDays = Math.Max(0, (now - signal.SelectedAt).TotalDays);
                var decay = Math.Exp(-ageDays / 180.0);
                return (
                    signal.SeriesKey,
                    GenreKey: NormalizeGenre(signal.Genre),
                    ExactWeight: 18 * decay,
                    GenreWeight: 5 * decay);
            })
            .ToArray();
        var scored = new List<AIrhythmRecommendation>();
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var baseScoreAuditRows = new List<(
            AIrhythmEventIdentity Identity,
            string Title,
            int HistorySeriesCount,
            double ReservationSeriesWeight,
            int GenreCount,
            double GenreShare,
            double GenreComponent,
            double SeriesComponent,
            double TermComponent,
            double ServiceComponent,
            double HourComponent,
            double InterestComponent,
            double PreferredComponent,
            double ScoreBeforeCap,
            int ConfidenceCap,
            int RawScore,
            bool Convincing)>();
#endif

        void Add<TKey>(Dictionary<TKey, double> map, TKey key, double value) where TKey : notnull
            => map[key] = map.TryGetValue(key, out var current) ? current + value : value;
        void Increment(Dictionary<string, int> map, string key)
        {
            if (key.Length == 0) return;
            map[key] = map.TryGetValue(key, out var count) ? count + 1 : 1;
        }
        foreach (var item in historyEvidence)
        {
            var age = Math.Max(0, (now - item.Start).TotalDays);
            var weight = 2.2 * Math.Exp(-age / 270.0);
            if (item.ServiceIdentity.IsValid) Add(serviceWeights, item.ServiceIdentity, weight);
            Add(hourWeights, item.Hour, weight);
            foreach (var token in item.Terms) Add(termWeights, token, weight);
            Increment(seriesHistoryCounts, item.SeriesKey);
            if (TryGetNumericParenthesizedServiceEvidenceKey(item.ProgramTitle, item.ServiceIdentity, out var historyNumericServiceKey))
                Increment(numericServiceHistoryCounts, historyNumericServiceKey);
        }
        foreach (var item in snapshot.Reservations)
        {
            var facts = CanonicalEvidenceFacts(item, identityContext);
            var reservationWeight = ReservationEvidenceWeight(item);
            if (reservationWeight <= 0) continue;
            var reservationService = facts.ServiceIdentity;
            if (reservationService.IsValid) Add(serviceWeights, reservationService, 1.25 * reservationWeight);
            Add(hourWeights, facts.Hour, 1.25 * reservationWeight);
            foreach (var token in facts.Terms) Add(termWeights, token, 1.25 * reservationWeight);
            var reservationSeriesKey = facts.WorkKey;
            if (reservationSeriesKey.Length > 0)
            {
                Add(seriesReservationWeights, reservationSeriesKey, reservationWeight);
                if (item.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule)
                    Add(seriesAutomatedReservationWeights, reservationSeriesKey, reservationWeight);
            }
            if (TryGetNumericParenthesizedServiceEvidenceKey(item.ProgramTitle, reservationService, out var reservationNumericServiceKey))
            {
                Add(numericServiceReservationWeights, reservationNumericServiceKey, reservationWeight);
                if (item.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule)
                    Add(numericServiceAutomatedReservationWeights, reservationNumericServiceKey, reservationWeight);
            }
        }
        foreach (var tuner in snapshot.Tuners.Where(x =>
            x.IsInUse && string.Equals(x.UsageKind, "Viewing", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryResolveCurrentServiceIdentity(snapshot.Channels, tuner.ServiceName, out var viewingService))
                Add(serviceWeights, viewingService, 1.0);
            foreach (var token in Tokens(tuner.ProgramTitle)) Add(termWeights, token, 1.0);
        }

        var preferred = Words(snapshot.Settings.Preferred).ToArray();
        var excluded = Words(snapshot.Settings.Excluded).ToArray();
        var channelMap = snapshot.Channels
            .Select(x => (Identity: ServiceIdentityOf(x), Channel: x))
            .Where(x => x.Identity.IsValid)
            .GroupBy(x => x.Identity)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Channel.DisplayOrder).First().Channel);
        var reserved = new HashSet<string>(snapshot.Reservations
            .Select(x => $"{CanonicalWorkKey(x, identityContext)}|{ServiceIdentityOf(x)}"), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreAllocatedAfterSetup = GC.GetAllocatedBytesForCurrentThread();
        // Candidate-only canonical breakdown: exclude setup/history/reservation canonicalization.
        identityContext.ResetCanonicalAllocationBreakdown();
        long candidateCanonicalTextFilterBytes = 0;
        long candidateCanonicalFactsBytes = 0;
        long candidateTextBuildBytes = 0;
        long candidateExcludedFilterBytes = 0;
        long candidateSeriesGenreBytes = 0;
        long candidateTermsBytes = 0;
        long candidateContextInterestBytes = 0;
        long candidateLocalExternalBytes = 0;
        long candidateDedupeBytes = 0;
        long candidateLocalContinuityBytes = 0;
        long candidateLocalShadowBytes = 0;
        long candidateBroadcastIdentityBytes = 0;
        long candidateExternalProjectionBytes = 0;
        long candidateLocalAggregateAdjustmentBytes = 0;
        long candidateRecommendationBytes = 0;
        var candidateLoopVisited = 0;
        var candidateLoopExcluded = 0;
        var candidateLoopDeduped = 0;
        var candidateLoopBelowMinimum = 0;
#endif

        foreach (var item in snapshot.Events)
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseStart = GC.GetAllocatedBytesForCurrentThread();
            candidateLoopVisited++;
#endif
            var facts = CanonicalEvidenceFacts(item, identityContext);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterCanonicalFacts = GC.GetAllocatedBytesForCurrentThread();
            candidateCanonicalFactsBytes += candidatePhaseAfterCanonicalFacts - candidatePhaseStart;
#endif
            // All consumers below already use OrdinalIgnoreCase. Avoid creating a second lower-cased
            // copy of the complete title/summary/detail/genre text for every EPG candidate.
            var hay = $"{item.Title} {item.Summary} {item.Detail} {item.Genre}";
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterTextBuild = GC.GetAllocatedBytesForCurrentThread();
            candidateTextBuildBytes += candidatePhaseAfterTextBuild - candidatePhaseAfterCanonicalFacts;
#endif
            if (excluded.Any(x => hay.Contains(x, StringComparison.OrdinalIgnoreCase)))
            {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                var candidatePhaseAfterExcludedFilter = GC.GetAllocatedBytesForCurrentThread();
                candidateExcludedFilterBytes += candidatePhaseAfterExcludedFilter - candidatePhaseAfterTextBuild;
                candidateCanonicalTextFilterBytes += candidatePhaseAfterExcludedFilter - candidatePhaseStart;
                candidateLoopExcluded++;
#endif
                continue;
            }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterCanonicalText = GC.GetAllocatedBytesForCurrentThread();
            candidateExcludedFilterBytes += candidatePhaseAfterCanonicalText - candidatePhaseAfterTextBuild;
            candidateCanonicalTextFilterBytes += candidatePhaseAfterCanonicalText - candidatePhaseStart;
#endif

            var score = 10.0;
            var reasons = new List<string>();
            var seriesKey = facts.WorkKey;
            var evidenceSeriesKey = seriesKey;
            var historySeriesCount = FindSeriesEvidence(seriesHistoryCounts, evidenceSeriesKey);
            var reservationSeriesWeight = FindSeriesEvidence(seriesReservationWeights, evidenceSeriesKey);
            var automatedReservationSeriesWeight = FindSeriesEvidence(seriesAutomatedReservationWeights, evidenceSeriesKey);
            var probableEpisodeSequence = IsProbableParenthesizedEpisodeSequence(item, numericLocalValuesByService);
            var usedNumericHistoryBridge = false;
            var usedNumericReservationBridge = false;
            if (probableEpisodeSequence
                && TryGetNumericParenthesizedStemEvidenceKey(item.Title, out var numericStemKey)
                && TryGetNumericParenthesizedServiceEvidenceKey(item.Title, ServiceIdentityOf(item), out var numericServiceKey))
            {
                probableEpisodeSequenceCandidates++;
                var numericHistoryCount = FindSeriesEvidence(numericServiceHistoryCounts, numericServiceKey);
                if (numericHistoryCount > historySeriesCount)
                {
                    historySeriesCount = numericHistoryCount;
                    usedNumericHistoryBridge = numericHistoryCount > 0;
                    if (usedNumericHistoryBridge)
                    {
                        probableEpisodeHistoryBridges++;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                        if (numericHistoryBridgeSamples.Count < 8)
                        {
                            var candidateParts = GetNumericParenthesizedDiagnosticParts(item.Title);
                            var candidateService = ServiceIdentityOf(item);
                            var source = historyEvidence.FirstOrDefault(historyItem =>
                                TryGetNumericParenthesizedServiceEvidenceKey(historyItem.ProgramTitle, historyItem.ServiceIdentity, out var historyServiceKey)
                                && string.Equals(historyServiceKey, numericServiceKey, StringComparison.OrdinalIgnoreCase));
                            if (source is not null)
                            {
                                var sourceParts = GetNumericParenthesizedDiagnosticParts(source.ProgramTitle);
                                var sameService = candidateService.IsValid
                                    && source.ServiceIdentity.IsValid
                                    && candidateService.Equals(source.ServiceIdentity);
                                numericHistoryBridgeSamples.Add(
                                    $"candidate={item.Title} candidateValue={candidateParts.Value} stem={candidateParts.Stem} candidateService={candidateService} source={source.ProgramTitle} sourceValue={sourceParts.Value} sourceService={source.ServiceIdentity} sameService={sameService} reason=probable_local_sequence_and_history_stem_evidence");
                            }
                        }
#endif
                    }
                }

                var numericReservationWeight = FindSeriesEvidence(numericServiceReservationWeights, numericServiceKey);
                if (numericReservationWeight > reservationSeriesWeight)
                {
                    reservationSeriesWeight = numericReservationWeight;
                    usedNumericReservationBridge = numericReservationWeight > 0;
                    if (usedNumericReservationBridge)
                    {
                        probableEpisodeReservationBridges++;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                        if (numericReservationBridgeSamples.Count < 8)
                        {
                            var candidateParts = GetNumericParenthesizedDiagnosticParts(item.Title);
                            var candidateService = ServiceIdentityOf(item);
                            var source = snapshot.Reservations.FirstOrDefault(reservationItem =>
                            {
                                var reservationService = ServiceIdentityOf(reservationItem);
                                return TryGetNumericParenthesizedServiceEvidenceKey(reservationItem.ProgramTitle, reservationService, out var reservationServiceKey)
                                    && string.Equals(reservationServiceKey, numericServiceKey, StringComparison.OrdinalIgnoreCase);
                            });
                            if (source is not null)
                            {
                                var sourceParts = GetNumericParenthesizedDiagnosticParts(source.ProgramTitle);
                                var sourceService = ServiceIdentityOf(source);
                                var sameService = candidateService.IsValid
                                    && sourceService.IsValid
                                    && candidateService.Equals(sourceService);
                                numericReservationBridgeSamples.Add(
                                    $"candidate={item.Title} candidateValue={candidateParts.Value} stem={candidateParts.Stem} candidateService={candidateService} source={source.ProgramTitle} sourceValue={sourceParts.Value} sourceService={sourceService} sameService={sameService} reason=probable_local_sequence_and_reservation_stem_evidence");
                            }
                        }
#endif
                    }
                }

                automatedReservationSeriesWeight = Math.Max(
                    automatedReservationSeriesWeight,
                    FindSeriesEvidence(numericServiceAutomatedReservationWeights, numericServiceKey));
            }
            var genreKey = facts.GenreKey;
            var genreCount = FindGenreEvidence(genreCounts, genreKey);
            var genreShare = genreCount / (double)genreTotal;
            var hasSeriesIdentityEvidence = historySeriesCount > 0 || reservationSeriesWeight > 0;
            var hasStrongSeriesEvidence = historySeriesCount > 0 || reservationSeriesWeight >= 1.0;
            var genreComponentScore = 0.0;
            var seriesComponentScore = 0.0;
            var termComponentScore = 0.0;
            var serviceComponentScore = 0.0;
            var hourComponentScore = 0.0;
            var interestComponentScore = 0.0;
            var preferredComponentScore = 0.0;

            if (genreCount > 0)
            {
                var genreEvidence = Math.Min(1.0, genreCount / 6.0);
                var genreComponent = Math.Min(20.0, 20.0 * Math.Sqrt(genreShare) * (0.55 + 0.45 * genreEvidence));
                score += genreComponent;
                genreComponentScore = genreComponent;
                if (genreShare >= 0.12) reasons.Add("よく録るジャンル");
                else if (genreShare >= 0.04) reasons.Add("録画傾向にあるジャンル");
            }

            if (historySeriesCount > 0)
            {
                seriesComponentScore = Math.Min(24, 8 + Math.Log2(historySeriesCount + 1) * 4);
                score += seriesComponentScore;
                reasons.Add(usedNumericHistoryBridge
                    ? "連番作品の録画実績と近い"
                    : historySeriesCount >= 3 ? "よく録るシリーズ" : "録画した作品と近い");
            }
            else if (reservationSeriesWeight > 0)
            {
                seriesComponentScore = Math.Min(18, 6 + reservationSeriesWeight * 5);
                score += seriesComponentScore;
                reasons.Add(usedNumericReservationBridge
                    ? "連番作品の予約実績と近い"
                    : automatedReservationSeriesWeight > 0
                        ? "自動検索で予約する作品と近い"
                        : "予約した作品と近い");
            }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterSeriesGenre = GC.GetAllocatedBytesForCurrentThread();
            candidateSeriesGenreBytes += candidatePhaseAfterSeriesGenre - candidatePhaseAfterCanonicalText;
#endif

            // Preserve the existing OrdinalIgnoreCase substring semantics and top-3-by-weight contract,
            // but avoid a Where -> OrderByDescending -> Take -> ToArray allocation chain for every candidate.
            var matchedTermCount = 0;
            var topTermWeight1 = double.NegativeInfinity;
            var topTermWeight2 = double.NegativeInfinity;
            var topTermWeight3 = double.NegativeInfinity;
            foreach (var termWeight in termWeights)
            {
                if (termWeight.Key.Length < 2 || !hay.Contains(termWeight.Key, StringComparison.OrdinalIgnoreCase))
                    continue;

                matchedTermCount++;
                var value = termWeight.Value;
                if (value > topTermWeight1)
                {
                    topTermWeight3 = topTermWeight2;
                    topTermWeight2 = topTermWeight1;
                    topTermWeight1 = value;
                }
                else if (value > topTermWeight2)
                {
                    topTermWeight3 = topTermWeight2;
                    topTermWeight2 = value;
                }
                else if (value > topTermWeight3)
                {
                    topTermWeight3 = value;
                }
            }

            // Series identity and title-term similarity are two representations of the same title evidence.
            // When a direct Work/series identity match already exists, Terms become a fallback rather than
            // a second additive vote from the same recording/reservation action. This preserves Terms for
            // discovery across different Works while preventing one event from multiplying its own identity.
            if (matchedTermCount > 0 && !hasSeriesIdentityEvidence)
            {
                var topTermScore = Math.Min(6, topTermWeight1 * 0.75);
                if (matchedTermCount > 1) topTermScore += Math.Min(6, topTermWeight2 * 0.75);
                if (matchedTermCount > 2) topTermScore += Math.Min(6, topTermWeight3 * 0.75);
                termComponentScore = Math.Min(12, topTermScore);
                score += termComponentScore;
                reasons.Add("録画・予約の題名傾向と近い");
            }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterTerms = GC.GetAllocatedBytesForCurrentThread();
            candidateTermsBytes += candidatePhaseAfterTerms - candidatePhaseAfterSeriesGenre;
#endif

            var itemService = ServiceIdentityOf(item);
            if (itemService.IsValid && serviceWeights.TryGetValue(itemService, out var serviceWeight))
            {
                serviceComponentScore = Math.Min(6, Math.Log2(serviceWeight + 1) * 2.4);
                score += serviceComponentScore;
                if (serviceWeight >= 5) reasons.Add("よく録る放送局");
            }
            if (hourWeights.TryGetValue(facts.Hour, out var hourWeight))
            {
                hourComponentScore = Math.Min(5, Math.Log2(hourWeight + 1) * 1.8);
                score += hourComponentScore;
                if (hourWeight >= 5) reasons.Add("よく録る時間帯");
            }

            var exactInterest = false;
            var interestScore = 0.0;
            foreach (var signal in preparedInterestSignals)
            {
                // 「気になる」も録画・予約と同じ強系列キーの完全一致だけを強い証拠にする。
                // 部分一致は派生番組やコンテナ枠へ本編嗜好を借用させるため、強証拠には使わない。
                if (evidenceSeriesKey.Length > 0
                    && string.Equals(evidenceSeriesKey, signal.SeriesKey, StringComparison.OrdinalIgnoreCase))
                {
                    interestScore += signal.ExactWeight;
                    exactInterest = true;
                }
                else if (genreKey.Length > 0 && signal.GenreKey.Length > 0 && GenreMatches(genreKey, signal.GenreKey))
                {
                    interestScore += signal.GenreWeight;
                }
            }
            if (interestScore > 0)
            {
                interestComponentScore = Math.Min(14, interestScore);
                score += interestComponentScore;
                reasons.Add(exactInterest ? "気になる選択から" : "気になるジャンル");
            }

            var preferredHit = preferred.Any(x => hay.Contains(x, StringComparison.OrdinalIgnoreCase));
            if (preferredHit)
            {
                preferredComponentScore = 16;
                score += preferredComponentScore;
                reasons.Add("優先語に一致");
            }
            if (reserved.Contains($"{seriesKey}|{itemService}"))
            {
                // 予約済みは候補の状態であり、ユーザー嗜好そのものを弱める根拠ではない。
                // 表示スコアは録画・予約・利用傾向との適合度を示すため、予約状態による減点は行わない。
                reasons.Add("予約済み");
            }

            // 放送までの近さは番組への嗜好ではない。嗜好スコアには混ぜず、必要な棚・検索側で時刻条件として扱う。

            // 録画実績がないジャンルは、別の強い根拠がない限り高得点にしない。
            // 発見枠へ出すことと嗜好スコアは分離し、未知ジャンルの点数を偽装しない。
            var confidenceCap = 98;
            if (genreCount == 0 && !hasStrongSeriesEvidence && !preferredHit && !exactInterest)
                confidenceCap = 44;
            else if (genreCount <= 1 && genreShare < 0.02 && !hasStrongSeriesEvidence && !preferredHit)
                confidenceCap = 55;
            else if (genreShare < 0.05 && historySeriesCount == 0 && !preferredHit)
                confidenceCap = 72;

            // 生スコア100は、十分な継続録画実績とジャンル嗜好が同時にある場合だけに限定する。
            if (historySeriesCount >= 5 && genreShare >= 0.10 && (preferredHit || exactInterest))
                confidenceCap = 100;

            var contentEvidence = 0;
            if (genreShare >= 0.12) contentEvidence++;
            if (matchedTermCount > 0) contentEvidence++;
            if (interestScore > 0) contentEvidence++;
            var contextualEvidence = 0;
            if (itemService.IsValid && serviceWeights.TryGetValue(itemService, out var trustServiceWeight) && trustServiceWeight >= 5) contextualEvidence++;
            if (hourWeights.TryGetValue(facts.Hour, out var trustHourWeight) && trustHourWeight >= 5) contextualEvidence++;

            // 局・時間帯は「それらしい」補助材料にはなるが、それだけで納得候補にはしない。
            // 番組内容側の根拠が複数ある、または内容根拠に利用傾向が重なる場合だけ納得側へ寄せる。
            var isConvincing = historySeriesCount > 0
                || reservationSeriesWeight >= 1.0
                || exactInterest
                || preferredHit
                || contentEvidence >= 2
                || (contentEvidence >= 1 && contextualEvidence >= 1);
            var hasContentConnection = genreCount > 0 || matchedTermCount > 0 || interestScore > 0 || preferredHit;
            var isPlausibleDiscovery = !isConvincing && hasContentConnection && score >= 20;

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterContextInterest = GC.GetAllocatedBytesForCurrentThread();
            candidateContextInterestBytes += candidatePhaseAfterContextInterest - candidatePhaseAfterTerms;
#endif

            var duplicateKey = seriesKey.Length > 0
                ? $"{seriesKey}|{itemService}"
                : $"event:{item.NetworkId}:{item.TransportStreamId}:{item.ServiceId}:{item.EventNumber}:{item.Start.UtcDateTime.Ticks}";
            var acceptedByDedupe = seen.Add(duplicateKey);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterDedupe = GC.GetAllocatedBytesForCurrentThread();
            candidateDedupeBytes += candidatePhaseAfterDedupe - candidatePhaseAfterContextInterest;
#endif
            if (!acceptedByDedupe)
            {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                candidateLocalExternalBytes += candidatePhaseAfterDedupe - candidatePhaseAfterContextInterest;
                candidateLoopDeduped++;
#endif
                continue;
            }
            // 偏差表示も既存の信頼度上限を共有し、丸めだけを行わない。
            // これにより推薦判定と表示スコアで評価基準が分岐しない。
            var deviationRawScore = Math.Min(score, confidenceCap);
            var rawScore = Math.Clamp((int)Math.Round(score), 0, confidenceCap);
            if (rawScore < 8)
            {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                candidateLocalExternalBytes += GC.GetAllocatedBytesForCurrentThread() - candidatePhaseAfterContextInterest;
                candidateLoopBelowMinimum++;
#endif
                continue;
            }
            var localContinuityEvaluation = EvaluateLocalContinuityFromCanonicalFacts(
                item,
                probableEpisodeSequence,
                numericLocalValuesByService,
                localEvidenceState.ContinuitySources,
                localWorkAliases,
                facts);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterLocalContinuity = GC.GetAllocatedBytesForCurrentThread();
            candidateLocalContinuityBytes += candidatePhaseAfterLocalContinuity - candidatePhaseAfterDedupe;
            localEvidenceShadowRows.Add(EvaluateLocalEvidenceShadow(
                item,
                localContinuityEvaluation,
                localEvidenceState.WorkAggregates,
                now));
            var candidatePhaseAfterLocalShadow = GC.GetAllocatedBytesForCurrentThread();
            candidateLocalShadowBytes += candidatePhaseAfterLocalShadow - candidatePhaseAfterLocalContinuity;
#endif
            var broadcast = itemService.IsValid && channelMap.TryGetValue(itemService, out var channel) ? channel.BroadcastType : string.Empty;
            var identity = new AIrhythmEventIdentity(item.NetworkId, item.TransportStreamId, item.ServiceId, item.EventNumber, item.Start);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            baseScoreAuditRows.Add((
                identity,
                item.Title,
                historySeriesCount,
                reservationSeriesWeight,
                genreCount,
                genreShare,
                genreComponentScore,
                seriesComponentScore,
                termComponentScore,
                serviceComponentScore,
                hourComponentScore,
                interestComponentScore,
                preferredComponentScore,
                score,
                confidenceCap,
                rawScore,
                isConvincing));
            var candidatePhaseAfterBroadcastIdentity = GC.GetAllocatedBytesForCurrentThread();
            candidateBroadcastIdentityBytes += candidatePhaseAfterBroadcastIdentity - candidatePhaseAfterLocalShadow;
#endif
            var externalEvidenceProjection = snapshot.Settings.ExternalLookupEnabled
                ? AIrhythmDataState.GetExternalEvidenceProjection(item)
                : (AIrhythmExternalEvidenceSummaryStatus.NoEvidence, AIrhythmExternalEvidenceAdjustmentKind.None, AIrhythmExternalEvidenceVerdictReason.NotApplicable);
            var externalEvidenceSummary = externalEvidenceProjection.Item1;
            var externalEvidenceEvaluationFlags = ToExternalEvidenceEvaluationFlags(externalEvidenceSummary);
            var externalEvidenceConfidenceGate = ToExternalEvidenceConfidenceGate(externalEvidenceEvaluationFlags);
            var externalEvidenceAdjustmentCandidate = ToExternalEvidenceAdjustmentCandidate(externalEvidenceConfidenceGate);
            var externalEvidenceAdjustmentKind = NormalizeExternalEvidenceAdjustmentKind(externalEvidenceAdjustmentCandidate, externalEvidenceProjection.Item2);
            var externalEvidenceAdjustmentStrength = ToExternalEvidenceAdjustmentStrength(externalEvidenceAdjustmentCandidate, externalEvidenceAdjustmentKind);
            var externalEvidenceSupportingVerdictReason = externalEvidenceAdjustmentCandidate == AIrhythmExternalEvidenceAdjustmentCandidate.Eligible
                ? externalEvidenceProjection.Item3
                : AIrhythmExternalEvidenceVerdictReason.NotApplicable;
            // External evidence is supplemental: only Supported -> Allowed -> Eligible reaches a non-zero strength.
            // Apply that small value to the continuous score after the existing confidence cap and before
            // population deviation normalization. Unresolved / Conflicting / NoEvidence remain exactly zero.
            var externalEvidenceAdjustmentValue = ToExternalEvidenceAdjustmentValue(externalEvidenceAdjustmentStrength);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterExternalProjection = GC.GetAllocatedBytesForCurrentThread();
            candidateExternalProjectionBytes += candidatePhaseAfterExternalProjection - candidatePhaseAfterBroadcastIdentity;
#endif
            localEvidenceState.WorkAggregates.TryGetValue(localContinuityEvaluation.WorkKey, out var localWorkAggregate);
            var localInterestStrength = EvaluateInterestStrength(localWorkAggregate);
            var localEvidenceAdjustmentValue = LocalEvidenceAdjustmentValue(
                localContinuityEvaluation.Strength,
                localInterestStrength,
                out var localDominantFamily);
            if (localEvidenceAdjustmentValue > 0.0d)
            {
                reasons.Insert(0, localDominantFamily == AIrhythmLocalEvidenceFamily.ContinuityFamily
                    ? "録画・予約した作品の続き"
                    : "継続して録画・予約している作品");
            }
            var adjustedDeviationRawScore = deviationRawScore + externalEvidenceAdjustmentValue + localEvidenceAdjustmentValue;
            var externalEvidenceShadowAdjustmentValue = externalEvidenceAdjustmentValue; // retained for developer telemetry compatibility
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterLocalExternal = GC.GetAllocatedBytesForCurrentThread();
            candidateLocalAggregateAdjustmentBytes += candidatePhaseAfterLocalExternal - candidatePhaseAfterExternalProjection;
            candidateLocalExternalBytes += candidatePhaseAfterLocalExternal - candidatePhaseAfterContextInterest;
#endif
            scored.Add(new(item.Title, item.ServiceName, broadcast, item.Genre ?? string.Empty, item.Start, rawScore, reasons.Distinct().Take(4).ToArray(), seriesKey, identity, isConvincing, isPlausibleDiscovery, rawScore, adjustedDeviationRawScore, externalEvidenceSummary, externalEvidenceEvaluationFlags, externalEvidenceConfidenceGate, externalEvidenceAdjustmentCandidate, externalEvidenceAdjustmentKind, externalEvidenceAdjustmentStrength, externalEvidenceSupportingVerdictReason, externalEvidenceShadowAdjustmentValue, externalEvidenceAdjustmentValue));
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            candidateRecommendationBytes += GC.GetAllocatedBytesForCurrentThread() - candidatePhaseAfterLocalExternal;
#endif
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreAllocatedAfterCandidateLoop = GC.GetAllocatedBytesForCurrentThread();
#endif

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        // Keep candidate-level Shadow diagnostics bounded. Recommendation scoring can run often and
        // the EPG candidate population can be large, so logging every row would make diagnostics
        // scale with both candidate count and refresh count. Summary statistics still cover the
        // complete population; the per-candidate logs are a deterministic evidence-focused sample.
        const int localEvidenceShadowLogLimit = 64;
        const int localEvidenceNoEvidenceSampleLimit = 8;
        const int localEvidenceDetailLogLimit = 16;
        static bool NeedsLocalEvidenceDetail(AIrhythmLocalEvidenceShadowRow row)
            => row.Continuity == AIrhythmLocalEvidenceStrength.Strong
                || (row.RecordingCount + row.ReservationCount >= 10 && row.ActiveWeeks <= 1)
                || row.Replay;

        var localEvidenceRowsWithEvidence = localEvidenceShadowRows
            .Where(row => row.Continuity != AIrhythmLocalEvidenceStrength.None || row.Interest != AIrhythmLocalEvidenceStrength.None)
            .OrderByDescending(row => NeedsLocalEvidenceDetail(row))
            .ThenByDescending(row => Math.Max((int)row.Continuity, (int)row.Interest))
            .ThenBy(row => row.Work, StringComparer.OrdinalIgnoreCase)
            .Take(localEvidenceShadowLogLimit)
            .ToArray();
        var localEvidenceNoEvidenceSample = localEvidenceShadowRows
            .Where(row => row.Continuity == AIrhythmLocalEvidenceStrength.None && row.Interest == AIrhythmLocalEvidenceStrength.None)
            .OrderBy(row => row.Work, StringComparer.OrdinalIgnoreCase)
            .Take(localEvidenceNoEvidenceSampleLimit)
            .ToArray();
        var localEvidenceLoggedRows = localEvidenceRowsWithEvidence
            .Concat(localEvidenceNoEvidenceSample)
            .ToArray();

        foreach (var row in localEvidenceLoggedRows)
        {
            AIrhythmDataState.WriteDeveloperLog(
                $"LOCAL_EVIDENCE_SHADOW work={row.Work} episode={row.Episode} continuity={row.Continuity} recordingCount={row.RecordingCount} reservationCount={row.ReservationCount} activeDays={row.ActiveDays} activeWeeks={row.ActiveWeeks} lastSeenDays={row.LastSeenDays} interest={row.Interest} dominantFamily={row.DominantFamily} proposedAdjustment={row.ProposedAdjustment} applied={(row.DominantFamily == AIrhythmLocalEvidenceFamily.ContinuityFamily && LocalContinuityAdjustmentValue(row.Continuity) > 0.0d ? "continuity" : row.DominantFamily == AIrhythmLocalEvidenceFamily.ExplicitInterestFamily && LocalInterestAdjustmentValue(row.Interest) > 0.0d ? "interest" : "False")}");
        }

        foreach (var row in localEvidenceShadowRows
            .Where(NeedsLocalEvidenceDetail)
            .OrderByDescending(row => Math.Max((int)row.Continuity, (int)row.Interest))
            .ThenBy(row => row.Work, StringComparer.OrdinalIgnoreCase)
            .Take(localEvidenceDetailLogLimit))
        {
            AIrhythmDataState.WriteDeveloperLog(
                $"LOCAL_EVIDENCE_DETAIL work={row.Work} episode={row.Episode} continuity={row.Continuity} source={row.ContinuitySourceKind} episodeDistance={row.EpisodeDistance} differentService={row.DifferentService} replay={row.Replay} recordingCount={row.RecordingCount} reservationCount={row.ReservationCount} activeDays={row.ActiveDays} activeWeeks={row.ActiveWeeks} interest={row.Interest} dominantFamily={row.DominantFamily} proposedAdjustment={row.ProposedAdjustment} applied={(row.DominantFamily == AIrhythmLocalEvidenceFamily.ContinuityFamily && LocalContinuityAdjustmentValue(row.Continuity) > 0.0d ? "continuity" : row.DominantFamily == AIrhythmLocalEvidenceFamily.ExplicitInterestFamily && LocalInterestAdjustmentValue(row.Interest) > 0.0d ? "interest" : "False")}");
        }

        var localEvidenceCount = 0;
        var localContinuityStrong = 0;
        var localContinuityModerate = 0;
        var localInterestStrong = 0;
        var localInterestModerate = 0;
        var localNoEvidence = 0;
        var localDominantRows = 0;
        var localDetailRows = 0;
        var localContinuityApplied = 0;
        var localInterestApplied = 0;
        var localReplayInterestApplied = 0;
        foreach (var row in localEvidenceShadowRows)
        {
            if (row.Continuity == AIrhythmLocalEvidenceStrength.Strong) localContinuityStrong++;
            if (row.Continuity == AIrhythmLocalEvidenceStrength.Moderate) localContinuityModerate++;
            if (row.Interest == AIrhythmLocalEvidenceStrength.Strong) localInterestStrong++;
            if (row.Interest == AIrhythmLocalEvidenceStrength.Moderate) localInterestModerate++;
            var hasEvidence = row.Continuity != AIrhythmLocalEvidenceStrength.None || row.Interest != AIrhythmLocalEvidenceStrength.None;
            if (hasEvidence) localEvidenceCount++; else localNoEvidence++;
            if (row.DominantFamily != AIrhythmLocalEvidenceFamily.None) localDominantRows++;
            if (NeedsLocalEvidenceDetail(row)) localDetailRows++;
            if (row.DominantFamily == AIrhythmLocalEvidenceFamily.ContinuityFamily && LocalContinuityAdjustmentValue(row.Continuity) > 0.0d) localContinuityApplied++;
            if (row.DominantFamily == AIrhythmLocalEvidenceFamily.ExplicitInterestFamily && LocalInterestAdjustmentValue(row.Interest) > 0.0d)
            {
                localInterestApplied++;
                if (row.Replay) localReplayInterestApplied++;
            }
        }
        var localEvidenceAliasSamples = localWorkAliases
            .OrderBy(pair => pair.Key.WorkKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key.ServiceIdentity.ToString(), StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(pair => $"{pair.Key.WorkKey}@{pair.Key.ServiceIdentity}->{pair.Value}")
            .ToArray();
        AIrhythmDataState.WriteDeveloperLog(
            $"LOCAL_EVIDENCE_WORK_ALIAS count={localWorkAliases.Count} samples=[{string.Join("/", localEvidenceAliasSamples)}]");
        AIrhythmDataState.WriteDeveloperLog(
            $"LOCAL_EVIDENCE_SUMMARY candidates={localEvidenceShadowRows.Count} continuityStrong={localContinuityStrong} continuityModerate={localContinuityModerate} interestStrong={localInterestStrong} interestModerate={localInterestModerate} noLocalEvidence={localNoEvidence} dominantRows={localDominantRows} evidenceRows={localEvidenceCount} evidenceLogged={localEvidenceRowsWithEvidence.Length} evidenceTruncated={Math.Max(0, localEvidenceCount - localEvidenceRowsWithEvidence.Length)} noEvidenceSampled={localEvidenceNoEvidenceSample.Length} detailLogged={Math.Min(localDetailRows, localEvidenceDetailLogLimit)} continuityApplied={localContinuityApplied} interestApplied={localInterestApplied} replayInterestApplied={localReplayInterestApplied} diagnosticsAggregation=single_pass");

        AIrhythmDataState.WriteDeveloperLog($"local numeric episode evidence candidates={probableEpisodeSequenceCandidates} historyBridges={probableEpisodeHistoryBridges} reservationBridges={probableEpisodeReservationBridges} historySamples={numericHistoryBridgeSamples.Count} reservationSamples={numericReservationBridgeSamples.Count} policy=corroborated_neighbor_only");
        foreach (var sample in numericHistoryBridgeSamples)
            AIrhythmDataState.WriteDeveloperLog($"local numeric episode history bridge {sample}");
        foreach (var sample in numericReservationBridgeSamples)
            AIrhythmDataState.WriteDeveloperLog($"local numeric episode reservation bridge {sample}");
        var projectionNoEvidence = 0;
        var projectionUnresolved = 0;
        var projectionSupported = 0;
        var projectionConflicting = 0;
        var evaluationFlagNone = 0;
        var evaluationFlagUnresolved = 0;
        var evaluationFlagSupported = 0;
        var evaluationFlagConflicting = 0;
        var evaluationFlagMismatch = 0;
        var confidenceGateNotApplicable = 0;
        var confidenceGateNeutral = 0;
        var confidenceGateAllowed = 0;
        var confidenceGateBlocked = 0;
        var confidenceGateMismatch = 0;
        var adjustmentCandidateNone = 0;
        var adjustmentCandidateEligible = 0;
        var adjustmentCandidateMismatch = 0;
        var adjustmentKindNone = 0;
        var adjustmentKindIdentity = 0;
        var adjustmentKindEpisode = 0;
        var adjustmentKindRelation = 0;
        var adjustmentKindMismatch = 0;
        var adjustmentStrengthNone = 0;
        var adjustmentStrengthWeak = 0;
        var adjustmentStrengthModerate = 0;
        var adjustmentStrengthMismatch = 0;
        var shadowAdjustmentValueNonZero = 0;
        var shadowAdjustmentValueMismatch = 0;
        var shadowAdjustmentValueTotal = 0.0d;
        var shadowAdjustmentValueMax = 0.0d;
        var adjustmentValueNonZero = 0;
        foreach (var recommendation in scored)
        {
            switch (recommendation.ExternalEvidenceSummary)
            {
                case AIrhythmExternalEvidenceSummaryStatus.NoEvidence: projectionNoEvidence++; break;
                case AIrhythmExternalEvidenceSummaryStatus.UnresolvedOnly: projectionUnresolved++; break;
                case AIrhythmExternalEvidenceSummaryStatus.Supported: projectionSupported++; break;
                case AIrhythmExternalEvidenceSummaryStatus.Conflicting: projectionConflicting++; break;
            }

            if (recommendation.ExternalEvidenceEvaluationFlags == AIrhythmExternalEvidenceEvaluationFlags.None) evaluationFlagNone++;
            if (recommendation.ExternalEvidenceEvaluationFlags.HasFlag(AIrhythmExternalEvidenceEvaluationFlags.Unresolved)) evaluationFlagUnresolved++;
            if (recommendation.ExternalEvidenceEvaluationFlags.HasFlag(AIrhythmExternalEvidenceEvaluationFlags.Supported)) evaluationFlagSupported++;
            if (recommendation.ExternalEvidenceEvaluationFlags.HasFlag(AIrhythmExternalEvidenceEvaluationFlags.Conflicting)) evaluationFlagConflicting++;
            if (recommendation.ExternalEvidenceEvaluationFlags != ToExternalEvidenceEvaluationFlags(recommendation.ExternalEvidenceSummary)) evaluationFlagMismatch++;

            switch (recommendation.ExternalEvidenceConfidenceGate)
            {
                case AIrhythmExternalEvidenceConfidenceGate.NotApplicable: confidenceGateNotApplicable++; break;
                case AIrhythmExternalEvidenceConfidenceGate.Neutral: confidenceGateNeutral++; break;
                case AIrhythmExternalEvidenceConfidenceGate.Allowed: confidenceGateAllowed++; break;
                case AIrhythmExternalEvidenceConfidenceGate.Blocked: confidenceGateBlocked++; break;
            }
            if (recommendation.ExternalEvidenceConfidenceGate != ToExternalEvidenceConfidenceGate(recommendation.ExternalEvidenceEvaluationFlags)) confidenceGateMismatch++;

            if (recommendation.ExternalEvidenceAdjustmentCandidate == AIrhythmExternalEvidenceAdjustmentCandidate.None) adjustmentCandidateNone++;
            if (recommendation.ExternalEvidenceAdjustmentCandidate == AIrhythmExternalEvidenceAdjustmentCandidate.Eligible) adjustmentCandidateEligible++;
            if (recommendation.ExternalEvidenceAdjustmentCandidate != ToExternalEvidenceAdjustmentCandidate(recommendation.ExternalEvidenceConfidenceGate)) adjustmentCandidateMismatch++;

            switch (recommendation.ExternalEvidenceAdjustmentKind)
            {
                case AIrhythmExternalEvidenceAdjustmentKind.None: adjustmentKindNone++; break;
                case AIrhythmExternalEvidenceAdjustmentKind.IdentitySupport: adjustmentKindIdentity++; break;
                case AIrhythmExternalEvidenceAdjustmentKind.EpisodeSupport: adjustmentKindEpisode++; break;
                case AIrhythmExternalEvidenceAdjustmentKind.RelationSupport: adjustmentKindRelation++; break;
            }
            if (recommendation.ExternalEvidenceAdjustmentCandidate != AIrhythmExternalEvidenceAdjustmentCandidate.Eligible
                && recommendation.ExternalEvidenceAdjustmentKind != AIrhythmExternalEvidenceAdjustmentKind.None) adjustmentKindMismatch++;

            switch (recommendation.ExternalEvidenceAdjustmentStrength)
            {
                case AIrhythmExternalEvidenceAdjustmentStrength.None: adjustmentStrengthNone++; break;
                case AIrhythmExternalEvidenceAdjustmentStrength.Weak: adjustmentStrengthWeak++; break;
                case AIrhythmExternalEvidenceAdjustmentStrength.Moderate: adjustmentStrengthModerate++; break;
            }
            if (recommendation.ExternalEvidenceAdjustmentStrength != ToExternalEvidenceAdjustmentStrength(recommendation.ExternalEvidenceAdjustmentCandidate, recommendation.ExternalEvidenceAdjustmentKind)) adjustmentStrengthMismatch++;

            if (Math.Abs(recommendation.ExternalEvidenceShadowAdjustmentValue) > 0.000001d) shadowAdjustmentValueNonZero++;
            if (Math.Abs(recommendation.ExternalEvidenceShadowAdjustmentValue - ToExternalEvidenceAdjustmentValue(recommendation.ExternalEvidenceAdjustmentStrength)) > 0.000001d) shadowAdjustmentValueMismatch++;
            shadowAdjustmentValueTotal += recommendation.ExternalEvidenceShadowAdjustmentValue;
            if (recommendation.ExternalEvidenceShadowAdjustmentValue > shadowAdjustmentValueMax) shadowAdjustmentValueMax = recommendation.ExternalEvidenceShadowAdjustmentValue;
            if (Math.Abs(recommendation.ExternalEvidenceAdjustmentValue) > 0.000001d) adjustmentValueNonZero++;
        }
        AIrhythmDataState.WriteDeveloperLog($"external evidence recommendation projection total={scored.Count} noEvidence={projectionNoEvidence} unresolved={projectionUnresolved} supported={projectionSupported} conflicting={projectionConflicting} evaluationFlagNone={evaluationFlagNone} evaluationFlagUnresolved={evaluationFlagUnresolved} evaluationFlagSupported={evaluationFlagSupported} evaluationFlagConflicting={evaluationFlagConflicting} evaluationFlagMismatch={evaluationFlagMismatch} confidenceGateNotApplicable={confidenceGateNotApplicable} confidenceGateNeutral={confidenceGateNeutral} confidenceGateAllowed={confidenceGateAllowed} confidenceGateBlocked={confidenceGateBlocked} confidenceGateMismatch={confidenceGateMismatch} adjustmentCandidateNone={adjustmentCandidateNone} adjustmentCandidateEligible={adjustmentCandidateEligible} adjustmentCandidateMismatch={adjustmentCandidateMismatch} adjustmentKindNone={adjustmentKindNone} adjustmentKindIdentity={adjustmentKindIdentity} adjustmentKindEpisode={adjustmentKindEpisode} adjustmentKindRelation={adjustmentKindRelation} adjustmentKindMismatch={adjustmentKindMismatch} adjustmentStrengthNone={adjustmentStrengthNone} adjustmentStrengthWeak={adjustmentStrengthWeak} adjustmentStrengthModerate={adjustmentStrengthModerate} adjustmentStrengthMismatch={adjustmentStrengthMismatch} shadowAdjustmentValueNonZero={shadowAdjustmentValueNonZero} shadowAdjustmentValueMismatch={shadowAdjustmentValueMismatch} shadowAdjustmentValueTotal={shadowAdjustmentValueTotal:0.00} shadowAdjustmentValueMax={shadowAdjustmentValueMax:0.00} adjustmentValueNonZero={adjustmentValueNonZero} evaluationConsumer=read_only confidenceGateConsumer=read_only adjustmentCandidateConsumer=read_only adjustmentKindConsumer=read_only adjustmentStrengthConsumer=read_only adjustmentStrengthPolicy=identity_weak_episode_moderate_relation_weak_reserved shadowAdjustmentValuePolicy=raw_coordinate_applied_weak_0.25_moderate_0.75_supported_only shadowCoordinate=DeviationRawScore_post_confidence_cap_pre_deviation_normalization displayedScoreAdditivePath=retired adjustmentValuePolicy=raw_coordinate_supported_only recommendationMutation=supported_only scoreMutation=supported_only reasonMutation=False orderingMutation=score_derived diagnosticsAggregation=single_pass");
        var scoreAllocatedAfterPreDeviationDiagnostics = GC.GetAllocatedBytesForCurrentThread();
#endif
        var deviationScored = ApplyDeviationScores(scored);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreAllocatedAfterDeviation = GC.GetAllocatedBytesForCurrentThread();
        var baseScoreAuditByIdentity = baseScoreAuditRows.ToDictionary(
            row => $"{row.Identity.NetworkId}:{row.Identity.TransportStreamId}:{row.Identity.ServiceId}:{row.Identity.EventNumber}:{row.Identity.Start.UtcDateTime.Ticks}",
            row => row,
            StringComparer.OrdinalIgnoreCase);
        foreach (var ranked in deviationScored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Start)
            .Take(24)
            .Select((item, index) => new { Item = item, Rank = index + 1 }))
        {
            if (ranked.Item.EventIdentity is null)
                continue;
            var auditKey = $"{ranked.Item.EventIdentity.NetworkId}:{ranked.Item.EventIdentity.TransportStreamId}:{ranked.Item.EventIdentity.ServiceId}:{ranked.Item.EventIdentity.EventNumber}:{ranked.Item.EventIdentity.Start.UtcDateTime.Ticks}";
            if (!baseScoreAuditByIdentity.TryGetValue(auditKey, out var audit))
                continue;
            AIrhythmDataState.WriteDeveloperLog(
                $"BASE_SCORE_COMPONENT_AUDIT rank={ranked.Rank} displayScore={ranked.Item.Score} title={audit.Title} historySeriesCount={audit.HistorySeriesCount} reservationSeriesWeight={audit.ReservationSeriesWeight:0.00} genreCount={audit.GenreCount} genreShare={audit.GenreShare:0.0000} components=base:10.00,genre:{audit.GenreComponent:0.00},series:{audit.SeriesComponent:0.00},terms:{audit.TermComponent:0.00},service:{audit.ServiceComponent:0.00},hour:{audit.HourComponent:0.00},interest:{audit.InterestComponent:0.00},preferred:{audit.PreferredComponent:0.00} scoreBeforeCap={audit.ScoreBeforeCap:0.00} confidenceCap={audit.ConfidenceCap} rawScore={audit.RawScore} convincing={audit.Convincing} policy=observe_only_no_score_mutation");
        }
        AIrhythmDataState.WriteDeveloperLog(
            $"BASE_SCORE_COMPONENT_AUDIT_SUMMARY logged={Math.Min(24, deviationScored.Count)} componentPolicy=factual_decomposition_only scoreMutation=False");

        var currentRankByCandidateKey = deviationScored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Start)
            .Select((item, index) => new
            {
                Key = item.EventIdentity is null
                    ? string.Empty
                    : $"{item.EventIdentity.NetworkId}:{item.EventIdentity.TransportStreamId}:{item.EventIdentity.ServiceId}:{item.EventIdentity.EventNumber}:{item.EventIdentity.Start.UtcDateTime.Ticks}",
                Rank = index + 1,
                item.Score,
                item.RawScore,
                item.DeviationRawScore
            })
            .Where(x => x.Key.Length > 0)
            .ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

        const int localEvidenceRankLogLimit = 24;
        var localEvidenceRankRows = localEvidenceShadowRows
            .Where(row => row.DominantFamily != AIrhythmLocalEvidenceFamily.None)
            .Select(row => new { Row = row, Current = currentRankByCandidateKey.GetValueOrDefault(row.CandidateKey) })
            .Where(x => x.Current is not null)
            .OrderBy(x => x.Current!.Rank)
            .ThenByDescending(x => Math.Max((int)x.Row.Continuity, (int)x.Row.Interest))
            .Take(localEvidenceRankLogLimit)
            .ToArray();
        foreach (var entry in localEvidenceRankRows)
        {
            var current = entry.Current!;
            AIrhythmDataState.WriteDeveloperLog(
                $"LOCAL_EVIDENCE_CURRENT_RANK rank={current.Rank} displayScore={current.Score} rawScore={current.RawScore} deviationRawScore={current.DeviationRawScore:0.00} work={entry.Row.Work} episode={entry.Row.Episode} continuity={entry.Row.Continuity} interest={entry.Row.Interest} dominantFamily={entry.Row.DominantFamily} continuityApplied={(entry.Row.DominantFamily == AIrhythmLocalEvidenceFamily.ContinuityFamily && LocalContinuityAdjustmentValue(entry.Row.Continuity) > 0.0d)} interestApplied={(entry.Row.DominantFamily == AIrhythmLocalEvidenceFamily.ExplicitInterestFamily && LocalInterestAdjustmentValue(entry.Row.Interest) > 0.0d)}");
        }
        AIrhythmDataState.WriteDeveloperLog(
            $"LOCAL_EVIDENCE_RANK_SUMMARY evidenceRows={localEvidenceCount} rankedRows={localEvidenceRankRows.Length} rankLogLimit={localEvidenceRankLogLimit} continuityApplied={localContinuityApplied} interestApplied={localInterestApplied} purpose=post_local_evidence_baseline");

        var top30WorkOccupancy = localEvidenceShadowRows
            .Select(row => new { Row = row, Current = currentRankByCandidateKey.GetValueOrDefault(row.CandidateKey) })
            .Where(x => x.Current is not null && x.Current.Rank <= 30 && x.Row.Work.Length > 0)
            .GroupBy(x => x.Row.Work, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Work = group.Key, Count = group.Count(), BestRank = group.Min(x => x.Current!.Rank) })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.BestRank)
            .ToArray();
        var top30WorkOccupancySamples = top30WorkOccupancy.Take(8).ToArray();
        AIrhythmDataState.WriteDeveloperLog(
            $"LOCAL_WORK_TOP30_OCCUPANCY uniqueWorks={top30WorkOccupancy.Length} repeatedWorks={top30WorkOccupancy.Count(x => x.Count > 1)} maxPerWork={(top30WorkOccupancy.Length == 0 ? 0 : top30WorkOccupancy.Max(x => x.Count))} samples=[{string.Join(",", top30WorkOccupancySamples.Select(x => $"{x.Work}:{x.Count}@{x.BestRank}"))}] policy=observe_only_no_diversity_mutation");

        // Continuity and Explicit Interest now share one dominant-family product coordinate.
        // These summaries verify that each applied family stays aligned with the same canonical
        // evaluation used by scoring; correlated evidence is never added twice.
        AIrhythmDataState.WriteDeveloperLog(
            $"LOCAL_CONTINUITY_APPLIED_SUMMARY strongRaw=0.50 moderateRaw=0.25 candidates={localContinuityApplied} interestApplied={localInterestApplied} coordinate=DeviationRawScore_post_confidence_cap_pre_deviation_normalization");

        AIrhythmDataState.WriteDeveloperLog(
            $"LOCAL_INTEREST_APPLIED_SUMMARY strongRaw=0.250 moderateRaw=0.125 weakRaw=0.000 candidates={localInterestApplied} continuityBaseline=applied coordinate=DeviationRawScore_post_confidence_cap_pre_deviation_normalization");
        var scoreAllocatedAfterPostDeviationDiagnostics = GC.GetAllocatedBytesForCurrentThread();
#endif
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreAllocatedBytes = scoreAllocatedAfterPostDeviationDiagnostics - scoreAllocatedStart;
        var setupBytes = scoreAllocatedAfterSetup - scoreAllocatedStart;
        var candidateLoopBytes = scoreAllocatedAfterCandidateLoop - scoreAllocatedAfterSetup;
        var preDeviationDiagnosticsBytes = scoreAllocatedAfterPreDeviationDiagnostics - scoreAllocatedAfterCandidateLoop;
        var deviationBytes = scoreAllocatedAfterDeviation - scoreAllocatedAfterPreDeviationDiagnostics;
        var postDeviationDiagnosticsBytes = scoreAllocatedAfterPostDeviationDiagnostics - scoreAllocatedAfterDeviation;
        var candidateSubphaseAccountedBytes = candidateCanonicalTextFilterBytes + candidateSeriesGenreBytes + candidateTermsBytes + candidateContextInterestBytes + candidateLocalExternalBytes + candidateRecommendationBytes;
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_CANDIDATE_ALLOCATION_PHASES canonicalTextFilterBytes={candidateCanonicalTextFilterBytes} seriesGenreBytes={candidateSeriesGenreBytes} termsBytes={candidateTermsBytes} contextInterestBytes={candidateContextInterestBytes} localExternalBytes={candidateLocalExternalBytes} recommendationBytes={candidateRecommendationBytes} accountedBytes={candidateSubphaseAccountedBytes} candidateLoopBytes={candidateLoopBytes} unaccountedBytes={candidateLoopBytes - candidateSubphaseAccountedBytes} visited={candidateLoopVisited} excluded={candidateLoopExcluded} deduped={candidateLoopDeduped} belowMinimum={candidateLoopBelowMinimum} scored={deviationScored.Count} scope=current_thread_exact samplingPoints=aggregate_only_no_midphase_logging semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_CANDIDATE_CANONICAL_DETAIL canonicalFactsBytes={candidateCanonicalFactsBytes} textBuildBytes={candidateTextBuildBytes} excludedFilterBytes={candidateExcludedFilterBytes} parentBytes={candidateCanonicalTextFilterBytes} accountedBytes={candidateCanonicalFactsBytes + candidateTextBuildBytes + candidateExcludedFilterBytes} unaccountedBytes={candidateCanonicalTextFilterBytes - (candidateCanonicalFactsBytes + candidateTextBuildBytes + candidateExcludedFilterBytes)} visited={candidateLoopVisited} excluded={candidateLoopExcluded} scope=current_thread_exact semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_CANONICAL_FACTS_INTERNAL preEvaluationTitleBytes={identityContext.CanonicalPreEvaluationTitleBytes} localWorkKeyBytes={identityContext.CanonicalLocalWorkKeyBytes} aliasResolutionBytes={identityContext.CanonicalAliasResolutionBytes} genreBytes={identityContext.CanonicalGenreBytes} tokensBytes={identityContext.CanonicalTokensBytes} replayBytes={identityContext.CanonicalReplayBytes} accountedBytes={identityContext.CanonicalPreEvaluationTitleBytes + identityContext.CanonicalLocalWorkKeyBytes + identityContext.CanonicalAliasResolutionBytes + identityContext.CanonicalGenreBytes + identityContext.CanonicalTokensBytes + identityContext.CanonicalReplayBytes} parentCanonicalFactsBytes={candidateCanonicalFactsBytes} unaccountedBytes={candidateCanonicalFactsBytes - (identityContext.CanonicalPreEvaluationTitleBytes + identityContext.CanonicalLocalWorkKeyBytes + identityContext.CanonicalAliasResolutionBytes + identityContext.CanonicalGenreBytes + identityContext.CanonicalTokensBytes + identityContext.CanonicalReplayBytes)} scope=candidate_loop_only_current_thread_exact semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_CANDIDATE_LOCAL_EXTERNAL_DETAIL dedupeBytes={candidateDedupeBytes} localContinuityBytes={candidateLocalContinuityBytes} localShadowBytes={candidateLocalShadowBytes} broadcastIdentityBytes={candidateBroadcastIdentityBytes} externalProjectionBytes={candidateExternalProjectionBytes} localAggregateAdjustmentBytes={candidateLocalAggregateAdjustmentBytes} parentBytes={candidateLocalExternalBytes} accountedBytes={candidateDedupeBytes + candidateLocalContinuityBytes + candidateLocalShadowBytes + candidateBroadcastIdentityBytes + candidateExternalProjectionBytes + candidateLocalAggregateAdjustmentBytes} unaccountedBytes={candidateLocalExternalBytes - (candidateDedupeBytes + candidateLocalContinuityBytes + candidateLocalShadowBytes + candidateBroadcastIdentityBytes + candidateExternalProjectionBytes + candidateLocalAggregateAdjustmentBytes)} deduped={candidateLoopDeduped} belowMinimum={candidateLoopBelowMinimum} scored={deviationScored.Count} scope=current_thread_exact semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_ALLOCATION_PHASES setupBytes={setupBytes} candidateLoopBytes={candidateLoopBytes} preDeviationDiagnosticsBytes={preDeviationDiagnosticsBytes} deviationBytes={deviationBytes} postDeviationDiagnosticsBytes={postDeviationDiagnosticsBytes} accountedBytes={setupBytes + candidateLoopBytes + preDeviationDiagnosticsBytes + deviationBytes + postDeviationDiagnosticsBytes} scope=current_thread_exact samplingPoints=no_midphase_logging semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_ALLOCATION allocatedBytes={scoreAllocatedBytes} events={snapshot.Events.Count} scored={deviationScored.Count} termWeights={termWeights.Count} interestSignals={preparedInterestSignals.Length} scope=current_thread_exact candidateTextLowerCopy=removed termMatchMaterialization=single_pass_top3 interestSignalNormalization=single_pass phaseBreakdown=enabled semantics=unchanged");
#endif
        return deviationScored;
    }

    private static IReadOnlyList<AIrhythmRecommendation> ApplyDeviationScores(IReadOnlyList<AIrhythmRecommendation> items)
    {
        if (items.Count == 0) return Array.Empty<AIrhythmRecommendation>();

        // 偏差表示は、評価対象として残った全候補の未丸め・既存confidenceCap適用済みスコアを同じ母集団として扱う。
        // 特定の棚・番組・ジャンル・録画件数による補正や母集団の選別は行わない。
        // 表示値だけを標準的な偏差値（平均50、標準偏差10）へ変換する。
        if (items.Count <= 1)
            return items.Select(x => x with { Score = 50 }).ToArray();

        var mean = items.Average(x => x.DeviationRawScore);
        var variance = items.Average(x =>
        {
            var delta = x.DeviationRawScore - mean;
            return delta * delta;
        });
        var standardDeviation = Math.Sqrt(variance);
        if (standardDeviation < 0.000001)
            return items.Select(x => x with { Score = 50 }).ToArray();

        return items.Select(x =>
        {
            var deviation = (x.DeviationRawScore - mean) / standardDeviation;
            var displayScore = Math.Clamp((int)Math.Round(50 + 10 * deviation), 0, 100);
            return x with { Score = displayScore };
        }).ToArray();
    }

    private static int FindSeriesEvidence(IReadOnlyDictionary<string, int> counts, string seriesKey)
    {
        // 録画回数のような強い系列証拠は、同じ安定系列キーへ収束した場合だけ共有する。
        // 部分一致は関連作品の発見には使えても、同一作品を追っている証拠にはしない。
        if (seriesKey.Length < 3) return 0;
        return counts.TryGetValue(seriesKey, out var exact) ? exact : 0;
    }

    private static double FindSeriesEvidence(IReadOnlyDictionary<string, double> weights, string seriesKey)
    {
        // 自動検索・予約由来の継続意思も、同じ安定系列キーにだけ帰属させる。
        if (seriesKey.Length < 3) return 0;
        return weights.TryGetValue(seriesKey, out var exact) ? exact : 0;
    }

    private static AIrhythmServiceIdentity ServiceIdentityOf(TvAirProgramEventDto value)
        => new(value.NetworkId, value.TransportStreamId, value.ServiceId);

    private static AIrhythmServiceIdentity ServiceIdentityOf(TvAirReservationDto value)
        => new(value.NetworkId, value.TransportStreamId, value.ServiceId);

    private static AIrhythmServiceIdentity ServiceIdentityOf(TvAirRecordingHistoryDto value)
        => new(value.NetworkId, value.TransportStreamId, value.ServiceId);

    private static AIrhythmServiceIdentity ServiceIdentityOf(TvAirServiceDto value)
        => new(value.NetworkId, value.TransportStreamId, value.ServiceId);

    private static AIrhythmServiceIdentity ServiceIdentityOf(AIrhythmEventIdentity value)
        => new(value.NetworkId, value.TransportStreamId, value.ServiceId);

    private static AIrhythmServiceIdentity ServiceIdentityOf(AIrhythmInterestSignal value)
        => new(value.NetworkId, value.TransportStreamId, value.ServiceId);

    private static bool TryResolveCurrentServiceIdentity(
        IReadOnlyList<TvAirServiceDto> channels, string? serviceName, out AIrhythmServiceIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(serviceName)) return false;
        var matches = channels
            .Where(x => string.Equals(x.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase))
            .Select(ServiceIdentityOf)
            .Where(x => x.IsValid)
            .Distinct()
            .Take(2)
            .ToArray();
        if (matches.Length != 1) return false;
        identity = matches[0];
        return true;
    }

    private static string ResolveCurrentServiceName(
        IReadOnlyList<TvAirServiceDto> channels, AIrhythmServiceIdentity identity, string fallback)
    {
        var current = channels.FirstOrDefault(x => ServiceIdentityOf(x) == identity)?.ServiceName;
        return string.IsNullOrWhiteSpace(current) ? fallback : current;
    }

    private static string NormalizeGenre(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+", string.Empty);
        normalized = normalized.Replace("／", "/", StringComparison.Ordinal);
        return normalized;
    }

    private static bool GenreMatches(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return false;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindGenreEvidence(IReadOnlyDictionary<string, int> counts, string genreKey)
    {
        if (genreKey.Length == 0) return 0;
        if (counts.TryGetValue(genreKey, out var exact)) return exact;
        return counts.Where(x => GenreMatches(x.Key, genreKey)).Select(x => x.Value).DefaultIfEmpty(0).Max();
    }


    private static string BuildDiscovery(
        RuntimeUiRenderContext context,
        AIrhythmRuntimeSnapshot snapshot,
        IReadOnlyList<AIrhythmRecommendation> allRecommendations,
        IReadOnlyList<AIrhythmRecommendation> recommendations,
        AIrhythmEvidenceIdentityContext identityContext)
    {
        var now = DateTimeOffset.Now;
        var reserved = new HashSet<string>(snapshot.Reservations.Select(x => CanonicalWorkKey(x, identityContext)).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
        var recorded = new HashSet<string>(snapshot.History.Select(x => CanonicalWorkKey(x, identityContext)).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
        var eventMap = snapshot.Events
            .GroupBy(EventIdentityOf)
            .ToDictionary(x => x.Key, x => x.First());

        TvAirProgramEventDto? EventOf(AIrhythmRecommendation item)
            => item.EventIdentity is not null && eventMap.TryGetValue(item.EventIdentity, out var value) ? value : null;

        bool ContainsAny(string text, params string[] markers)
            => markers.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));

        // 「発見」は予約リストの新番組タブと責務を重ねない。
        // ここではローカル履歴・予約だけを正本に、まだ録画も予約もしていない作品を出す。
        var firstSeen = recommendations
            .Where(x => !recorded.Contains(x.SeriesKey) && !reserved.Contains(x.SeriesKey))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Start)
            .Take(6)
            .ToArray();

        var replayFinds = recommendations.Where(x =>
        {
            var e = EventOf(x);
            var text = $"{x.Title} {e?.Summary} {e?.Detail}";
            var title = x.SeriesKey;
            return ContainsAny(text, "[再]", "【再】", "再放送", "アンコール", "一挙", "リピート")
                && !reserved.Contains(title) && !recorded.Contains(title);
        }).Take(6).ToArray();

        var tonightEnd = new DateTimeOffset(now.Date.AddDays(1).AddHours(4), now.Offset);
        var tonight = recommendations.Where(x => x.Start >= now && x.Start <= tonightEnd).Take(6).ToArray();

        var frequentServices = snapshot.Reservations.Select(ServiceIdentityOf)
            .Concat(snapshot.History.Select(ServiceIdentityOf))
            .Where(x => x.IsValid)
            .GroupBy(x => x)
            .OrderByDescending(x => x.Count()).Take(5).Select(x => x.Key)
            .ToHashSet();
        var surprise = recommendations
            .Where(x => x.EventIdentity is not null && !frequentServices.Contains(ServiceIdentityOf(x.EventIdentity)) && x.RawScore >= 20 && x.RawScore < 70)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Start)
            .Take(6)
            .ToArray();
        string Shelf(string css, string icon, string title, string subtitle, IReadOnlyList<AIrhythmRecommendation> items, bool showScore = false)
        {
            if (items.Count == 0)
                return $"<section class='discovery-shelf discovery-shelf-empty {css}'><div class='shelf-title'><span class='shelf-accent' aria-hidden='true'></span><div><h3>{AIrhythmHtml.Encode(title)}</h3><p>{AIrhythmHtml.Encode(subtitle)}</p></div></div><div class='shelf-empty'>現在候補はありません</div></section>";
            var cards = string.Join(string.Empty, items.Select((x, index) =>
            {
                var scoreHtml = showScore ? $"<b>{x.Score}</b>" : string.Empty;
                return $"<article class='spark-card'><div class='spark-rank'>{index + 1}</div><div class='spark-body'>{ProgramTitleElement("strong", x.Title)}<span>{AIrhythmHtml.Encode(x.ServiceName)}・{x.Start:MM/dd HH:mm}</span><p>{AIrhythmHtml.Encode(x.Reasons.Count > 0 ? string.Join("・", x.Reasons.Take(2)) : "あなたの傾向から発見")}</p>{ReserveButton(context, x, snapshot, true)}</div>{scoreHtml}</article>";
            }));
            return $"<section class='discovery-shelf {css}'><div class='shelf-title'><span class='shelf-accent' aria-hidden='true'></span><div><h3>{AIrhythmHtml.Encode(title)}</h3><p>{AIrhythmHtml.Encode(subtitle)}</p></div></div><div class='spark-grid'>{cards}</div></section>";
        }

        var active = snapshot.Advanced.ActiveRecordings
            .Select(x => new AIrhythmRecommendation(x.ProgramTitle, x.ServiceName, string.Empty, "録画中", x.Start, 0, new[] { $"{x.TunerName}で録画中", $"終了予定 {x.ScheduledEnd:HH:mm}" }, CanonicalWorkKey(x.ProgramTitle, new AIrhythmServiceIdentity(x.NetworkId, x.TransportStreamId, x.ServiceId), identityContext), null))
            .Take(6).ToArray();

        var qualityIssues = snapshot.RecoveryHistory
            .Where(x => x.ResultFinalized)
            .Where(x => IsRecordingFailure(x)
                || (x.QualityDataAvailable && ((x.DropCount ?? 0) > 0 || (x.ErrorCount ?? 0) > 0 || (x.ScrambleCount ?? 0) > 0)))
            .Select(x => new
            {
                SeriesKey = CanonicalWorkKey(x, identityContext),
                Severity = IsRecordingFailure(x) ? long.MaxValue / 4 : QualitySeverity(x),
                Failure = IsRecordingFailure(x),
                x.ProgramTitle
            })
            .Where(x => x.SeriesKey.Length > 0)
            .ToArray();
        // 取り直し候補は推薦の「納得/発見」選抜とは別契約。
        // 録画失敗・品質結果と再放送候補の一致を正本にし、推薦信頼度で候補を落とさない。
        var recovery = allRecommendations
            .Select(x => new
            {
                Item = x,
                Match = qualityIssues
                    .Where(q => WorkMatches(q.SeriesKey, x.SeriesKey))
                    .OrderByDescending(q => string.Equals(q.SeriesKey, x.SeriesKey, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(q => q.Severity)
                    .FirstOrDefault()
            })
            .Where(x => x.Match is not null)
            .Where(x =>
            {
                var text = $"{x.Item.Title} {EventOf(x.Item)?.Summary} {EventOf(x.Item)?.Detail}";
                return ContainsAny(text, "[再]", "【再】", "再放送", "アンコール", "リピート", "一挙");
            })
            .OrderByDescending(x => string.Equals(x.Match!.SeriesKey, x.Item.SeriesKey, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Match!.Severity)
            .ThenByDescending(x => x.Item.Score)
            .ThenBy(x => x.Item.Start)
            .Select(x => x.Item with { Reasons = x.Match!.Failure
                ? new[] { "録画できなかった番組", "同じ番組の再放送候補" }
                : new[] { "録画品質に問題があった番組", "同じ番組の再放送候補" } })
            .Take(6)
            .ToArray();

        return string.Concat(
            "<div class='discovery-group-heading'><strong>今</strong><span>録画中のもの、今夜の候補</span></div>",
            Shelf("shelf-active", "REC", "いま録画中", "現在進行中の録画セッション", active),
            Shelf("shelf-tonight", "✦", "今夜のおすすめ", "今から深夜4時までの候補", tonight),
            "<div class='discovery-group-heading'><strong>もう一度</strong><span>再放送や、録画し直せる候補</span></div>",
            Shelf("shelf-recovery", "FIX", "取り直し候補", "録画できなかった、または品質情報に問題があった番組", recovery),
            Shelf("shelf-replay", "↻", "再放送を見つける", "未予約・未録画の再放送候補", replayFinds),
            "<div class='discovery-group-heading'><strong>発見</strong><span>まだ録画していない作品や、いつもと少し違う候補</span></div>",
            Shelf("shelf-first-seen", "NEW", "はじめて見る作品", "まだ録画・予約していない候補", firstSeen),
            Shelf("shelf-surprise", "!", "いつもと違う発見", "普段あまり選ばない局から見つけた候補", surprise));
    }

    private static bool IsNewProgram(string title, TvAirProgramEventDto? value)
    {
        var text = $"{title} {value?.Summary} {value?.Detail} {value?.ExtendedItems}";
        if (ContainsAny(text, "[新]", "【新】", "新番組", "新シリーズ", "初回", "第1話", "第１話", "#1", "＃1", "第1回", "第１回"))
            return true;
        return Regex.IsMatch(text, @"(?:^|\s)(?:episode|ep\.?)[\s:：-]*0?1(?:\D|$)", RegexOptions.IgnoreCase);
    }

    private static int NewProgramConfidence(string title, TvAirProgramEventDto? value)
    {
        var text = $"{title} {value?.Summary} {value?.Detail} {value?.ExtendedItems}";
        var confidence = 0;
        if (ContainsAny(text, "[新]", "【新】", "新番組", "新シリーズ")) confidence += 4;
        if (ContainsAny(text, "初回", "第1話", "第１話", "第1回", "第１回")) confidence += 3;
        if (ContainsAny(text, "#1", "＃1")) confidence += 2;
        return confidence;
    }

    private static bool IsRecordingFailure(TvAirRecordingHistoryDto value)
    {
        var state = $"{value.Result} {value.EndReason}";
        if (ContainsAny(state, "cancel", "abort", "取消", "中止")) return false;
        if (ContainsAny(state, "fail", "error", "失敗")) return true;
        return value.FileCreated == false;
    }

    private static long QualitySeverity(TvAirRecordingHistoryDto value)
        => Math.Max(0, value.ErrorCount ?? 0) * 1_000_000L
            + Math.Max(0, value.ScrambleCount ?? 0) * 10_000L
            + Math.Max(0, value.DropCount ?? 0);

    private static AIrhythmEventIdentity EventIdentityOf(TvAirProgramEventDto value)
        => new(value.NetworkId, value.TransportStreamId, value.ServiceId, value.EventNumber, value.Start);



    internal static string StripNonIdentityBroadcastAnnotations(string value)
    {
        // The pattern below can only match ASCII '['. Most titles carry no such broadcast tag,
        // so bypass Regex entirely when a match is impossible. This is an allocation-only fast path.
        if (value.IndexOf('[') < 0)
            return value;

        // 括弧内を一律に捨てない。Season2、作品番号、固有副題など作品識別に必要な情報まで
        // 消してしまうため、放送属性として意味が確定している短いタグだけを除去する。
        // ARIB decode / EPG presentation attributes are not work identity.
        // Keep this list explicit and conservative: only tags whose meaning is known as
        // broadcast/presentation/classification metadata are removed here.
        return Regex.Replace(
            value,
            @"\[\s*(?:字|解|デ|双|多|二|S|SS|B|N|天|交|映|新|終|再|初|生|HV|SD|無|料|前|後|手|吹|字幕|PG12|R15\+)\s*\]",
            " ",
            RegexOptions.IgnoreCase);
    }

    private readonly record struct TitleEvidenceParts(
        string ProgramIdentity,
        string SeasonOrEdition,
        string EpisodeOrSession,
        string SubtitleOrDetail,
        string HostAttribute);

    // 共通Work Identity正本の内部パーサー。画面・スコア・集計から直接使わず、
    // CanonicalWorkKey 経由でだけ利用する。文字列を単純に削るのではなく、
    // 作品本体と各回属性を保守的に分離する。
    // 年・大会番号・作品番号・Season 等は作品階層になり得るため、意味が確定しない限り保持する。
    private static string EvidenceSeriesKey(string? value)
    {
        var parts = ParseTitleEvidence(value);
        return CompactIdentity(parts.ProgramIdentity);
    }

    private static TitleEvidenceParts ParseTitleEvidence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

        var normalized = value.Normalize(NormalizationForm.FormKC);
        normalized = StripNonIdentityBroadcastAnnotations(normalized);

        // TvAIr のプログラム予約は表示名末尾へ曜日を付加する。
        // これは EPG タイトルではなく Host 管理属性なので、作品同一性からだけ外して保持する。
        var hostAttribute = string.Empty;
        if (normalized.Length > 0 && (normalized[^1] == ')' || normalized[^1] == '）'))
        {
            var weekday = Regex.Match(normalized, @"\s*[（(]\s*(月|火|水|木|金|土|日)\s*[）)]\s*$", RegexOptions.IgnoreCase);
            if (weekday.Success)
            {
                hostAttribute = weekday.Value.Trim();
                normalized = normalized[..weekday.Index].TrimEnd();
            }
        }

        var seasonOrEdition = string.Empty;
        var episodeOrSession = string.Empty;
        var subtitleOrDetail = string.Empty;

        // 「第N話/回」「#N」「EP N」など、意味が明確な各回マーカーのみ分離する。
        // 「第N戦」は大会/ラウンド識別、「SeasonN」はシーズン識別になり得るため、ここでは消さない。
        var mayContainEpisodeMarker = normalized.IndexOf('#') >= 0
            || normalized.IndexOf('＃') >= 0
            || normalized.IndexOf('話') >= 0
            || normalized.IndexOf('回') >= 0
            || normalized.Contains("ep", StringComparison.OrdinalIgnoreCase);
        var marker = mayContainEpisodeMarker
            ? Regex.Match(
                normalized,
                @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)",
                RegexOptions.IgnoreCase)
            : Match.Empty;
        if (marker.Success)
        {
            episodeOrSession = marker.Value.Trim();
            var prefix = normalized[..marker.Index].TrimEnd();
            var suffix = normalized[(marker.Index + marker.Length)..].Trim();

            // 明示話数の直前に閉じた長い括弧ブロックがある場合は各話副題として分離する。
            // 放送属性の短いタグは上で除去済み。括弧そのものだけを理由に全タイトルからは削らない。
            var bracketSubtitle = Regex.Match(prefix, @"(?:【[^【】]{2,}】|\[[^\[\]]{2,}\])\s*$");
            if (bracketSubtitle.Success)
            {
                subtitleOrDetail = bracketSubtitle.Value.Trim();
                prefix = prefix[..bracketSubtitle.Index].TrimEnd();
            }

            if (suffix.Length > 0)
                subtitleOrDetail = subtitleOrDetail.Length == 0 ? suffix : $"{subtitleOrDetail} {suffix}";

            var prefixIdentityLength = prefix.Count(char.IsLetterOrDigit);
            normalized = prefixIdentityLength >= 2
                ? prefix
                : normalized.Remove(marker.Index, marker.Length);
        }

        if (normalized.Contains("新番組", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("初回", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("最終回", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("再放送", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("アンコール", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("リピート", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("一挙放送", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Regex.Replace(normalized, @"(?:新番組|初回|最終回|再放送|アンコール|リピート|一挙放送)", " ", RegexOptions.IgnoreCase);
        }
        return new(normalized.Trim(), seasonOrEdition, episodeOrSession, subtitleOrDetail, hostAttribute);
    }

    private static string CompactIdentity(string value)
    {
        var count = 0;
        foreach (var ch in value)
            if (char.IsLetterOrDigit(ch))
                count++;
        if (count == 0)
            return string.Empty;
        return string.Create(count, value, static (span, source) =>
        {
            var index = 0;
            foreach (var ch in source)
                if (char.IsLetterOrDigit(ch))
                    span[index++] = char.ToLowerInvariant(ch);
        });
    }

    private static bool WorkMatches(string left, string right)
    {
        if (left.Length < 3 || right.Length < 3) return false;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        if (Math.Min(left.Length, right.Length) < 5) return false;
        return left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    // ExternalLookup is supplemental evidence only. This boundary deliberately does not
    // alter canonical Work Identity or recommendation score. It only identifies
    // title shapes for which local text alone may be insufficient.
    internal static AIrhythmExternalEvidenceNeed EvaluateExternalEvidenceNeed(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new(false, AIrhythmExternalEvidenceNeedReason.None);

        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC)).Trim();
        if (normalized.Length == 0)
            return new(false, AIrhythmExternalEvidenceNeedReason.None);

        // A trailing parenthesized number can be an episode (e.g. a serial drama) or a
        // stable part of a work title. Do not reinterpret it locally without corroboration.
        if (Regex.IsMatch(normalized, @"[（(]\s*[0-9０-９]+\s*[）)](?:\s*)$"))
            return new(true, AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix);

        // A leading long corner-bracket block often represents a broadcast container, but
        // bracket text can also belong to a work title. Mark it ambiguous instead of stripping it.
        if (Regex.IsMatch(normalized, @"^【[^【】]{2,}】\s*\S+"))
            return new(true, AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate);

        // Preview/PR/recap-like variants are related to a work without being identical to it.
        // External evidence may later help classify the relation; until then keep them separate.
        if (Regex.IsMatch(normalized, @"(?:^|[\s　])(?:PR|ＰＲ|予告|みどころ|総集編)(?:$|[\s　])", RegexOptions.IgnoreCase)
            || Regex.IsMatch(normalized, @"(?:PR|ＰＲ|予告|みどころ|総集編)$", RegexOptions.IgnoreCase))
            return new(true, AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker);

        // Explicit episode markers are deterministic local structure. They are parsed
        // internally and do not, by themselves, justify an external lookup.
        if (Regex.IsMatch(normalized, @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)", RegexOptions.IgnoreCase))
            return new(false, AIrhythmExternalEvidenceNeedReason.None);

        return new(false, AIrhythmExternalEvidenceNeedReason.None);
    }

    internal static AIrhythmDerivedProgramRelation ClassifyDerivedProgramRelation(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return AIrhythmDerivedProgramRelation.None;

        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC)).Trim();
        if (Regex.IsMatch(normalized, @"(?:PR|ＰＲ|予告)(?:\s|　)*$", RegexOptions.IgnoreCase))
            return AIrhythmDerivedProgramRelation.Promo;
        if (Regex.IsMatch(normalized, @"みどころ(?:\s|　)*$", RegexOptions.IgnoreCase))
            return AIrhythmDerivedProgramRelation.Preview;
        if (Regex.IsMatch(normalized, @"総集編(?:\s|　)*$", RegexOptions.IgnoreCase))
            return AIrhythmDerivedProgramRelation.Recap;
        return AIrhythmDerivedProgramRelation.None;
    }

    internal static string BuildExternalLookupQueryTitle(string? title)
        => BuildExternalLookupQueryTitles(title).FirstOrDefault() ?? string.Empty;

    internal static IReadOnlyList<string> BuildExternalLookupQueryTitles(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Array.Empty<string>();

        static string Clean(string value)
        {
            var cleaned = Regex.Replace(value, @"\s+", " ").Trim();
            return cleaned.Length <= 160 ? cleaned : cleaned[..160].Trim();
        }

        var root = Clean(StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC)).Trim());
        if (root.Length == 0)
            return Array.Empty<string>();

        var containerParts = ParseLeadingContainerParts(root);
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        void Add(string value)
        {
            value = Clean(value);
            if (value.Length >= 2 && seen.Add(value))
            {
                results.Add(value);
                queue.Enqueue(value);
            }
        }

        Add(root);
        if (!string.IsNullOrWhiteSpace(containerParts.WorkCandidate))
            Add(containerParts.WorkCandidate);
        while (queue.Count > 0 && results.Count < 8)
        {
            var value = queue.Dequeue();
            Add(Regex.Replace(value, @"^【[^【】]{2,}】\s*", string.Empty).Trim());
            Add(Regex.Replace(value, @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)(?:\s*[/／].*)?$", string.Empty, RegexOptions.IgnoreCase).Trim());
            Add(Regex.Replace(value, @"[（(]\s*[0-9０-９]+\s*[）)]\s*$", string.Empty).Trim());
            Add(Regex.Replace(value, @"(?:\s|　)*(?:PR|ＰＲ|予告|みどころ|総集編)(?:\s|　)*$", string.Empty, RegexOptions.IgnoreCase).Trim());

            var quoted = Regex.Match(value, @"[「『]([^」』]{2,80})[」』]");
            if (quoted.Success)
                Add(quoted.Groups[1].Value);
        }

        // Prefer the structurally extracted work candidate for a leading container. A quoted
        // item after a colon/descriptor is useful supplemental context, but must not outrank
        // the surrounding programme identity merely because it is shorter. Keep the original
        // title as a broad fallback.
        return results
            .OrderBy(item => !string.IsNullOrWhiteSpace(containerParts.WorkCandidate)
                && string.Equals(item, containerParts.WorkCandidate, StringComparison.OrdinalIgnoreCase) ? 0
                : !string.IsNullOrWhiteSpace(containerParts.EmbeddedTopic)
                    && string.Equals(item, containerParts.EmbeddedTopic, StringComparison.OrdinalIgnoreCase) ? 2
                    : item == root ? 3 : 1)
            .ThenBy(item => item.Length)
            .Take(6)
            .ToArray();
    }

    internal static AIrhythmLeadingContainerParts ParseLeadingContainerParts(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

        var normalized = Regex.Replace(
            StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC)).Trim(),
            @"\s+", " ").Trim();
        var match = Regex.Match(normalized, @"^【([^【】]{2,})】\s*(.+)$");
        if (!match.Success)
            return new(normalized, string.Empty, string.Empty, string.Empty, string.Empty);

        var container = match.Groups[1].Value.Trim();
        var remainder = match.Groups[2].Value.Trim();
        var work = remainder;
        var topic = string.Empty;

        // A quoted phrase introduced after a colon commonly describes an item/topic within the
        // programme rather than replacing the programme identity. This is structural only: the
        // quoted text is retained as supplemental evidence and no canonical title is rewritten.
        var topicMatch = Regex.Match(remainder, @"^(.{2,80}?)[：:]\s*[「『]([^」』]{2,80})[」』](?:\s+ほか.*)?$");
        if (topicMatch.Success)
        {
            work = topicMatch.Groups[1].Value.Trim();
            topic = topicMatch.Groups[2].Value.Trim();
        }

        // Remove only deterministic suffix forms already handled elsewhere. This yields a search
        // candidate; it does not alter SeriesKey/EvidenceSeriesKey or the stored programme title.
        work = Regex.Replace(work, @"(?:\s|　)*(?:PR|ＰＲ|予告|みどころ|総集編)(?:\s|　)*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        work = Regex.Replace(work, @"[（(]\s*[0-9０-９]+\s*[）)]\s*$", string.Empty).Trim();

        return new(normalized, container, remainder, work, topic);
    }

    internal static AIrhythmNumericParenthesizedClass ClassifyNumericParenthesizedSuffix(
        string? title,
        IReadOnlyDictionary<string, int>? localSequenceCounts = null)
    {
        if (!TryParseTrailingParenthesizedNumber(title, out var stem, out var value))
            return AIrhythmNumericParenthesizedClass.None;

        if (IsLikelyCalendarYear(value))
            return AIrhythmNumericParenthesizedClass.LikelyYear;

        if (localSequenceCounts is not null
            && localSequenceCounts.TryGetValue(stem, out var distinctCount)
            && distinctCount >= 3)
            return AIrhythmNumericParenthesizedClass.LocalSequence;

        return AIrhythmNumericParenthesizedClass.Ambiguous;
    }

    internal static IReadOnlyDictionary<string, int[]> BuildNumericParenthesizedLocalSequenceValues(
        IReadOnlyList<TvAirProgramEventDto> events)
    {
        var groups = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events)
        {
            if (!TryParseTrailingParenthesizedNumber(item.Title, out var stem, out var value)
                || IsLikelyCalendarYear(value)
                || value <= 0
                || value > 999)
                continue;

            if (!groups.TryGetValue(stem, out var values))
            {
                values = new HashSet<int>();
                groups[stem] = values;
            }
            values.Add(value);
        }

        return groups.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(value => value).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyDictionary<string, int[]> BuildNumericParenthesizedLocalSequenceValuesByService(
        IReadOnlyList<TvAirProgramEventDto> events)
    {
        var groups = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events)
        {
            if (!TryParseTrailingParenthesizedNumber(item.Title, out var stem, out var value)
                || IsLikelyCalendarYear(value)
                || value <= 0
                || value > 999)
                continue;

            var service = ServiceIdentityOf(item);
            if (!service.IsValid)
                continue;

            var key = BuildNumericServiceSequenceKey(stem, service);
            if (!groups.TryGetValue(key, out var values))
            {
                values = new HashSet<int>();
                groups[key] = values;
            }
            values.Add(value);
        }

        return groups.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(value => value).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildNumericServiceSequenceKey(string stem, AIrhythmServiceIdentity service)
        => $"{CompactIdentity(stem)}|{service.NetworkId}:{service.TransportStreamId}:{service.ServiceId}";

    internal static AIrhythmNumericParenthesizedProbeContext GetNumericParenthesizedProbeContext(
        string? title,
        IReadOnlyDictionary<string, int[]> localValues)
    {
        if (!TryParseTrailingParenthesizedNumber(title, out var stem, out var value))
            return new(0, 0, -1);

        if (!localValues.TryGetValue(stem, out var values) || values.Length == 0)
            return new(value, 0, -1);

        var nearest = values
            .Where(candidate => candidate != value)
            .Select(candidate => Math.Abs(candidate - value))
            .DefaultIfEmpty(-1)
            .Min();
        return new(value, values.Length, nearest);
    }

    private static bool IsProbableParenthesizedEpisodeSequence(
        string? title,
        AIrhythmServiceIdentity service,
        IReadOnlyDictionary<string, int[]> localValuesByService)
    {
        if (!TryParseTrailingParenthesizedNumber(title, out var stem, out var value)
            || IsLikelyCalendarYear(value)
            || value <= 0
            || value > 999
            || !service.IsValid
            || !localValuesByService.TryGetValue(BuildNumericServiceSequenceKey(stem, service), out var values)
            || values.Length < 2)
            return false;

        // Parenthesized episode inference is valid only on the stable service that supplied
        // the corroborating local sequence. Never promote a stem observed on one service into
        // a global work identity for another service.
        if (values.Length >= 3)
            return true;

        return values.Any(candidate => candidate != value && Math.Abs(candidate - value) == 1);
    }

    internal static bool IsProbableParenthesizedEpisodeSequence(
        TvAirProgramEventDto item,
        IReadOnlyDictionary<string, int[]> localValuesByService)
        => IsProbableParenthesizedEpisodeSequence(item.Title, ServiceIdentityOf(item), localValuesByService);

    private static bool TryGetNumericParenthesizedStemEvidenceKey(string? title, out string key)
    {
        key = string.Empty;
        if (!TryParseTrailingParenthesizedNumber(title, out var stem, out var value)
            || IsLikelyCalendarYear(value)
            || value <= 0
            || value > 999)
            return false;

        key = CompactIdentity(stem);
        return key.Length >= 3;
    }

    private static bool TryGetNumericParenthesizedServiceEvidenceKey(
        string? title,
        AIrhythmServiceIdentity service,
        out string key)
    {
        key = string.Empty;
        if (!service.IsValid
            || !TryGetNumericParenthesizedStemEvidenceKey(title, out var stemKey))
            return false;

        key = $"{stemKey}|{service.NetworkId}:{service.TransportStreamId}:{service.ServiceId}";
        return true;
    }

    internal static (string NormalizedTitle, string Stem, int Value) GetNumericParenthesizedDiagnosticParts(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return (string.Empty, string.Empty, 0);

        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC)).Trim();
        if (!TryParseTrailingParenthesizedNumber(title, out var stem, out var value))
            return (normalized, string.Empty, 0);

        return (normalized, stem, value);
    }

    private static bool TryParseTrailingParenthesizedNumber(string? title, out string stem, out int value)
    {
        stem = string.Empty;
        value = 0;
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC)).Trim();
        var match = Regex.Match(normalized, @"^(.*?)[（(]\s*([0-9]+)\s*[）)]\s*$");
        if (!match.Success
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return false;

        stem = Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim();
        return stem.Length >= 2;
    }

    private static bool IsLikelyCalendarYear(int value)
        => value is >= 1900 and <= 2099;

    internal static int? TryGetExternalEpisodeCandidate(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        // External episode follow-up also needs a single episode. A range must not be represented
        // as the first number, otherwise provider evidence for one episode is attached to a bundle.
        if (HasExplicitEpisodeRange(title))
            return null;
        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC)).Trim();
        var parenthesized = Regex.Match(normalized, @"[（(]\s*([0-9]+)\s*[）)]\s*$");
        if (parenthesized.Success
            && int.TryParse(parenthesized.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parenthesizedValue)
            && parenthesizedValue > 0)
        {
            // A four-digit calendar year is work identity/date evidence, never an episode number.
            // Keep the title intact and do not fan out into GetEpisodes for e.g. 「作品（2010）」.
            return IsLikelyCalendarYear(parenthesizedValue) ? null : parenthesizedValue;
        }

        var match = Regex.Match(normalized, @"(?:第\s*([0-9]+)\s*(?:話|回)|[#＃]\s*([0-9]+)|(?:episode|ep\.?)\s*([0-9]+))", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;
        foreach (Group group in match.Groups.Cast<Group>().Skip(1))
            if (group.Success && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0)
                return value;
        return null;
    }

    internal static AIrhythmExternalEvidenceNeedSummary SummarizeExternalEvidenceNeeds(IReadOnlyList<TvAirProgramEventDto> events)
    {
        var reasons = new Dictionary<AIrhythmExternalEvidenceNeedReason, int>();
        var needed = 0;
        foreach (var item in events)
        {
            var decision = EvaluateExternalEvidenceNeed(item.Title);
            if (!decision.Needed)
                continue;
            needed++;
            reasons.TryGetValue(decision.Reason, out var count);
            reasons[decision.Reason] = count + 1;
        }
        return new(events.Count, needed, reasons);
    }

    private static string BuildRhythmSearch(
        RuntimeUiRenderContext context,
        AIrhythmRuntimeSnapshot snapshot,
        IReadOnlyList<AIrhythmRecommendation> recommendations,
        string? rawQuery,
        AIrhythmEvidenceIdentityContext identityContext)
    {
        var chips = new[]
        {
            "寝る前に軽く",
            "今夜30分以内",
            "知らない局から発見",
            "再放送を探す",
            "今週の新番組",
            "いつもの安心",
            "半歩冒険",
            "完全冒険",
            "ニュース以外",
            "通販なし"
        };
        var chipHtml = string.Join(string.Empty, chips.Select(x =>
            $"<a class='rhythm-chip' href='/plugin/{AIrhythmIdentity.Route}?rhythm={Uri.EscapeDataString(x)}#rhythmSearch'>{AIrhythmHtml.Encode(x)}</a>"));

        var normalized = NormalizeRhythmQuery(rawQuery, out var validationError);
        if (string.IsNullOrWhiteSpace(validationError) && !string.IsNullOrWhiteSpace(normalized))
            AIrhythmDataState.RememberRhythmSearch(normalized);
        var recent = AIrhythmDataState.GetRecentRhythmSearches();
        var recentHtml = recent.Count == 0
            ? string.Empty
            : $"<div class='rhythm-recent'><span>最近の探し方</span><div class='rhythm-chips'>{string.Join(string.Empty, recent.Select(x => $"<a class='rhythm-chip rhythm-chip-recent' href='/plugin/{AIrhythmIdentity.Route}?rhythm={Uri.EscapeDataString(x)}#rhythmSearch'>{AIrhythmHtml.Encode(x)}</a>"))}</div></div>";
        var interests = AIrhythmDataState.GetInterestSignals();
        string RemoveInterestButton(AIrhythmInterestSignal signal)
        {
            var attributes = context.BuildPluginActionAttributes(
                new Dictionary<string, string?>
                {
                    ["operation"] = "removeInterest",
                    ["eventId"] = signal.EventId
                },
                new PluginActionFeedbackOptions
                {
                    PendingLabel = "解除中",
                    SuccessLabel = "解除しました",
                    FailureLabel = "解除",
                    DisableWhileRunning = true,
                    RestoreOnFailure = true
                },
                responseMode: "hostHandled");
            return $"<button type='button' class='interest-remove' aria-label='気になる選択から解除' {attributes}>解除</button>";
        }
        var interestHtml = interests.Count == 0
            ? string.Empty
            : $"<div class='interest-manager'><span>気になる選択</span><div class='interest-items'>{string.Join(string.Empty, interests.Take(8).Select(x => $"<span class='interest-item'>{ProgramTitleElement("b", x.SeriesKey, scrollThreshold: 12)}<small>{AIrhythmHtml.Encode(x.ServiceName)}</small>{RemoveInterestButton(x)}</span>"))}</div></div>";
        var form = $"<form class='rhythm-form' method='get' action='/plugin/{AIrhythmIdentity.Route}#rhythmSearch'><label for='rhythmInput'>気分・時間・目的を言葉で入力</label><div class='rhythm-row'><input id='rhythmInput' name='rhythm' maxlength='160' autocomplete='off' value='{AIrhythmHtml.Encode(normalized)}' placeholder='例：今夜30分以内で笑える番組'><button type='submit'>探す</button></div></form>";

        if (!string.IsNullOrWhiteSpace(validationError))
            return $"{form}<div class='rhythm-chips'>{chipHtml}</div>{recentHtml}{interestHtml}<div class='rhythm-alert'>{AIrhythmHtml.Encode(validationError)}</div>";
        if (string.IsNullOrWhiteSpace(normalized))
            return $"{form}<div class='rhythm-chips'>{chipHtml}</div>{recentHtml}{interestHtml}<div class='rhythm-guide'>番組名が決まっていなくても、今の気分や空き時間から探せます。</div>";

        var now = DateTimeOffset.Now;
        var familiarServices = snapshot.History.Select(ServiceIdentityOf)
            .Concat(snapshot.Reservations.Select(ServiceIdentityOf))
            .Where(x => x.IsValid)
            .GroupBy(x => x)
            .OrderByDescending(x => x.Count())
            .Take(8)
            .Select(x => x.Key)
            .ToHashSet();

        var wantsTonight = ContainsAny(normalized, "今夜", "今日", "これから", "寝る前");
        var wantsWeek = ContainsAny(normalized, "今週", "週末");
        var wantsShort = ContainsAny(normalized, "30分", "三十分", "短時間", "軽く", "寝る前");
        var wantsNew = ContainsAny(normalized, "新番組", "初回", "第1話", "第１話", "新しい");
        var wantsReplay = ContainsAny(normalized, "再放送", "見逃し", "取り直し", "アンコール", "リピート");
        var wantsSafe = ContainsAny(normalized, "いつもの安心", "いつもの局", "慣れた局");
        var wantsFullAdventure = ContainsAny(normalized, "完全冒険", "知らない局", "普段見ない局");
        var wantsHalfAdventure = !wantsFullAdventure && ContainsAny(normalized, "半歩冒険", "意外", "冒険");
        var wantsLaugh = ContainsAny(normalized, "笑", "楽しい", "バラエティ", "コメディ");
        var wantsCalm = ContainsAny(normalized, "落ち着", "ゆったり", "癒", "自然", "紀行");
        var excludesNews = ContainsAny(normalized, "ニュース以外", "ニュースなし", "ニュース除外");
        var excludesShopping = ContainsAny(normalized, "通販なし", "通販以外", "通販除外");
        var excludesSports = ContainsAny(normalized, "スポーツ以外", "スポーツなし", "スポーツ除外");
        var excludesAnime = ContainsAny(normalized, "アニメ以外", "アニメなし", "アニメ除外");
        var excludesMovie = ContainsAny(normalized, "映画以外", "映画なし", "映画除外");

        var reserved = snapshot.Reservations.Select(x => CanonicalWorkKey(x, identityContext)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recorded = snapshot.History.Select(x => CanonicalWorkKey(x, identityContext)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var intentWords = Words(normalized)
            .Where(x => !ContainsAny(x,
                "今夜", "今日", "これから", "寝る前", "今週", "週末", "30分", "三十分", "短時間", "軽く",
                "新番組", "初回", "第1話", "第１話", "新しい", "再放送", "見逃し", "取り直し", "アンコール", "リピート",
                "知らない局", "普段見ない局", "いつもの安心", "いつもの局", "慣れた局", "半歩冒険", "完全冒険", "意外", "冒険",
                "ニュース以外", "ニュースなし", "ニュース除外", "通販なし", "通販以外", "通販除外",
                "スポーツ以外", "スポーツなし", "スポーツ除外", "アニメ以外", "アニメなし", "アニメ除外",
                "映画以外", "映画なし", "映画除外", "笑", "楽しい", "落ち着", "ゆったり", "癒"))
            .Take(6)
            .ToArray();

        var candidates = new List<(AIrhythmRecommendation Item, int Score, string Reason)>();
        foreach (var item in recommendations)
        {
            var ev = item.EventIdentity is null ? null : snapshot.Events.FirstOrDefault(x => EventIdentityOf(x) == item.EventIdentity);
            var end = ev?.End ?? item.Start.AddHours(1);
            var minutes = Math.Max(1, (end - item.Start).TotalMinutes);
            var text = $"{item.Title} {item.ServiceName} {item.Genre} {ev?.Summary} {ev?.Detail}";
            var score = item.RawScore;
            var reasons = new List<string>();

            if (excludesNews && ContainsAny(text, "ニュース", "報道", "news")) continue;
            if (excludesShopping && ContainsAny(text, "通販", "ショッピング", "商品紹介", "テレビショッピング")) continue;
            if (excludesSports && ContainsAny(text, "スポーツ", "野球", "サッカー", "ゴルフ", "競馬", "formula 1", "f1")) continue;
            if (excludesAnime && ContainsAny(text, "アニメ", "animation")) continue;
            if (excludesMovie && ContainsAny(text, "映画", "シネマ", "movie")) continue;

            if (wantsTonight)
            {
                var tonightEnd = now.Date.AddDays(now.Hour < 4 ? 0 : 1).AddHours(4);
                if (item.Start < now || item.Start > tonightEnd) continue;
                score += 12; reasons.Add("今夜に放送");
            }
            if (wantsWeek)
            {
                if (item.Start < now || item.Start > now.AddDays(7)) continue;
                score += 8; reasons.Add("7日以内");
            }
            if (wantsShort)
            {
                if (minutes > 40) continue;
                score += 12; reasons.Add($"約{minutes:0}分");
            }
            if (wantsNew)
            {
                if (!ContainsAny(text, "[新]", "【新】", "新番組", "初回", "第1話", "第１話", "新シリーズ")) continue;
                score += 16; reasons.Add("新しい入口");
            }
            if (wantsReplay)
            {
                if (!ContainsAny(text, "[再]", "【再】", "再放送", "アンコール", "リピート", "一挙")) continue;
                if (reserved.Contains(item.SeriesKey)) continue;
                score += recorded.Contains(item.SeriesKey) ? 14 : 8;
                reasons.Add(recorded.Contains(item.SeriesKey) ? "録画作品の再放送" : "再放送候補");
            }
            if (wantsSafe)
            {
                if (item.EventIdentity is null || !familiarServices.Contains(ServiceIdentityOf(item.EventIdentity))) continue;
                score += 14; reasons.Add("いつもの局から安心して選択");
            }
            else if (wantsFullAdventure)
            {
                if (item.EventIdentity is not null && familiarServices.Contains(ServiceIdentityOf(item.EventIdentity))) continue;
                score += 18; reasons.Add("未知の局から完全冒険");
            }
            else if (wantsHalfAdventure)
            {
                if (item.EventIdentity is not null && familiarServices.Contains(ServiceIdentityOf(item.EventIdentity)))
                {
                    score += 3; reasons.Add("慣れた傾向も残す");
                }
                else
                {
                    score += 12; reasons.Add("普段見ない局へ半歩冒険");
                }
            }
            if (wantsLaugh)
            {
                if (!ContainsAny(text, "バラエティ", "コメディ", "お笑い", "トーク", "笑")) continue;
                score += 10; reasons.Add("笑えそう");
            }
            if (wantsCalm)
            {
                if (!ContainsAny(text, "紀行", "自然", "音楽", "旅", "風景", "癒", "ドキュメンタリー")) continue;
                score += 10; reasons.Add("落ち着いて楽しめそう");
            }
            if (intentWords.Length > 0)
            {
                var matched = intentWords.Where(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matched.Length == 0) continue;
                score += Math.Min(18, matched.Length * 6);
                reasons.Add($"「{string.Join("・", matched)}」に一致");
            }
            if (reasons.Count == 0)
                reasons.Add("おすすめ傾向と一致");
            candidates.Add((item, Math.Clamp(score, 0, 100), string.Join("、", reasons)));
        }

        var selected = new List<(AIrhythmRecommendation Item, int Score, string Reason)>();
        var seriesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serviceCounts = new Dictionary<AIrhythmServiceIdentity, int>();
        foreach (var candidate in candidates.OrderByDescending(x => x.Score).ThenBy(x => x.Item.Start))
        {
            var series = candidate.Item.SeriesKey;
            if (series.Length > 0 && !seriesSeen.Add(series)) continue;
            var serviceIdentity = candidate.Item.EventIdentity is null ? default : ServiceIdentityOf(candidate.Item.EventIdentity);
            var count = serviceIdentity.IsValid && serviceCounts.TryGetValue(serviceIdentity, out var current) ? current : 0;
            if (count >= 2) continue;
            if (serviceIdentity.IsValid) serviceCounts[serviceIdentity] = count + 1;
            selected.Add(candidate);
            if (selected.Count >= 12) break;
        }

        string resultHtml;
        if (selected.Count == 0)
        {
            resultHtml = "<div class='rhythm-empty'>条件に合う候補がありません。言葉を少し減らすと見つかりやすくなります。</div>";
        }
        else
        {
            var resultBuilder = new StringBuilder("<div class='rhythm-results'>");
            for (var i = 0; i < selected.Count; i++)
            {
                var candidate = selected[i];
                var selectedEvent = candidate.Item.EventIdentity is null
                    ? null
                    : snapshot.Events.FirstOrDefault(x => EventIdentityOf(x) == candidate.Item.EventIdentity);
                var pickLink = string.Empty;
                if (selectedEvent is not null && !string.IsNullOrWhiteSpace(selectedEvent.EventId))
                {
                    var selectedSeries = CanonicalWorkKey(selectedEvent, identityContext);
                    var alreadyInterested = interests.Any(x =>
                        string.Equals(x.SeriesKey, selectedSeries, StringComparison.OrdinalIgnoreCase) &&
                        ServiceIdentityOf(x) == ServiceIdentityOf(selectedEvent));
                    if (alreadyInterested)
                    {
                        pickLink = "<span class='interest-button interest-button-selected' aria-label='気になるへ追加済み'>追加済み</span>";
                    }
                    else
                    {
                        var interestAttributes = context.BuildPluginActionAttributes(
                            new Dictionary<string, string?>
                            {
                                ["operation"] = "addInterest",
                                ["eventId"] = selectedEvent.EventId
                            },
                            new PluginActionFeedbackOptions
                            {
                                PendingLabel = "追加中",
                                SuccessLabel = "追加済み",
                                FailureLabel = "気になる",
                                DisableWhileRunning = true,
                                RestoreOnFailure = true
                            },
                            responseMode: "hostHandled");
                        pickLink = $"<button type='button' class='interest-button' {interestAttributes}>気になる</button>";
                    }
                }
                resultBuilder.Append($"<article class='rhythm-result'><span class='rhythm-rank'>{i + 1}</span><div>{ProgramTitleElement("strong", candidate.Item.Title)}<p>{AIrhythmHtml.Encode(candidate.Item.ServiceName)}・{candidate.Item.Start:MM/dd HH:mm}</p><small>{AIrhythmHtml.Encode(candidate.Reason)}</small><div class='result-actions'>{pickLink}{ReserveButton(context, candidate.Item, snapshot, true)}</div></div><b>{candidate.Score}</b></article>");
            }
            resultBuilder.Append("</div>");
            resultHtml = resultBuilder.ToString();
        }
        return $"{form}<div class='rhythm-chips'>{chipHtml}</div>{recentHtml}{interestHtml}<div class='rhythm-current'>検索中：<strong>{AIrhythmHtml.Encode(normalized)}</strong></div>{resultHtml}";
    }

    private static string NormalizeRhythmQuery(string? raw, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string value;
        try { value = raw.Normalize(NormalizationForm.FormKC).Trim(); }
        catch { error = "検索文を読み取れませんでした。"; return string.Empty; }
        if (value.Length > 160) { error = "検索文は160文字以内にしてください。"; return string.Empty; }
        if (value.Any(ch =>
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            return char.IsControl(ch)
                || category is UnicodeCategory.Format
                    or UnicodeCategory.Surrogate
                    or UnicodeCategory.PrivateUse
                    or UnicodeCategory.OtherNotAssigned;
        }))
        {
            error = "制御文字や不可視文字を含む検索文は使用できません。";
            return string.Empty;
        }
        if (value.Contains('<') || value.Contains('>')) { error = "HTMLタグを含む検索文は使用できません。"; return string.Empty; }
        if (ContainsAny(value, "javascript:", "vbscript:", "data:", "srcdoc=", "<script", "onload=", "onclick=", "onerror=", "onmouseover="))
        {
            error = "実行可能なコード形式を含む検索文は使用できません。";
            return string.Empty;
        }
        return value;
    }

    private static string DisplayDiscoveryReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "今の時間に合う候補";
        return value.Trim().ToLowerInvariant() switch
        {
            "available_now" => "いま見られます",
            "recording_within_time" => "今の時間に合う録画番組",
            "live_within_time" => "今の時間に合う放送中番組",
            "resume_within_time" => "今の時間に合う続き番組",
            _ when Regex.IsMatch(value, @"^[a-z0-9_.:-]+$", RegexOptions.IgnoreCase) => "今の時間に合う候補",
            _ => value
        };
    }

    private static string BuildCharts(AIrhythmRuntimeSnapshot snapshot, AIrhythmEvidenceIdentityContext identityContext)
    {
        var palette = new[] { "var(--viz-blue)", "var(--viz-green)", "var(--viz-yellow)", "var(--viz-orange)", "var(--viz-purple)", "var(--viz-cyan)", "var(--viz-amber)", "var(--viz-teal)" };

        static string EmptyChart(string message)
            => $"<div class='chart-empty'>{AIrhythmHtml.Encode(message)}</div>";

        static string Donut(IEnumerable<(string Label, int Value)> source, string[] colors, string unit, string empty, string extraClass)
        {
            var values = source.Where(x => x.Value > 0).ToArray();
            if (values.Length == 0) return EmptyChart(empty);
            var total = Math.Max(1, values.Sum(x => x.Value));
            var cursor = 0d;
            var stops = new List<string>();
            for (var i = 0; i < values.Length; i++)
            {
                var start = cursor;
                cursor += values[i].Value * 100d / total;
                stops.Add($"{colors[i % colors.Length]} {start:0.##}% {cursor:0.##}%");
            }
            var legend = string.Join(string.Empty, values.Select((x, i) =>
            {
                var pct = x.Value * 100d / total;
                return $"<span><i style='background:{colors[i % colors.Length]}'></i><span class='legend-label' title='{AIrhythmHtml.Encode(x.Label)}'>{AIrhythmHtml.Encode(x.Label)}</span><b>{x.Value} <small>{pct:0.#}%</small></b></span>";
            }));
            return $"<div class='donut-wrap {extraClass}'><div class='donut' style='background:conic-gradient({string.Join(',', stops)})'><em>{total}</em><small>{AIrhythmHtml.Encode(unit)}</small></div><div class='legend'>{legend}</div></div>";
        }


        static string WeekdayLine(IEnumerable<(string Label, int Value)> source, string empty)
        {
            var values = source.ToArray();
            if (values.Length == 0 || values.All(x => x.Value <= 0)) return EmptyChart(empty);
            var max = Math.Max(1, values.Max(x => x.Value));
            const double width = 640d;
            const double height = 230d;
            const double left = 38d;
            const double right = 18d;
            const double top = 20d;
            const double bottom = 42d;
            var plotWidth = width - left - right;
            var plotHeight = height - top - bottom;
            var points = values.Select((x, i) =>
            {
                var px = values.Length <= 1 ? left + plotWidth / 2d : left + plotWidth * i / (values.Length - 1d);
                var py = top + plotHeight * (1d - x.Value / (double)max);
                return (x.Label, x.Value, X: px, Y: py);
            }).ToArray();
            var polyline = string.Join(" ", points.Select(x => $"{x.X:0.#},{x.Y:0.#}"));
            var grid = new StringBuilder();
            for (var step = 0; step <= 4; step++)
            {
                var y = top + plotHeight * step / 4d;
                var v = (int)Math.Round(max * (1d - step / 4d));
                grid.Append($"<line class='grid' x1='{left:0.#}' y1='{y:0.#}' x2='{width-right:0.#}' y2='{y:0.#}'/><text class='y-label' x='{left-8:0.#}' y='{y+3:0.#}' text-anchor='end'>{v}</text>");
            }
            var marks = string.Join(string.Empty, points.Select(x =>
                $"<circle cx='{x.X:0.#}' cy='{x.Y:0.#}' r='5'/><text class='point-value' x='{x.X:0.#}' y='{x.Y-11:0.#}' text-anchor='middle'>{x.Value}本</text><text class='x-label' x='{x.X:0.#}' y='{height-14:0.#}' text-anchor='middle'>{AIrhythmHtml.Encode(x.Label)}</text>"));
            return $"<div class='weekday-line-wrap'><svg class='line-chart weekday-line-chart' viewBox='0 0 {width:0} {height:0}' role='img' aria-label='曜日別録画本数'><line class='axis' x1='{left:0.#}' y1='{top+plotHeight:0.#}' x2='{width-right:0.#}' y2='{top+plotHeight:0.#}'/>{grid}<polyline class='weekday-line' points='{polyline}'/>{marks}</svg></div>";
        }

        static string VerticalBars(IEnumerable<(string Label, int Value)> source, string[] colors, string empty, int total, int limit = 10)
        {
            var rows = source.Where(x => x.Value > 0).Take(Math.Max(1, limit)).ToArray();
            if (rows.Length == 0) return EmptyChart(empty);
            var max = Math.Max(1, rows.Max(x => x.Value));
            var html = string.Join(string.Empty, rows.Select((x, i) =>
            {
                var pct = total <= 0 ? 0d : x.Value * 100d / total;
                var heightPct = Math.Max(5, x.Value * 100 / max);
                return $"<div class='vbar-item'><strong>{x.Value}本<small>{pct:0.#}%</small></strong><div class='vbar-track'><i style='height:{heightPct}%;background:{colors[i % colors.Length]}'></i></div><span title='{AIrhythmHtml.Encode(x.Label)}'>{AIrhythmHtml.Encode(x.Label)}</span></div>";
            }));
            return $"<div class='vertical-bars genre-vertical-bars' style='--bar-count:{rows.Length}'>{html}</div>";
        }

        static string RankedBars(
            IEnumerable<(string Label, int Value)> source,
            string[] colors,
            string empty,
            bool programTitles,
            bool showShare,
            int total,
            int limit = 10)
        {
            var rows = source.Where(x => x.Value > 0).Take(Math.Max(1, limit)).ToArray();
            if (rows.Length == 0) return EmptyChart(empty);
            var max = Math.Max(1, rows.Max(x => x.Value));
            var html = string.Join(string.Empty, rows.Select((x, i) =>
            {
                var label = programTitles
                    ? ProgramTitleElement("span", x.Label, "ranking-label", 10)
                    : $"<span class='ranking-label' title='{AIrhythmHtml.Encode(x.Label)}'>{AIrhythmHtml.Encode(x.Label)}</span>";
                var share = showShare && total > 0
                    ? $"<small>{x.Value * 100d / total:0.#}%</small>"
                    : string.Empty;
                return $"<div class='ranking-row'><span class='ranking-rank'>{i + 1}</span>{label}<div class='ranking-track'><i style='width:{Math.Max(4, x.Value * 100 / max)}%;background:{colors[i % colors.Length]}'></i></div><strong>{x.Value}本{share}</strong></div>";
            }));
            return $"<div class='ranking-chart'>{html}</div>";
        }

        static string GenreWorkCard(
            AIrhythmCanonicalRecordingGenreAggregate genre,
            int genreRank,
            int allRecordingCount,
            string[] colors)
        {
            var topWorks = genre.Works.Take(10).Select(x => (Label: x.DisplayTitle, Value: x.RecordingCount));
            var body = RankedBars(topWorks, colors, "このジャンルの録画情報がまだありません", true, false, genre.RecordingCount, 10);
            var share = allRecordingCount <= 0 ? 0d : genre.RecordingCount * 100d / allRecordingCount;
            return $"<article class='chart-card dashboard-span-2 genre-detail-card chart-accent-purple'><div class='chart-head genre-detail-head'><div><span class='genre-rank'>ジャンル {genreRank}位</span><h3>{AIrhythmHtml.Encode(genre.GenreKey)}</h3></div><span>{genre.RecordingCount}本・{share:0.#}%</span></div>{body}</article>";
        }

        var recordingAggregates = BuildCanonicalRecordingAggregates(snapshot, identityContext);
        var recordingTotals = BuildCanonicalRecordingWorkTotals(snapshot, identityContext);
        var totalRecordingCount = snapshot.History.Count;
        var topGenres = recordingAggregates.Take(6).ToArray();
        var genreTop10 = recordingAggregates.Take(10)
            .Select(x => (Label: x.GenreKey, Value: x.RecordingCount))
            .ToArray();
        var totalTop10 = recordingTotals.Take(10)
            .Select(x => (Label: x.DisplayTitle, Value: x.RecordingCount))
            .ToArray();

        var allServices = snapshot.History
            .Select(x => (Identity: ServiceIdentityOf(x), FallbackName: x.ServiceName))
            .Where(x => x.Identity.IsValid)
            .GroupBy(x => x.Identity)
            .Select(x => (Label: ResolveCurrentServiceName(snapshot.Channels, x.Key, x.OrderByDescending(y => y.FallbackName.Length).First().FallbackName), Value: x.Count()))
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var stationTop = allServices.Take(8).ToList();
        var otherStations = allServices.Skip(8).Sum(x => x.Value);
        if (otherStations > 0) stationTop.Add(("その他", otherStations));

        var weekdayCounts = snapshot.History
            .Select(x => (x.ActualStart ?? x.Start).DayOfWeek)
            .GroupBy(x => x)
            .ToDictionary(x => x.Key, x => x.Count());
        var weekdays = new[]
        {
            (Label: "月", Day: DayOfWeek.Monday),
            (Label: "火", Day: DayOfWeek.Tuesday),
            (Label: "水", Day: DayOfWeek.Wednesday),
            (Label: "木", Day: DayOfWeek.Thursday),
            (Label: "金", Day: DayOfWeek.Friday),
            (Label: "土", Day: DayOfWeek.Saturday),
            (Label: "日", Day: DayOfWeek.Sunday)
        }.Select(x => (x.Label, Value: weekdayCounts.TryGetValue(x.Day, out var count) ? count : 0)).ToArray();

        AIrhythmDataState.WriteDeveloperLog(
            $"CANONICAL_RECORDING_DASHBOARD_SUMMARY total={totalRecordingCount} totalTop10=[{string.Join(",", recordingTotals.Take(10).Select(x => $"{CompactIdentity(x.DisplayTitle)}:{x.RecordingCount}"))}] genreTop10=[{string.Join(",", recordingAggregates.Take(10).Select(x => $"{x.GenreKey}:{x.RecordingCount}"))}] expandedGenres=[{string.Join(",", topGenres.Select(x => x.GenreKey))}] titleIdentity=common_canonical_work recordingCount=persistent_success_recording_facts layout=station_donut_weekday_line,total_horizontal_genre_vertical,genre1_6x10");

        var html = new StringBuilder();
        // Row 1: two large distribution cards. Do not dilute these with a third time-of-day card.
        html.Append($"<article class='chart-card dashboard-span-3 distribution-card chart-accent-orange'><div class='chart-head'><h3>よく録画する放送局</h3><span>分析できる録画 {totalRecordingCount}本</span></div>{Donut(stationTop, palette, "本", "放送局別の録画情報がまだありません", "large-donut")}</article>");
        html.Append($"<article class='chart-card dashboard-span-3 distribution-card chart-accent-cyan'><div class='chart-head'><h3>録画する曜日</h3><span>録画実績</span></div>{WeekdayLine(weekdays, "曜日別の録画情報がまだありません")}</article>");

        // Row 2: the factual all-title ranking is intentionally the left/main card; genre composition is beside it.
        html.Append($"<article class='chart-card dashboard-span-3 total-ranking-card chart-accent-blue'><div class='chart-head'><h3>総合録画 Top10</h3><span>録画本数</span></div>{RankedBars(totalTop10, palette, "録画した番組がまだありません", true, false, totalRecordingCount, 10)}</article>");
        html.Append($"<article class='chart-card dashboard-span-3 genre-ranking-card chart-accent-green'><div class='chart-head'><h3>よく録画するジャンル Top10</h3><span>本数・割合</span></div>{VerticalBars(genreTop10, palette.Reverse().ToArray(), "ジャンル別の録画情報がまだありません", totalRecordingCount, 10)}</article>");

        // Rows 3-4: top six genres, three equal cards per row, each with factual title Top10.
        for (var i = 0; i < topGenres.Length; i++)
            html.Append(GenreWorkCard(topGenres[i], i + 1, totalRecordingCount, palette));

        return html.ToString();
    }

    private static string SeriesDisplayTitle(string? value)
    {
        var programIdentity = ParseTitleEvidence(value).ProgramIdentity;
        if (string.IsNullOrWhiteSpace(programIdentity)) return string.Empty;
        return Regex.Replace(programIdentity, @"\s+", " ").Trim(' ', '-', '－', ':', '：');
    }

    private static string RenderCard(RuntimeUiRenderContext context, AIrhythmRecommendation item, AIrhythmRuntimeSnapshot snapshot)
    {
        var tags = new[] { item.BroadcastType, item.Genre }.Concat(item.Reasons).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(4);
        return $"<article class=\"card\"><div class=\"card-top\"><div>{ProgramTitleElement("strong", item.Title)}<div class=\"meta\">{AIrhythmHtml.Encode(item.ServiceName)}・{item.Start:MM/dd HH:mm}</div></div><span class=\"score\">{item.Score}</span></div><div class=\"reason\">{AIrhythmHtml.Encode(item.Reasons.Count > 0 ? string.Join("、", item.Reasons) : "番組情報から選定")}</div><div class=\"tags\">{string.Join(string.Empty, tags.Select(x => $"<span class=\"tag\">{AIrhythmHtml.Encode(x)}</span>"))}</div><div class=\"card-actions\">{ReserveButton(context, item, snapshot, false)}</div></article>";
    }

    private static string ReserveButton(RuntimeUiRenderContext context, AIrhythmRecommendation item, AIrhythmRuntimeSnapshot snapshot, bool compact)
    {
        var identity = item.EventIdentity;
        if (identity is null)
            return string.Empty;

        var now = DateTimeOffset.Now;
        var program = snapshot.Events.FirstOrDefault(x =>
            x.NetworkId == identity.NetworkId &&
            x.TransportStreamId == identity.TransportStreamId &&
            x.ServiceId == identity.ServiceId &&
            x.EventNumber == identity.EventNumber);
        if (program is null || program.End <= now)
            return string.Empty;

        var reservation = snapshot.ReservationRecords.FirstOrDefault(x =>
            x.NetworkId == identity.NetworkId &&
            x.TransportStreamId == identity.TransportStreamId &&
            x.ServiceId == identity.ServiceId &&
            x.EventNumber == identity.EventNumber &&
            !ContainsAny($"{x.Status} {x.Source} {x.Route}", "cancel", "removed", "取消", "削除"));
        var css = compact ? "reserve-button reserve-button-compact" : "reserve-button";
        var reservationKey = $"{identity.NetworkId}:{identity.TransportStreamId}:{identity.ServiceId}:{identity.EventNumber}";
        if (reservation is not null)
        {
            var state = $"{reservation.Status} {reservation.Source} {reservation.Route}";
            if (ContainsAny(state, "recording", "録画中"))
                return $"<span class=\"{css} secondary\" data-airhythm-reservation-key=\"{reservationKey}\" aria-label=\"録画中\">録画中</span>";

            var cancelAttributes = context.BuildPluginActionAttributes(
                new Dictionary<string, string?>
                {
                    ["operation"] = "cancelReservation",
                    ["reservationId"] = reservation.ReservationId,
                    ["refreshAfter"] = "true",
                    ["preserveScroll"] = "true"
                },
                new PluginActionFeedbackOptions
                {
                    PendingLabel = "取消中",
                    SuccessLabel = "予約する",
                    FailureLabel = "予約取消",
                    DisableWhileRunning = true,
                    RestoreOnFailure = true,
                    KeepUntilRefresh = true
                },
                eventName: "click",
                responseMode: "hostHandled",
                repeatPolicy: "suppressBurst",
                burstWindowMs: 800);

            var enable = !reservation.IsEnabled || ContainsAny(state, "disabled", "無効");
            var toggleLabel = enable ? "有効" : "無効";
            var toggleAttributes = context.BuildPluginActionAttributes(
                new Dictionary<string, string?>
                {
                    ["operation"] = "setReservationEnabled",
                    ["reservationId"] = reservation.ReservationId,
                    ["enabled"] = enable ? "true" : "false",
                    ["refreshAfter"] = "true",
                    ["preserveScroll"] = "true"
                },
                new PluginActionFeedbackOptions
                {
                    PendingLabel = enable ? "有効化中" : "無効化中",
                    SuccessLabel = enable ? "無効" : "有効",
                    FailureLabel = toggleLabel,
                    DisableWhileRunning = true,
                    RestoreOnFailure = true,
                    KeepUntilRefresh = true
                },
                eventName: "click",
                responseMode: "hostHandled",
                repeatPolicy: "suppressBurst",
                burstWindowMs: 800);

            var statusLabel = reservation.HasConflict && reservation.IsEnabled ? "予約済み（競合）" : "予約済み";
            return $"<span class=\"reservation-status\">{statusLabel}</span><button type=\"button\" class=\"{css} secondary\" aria-label=\"予約取消\" {cancelAttributes}>予約取消</button><button type=\"button\" class=\"{css} secondary\" aria-label=\"予約を{toggleLabel}にする\" {toggleAttributes}>{toggleLabel}</button>";
        }

        var attributes = context.BuildPluginActionAttributes(
            new Dictionary<string, string?>
            {
                ["operation"] = "reserve",
                ["networkId"] = identity.NetworkId.ToString(CultureInfo.InvariantCulture),
                ["transportStreamId"] = identity.TransportStreamId.ToString(CultureInfo.InvariantCulture),
                ["serviceId"] = identity.ServiceId.ToString(CultureInfo.InvariantCulture),
                ["eventId"] = identity.EventNumber.ToString(CultureInfo.InvariantCulture),
                ["startTime"] = identity.Start.ToString("O", CultureInfo.InvariantCulture)
            },
            new PluginActionFeedbackOptions
            {
                PendingLabel = "予約処理中",
                SuccessLabel = "予約済み",
                FailureLabel = "予約する",
                DisableWhileRunning = true,
                KeepDisabledOnSuccess = true,
                RestoreOnFailure = true,
                KeepUntilRefresh = true
            },
            eventName: "click",
            responseMode: "hostHandled",
            repeatPolicy: "suppressBurst",
            burstWindowMs: 800);
        return $"<button type=\"button\" class=\"{css} reserve-button-feedback\" data-airhythm-reservation-key=\"{reservationKey}\" data-airhythm-program-title=\"{AIrhythmHtml.Encode(item.Title)}\" aria-label=\"予約する\" {attributes}>予約する</button>";
    }

    private static IEnumerable<string> Words(string? value)
        => (value ?? string.Empty).Split(new[] { ' ', '\t', '\r', '\n', ',', '、', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()).Where(x => x.Length > 1);

    private static IEnumerable<string> Tokens(string? value)
    {
        // 英数字が混在する語も番組名の安定した識別要素になり得る。
        // 数字だけの1文字は従来どおり Words 側で落ちるが、英数字識別子は失わない。
        var cleaned = new string((value ?? string.Empty).Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        return Words(cleaned).Where(x => x.Length >= 2);
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0) return string.Empty;
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}時間{span.Minutes:00}分" : $"{Math.Max(1, span.Minutes)}分";
    }

}

internal static class AIrhythmAdvancedDataState
{
    public static AIrhythmAdvancedSnapshot Capture(
        IReadOnlyList<TvAirRecordingSessionDto> active,
        IReadOnlyList<TvAirRecordingHistoryDto> history)
    {
        // Runtime契約では録画履歴に確定品質値が含まれる。
        // CapabilityとRuntimeは同一Plugin IDで併載されないため、
        // RecordingFiles / RecordingInspectionへ別入口から触れず、履歴を正本にする。
        var inspections = history
            .Where(x => x.QualityDataAvailable && !string.IsNullOrWhiteSpace(x.ReservationId))
            .Select(x => new TvAirRecordingInspectionResultDto
            {
                ReservationId = x.ReservationId,
                State = x.ResultFinalized ? "Finalized" : "History",
                DropCount = x.DropCount,
                ErrorCount = x.ErrorCount,
                ScrambleCount = x.ScrambleCount,
                Summary = x.EndReason
            })
            .ToArray();

        return new AIrhythmAdvancedSnapshot(active, inspections);
    }
}

internal static class AIrhythmExternalLookupAdapter
{
    public const string TvMazeProviderId = "tvmaze";
    public const string JikanProviderId = "jikan";

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

    internal sealed record ExternalLookupRefreshState(
        ProviderRefreshState TvMaze,
        ProviderRefreshState Jikan);

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

    // Provider communication policy is single-sourced here. Every operation for a provider
    // shares the same gate, timing state, refresh circuit, request budget and cross-refresh cooldown.
    // External Evidence is supplemental, so keep the existing safe pacing and bound total provider
    // traffic well below the candidate-selection count instead of tuning intervals toward an API limit.
    private static readonly ProviderCommunicationState TvMazeState = new(
        TvMazeProviderId,
        TimeSpan.FromMilliseconds(1250),
        TimeSpan.FromMinutes(15),
        requestBudgetPerRefresh: 16);
    private static readonly ProviderCommunicationState JikanState = new(
        JikanProviderId,
        TimeSpan.FromMilliseconds(2000),
        TimeSpan.FromMinutes(15),
        requestBudgetPerRefresh: 1);

    internal static ExternalLookupRefreshState BeginRefresh()
        => new(TvMazeState.BeginRefresh(), JikanState.BeginRefresh());

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
    internal static TimeSpan TvMazeMinimumIntervalForDiagnostics => TvMazeState.MinimumInterval;
    internal static TimeSpan JikanMinimumIntervalForDiagnostics => JikanState.MinimumInterval;
    internal static int TvMazeRequestBudgetForDiagnostics => TvMazeState.RequestBudgetPerRefresh;
    internal static int JikanRequestBudgetForDiagnostics => JikanState.RequestBudgetPerRefresh;
    internal static TimeSpan TvMazeRateLimitCooldownForDiagnostics => TvMazeState.RateLimitCooldown;
    internal static TimeSpan JikanRateLimitCooldownForDiagnostics => JikanState.RateLimitCooldown;
#endif

    public static Task<AIrhythmExternalEvidenceResult> SearchTvMazeAsync(
        ProviderRefreshState refreshState,
        ITvAirPluginRuntimeContext context,
        TvAirExternalLookupCapabilityDto capability,
        string normalizedTitle,
        CancellationToken cancellationToken = default)
        => LookupProviderPacedAsync(refreshState, TvMazeState, context, capability, "SearchShow",
            new Dictionary<string, string> { ["query"] = NormalizeQuery(normalizedTitle, 160) }, cancellationToken);

    public static Task<AIrhythmExternalEvidenceResult> GetTvMazeEpisodesAsync(
        ProviderRefreshState refreshState,
        ITvAirPluginRuntimeContext context,
        TvAirExternalLookupCapabilityDto capability,
        string showId,
        CancellationToken cancellationToken = default)
        => LookupProviderPacedAsync(refreshState, TvMazeState, context, capability, "GetEpisodes",
            new Dictionary<string, string> { ["showId"] = NormalizePositiveInteger(showId) }, cancellationToken);

    public static Task<AIrhythmExternalEvidenceResult> GetTvMazeAliasesAsync(
        ProviderRefreshState refreshState,
        ITvAirPluginRuntimeContext context,
        TvAirExternalLookupCapabilityDto capability,
        string showId,
        CancellationToken cancellationToken = default)
        => LookupProviderPacedAsync(refreshState, TvMazeState, context, capability, "GetAliases",
            new Dictionary<string, string> { ["showId"] = NormalizePositiveInteger(showId) }, cancellationToken);

    public static async Task<AIrhythmExternalEvidenceResult> SearchJikanAnimeAsync(
        ProviderRefreshState refreshState,
        ITvAirPluginRuntimeContext context,
        TvAirExternalLookupCapabilityDto capability,
        string normalizedTitle,
        CancellationToken cancellationToken = default)
    {
        var result = await LookupProviderPacedAsync(refreshState, JikanState, context, capability, "SearchAnime",
            new Dictionary<string, string> { ["query"] = NormalizeQuery(normalizedTitle, 160) }, cancellationToken).ConfigureAwait(false);
        if (result.Code == TvAirExternalLookupResultCode.HttpError)
            JikanState.StopAndEnterCooldown(refreshState);
        return result;
    }

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
                JikanProviderId => NormalizeJikan(result, queryTitle),
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

    private static IReadOnlyList<AIrhythmExternalEvidence> NormalizeJikan(AIrhythmExternalEvidenceResult result, string queryTitle)
    {
        if (!string.Equals(result.Operation, "SearchAnime", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<AIrhythmExternalEvidence>();

        using var document = JsonDocument.Parse(result.Body);
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().ToArray()
                : root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray().ToArray()
                    : Array.Empty<JsonElement>();
        if (items.Length == 0)
            return Array.Empty<AIrhythmExternalEvidence>();

        var list = new List<AIrhythmExternalEvidence>();
        foreach (var item in items.Take(8))
        {
            var id = FirstNonBlank(ReadScalar(item, "mal_id"), ReadScalar(item, "id"));
            var title = FirstNonBlank(
                ReadString(item, "title_japanese"),
                ReadString(item, "title"),
                ReadString(item, "title_english"));
            if (id.Length == 0 || title.Length == 0)
                continue;

            var aliases = new List<string>();
            AddDistinctNonBlank(aliases, ReadString(item, "title"));
            AddDistinctNonBlank(aliases, ReadString(item, "title_english"));
            AddDistinctNonBlank(aliases, ReadString(item, "title_japanese"));
            if (item.TryGetProperty("title_synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
            {
                foreach (var synonym in synonyms.EnumerateArray().Take(20))
                    if (synonym.ValueKind == JsonValueKind.String)
                        AddDistinctNonBlank(aliases, synonym.GetString());
            }
            aliases.RemoveAll(value => string.Equals(value, title, StringComparison.OrdinalIgnoreCase));

            var mediaType = ReadString(item, "type");
            var airedFrom = string.Empty;
            if (item.TryGetProperty("aired", out var aired) && aired.ValueKind == JsonValueKind.Object)
                airedFrom = ReadString(aired, "from");

            list.Add(new AIrhythmExternalEvidence(
                JikanProviderId,
                id,
                title,
                aliases,
                mediaType.Length == 0 ? "anime" : mediaType.ToLowerInvariant(),
                null,
                null,
                ParseDate(airedFrom),
                "candidate",
                Math.Clamp(TitleSimilarity(queryTitle, title) + (aliases.Any(alias => TitleSimilarity(queryTitle, alias) >= 0.92d) ? 0.08d : 0d), 0d, 1d),
                DateTimeOffset.Now));
        }
        return list;
    }

    private static void AddDistinctNonBlank(List<string> values, string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0 || values.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            return;
        values.Add(normalized);
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

    private static string FirstNonBlank(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

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
    bool ResultFinalized);

internal static partial class AIrhythmDataState
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly object Gate = new();
    private static readonly object CaptureGate = new();
    private static ITvAirPluginRuntimeContext? _runtimeContext;
    private static readonly List<IDisposable> EventSubscriptions = new();
    private static AIrhythmRuntimeSnapshot? _cachedSnapshot;
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
    private const int JikanLookupProbeLimit = 1;
    private const int UsageRecentIdentityLimit = 1024;
    private static AIrhythmUsageTotals _usageTotals;
    private static bool _usageTotalsInitialized;
    private static readonly HashSet<string> UsageRecentReservationIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> UsageRecentReservationOrder = new();
    private static readonly HashSet<string> UsageRecentRecordingIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> UsageRecentRecordingOrder = new();
    private const string RecordingFactStorageNamespace = "recordingFacts";
    private static readonly Dictionary<string, AIrhythmRecordingFact> RecordingFacts = new(StringComparer.OrdinalIgnoreCase);
    private static bool _recordingFactsLoaded;
    private static DateTimeOffset? _dataResetCutoff;
    private static bool _dataResetCutoffLoaded;
    private const string DataLifecycleStorageNamespace = "dataLifecycle";
    private const string DataResetCutoffStorageKey = "resetCutoff";
    private const string ExternalLookupRuntimeStorageNamespace = "externalLookupRuntime";
    private const string ExternalManualRefreshDateStorageKey = "lastManualRefreshDate";
    private static DateOnly? _lastManualExternalRefreshDate;
    private static bool _lastManualExternalRefreshDateLoaded;
    private const int BackupFormatVersion = 1;

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
            _cachedSnapshot = null;
            _externalLookupCapability = null;
            _usageTotals = default;
            _usageTotalsInitialized = false;
            UsageRecentReservationIds.Clear();
            UsageRecentReservationOrder.Clear();
            UsageRecentRecordingIds.Clear();
            UsageRecentRecordingOrder.Clear();
            RecordingFacts.Clear();
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
            _cachedSnapshot = null;
            _externalLookupCapability = null;
            ExternalEvidenceCache.Clear();
            ExternalEvidenceSummaryByEvent.Clear();
            ExternalCanonicalTitleBySource.Clear();
            ExternalLookupAttempts.Clear();
            _usageTotals = default;
            _usageTotalsInitialized = false;
            UsageRecentReservationIds.Clear();
            UsageRecentReservationOrder.Clear();
            UsageRecentRecordingIds.Clear();
            UsageRecentRecordingOrder.Clear();
            RecordingFacts.Clear();
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
        lock (Gate) return _usageTotals;
    }

    private static void EnsureUsageTotalsInitialized(ITvAirPluginRuntimeContext context)
    {
        lock (Gate)
        {
            if (_usageTotalsInitialized || !ReferenceEquals(_runtimeContext, context))
                return;
        }

        var stored = ReadUsageCounterState(context);
        var resetCutoff = GetDataResetCutoff(context);
        long recordingBaseline = 0;
        long reservationBaseline = 0;
        var recentRecordingIds = new List<string>();
        var recentReservationIds = new List<string>();
        try
        {
            var recordings = context.Recordings.ListHistory(new TvAirRecordingHistoryQueryDto
            {
                IncludeSystemEntries = false,
                Limit = 10000
            });
            var recordingKeys = recordings
                .Where(x => resetCutoff is null || (x.ActualStart ?? x.Start) >= resetCutoff.Value)
                .Select(UsageRecordingIdentity)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            recordingBaseline = recordingKeys.LongLength;
            recentRecordingIds.AddRange(recordingKeys.Take(UsageRecentIdentityLimit));
        }
        catch { }

        try
        {
            var activeReservations = context.Reservations.List(new TvAirReservationQueryDto
            {
                IncludeSystemEntries = false
            });
            var reservationHistory = context.Reservations.ListHistory(new TvAirReservationHistoryQueryDto
            {
                IncludeSystemEntries = false,
                Limit = 10000
            });
            var reservationKeys = activeReservations
                .Concat(reservationHistory)
                .Where(x => resetCutoff is null || x.CreatedAt >= resetCutoff.Value)
                .Select(x => x.ReservationId?.Trim() ?? string.Empty)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            reservationBaseline = reservationKeys.LongLength;
            recentReservationIds.AddRange(reservationKeys.Take(UsageRecentIdentityLimit));
        }
        catch { }

        var next = new AIrhythmUsageTotals(
            Math.Max(stored?.RecordingTotal ?? 0, recordingBaseline),
            Math.Max(stored?.ReservationTotal ?? 0, reservationBaseline));

        lock (Gate)
        {
            if (!ReferenceEquals(_runtimeContext, context) || _usageTotalsInitialized)
                return;
            _usageTotals = next;
            _usageTotalsInitialized = true;
            foreach (var id in recentRecordingIds)
                RememberUsageIdentity(UsageRecentRecordingIds, UsageRecentRecordingOrder, id);
            foreach (var id in recentReservationIds)
                RememberUsageIdentity(UsageRecentReservationIds, UsageRecentReservationOrder, id);
        }
        PersistUsageCounterState(context, next);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"usage totals initialized recordingTotal={next.RecordingTotal} reservationTotal={next.ReservationTotal} recordingBaseline={recordingBaseline} reservationBaseline={reservationBaseline} policy=terminal_recording_plus_established_reservation systemEntries=False");
#endif
    }

    private static AIrhythmUsageCounterState? ReadUsageCounterState(ITvAirPluginRuntimeContext context)
    {
        try
        {
            var result = context.Storage.Get("usage", "totals");
            var json = result.Succeeded ? result.Value?.Value?.ToString() : null;
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<AIrhythmUsageCounterState>(json, JsonOptions);
        }
        catch { return null; }
    }

    private static void PersistUsageCounterState(ITvAirPluginRuntimeContext context, AIrhythmUsageTotals totals)
    {
        try
        {
            var json = JsonSerializer.Serialize(new AIrhythmUsageCounterState(totals.RecordingTotal, totals.ReservationTotal), JsonOptions);
            context.Storage.Set("usage", "totals", json, expectedRevision: null);
        }
        catch { }
    }

    private static string UsageRecordingIdentity(TvAirRecordingHistoryDto item)
    {
        if (!string.IsNullOrWhiteSpace(item.RecordingId))
            return item.RecordingId.Trim();
        if (!string.IsNullOrWhiteSpace(item.ReservationId))
            return $"reservation:{item.ReservationId.Trim()}:{item.Start.UtcDateTime.Ticks}:{item.End.UtcDateTime.Ticks}";
        return $"recording:{item.NetworkId}:{item.TransportStreamId}:{item.ServiceId}:{item.EventId}:{item.Start.UtcDateTime.Ticks}:{item.End.UtcDateTime.Ticks}";
    }

    private static AIrhythmRecordingFact ToRecordingFact(TvAirRecordingHistoryDto item)
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
            item.ResultFinalized);

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
            ResultFinalized = fact.ResultFinalized
        };

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

    private static void EnsureRecordingFactsLoaded(ITvAirPluginRuntimeContext context)
    {
        lock (Gate)
        {
            if (_recordingFactsLoaded)
                return;
        }

        var loaded = new Dictionary<string, AIrhythmRecordingFact>(StringComparer.OrdinalIgnoreCase);
        var resetCutoff = GetDataResetCutoff(context);
        try
        {
            foreach (var key in context.Storage.ListKeys(RecordingFactStorageNamespace))
            {
                var entry = context.Storage.Get(RecordingFactStorageNamespace, key);
                var json = entry.Succeeded ? entry.Value?.Value?.ToString() : null;
                if (string.IsNullOrWhiteSpace(json))
                    continue;
                var facts = JsonSerializer.Deserialize<AIrhythmRecordingFact[]>(json, JsonOptions) ?? Array.Empty<AIrhythmRecordingFact>();
                foreach (var fact in facts)
                {
                    if (resetCutoff is not null && (fact.ActualStart ?? fact.Start) < resetCutoff.Value)
                        continue;
                    if (!string.IsNullOrWhiteSpace(fact.Identity))
                        loaded[fact.Identity] = fact;
                }
            }
        }
        catch (Exception ex)
        {
            WriteDeveloperLog($"recording fact store load failed type={ex.GetType().Name} action=continue_with_host_seed");
        }

        lock (Gate)
        {
            if (_recordingFactsLoaded)
                return;
            foreach (var pair in loaded)
                RecordingFacts[pair.Key] = pair.Value;
            _recordingFactsLoaded = true;
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"recording fact store loaded facts={loaded.Count} chunks={loaded.Values.Select(RecordingFactMonthKey).Distinct(StringComparer.Ordinal).Count()} policy=append_success_terminal_no_host_deletion");
#endif
    }

    private static void PersistRecordingFactChunks(ITvAirPluginRuntimeContext context, IReadOnlyCollection<string> monthKeys)
    {
        if (monthKeys.Count == 0)
            return;

        Dictionary<string, AIrhythmRecordingFact[]> chunks;
        lock (Gate)
        {
            var wanted = new HashSet<string>(monthKeys, StringComparer.Ordinal);
            chunks = RecordingFacts.Values
                .Where(x => wanted.Contains(RecordingFactMonthKey(x)))
                .GroupBy(RecordingFactMonthKey, StringComparer.Ordinal)
                .ToDictionary(
                    x => x.Key,
                    x => x.OrderBy(y => y.ActualStart ?? y.Start).ThenBy(y => y.Identity, StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
        }

        foreach (var pair in chunks)
        {
            try
            {
                var json = JsonSerializer.Serialize(pair.Value, JsonOptions);
                context.Storage.Set(RecordingFactStorageNamespace, pair.Key, json, expectedRevision: null);
            }
            catch (Exception ex)
            {
                WriteDeveloperLog($"recording fact store persist failed chunk={pair.Key} type={ex.GetType().Name}");
            }
        }
    }

    private static int MergeUsefulRecordingFacts(ITvAirPluginRuntimeContext context, IEnumerable<TvAirRecordingHistoryDto> source)
    {
        EnsureRecordingFactsLoaded(context);
        var resetCutoff = GetDataResetCutoff(context);
        var changedMonths = new HashSet<string>(StringComparer.Ordinal);
        var changed = 0;
        lock (Gate)
        {
            foreach (var item in source)
            {
                if (!IsUsefulHistory(item))
                    continue;
                if (resetCutoff is not null && (item.ActualStart ?? item.Start) < resetCutoff.Value)
                    continue;
                var fact = ToRecordingFact(item);
                if (fact.Identity.Length == 0)
                    continue;
                if (RecordingFacts.TryGetValue(fact.Identity, out var existing) && existing == fact)
                    continue;
                if (existing is not null)
                    changedMonths.Add(RecordingFactMonthKey(existing));
                RecordingFacts[fact.Identity] = fact;
                changedMonths.Add(RecordingFactMonthKey(fact));
                changed++;
            }
        }
        PersistRecordingFactChunks(context, changedMonths);
        return changed;
    }

    private static IReadOnlyList<TvAirRecordingHistoryDto> GetRecordingFactHistory(ITvAirPluginRuntimeContext context, IEnumerable<TvAirRecordingHistoryDto> hostHistory, out int seeded)
    {
        seeded = MergeUsefulRecordingFacts(context, hostHistory);
        lock (Gate)
        {
            return RecordingFacts.Values
                .Select(FromRecordingFact)
                .OrderByDescending(x => x.ActualStart ?? x.Start)
                .ToArray();
        }
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

    private static void UpdateUsageTotalsFromRuntimeEvent(ITvAirPluginRuntimeContext context, string eventType, PluginEventEnvelope envelope)
    {
        EnsureUsageTotalsInitialized(context);
        if (string.Equals(eventType, "ReservationAdded", StringComparison.OrdinalIgnoreCase))
        {
            var entityId = envelope.EntityId?.Trim() ?? string.Empty;
            TvAirReservationDto? matchedReservation = envelope.Payload as TvAirReservationDto;
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
            catch { return; }
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
            PersistUsageCounterState(context, next);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"usage total increment kind=reservation entity={reservationIdentity} total={next.ReservationTotal} trigger=ReservationAdded");
#endif
            return;
        }

        if (!string.Equals(eventType, "RecordingResultFinalized", StringComparison.OrdinalIgnoreCase))
            return;

        var recordingEntity = envelope.EntityId?.Trim() ?? string.Empty;
        TvAirRecordingHistoryDto? matched = envelope.Payload as TvAirRecordingHistoryDto;
        try
        {
            var recent = context.Recordings.ListHistory(new TvAirRecordingHistoryQueryDto
            {
                IncludeSystemEntries = false,
                Limit = 1000
            });
            if (matched is null || !recent.Any(x => string.Equals(UsageRecordingIdentity(x), UsageRecordingIdentity(matched), StringComparison.OrdinalIgnoreCase)))
            {
                matched = recent.FirstOrDefault(x =>
                    string.Equals(x.RecordingId, recordingEntity, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.ReservationId, recordingEntity, StringComparison.OrdinalIgnoreCase)
                    || (recordingEntity.Length > 0 && !string.IsNullOrWhiteSpace(x.RecordingId) && recordingEntity.EndsWith($":{x.RecordingId}", StringComparison.OrdinalIgnoreCase))
                    || (recordingEntity.Length > 0 && !string.IsNullOrWhiteSpace(x.ReservationId) && recordingEntity.EndsWith($":{x.ReservationId}", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch { }
        if (matched is null)
            return;

        if (IsUsefulHistory(matched))
        {
            var mergedFacts = MergeUsefulRecordingFacts(context, new[] { matched });
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            if (mergedFacts > 0)
                WriteDeveloperLog($"recording fact appended trigger=RecordingResultFinalized identity={UsageRecordingIdentity(matched)} totalFacts={GetRecordingFactCount()}");
#endif
        }

        var recordingIdentity = UsageRecordingIdentity(matched);
        AIrhythmUsageTotals recordingNext;
        lock (Gate)
        {
            if (!RememberUsageIdentity(UsageRecentRecordingIds, UsageRecentRecordingOrder, recordingIdentity))
                return;
            recordingNext = _usageTotals with { RecordingTotal = _usageTotals.RecordingTotal + 1 };
            _usageTotals = recordingNext;
        }
        PersistUsageCounterState(context, recordingNext);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"usage total increment kind=recording entity={recordingIdentity} total={recordingNext.RecordingTotal} trigger=RecordingResultFinalized");
#endif
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
        // Web画面とプラグインイベントが同時に再描画を要求しても、
        // 同一の複数Source Snapshotを一度だけ取得する。
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
            IReadOnlyList<TvAirRecordingHistoryDto> history = Array.Empty<TvAirRecordingHistoryDto>();
            IReadOnlyList<TvAirRecordingHistoryDto> recoveryHistory = Array.Empty<TvAirRecordingHistoryDto>();
            IReadOnlyList<TvAirRecordingSessionDto> active = Array.Empty<TvAirRecordingSessionDto>();
            IReadOnlyList<TvAirServiceDto> channels = Array.Empty<TvAirServiceDto>();
            IReadOnlyList<TvAirTunerStatusDto> tuners = Array.Empty<TvAirTunerStatusDto>();
            var playbackProgress = new TvAirPlaybackProgressSnapshotDto();
            var mediaInsights = new TvAirMediaContextSnapshotDto();
            var contentDiscovery = new TvAirContentDiscoveryResultDto();
            var programRaw = 0;
            var reservationRaw = 0;
            var historyRaw = 0;
            var channelRaw = 0;

            var snapshotResult = ReadRuntimeSnapshot(context);
            if (!snapshotResult.Success)
            {
                errors.Add("番組情報");
                errors.Add("予約");
                errors.Add("録画実績");
                errors.Add("チャンネル");
                errors.Add("チューナー状態");
                ReportFailure(context, "dataSnapshot", snapshotResult.Error ?? new InvalidOperationException("Snapshot could not be read."));
            }
            else
            {
                var data = snapshotResult.Data!;
                programRaw = data.ProgramEvents.Count;
                reservationRaw = data.Reservations.Count;
                channelRaw = data.Channels.Count;

                var hostRecordingHistory = ReadCanonicalRecordingHistory(context, data.RecordingHistory, out var recordingHistorySource);
                var recordingFactHistory = GetRecordingFactHistory(context, hostRecordingHistory, out var seededRecordingFacts);
                historyRaw = recordingFactHistory.Count;

                events = data.ProgramEvents
                    .Where(x => !string.IsNullOrWhiteSpace(x.Title) && x.End > now && x.Start < now.AddDays(14))
                    .OrderBy(x => x.Start)
                    .ToArray();
                reservationRecords = data.Reservations
                    .Where(x => x.NetworkId > 0 && x.TransportStreamId > 0 && x.ServiceId > 0 && x.EventNumber > 0)
                    .OrderBy(x => x.Start)
                    .ToArray();
                reservations = reservationRecords
                    .Where(IsUsefulReservation)
                    .ToArray();
                recoveryHistory = hostRecordingHistory
                    .Where(x => (x.ActualStart ?? x.Start) <= now)
                    .Where(x => !string.IsNullOrWhiteSpace(x.ProgramTitle) && x.ResultFinalized)
                    .OrderByDescending(x => x.ActualStart ?? x.Start)
                    .ToArray();
                history = recordingFactHistory;
                WriteDeveloperLog($"canonical recording facts source=AIrhythm.PersistentRecordingFacts facts={history.Count} seeded={seededRecordingFacts} hostRaw={hostRecordingHistory.Count} hostSource={recordingHistorySource} snapshotRaw={data.RecordingHistory.Count} retention=append_success_terminal_no_host_deletion consumers=score,localEvidence,dashboard,selection,discovery,search,interest");
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
                playbackProgress = context.PlaybackProgress.GetSnapshot();
            }
            catch (Exception ex)
            {
                errors.Add("再生状況");
                ReportFailure(context, "playbackProgress", ex);
            }

            try
            {
                var insightsFrom = history.Count > 0
                    ? history.Min(x => x.ActualStart ?? x.Start)
                    : now.AddDays(-365);
                mediaInsights = context.MediaInsights.GetContextSnapshot(new TvAirMediaContextQueryDto
                {
                    From = insightsFrom,
                    To = now
                });
            }
            catch (Exception ex)
            {
                errors.Add("分析情報");
                ReportFailure(context, "mediaInsights", ex);
            }

            try
            {
                contentDiscovery = context.ContentDiscovery.SearchAvailable(new TvAirContentDiscoveryQueryDto
                {
                    Now = now,
                    MaximumAvailableMinutes = 30,
                    IncludeLive = true,
                    IncludeRecordings = true,
                    UnwatchedOnly = false,
                    ResumableOnly = false,
                    Limit = 30
                });
            }
            catch (Exception ex)
            {
                errors.Add("視聴候補");
                ReportFailure(context, "contentDiscovery", ex);
            }

            var advanced = AIrhythmAdvancedDataState.Capture(active, history);

            var settings = ReadSettings(context, out var revision);
            var diagnostics = new AIrhythmRuntimeDiagnostics(
                programRaw, events.Count, reservationRaw, reservations.Count, historyRaw, history.Count, channelRaw, channels.Count, errors.ToArray());

            var coreFailureCount = errors.Count(x => x is "番組情報" or "予約" or "録画実績");
            var snapshot = coreFailureCount == 3
                ? Empty("番組情報を読み込めませんでした", settings, revision, diagnostics)
                : new AIrhythmRuntimeSnapshot(
                    true,
                    errors.Count == 0 ? string.Empty : $"{string.Join("・", errors.Distinct())}を読み込めませんでした",
                    events, reservations, reservationRecords, history, recoveryHistory, channels, tuners, playbackProgress, mediaInsights, contentDiscovery, advanced, settings, revision, diagnostics);

            ReportSnapshot(context, snapshot);
            lock (Gate)
            {
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
        catch (Exception ex)
        {
            WriteDeveloperLog($"canonical recording history primary read failed type={ex.GetType().Name} fallback=snapshot");
        }

        source = "DataSnapshot.recording-history(fallback)";
        return snapshotFallback;
    }

    private static SnapshotReadResult ReadRuntimeSnapshot(ITvAirPluginRuntimeContext context)
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
                // 不正な1件だけを除外し、同じSnapshot内の残りを利用する。
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
            // 検索履歴の保存失敗は検索結果の表示を妨げない。
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

            // 旧形式でidentityを一意に解決できない項目は、推測せず保存上だけ保持する。
            // 推薦・集計・表示の正本には使わない。
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
            selected.NetworkId, selected.TransportStreamId, selected.ServiceId);
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
        catch (Exception ex)
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
        catch (Exception ex)
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
            var zeroUsage = JsonSerializer.Serialize(new AIrhythmUsageCounterState(0, 0), JsonOptions);
            var marker = JsonSerializer.Serialize(new AIrhythmDataResetMarker(resetAt), JsonOptions);

            foreach (var key in context.Storage.ListKeys(RecordingFactStorageNamespace).ToArray())
            {
                var deleted = context.Storage.Delete(RecordingFactStorageNamespace, key, expectedRevision: null);
                if (!deleted.Succeeded)
                {
                    ReplacePersistentStorage(context, previous, out _);
                    return new(false, "録画Factをリセットできませんでした");
                }
            }
            var writes = new[]
            {
                context.Storage.Set("rhythmSearch", "recent", empty, expectedRevision: null),
                context.Storage.Set("rhythmSearch", "interestSignals", empty, expectedRevision: null),
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
                _usageTotalsInitialized = true;
            }
            Invalidate("AccumulatedDataReset");
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"persistent accumulated data reset result=OK resetAt={resetAt:O} policy=start_new_epoch_settings_preserved");
#endif
            return new(true, "今この時点から新しく蓄積を開始します", Changed: true);
        }
        catch (Exception ex)
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
        CaptureFixedStorage(context, storage, "rhythmSearch", new[] { "recent", "interestSignals" });
        CaptureFixedStorage(context, storage, DataLifecycleStorageNamespace, new[] { DataResetCutoffStorageKey });
        CaptureAllStorageKeys(context, storage, RecordingFactStorageNamespace);
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
        var allowedNamespaces = new HashSet<string>(new[] { "settings", "usage", "rhythmSearch", RecordingFactStorageNamespace, DataLifecycleStorageNamespace }, StringComparer.Ordinal);
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
            foreach (var storageNamespace in new[] { "settings", "usage", "rhythmSearch", RecordingFactStorageNamespace, DataLifecycleStorageNamespace })
            {
                var desiredKeys = desired.Storage.TryGetValue(storageNamespace, out var desiredBucket)
                    ? new HashSet<string>(desiredBucket.Keys, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                IEnumerable<string> currentKeys = storageNamespace == RecordingFactStorageNamespace
                    ? context.Storage.ListKeys(storageNamespace)
                    : storageNamespace switch
                    {
                        "settings" => new[] { "main" }.Where(key => context.Storage.Exists(storageNamespace, key)),
                        "usage" => new[] { "totals" }.Where(key => context.Storage.Exists(storageNamespace, key)),
                        "rhythmSearch" => new[] { "recent", "interestSignals" }.Where(key => context.Storage.Exists(storageNamespace, key)),
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
        foreach (var storageNamespace in new[] { "settings", "usage", "rhythmSearch", RecordingFactStorageNamespace, DataLifecycleStorageNamespace })
        {
            IEnumerable<string> currentKeys = storageNamespace == RecordingFactStorageNamespace
                ? context.Storage.ListKeys(storageNamespace)
                : storageNamespace switch
                {
                    "settings" => new[] { "main" }.Where(key => context.Storage.Exists(storageNamespace, key)),
                    "usage" => new[] { "totals" }.Where(key => context.Storage.Exists(storageNamespace, key)),
                    "rhythmSearch" => new[] { "recent", "interestSignals" }.Where(key => context.Storage.Exists(storageNamespace, key)),
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
        lock (Gate)
        {
            _cachedSnapshot = null;
            _usageTotals = default;
            _usageTotalsInitialized = false;
            UsageRecentReservationIds.Clear();
            UsageRecentReservationOrder.Clear();
            UsageRecentRecordingIds.Clear();
            UsageRecentRecordingOrder.Clear();
            RecordingFacts.Clear();
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
            var recentJson = recentBefore.Succeeded ? recentBefore.Value?.Value?.ToString() : null;
            var interestsJson = interestsBefore.Succeeded ? interestsBefore.Value?.Value?.ToString() : null;
            var hasLearningInformation = HasStoredArrayItems(recentJson) || HasStoredArrayItems(interestsJson);
            if (!hasLearningInformation)
                return new(true, string.Empty, Changed: false);

            var empty = JsonSerializer.Serialize(Array.Empty<object>(), JsonOptions);
            var recent = context.Storage.Set("rhythmSearch", "recent", empty, expectedRevision: null);
            var interests = context.Storage.Set("rhythmSearch", "interestSignals", empty, expectedRevision: null);
            if (!recent.Succeeded || !interests.Succeeded)
                return new(false, recent.Error?.Message ?? interests.Error?.Message ?? "学習情報をリセットできませんでした");

            Invalidate("LearningInformationReset");
            return new(true, string.Empty, Changed: true);
        }
        catch
        {
            return new(false, "学習情報をリセットできませんでした");
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
            settings.Excluded?.Trim() ?? string.Empty,
            settings.ExternalLookupEnabled);
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
            UpdateUsageTotalsFromRuntimeEvent(context, eventType, envelope);
        if (string.Equals(eventType, "PluginPermissionChanged", StringComparison.OrdinalIgnoreCase))
            RefreshExternalLookupCapability();
        Invalidate(eventType);
    }

    private static void RefreshExternalLookupCapability()
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return;

        TvAirExternalLookupCapabilityDto? capability = null;
        try { capability = context.ExternalLookup.GetCapability(); } catch { }
        lock (Gate) _externalLookupCapability = capability;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        LogExternalLookupCapability(capability);
#endif
    }

    private static string BuildExternalProviderSearchQuery(
        string query,
        AIrhythmExternalEvidenceNeedReason reason,
        AIrhythmNumericParenthesizedClass numericClass,
        bool strongLocalSequence)
    {
        if (reason != AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix
            || numericClass != AIrhythmNumericParenthesizedClass.LocalSequence
            || !strongLocalSequence
            || string.IsNullOrWhiteSpace(query))
            return query;

        // SearchShow resolves the series/show identity; the local episode number is retained
        // separately for GetEpisodes verification. Remove only a terminal numeric parenthesis
        // already classified as a strong LocalSequence. Never strip years or arbitrary
        // parenthesized identity from other ambiguity classes.
        var stem = Regex.Replace(query, @"\s*[（(]\s*[0-9]+\s*[）)]\s*$", string.Empty).Trim();
        return string.IsNullOrWhiteSpace(stem) ? query : stem;
    }

    private static bool IsJikanAnimeCandidate(TvAirProgramEventDto item)
    {
        var genre = (item.Genre ?? string.Empty).Normalize(NormalizationForm.FormKC);
        return genre.Contains("アニメ", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildJikanProviderSearchQuery(
        TvAirProgramEventDto item,
        string fallbackSearchQuery,
        AIrhythmLeadingContainerParts containerParts,
        AIrhythmNumericParenthesizedClass numericClass,
        string numericStem)
    {
        if (!IsJikanAnimeCandidate(item) || string.IsNullOrWhiteSpace(fallbackSearchQuery))
            return fallbackSearchQuery;

        // Jikan is work-catalogue evidence. When the local title ends in a numeric episode-like
        // parenthesis, send the work stem once instead of issuing both stem and numbered-title
        // searches in the same manual refresh. Preserve likely-year parentheses as identity.
        var representativeSearchQuery = (numericClass is AIrhythmNumericParenthesizedClass.LocalSequence
                or AIrhythmNumericParenthesizedClass.Ambiguous)
            && !string.IsNullOrWhiteSpace(numericStem)
                ? numericStem
                : fallbackSearchQuery;

        static bool LooksEpisodeOnly(string value)
            => Regex.IsMatch(value.Trim(),
                @"^(?:[#＃]\s*[0-9０-９]+(?:\s*[-~〜～]\s*[0-9０-９]+)?|(?:第\s*)?[0-9０-９]+\s*(?:話|回)(?:\s*[-~〜～]\s*[0-9０-９]+\s*(?:話|回)?)?)$",
                RegexOptions.IgnoreCase);

        static string StripBroadcastWrapperSuffix(string value)
        {
            var current = Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim();
            for (var i = 0; i < 3; i++)
            {
                var next = Regex.Replace(current,
                    @"(?:\s|　)*(?:シリーズ全編再放送|全(?:話|編)(?:一挙)?(?:再)?放送|一挙(?:再)?放送|連続(?:再)?放送|まとめて(?:再)?放送|再放送)$",
                    string.Empty, RegexOptions.IgnoreCase).Trim();
                if (string.Equals(next, current, StringComparison.Ordinal))
                    break;
                current = next;
            }
            return current;
        }

        // A leading wrapper can itself contain the anime work identity while the remainder is
        // only an episode/range token (for example "#501-510"). In that structure, sending the
        // remainder to an anime catalogue loses the work identity. Recover only the structural
        // wrapper identity and remove generic broadcast-scheduling suffixes; never hard-code a title.
        if (!string.IsNullOrWhiteSpace(containerParts.Container)
            && (LooksEpisodeOnly(containerParts.WorkCandidate) || LooksEpisodeOnly(representativeSearchQuery)))
        {
            var wrapperIdentity = StripBroadcastWrapperSuffix(containerParts.Container);
            if (wrapperIdentity.Length >= 2 && !LooksEpisodeOnly(wrapperIdentity))
                return wrapperIdentity;
        }

        return representativeSearchQuery;
    }

    private static AIrhythmExternalUserIntentSignal GetExternalUserIntentSignal(
        TvAirProgramEventDto item,
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmRecommendationEngine.AIrhythmEvidenceIdentityContext identityContext)
    {
        var eventKey = AIrhythmRecommendationEngine.CanonicalWorkKey(item, identityContext);
        if (string.IsNullOrWhiteSpace(eventKey))
            return default;

        var automaticReservations = snapshot.Reservations.Count(reservation =>
            AIrhythmDataState.IsUsefulReservation(reservation)
            && reservation.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule
            && string.Equals(
                AIrhythmRecommendationEngine.CanonicalWorkKey(reservation, identityContext),
                eventKey,
                StringComparison.OrdinalIgnoreCase));
        var manualReservations = snapshot.Reservations.Count(reservation =>
            AIrhythmDataState.IsUsefulReservation(reservation)
            && reservation.Intent is not (TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule or TvAirReservationIntent.System)
            && string.Equals(
                AIrhythmRecommendationEngine.CanonicalWorkKey(reservation, identityContext),
                eventKey,
                StringComparison.OrdinalIgnoreCase));
        var recordings = snapshot.History.Count(history =>
            AIrhythmDataState.IsUsefulHistory(history)
            && string.Equals(
                AIrhythmRecommendationEngine.CanonicalWorkKey(history, identityContext),
                eventKey,
                StringComparison.OrdinalIgnoreCase));

        return new AIrhythmExternalUserIntentSignal(automaticReservations, manualReservations, recordings);
    }

    private static string BuildPrimaryExternalWorkSearchQuery(
        TvAirProgramEventDto item,
        AIrhythmNumericParenthesizedClass numericClass,
        bool strongLocalSequence,
        AIrhythmLeadingContainerParts containerParts)
    {
        var normalized = AIrhythmRecommendationEngine.BuildExternalLookupQueryTitle(item.Title);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        // Explicit #/第n話/EP markers are deterministic local episode structure, so a catalogue
        // lookup should use the work title rather than the episode-labelled broadcast title.
        // A quoted token before that episode marker is often the actual Work inside a broadcast
        // slot wrapper; a quoted token after the marker is more likely an episode subtitle and is
        // deliberately not extracted (avoids short subtitle-only lookups such as "約束").
        var episodeMarker = Regex.Match(normalized,
            @"(?:[#＃]\s*[0-9０-９]+(?:\s*[-~〜～]\s*[0-9０-９]+)?|(?:第\s*)?[0-9０-９]+\s*(?:話|回)|(?:episode|ep\.?)\s*[0-9０-９]+)",
            RegexOptions.IgnoreCase);
        var quotedWork = Regex.Match(normalized, @"[「『]([^」』]{2,80})[」』]");
        var work = quotedWork.Success && episodeMarker.Success && quotedWork.Index < episodeMarker.Index
            ? quotedWork.Groups[1].Value.Trim()
            : normalized;

        // A bracketed block immediately before a deterministic episode marker behaves like an
        // episode subtitle rather than the Work. Strip only in that positional structure.
        work = Regex.Replace(work,
            @"(?:\s|　)*【[^【】]{2,100}】(?=(?:\s|　)*(?:[#＃]\s*[0-9０-９]+|(?:第\s*)?[0-9０-９]+\s*(?:話|回)|(?:episode|ep\.?)\s*[0-9０-９]+))",
            string.Empty, RegexOptions.IgnoreCase).Trim();
        work = Regex.Replace(work,
            @"(?:\s|　)*(?:[#＃]\s*[0-9０-９]+(?:\s*[-~〜～]\s*[0-9０-９]+)?|(?:第\s*)?[0-9０-９]+\s*(?:話|回)|(?:episode|ep\.?)\s*[0-9０-９]+)(?:\s*[/／].*)?$",
            string.Empty, RegexOptions.IgnoreCase).Trim();

        // A leading broadcast container is not the work identity when a usable work candidate
        // is already structurally available. This remains generic and never names a programme.
        if (!string.IsNullOrWhiteSpace(containerParts.WorkCandidate)
            && !string.Equals(containerParts.WorkCandidate, work, StringComparison.OrdinalIgnoreCase)
            && work.StartsWith("【", StringComparison.Ordinal))
            work = containerParts.WorkCandidate.Trim();

        // Parenthesized numbers are stripped only when local same-series evidence already says
        // they behave like episode numbering. Years and unsupported numeric identity stay intact.
        // This runs after container extraction so the WorkCandidate cannot reintroduce the episode.
        if (strongLocalSequence && numericClass is AIrhythmNumericParenthesizedClass.LocalSequence or AIrhythmNumericParenthesizedClass.Ambiguous)
            work = Regex.Replace(work, @"\s*[（(]\s*[0-9０-９]+\s*[）)]\s*$", string.Empty).Trim();

        return work.Length >= 2 ? work : normalized;
    }

    private static void EnsureManualExternalRefreshDateLoaded(ITvAirPluginRuntimeContext context)
    {
        lock (Gate)
        {
            if (_lastManualExternalRefreshDateLoaded)
                return;

            try
            {
                var stored = context.Storage.Get(ExternalLookupRuntimeStorageNamespace, ExternalManualRefreshDateStorageKey);
                var json = stored.Succeeded ? stored.Value?.Value?.ToString() : null;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("localDate", out var localDateElement)
                        && DateOnly.TryParseExact(localDateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                        _lastManualExternalRefreshDate = parsedDate;
                }
            }
            catch { }

            _lastManualExternalRefreshDateLoaded = true;
        }
    }

    private static (bool AlreadyRefreshed, string LocalDate) IsManualExternalRefreshAlreadyCompletedToday(ITvAirPluginRuntimeContext context, DateTimeOffset now)
    {
        EnsureManualExternalRefreshDateLoaded(context);
        var today = DateOnly.FromDateTime(now.LocalDateTime.Date);
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (Gate)
            return (_lastManualExternalRefreshDate == today, todayText);
    }

    private static (bool Claimed, string Reason, string LocalDate) TryClaimManualExternalRefreshDay(ITvAirPluginRuntimeContext context, DateTimeOffset now)
    {
        EnsureManualExternalRefreshDateLoaded(context);
        var today = DateOnly.FromDateTime(now.LocalDateTime.Date);
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (Gate)
        {
            if (_lastManualExternalRefreshDate == today)
                return (false, "already_refreshed_today", todayText);

            try
            {
                var json = JsonSerializer.Serialize(new { localDate = todayText }, JsonOptions);
                var result = context.Storage.Set(ExternalLookupRuntimeStorageNamespace, ExternalManualRefreshDateStorageKey, json, expectedRevision: null);
                if (!result.Succeeded)
                    return (false, "daily_gate_persist_failed", todayText);
            }
            catch
            {
                return (false, "daily_gate_persist_failed", todayText);
            }

            _lastManualExternalRefreshDate = today;
            return (true, "claimed", todayText);
        }
    }

    public static async Task RefreshExternalEvidenceAsync(CancellationToken cancellationToken)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return;

        var settings = ReadSettings(context, out _);
        if (!settings.ExternalLookupEnabled)
            return;

        var capability = AIrhythmExternalLookupAdapter.GetCapability(context);
        lock (Gate) _externalLookupCapability = capability;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        LogExternalLookupCapability(capability);
#endif
        if (capability is null || !capability.PluginDeclaredPermission || !capability.UserAllowed)
            return;

        // The once-per-local-day gate belongs to external refresh work, not to local refresh.
        // If today's external refresh was already completed, exit before snapshot/probe/provider-plan
        // construction; the caller still invalidates the local snapshot and rerenders normally.
        var dailyPreflight = IsManualExternalRefreshAlreadyCompletedToday(context, DateTimeOffset.Now);
        if (dailyPreflight.AlreadyRefreshed)
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"external manual refresh daily gate result=SKIP reason=already_refreshed_today localDate={dailyPreflight.LocalDate} policy=manual_refresh_once_per_local_day stage=preflight");
#endif
            return;
        }

        var snapshot = Capture();
        if (!snapshot.Ready || snapshot.Events.Count == 0)
            return;

        PruneExternalEvidenceState(DateTimeOffset.Now);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        RunExternalEvidenceInterpretationSelfValidation();
        LogExternalEvidenceCacheRelationState("before_refresh");
#endif
var identityContext = AIrhythmRecommendationEngine.BuildEvidenceIdentityContext(snapshot);
                var numericLocalValues = AIrhythmRecommendationEngine.BuildNumericParenthesizedLocalSequenceValues(snapshot.Events);
        var numericLocalValuesByService = AIrhythmRecommendationEngine.BuildNumericParenthesizedLocalSequenceValuesByService(snapshot.Events);
        var numericLocalSequenceCounts = numericLocalValues.ToDictionary(
            pair => pair.Key, pair => pair.Value.Length, StringComparer.OrdinalIgnoreCase);
        var leadingContainerLocalContexts = BuildLeadingContainerLocalContexts(snapshot.Events);
        var representativeProbes = snapshot.Events
            .Select(item => new
            {
                Event = item,
                Need = AIrhythmRecommendationEngine.EvaluateExternalEvidenceNeed(item.Title),
                Queries = AIrhythmRecommendationEngine.BuildExternalLookupQueryTitles(item.Title),
                ContainerKey = BuildLeadingContainerProbeKey(item.Title),
                ContainerParts = AIrhythmRecommendationEngine.ParseLeadingContainerParts(item.Title),
                ContainerLocalContext = GetLeadingContainerLocalContext(item, leadingContainerLocalContexts),
                DerivedRelation = AIrhythmRecommendationEngine.ClassifyDerivedProgramRelation(item.Title),
                NumericClass = AIrhythmRecommendationEngine.ClassifyNumericParenthesizedSuffix(item.Title, numericLocalSequenceCounts),
                NumericContext = AIrhythmRecommendationEngine.GetNumericParenthesizedProbeContext(item.Title, numericLocalValues),
                StrongLocalSequence = AIrhythmRecommendationEngine.IsProbableParenthesizedEpisodeSequence(item, numericLocalValuesByService),
                NumericDiagnostic = AIrhythmRecommendationEngine.GetNumericParenthesizedDiagnosticParts(item.Title)
            })
            .Where(item => item.Need.Needed && item.Queries.Count > 0)
            .SelectMany(item => item.Queries.Select(query => new
            {
                item.Event,
                item.Need,
                item.ContainerKey,
                item.ContainerParts,
                item.ContainerLocalContext,
                item.DerivedRelation,
                item.NumericClass,
                item.NumericContext,
                item.StrongLocalSequence,
                item.NumericDiagnostic,
                Query = query,
                SearchQuery = BuildExternalProviderSearchQuery(
                    query,
                    item.Need.Reason,
                    item.NumericClass,
                    item.StrongLocalSequence),
                Priority = GetExternalLookupProbePriority(item.Event.Title, query)
                    + GetNumericParenthesizedProbePriority(item.Need.Reason, item.NumericClass, item.NumericContext)
                    + GetLeadingContainerProbePriority(item.Need.Reason, query, item.ContainerParts, item.ContainerLocalContext)
                    + (item.Need.Reason == AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker ? 60 : 0)
            }))
            .Where(item => !string.IsNullOrWhiteSpace(item.Query) && !string.IsNullOrWhiteSpace(item.SearchQuery))
            // A repeated leading 【...】 prefix is one ambiguity class, not hundreds of
            // independent network decisions. Keep one representative probe per prefix while
            // preserving the original title locally until evidence actually corroborates it.
            .GroupBy(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate
                    && !string.IsNullOrWhiteSpace(item.ContainerKey)
                ? $"container:{item.ContainerKey}"
                : $"query:{item.SearchQuery}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.Event.Start)
                .First())
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Event.Start)
            .ToArray();

        // Different ambiguity classes can still collapse to the same normalized search query.
        // Network work is query-scoped, so dedupe once more by query before applying the
        // per-refresh budget. This keeps the plan count aligned with actual provider calls.
        var uniqueQueryProbes = representativeProbes
            .GroupBy(item => item.SearchQuery, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.Event.Start)
                .First())
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Event.Start)
            .ToArray();

        // Primary provider candidates come from explicit user behaviour, not title oddities.
        // Automatic/keyword reservations and manual reservations are the strongest signal,
        // followed by actual recording history. One representative query is kept per local Work.
        // Structural ambiguity candidates are retained separately only as spare-capacity fallback.
        var primaryWorkProbes = snapshot.Events
            .Select(item =>
            {
                var need = AIrhythmRecommendationEngine.EvaluateExternalEvidenceNeed(item.Title);
                var containerKey = BuildLeadingContainerProbeKey(item.Title);
                var containerParts = AIrhythmRecommendationEngine.ParseLeadingContainerParts(item.Title);
                var containerLocalContext = GetLeadingContainerLocalContext(item, leadingContainerLocalContexts);
                var derivedRelation = AIrhythmRecommendationEngine.ClassifyDerivedProgramRelation(item.Title);
                var numericClass = AIrhythmRecommendationEngine.ClassifyNumericParenthesizedSuffix(item.Title, numericLocalSequenceCounts);
                var numericContext = AIrhythmRecommendationEngine.GetNumericParenthesizedProbeContext(item.Title, numericLocalValues);
                var strongLocalSequence = AIrhythmRecommendationEngine.IsProbableParenthesizedEpisodeSequence(item, numericLocalValuesByService);
                var numericDiagnostic = AIrhythmRecommendationEngine.GetNumericParenthesizedDiagnosticParts(item.Title);
                var signal = GetExternalUserIntentSignal(item, snapshot, identityContext);
                var searchQuery = BuildPrimaryExternalWorkSearchQuery(item, numericClass, strongLocalSequence, containerParts);
                return new
                {
                    Event = item,
                    Need = need,
                    ContainerKey = containerKey,
                    ContainerParts = containerParts,
                    ContainerLocalContext = containerLocalContext,
                    DerivedRelation = derivedRelation,
                    NumericClass = numericClass,
                    NumericContext = numericContext,
                    StrongLocalSequence = strongLocalSequence,
                    NumericDiagnostic = numericDiagnostic,
                    Query = searchQuery,
                    SearchQuery = searchQuery,
                    Priority = signal.Priority
                };
            })
            .Where(item => item.Priority > 0 && !string.IsNullOrWhiteSpace(item.SearchQuery))
            .GroupBy(item => AIrhythmRecommendationEngine.CanonicalWorkKey(item.Event, identityContext), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.SearchQuery.Length)
                .ThenBy(item => item.Event.Start)
                .First())
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.SearchQuery.Length)
            .ThenBy(item => item.Event.Start)
            .ToArray();

        // Keep a broad per-refresh batch, but spend the first numeric slots on the strongest
        // already-corroborated local sequence evidence first. This is probe selection only:
        // same-service local sequence evidence does not rewrite identity or make a verdict.
        // Ambiguous siblings remain fallback probes when no stronger LocalSequence candidate fills
        // the numeric slots. Derived/container classes retain one seed each.
        var derivedSeed = uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker)
            .Take(1)
            .ToArray();
        var containerSeed = uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate)
            .Take(1)
            .ToArray();
        var numericLocalSequencePreferred = uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix
                && item.NumericClass == AIrhythmNumericParenthesizedClass.LocalSequence
                && item.StrongLocalSequence)
            .Take(2)
            .ToArray();
        var numericAmbiguousCorroborated = uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix
                && item.NumericClass == AIrhythmNumericParenthesizedClass.Ambiguous
                && item.NumericContext.DistinctLocalValues >= 2
                && !numericLocalSequencePreferred.Any(selected => string.Equals(selected.SearchQuery, item.SearchQuery, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Max(0, 2 - numericLocalSequencePreferred.Length))
            .ToArray();
        var numericFallback = uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix
                && item.NumericClass == AIrhythmNumericParenthesizedClass.Ambiguous
                && item.NumericContext.DistinctLocalValues < 2
                && !numericLocalSequencePreferred.Any(selected => string.Equals(selected.SearchQuery, item.SearchQuery, StringComparison.OrdinalIgnoreCase))
                && !numericAmbiguousCorroborated.Any(selected => string.Equals(selected.SearchQuery, item.SearchQuery, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Max(0, 2 - numericLocalSequencePreferred.Length - numericAmbiguousCorroborated.Length))
            .ToArray();

        var seededProbes = derivedSeed
            .Concat(containerSeed)
            .Concat(numericLocalSequencePreferred)
            .Concat(numericAmbiguousCorroborated)
            .Concat(numericFallback)
            .GroupBy(item => item.SearchQuery, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(ExternalLookupProbeLimit)
            .ToArray();
        var probes = seededProbes
            .Concat(uniqueQueryProbes.Where(item => !seededProbes.Any(selected =>
                string.Equals(selected.SearchQuery, item.SearchQuery, StringComparison.OrdinalIgnoreCase))))
            .Take(ExternalLookupProbeLimit)
            .ToArray();

        // Provider queues are user-intent-first. TVmaze receives non-anime tracked Works;
        // Jikan receives anime tracked Works. Structural-ambiguity probes are appended only after
        // the primary queue, so unusual title shapes consume network capacity only when spare
        // budget remains. No programme title is hard-coded.
        var tvMazePrimaryProbes = primaryWorkProbes
            .Where(item => !IsJikanAnimeCandidate(item.Event))
            .ToArray();
        var tvMazeProbes = tvMazePrimaryProbes
            .Concat(probes.Where(item => !IsJikanAnimeCandidate(item.Event)
                && !tvMazePrimaryProbes.Any(primary =>
                    string.Equals(primary.SearchQuery, item.SearchQuery, StringComparison.OrdinalIgnoreCase))))
            .GroupBy(item => item.SearchQuery, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(ExternalLookupProbeLimit)
            .ToArray();

        var jikanPrimaryProbes = primaryWorkProbes
            .Where(item => IsJikanAnimeCandidate(item.Event))
            .ToArray();
        var jikanFallbackProbes = uniqueQueryProbes
            .Where(item => IsJikanAnimeCandidate(item.Event))
            .ToArray();
        var jikanProbes = jikanPrimaryProbes
            .Concat(jikanFallbackProbes.Where(item => !jikanPrimaryProbes.Any(primary =>
                string.Equals(primary.SearchQuery, item.SearchQuery, StringComparison.OrdinalIgnoreCase))))
            .GroupBy(item => BuildJikanProviderSearchQuery(
                item.Event, item.SearchQuery, item.ContainerParts, item.NumericClass, item.NumericDiagnostic.Stem),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.SearchQuery.Length)
                .ThenBy(item => item.Event.Start)
                .First())
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.SearchQuery.Length)
            .ThenBy(item => item.Event.Start)
            .Take(JikanLookupProbeLimit)
            .ToArray();
        var jikanProviderQueryByProbeQuery = jikanProbes
            .GroupBy(item => item.SearchQuery, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => BuildJikanProviderSearchQuery(
                    group.First().Event, group.First().SearchQuery, group.First().ContainerParts,
                    group.First().NumericClass, group.First().NumericDiagnostic.Stem),
                StringComparer.OrdinalIgnoreCase);
        var tvMazeProbeQueries = new HashSet<string>(tvMazeProbes.Select(item => item.SearchQuery), StringComparer.OrdinalIgnoreCase);
        var jikanProbeQueries = new HashSet<string>(jikanProbes.Select(item => item.SearchQuery), StringComparer.OrdinalIgnoreCase);
        var executionProbes = tvMazeProbes
            .Concat(jikanProbes.Where(item => !tvMazeProbeQueries.Contains(item.SearchQuery)))
            .ToArray();
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var representativeReasons = string.Join(",", representativeProbes
            .GroupBy(item => item.Need.Reason)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}"));
        var uniqueQueryReasons = string.Join(",", uniqueQueryProbes
            .GroupBy(item => item.Need.Reason)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}"));
        var selectedReasons = string.Join(",", probes
            .GroupBy(item => item.Need.Reason)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}"));
        var numericClasses = string.Join(",", uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix)
            .GroupBy(item => item.NumericClass)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}"));
        var derivedRelations = string.Join(",", uniqueQueryProbes
            .Where(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker)
            .GroupBy(item => item.DerivedRelation)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}"));
        WriteDeveloperLog($"external probe plan policy=local_sequence_stem_search_v3 representatives={representativeProbes.Length} uniqueQueries={uniqueQueryProbes.Length} candidateSelected={probes.Length} representativeReasons=[{representativeReasons}] uniqueQueryReasons=[{uniqueQueryReasons}] selectedReasons=[{selectedReasons}] numericClasses=[{numericClasses}] derivedRelations=[{derivedRelations}] numericStrongLocalSequenceCandidates={uniqueQueryProbes.Count(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix && item.NumericClass == AIrhythmNumericParenthesizedClass.LocalSequence && item.StrongLocalSequence)} numericStrongLocalSequenceSelected={numericLocalSequencePreferred.Length} numericAmbiguousCorroboratedCandidates={uniqueQueryProbes.Count(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix && item.NumericClass == AIrhythmNumericParenthesizedClass.Ambiguous && item.NumericContext.DistinctLocalValues >= 2)} numericAmbiguousCorroboratedSelected={numericAmbiguousCorroborated.Length} numericFallbackSelected={numericFallback.Length} containerDiversifiedCandidates={uniqueQueryProbes.Count(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate && item.ContainerLocalContext.IsDiversified)} containerRepeatedOnlyCandidates={uniqueQueryProbes.Count(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate && item.ContainerLocalContext.EvidenceLevel == AIrhythmLeadingContainerEvidenceLevel.Repeated)} containerSingletonCandidates={uniqueQueryProbes.Count(item => item.Need.Reason == AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate && item.ContainerLocalContext.EvidenceLevel == AIrhythmLeadingContainerEvidenceLevel.None)} candidateSelectionMax={ExternalLookupProbeLimit} tvMazeRequestBudget={AIrhythmExternalLookupAdapter.TvMazeRequestBudgetForDiagnostics} jikanRequestBudget={AIrhythmExternalLookupAdapter.JikanRequestBudgetForDiagnostics}");
        var tvMazeSearchAvailable = AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "SearchShow");
        var tvMazeAliasesAvailable = AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "GetAliases");
        var tvMazeEpisodesAvailable = AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "GetEpisodes");
        var jikanSearchAvailable = AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.JikanProviderId, "SearchAnime");
        var selectedEpisodeCandidates = tvMazeProbes.Count(item => AIrhythmRecommendationEngine.TryGetExternalEpisodeCandidate(item.Event.Title) is > 0);
        var selectedAnimeCandidates = jikanProbes.Length;
        var tvMazePrimarySummary = string.Join(",", tvMazePrimaryProbes.Take(12).Select(item =>
        {
            var signal = GetExternalUserIntentSignal(item.Event, snapshot, identityContext);
            return $"{item.SearchQuery}:auto={signal.AutomaticReservationCount}/manual={signal.ManualReservationCount}/recording={signal.RecordingCount}";
        }));
        var jikanPrimarySummary = string.Join(",", jikanPrimaryProbes.Take(12).Select(item =>
        {
            var signal = GetExternalUserIntentSignal(item.Event, snapshot, identityContext);
            return $"{BuildJikanProviderSearchQuery(item.Event, item.SearchQuery, item.ContainerParts, item.NumericClass, item.NumericDiagnostic.Stem)}:auto={signal.AutomaticReservationCount}/manual={signal.ManualReservationCount}/recording={signal.RecordingCount}";
        }));
        WriteDeveloperLog($"external provider plan policy=user_intent_first_v1 primaryWorks={primaryWorkProbes.Length} tvMazePrimary={tvMazePrimaryProbes.Length} tvMazeSelected={tvMazeProbes.Length} jikanPrimary={jikanPrimaryProbes.Length} jikanSelected={selectedAnimeCandidates} fallbackSpecialSelected={probes.Length} tvMazePrimarySample=[{tvMazePrimarySummary}] jikanPrimarySample=[{jikanPrimarySummary}] tvMazeSearchAvailable={tvMazeSearchAvailable} tvMazeAliasesAvailable={tvMazeAliasesAvailable} tvMazeEpisodesAvailable={tvMazeEpisodesAvailable} jikanSearchAvailable={jikanSearchAvailable} selectedLocalEpisodeCandidates={selectedEpisodeCandidates} fallbackPolicy=special_titles_only_after_user_intent providerRouting=anime_to_jikan_nonanime_to_tvmaze tvMazeRequestBudget={AIrhythmExternalLookupAdapter.TvMazeRequestBudgetForDiagnostics} jikanRequestBudget={AIrhythmExternalLookupAdapter.JikanRequestBudgetForDiagnostics} tvMazeCooldownSeconds={(long)AIrhythmExternalLookupAdapter.TvMazeRateLimitCooldownForDiagnostics.TotalSeconds} jikanCooldownSeconds={(long)AIrhythmExternalLookupAdapter.JikanRateLimitCooldownForDiagnostics.TotalSeconds} followupNetwork=provider_budgeted");
#endif
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var episodeCandidateProbeCount = 0;
        var episodeCandidateSearchEvidenceCount = 0;
        var episodeCandidateShowIdResolvedCount = 0;
        var episodeFollowupExecutedCount = 0;
        var episodeFollowupSupportedCount = 0;
#endif

        var providerRefresh = AIrhythmExternalLookupAdapter.BeginRefresh();
        var tvMazeAvailableForRefresh = AIrhythmExternalLookupAdapter.Supports(
            capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "SearchShow");
        var jikanAvailableForRefresh = AIrhythmExternalLookupAdapter.Supports(
            capability, AIrhythmExternalLookupAdapter.JikanProviderId, "SearchAnime");
        var dailyExternalRefreshClaimed = false;

        foreach (var probe in executionProbes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episodeCandidate = AIrhythmRecommendationEngine.TryGetExternalEpisodeCandidate(probe.Event.Title);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            if (episodeCandidate is > 0)
                episodeCandidateProbeCount++;
#endif

            var jikanProbeSearchAvailable = jikanAvailableForRefresh && providerRefresh.Jikan.IsOpen && jikanProbeQueries.Contains(probe.SearchQuery);
            var jikanProviderSearchQuery = jikanProbeSearchAvailable
                && jikanProviderQueryByProbeQuery.TryGetValue(probe.SearchQuery, out var mappedJikanQuery)
                    ? mappedJikanQuery
                    : probe.SearchQuery;
            var tvMazeProbeSearchAvailable = tvMazeAvailableForRefresh && providerRefresh.TvMaze.IsOpen && tvMazeProbeQueries.Contains(probe.SearchQuery);
            var searchProviderAvailable = jikanProbeSearchAvailable || tvMazeProbeSearchAvailable;
            var attemptDecision = searchProviderAvailable
                ? GetExternalLookupAttemptDecision(probe.SearchQuery)
                : (ShouldAttempt: false, SkipReason: "provider_unavailable", AgeSeconds: -1L);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            WriteDeveloperLog($"external probe decision query={probe.Query} searchQuery={probe.SearchQuery} jikanSearchQuery={(jikanProbeSearchAvailable ? jikanProviderSearchQuery : string.Empty)} reason={probe.Need.Reason} rawTitle={probe.Event.Title ?? string.Empty} normalizedTitle={probe.NumericDiagnostic.NormalizedTitle} numericStem={probe.NumericDiagnostic.Stem} numericClass={probe.NumericClass} numericValue={probe.NumericContext.Value} localValues={probe.NumericContext.DistinctLocalValues} nearestDistance={probe.NumericContext.NearestLocalDistance} strongLocalSequence={probe.StrongLocalSequence} container={probe.ContainerParts.Container} containerWork={probe.ContainerParts.WorkCandidate} embeddedTopic={probe.ContainerParts.EmbeddedTopic} containerEvents={probe.ContainerLocalContext.EventCount} containerDistinctWorks={probe.ContainerLocalContext.DistinctWorkCount} containerEvidence={probe.ContainerLocalContext.EvidenceLevel} containerRepeated={probe.ContainerLocalContext.IsRepeated} containerDiversified={probe.ContainerLocalContext.IsDiversified} derivedRelation={probe.DerivedRelation} action={(attemptDecision.ShouldAttempt ? "execute" : "skip")} skipReason={attemptDecision.SkipReason} ageSeconds={attemptDecision.AgeSeconds} providerAvailable={searchProviderAvailable}");
#endif
            if (!attemptDecision.ShouldAttempt)
                continue;

            if (!dailyExternalRefreshClaimed)
            {
                var dailyGate = TryClaimManualExternalRefreshDay(context, DateTimeOffset.Now);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"external manual refresh daily gate result={(dailyGate.Claimed ? "CLAIMED" : "SKIP")} reason={dailyGate.Reason} localDate={dailyGate.LocalDate} policy=manual_refresh_once_per_local_day jikanBudget={AIrhythmExternalLookupAdapter.JikanRequestBudgetForDiagnostics}");
#endif
                if (!dailyGate.Claimed)
                    break;
                dailyExternalRefreshClaimed = true;
            }

            // Online mode routes each ambiguity only to the bounded provider queue that selected it.
            // If both queues selected the same query, both Host-authorized providers may contribute.
            // Provider answers remain supplemental Evidence; no provider is allowed to rewrite local identity.
            MarkExternalLookupAttempt(probe.SearchQuery, DateTimeOffset.Now);
            var searchResults = new List<(AIrhythmExternalEvidenceResult Result, string ProviderSearchQuery, string InterpretationQuery)>(2);
            if (jikanProbeSearchAvailable)
            {
                var jikanSearchResult = await AIrhythmExternalLookupAdapter.SearchJikanAnimeAsync(
                    providerRefresh.Jikan, context, capability, jikanProviderSearchQuery, cancellationToken).ConfigureAwait(false);
                searchResults.Add((jikanSearchResult, jikanProviderSearchQuery, jikanProviderSearchQuery));
                if (jikanSearchResult.Code is TvAirExternalLookupResultCode.RateLimited or TvAirExternalLookupResultCode.HttpError)
                {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                    WriteDeveloperLog($"external jikan refresh circuit action=stop_remaining reason={jikanSearchResult.Code} query={jikanProviderSearchQuery} sourceQuery={probe.SearchQuery} minimumIntervalMs={(long)AIrhythmExternalLookupAdapter.JikanMinimumIntervalForDiagnostics.TotalMilliseconds} cooldownSeconds={(long)AIrhythmExternalLookupAdapter.JikanRateLimitCooldownForDiagnostics.TotalSeconds}");
#endif
                }
            }
            if (tvMazeProbeSearchAvailable)
            {
                var tvMazeSearchResult = await AIrhythmExternalLookupAdapter.SearchTvMazeAsync(
                    providerRefresh.TvMaze, context, capability, probe.SearchQuery, cancellationToken).ConfigureAwait(false);
                searchResults.Add((tvMazeSearchResult, probe.SearchQuery, probe.Query));
                if (tvMazeSearchResult.Code == TvAirExternalLookupResultCode.RateLimited)
                {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                    WriteDeveloperLog($"external tvmaze refresh circuit action=stop_remaining reason=RateLimited query={probe.SearchQuery} minimumIntervalMs={(long)AIrhythmExternalLookupAdapter.TvMazeMinimumIntervalForDiagnostics.TotalMilliseconds}");
#endif
                }
            }
            if (searchResults.Count == 0)
                continue;

            var normalizedList = new List<AIrhythmExternalEvidence>();
            var interpretedList = new List<AIrhythmExternalEvidence>();
            foreach (var searchEntry in searchResults)
            {
                var searchResult = searchEntry.Result;
                var providerEvidence = AIrhythmExternalLookupAdapter.Normalize(searchResult, searchEntry.InterpretationQuery).ToArray();
                var providerInterpreted = InterpretExternalEvidence(
                    providerEvidence,
                    probe.Need.Reason,
                    probe.ContainerParts,
                    probe.ContainerLocalContext,
                    probe.DerivedRelation,
                    probe.NumericClass,
                    searchEntry.InterpretationQuery,
                    probe.NumericContext.Value);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                WriteDeveloperLog($"external lookup return query={probe.Query} searchQuery={searchEntry.ProviderSearchQuery} sourceSearchQuery={probe.SearchQuery} provider={searchResult.ProviderId} operation={searchResult.Operation} code={searchResult.Code} success={searchResult.Success} bodyLength={(searchResult.Body ?? string.Empty).Length} providerMode=all_authorized");
#endif
                normalizedList.AddRange(providerEvidence);
                interpretedList.AddRange(providerInterpreted);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                AddExternalLookupTrace(probe.Event.Title ?? string.Empty, searchEntry.InterpretationQuery, searchResult, providerInterpreted);
                LogExternalLookupResult(searchResult.ProviderId, searchResult.Operation, searchResult.Code,
                    providerInterpreted.Length, localFallback: !searchResult.Success);
#endif
            }
            var normalized = normalizedList
                .GroupBy(item => $"{item.ProviderId}:{item.EntityId}:{item.CanonicalTitle}:{item.Relation}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var interpreted = interpretedList
                .GroupBy(item => $"{item.ProviderId}:{item.EntityId}:{item.CanonicalTitle}:{item.Relation}:{item.Verdict}:{item.VerdictReason}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var reachabilityEpisodeCandidate = episodeCandidate ?? 0;
            var reachabilityShowId = normalized.FirstOrDefault(item =>
                string.Equals(item.ProviderId, AIrhythmExternalLookupAdapter.TvMazeProviderId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase))?.EntityId;
            if (episodeCandidate is > 0 && normalized.Length > 0)
                episodeCandidateSearchEvidenceCount++;
            if (episodeCandidate is > 0 && !string.IsNullOrWhiteSpace(reachabilityShowId))
                episodeCandidateShowIdResolvedCount++;
            var reachabilityAliasesAvailable = tvMazeProbeSearchAvailable
                && AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "GetAliases");
            var reachabilityEpisodesAvailable = tvMazeProbeSearchAvailable
                && AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "GetEpisodes");
            var supportedReachability = ClassifyExternalSupportedReachability(
                normalized, interpreted, reachabilityAliasesAvailable, reachabilityEpisodesAvailable,
                reachabilityEpisodeCandidate, !string.IsNullOrWhiteSpace(reachabilityShowId));
            WriteDeveloperLog($"external supported reachability query={probe.Query} searchQuery={probe.SearchQuery} searchEvidence={normalized.Length} searchSupported={interpreted.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported)} searchUnresolved={interpreted.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Unresolved)} searchConflicting={interpreted.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Conflicting)} showIdPresent={!string.IsNullOrWhiteSpace(reachabilityShowId)} localEpisodeCandidate={reachabilityEpisodeCandidate} aliasesAvailable={reachabilityAliasesAvailable} episodesAvailable={reachabilityEpisodesAvailable} path={supportedReachability} aliasFollowupPlanned={supportedReachability.HasFlag(AIrhythmExternalSupportedReachability.AliasFollowupAvailable)} episodeFollowupPlanned={supportedReachability.HasFlag(AIrhythmExternalSupportedReachability.EpisodeFollowupAvailable)} criteriaUnchanged=True");
#endif
            AddExternalEvidence(interpreted);
            var projectedEvidence = interpreted.ToList();
            if (searchResults.Any(item => item.Result.Success))
                SetExternalEvidenceSummaryProjection(probe.Event, SummarizeExternalEvidence(projectedEvidence), DetermineExternalEvidenceAdjustmentKind(projectedEvidence), DetermineExternalEvidenceSupportingVerdictReason(projectedEvidence), DateTimeOffset.Now, projectedEvidence);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var expectedRelation = probe.Need.Reason switch
            {
                AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate => GetLeadingContainerExternalEvidenceRelation(probe.ContainerLocalContext),
                AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker => GetDerivedProgramExternalEvidenceRelation(probe.DerivedRelation),
                AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix => GetNumericParenthesizedExternalEvidenceRelation(probe.NumericClass),
                _ => "not_applicable"
            };
            var appliedRelations = interpreted
                .Where(item => item.Relation.StartsWith("candidate:container_work_", StringComparison.OrdinalIgnoreCase)
                    || item.Relation.StartsWith("candidate:numeric_", StringComparison.OrdinalIgnoreCase)
                    || item.Relation.StartsWith("related:", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var relationSample = appliedRelations.FirstOrDefault();
            var evidenceSummary = SummarizeExternalEvidence(interpreted);
            WriteDeveloperLog($"external evidence interpretation query={probe.Query} searchQuery={probe.SearchQuery} reason={probe.Need.Reason} input={normalized.Length} output={interpreted.Length} containerEvidence={probe.ContainerLocalContext.EvidenceLevel} derivedRelation={probe.DerivedRelation} numericRelationClass={probe.NumericClass} relation={expectedRelation} relationApplied={appliedRelations.Length} relationSampleEntity={relationSample?.EntityId ?? string.Empty} relationSampleTitle={relationSample?.CanonicalTitle ?? string.Empty} relationSampleValue={relationSample?.Relation ?? string.Empty} verdicts=[{string.Join(",", interpreted.GroupBy(item => item.Verdict).OrderBy(group => group.Key).Select(group => $"{group.Key}:{group.Count()}"))}] verdictReasons=[{string.Join(",", interpreted.GroupBy(item => item.VerdictReason).OrderBy(group => group.Key).Select(group => $"{group.Key}:{group.Count()}"))}] verdictSample={relationSample?.Verdict.ToString() ?? string.Empty} verdictReasonSample={relationSample?.VerdictReason.ToString() ?? string.Empty} evidenceSummary={evidenceSummary.Status} summaryTotal={evidenceSummary.Total} summarySupported={evidenceSummary.Supported} summaryUnresolved={evidenceSummary.Unresolved} summaryConflicting={evidenceSummary.Conflicting} identityRewrite=False scoreRewrite=False");
#endif

            var tvMazeNormalized = normalized
                .Where(item => string.Equals(item.ProviderId, AIrhythmExternalLookupAdapter.TvMazeProviderId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (tvMazeNormalized.Length == 0)
                continue;

            var showId = tvMazeNormalized.FirstOrDefault(item => string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase))?.EntityId;
            var searchNeedsIdentityFollowup = interpreted.Any(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Unresolved)
                && !interpreted.Any(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported)
                && !interpreted.Any(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Conflicting)
                && !string.IsNullOrWhiteSpace(showId);
            if (tvMazeAvailableForRefresh
                && providerRefresh.TvMaze.IsOpen
                && searchNeedsIdentityFollowup
                && AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "GetAliases"))
            {
                var aliasesResult = await AIrhythmExternalLookupAdapter.GetTvMazeAliasesAsync(providerRefresh.TvMaze, context, capability, showId!, cancellationToken).ConfigureAwait(false);
                var aliasEvidence = AIrhythmExternalLookupAdapter.Normalize(aliasesResult, probe.Query).Take(24).ToArray();
                var interpretedAliasEvidence = InterpretExternalEvidence(
                    aliasEvidence,
                    probe.Need.Reason,
                    probe.ContainerParts,
                    probe.ContainerLocalContext,
                    probe.DerivedRelation,
                    probe.NumericClass,
                    probe.Query,
                    probe.NumericContext.Value);
                AddExternalEvidence(interpretedAliasEvidence);
                if (aliasesResult.Success)
                {
                    projectedEvidence.AddRange(interpretedAliasEvidence);
                    SetExternalEvidenceSummaryProjection(probe.Event, SummarizeExternalEvidence(projectedEvidence), DetermineExternalEvidenceAdjustmentKind(projectedEvidence), DetermineExternalEvidenceSupportingVerdictReason(projectedEvidence), DateTimeOffset.Now, projectedEvidence);
                }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                var aliasSummary = SummarizeExternalEvidence(interpretedAliasEvidence);
                WriteDeveloperLog($"external alias followup query={probe.Query} searchQuery={probe.SearchQuery} showId={showId} result={aliasesResult.Code} evidence={interpretedAliasEvidence.Length} summary={aliasSummary.Status} supported={aliasSummary.Supported} unresolved={aliasSummary.Unresolved} conflicting={aliasSummary.Conflicting} criteriaUnchanged=True scoreRewrite=False orderingRewrite=False");
                AddExternalLookupTrace(probe.Event.Title ?? string.Empty, probe.Query, aliasesResult, interpretedAliasEvidence);
                LogExternalLookupResult(aliasesResult.ProviderId, aliasesResult.Operation, aliasesResult.Code, interpretedAliasEvidence.Length, localFallback: !aliasesResult.Success);
#endif
            }

            if (!tvMazeAvailableForRefresh || !providerRefresh.TvMaze.IsOpen || episodeCandidate is not > 0 || string.IsNullOrWhiteSpace(showId)
                || !AIrhythmExternalLookupAdapter.Supports(capability, AIrhythmExternalLookupAdapter.TvMazeProviderId, "GetEpisodes"))
                continue;

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            episodeFollowupExecutedCount++;
#endif
            var episodesResult = await AIrhythmExternalLookupAdapter.GetTvMazeEpisodesAsync(providerRefresh.TvMaze, context, capability, showId, cancellationToken).ConfigureAwait(false);
            var episodeEvidence = AIrhythmExternalLookupAdapter.Normalize(episodesResult, probe.Query)
                .Where(item => item.Episode == episodeCandidate)
                .Take(8)
                .ToArray();
            var interpretedEpisodeEvidence = InterpretExternalEvidence(
                episodeEvidence,
                probe.Need.Reason,
                probe.ContainerParts,
                probe.ContainerLocalContext,
                probe.DerivedRelation,
                probe.NumericClass,
                probe.Query,
                probe.NumericContext.Value);
            AddExternalEvidence(interpretedEpisodeEvidence);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            if (interpretedEpisodeEvidence.Any(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported))
                episodeFollowupSupportedCount++;
#endif
            if (episodesResult.Success)
            {
                projectedEvidence.AddRange(interpretedEpisodeEvidence);
                SetExternalEvidenceSummaryProjection(probe.Event, SummarizeExternalEvidence(projectedEvidence), DetermineExternalEvidenceAdjustmentKind(projectedEvidence), DetermineExternalEvidenceSupportingVerdictReason(projectedEvidence), DateTimeOffset.Now, projectedEvidence);
            }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            AddExternalLookupTrace(probe.Event.Title ?? string.Empty, probe.Query, episodesResult, interpretedEpisodeEvidence);
            LogExternalLookupResult(episodesResult.ProviderId, episodesResult.Operation, episodesResult.Code, interpretedEpisodeEvidence.Length, localFallback: !episodesResult.Success);
#endif
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        WriteDeveloperLog($"external episode support reachability selectedEpisodeCandidates={episodeCandidateProbeCount} searchEvidence={episodeCandidateSearchEvidenceCount} showIdResolved={episodeCandidateShowIdResolvedCount} getEpisodesExecuted={episodeFollowupExecutedCount} episodeSupported={episodeFollowupSupportedCount} moderateCalibrationState={(episodeFollowupSupportedCount > 0 ? "runtime_evidence_available" : "awaiting_real_episode_support")} selectionPolicy=user_intent_first_v1_with_special_fallback searchBudgetUnchanged=True");
        LogExternalEvidenceCacheRelationState("after_refresh");
#endif
    }

    private static AIrhythmExternalEvidence[] InterpretExternalEvidence(
        IReadOnlyList<AIrhythmExternalEvidence> evidence,
        AIrhythmExternalEvidenceNeedReason reason,
        AIrhythmLeadingContainerParts containerParts,
        AIrhythmLeadingContainerLocalContext containerContext,
        AIrhythmDerivedProgramRelation derivedRelation,
        AIrhythmNumericParenthesizedClass numericClass,
        string queryTitle = "",
        int numericValue = 0)
    {
        if (evidence.Count == 0)
            return Array.Empty<AIrhythmExternalEvidence>();

        string? relation = null;
        if (reason == AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate
            && !string.IsNullOrWhiteSpace(containerParts.WorkCandidate))
            relation = GetLeadingContainerExternalEvidenceRelation(containerContext);
        else if (reason == AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker)
            relation = GetDerivedProgramExternalEvidenceRelation(derivedRelation);
        else if (reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix)
            relation = GetNumericParenthesizedExternalEvidenceRelation(numericClass);

        return evidence
            .Select(item =>
            {
                var verdict = EvaluateExternalEvidenceVerdict(item, reason, queryTitle, numericValue);
                return item with
                {
                    Relation = string.IsNullOrWhiteSpace(relation) ? item.Relation : relation,
                    Verdict = verdict.Verdict,
                    VerdictReason = verdict.Reason
                };
            })
            .ToArray();
    }

    private static (AIrhythmExternalEvidenceVerdict Verdict, AIrhythmExternalEvidenceVerdictReason Reason) EvaluateExternalEvidenceVerdict(
        AIrhythmExternalEvidence evidence,
        AIrhythmExternalEvidenceNeedReason reason,
        string queryTitle,
        int numericValue)
    {
        if (reason == AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix
            && numericValue > 0
            && evidence.Episode is int externalEpisode
            && externalEpisode > 0)
        {
            return externalEpisode == numericValue
                ? (AIrhythmExternalEvidenceVerdict.Supported, AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch)
                : (AIrhythmExternalEvidenceVerdict.Conflicting, AIrhythmExternalEvidenceVerdictReason.EpisodeNumberConflict);
        }

        if (AIrhythmExternalLookupAdapter.GetTitleSimilarity(queryTitle, evidence.CanonicalTitle) > 0d)
            return (AIrhythmExternalEvidenceVerdict.Supported, AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch);

        if (evidence.Aliases.Count > 0
            && evidence.Aliases.Any(alias => AIrhythmExternalLookupAdapter.GetTitleSimilarity(queryTitle, alias) > 0d))
            return (AIrhythmExternalEvidenceVerdict.Supported, AIrhythmExternalEvidenceVerdictReason.AliasMatch);

        // Normal user-intent Work lookups have no structural ambiguity relation to justify a
        // looser provider-confidence verdict. Require an actual canonical-title or alias match.
        if (reason == AIrhythmExternalEvidenceNeedReason.None)
            return (AIrhythmExternalEvidenceVerdict.Unresolved, AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence);

        // Alias follow-up is identity evidence only when an alias actually matches the local query.
        // Do not turn the mere existence of provider aliases into generic ProviderConfidence support.
        if (!string.Equals(evidence.MediaType, "alias", StringComparison.OrdinalIgnoreCase)
            && evidence.Confidence >= 0.5d)
            return (AIrhythmExternalEvidenceVerdict.Supported, AIrhythmExternalEvidenceVerdictReason.ProviderConfidence);

        return (AIrhythmExternalEvidenceVerdict.Unresolved, AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence);
    }

    private static AIrhythmExternalEvidenceSummary SummarizeExternalEvidence(IReadOnlyList<AIrhythmExternalEvidence> evidence)
    {
        if (evidence.Count == 0)
            return new AIrhythmExternalEvidenceSummary(AIrhythmExternalEvidenceSummaryStatus.NoEvidence, 0, 0, 0, 0);

        var supported = evidence.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported);
        var unresolved = evidence.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Unresolved);
        var conflicting = evidence.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Conflicting);
        var status = conflicting > 0
            ? AIrhythmExternalEvidenceSummaryStatus.Conflicting
            : supported > 0
                ? AIrhythmExternalEvidenceSummaryStatus.Supported
                : AIrhythmExternalEvidenceSummaryStatus.UnresolvedOnly;

        return new AIrhythmExternalEvidenceSummary(status, evidence.Count, supported, unresolved, conflicting);
    }

    private static string GetLeadingContainerExternalEvidenceRelation(AIrhythmLeadingContainerLocalContext context)
        => context.EvidenceLevel switch
        {
            AIrhythmLeadingContainerEvidenceLevel.Diversified => "candidate:container_work_diversified",
            AIrhythmLeadingContainerEvidenceLevel.Repeated => "candidate:container_work_repeated",
            _ => "candidate:container_work_unconfirmed"
        };

    private static string GetDerivedProgramExternalEvidenceRelation(AIrhythmDerivedProgramRelation relation)
        => relation switch
        {
            AIrhythmDerivedProgramRelation.Promo => "related:promo",
            AIrhythmDerivedProgramRelation.Preview => "related:preview",
            AIrhythmDerivedProgramRelation.Recap => "related:recap",
            _ => "related:derived_unconfirmed"
        };

    private static string GetNumericParenthesizedExternalEvidenceRelation(AIrhythmNumericParenthesizedClass numericClass)
        => numericClass switch
        {
            AIrhythmNumericParenthesizedClass.LikelyYear => "candidate:numeric_likely_year",
            AIrhythmNumericParenthesizedClass.LocalSequence => "candidate:numeric_local_sequence",
            AIrhythmNumericParenthesizedClass.Ambiguous => "candidate:numeric_ambiguous",
            _ => "candidate:numeric_unconfirmed"
        };

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
    private static AIrhythmExternalSupportedReachability ClassifyExternalSupportedReachability(
        IReadOnlyList<AIrhythmExternalEvidence> normalized,
        IReadOnlyList<AIrhythmExternalEvidence> interpreted,
        bool aliasFollowupAvailable,
        bool episodeFollowupAvailable,
        int episodeCandidate,
        bool showIdPresent)
    {
        if (interpreted.Any(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported))
            return AIrhythmExternalSupportedReachability.SearchConfirmed;
        if (normalized.Count == 0)
            return AIrhythmExternalSupportedReachability.NoProviderEvidence;

        var reachability = AIrhythmExternalSupportedReachability.None;
        if (interpreted.Any(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Unresolved))
        {
            if (aliasFollowupAvailable && showIdPresent)
                reachability |= AIrhythmExternalSupportedReachability.AliasFollowupAvailable;
            if (episodeFollowupAvailable && showIdPresent && episodeCandidate > 0)
                reachability |= AIrhythmExternalSupportedReachability.EpisodeFollowupAvailable;
        }

        return reachability == AIrhythmExternalSupportedReachability.None
            ? AIrhythmExternalSupportedReachability.UnresolvedNoFollowupPath
            : reachability;
    }

    private static void RunExternalEvidenceInterpretationSelfValidation()
    {
        // Exercise the exact production interpretation function without provider/network input.
        // This proves Relation attachment on the evidence object itself; it never enters cache,
        // scoring, identity, or the external lookup budget.
        var source = new[]
        {
            new AIrhythmExternalEvidence(
                "diagnostic",
                "selftest:relation",
                "SelfTest Work",
                Array.Empty<string>(),
                "tv",
                null,
                7,
                null,
                "provider:original",
                1.0,
                DateTimeOffset.UnixEpoch)
        };
        var parts = new AIrhythmLeadingContainerParts(
            "【SelfTest Container】SelfTest Work",
            "SelfTest Container",
            "SelfTest Work",
            "SelfTest Work",
            string.Empty);

        static string RelationOf(AIrhythmExternalEvidence[] items)
            => items.Length == 1 ? items[0].Relation : $"count:{items.Length}";
        static string VerdictOf(AIrhythmExternalEvidence[] items)
            => items.Length == 1 ? items[0].Verdict.ToString() : $"count:{items.Length}";
        static string VerdictReasonOf(AIrhythmExternalEvidence[] items)
            => items.Length == 1 ? items[0].VerdictReason.ToString() : $"count:{items.Length}";

        var diversified = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate, parts,
            new AIrhythmLeadingContainerLocalContext(2, 2, AIrhythmLeadingContainerEvidenceLevel.Diversified),
            AIrhythmDerivedProgramRelation.None, AIrhythmNumericParenthesizedClass.None);
        var repeated = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate, parts,
            new AIrhythmLeadingContainerLocalContext(2, 1, AIrhythmLeadingContainerEvidenceLevel.Repeated),
            AIrhythmDerivedProgramRelation.None, AIrhythmNumericParenthesizedClass.None);
        var unconfirmed = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate, parts,
            new AIrhythmLeadingContainerLocalContext(1, 1, AIrhythmLeadingContainerEvidenceLevel.None),
            AIrhythmDerivedProgramRelation.None, AIrhythmNumericParenthesizedClass.None);
        var promo = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker, default, default,
            AIrhythmDerivedProgramRelation.Promo, AIrhythmNumericParenthesizedClass.None);
        var preview = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker, default, default,
            AIrhythmDerivedProgramRelation.Preview, AIrhythmNumericParenthesizedClass.None);
        var recap = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker, default, default,
            AIrhythmDerivedProgramRelation.Recap, AIrhythmNumericParenthesizedClass.None);
        var numericLikelyYear = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix, default, default,
            AIrhythmDerivedProgramRelation.None, AIrhythmNumericParenthesizedClass.LikelyYear);
        var numericLocalSequence = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix, default, default,
            AIrhythmDerivedProgramRelation.None, AIrhythmNumericParenthesizedClass.LocalSequence, "SelfTest Work", 7);
        var numericAmbiguous = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix, default, default,
            AIrhythmDerivedProgramRelation.None, AIrhythmNumericParenthesizedClass.Ambiguous, "SelfTest Work", 8);
        var nonAmbiguous = InterpretExternalEvidence(
            source, AIrhythmExternalEvidenceNeedReason.None, parts,
            new AIrhythmLeadingContainerLocalContext(2, 2, AIrhythmLeadingContainerEvidenceLevel.Diversified),
            AIrhythmDerivedProgramRelation.Promo, AIrhythmNumericParenthesizedClass.Ambiguous);
        var aliasMatchEvidence = new[]
        {
            source[0] with
            {
                EntityId = string.Empty,
                CanonicalTitle = string.Empty,
                Aliases = new[] { "SelfTest Alias" },
                MediaType = "alias",
                Confidence = 1.0
            }
        };
        var aliasMismatchEvidence = new[]
        {
            aliasMatchEvidence[0] with { Aliases = new[] { "Unrelated Alias" } }
        };
        var aliasMatched = InterpretExternalEvidence(
            aliasMatchEvidence, AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker, default, default,
            AIrhythmDerivedProgramRelation.Promo, AIrhythmNumericParenthesizedClass.None, "SelfTest Alias");
        var aliasMismatched = InterpretExternalEvidence(
            aliasMismatchEvidence, AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker, default, default,
            AIrhythmDerivedProgramRelation.Promo, AIrhythmNumericParenthesizedClass.None, "SelfTest Alias");

        var diversifiedRelation = RelationOf(diversified);
        var repeatedRelation = RelationOf(repeated);
        var unconfirmedRelation = RelationOf(unconfirmed);
        var promoRelation = RelationOf(promo);
        var previewRelation = RelationOf(preview);
        var recapRelation = RelationOf(recap);
        var numericLikelyYearRelation = RelationOf(numericLikelyYear);
        var numericLocalSequenceRelation = RelationOf(numericLocalSequence);
        var numericAmbiguousRelation = RelationOf(numericAmbiguous);
        var nonAmbiguousRelation = RelationOf(nonAmbiguous);
        var supportedVerdict = VerdictOf(promo);
        var supportedReason = VerdictReasonOf(promo);
        var numericSupportedVerdict = VerdictOf(numericLocalSequence);
        var numericSupportedReason = VerdictReasonOf(numericLocalSequence);
        var numericConflictingVerdict = VerdictOf(numericAmbiguous);
        var numericConflictingReason = VerdictReasonOf(numericAmbiguous);
        var nonAmbiguousVerdict = VerdictOf(nonAmbiguous);
        var nonAmbiguousReason = VerdictReasonOf(nonAmbiguous);
        var aliasMatchedVerdict = VerdictOf(aliasMatched);
        var aliasMatchedReason = VerdictReasonOf(aliasMatched);
        var aliasMismatchedVerdict = VerdictOf(aliasMismatched);
        var aliasMismatchedReason = VerdictReasonOf(aliasMismatched);
        var summaryNoEvidence = SummarizeExternalEvidence(Array.Empty<AIrhythmExternalEvidence>());
        var summarySupported = SummarizeExternalEvidence(new[]
        {
            source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Supported },
            source[0] with { EntityId = "selftest:summary:unresolved", Verdict = AIrhythmExternalEvidenceVerdict.Unresolved }
        });
        var summaryUnresolved = SummarizeExternalEvidence(new[]
        {
            source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Unresolved }
        });
        var summaryConflicting = SummarizeExternalEvidence(new[]
        {
            source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Supported },
            source[0] with { EntityId = "selftest:summary:conflict", Verdict = AIrhythmExternalEvidenceVerdict.Conflicting }
        });
        var evaluationNoEvidence = AIrhythmRecommendationEngine.ToExternalEvidenceEvaluationFlags(summaryNoEvidence.Status);
        var evaluationSupported = AIrhythmRecommendationEngine.ToExternalEvidenceEvaluationFlags(summarySupported.Status);
        var evaluationUnresolved = AIrhythmRecommendationEngine.ToExternalEvidenceEvaluationFlags(summaryUnresolved.Status);
        var evaluationConflicting = AIrhythmRecommendationEngine.ToExternalEvidenceEvaluationFlags(summaryConflicting.Status);
        var gateNoEvidence = AIrhythmRecommendationEngine.ToExternalEvidenceConfidenceGate(evaluationNoEvidence);
        var gateSupported = AIrhythmRecommendationEngine.ToExternalEvidenceConfidenceGate(evaluationSupported);
        var gateUnresolved = AIrhythmRecommendationEngine.ToExternalEvidenceConfidenceGate(evaluationUnresolved);
        var gateConflicting = AIrhythmRecommendationEngine.ToExternalEvidenceConfidenceGate(evaluationConflicting);
        var adjustmentNoEvidence = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentCandidate(gateNoEvidence);
        var adjustmentSupported = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentCandidate(gateSupported);
        var adjustmentUnresolved = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentCandidate(gateUnresolved);
        var adjustmentConflicting = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentCandidate(gateConflicting);
        var adjustmentKindIdentity = DetermineExternalEvidenceAdjustmentKind(new[]
        {
            source[0] with
            {
                Verdict = AIrhythmExternalEvidenceVerdict.Supported,
                VerdictReason = AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch
            }
        });
        var adjustmentKindEpisode = DetermineExternalEvidenceAdjustmentKind(new[]
        {
            source[0] with
            {
                Verdict = AIrhythmExternalEvidenceVerdict.Supported,
                VerdictReason = AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch,
                Episode = 2
            }
        });
        var adjustmentKindUnresolved = DetermineExternalEvidenceAdjustmentKind(new[]
        {
            source[0] with
            {
                Verdict = AIrhythmExternalEvidenceVerdict.Unresolved,
                VerdictReason = AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence
            }
        });
        var normalizedKindBlocked = AIrhythmRecommendationEngine.NormalizeExternalEvidenceAdjustmentKind(adjustmentConflicting, adjustmentKindIdentity);
        var adjustmentStrengthIdentity = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentStrength(adjustmentSupported, adjustmentKindIdentity);
        var adjustmentStrengthEpisode = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentStrength(adjustmentSupported, adjustmentKindEpisode);
        var adjustmentStrengthUnresolved = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentStrength(adjustmentUnresolved, adjustmentKindUnresolved);
        var adjustmentStrengthBlocked = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentStrength(adjustmentConflicting, normalizedKindBlocked);
        var shadowAdjustmentValueIdentity = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentValue(adjustmentStrengthIdentity);
        var shadowAdjustmentValueEpisode = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentValue(adjustmentStrengthEpisode);
        var shadowAdjustmentValueUnresolved = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentValue(adjustmentStrengthUnresolved);
        var shadowAdjustmentValueBlocked = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentValue(adjustmentStrengthBlocked);
        var shadowImpactBase = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00", CultureInfo.InvariantCulture);
        AIrhythmRecommendation RawCoordinateItem(int index, int displayScore, double deviationRawScore, AIrhythmExternalEvidenceAdjustmentStrength strength = AIrhythmExternalEvidenceAdjustmentStrength.None)
            => new($"raw-shadow-{index}", "selftest", string.Empty, string.Empty, shadowImpactBase.AddMinutes(300 + index), displayScore, Array.Empty<string>(), $"raw-shadow-series-{index}", null, DeviationRawScore: deviationRawScore, ExternalEvidenceAdjustmentKind: strength == AIrhythmExternalEvidenceAdjustmentStrength.None ? AIrhythmExternalEvidenceAdjustmentKind.None : AIrhythmExternalEvidenceAdjustmentKind.IdentitySupport, ExternalEvidenceAdjustmentStrength: strength, ExternalEvidenceSupportingVerdictReason: strength == AIrhythmExternalEvidenceAdjustmentStrength.None ? AIrhythmExternalEvidenceVerdictReason.NotApplicable : AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch);
        var rawCoordinateItems = new[]
        {
            RawCoordinateItem(1, 38, 40.0d),
            RawCoordinateItem(2, 50, 50.0d, AIrhythmExternalEvidenceAdjustmentStrength.Weak),
            RawCoordinateItem(3, 62, 60.0d)
        };
        var rawCoordinateSweep = AIrhythmRecommendationEngine.AnalyzeExternalEvidenceRawCoordinateShadowSweep(rawCoordinateItems, 0.25d, 0.75d);
        var rawCoordinateDetail = AIrhythmRecommendationEngine.AnalyzeExternalEvidenceRawCoordinateShadowSweepDetails(rawCoordinateItems, 0.25d, 0.75d, 8);
        var reachabilitySearchConfirmed = ClassifyExternalSupportedReachability(
            source, new[] { source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Supported } }, true, true, 7, true);
        var reachabilityAlias = ClassifyExternalSupportedReachability(
            source, new[] { source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Unresolved } }, true, false, 0, true);
        var reachabilityEpisode = ClassifyExternalSupportedReachability(
            source, new[] { source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Unresolved } }, false, true, 7, true);
        var reachabilityNoEvidence = ClassifyExternalSupportedReachability(
            Array.Empty<AIrhythmExternalEvidence>(), Array.Empty<AIrhythmExternalEvidence>(), true, true, 7, false);
        var reachabilityNoFollowup = ClassifyExternalSupportedReachability(
            source, new[] { source[0] with { Verdict = AIrhythmExternalEvidenceVerdict.Unresolved } }, false, false, 0, true);
        var strongLocalSequenceSearchQuery = BuildExternalProviderSearchQuery(
            "税金で買った本(5)",
            AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix,
            AIrhythmNumericParenthesizedClass.LocalSequence,
            true);
        var ambiguousSearchQuery = BuildExternalProviderSearchQuery(
            "作品(5)",
            AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix,
            AIrhythmNumericParenthesizedClass.Ambiguous,
            false);
        var likelyYearSearchQuery = BuildExternalProviderSearchQuery(
            "作品(2020)",
            AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix,
            AIrhythmNumericParenthesizedClass.LikelyYear,
            false);
        var adjustmentValue = AIrhythmRecommendationEngine.ToExternalEvidenceAdjustmentValue(adjustmentStrengthIdentity);
        var result = string.Equals(diversifiedRelation, "candidate:container_work_diversified", StringComparison.Ordinal)
            && string.Equals(repeatedRelation, "candidate:container_work_repeated", StringComparison.Ordinal)
            && string.Equals(unconfirmedRelation, "candidate:container_work_unconfirmed", StringComparison.Ordinal)
            && string.Equals(promoRelation, "related:promo", StringComparison.Ordinal)
            && string.Equals(previewRelation, "related:preview", StringComparison.Ordinal)
            && string.Equals(recapRelation, "related:recap", StringComparison.Ordinal)
            && string.Equals(numericLikelyYearRelation, "candidate:numeric_likely_year", StringComparison.Ordinal)
            && string.Equals(numericLocalSequenceRelation, "candidate:numeric_local_sequence", StringComparison.Ordinal)
            && string.Equals(numericAmbiguousRelation, "candidate:numeric_ambiguous", StringComparison.Ordinal)
            && string.Equals(nonAmbiguousRelation, "provider:original", StringComparison.Ordinal)
            && supportedVerdict == nameof(AIrhythmExternalEvidenceVerdict.Supported)
            && supportedReason == nameof(AIrhythmExternalEvidenceVerdictReason.ProviderConfidence)
            && numericSupportedVerdict == nameof(AIrhythmExternalEvidenceVerdict.Supported)
            && numericSupportedReason == nameof(AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch)
            && numericConflictingVerdict == nameof(AIrhythmExternalEvidenceVerdict.Conflicting)
            && numericConflictingReason == nameof(AIrhythmExternalEvidenceVerdictReason.EpisodeNumberConflict)
            && nonAmbiguousVerdict == nameof(AIrhythmExternalEvidenceVerdict.Unresolved)
            && nonAmbiguousReason == nameof(AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence)
            && summaryNoEvidence.Status == AIrhythmExternalEvidenceSummaryStatus.NoEvidence
            && summarySupported.Status == AIrhythmExternalEvidenceSummaryStatus.Supported
            && summarySupported.Total == 2 && summarySupported.Supported == 1 && summarySupported.Unresolved == 1 && summarySupported.Conflicting == 0
            && summaryUnresolved.Status == AIrhythmExternalEvidenceSummaryStatus.UnresolvedOnly
            && summaryConflicting.Status == AIrhythmExternalEvidenceSummaryStatus.Conflicting
            && summaryConflicting.Total == 2 && summaryConflicting.Supported == 1 && summaryConflicting.Conflicting == 1
            && evaluationNoEvidence == AIrhythmExternalEvidenceEvaluationFlags.None
            && evaluationSupported == AIrhythmExternalEvidenceEvaluationFlags.Supported
            && evaluationUnresolved == AIrhythmExternalEvidenceEvaluationFlags.Unresolved
            && evaluationConflicting == AIrhythmExternalEvidenceEvaluationFlags.Conflicting
            && gateNoEvidence == AIrhythmExternalEvidenceConfidenceGate.NotApplicable
            && gateSupported == AIrhythmExternalEvidenceConfidenceGate.Allowed
            && gateUnresolved == AIrhythmExternalEvidenceConfidenceGate.Neutral
            && gateConflicting == AIrhythmExternalEvidenceConfidenceGate.Blocked
            && adjustmentNoEvidence == AIrhythmExternalEvidenceAdjustmentCandidate.None
            && adjustmentSupported == AIrhythmExternalEvidenceAdjustmentCandidate.Eligible
            && adjustmentUnresolved == AIrhythmExternalEvidenceAdjustmentCandidate.None
            && adjustmentConflicting == AIrhythmExternalEvidenceAdjustmentCandidate.None
            && adjustmentKindIdentity == AIrhythmExternalEvidenceAdjustmentKind.IdentitySupport
            && adjustmentKindEpisode == AIrhythmExternalEvidenceAdjustmentKind.EpisodeSupport
            && adjustmentKindUnresolved == AIrhythmExternalEvidenceAdjustmentKind.None
            && normalizedKindBlocked == AIrhythmExternalEvidenceAdjustmentKind.None
            && adjustmentStrengthIdentity == AIrhythmExternalEvidenceAdjustmentStrength.Weak
            && adjustmentStrengthEpisode == AIrhythmExternalEvidenceAdjustmentStrength.Moderate
            && adjustmentStrengthUnresolved == AIrhythmExternalEvidenceAdjustmentStrength.None
            && adjustmentStrengthBlocked == AIrhythmExternalEvidenceAdjustmentStrength.None
            && Math.Abs(shadowAdjustmentValueIdentity - 0.25d) < 0.000001d
            && Math.Abs(shadowAdjustmentValueEpisode - 0.75d) < 0.000001d
            && Math.Abs(shadowAdjustmentValueUnresolved) < 0.000001d
            && Math.Abs(shadowAdjustmentValueBlocked) < 0.000001d
            && rawCoordinateSweep.NonZeroCandidates == 1
            && rawCoordinateDetail.Length == 1
            && rawCoordinateDetail[0].Title == "raw-shadow-2"
            && Math.Abs(rawCoordinateDetail[0].CurrentDeviationRawScore - 50.0d) < 0.000001d
            && Math.Abs(rawCoordinateDetail[0].ShadowDeviationRawScore - 50.25d) < 0.000001d
            && aliasMatchedVerdict == AIrhythmExternalEvidenceVerdict.Supported.ToString()
            && aliasMatchedReason == AIrhythmExternalEvidenceVerdictReason.AliasMatch.ToString()
            && aliasMismatchedVerdict == AIrhythmExternalEvidenceVerdict.Unresolved.ToString()
            && aliasMismatchedReason == AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence.ToString()
            && reachabilitySearchConfirmed == AIrhythmExternalSupportedReachability.SearchConfirmed
            && reachabilityAlias == AIrhythmExternalSupportedReachability.AliasFollowupAvailable
            && reachabilityEpisode == AIrhythmExternalSupportedReachability.EpisodeFollowupAvailable
            && reachabilityNoEvidence == AIrhythmExternalSupportedReachability.NoProviderEvidence
            && reachabilityNoFollowup == AIrhythmExternalSupportedReachability.UnresolvedNoFollowupPath
            && string.Equals(strongLocalSequenceSearchQuery, "税金で買った本", StringComparison.Ordinal)
            && string.Equals(ambiguousSearchQuery, "作品(5)", StringComparison.Ordinal)
            && string.Equals(likelyYearSearchQuery, "作品(2020)", StringComparison.Ordinal)
            && Math.Abs(adjustmentValue - 0.25d) < 0.000001d
            ? "PASS"
            : "FAIL";

        WriteDeveloperLog($"external evidence interpretation selftest result={result} diversified={diversifiedRelation} repeated={repeatedRelation} unconfirmed={unconfirmedRelation} promo={promoRelation} preview={previewRelation} recap={recapRelation} numericLikelyYear={numericLikelyYearRelation} numericLocalSequence={numericLocalSequenceRelation} numericAmbiguous={numericAmbiguousRelation} nonAmbiguous={nonAmbiguousRelation} verdictSupported={supportedVerdict} verdictSupportedReason={supportedReason} verdictNumericSupported={numericSupportedVerdict} verdictNumericSupportedReason={numericSupportedReason} verdictNumericConflict={numericConflictingVerdict} verdictNumericConflictReason={numericConflictingReason} verdictNonAmbiguous={nonAmbiguousVerdict} verdictNonAmbiguousReason={nonAmbiguousReason} aliasMatchedVerdict={aliasMatchedVerdict} aliasMatchedReason={aliasMatchedReason} aliasMismatchedVerdict={aliasMismatchedVerdict} aliasMismatchedReason={aliasMismatchedReason} summaryNoEvidence={summaryNoEvidence.Status} summarySupported={summarySupported.Status}:{summarySupported.Supported}/{summarySupported.Total} summaryUnresolved={summaryUnresolved.Status}:{summaryUnresolved.Unresolved}/{summaryUnresolved.Total} summaryConflicting={summaryConflicting.Status}:{summaryConflicting.Conflicting}/{summaryConflicting.Total} evaluationNoEvidence={evaluationNoEvidence} evaluationSupported={evaluationSupported} evaluationUnresolved={evaluationUnresolved} evaluationConflicting={evaluationConflicting} gateNoEvidence={gateNoEvidence} gateSupported={gateSupported} gateUnresolved={gateUnresolved} gateConflicting={gateConflicting} adjustmentNoEvidence={adjustmentNoEvidence} adjustmentSupported={adjustmentSupported} adjustmentUnresolved={adjustmentUnresolved} adjustmentConflicting={adjustmentConflicting} adjustmentKindIdentity={adjustmentKindIdentity} adjustmentKindEpisode={adjustmentKindEpisode} adjustmentKindUnresolved={adjustmentKindUnresolved} adjustmentKindBlockedNormalized={normalizedKindBlocked} adjustmentStrengthIdentity={adjustmentStrengthIdentity} adjustmentStrengthEpisode={adjustmentStrengthEpisode} adjustmentStrengthUnresolved={adjustmentStrengthUnresolved} adjustmentStrengthBlocked={adjustmentStrengthBlocked} shadowAdjustmentValueIdentity={shadowAdjustmentValueIdentity:0.00} shadowAdjustmentValueEpisode={shadowAdjustmentValueEpisode:0.00} shadowAdjustmentValueUnresolved={shadowAdjustmentValueUnresolved:0.0} shadowAdjustmentValueBlocked={shadowAdjustmentValueBlocked:0.0} rawCoordinateNonZero={rawCoordinateSweep.NonZeroCandidates} rawCoordinateDisplayChanged={rawCoordinateSweep.DisplayScoreChangedCandidates} rawCoordinateRankChanged={rawCoordinateSweep.RankChangedCandidates} rawCoordinateDetailCount={rawCoordinateDetail.Length} reachabilitySearchConfirmed={reachabilitySearchConfirmed} reachabilityAlias={reachabilityAlias} reachabilityEpisode={reachabilityEpisode} reachabilityNoEvidence={reachabilityNoEvidence} reachabilityNoFollowup={reachabilityNoFollowup} strongLocalSequenceSearchQuery={strongLocalSequenceSearchQuery} ambiguousSearchQuery={ambiguousSearchQuery} likelyYearSearchQuery={likelyYearSearchQuery} adjustmentStrengthPolicy=identity_weak_episode_moderate_relation_weak_reserved shadowAdjustmentValuePolicy=raw_coordinate_applied_weak_0.25_moderate_0.75_supported_only adjustmentRelationPolicy=reserved_until_relation_specific_supported_reason adjustmentValue={adjustmentValue:0.00} cacheWrite=False recommendationMutation=True identityRewrite=False scoreRewrite=True reasonRewrite=False orderingRewrite=score_derived network=False budgetDelta=0");
    }

    private static void LogExternalEvidenceCacheRelationState(string phase)
    {
        AIrhythmExternalEvidence[] snapshot;
        AIrhythmExternalEvidenceSummaryProjection[] projectionSnapshot;
        lock (Gate)
        {
            snapshot = ExternalEvidenceCache.Values.ToArray();
            projectionSnapshot = ExternalEvidenceSummaryByEvent.Values.ToArray();
        }

        var leading = snapshot
            .Where(item => item.Relation.StartsWith("candidate:container_work_", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var diversified = leading.Count(item => string.Equals(item.Relation, "candidate:container_work_diversified", StringComparison.OrdinalIgnoreCase));
        var repeated = leading.Count(item => string.Equals(item.Relation, "candidate:container_work_repeated", StringComparison.OrdinalIgnoreCase));
        var unconfirmed = leading.Count(item => string.Equals(item.Relation, "candidate:container_work_unconfirmed", StringComparison.OrdinalIgnoreCase));
        var derived = snapshot
            .Where(item => item.Relation.StartsWith("related:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var promo = derived.Count(item => string.Equals(item.Relation, "related:promo", StringComparison.OrdinalIgnoreCase));
        var preview = derived.Count(item => string.Equals(item.Relation, "related:preview", StringComparison.OrdinalIgnoreCase));
        var recap = derived.Count(item => string.Equals(item.Relation, "related:recap", StringComparison.OrdinalIgnoreCase));
        var numeric = snapshot
            .Where(item => item.Relation.StartsWith("candidate:numeric_", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var numericLikelyYear = numeric.Count(item => string.Equals(item.Relation, "candidate:numeric_likely_year", StringComparison.OrdinalIgnoreCase));
        var numericLocalSequence = numeric.Count(item => string.Equals(item.Relation, "candidate:numeric_local_sequence", StringComparison.OrdinalIgnoreCase));
        var numericAmbiguous = numeric.Count(item => string.Equals(item.Relation, "candidate:numeric_ambiguous", StringComparison.OrdinalIgnoreCase));
        var numericUnconfirmed = numeric.Count(item => string.Equals(item.Relation, "candidate:numeric_unconfirmed", StringComparison.OrdinalIgnoreCase));
        var verdictSupported = snapshot.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported);
        var verdictUnresolved = snapshot.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Unresolved);
        var verdictConflicting = snapshot.Count(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Conflicting);
        var verdictReasonCanonicalTitle = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch);
        var verdictReasonAlias = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.AliasMatch);
        var verdictReasonProviderConfidence = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.ProviderConfidence);
        var verdictReasonEpisodeMatch = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch);
        var verdictReasonEpisodeConflict = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.EpisodeNumberConflict);
        var verdictReasonInsufficient = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.InsufficientEvidence);
        var verdictReasonNotApplicable = snapshot.Count(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.NotApplicable);
        var sample = snapshot
            .Where(item => item.Relation.StartsWith("candidate:container_work_", StringComparison.OrdinalIgnoreCase)
                || item.Relation.StartsWith("candidate:numeric_", StringComparison.OrdinalIgnoreCase)
                || item.Relation.StartsWith("related:", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.AcquiredAt)
            .FirstOrDefault();
        var projectedNoEvidence = projectionSnapshot.Count(item => item.Summary.Status == AIrhythmExternalEvidenceSummaryStatus.NoEvidence);
        var projectedUnresolved = projectionSnapshot.Count(item => item.Summary.Status == AIrhythmExternalEvidenceSummaryStatus.UnresolvedOnly);
        var projectedSupported = projectionSnapshot.Count(item => item.Summary.Status == AIrhythmExternalEvidenceSummaryStatus.Supported);
        var projectedConflicting = projectionSnapshot.Count(item => item.Summary.Status == AIrhythmExternalEvidenceSummaryStatus.Conflicting);
        var projectedAdjustmentKindNone = projectionSnapshot.Count(item => item.AdjustmentKind == AIrhythmExternalEvidenceAdjustmentKind.None);
        var projectedAdjustmentKindIdentity = projectionSnapshot.Count(item => item.AdjustmentKind == AIrhythmExternalEvidenceAdjustmentKind.IdentitySupport);
        var projectedAdjustmentKindEpisode = projectionSnapshot.Count(item => item.AdjustmentKind == AIrhythmExternalEvidenceAdjustmentKind.EpisodeSupport);
        var projectedAdjustmentKindRelation = projectionSnapshot.Count(item => item.AdjustmentKind == AIrhythmExternalEvidenceAdjustmentKind.RelationSupport);

        WriteDeveloperLog($"external evidence cache relation phase={phase} total={snapshot.Length} leadingRelations={leading.Length} diversified={diversified} repeated={repeated} unconfirmed={unconfirmed} derivedRelations={derived.Length} promo={promo} preview={preview} recap={recap} numericRelations={numeric.Length} numericLikelyYear={numericLikelyYear} numericLocalSequence={numericLocalSequence} numericAmbiguous={numericAmbiguous} numericUnconfirmed={numericUnconfirmed} verdictSupported={verdictSupported} verdictUnresolved={verdictUnresolved} verdictConflicting={verdictConflicting} verdictReasonCanonicalTitle={verdictReasonCanonicalTitle} verdictReasonAlias={verdictReasonAlias} verdictReasonProviderConfidence={verdictReasonProviderConfidence} verdictReasonEpisodeMatch={verdictReasonEpisodeMatch} verdictReasonEpisodeConflict={verdictReasonEpisodeConflict} verdictReasonInsufficient={verdictReasonInsufficient} verdictReasonNotApplicable={verdictReasonNotApplicable} projectionTotal={projectionSnapshot.Length} projectionNoEvidence={projectedNoEvidence} projectionUnresolved={projectedUnresolved} projectionSupported={projectedSupported} projectionConflicting={projectedConflicting} projectionAdjustmentKindNone={projectedAdjustmentKindNone} projectionAdjustmentKindIdentity={projectedAdjustmentKindIdentity} projectionAdjustmentKindEpisode={projectedAdjustmentKindEpisode} projectionAdjustmentKindRelation={projectedAdjustmentKindRelation} sampleProvider={sample?.ProviderId ?? string.Empty} sampleEntity={sample?.EntityId ?? string.Empty} sampleTitle={sample?.CanonicalTitle ?? string.Empty} sampleRelation={sample?.Relation ?? string.Empty}");
    }

    private static void AddExternalLookupTrace(
        string sourceTitle,
        string queryTitle,
        AIrhythmExternalEvidenceResult result,
        IReadOnlyList<AIrhythmExternalEvidence> evidence)
    {
        var titles = evidence
            .Select(item => item.CanonicalTitle)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        lock (Gate)
        {
            ExternalLookupTraces.Insert(0, new AIrhythmExternalLookupTrace(
                DateTimeOffset.Now, sourceTitle, queryTitle, result.ProviderId, result.Operation, result.Code, titles));
            if (ExternalLookupTraces.Count > ExternalLookupTraceLimit)
                ExternalLookupTraces.RemoveRange(ExternalLookupTraceLimit, ExternalLookupTraces.Count - ExternalLookupTraceLimit);
        }
    }


#endif

    private static int GetLeadingContainerProbePriority(
        AIrhythmExternalEvidenceNeedReason reason,
        string query,
        AIrhythmLeadingContainerParts parts,
        AIrhythmLeadingContainerLocalContext localContext)
    {
        if (reason != AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate)
            return 0;

        // Keep the local evidence two-level: Repeated means the same leading container recurs on
        // the service, while Diversified additionally proves that it wraps multiple distinct work
        // candidates. Only the stronger Diversified evidence may raise bounded external-probe
        // priority. Repeated-only remains diagnostic/downstream evidence and never rewrites identity.
        var repetitionBoost = localContext.IsDiversified ? 25 : 0;

        if (!string.IsNullOrWhiteSpace(parts.WorkCandidate)
            && string.Equals(query, parts.WorkCandidate, StringComparison.OrdinalIgnoreCase))
            return 100 + repetitionBoost;

        if (!string.IsNullOrWhiteSpace(parts.EmbeddedTopic)
            && string.Equals(query, parts.EmbeddedTopic, StringComparison.OrdinalIgnoreCase))
            return -40 + repetitionBoost;

        return repetitionBoost;
    }

    private static IReadOnlyDictionary<string, AIrhythmLeadingContainerLocalContext> BuildLeadingContainerLocalContexts(
        IReadOnlyList<TvAirProgramEventDto> events)
    {
        var groups = new Dictionary<string, (int EventCount, HashSet<string> Works)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events)
        {
            var service = new AIrhythmServiceIdentity(item.NetworkId, item.TransportStreamId, item.ServiceId);
            if (!service.IsValid)
                continue;

            var parts = AIrhythmRecommendationEngine.ParseLeadingContainerParts(item.Title);
            if (string.IsNullOrWhiteSpace(parts.Container) || string.IsNullOrWhiteSpace(parts.WorkCandidate))
                continue;

            var key = $"{service}|{parts.Container}";
            if (!groups.TryGetValue(key, out var group))
                group = (0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            group.EventCount++;
            group.Works.Add(parts.WorkCandidate);
            groups[key] = group;
        }

        return groups.ToDictionary(
            pair => pair.Key,
            pair => new AIrhythmLeadingContainerLocalContext(
                pair.Value.EventCount,
                pair.Value.Works.Count,
                pair.Value.EventCount >= 2 && pair.Value.Works.Count >= 2
                    ? AIrhythmLeadingContainerEvidenceLevel.Diversified
                    : pair.Value.EventCount >= 2
                        ? AIrhythmLeadingContainerEvidenceLevel.Repeated
                        : AIrhythmLeadingContainerEvidenceLevel.None),
            StringComparer.OrdinalIgnoreCase);
    }

    private static AIrhythmLeadingContainerLocalContext GetLeadingContainerLocalContext(
        TvAirProgramEventDto item,
        IReadOnlyDictionary<string, AIrhythmLeadingContainerLocalContext> contexts)
    {
        var service = new AIrhythmServiceIdentity(item.NetworkId, item.TransportStreamId, item.ServiceId);
        var parts = AIrhythmRecommendationEngine.ParseLeadingContainerParts(item.Title);
        if (!service.IsValid || string.IsNullOrWhiteSpace(parts.Container))
            return default;

        return contexts.TryGetValue($"{service}|{parts.Container}", out var context) ? context : default;
    }

    private static string BuildLeadingContainerProbeKey(string? sourceTitle)
    {
        if (string.IsNullOrWhiteSpace(sourceTitle))
            return string.Empty;

        // Use the same title-preprocessing boundary as EvaluateExternalEvidenceNeed.
        // Pure ARIB/broadcast attributes (e.g. [映][初]) may legally precede a
        // leading container. They must not make the grouping key disagree with
        // the ambiguity detector and explode one container class into per-title probes.
        var normalized = AIrhythmRecommendationEngine.StripNonIdentityBroadcastAnnotations(
            sourceTitle.Normalize(NormalizationForm.FormKC)).Trim();
        var match = Regex.Match(normalized, @"^【([^【】]{2,})】\s*\S+");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static int GetNumericParenthesizedProbePriority(
        AIrhythmExternalEvidenceNeedReason reason,
        AIrhythmNumericParenthesizedClass numericClass,
        AIrhythmNumericParenthesizedProbeContext context)
    {
        if (reason != AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix)
            return 0;

        // External lookup is bounded, so rank by information value rather than treating every
        // unresolved parenthesized number alike. This does not decide identity: local siblings
        // only make a provider answer more useful for resolving the relation. A lone number
        // remains ambiguous, but should not monopolize the four-call budget merely for being lone.
        var score = numericClass switch
        {
            AIrhythmNumericParenthesizedClass.LikelyYear => 20,
            AIrhythmNumericParenthesizedClass.LocalSequence => 55,
            _ => 65
        };

        if (numericClass == AIrhythmNumericParenthesizedClass.Ambiguous)
        {
            if (context.DistinctLocalValues >= 2)
                score += 30;
            else
                score -= 20;

            if (context.NearestLocalDistance is > 0 and <= 2)
                score += 15;

            if (context.Value is > 0 and <= 99)
                score += 10;
            else if (context.Value > 999)
                score -= 15;
        }

        return score;
    }

    private static int GetExternalLookupProbePriority(string? sourceTitle, string query)
    {
        var source = (sourceTitle ?? string.Empty).Normalize(NormalizationForm.FormKC);
        var value = (query ?? string.Empty).Trim();
        if (value.Length < 2)
            return int.MinValue;

        var score = 0;

        // Japanese broadcast containers often quote the actual work title.
        if (Regex.IsMatch(source, @"[「『][^」』]{2,80}[」』]")
            && !Regex.IsMatch(value, @"[「『」』]"))
            score += 120;

        // Compact work-level strings are useful broad probes for external catalogues.
        if (value.Length <= 12)
            score += 60;
        else if (value.Length <= 24)
            score += 40;
        else if (value.Length <= 48)
            score += 15;

        // International/romanized catalogue names are often indexed as-is.
        if (Regex.IsMatch(value, @"[A-Za-z]"))
            score += 15;

        // Prefer cleaned forms over strings that still contain broadcast wrappers.
        if (!Regex.IsMatch(value, @"^【") && !Regex.IsMatch(value, @"(?:PR|ＰＲ|予告|みどころ|総集編)$", RegexOptions.IgnoreCase))
            score += 20;

        // Episode markers are useful as source evidence, but search itself should start at work level.
        if (Regex.IsMatch(value, @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃]\s*[0-9０-９]+)", RegexOptions.IgnoreCase))
            score -= 25;

        return score;
    }

    private static (bool ShouldAttempt, string SkipReason, long AgeSeconds) GetExternalLookupAttemptDecision(string queryTitle)
    {
        lock (Gate)
        {
            if (!ExternalLookupAttempts.TryGetValue(queryTitle, out var attemptedAt))
                return (true, "none", -1);

            var age = DateTimeOffset.Now - attemptedAt;
            if (age >= ExternalLookupAttemptTtl)
                return (true, "ttl_expired", Math.Max(0L, (long)age.TotalSeconds));

            return (false, "attempt_ttl", Math.Max(0L, (long)age.TotalSeconds));
        }
    }

    private static void MarkExternalLookupAttempt(string queryTitle, DateTimeOffset attemptedAt)
    {
        lock (Gate)
        {
            ExternalLookupAttempts[queryTitle] = attemptedAt;
            if (ExternalLookupAttempts.Count <= ExternalEvidenceCacheLimit)
                return;
            foreach (var key in ExternalLookupAttempts.OrderBy(pair => pair.Value).Take(ExternalLookupAttempts.Count - ExternalEvidenceCacheLimit).Select(pair => pair.Key).ToArray())
                ExternalLookupAttempts.Remove(key);
        }
    }

    internal static AIrhythmExternalEvidenceAdjustmentKind DetermineExternalEvidenceAdjustmentKind(IReadOnlyList<AIrhythmExternalEvidence> evidence)
    {
        var supported = evidence
            .Where(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported)
            .ToArray();
        if (supported.Length == 0)
            return AIrhythmExternalEvidenceAdjustmentKind.None;

        if (supported.Any(item => item.VerdictReason == AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch))
            return AIrhythmExternalEvidenceAdjustmentKind.EpisodeSupport;

        if (supported.Any(item => item.VerdictReason is AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch
            or AIrhythmExternalEvidenceVerdictReason.AliasMatch
            or AIrhythmExternalEvidenceVerdictReason.ProviderConfidence))
            return AIrhythmExternalEvidenceAdjustmentKind.IdentitySupport;

        // RelationSupport is reserved for a future relation-specific Supported reason.
        // Existing title/alias/provider support must not be reinterpreted as proof of the relation itself.
        return AIrhythmExternalEvidenceAdjustmentKind.None;
    }

    internal static AIrhythmExternalEvidenceVerdictReason DetermineExternalEvidenceSupportingVerdictReason(IReadOnlyList<AIrhythmExternalEvidence> evidence)
    {
        var supportedReasons = evidence
            .Where(item => item.Verdict == AIrhythmExternalEvidenceVerdict.Supported)
            .Select(item => item.VerdictReason)
            .ToArray();
        if (supportedReasons.Contains(AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch))
            return AIrhythmExternalEvidenceVerdictReason.EpisodeNumberMatch;
        if (supportedReasons.Contains(AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch))
            return AIrhythmExternalEvidenceVerdictReason.CanonicalTitleMatch;
        if (supportedReasons.Contains(AIrhythmExternalEvidenceVerdictReason.AliasMatch))
            return AIrhythmExternalEvidenceVerdictReason.AliasMatch;
        if (supportedReasons.Contains(AIrhythmExternalEvidenceVerdictReason.ProviderConfidence))
            return AIrhythmExternalEvidenceVerdictReason.ProviderConfidence;
        return AIrhythmExternalEvidenceVerdictReason.NotApplicable;
    }

    public static long GetExternalEvidenceGeneration()
    {
        lock (Gate) return _externalEvidenceGeneration;
    }

    public static IReadOnlyDictionary<string, string> GetExternalPreEvaluationCanonicalTitles()
    {
        var now = DateTimeOffset.Now;
        lock (Gate)
        {
            var expiredKeys = ExternalCanonicalTitleBySource
                .Where(pair => now - pair.Value.UpdatedAt >= ExternalEvidenceTtl)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in expiredKeys)
                ExternalCanonicalTitleBySource.Remove(key);
            if (expiredKeys.Length > 0)
                _externalEvidenceGeneration++;

            return ExternalCanonicalTitleBySource.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.CanonicalTitle,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public static (AIrhythmExternalEvidenceSummaryStatus Summary, AIrhythmExternalEvidenceAdjustmentKind AdjustmentKind, AIrhythmExternalEvidenceVerdictReason SupportingVerdictReason) GetExternalEvidenceProjection(TvAirProgramEventDto item)
    {
        var key = ExternalEvidenceEventKey(item.NetworkId, item.TransportStreamId, item.ServiceId, item.EventNumber, item.Start);
        lock (Gate)
        {
            if (!ExternalEvidenceSummaryByEvent.TryGetValue(key, out var projection))
                return (AIrhythmExternalEvidenceSummaryStatus.NoEvidence, AIrhythmExternalEvidenceAdjustmentKind.None, AIrhythmExternalEvidenceVerdictReason.NotApplicable);
            if (DateTimeOffset.Now - projection.UpdatedAt >= ExternalEvidenceTtl)
            {
                ExternalEvidenceSummaryByEvent.Remove(key);
                _externalEvidenceGeneration++;
                return (AIrhythmExternalEvidenceSummaryStatus.NoEvidence, AIrhythmExternalEvidenceAdjustmentKind.None, AIrhythmExternalEvidenceVerdictReason.NotApplicable);
            }
            return (projection.Summary.Status, projection.AdjustmentKind, projection.SupportingVerdictReason);
        }
    }

    private static void SetExternalEvidenceSummaryProjection(
        TvAirProgramEventDto item,
        AIrhythmExternalEvidenceSummary summary,
        AIrhythmExternalEvidenceAdjustmentKind adjustmentKind,
        AIrhythmExternalEvidenceVerdictReason supportingVerdictReason,
        DateTimeOffset now,
        IReadOnlyList<AIrhythmExternalEvidence>? evidence = null)
    {
        var key = ExternalEvidenceEventKey(item.NetworkId, item.TransportStreamId, item.ServiceId, item.EventNumber, item.Start);
        lock (Gate)
        {
            ExternalEvidenceSummaryByEvent[key] = new AIrhythmExternalEvidenceSummaryProjection(summary, adjustmentKind, supportingVerdictReason, now);
            if (summary.Status == AIrhythmExternalEvidenceSummaryStatus.Supported && evidence is not null)
            {
                var supportedTitles = evidence
                    .Where(x => x.Verdict == AIrhythmExternalEvidenceVerdict.Supported && !string.IsNullOrWhiteSpace(x.CanonicalTitle))
                    .Select(x => x.CanonicalTitle.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (supportedTitles.Length == 1)
                {
                    var sourceKey = AIrhythmRecommendationEngine.ExternalPreEvaluationTitleKey(item.Title);
                    if (sourceKey.Length > 0)
                        ExternalCanonicalTitleBySource[sourceKey] = (supportedTitles[0], now);
                }
                if (ExternalCanonicalTitleBySource.Count > ExternalEvidenceCacheLimit)
                {
                    foreach (var staleKey in ExternalCanonicalTitleBySource
                        .OrderBy(pair => pair.Value.UpdatedAt)
                        .Take(ExternalCanonicalTitleBySource.Count - ExternalEvidenceCacheLimit)
                        .Select(pair => pair.Key)
                        .ToArray())
                        ExternalCanonicalTitleBySource.Remove(staleKey);
                }
            }
            if (ExternalEvidenceSummaryByEvent.Count > ExternalEvidenceCacheLimit)
            {
                foreach (var staleKey in ExternalEvidenceSummaryByEvent
                    .OrderBy(pair => pair.Value.UpdatedAt)
                    .Take(ExternalEvidenceSummaryByEvent.Count - ExternalEvidenceCacheLimit)
                    .Select(pair => pair.Key)
                    .ToArray())
                    ExternalEvidenceSummaryByEvent.Remove(staleKey);
            }
            _externalEvidenceGeneration++;
        }
    }

    private static string ExternalEvidenceEventKey(int networkId, int transportStreamId, int serviceId, int eventNumber, DateTimeOffset start)
        => $"{networkId}:{transportStreamId}:{serviceId}:{eventNumber}:{start.UtcDateTime.Ticks}";

    private static void AddExternalEvidence(IEnumerable<AIrhythmExternalEvidence> items)
    {
        lock (Gate)
        {
            foreach (var item in items)
            {
                var key = $"{item.ProviderId}|{item.EntityId}|{item.Season?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{item.Episode?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{item.CanonicalTitle}";
                ExternalEvidenceCache[key] = item;
            }
            if (ExternalEvidenceCache.Count > ExternalEvidenceCacheLimit)
            {
                foreach (var key in ExternalEvidenceCache.OrderBy(pair => pair.Value.AcquiredAt).Take(ExternalEvidenceCache.Count - ExternalEvidenceCacheLimit).Select(pair => pair.Key).ToArray())
                    ExternalEvidenceCache.Remove(key);
            }
        }
    }

    private static void PruneExternalEvidenceState(DateTimeOffset now)
    {
        lock (Gate)
        {
            foreach (var key in ExternalEvidenceCache.Where(pair => now - pair.Value.AcquiredAt >= ExternalEvidenceTtl).Select(pair => pair.Key).ToArray())
                ExternalEvidenceCache.Remove(key);
            var expiredProjectionKeys = ExternalEvidenceSummaryByEvent.Where(pair => now - pair.Value.UpdatedAt >= ExternalEvidenceTtl).Select(pair => pair.Key).ToArray();
            foreach (var key in expiredProjectionKeys)
                ExternalEvidenceSummaryByEvent.Remove(key);
            var expiredCanonicalTitleKeys = ExternalCanonicalTitleBySource.Where(pair => now - pair.Value.UpdatedAt >= ExternalEvidenceTtl).Select(pair => pair.Key).ToArray();
            foreach (var key in expiredCanonicalTitleKeys)
                ExternalCanonicalTitleBySource.Remove(key);
            foreach (var key in ExternalLookupAttempts.Where(pair => now - pair.Value >= ExternalLookupAttemptTtl).Select(pair => pair.Key).ToArray())
                ExternalLookupAttempts.Remove(key);
            if (expiredProjectionKeys.Length > 0 || expiredCanonicalTitleKeys.Length > 0)
                _externalEvidenceGeneration++;
        }
    }

    public static AIrhythmSaveResult SetExternalLookupEnabled(bool enabled)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null)
            return new(false, "設定を変更できませんでした");

        var current = ReadSettings(context, out var revision);
        if (current.ExternalLookupEnabled == enabled)
            return new(true, string.Empty, Changed: false);

        return SaveSettings(current with { ExternalLookupEnabled = enabled }, revision?.ToString(CultureInfo.InvariantCulture));
    }

    public static AIrhythmExternalLookupState GetExternalLookupState(bool requested)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (!requested)
            return new(false, false, false, false, 0, "外部情報は利用しません");
        if (context is null)
            return new(true, false, false, false, 0, "外部情報の利用状態を確認できません");

        try
        {
            TvAirExternalLookupCapabilityDto? capability;
            lock (Gate) capability = _externalLookupCapability;
            if (capability is null)
            {
                capability = context.ExternalLookup.GetCapability();
                lock (Gate) _externalLookupCapability = capability;
            }
            if (!capability.PluginDeclaredPermission)
                return new(true, false, capability.UserAllowed, false, capability.Providers.Count, "このバージョンでは外部情報を利用できません");
            if (!capability.UserAllowed)
                return new(true, true, false, false, capability.Providers.Count, "TvAIrの設定でAI-rhythmのインターネット接続を許可してください");
            if (capability.Providers.Count == 0)
                return new(true, true, true, false, 0, "利用は許可されています。現在利用できる外部情報サービスはありません");
            return new(true, true, true, capability.Available, capability.Providers.Count, "外部情報を利用できます");
        }
        catch
        {
            return new(true, true, false, false, 0, "外部情報の利用状態を確認できません");
        }
    }

    internal static bool IsUsefulHistory(TvAirRecordingHistoryDto item)
    {
        if (string.IsNullOrWhiteSpace(item.ProgramTitle) || !item.ResultFinalized)
            return false;
        if (item.FileCreated == false)
            return false;
        var state = $"{item.Result} {item.EndReason}";
        return !ContainsAny(state, "fail", "error", "cancel", "abort", "失敗", "取消", "中止");
    }

    private static bool ContainsAny(string value, params string[] words)
        => words.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static AIrhythmSettings ReadSettings(ITvAirPluginRuntimeContext context, out long? revision)
    {
        revision = null;
        try
        {
            var result = context.Storage.Get("settings", "main");
            if (!result.Succeeded || result.Value is null)
                return new();
            revision = result.Value.Revision;
            var json = result.Value.Value?.ToString();
            if (string.IsNullOrWhiteSpace(json))
                return new();

            var settings = JsonSerializer.Deserialize<AIrhythmSettings>(json, JsonOptions) ?? new();
            return new(Math.Clamp(settings.Limit, 10, 30), settings.Preferred ?? string.Empty, settings.Excluded ?? string.Empty, settings.ExternalLookupEnabled);
        }
        catch
        {
            return new();
        }
    }

    private static void ReportSnapshot(ITvAirPluginRuntimeContext context, AIrhythmRuntimeSnapshot snapshot)
    {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        ReportDeveloperSnapshot(context, snapshot);
#endif
    }

    private static void ReportFailure(ITvAirPluginRuntimeContext context, string phase, Exception exception)
    {
        try
        {
            context.Logs.Write(new TvAirLogWriteDto
            {
                Level = "Info",
                Category = "AI-rhythm",
                Message = $"{phase} result=ERROR type={exception.GetType().Name} message={exception.Message}"
            });
        }
        catch { }
    }

    private static void Invalidate(string eventType)
    {
        lock (Gate)
        {
            _cachedSnapshot = null;
            _cacheGeneration++;
        }
        // Release the previous snapshot/recommendation graph immediately. Do this outside Gate
        // to keep the lock order one-way (Score cache -> DataState Gate) and avoid deadlock.
        AIrhythmRecommendationEngine.InvalidateScoreResultCache();
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        LogSnapshotInvalidated(eventType);
#endif
    }

    // Runtime data changes invalidate the snapshot cache. Theme synchronization is not
    // owned by this event list; each RenderHtml call reads RuntimeUiRenderContext.ThemeContract.
    private static readonly string[] RefreshEventTypes =
    {
        "ProgramGuideUpdated",
        "ReservationAdded",
        "ReservationUpdated",
        "ReservationRemoved",
        "ReservationEnabled",
        "ReservationDisabled",
        "ReservationConflictChanged",
        "RecordingStarted",
        "RecordingCompleted",
        "RecordingFailed",
        "RecordingResultFinalized",
        "SettingsChanged",
        "PluginPermissionChanged"
    };

    private static AIrhythmRuntimeSnapshot Empty(
        string error,
        AIrhythmSettings? settings = null,
        long? revision = null,
        AIrhythmRuntimeDiagnostics? diagnostics = null)
        => new(
            false,
            error,
            Array.Empty<TvAirProgramEventDto>(),
            Array.Empty<TvAirReservationDto>(),
            Array.Empty<TvAirReservationDto>(),
            Array.Empty<TvAirRecordingHistoryDto>(),
            Array.Empty<TvAirRecordingHistoryDto>(),
            Array.Empty<TvAirServiceDto>(),
            Array.Empty<TvAirTunerStatusDto>(),
            new TvAirPlaybackProgressSnapshotDto(),
            new TvAirMediaContextSnapshotDto(),
            new TvAirContentDiscoveryResultDto(),
            new AIrhythmAdvancedSnapshot(
                Array.Empty<TvAirRecordingSessionDto>(),
                Array.Empty<TvAirRecordingInspectionResultDto>()),
            settings ?? new(),
            revision,
            diagnostics ?? new AIrhythmRuntimeDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<string>()));
}
