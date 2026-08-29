using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TvAIrPlugin;
using TvAIrPlugin.Data;
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
        var bootstrap = new
        {
            settings = snapshot.Settings
        };
        var bootstrapJson = JsonSerializer.Serialize(bootstrap, JsonOptions);
        var rhythmQuery = context.RequestQuery.TryGetValue("rhythm", out var rawRhythm)
            ? rawRhythm
            : string.Empty;
        var server = AIrhythmRecommendationEngine.Build(context, snapshot, rhythmQuery);

        html = html.Replace("<html lang=\"ja\">", $"<html lang=\"ja\" data-theme=\"{theme.Name}\" style=\"{theme.CssVariables}\">", StringComparison.Ordinal);
        html = html.Replace("<link rel=\"stylesheet\" href=\"app.css\">", $"<style>{css}</style>", StringComparison.Ordinal);
        html = html.Replace("<strong id=\"historyCount\">0</strong>", $"<strong id=\"historyCount\">{snapshot.History.Count}</strong>", StringComparison.Ordinal);
        html = html.Replace("<strong id=\"reservationCount\">0</strong>", $"<strong id=\"reservationCount\">{snapshot.Reservations.Count}</strong>", StringComparison.Ordinal);
        html = html.Replace("<strong id=\"programCount\">0</strong>", $"<strong id=\"programCount\">{snapshot.ContentDiscovery.Items.Count}</strong>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"message\" class=\"message\" hidden></div>", server.MessageHtml, StringComparison.Ordinal);
        html = html.Replace("<div id=\"dashboardCharts\" class=\"dashboard-grid\"></div>", $"<div id=\"dashboardCharts\" class=\"dashboard-grid\">{server.ChartsHtml}</div>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"discoveryHub\" class=\"discovery-hub\"></div>", $"<div id=\"discoveryHub\" class=\"discovery-hub\">{server.DiscoveryHtml}</div>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"rhythmSearch\" class=\"rhythm-search\"></div>", $"<div id=\"rhythmSearch\" class=\"rhythm-search\">{server.SearchHtml}</div>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"recommendations\" class=\"cards\"></div>", $"<div id=\"recommendations\" class=\"cards\">{server.CardsHtml}</div>", StringComparison.Ordinal);
        html = html.Replace("<div id=\"trends\" class=\"trends\"></div>", $"<div id=\"trends\" class=\"trends\">{server.TrendsHtml}</div>", StringComparison.Ordinal);
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
                PendingLabel = "標準へ戻しています",
                SuccessLabel = "標準に戻しました",
                NoChangeLabel = "標準設定です",
                FailureLabel = "標準に戻す",
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
                FailureLabel = "学習情報リセット",
                ConfirmationMessage = "学習情報をリセットしますか？",
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
        html = html.Replace("<textarea id=\"preferred\"", "<textarea id=\"preferred\" name=\"preferred\"", StringComparison.Ordinal);
        html = html.Replace("<textarea id=\"excluded\"", "<textarea id=\"excluded\" name=\"excluded\"", StringComparison.Ordinal);
        html = html.Replace("<select id=\"limit\">", "<select id=\"limit\" name=\"limit\">", StringComparison.Ordinal);
        html = html.Replace("<button id=\"save\" type=\"button\">保存</button>", $"<button id=\"save\" type=\"button\" {saveAttributes}>保存</button>", StringComparison.Ordinal);
        html = html.Replace("<button id=\"reset\" type=\"button\" class=\"secondary\">標準に戻す</button>", $"<button id=\"reset\" type=\"button\" class=\"secondary\" {resetSettingsAttributes}>標準に戻す</button>", StringComparison.Ordinal);
        html = html.Replace("<button id=\"resetLearning\" type=\"button\" class=\"learning-reset\">学習情報リセット</button>", $"<button id=\"resetLearning\" type=\"button\" class=\"learning-reset\" {resetLearningAttributes}>学習情報リセット</button></form>", StringComparison.Ordinal);

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

        if (string.Equals(operation, "addInterest", StringComparison.OrdinalIgnoreCase))
        {
            request.Payload.TryGetValue("eventId", out var eventId);
            var interestResult = AIrhythmDataState.RecordInterestSignal(AIrhythmDataState.Capture().Events, eventId ?? string.Empty);
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
        {
            AIrhythmDataState.InvalidateForAction("ManualRefresh");
            return Task.FromResult(new RuntimeUiActionResult
            {
                Succeeded = true,
                RefreshRequested = true,
                RefreshTarget = "content",
                PreserveScroll = true,
                ContentRoute = AIrhythmIdentity.Route
            });
        }

        if (string.Equals(operation, "resetLearning", StringComparison.OrdinalIgnoreCase))
        {
            var resetResult = AIrhythmDataState.ResetLearningInformation();
            return Task.FromResult(BuildFeedbackResult(
                resetResult,
                successMessage: "リセットしました",
                noChangeMessage: "リセットする情報はありません",
                failureMessage: "リセットできませんでした"));
        }

        if (string.Equals(operation, "resetSettings", StringComparison.OrdinalIgnoreCase))
        {
            var resetSettingsResult = AIrhythmDataState.SaveSettings(new AIrhythmSettings(20, string.Empty, string.Empty), expectedRevision: null);
            return Task.FromResult(BuildFeedbackResult(
                resetSettingsResult,
                successMessage: "標準に戻しました",
                noChangeMessage: "すでに標準設定です",
                failureMessage: "標準に戻せませんでした"));
        }

        if (!string.Equals(operation, "saveSettings", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(BuildFeedbackResult(new AIrhythmSaveResult(false, "操作を実行できません"), failureMessage: "操作を実行できませんでした"));

        request.Payload.TryGetValue("limit", out var limitText);
        request.Payload.TryGetValue("preferred", out var preferred);
        request.Payload.TryGetValue("excluded", out var excluded);
        request.Payload.TryGetValue("revision", out var revision);
        var limit = int.TryParse(limitText, out var parsed) ? Math.Clamp(parsed, 10, 30) : 20;
        var result = AIrhythmDataState.SaveSettings(new AIrhythmSettings(limit, preferred ?? string.Empty, excluded ?? string.Empty), revision);
        return Task.FromResult(BuildFeedbackResult(
            result,
            successMessage: "保存しました",
            noChangeMessage: "変更はありません",
            failureMessage: "保存できませんでした"));
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

        // ThemeContract is the color authority. Hard-coded values below are only for old hosts
        // that do not publish the compatible base roles. ThemeContract v2 semantic roles fall
        // back only to other Host-provided roles, never to a Plugin-owned semantic palette.
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
            PluginPermission.WritePluginStorage
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

internal sealed record AIrhythmSettings(int Limit = 20, string Preferred = "", string Excluded = "");

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
    long? SettingsRevision);
internal sealed record AIrhythmSaveResult(bool Success, string Message, bool Changed = true);
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
    double DeviationRawScore = 0.0);
internal sealed record AIrhythmServerRender(string CardsHtml, string TrendsHtml, string ChartsHtml, string DiscoveryHtml, string SearchHtml, string MessageHtml, string StatusText);

internal static partial class AIrhythmRecommendationEngine
{
    // 録画履歴から作る正規化済みの特徴量は、番組表更新や予約変更では内容が変わらない。
    // 現在の履歴と完全一致する1世代だけを保持し、タイトル分解・ジャンル正規化・シリーズ正規化の
    // 再実行を避ける。履歴が変われば全件照合で検出して置換するため、古い世代は蓄積しない。
    private static readonly object HistoryEvidenceGate = new();
    private static AIrhythmHistoryEvidenceSignature[] CachedHistoryEvidenceSignatures = Array.Empty<AIrhythmHistoryEvidenceSignature>();
    private static AIrhythmHistoryEvidenceItem[] CachedHistoryEvidence = Array.Empty<AIrhythmHistoryEvidenceItem>();

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
        string SeriesKey,
        string GenreKey,
        IReadOnlyList<string> Terms);

    private static IReadOnlyList<AIrhythmHistoryEvidenceItem> GetHistoryEvidence(IReadOnlyList<TvAirRecordingHistoryDto> history)
    {
        lock (HistoryEvidenceGate)
        {
            var previousCount = CachedHistoryEvidence.Length;
            var hadCache = CachedHistoryEvidenceSignatures.Length > 0 || CachedHistoryEvidence.Length > 0;
            var missReason = hadCache ? "history_count_changed" : "initial";

            if (CachedHistoryEvidenceSignatures.Length == history.Count && CachedHistoryEvidence.Length == history.Count)
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
                evidence[i] = new AIrhythmHistoryEvidenceItem(
                    start,
                    ServiceIdentityOf(item),
                    start.Hour,
                    EvidenceSeriesKey(item.ProgramTitle),
                    NormalizeGenre(item.Genre),
                    Tokens(item.ProgramTitle).ToArray());
            }

            CachedHistoryEvidenceSignatures = signatures;
            CachedHistoryEvidence = evidence;
            return CachedHistoryEvidence;
        }
    }

    private static AIrhythmHistoryEvidenceSignature HistoryEvidenceSignatureOf(TvAirRecordingHistoryDto item, DateTimeOffset start)
        => new(
            start.UtcTicks,
            item.NetworkId,
            item.TransportStreamId,
            item.ServiceId,
            item.ProgramTitle ?? string.Empty,
            item.Genre ?? string.Empty);

    private static bool HistoryEvidenceMatches(AIrhythmHistoryEvidenceSignature signature, TvAirRecordingHistoryDto item)
    {
        var start = item.ActualStart ?? item.Start;
        return signature.StartUtcTicks == start.UtcTicks
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
        var allRecommendations = Score(snapshot).OrderByDescending(x => x.Score).ThenBy(x => x.Start).ToArray();
        var recommendations = SelectRecommendations(allRecommendations, Math.Clamp(snapshot.Settings.Limit, 10, 30));
        var recommendationDiscoveryPool = allRecommendations
            .Where(x => x.IsConvincing || x.IsPlausibleDiscovery)
            .ToArray();
        var cards = string.Join(string.Empty, recommendations.Select(item => RenderCard(context, item, snapshot)));
        var trends = BuildTrends(snapshot);
        var charts = BuildCharts(snapshot, recommendations);
        var discovery = BuildDiscovery(context, snapshot, allRecommendations, recommendationDiscoveryPool);
        var search = BuildRhythmSearch(context, snapshot, allRecommendations, rawRhythmQuery);
        var message = recommendations.Length > 0
            ? "<div id=\"message\" class=\"message\" hidden></div>"
            : $"<div id=\"message\" class=\"message\">{AIrhythmHtml.Encode(snapshot.Ready ? "条件に合う候補がありません。" : snapshot.Error)}</div>";
        var status = snapshot.Ready
            ? $"録画実績 {snapshot.History.Count}本・おすすめ候補 {allRecommendations.Length}件・{DateTimeOffset.Now:HH:mm} 更新"
            : AIrhythmHtml.Encode(snapshot.Error);
        return new(cards, trends, charts, discovery, search, message, status);
    }

    private static AIrhythmRecommendation[] SelectRecommendations(IReadOnlyList<AIrhythmRecommendation> candidates, int limit)
    {
        var convincing = candidates.Where(x => x.IsConvincing).Take(limit).ToList();
        var discovery = candidates.Where(x => !x.IsConvincing && x.IsPlausibleDiscovery).ToArray();

        // 「納得」が十分に成立してからだけ、説明できる意外性を少量混ぜる。
        // 件数比率を固定せず、その時点の信頼できる候補密度を優先する。
        var discoveryAllowance = convincing.Count switch
        {
            < 4 => 0,
            < 8 => 1,
            < 14 => 2,
            _ => Math.Min(4, Math.Max(2, limit / 6))
        };
        discoveryAllowance = Math.Min(discoveryAllowance, discovery.Length);

        if (convincing.Count > limit - discoveryAllowance)
            convincing.RemoveRange(limit - discoveryAllowance, convincing.Count - (limit - discoveryAllowance));

        var selected = convincing
            .Concat(discovery.Take(discoveryAllowance))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Start)
            .Take(limit)
            .ToList();

        // 発見候補が不足した場合のみ、残っている納得候補で埋める。
        if (selected.Count < limit)
        {
            var selectedKeys = selected.Select(RecommendationIdentityKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected.AddRange(candidates
                .Where(x => x.IsConvincing && selectedKeys.Add(RecommendationIdentityKey(x)))
                .Take(limit - selected.Count));
        }

        return selected.ToArray();
    }

    private static string RecommendationIdentityKey(AIrhythmRecommendation item)
        => item.EventIdentity is not null
            ? $"{item.EventIdentity.NetworkId}:{item.EventIdentity.TransportStreamId}:{item.EventIdentity.ServiceId}:{item.EventIdentity.EventNumber}:{item.EventIdentity.Start.UtcTicks}"
            : $"{item.SeriesKey}|unknown-service|{item.Start.UtcTicks}";

    private static double ReservationEvidenceWeight(TvAirReservationDto value) => value.Intent switch
    {
        TvAirReservationIntent.System => 0.0,
        TvAirReservationIntent.ProgramTimeSlot => 0.35,
        TvAirReservationIntent.AutomaticSearch => 1.6,
        TvAirReservationIntent.KeywordRule => 1.6,
        TvAirReservationIntent.InteractiveProgramEvent => 1.0,
        _ => 0.75
    };

    private static IReadOnlyList<AIrhythmRecommendation> Score(AIrhythmRuntimeSnapshot snapshot)
    {
        var serviceWeights = new Dictionary<AIrhythmServiceIdentity, double>();
        var termWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var hourWeights = new Dictionary<int, double>();
        var seriesHistoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seriesReservationWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var seriesAutomatedReservationWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var historyEvidence = GetHistoryEvidence(snapshot.History);
        var genreCounts = historyEvidence
            .Select(x => x.GenreKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var genreTotal = Math.Max(1, genreCounts.Values.Sum());
        var interestSignals = AIrhythmDataState.GetInterestSignals();
        var now = DateTimeOffset.Now;
        var scored = new List<AIrhythmRecommendation>();

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
        }
        foreach (var item in snapshot.Reservations)
        {
            var reservationWeight = ReservationEvidenceWeight(item);
            if (reservationWeight <= 0) continue;
            var reservationService = ServiceIdentityOf(item);
            if (reservationService.IsValid) Add(serviceWeights, reservationService, 1.25 * reservationWeight);
            Add(hourWeights, item.Start.Hour, 1.25 * reservationWeight);
            foreach (var token in Tokens(item.ProgramTitle)) Add(termWeights, token, 1.25 * reservationWeight);
            var reservationSeriesKey = EvidenceSeriesKey(item.ProgramTitle);
            if (reservationSeriesKey.Length > 0)
            {
                Add(seriesReservationWeights, reservationSeriesKey, reservationWeight);
                if (item.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule)
                    Add(seriesAutomatedReservationWeights, reservationSeriesKey, reservationWeight);
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
            .Select(x => $"{SeriesKey(x.ProgramTitle)}|{ServiceIdentityOf(x)}"), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in snapshot.Events)
        {
            var hay = $"{item.Title} {item.Summary} {item.Detail} {item.Genre}".ToLowerInvariant();
            if (excluded.Any(x => hay.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;

            var score = 10.0;
            var reasons = new List<string>();
            var seriesKey = SeriesKey(item.Title);
            var evidenceSeriesKey = EvidenceSeriesKey(item.Title);
            var historySeriesCount = FindSeriesEvidence(seriesHistoryCounts, evidenceSeriesKey);
            var reservationSeriesWeight = FindSeriesEvidence(seriesReservationWeights, evidenceSeriesKey);
            var automatedReservationSeriesWeight = FindSeriesEvidence(seriesAutomatedReservationWeights, evidenceSeriesKey);
            var genreKey = NormalizeGenre(item.Genre);
            var genreCount = FindGenreEvidence(genreCounts, genreKey);
            var genreShare = genreCount / (double)genreTotal;
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
                reasons.Add(historySeriesCount >= 3 ? "よく録るシリーズ" : "録画した作品と近い");
            }
            else if (reservationSeriesWeight > 0)
            {
                seriesComponentScore = Math.Min(18, 6 + reservationSeriesWeight * 5);
                score += seriesComponentScore;
                reasons.Add(automatedReservationSeriesWeight > 0
                    ? "自動検索で予約する作品と近い"
                    : "予約した作品と近い");
            }

            var hits = termWeights
                .Where(x => x.Key.Length >= 2 && hay.Contains(x.Key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Value)
                .Take(3)
                .ToArray();
            if (hits.Length > 0 && !hasStrongSeriesEvidence)
            {
                termComponentScore = Math.Min(12, hits.Sum(x => Math.Min(6, x.Value * 0.75)));
                score += termComponentScore;
                reasons.Add("録画・予約の題名傾向と近い");
            }

            var itemService = ServiceIdentityOf(item);
            if (itemService.IsValid && serviceWeights.TryGetValue(itemService, out var serviceWeight))
            {
                serviceComponentScore = Math.Min(6, Math.Log2(serviceWeight + 1) * 2.4);
                score += serviceComponentScore;
                if (serviceWeight >= 5) reasons.Add("よく録る放送局");
            }
            if (hourWeights.TryGetValue(item.Start.Hour, out var hourWeight))
            {
                hourComponentScore = Math.Min(5, Math.Log2(hourWeight + 1) * 1.8);
                score += hourComponentScore;
                if (hourWeight >= 5) reasons.Add("よく録る時間帯");
            }

            var exactInterest = false;
            var interestScore = 0.0;
            foreach (var signal in interestSignals)
            {
                var ageDays = Math.Max(0, (now - signal.SelectedAt).TotalDays);
                var decay = Math.Exp(-ageDays / 180.0);
                if (SeriesMatches(seriesKey, signal.SeriesKey))
                {
                    interestScore += 18 * decay;
                    exactInterest = true;
                }
                else if (genreKey.Length > 0 && NormalizeGenre(signal.Genre) is var signalGenre && signalGenre.Length > 0 && GenreMatches(genreKey, signalGenre))
                {
                    interestScore += 5 * decay;
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
            if (hits.Length > 0) contentEvidence++;
            if (interestScore > 0) contentEvidence++;
            var contextualEvidence = 0;
            if (itemService.IsValid && serviceWeights.TryGetValue(itemService, out var trustServiceWeight) && trustServiceWeight >= 5) contextualEvidence++;
            if (hourWeights.TryGetValue(item.Start.Hour, out var trustHourWeight) && trustHourWeight >= 5) contextualEvidence++;

            // 局・時間帯は「それらしい」補助材料にはなるが、それだけで納得候補にはしない。
            // 番組内容側の根拠が複数ある、または内容根拠に利用傾向が重なる場合だけ納得側へ寄せる。
            var isConvincing = historySeriesCount > 0
                || reservationSeriesWeight >= 1.0
                || exactInterest
                || preferredHit
                || contentEvidence >= 2
                || (contentEvidence >= 1 && contextualEvidence >= 1);
            var hasContentConnection = genreCount > 0 || hits.Length > 0 || interestScore > 0 || preferredHit;
            var isPlausibleDiscovery = !isConvincing && hasContentConnection && score >= 20;

            var duplicateKey = seriesKey.Length > 0
                ? $"{seriesKey}|{itemService}"
                : $"event:{item.NetworkId}:{item.TransportStreamId}:{item.ServiceId}:{item.EventNumber}:{item.Start.UtcTicks}";
            if (!seen.Add(duplicateKey)) continue;
            // 偏差表示も既存の信頼度上限を共有し、丸めだけを行わない。
            // これにより推薦判定と表示スコアで評価基準が分岐しない。
            var deviationRawScore = Math.Min(score, confidenceCap);
            var rawScore = Math.Clamp((int)Math.Round(score), 0, confidenceCap);
            if (rawScore < 8) continue;
            var broadcast = itemService.IsValid && channelMap.TryGetValue(itemService, out var channel) ? channel.BroadcastType : string.Empty;
            var identity = new AIrhythmEventIdentity(item.NetworkId, item.TransportStreamId, item.ServiceId, item.EventNumber, item.Start);
            scored.Add(new(item.Title, item.ServiceName, broadcast, item.Genre ?? string.Empty, item.Start, rawScore, reasons.Distinct().Take(4).ToArray(), seriesKey, identity, isConvincing, isPlausibleDiscovery, rawScore, deviationRawScore));
        }

        return ApplyDeviationScores(scored);
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
        IReadOnlyList<AIrhythmRecommendation> recommendations)
    {
        var now = DateTimeOffset.Now;
        var reserved = new HashSet<string>(snapshot.Reservations.Select(x => SeriesKey(x.ProgramTitle)).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
        var recorded = new HashSet<string>(snapshot.History.Select(x => SeriesKey(x.ProgramTitle)).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
        var eventMap = snapshot.Events
            .GroupBy(EventIdentityOf)
            .ToDictionary(x => x.Key, x => x.First());

        TvAirProgramEventDto? EventOf(AIrhythmRecommendation item)
            => item.EventIdentity is not null && eventMap.TryGetValue(item.EventIdentity, out var value) ? value : null;

        bool ContainsAny(string text, params string[] markers)
            => markers.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));

        var newPrograms = recommendations
            .Select(x => (Item: x, Event: EventOf(x)))
            .Where(x => IsNewProgram(x.Item.Title, x.Event))
            .Where(x => !recorded.Contains(x.Item.SeriesKey))
            .OrderByDescending(x => x.Item.Score)
            .ThenByDescending(x => NewProgramConfidence(x.Item.Title, x.Event))
            .ThenBy(x => x.Item.Start)
            .Select(x => x.Item)
            .Take(8)
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
        var week = recommendations.Where(x => x.Start <= now.AddDays(7)).Take(6).ToArray();

        string Shelf(string css, string icon, string title, string subtitle, IReadOnlyList<AIrhythmRecommendation> items, bool showScore = true)
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
            .Select(x => new AIrhythmRecommendation(x.ProgramTitle, x.ServiceName, string.Empty, "録画中", x.Start, 0, new[] { $"{x.TunerName}で録画中", $"終了予定 {x.ScheduledEnd:HH:mm}" }, SeriesKey(x.ProgramTitle), null))
            .Take(6).ToArray();

        var qualityIssues = snapshot.RecoveryHistory
            .Where(x => x.ResultFinalized)
            .Where(x => IsRecordingFailure(x)
                || (x.QualityDataAvailable && ((x.DropCount ?? 0) > 0 || (x.ErrorCount ?? 0) > 0 || (x.ScrambleCount ?? 0) > 0)))
            .Select(x => new
            {
                SeriesKey = SeriesKey(x.ProgramTitle),
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
                    .Where(q => SeriesMatches(q.SeriesKey, x.SeriesKey))
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

        string AvailableShelf()
        {
            var items = snapshot.ContentDiscovery.Items.Take(12).ToArray();
            if (items.Length == 0)
                return "<section class='discovery-shelf discovery-shelf-empty shelf-available'><div class='shelf-title'><span class='shelf-accent' aria-hidden='true'></span><div><h3>今から見られる</h3><p>30分以内の放送中・録画済み候補</p></div></div><div class='shelf-empty'>現在候補はありません</div></section>";
            var cards = string.Join(string.Empty, items.Select((x, index) =>
            {
                var remaining = x.RemainingSeconds > 0 ? $"残り {FormatDuration(x.RemainingSeconds)}" : FormatDuration(x.TotalSeconds);
                var action = x.CanResume ? "続きから" : x.CanWatchLive ? "放送中" : x.CanPlayRecording ? "録画済み" : string.Empty;
                var meta = string.Join("・", new[] { x.ServiceName, remaining, action }.Where(v => !string.IsNullOrWhiteSpace(v)));
                var reason = DisplayDiscoveryReason(x.MatchReason);
                return $"<article class='spark-card'><div class='spark-rank'>{index + 1}</div><div class='spark-body'>{ProgramTitleElement("strong", x.Title)}<span>{AIrhythmHtml.Encode(meta)}</span><p>{AIrhythmHtml.Encode(reason)}</p></div></article>";
            }));
            return $"<section class='discovery-shelf shelf-available'><div class='shelf-title'><span class='shelf-accent' aria-hidden='true'></span><div><h3>今から見られる</h3><p>30分以内の放送中・録画済み候補</p></div></div><div class='spark-grid'>{cards}</div></section>";
        }

        return string.Concat(
            AvailableShelf(),
            Shelf("shelf-active", "REC", "いま録画中", "現在進行中の録画セッション", active, showScore: false),
            Shelf("shelf-recovery", "FIX", "取り直しチャンス", "品質情報に問題があった番組の再放送候補", recovery),
            Shelf("shelf-tonight", "✦", "今夜のピックアップ", "今から深夜4時までの高相性番組", tonight),
            Shelf("shelf-new", "NEW", "好みに刺さる新番組", "今季の新番組を録画傾向に合わせてピックアップ", newPrograms),
            Shelf("shelf-replay", "↻", "見逃し再放送レーダー", "未予約・未録画の再放送候補", replayFinds),
            Shelf("shelf-surprise", "!", "いつもと違う発見", "普段選ばない局にある好みの番組", surprise),
            Shelf("shelf-week", "7", "今週の期待作", "7日以内のおすすめ上位", week));
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


    private static string SeriesKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC);
        normalized = Regex.Replace(normalized, @"[\[【（(][^\]】）)]{0,8}[\]】）)]", " ");
        normalized = Regex.Replace(normalized, @"(?:第\s*[0-9]+\s*(?:話|回)|[#＃]\s*[0-9]+|(?:episode|ep\.?)\s*[0-9]+|[0-9]+\s*話)", " ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"(?:新番組|初回|最終回|再放送|アンコール|リピート|一挙放送)", " ", RegexOptions.IgnoreCase);
        return new string(normalized.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    // 録画・予約の「同じ作品を追っている」という継続証拠だけに使うキー。
    // 表示・重複排除・予約状態など番組個体の識別には SeriesKey を使い続け、
    // エピソード/ラウンド差を吸収したことで別回の状態まで混同しない。
    private static string EvidenceSeriesKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormKC);
        normalized = Regex.Replace(normalized, @"[\[【（(][^\]】）)]{0,8}[\]】）)]", " ");
        normalized = Regex.Replace(normalized, @"^\s*(?:19|20)[0-9]{2}(?:年)?[\s　]+", " ", RegexOptions.IgnoreCase);

        // 「第N話/回/戦」「#N」「EP N」など、明示的な回次より後ろは各回固有の副題として扱う。
        // タイトル先頭が回次の場合はシリーズ名を失わないよう、回次だけを除去する。
        var marker = Regex.Match(
            normalized,
            @"(?:第\s*[0-9０-９]+\s*(?:話|回|戦)|[#＃]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)",
            RegexOptions.IgnoreCase);
        if (marker.Success)
        {
            var prefix = normalized[..marker.Index];
            var prefixIdentityLength = prefix.Count(char.IsLetterOrDigit);
            normalized = prefixIdentityLength >= 2
                ? prefix
                : normalized.Remove(marker.Index, marker.Length);
        }

        normalized = Regex.Replace(normalized, @"(?:新番組|初回|最終回|再放送|アンコール|リピート|一挙放送)", " ", RegexOptions.IgnoreCase);
        return new string(normalized.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static bool SeriesMatches(string left, string right)
    {
        if (left.Length < 3 || right.Length < 3) return false;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        if (Math.Min(left.Length, right.Length) < 5) return false;
        return left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    internal static string CreateSeriesKey(string? title) => SeriesKey(title);

    private static string BuildRhythmSearch(
        RuntimeUiRenderContext context,
        AIrhythmRuntimeSnapshot snapshot,
        IReadOnlyList<AIrhythmRecommendation> recommendations,
        string? rawQuery)
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

        var reserved = snapshot.Reservations.Select(x => SeriesKey(x.ProgramTitle)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recorded = snapshot.History.Select(x => SeriesKey(x.ProgramTitle)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                    var selectedSeries = SeriesKey(selectedEvent.Title);
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

    private static string BuildCharts(AIrhythmRuntimeSnapshot snapshot, IReadOnlyList<AIrhythmRecommendation> recommendations)
    {
        var palette = new[] { "var(--viz-blue)", "var(--viz-green)", "var(--viz-yellow)", "var(--viz-orange)", "var(--viz-purple)", "var(--viz-cyan)", "var(--viz-amber)", "var(--viz-teal)" };

        static string EmptyChart(string message)
            => $"<div class='chart-empty'>{AIrhythmHtml.Encode(message)}</div>";

        static string BarRows(IEnumerable<(string Label, int Value)> source, string[] colors, int limit = 8, bool programTitles = false)
        {
            var rows = source.Where(x => x.Value > 0).Take(Math.Max(1, limit)).ToArray();
            if (rows.Length == 0) return string.Empty;
            var max = Math.Max(1, rows.Max(x => x.Value));
            return string.Join(string.Empty, rows.Select((x, i) =>
            {
                var label = programTitles
                    ? ProgramTitleElement("span", x.Label, "bar-label", 10)
                    : $"<span class='bar-label' title='{AIrhythmHtml.Encode(x.Label)}'>{AIrhythmHtml.Encode(x.Label)}</span>";
                return $"<div class='bar-row'>{label}<div class='bar-track'><i style='width:{Math.Max(4, x.Value * 100 / max)}%;background:{colors[i % colors.Length]}'></i></div><strong>{x.Value}</strong></div>";
            }));
        }

        static string Donut(IEnumerable<(string Label, int Value)> source, string[] colors, int limit = 6, string unit = "件", string empty = "候補がまだありません", string extraClass = "")
        {
            var values = source.Where(x => x.Value > 0).Take(Math.Max(1, limit)).ToArray();
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
            var legend = string.Join(string.Empty, values.Select((x, i) => $"<span><i style='background:{colors[i % colors.Length]}'></i><span class='legend-label' title='{AIrhythmHtml.Encode(x.Label)}'>{AIrhythmHtml.Encode(x.Label)}</span><b>{x.Value}</b></span>"));
            var donutClass = string.IsNullOrWhiteSpace(extraClass) ? "donut-wrap" : $"donut-wrap {extraClass}";
            return $"<div class='{donutClass}'><div class='donut' style='background:conic-gradient({string.Join(',', stops)})'><em>{total}</em><small>{AIrhythmHtml.Encode(unit)}</small></div><div class='legend'>{legend}</div></div>";
        }

        static string LineSvg(IEnumerable<(string Label, int Value)> source, string color)
        {
            var values = source.Where(x => x.Value > 0).ToArray();
            if (values.Length == 0) return EmptyChart("曜日別の録画情報がまだありません");
            var max = Math.Max(1, values.Max(x => x.Value));
            var pts = values.Select((x, i) => $"{20 + i * (360d / Math.Max(1, values.Length - 1)):0.#},{140 - x.Value * 110d / max:0.#}").ToArray();
            var labels = string.Join(string.Empty, values.Select((x, i) => $"<text x='{20 + i * (360d / Math.Max(1, values.Length - 1)):0.#}' y='164' text-anchor='middle'>{AIrhythmHtml.Encode(x.Label)}</text>"));
            var dots = string.Join(string.Empty, pts.Select(p =>
            {
                var xy = p.Split(',');
                return $"<circle cx='{xy[0]}' cy='{xy[1]}' r='4' fill='{color}'/>";
            }));
            return $"<svg class='line-chart' viewBox='0 0 400 175' role='img'><path d='M20 140 H380' class='axis'/><polyline points='{string.Join(" ", pts)}' fill='none' stroke='{color}' stroke-width='4' stroke-linejoin='round' stroke-linecap='round'/>{dots}{labels}</svg>";
        }

        static string VerticalBars(IEnumerable<(string Label, int Value)> source, string[] colors, string empty)
        {
            var values = source.Where(x => x.Value > 0).ToArray();
            if (values.Length == 0) return EmptyChart(empty);
            var max = Math.Max(1, values.Max(x => x.Value));
            var bars = string.Join(string.Empty, values.Select((x, i) =>
            {
                var height = Math.Max(6, x.Value * 100 / max);
                return $"<div class='vbar-item'><strong>{x.Value}</strong><div class='vbar-track'><i style='height:{height}%;background:{colors[i % colors.Length]}'></i></div><span>{AIrhythmHtml.Encode(x.Label)}</span></div>";
            }));
            return $"<div class='vertical-bars'>{bars}</div>";
        }

        static string BarBody(IEnumerable<(string Label, int Value)> rows, string[] colors, string empty, int limit = 8, string extraClass = "")
        {
            var html = BarRows(rows, colors, limit, string.Equals(extraClass, "title-ranking", StringComparison.Ordinal));
            return string.IsNullOrEmpty(html) ? EmptyChart(empty) : $"<div class='bar-chart {extraClass}'>{html}</div>";
        }

        var rawGenres = snapshot.MediaInsights.Genres
            .Where(x => x.Count > 0)
            .Select(x => (Label: string.IsNullOrWhiteSpace(x.Key) ? "その他" : x.Key, Value: x.Count))
            .OrderByDescending(x => x.Value)
            .ToArray();
        // Genre buckets are owned by MediaInsights. RecordingCount may include finalized rows that are
        // intentionally excluded from the dashboard total (failed/cancelled/no-file), so do not gate
        // genre projection by equality with the successful-recording History population.
        var genres = rawGenres;
        var titleTop = snapshot.History
            .Select(x => (Key: SeriesKey(x.ProgramTitle), Label: SeriesDisplayTitle(x.ProgramTitle)))
            .Where(x => x.Key.Length > 0 && x.Label.Length > 0)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => (Label: x.GroupBy(y => y.Label, StringComparer.OrdinalIgnoreCase).OrderByDescending(y => y.Count()).First().Key, Value: x.Count()))
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
        var waves = recommendations
            .GroupBy(x => string.IsNullOrWhiteSpace(x.BroadcastType) ? "その他" : x.BroadcastType)
            .Select(x => (Label: x.Key, Value: x.Count()))
            .OrderByDescending(x => x.Value)
            .ToArray();
        var services = snapshot.History
            .Select(x => (Identity: ServiceIdentityOf(x), FallbackName: x.ServiceName))
            .Where(x => x.Identity.IsValid)
            .GroupBy(x => x.Identity)
            .Select(x => (Label: ResolveCurrentServiceName(snapshot.Channels, x.Key, x.OrderByDescending(y => y.FallbackName.Length).First().FallbackName), Value: x.Count()))
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
        var hours = snapshot.History
            .Select(x => (x.ActualStart ?? x.Start).Hour)
            .GroupBy(hour => hour switch
            {
                >= 5 and < 10 => "朝",
                >= 10 and < 17 => "昼",
                >= 17 and < 20 => "夕方",
                >= 20 => "夜",
                _ => "深夜"
            })
            .Select(x => (Label: x.Key, Value: x.Count()))
            .OrderBy(x => x.Label switch { "朝" => 0, "昼" => 1, "夕方" => 2, "夜" => 3, _ => 4 })
            .ToArray();
        var weekdays = snapshot.History
            .Select(x => (x.ActualStart ?? x.Start).DayOfWeek)
            .GroupBy(x => x)
            .Select(x => (Label: x.Key switch
            {
                DayOfWeek.Monday => "月",
                DayOfWeek.Tuesday => "火",
                DayOfWeek.Wednesday => "水",
                DayOfWeek.Thursday => "木",
                DayOfWeek.Friday => "金",
                DayOfWeek.Saturday => "土",
                _ => "日"
            }, Value: x.Count(), Order: x.Key == DayOfWeek.Sunday ? 6 : (int)x.Key - 1))
            .OrderBy(x => x.Order)
            .Select(x => (x.Label, x.Value))
            .ToArray();
        var scores = recommendations
            .GroupBy(x => x.Score / 10 * 10)
            .OrderBy(x => x.Key)
            .Select(x => (Label: $"{x.Key}台", Value: x.Count()))
            .ToArray();

        // Wide-screen dashboard: balance by visual density rather than forcing equal card heights.
        // Column 1 keeps compact/short analyses together, column 2 owns title history + weekday rhythm,
        // and column 3 owns the two larger recording-distribution charts.
        var first = new StringBuilder();
        first.Append($"<article class='chart-card chart-compact chart-accent-blue'><div class='chart-head'><h3>録画総本数</h3><span>録画実績</span></div><div class='recording-total'><b>{snapshot.History.Count}</b><span>本</span></div></article>");
        first.Append($"<article class='chart-card chart-accent-blue'><div class='chart-head'><h3>ジャンル別内訳</h3><span>録画本数</span></div>{BarBody(genres, palette, "ジャンル別の録画情報がまだありません")}</article>");
        first.Append($"<article class='chart-card chart-accent-red'><div class='chart-head'><h3>おすすめ候補の傾向</h3><span>候補件数</span></div><div class='recommendation-trends'><section><h4>スコア分布</h4>{BarBody(scores, palette.Skip(3).Concat(palette.Take(3)).ToArray(), "おすすめ候補がまだありません", 8, "horizontal compact")}</section><section><h4>放送波</h4>{Donut(waves, palette.Skip(2).Concat(palette.Take(2)).ToArray(), 4, "件", "おすすめ候補がまだありません")}</section></div></article>");

        var second = new StringBuilder();
        second.Append($"<article class='chart-card chart-accent-purple'><div class='chart-head'><h3>タイトル上位10</h3><span>録画本数</span></div>{BarBody(titleTop, palette.Reverse().ToArray(), "タイトル情報がまだありません", 10, "title-ranking")}</article>");
        second.Append($"<article class='chart-card chart-accent-cyan'><div class='chart-head'><h3>曜日別録画傾向</h3><span>録画本数</span></div>{LineSvg(weekdays, "var(--viz-cyan)")}</article>");

        var third = new StringBuilder();
        third.Append($"<article class='chart-card chart-accent-orange'><div class='chart-head'><h3>よく録る放送局</h3><span>上位10・録画本数</span></div>{Donut(services, palette, 10, "本", "放送局別の録画情報がまだありません", "station-donut")}</article>");
        third.Append($"<article class='chart-card chart-accent-purple'><div class='chart-head'><h3>録画時間帯</h3><span>録画本数</span></div>{VerticalBars(hours, palette.Reverse().ToArray(), "時間帯別の録画情報がまだありません")}</article>");

        return $"<div class='dashboard-column'>{first}</div><div class='dashboard-column'>{second}</div><div class='dashboard-column'>{third}</div>";
    }

    private static string SeriesDisplayTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC);
        normalized = Regex.Replace(normalized, @"(?:第\s*[0-9]+\s*(?:話|回)|[#＃]\s*[0-9]+|(?:episode|ep\.?)\s*[0-9]+|[0-9]+\s*話)", " ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"(?:新番組|初回|最終回|再放送|アンコール|リピート|一挙放送)", " ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim(' ', '-', '－', ':', '：');
        return normalized;
    }

    private static string BuildTrends(AIrhythmRuntimeSnapshot snapshot)
    {
        var terms = snapshot.History.SelectMany(x => Tokens(x.ProgramTitle))
            .Concat(snapshot.Reservations.SelectMany(x => Tokens(x.ProgramTitle)))
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key);
        var services = snapshot.History.Select(x => (Identity: ServiceIdentityOf(x), FallbackName: x.ServiceName))
            .Concat(snapshot.Reservations.Select(x => (Identity: ServiceIdentityOf(x), FallbackName: x.ServiceName)))
            .Where(x => x.Identity.IsValid)
            .GroupBy(x => x.Identity)
            .OrderByDescending(x => x.Count())
            .Select(x => ResolveCurrentServiceName(snapshot.Channels, x.Key, x.First().FallbackName));
        var values = terms.Concat(services).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        return values.Length == 0
            ? "<div class=\"empty-state\">学習できるデータがまだありません。</div>"
            : string.Join(string.Empty, values.Select(x => $"<span class=\"trend\">{AIrhythmHtml.Encode(x)}</span>"));
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
            var label = !reservation.IsEnabled || ContainsAny(state, "disabled", "無効")
                ? "予約無効"
                : reservation.HasConflict
                    ? "予約済み（競合）"
                    : "予約済み";
            return $"<span class=\"{css} secondary\" data-airhythm-reservation-key=\"{reservationKey}\" aria-label=\"{label}\">{label}</span>";
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
    private static long _cacheGeneration;

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
            _cacheGeneration++;

            var available = new HashSet<string>(context.Events.ListEventTypes(), StringComparer.OrdinalIgnoreCase);
            foreach (var eventType in RefreshEventTypes)
            {
                if (!available.Contains(eventType))
                    continue;
                EventSubscriptions.Add(context.Events.Subscribe(eventType, _ => Invalidate(eventType)));
            }
        }
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (_runtimeContext is null) return;
            if (EventSubscriptions.Count == 0)
                Initialize(_runtimeContext);
        }
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
            _cacheGeneration++;
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
                return new AIrhythmSaveResult(false, failureMessage);
            }

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
                recoveryHistory = data.RecordingHistory
                    .Where(x => (x.ActualStart ?? x.Start) <= now)
                    .Where(x => !string.IsNullOrWhiteSpace(x.ProgramTitle) && x.ResultFinalized)
                    .OrderByDescending(x => x.ActualStart ?? x.Start)
                    .ToArray();
                history = recoveryHistory
                    .Where(IsUsefulHistory)
                    .ToArray();
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
            var coreFailureCount = errors.Count(x => x is "番組情報" or "予約" or "録画実績");
            var snapshot = coreFailureCount == 3
                ? Empty("番組情報を読み込めませんでした", settings, revision)
                : new AIrhythmRuntimeSnapshot(
                    true,
                    errors.Count == 0 ? string.Empty : $"{string.Join("・", errors.Distinct())}を読み込めませんでした",
                    events, reservations, reservationRecords, history, recoveryHistory, channels, tuners, playbackProgress, mediaInsights, contentDiscovery, advanced, settings, revision);

            lock (Gate)
            {
                if (ReferenceEquals(_runtimeContext, context) && _cacheGeneration == captureGeneration)
                    _cachedSnapshot = snapshot;
            }
            return snapshot;
        }
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

    public static AIrhythmSaveResult RecordInterestSignal(IReadOnlyList<TvAirProgramEventDto> events, string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 512)
            return new(false, "追加対象を確認できませんでした。");
        var selected = events.FirstOrDefault(x => string.Equals(x.EventId, eventId, StringComparison.Ordinal));
        if (selected is null)
            return new(false, "追加対象を確認できませんでした。");
        var series = AIrhythmRecommendationEngine.CreateSeriesKey(selected.Title);
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

    private static bool IsUsefulReservation(TvAirReservationDto item)
    {
        if (!item.IsEnabled || item.HasConflict || string.IsNullOrWhiteSpace(item.ProgramTitle))
            return false;
        var state = $"{item.Status} {item.Source} {item.Route}";
        return !ContainsAny(state, "cancel", "disabled", "removed", "取消", "無効", "削除");
    }

    public static void InvalidateForAction(string reason)
        => Invalidate(reason);

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

    private static bool IsUsefulHistory(TvAirRecordingHistoryDto item)
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
            return new(Math.Clamp(settings.Limit, 10, 30), settings.Preferred ?? string.Empty, settings.Excluded ?? string.Empty);
        }
        catch
        {
            return new();
        }
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
        "SettingsChanged"
    };

    private static AIrhythmRuntimeSnapshot Empty(
        string error,
        AIrhythmSettings? settings = null,
        long? revision = null)
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
            revision);
}
