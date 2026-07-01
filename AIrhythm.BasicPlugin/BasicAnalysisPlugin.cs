using System.Globalization;
using System.Net;
using System.Text;
using TvAIrPlugin;

namespace AIrhythm.BasicPlugin;

public sealed class BasicAnalysisPlugin : IAnalysisPlugin, IUiPlugin, IManifestPlugin
{
    private const string ProductVersion = "1.0.2";
    private IPluginContext? _context;
    public string Name => "AI-rhythm";
    public string Version => ProductVersion;

    public PluginManifest Manifest { get; } = new()
    {
        Id = "airhythm.basic",
        Name = "AI-rhythm",
        Version = ProductVersion,
        Route = "airhythm",
        DefaultRoute = "airhythm",
        Entry = "AIrhythm.BasicPlugin.BasicAnalysisPlugin",
        Description = "予約・番組表分析プラグイン",
        Vendor = "AI-rhythm Plugin Team",
        HostContractVersion = TvAIrPluginSdkContract.HostContractVersion,
        SdkVersion = TvAIrPluginSdkContract.SdkVersion,
        Capabilities = new[] { "ShowUi", "OpenPage", "ReadEpg", "ReadReservations" },
        Tags = new[] { "official", "analysis", "page" },
        PreferredOpenMode = "page",
        DefaultMenuActionKind = "page",
        DefaultMenuActionLabel = "AI-rhythm",
        DefaultMenuActionPriority = 300,
        Kind = new[] { "Analysis", "UI" },
        Permissions = new[] { PluginPermission.ShowUi, PluginPermission.OpenPage, PluginPermission.ReadEpg, PluginPermission.ReadReservations }
    };

    public PluginUiDescriptor Ui { get; } = new()
    {
        RouteSegment = "airhythm",
        MenuText = "AI-rhythm",
        Description = "AI-rhythm",
        Capabilities = new[] { "ShowUi", "OpenPage", "ReadEpg", "ReadReservations" },
        PreferredOpenMode = "page",
        DefaultMenuActionKind = "page",
        DefaultMenuActionLabel = "AI-rhythm",
        DefaultMenuActionPriority = 300
    };

    public void Initialize(IPluginContext context)
    {
        _context = context;
        SafeLog("Initialize completed.");
    }

    public void OnStart() => SafeLog("OnStart completed.");
    public void OnStop() => SafeLog("OnStop completed.");

    public AnalysisResult Analyze(AnalysisContext context)
    {
        try
        {
            var snapshot = ReadSnapshot();
            var reservations = snapshot.Reservations.Count > 0
                ? snapshot.Reservations.Select(r => new AnalysisReservationInfo
                {
                    Id = r.Id,
                    Title = r.Title,
                    ServiceName = r.ServiceName,
                    Source = r.Source,
                    IsEnabled = r.IsEnabled,
                    IsConflicted = r.IsConflicted,
                    StartTime = r.StartTime,
                    EndTime = r.EndTime
                }).ToArray()
                : context.Reservations ?? Array.Empty<AnalysisReservationInfo>();

            var programs = snapshot.Epg.Count > 0
                ? snapshot.Epg.Select(e => new AnalysisProgramInfo
                {
                    Title = e.Title,
                    ServiceName = e.ServiceName,
                    Genre = e.Genre,
                    Description = e.Description,
                    StartTime = e.StartTime,
                    EndTime = e.EndTime
                }).ToArray()
                : context.Programs ?? Array.Empty<AnalysisProgramInfo>();

            var enabledReservations = reservations.Count(r => r.IsEnabled);
            var conflictedReservations = reservations.Count(r => r.IsConflicted);
            var eveningReservations = reservations.Count(r => r.StartTime.Hour >= 18 || r.StartTime.Hour < 4);
            var favoriteGenre = DetectFavoriteGenre(reservations, programs);
            var favoriteTimeBand = eveningReservations >= Math.Max(1, enabledReservations / 3) ? "夜帯" : "通常帯";

            var evidenceScore = ClampPercent(programs.Count >= 5 ? 80 : programs.Count * 16);
            var densityScore = ClampPercent(enabledReservations >= 100 ? 100 : enabledReservations);
            var safetyScore = context.IsClosedNetwork ? 100 : 45;
            var timeScore = favoriteTimeBand == "夜帯" ? 100 : 55;
            var keywordScore = GuessKeywordScore(reservations, programs);
            var genreScore = favoriteGenre.Length > 0 ? 70 : 0;
            var performerScore = GuessPerformerScore(programs);
            var confidence = ClampPercent((evidenceScore + safetyScore + timeScore) / 3);
            var recommend = ClampPercent((keywordScore * 0.35) + (genreScore * 0.15) + (timeScore * 0.20) + (evidenceScore * 0.20) + (safetyScore * 0.10) - conflictedReservations * 3);

            return new AnalysisResult
            {
                PluginName = Name,
                PluginVersion = Version,
                Score = recommend,
                Summary = $"{DisplayUser(context.UserNickname)}、番組表 {programs.Count} 件、予約 {reservations.Count} 件からおすすめを作成しました。注目傾向は {favoriteGenre.DefaultIfBlank("未確定")} / {favoriteTimeBand} です。",
                Reasons = BuildReasons(enabledReservations, conflictedReservations, favoriteGenre, favoriteTimeBand, keywordScore, evidenceScore, confidence, snapshot.SourceNote),
                Metrics = new[]
                {
                    new AnalysisMetric { Label = "おすすめ度", Value = recommend, Unit = "%" },
                    new AnalysisMetric { Label = "信頼度", Value = confidence, Unit = "%" },
                    new AnalysisMetric { Label = "傾向密度", Value = densityScore, Unit = "%" },
                    new AnalysisMetric { Label = "説明材料", Value = evidenceScore, Unit = "%" },
                    new AnalysisMetric { Label = "ジャンル", Value = genreScore, Unit = "%" },
                    new AnalysisMetric { Label = "キーワード", Value = keywordScore, Unit = "%" },
                    new AnalysisMetric { Label = "出演者", Value = performerScore, Unit = "%" },
                    new AnalysisMetric { Label = "時間帯", Value = timeScore, Unit = "%" },
                    new AnalysisMetric { Label = "閉域安全", Value = safetyScore, Unit = "%" },
                }
            };
        }
        catch (Exception ex)
        {
            SafeLog("Analyze failed: " + ex.Message);
            return new AnalysisResult
            {
                PluginName = Name,
                PluginVersion = Version,
                Score = 0,
                Summary = "AI-rhythm の分析中に問題が発生しました。",
                Reasons = new[] { ex.Message },
                Metrics = Array.Empty<AnalysisMetric>()
            };
        }
    }

    public string RenderHtml(PluginUiContext context)
    {
        try
        {
            AirhythmLocalSettings.UpdateFromQuery(context);
            return BuildDashboardHtml(context, ReadSnapshot(), _context);
        }
        catch (Exception ex)
        {
            SafeLog("RenderHtml failed: " + ex.Message);
            return "<main class=\"air-dashboard\"><h1>AI-rhythm</h1><p>表示できませんでした。</p></main>";
        }
    }

    private DashboardSnapshot ReadSnapshot()
    {
        try
        {
            if (_context is null)
            {
                return new DashboardSnapshot(Array.Empty<ApiEpgEvent>(), Array.Empty<ApiReservation>(), "番組情報がありません");
            }

            var epg = _context.GetEpg(new PluginEpgQuery { Days = 7, Limit = 2000 })
                .Select(ApiEpgEvent.FromPlugin)
                .ToArray();
            var reservations = _context.GetReservations(new PluginReservationQuery())
                .Select(ApiReservation.FromPlugin)
                .ToArray();
            return new DashboardSnapshot(epg, reservations, "取得済み");
        }
        catch (Exception ex)
        {
            SafeLog("ReadSnapshot failed: " + ex.Message);
            return new DashboardSnapshot(Array.Empty<ApiEpgEvent>(), Array.Empty<ApiReservation>(), "番組情報がありません");
        }
    }

    private static string BuildDashboardHtml(PluginUiContext context, DashboardSnapshot snapshot, IPluginContext? pluginContext)
    {
        _ = context;
        _ = pluginContext;
        var settings = AirhythmLocalSettings.Load();
        var epgCount = snapshot.Epg.Count;
        var reservationCount = snapshot.Reservations.Count;
        var conflictCount = snapshot.Reservations.Count(r => r.IsConflicted);
        var activeReservationCount = snapshot.Reservations.Count(r => r.IsEnabled);
        var genreTop = snapshot.Epg.Where(e => !string.IsNullOrWhiteSpace(e.Genre)).GroupBy(e => e.Genre.Trim()).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "未確定";

        var syncScore = ClampPercent((Math.Min(epgCount, 100) * 0.35) + (Math.Min(activeReservationCount, 50) * 0.8) + 35 - conflictCount * 5);
        var trends = ViewingTrendProfile.Build(snapshot.Reservations, snapshot.Epg);
        var scoredPrograms = ProgramScorer.Score(snapshot.Epg, snapshot.Reservations, trends, settings)
            .Where(p => p.Score >= settings.ScoreThreshold)
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Program.StartTime)
            .Take(settings.CandidateCount)
            .ToArray();

        var emptyStateHtml = BuildUnifiedEmptyState(snapshot, scoredPrograms, settings);
        var insightSummaryHtml = BuildRecommendationInsightSummary(scoredPrograms, snapshot, settings);
        var compactPanelHtml = BuildCompactLivePanel(scoredPrograms, snapshot.Reservations, settings);
        var widgetStripHtml = BuildMiniWidgetStrip(scoredPrograms, snapshot.Reservations, conflictCount, activeReservationCount);
        var epgRows = BuildEpgRows(snapshot.Epg.OrderBy(e => e.StartTime).Take(8));
        var reservationRows = BuildReservationRows(snapshot.Reservations.OrderBy(r => r.StartTime).Take(8));
        var scoringRows = BuildDarkScoringRows(scoredPrograms.Take(10));
        var topScore = scoredPrograms.Length == 0 ? 0 : scoredPrograms.Max(p => p.Score);
        var averageScore = scoredPrograms.Length == 0 ? 0 : (int)Math.Round(scoredPrograms.Average(p => p.Score));
        var strongCount = scoredPrograms.Count(p => p.Score >= 70);
        var candidateCount = scoredPrograms.Count(p => p.Score >= 40 && p.Score < 70);
        var watchCount = scoredPrograms.Count(p => p.Score >= 12 && p.Score < 40);
        var meterHtml = BuildDarkMeterPanel(topScore, averageScore, strongCount, candidateCount, watchCount, scoredPrograms.Length);
        var heatmap = settings.GraphsEnabled ? BuildDarkHeatmap(snapshot.Reservations, settings) : "<div class=\"air-muted\">グラフ表示はOFFです。</div>";
        var candidateCards = BuildDarkCandidateCards(scoredPrograms.Take(10));
        var settingsPanel = BuildDarkSettingsPanel(settings);
        var css = """
<style>
.air-dashboard{font-family:Meiryo,"Yu Gothic UI",sans-serif;color:#172033;background:#f4f7fb;padding:22px;min-height:100vh;box-sizing:border-box}.air-top{display:flex;align-items:flex-start;justify-content:space-between;gap:18px;margin:0 0 18px;padding:16px 18px;border:1px solid #dbe4f0;border-radius:18px;background:#fff;box-shadow:0 12px 28px rgba(15,23,42,.08)}.air-brand-row{display:flex;align-items:center;gap:12px;flex-wrap:wrap}.air-title{margin:0!important;color:#0b1220!important;font-size:34px!important;line-height:1.05!important;font-weight:900!important;letter-spacing:.01em!important;text-shadow:none!important}.air-version{display:inline-flex;align-items:center;height:25px;padding:0 11px;border-radius:999px;border:1px solid #93c5fd;background:#eff6ff;color:#1d4ed8;font-size:13px;font-weight:900}.air-sub{font-size:14px;color:#334155;margin-top:10px;font-weight:800}.air-sync{display:flex;align-items:center;gap:10px;background:#fff;border:1px solid #dbe4f0;border-radius:16px;padding:10px 14px;box-shadow:0 10px 24px rgba(15,23,42,.08)}.air-sync-ring{width:46px;height:46px;border-radius:14px;background:linear-gradient(135deg,#16a34a,#22c55e);color:#fff;font-size:20px;font-weight:900;display:flex;align-items:center;justify-content:center}.air-sync-label{font-size:12px;color:#64748b;font-weight:700}.air-sync-text{font-size:13px;color:#16a34a;font-weight:900;margin-top:2px}.air-empty{border:1px solid #fed7aa;background:#fff7ed;border-radius:14px;padding:13px 15px;margin:0 0 16px;color:#9a3412;box-shadow:0 8px 18px rgba(15,23,42,.05)}.air-empty.ok{border-color:#bbf7d0;background:#ecfdf5;color:#166534}.air-empty strong{display:block;color:#111827;margin-bottom:5px;font-size:14px}.air-kpis{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:14px;margin-bottom:16px}.air-kpi{border-radius:16px;padding:18px 20px;color:#fff;min-height:108px;background:linear-gradient(135deg,#35476a,#182338);box-shadow:0 14px 28px rgba(15,23,42,.16)}.air-kpi.green{background:linear-gradient(135deg,#2f7a66,#1f2937)}.air-kpi.gold{background:linear-gradient(135deg,#9a6b50,#262a33)}.air-kpi.purple{background:linear-gradient(135deg,#7850a5,#283042)}.air-kpi.red{background:linear-gradient(135deg,#9f5b7c,#283042)}.air-kpi-label{font-size:13px;color:#bfdbfe;font-weight:800}.air-kpi-value{font-size:30px;font-weight:900;line-height:1.25;margin-top:11px;word-break:break-word}.air-kpi-note{font-size:12px;color:#e2e8f0;margin-top:6px}.air-section-title{font-size:18px;color:#111827;margin:22px 0 10px}.air-live-layout{display:grid;grid-template-columns:320px 1fr;gap:16px;margin-bottom:16px}.air-live-panel,.air-panel{border:1px solid rgba(148,163,184,.22);border-radius:18px;background:linear-gradient(180deg,#172235,#111827);color:#e8f2ff;box-shadow:0 18px 34px rgba(15,23,42,.18)}.air-live-panel{padding:16px}.air-panel{padding:18px}.air-panel h2{margin:0 0 14px;color:#f8fafc;font-size:18px;line-height:1.3}.air-muted{color:#c9d7e8;font-size:12px;line-height:1.65}.air-accent{color:#fde047;font-weight:900}.air-live-title{display:flex;justify-content:space-between;align-items:center;font-weight:900;color:#f8fafc;margin-bottom:12px}.air-live-dot{width:10px;height:10px;border-radius:50%;background:#22c55e;box-shadow:0 0 12px rgba(34,197,94,.9)}.air-temp{display:flex;align-items:end;gap:10px;margin:12px 0}.air-temp-score{font-size:44px;font-weight:900;line-height:1;color:#facc15}.air-temp-label{font-size:12px;color:#d6e2f0;margin-bottom:6px}.air-live-meter{height:9px;border-radius:999px;background:rgba(148,163,184,.20);overflow:hidden}.air-live-meter span{display:block;height:100%;border-radius:999px;background:linear-gradient(90deg,#38bdf8,#22c55e,#facc15,#fb923c)}.air-live-list{display:grid;gap:10px;margin-top:14px}.air-live-item{border:1px solid rgba(148,163,184,.14);border-radius:12px;background:rgba(15,23,42,.48);padding:11px}.air-live-item strong{display:block;color:#fff;font-size:13px;line-height:1.4;margin-bottom:4px}.air-widget-strip{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;margin-bottom:16px}.air-widget{border:1px solid rgba(148,163,184,.18);border-radius:14px;background:#fff;color:#172033;padding:14px 16px;box-shadow:0 8px 18px rgba(15,23,42,.08)}.air-widget-label{font-size:12px;color:#64748b;font-weight:800}.air-widget-value{font-size:25px;font-weight:900;margin-top:5px}.air-widget.blue .air-widget-value{color:#0284c7}.air-widget.green .air-widget-value{color:#16a34a}.air-widget.yellow .air-widget-value{color:#ca8a04}.air-widget.red .air-widget-value{color:#e11d48}.air-grid-3{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px}.air-grid-2{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:16px}.air-meter-row{display:flex;gap:22px;align-items:center}.air-gauge{width:142px;height:142px;border-radius:50%;position:relative;background:conic-gradient(#06b6d4 0 42%,#a3e635 42% 62%,#facc15 62% 78%,#fb923c 78% 88%,#ef4444 88% 100%);display:flex;align-items:center;justify-content:center;flex:0 0 auto}.air-gauge::after{content:"";position:absolute;inset:20px;border-radius:50%;background:#111827}.air-gauge-value{position:relative;z-index:1;text-align:center;font-size:34px;font-weight:900;color:#f8fafc}.air-gauge-value span{display:block;font-size:12px;color:#cbd5e1;font-weight:600;margin-top:4px}.air-mini-lines{flex:1}.air-mini-line{display:grid;grid-template-columns:92px 1fr 38px;align-items:center;gap:10px;margin:8px 0;font-size:12px;color:#e8f2ff}.air-mini-track,.air-break-track{height:6px;background:rgba(148,163,184,.20);border-radius:999px;overflow:hidden}.air-mini-fill,.air-break-fill{height:100%;border-radius:999px;background:#38bdf8}.air-insight-list,.air-breakdown{display:grid;gap:10px}.air-insight{border:1px solid rgba(148,163,184,.16);background:rgba(15,23,42,.48);border-radius:12px;padding:12px}.air-insight strong{display:block;color:#fff;margin-bottom:6px}.air-break-line{display:grid;grid-template-columns:86px 1fr 42px;align-items:center;gap:8px;font-size:12px;color:#e8f2ff}.air-heatmap{display:grid;grid-template-columns:46px repeat(8,1fr);gap:4px;align-items:center}.air-heat-head,.air-heat-label{font-size:12px;color:#d6e2f0;text-align:center}.air-heat-cell{height:30px;border-radius:5px;border:1px solid rgba(15,23,42,.55);background:#1e3a8a}.air-heat-cell.lv1{background:#1e3a8a}.air-heat-cell.lv2{background:#0891b2}.air-heat-cell.lv3{background:#22c55e}.air-heat-cell.lv4{background:#facc15}.air-heat-cell.lv5{background:#fb923c}.air-heat-cell.lv6{background:#ef4444}.air-legend{display:flex;align-items:center;gap:8px;margin-top:12px;color:#d6e2f0;font-size:12px}.air-grad{height:12px;width:180px;border-radius:999px;background:linear-gradient(90deg,#1e3a8a,#0891b2,#22c55e,#facc15,#fb923c,#ef4444)}.air-table{width:100%;border-collapse:collapse}.air-table th{font-size:12px;color:#cbd5e1;background:rgba(148,163,184,.10);padding:9px;text-align:left;border-bottom:1px solid rgba(148,163,184,.18)}.air-table td{font-size:13px;color:#edf5ff;padding:9px;border-bottom:1px solid rgba(148,163,184,.12);vertical-align:middle}.air-score-cell{display:inline-block;min-width:44px;text-align:center;border-radius:8px;padding:5px 8px;font-weight:900;color:#fff;background:#2563eb}.air-score-cell.high{background:#dc2626}.air-score-cell.mid{background:#ea580c}.air-score-cell.normal{background:#2563eb}.air-score-cell.low{background:#64748b}.air-tags{display:flex;flex-wrap:wrap;gap:6px;margin:7px 0}.air-tag{display:inline-block;border-radius:999px;padding:4px 9px;font-size:11px;border:1px solid rgba(255,255,255,.12);background:rgba(255,255,255,.07);color:#e2e8f0}.air-tag.genre{background:rgba(34,197,94,.20);color:#bbf7d0}.air-tag.station{background:rgba(59,130,246,.22);color:#bfdbfe}.air-tag.keyword{background:rgba(250,204,21,.18);color:#fef08a}.air-tag.new{background:rgba(168,85,247,.22);color:#e9d5ff}.air-tag.risk{background:rgba(239,68,68,.20);color:#fecaca}.air-cards{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px}.air-candidate{border:1px solid rgba(148,163,184,.18);border-radius:14px;background:rgba(15,23,42,.50);padding:14px}.air-candidate-head{display:flex;justify-content:space-between;gap:10px}.air-candidate-title{font-weight:800;margin:9px 0;color:#fff;line-height:1.4}.air-settings-grid{display:grid;grid-template-columns:repeat(2,minmax(230px,1fr));gap:14px}.air-setting{border:1px solid rgba(148,163,184,.18);background:rgba(15,23,42,.48);border-radius:12px;padding:14px}.air-setting label{display:block;font-weight:800;margin-bottom:7px;color:#f8fafc}.air-setting input,.air-setting select,.air-textarea{width:100%;border:1px solid rgba(148,163,184,.30);background:#0f172a;color:#e5edf7;border-radius:8px;padding:9px;box-sizing:border-box}.air-textarea{min-height:72px;font-family:inherit}.air-actions{margin-top:14px}.air-button{border:1px solid #0284c7;background:#0284c7;color:#fff;border-radius:10px;padding:9px 16px;font-weight:900;cursor:pointer}.air-help{font-size:12px;color:#d6e2f0;margin-top:7px;line-height:1.55}@media(max-width:1200px){.air-live-layout{grid-template-columns:1fr}.air-widget-strip{grid-template-columns:repeat(2,1fr)}.air-kpis{grid-template-columns:repeat(2,1fr)}.air-grid-3,.air-grid-2{grid-template-columns:1fr}.air-cards{grid-template-columns:1fr}}
</style>
""";

        var html = $"""
<main class="air-dashboard">
  <div class="air-top">
    <div class="air-brand">
      <div class="air-brand-row"><h1 class="air-title">AI-rhythm</h1><span class="air-version">v1.0.2</span></div>
      <div class="air-sub">番組表からおすすめを表示します</div>
    </div>
    <div class="air-sync"><div class="air-sync-ring">{syncScore}</div><div><div class="air-sync-label">状態</div><div class="air-sync-text">{(syncScore >= 50 ? "良好" : "確認中")}</div></div></div>
  </div>
  {emptyStateHtml}
  <section class="air-kpis">
    <div class="air-kpi"><div class="air-kpi-label">番組表</div><div class="air-kpi-value">{epgCount}</div><div class="air-kpi-note">表示対象</div></div>
    <div class="air-kpi green"><div class="air-kpi-label">予約</div><div class="air-kpi-value">{reservationCount}</div><div class="air-kpi-note">参考情報</div></div>
    <div class="air-kpi gold"><div class="air-kpi-label">有効予約</div><div class="air-kpi-value">{activeReservationCount}</div><div class="air-kpi-note">傾向判定</div></div>
    <div class="air-kpi purple"><div class="air-kpi-label">主要ジャンル</div><div class="air-kpi-value">{Html(genreTop)}</div><div class="air-kpi-note">番組表より</div></div>
    <div class="air-kpi red"><div class="air-kpi-label">ニックネーム</div><div class="air-kpi-value">{Html(settings.Nickname.DefaultIfBlank("未設定"))}</div><div class="air-kpi-note">設定</div></div>
  </section>

  <div class="air-live-layout"><aside>{compactPanelHtml}</aside><section>{widgetStripHtml}<div class="air-grid-3">
    <section class="air-panel"><h2>おすすめ度</h2>{meterHtml}</section>
    <section class="air-panel"><h2>推薦理由</h2>{insightSummaryHtml}</section>
    <section class="air-panel"><h2>時間帯の傾向</h2>{heatmap}</section>
  </div></section></div>

  <section class="air-panel"><h2>おすすめ候補</h2><table class="air-table"><thead><tr><th>スコア</th><th>放送日時</th><th>局名</th><th>番組名</th><th>理由</th></tr></thead><tbody>{scoringRows}</tbody></table></section>

  <h2 class="air-section-title">候補カード</h2>
  <section class="air-panel"><div class="air-cards">{candidateCards}</div></section>

  <h2 class="air-section-title">番組表</h2>
  <div class="air-grid-2">
    <section class="air-panel"><h2>近日の番組</h2><table class="air-table"><thead><tr><th>開始</th><th>局名</th><th>番組名</th><th>ジャンル</th></tr></thead><tbody>{epgRows}</tbody></table></section>
    <section class="air-panel"><h2>予約</h2><table class="air-table"><thead><tr><th>開始</th><th>局名</th><th>番組名</th><th>状態</th></tr></thead><tbody>{reservationRows}</tbody></table></section>
  </div>

  <h2 class="air-section-title">設定</h2>
  {settingsPanel}
</main>
""";

        return css + html;
    }


    private static string BuildUnifiedEmptyState(DashboardSnapshot snapshot, IReadOnlyList<ScoredProgram> scoredPrograms, AirhythmLocalSettings settings)
    {
        var messages = new List<string>();
        if (snapshot.Epg.Count == 0) messages.Add("番組情報がありません。");
        if (snapshot.Reservations.Count == 0) messages.Add("予約情報がないため、番組表を中心におすすめします。");
        if (scoredPrograms.Count == 0 && snapshot.Epg.Count > 0) messages.Add($"条件に合う候補がありません。設定を調整してください。");
        if (messages.Count == 0) return "<div class=\"air-empty ok\"><strong>AI-rhythm状態</strong>おすすめを表示しています。</div>";
        return "<div class=\"air-empty\"><strong>AI-rhythm状態</strong>" + string.Join("<br>", messages.Select(Html)) + "</div>";
    }

    private static string BuildRecommendationInsightSummary(IReadOnlyList<ScoredProgram> programs, DashboardSnapshot snapshot, AirhythmLocalSettings settings)
    {
        if (programs.Count == 0)
        {
            return $"<div class=\"air-insight-list\"><div class=\"air-insight\"><strong>推薦理由は未生成です</strong><span class=\"air-muted\">候補が出ない場合は、優先キーワードや候補条件を調整してください。</span></div></div>";
        }

        var top = programs[0];
        var contributions = EstimateContributions(top);
        var bars = new[]
        {
            ("ジャンル", contributions.GetValueOrDefault("genre")),
            ("局傾向", contributions.GetValueOrDefault("station")),
            ("キーワード", contributions.GetValueOrDefault("keyword")),
            ("時間帯", contributions.GetValueOrDefault("time")),
            ("シリーズ", contributions.GetValueOrDefault("series")),
            ("安全度", contributions.GetValueOrDefault("risk"))
        }.Select(x => $"<div class=\"air-break-line\"><span>{Html(x.Item1)}</span><div class=\"air-break-track\"><div class=\"air-break-fill\" style=\"width:{ClampPercent(x.Item2)}%\"></div></div><strong>{ClampPercent(x.Item2)}</strong></div>");

        var preferred = settings.PreferredKeywords.Length == 0 ? "未設定" : Html(settings.PreferredKeywords);
        var excluded = settings.ExcludedKeywords.Length == 0 ? "未設定" : Html(settings.ExcludedKeywords);
        return $"<div class=\"air-insight-list\"><div class=\"air-insight\"><strong>{Html(top.Program.Title)}</strong><span class=\"air-muted\">{Html(top.Program.ServiceName)} / {Html(top.Program.StartTime.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture))} / おすすめ度 {top.Score}</span><div class=\"air-tags\">{BuildReasonTags(top)}</div><div class=\"air-muted\">{Html(top.Reason)}</div></div><div class=\"air-breakdown\">{string.Join("", bars)}</div><div class=\"air-help\">設定: 推薦モード={Html(settings.RecommendationModeLabel)} / 優先={preferred} / 除外={excluded}</div></div>";
    }

    private static string BuildCompactLivePanel(IReadOnlyList<ScoredProgram> scoredPrograms, IReadOnlyList<ApiReservation> reservations, AirhythmLocalSettings settings)
    {
        var top = scoredPrograms.FirstOrDefault();
        var topScore = top?.Score ?? 0;
        var temp = ClampPercent(topScore <= 0 ? reservations.Count(r => r.IsEnabled) * 4 : topScore);
        var title = top is null ? "候補なし" : top.Program.Title;
        var service = top is null ? "未取得" : top.Program.ServiceName;
        var tags = top is null ? "<span class=\"air-tag station\">待機中</span>" : BuildReasonTags(top);
        var activeCount = reservations.Count(r => r.IsEnabled);
        var conflictCount = reservations.Count(r => r.IsConflicted);
        return $@"<div class=""air-live-panel""><div class=""air-live-title""><span>AI-rhythm</span><span class=""air-live-dot""></span></div><div class=""air-temp""><div class=""air-temp-score"">{temp}</div><div class=""air-temp-label"">注目度</div></div><div class=""air-live-meter""><span style=""width:{Math.Max(3, temp)}%""></span></div><div class=""air-live-list""><div class=""air-live-item""><strong>今夜の注目</strong><span class=""air-muted"">{Html(title)}</span><br><span class=""air-muted"">{Html(service)}</span></div><div class=""air-live-item""><strong>理由タグ</strong><div class=""air-tags"">{tags}</div></div><div class=""air-live-item""><strong>状態</strong><span class=""air-muted"">有効予約 {activeCount} / 競合 {conflictCount} / 表示 {settings.CandidateCount}件</span></div></div></div>";
    }

    private static string BuildMiniWidgetStrip(IReadOnlyList<ScoredProgram> scoredPrograms, IReadOnlyList<ApiReservation> reservations, int conflictCount, int activeReservationCount)
    {
        var topScore = scoredPrograms.FirstOrDefault()?.Score ?? 0;
        var nearCount = scoredPrograms.Count(p => p.Program.StartTime <= DateTimeOffset.Now.AddHours(12));
        return $@"<div class=""air-widget-strip""><div class=""air-widget blue""><div class=""air-widget-label"">状態</div><div class=""air-widget-value"">OK</div></div><div class=""air-widget green""><div class=""air-widget-label"">上位スコア</div><div class=""air-widget-value"">{topScore}</div></div><div class=""air-widget yellow""><div class=""air-widget-label"">近日候補</div><div class=""air-widget-value"">{nearCount}</div></div><div class=""air-widget red""><div class=""air-widget-label"">要確認</div><div class=""air-widget-value"">{conflictCount}</div></div></div>";
    }

    private static string BuildDarkMeterPanel(int topScore, int averageScore, int strongCount, int candidateCount, int watchCount, int totalCount)
    {
        var meterValue = ClampPercent(topScore <= 0 ? averageScore : (topScore * 0.75) + (averageScore * 0.25));
        string Line(string label, int value, string color)
        {
            var pct = Math.Max(0, Math.Min(100, value));
            return $@"<div class=""air-mini-line""><span>{Html(label)}</span><div class=""air-mini-track""><div class=""air-mini-fill"" style=""width:{pct}%;background:{color}""></div></div><strong>{value}</strong></div>";
        }

        return $@"<div class=""air-meter-row""><div class=""air-gauge""><div class=""air-gauge-value"">{meterValue}<span>おすすめ度</span></div></div><div class=""air-mini-lines"">{Line("最上位候補", topScore, "#38bdf8")}{Line("候補平均", averageScore, "#60a5fa")}{Line("強候補", strongCount, "#facc15")}{Line("確認候補", candidateCount, "#fb923c")}{Line("様子見", watchCount, "#94a3b8")}{Line("全候補", totalCount, "#64748b")}</div></div><div class=""air-muted"">高いほど <span class=""air-accent"">予約候補として確認する価値が高い</span> 状態です。</div>";
    }

    private static string BuildDarkHeatmap(IReadOnlyList<ApiReservation> reservations, AirhythmLocalSettings settings)
    {
        var buckets = new int[7, 8];
        foreach (var r in reservations.Where(r => settings.CanUseForLearning(r)))
        {
            var day = ((int)r.StartTime.DayOfWeek + 6) % 7;
            var h = r.StartTime.Hour;
            var col = h < 3 ? 0 : h < 6 ? 1 : h < 9 ? 2 : h < 12 ? 3 : h < 15 ? 4 : h < 18 ? 5 : h < 21 ? 6 : 7;
            buckets[day, col]++;
        }
        var days = new[] { "月", "火", "水", "木", "金", "土", "日" };
        var hours = new[] { "0", "3", "6", "9", "12", "15", "18", "21" };
        var max = Math.Max(1, buckets.Cast<int>().Max());
        var sb = new StringBuilder("<div class=\"air-heatmap\"><div></div>");
        foreach (var h in hours) sb.Append($@"<div class=""air-heat-head"">{Html(h)}</div>");
        for (var r = 0; r < days.Length; r++)
        {
            sb.Append($@"<div class=""air-heat-label"">{Html(days[r])}</div>");
            for (var c = 0; c < hours.Length; c++)
            {
                var lv = Math.Max(1, Math.Min(6, (int)Math.Ceiling((buckets[r, c] / (double)max) * 6)));
                sb.Append($@"<div class=""air-heat-cell lv{lv}"" title=""{Html(days[r])} {Html(hours[c])}時台: {buckets[r, c]}""></div>");
            }
        }
        sb.Append("</div><div class=\"air-legend\"><span>弱い</span><span class=\"air-grad\"></span><span>強い</span></div><div class=\"air-muted\">時間帯別の傾向です。</div>");
        return sb.ToString();
    }

    private static string BuildDarkCandidateCards(IEnumerable<ScoredProgram> programs)
    {
        var list = programs.ToArray();
        if (list.Length == 0) return "<div class=\"air-muted\">候補はありません。</div>";
        var sb = new StringBuilder();
        foreach (var p in list)
        {
            sb.Append($@"<article class=""air-candidate""><div class=""air-candidate-head""><span class=""air-score-cell {ScoreClass(p.Score)}"">{p.Score}</span><span class=""air-muted"">{Html(p.Program.StartTime.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture))}<br>{Html(p.Program.ServiceName)}</span></div><div class=""air-candidate-title"">{Html(p.Program.Title)}</div><div class=""air-tags"">{BuildReasonTags(p)}</div><div class=""air-muted"">{Html(p.Reason)}</div></article>");
        }
        return sb.ToString();
    }

    private static string BuildDarkScoringRows(IEnumerable<ScoredProgram> programs)
    {
        var rows = programs.Select(p => $"<tr><td><span class=\"air-score-cell {ScoreClass(p.Score)}\">{p.Score}</span></td><td>{Html(p.Program.StartTime.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture))}</td><td>{Html(p.Program.ServiceName)}</td><td>{Html(p.Program.Title)}</td><td><div class=\"air-tags\">{BuildReasonTags(p)}</div><div class=\"air-muted\">{Html(p.Reason)}</div></td></tr>").ToArray();
        return rows.Length == 0 ? "<tr><td colspan=\"5\">候補はありません。</td></tr>" : string.Join(Environment.NewLine, rows);
    }

    private static IReadOnlyDictionary<string, int> EstimateContributions(ScoredProgram program)
    {
        var reason = program.Reason ?? string.Empty;
        var score = Math.Max(0, program.Score);
        var genre = reason.Contains("ジャンル", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(program.Program.Genre) ? Math.Min(30, 8 + score / 4) : Math.Min(12, score / 8);
        var station = reason.Contains("局", StringComparison.OrdinalIgnoreCase) ? Math.Min(30, 10 + score / 5) : Math.Min(12, score / 9);
        var keyword = reason.Contains("キーワード", StringComparison.OrdinalIgnoreCase) || reason.Contains("番組名", StringComparison.OrdinalIgnoreCase) ? Math.Min(30, 12 + score / 4) : Math.Min(10, score / 10);
        var time = (program.Program.StartTime.Hour >= 18 || program.Program.StartTime.Hour < 4) ? Math.Min(30, 10 + score / 6) : Math.Min(14, score / 8);
        var series = program.Program.Title.Contains("＃", StringComparison.OrdinalIgnoreCase) || program.Program.Title.Contains("#", StringComparison.OrdinalIgnoreCase) || reason.Contains("近い", StringComparison.OrdinalIgnoreCase) ? Math.Min(30, 12 + score / 5) : Math.Min(12, score / 9);
        var risk = reason.Contains("除外", StringComparison.OrdinalIgnoreCase) || reason.Contains("失敗", StringComparison.OrdinalIgnoreCase) ? 6 : Math.Min(30, 18 + score / 8);
        return new Dictionary<string, int> { ["genre"] = genre, ["station"] = station, ["keyword"] = keyword, ["time"] = time, ["series"] = series, ["risk"] = risk };
    }

    private static string BuildReasonTags(ScoredProgram program)
    {
        var tags = new List<(string Text, string Class)>();
        var reason = program.Reason ?? string.Empty;
        if (reason.Contains("ジャンル", StringComparison.OrdinalIgnoreCase)) tags.Add(("ジャンル", "genre"));
        if (reason.Contains("局", StringComparison.OrdinalIgnoreCase)) tags.Add(("局傾向", "station"));
        if (reason.Contains("キーワード", StringComparison.OrdinalIgnoreCase) || reason.Contains("番組名", StringComparison.OrdinalIgnoreCase)) tags.Add(("タイトル語", "keyword"));
        if (program.Program.Title.Contains("[新]", StringComparison.OrdinalIgnoreCase) || reason.Contains("新番組", StringComparison.OrdinalIgnoreCase)) tags.Add(("新番組", "new"));
        if (program.Program.Title.Contains("＃", StringComparison.OrdinalIgnoreCase) || program.Program.Title.Contains("#", StringComparison.OrdinalIgnoreCase)) tags.Add(("シリーズ", "new"));
        if (reason.Contains("除外", StringComparison.OrdinalIgnoreCase) || reason.Contains("失敗", StringComparison.OrdinalIgnoreCase)) tags.Add(("要確認", "risk"));
        if (tags.Count == 0) tags.Add(("AI推定", "station"));
        return string.Join("", tags.Take(5).Select(t => $@"<span class=""air-tag {t.Class}"">{Html(t.Text)}</span>"));
    }

    private static string BuildDarkSettingsPanel(AirhythmLocalSettings settings)
    {
        var checkedSystem = settings.ExcludeSystemReservations ? "checked" : string.Empty;
        var checkedCancelled = settings.ExcludeCancelledReservations ? "checked" : string.Empty;
        var checkedFailed = settings.ExcludeFailedReservations ? "checked" : string.Empty;
        var checkedLateNight = settings.BoostLateNight ? "checked" : string.Empty;
        return $@"<section class=""air-panel""><h2>AI-rhythm設定</h2><form method=""get""><div class=""air-settings-grid""><div class=""air-setting""><label>ニックネーム</label><input name=""airNickname"" type=""text"" value=""{Html(settings.Nickname)}"" /></div><div class=""air-setting""><label>推薦モード</label><select name=""airRecommendationMode""><option value=""balanced"" {(settings.RecommendationMode == "balanced" ? "selected" : "")}>バランス</option><option value=""stable"" {(settings.RecommendationMode == "stable" ? "selected" : "")}>安定志向</option><option value=""new"" {(settings.RecommendationMode == "new" ? "selected" : "")}>新番組重視</option><option value=""genre"" {(settings.RecommendationMode == "genre" ? "selected" : "")}>ジャンル重視</option><option value=""soon"" {(settings.RecommendationMode == "soon" ? "selected" : "")}>直近重視</option></select></div><div class=""air-setting""><label>優先キーワード</label><textarea class=""air-textarea"" name=""airPreferredKeywords"">{Html(settings.PreferredKeywords)}</textarea><div class=""air-help"">改行・カンマ・スペースで区切ります。</div></div><div class=""air-setting""><label>除外キーワード</label><textarea class=""air-textarea"" name=""airExcludedKeywords"">{Html(settings.ExcludedKeywords)}</textarea><div class=""air-help"">一致した番組は候補から外します。</div></div><div class=""air-setting""><label>表示件数</label><select name=""airCandidateCount""><option value=""10"" {(settings.CandidateCount == 10 ? "selected" : "")}>10件</option><option value=""20"" {(settings.CandidateCount == 20 ? "selected" : "")}>20件</option><option value=""30"" {(settings.CandidateCount == 30 ? "selected" : "")}>30件</option></select></div><div class=""air-setting""><label>候補スコア</label><select name=""airScoreThreshold""><option value=""20"" {(settings.ScoreThreshold == 20 ? "selected" : "")}>20以上</option><option value=""30"" {(settings.ScoreThreshold == 30 ? "selected" : "")}>30以上</option><option value=""40"" {(settings.ScoreThreshold == 40 ? "selected" : "")}>40以上</option><option value=""50"" {(settings.ScoreThreshold == 50 ? "selected" : "")}>50以上</option></select></div><div class=""air-setting""><label>再放送らしき番組</label><select name=""airRerunHandling""><option value=""normal"" {(settings.RerunHandling == "normal" ? "selected" : "")}>通常扱い</option><option value=""down"" {(settings.RerunHandling == "down" ? "selected" : "")}>少し弱める</option><option value=""exclude"" {(settings.RerunHandling == "exclude" ? "selected" : "")}>候補から外す</option></select><label style=""margin-top:8px""><input type=""checkbox"" name=""airBoostLateNight"" value=""1"" {checkedLateNight}> 深夜帯を少し強める</label></div><div class=""air-setting""><label>表示</label><select name=""airTheme""><option value=""dark"" {(settings.Theme == "dark" ? "selected" : "")}>標準</option><option value=""contrast"" {(settings.Theme == "contrast" ? "selected" : "")}>視認性重視</option><option value=""calm"" {(settings.Theme == "calm" ? "selected" : "")}>落ち着いた配色</option></select><label style=""margin-top:8px""><input type=""checkbox"" name=""airGraphs"" value=""on"" {(settings.GraphsEnabled ? "checked" : "")}> グラフ表示</label></div><div class=""air-setting""><label>傾向分析から除外</label><label><input type=""checkbox"" name=""airExcludeSystem"" value=""1"" {checkedSystem}> システム系</label><label><input type=""checkbox"" name=""airExcludeCancelled"" value=""1"" {checkedCancelled}> キャンセル</label><label><input type=""checkbox"" name=""airExcludeFailed"" value=""1"" {checkedFailed}> 失敗</label></div></div><div class=""air-actions""><button type=""submit"" class=""air-button"">保存</button></div></form></section>";
    }


    private static string ScoreClass(int score) => score >= 80 ? "high" : score >= 65 ? "mid" : score >= 45 ? "normal" : "low";

    private static string BuildEpgRows(IEnumerable<ApiEpgEvent> events)
    {
        var rows = events.Select(e => $"<tr><td>{Html(e.StartTime.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture))}</td><td>{Html(e.ServiceName)}</td><td>{Html(e.Title)}</td><td>{Html(e.Genre)}</td></tr>").ToArray();
        return rows.Length == 0 ? "<tr><td colspan=\"4\">番組情報がありません。</td></tr>" : string.Join("", rows);
    }

    private static string BuildReservationRows(IEnumerable<ApiReservation> reservations)
    {
        var rows = reservations.Select(r => $"<tr><td>{Html(r.StartTime.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture))}</td><td>{Html(r.ServiceName)}</td><td>{Html(r.Title)}</td><td>{Html(r.IsConflicted ? "競合" : r.Status.DefaultIfBlank("通常"))}</td></tr>").ToArray();
        return rows.Length == 0 ? "<tr><td colspan=\"4\">予約情報がありません。</td></tr>" : string.Join("", rows);
    }

    private IReadOnlyList<string> BuildReasons(int enabledReservations, int conflictedReservations, string favoriteGenre, string favoriteTimeBand, double keywordScore, double evidenceScore, double confidence, string sourceNote)
    {
        var reasons = new List<string>
        {
            $"有効な予約 {enabledReservations} 件を参考にしました。",
            $"信頼度 {confidence:0}% / 説明材料 {evidenceScore:0}% / キーワード一致 {keywordScore:0}%。",
            $"注目傾向は {favoriteGenre.DefaultIfBlank("未確定")} / {favoriteTimeBand} です。"
        };
        if (conflictedReservations > 0) reasons.Add($"競合状態の予約が {conflictedReservations} 件あります。安全側にスコアを抑えています。");
        return reasons;
    }

    private static string DetectFavoriteGenre(IReadOnlyList<AnalysisReservationInfo> reservations, IReadOnlyList<AnalysisProgramInfo> programs)
    {
        var genre = programs.Where(p => !string.IsNullOrWhiteSpace(p.Genre)).GroupBy(p => p.Genre.Trim()).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(genre)) return genre;
        var titles = reservations.Select(r => r.Title).Concat(programs.Select(p => p.Title)).ToArray();
        if (titles.Any(t => t.Contains("映画", StringComparison.OrdinalIgnoreCase))) return "映画";
        if (titles.Any(t => t.Contains("ドラマ", StringComparison.OrdinalIgnoreCase))) return "ドラマ";
        if (titles.Any(t => t.Contains("バラエティ", StringComparison.OrdinalIgnoreCase))) return "バラエティー";
        if (titles.Any(t => t.Contains("ニュース", StringComparison.OrdinalIgnoreCase))) return "ニュース";
        return string.Empty;
    }

    private static double GuessKeywordScore(IReadOnlyList<AnalysisReservationInfo> reservations, IReadOnlyList<AnalysisProgramInfo> programs)
    {
        var text = string.Join(" ", reservations.Select(r => r.Title).Concat(programs.Select(p => p.Title)).Concat(programs.Select(p => p.Description)));
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var score = 0;
        string[] keys = ["映画", "字幕", "ドラマ", "新番組", "初放送", "WOWOW", "BS", "夜", "リサージェンス"];
        foreach (var key in keys) if (text.Contains(key, StringComparison.OrdinalIgnoreCase)) score += 11;
        return ClampPercent(score);
    }

    private static double GuessPerformerScore(IReadOnlyList<AnalysisProgramInfo> programs)
    {
        var text = string.Join(" ", programs.Select(p => p.Description));
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Contains("出演", StringComparison.OrdinalIgnoreCase) || text.Contains("監督", StringComparison.OrdinalIgnoreCase) || text.Contains("脚本", StringComparison.OrdinalIgnoreCase) ? 60 : 0;
    }

    private static int ClampPercent(double value) => (int)Math.Max(0, Math.Min(100, Math.Round(value)));
    private static string DisplayUser(string? userNickname) => string.IsNullOrWhiteSpace(userNickname) ? "ユーザーさん" : userNickname.Trim();
    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private void SafeLog(string message)
    {
        try { _context?.Log(message); }
        catch { }
    }
}



internal sealed class AirhythmLocalSettings
{
    public static string SettingsPath
    {
        get
        {
            var assemblyPath = typeof(AirhythmLocalSettings).Assembly.Location;
            var dir = string.IsNullOrWhiteSpace(assemblyPath)
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
            return Path.Combine(dir, "AIrhythm.BasicPlugin.ini");
        }
    }

    public string Nickname { get; set; } = string.Empty;
    public string Theme { get; set; } = "dark";
    public int CandidateCount { get; set; } = 20;
    public int ScoreThreshold { get; set; } = 40;
    public bool GraphsEnabled { get; set; } = true;
    public string RecommendationMode { get; set; } = "balanced";
    public string PreferredKeywords { get; set; } = string.Empty;
    public string ExcludedKeywords { get; set; } = string.Empty;
    public bool BoostLateNight { get; set; } = false;
    public string RerunHandling { get; set; } = "normal";
    public bool ExcludeSystemReservations { get; set; } = true;
    public bool ExcludeCancelledReservations { get; set; } = false;
    public bool ExcludeFailedReservations { get; set; } = false;

    public static AirhythmLocalSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AirhythmLocalSettings();
            var values = ReadIni(SettingsPath);
            return new AirhythmLocalSettings
            {
                Nickname = values.GetValueOrDefault("General.Nickname", string.Empty),
                RecommendationMode = values.GetValueOrDefault("Recommendation.Mode", "balanced"),
                PreferredKeywords = DecodeMultiLine(values.GetValueOrDefault("Recommendation.PreferredKeywords", string.Empty)),
                ExcludedKeywords = DecodeMultiLine(values.GetValueOrDefault("Recommendation.ExcludedKeywords", string.Empty)),
                BoostLateNight = ReadBool(values, "Recommendation.BoostLateNight", false),
                RerunHandling = values.GetValueOrDefault("Recommendation.RerunHandling", "normal"),
                CandidateCount = ReadInt(values, "Recommendation.CandidateCount", 20),
                ScoreThreshold = ReadInt(values, "Recommendation.ScoreThreshold", 40),
                GraphsEnabled = ReadBool(values, "Display.GraphsEnabled", true),
                Theme = values.GetValueOrDefault("Display.Theme", "dark"),
                ExcludeSystemReservations = ReadBool(values, "Learning.ExcludeSystemReservations", true),
                ExcludeCancelledReservations = ReadBool(values, "Learning.ExcludeCancelledReservations", false),
                ExcludeFailedReservations = ReadBool(values, "Learning.ExcludeFailedReservations", false)
            }.Normalize();
        }
        catch
        {
            return new AirhythmLocalSettings();
        }
    }

    public static void Save(AirhythmLocalSettings settings)
    {
        var safe = settings.Normalize();
        var sb = new StringBuilder();
        sb.AppendLine("[General]");
        sb.AppendLine($"Nickname={EscapeIni(safe.Nickname)}");
        sb.AppendLine();
        sb.AppendLine("[Recommendation]");
        sb.AppendLine($"Mode={EscapeIni(safe.RecommendationMode)}");
        sb.AppendLine($"PreferredKeywords={EscapeIni(EncodeMultiLine(safe.PreferredKeywords))}");
        sb.AppendLine($"ExcludedKeywords={EscapeIni(EncodeMultiLine(safe.ExcludedKeywords))}");
        sb.AppendLine($"BoostLateNight={safe.BoostLateNight.ToString().ToLowerInvariant()}");
        sb.AppendLine($"RerunHandling={EscapeIni(safe.RerunHandling)}");
        sb.AppendLine($"CandidateCount={safe.CandidateCount}");
        sb.AppendLine($"ScoreThreshold={safe.ScoreThreshold}");
        sb.AppendLine();
        sb.AppendLine("[Display]");
        sb.AppendLine($"GraphsEnabled={safe.GraphsEnabled.ToString().ToLowerInvariant()}");
        sb.AppendLine($"Theme={EscapeIni(safe.Theme)}");
        sb.AppendLine();
        sb.AppendLine("[Learning]");
        sb.AppendLine($"ExcludeSystemReservations={safe.ExcludeSystemReservations.ToString().ToLowerInvariant()}");
        sb.AppendLine($"ExcludeCancelledReservations={safe.ExcludeCancelledReservations.ToString().ToLowerInvariant()}");
        sb.AppendLine($"ExcludeFailedReservations={safe.ExcludeFailedReservations.ToString().ToLowerInvariant()}");
        File.WriteAllText(SettingsPath, sb.ToString(), Encoding.UTF8);
    }

    public static void UpdateFromQuery(PluginUiContext context)
    {
        try
        {
            var query = ReadQuery(context);
            if (query.Count == 0) return;
            if (!query.ContainsKey("airNickname") && !query.ContainsKey("airTheme") && !query.ContainsKey("airCandidateCount") && !query.ContainsKey("airScoreThreshold") && !query.ContainsKey("airGraphs") && !query.ContainsKey("airRecommendationMode") && !query.ContainsKey("airPreferredKeywords") && !query.ContainsKey("airExcludedKeywords") && !query.ContainsKey("airBoostLateNight") && !query.ContainsKey("airRerunHandling")) return;

            var settings = Load();
            if (query.TryGetValue("airNickname", out var nickname)) settings.Nickname = nickname ?? string.Empty;
            if (query.TryGetValue("airTheme", out var theme)) settings.Theme = theme ?? "dark";
            if (query.TryGetValue("airCandidateCount", out var count) && int.TryParse(count, out var c)) settings.CandidateCount = c;
            if (query.TryGetValue("airScoreThreshold", out var threshold) && int.TryParse(threshold, out var t)) settings.ScoreThreshold = t;
            settings.GraphsEnabled = query.ContainsKey("airGraphs");
            if (query.TryGetValue("airRecommendationMode", out var mode)) settings.RecommendationMode = mode ?? "balanced";
            if (query.TryGetValue("airPreferredKeywords", out var preferred)) settings.PreferredKeywords = preferred ?? string.Empty;
            if (query.TryGetValue("airExcludedKeywords", out var excluded)) settings.ExcludedKeywords = excluded ?? string.Empty;
            if (query.TryGetValue("airRerunHandling", out var rerun)) settings.RerunHandling = rerun ?? "normal";
            settings.BoostLateNight = query.ContainsKey("airBoostLateNight");
            settings.ExcludeSystemReservations = query.ContainsKey("airExcludeSystem");
            settings.ExcludeCancelledReservations = query.ContainsKey("airExcludeCancelled");
            settings.ExcludeFailedReservations = query.ContainsKey("airExcludeFailed");
            Save(settings);
        }
        catch
        {
        }
    }

    public static Dictionary<string, string> ReadQuery(PluginUiContext context)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var currentRequestQuery = context.GetType().GetProperty("CurrentRequestQuery")?.GetValue(context);
            if (currentRequestQuery is not null)
            {
                foreach (var pair in QueryToDictionary(currentRequestQuery)) result[pair.Key] = pair.Value;
            }

            var legacyQuery = context.GetType().GetProperty("Query")?.GetValue(context);
            if (legacyQuery is not null)
            {
                foreach (var pair in QueryToDictionary(legacyQuery)) result[pair.Key] = pair.Value;
            }

            var queryString = context.GetType().GetProperty("CurrentRequestQueryString")?.GetValue(context)?.ToString();
            if (!string.IsNullOrWhiteSpace(queryString))
            {
                foreach (var pair in ParseQueryString(queryString)) result[pair.Key] = pair.Value;
            }
        }
        catch
        {
        }
        return result;
    }

    private static Dictionary<string, string> ParseQueryString(string queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var raw = queryString.TrimStart('?');
        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0].Replace('+', ' '));
            if (string.IsNullOrWhiteSpace(key)) continue;
            var value = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1].Replace('+', ' ')) : string.Empty;
            result[key] = value;
        }
        return result;
    }

    private static Dictionary<string, string> QueryToDictionary(object queryObject)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (queryObject is IEnumerable<KeyValuePair<string, string>> stringPairs)
        {
            foreach (var pair in stringPairs) result[pair.Key] = pair.Value;
            return result;
        }
        if (queryObject is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                var key = item?.GetType().GetProperty("Key")?.GetValue(item)?.ToString();
                var value = item?.GetType().GetProperty("Value")?.GetValue(item)?.ToString();
                if (!string.IsNullOrWhiteSpace(key)) result[key] = value ?? string.Empty;
            }
        }
        return result;
    }

    public bool CanUseForLearning(ApiReservation r)
    {
        if (ExcludeSystemReservations && IsSystemReservationLike(r)) return false;
        if (ExcludeCancelledReservations && (r.Status.Contains("cancel", StringComparison.OrdinalIgnoreCase) || r.Status.Contains("キャンセル", StringComparison.OrdinalIgnoreCase))) return false;
        if (ExcludeFailedReservations && (r.Status.Contains("failed", StringComparison.OrdinalIgnoreCase) || r.Status.Contains("失敗", StringComparison.OrdinalIgnoreCase))) return false;
        return !string.IsNullOrWhiteSpace(r.Title);
    }

    private static bool IsSystemReservationLike(ApiReservation r)
    {
        var text = (r.Title + " " + r.Source + " " + r.Status).Trim();
        return text.Contains("番組表確認", StringComparison.OrdinalIgnoreCase)
            || text.Contains("システム", StringComparison.OrdinalIgnoreCase)
            || text.Contains("system", StringComparison.OrdinalIgnoreCase);
    }

    private AirhythmLocalSettings Normalize()
    {
        CandidateCount = Math.Clamp(CandidateCount, 5, 50);
        ScoreThreshold = Math.Clamp(ScoreThreshold, 0, 100);
        if (Theme is not ("dark" or "contrast" or "calm" or "analysis")) Theme = "dark";
        if (RecommendationMode is not ("balanced" or "stable" or "new" or "genre" or "soon")) RecommendationMode = "balanced";
        if (RerunHandling is not ("normal" or "down" or "exclude")) RerunHandling = "normal";
        PreferredKeywords = NormalizeTextArea(PreferredKeywords);
        ExcludedKeywords = NormalizeTextArea(ExcludedKeywords);
        return this;
    }

    public string RecommendationModeLabel => RecommendationMode switch
    {
        "stable" => "安定志向",
        "new" => "新番組・初放送重視",
        "genre" => "ジャンル一致重視",
        "soon" => "直近放送重視",
        _ => "バランス"
    };

    public IReadOnlyList<string> PreferredKeywordList => SplitKeywordText(PreferredKeywords);
    public IReadOnlyList<string> ExcludedKeywordList => SplitKeywordText(ExcludedKeywords);

    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            values[$"{section}.{key}"] = UnescapeIni(value);
        }
        return values;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static string NormalizeTextArea(string? value)
        => string.Join(Environment.NewLine, SplitKeywordText(value).Take(40));

    private static IReadOnlyList<string> SplitKeywordText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        return value.Split(new[] { '\r', '\n', ',', '、', ';', '；', ' ', '　', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToArray();
    }

    private static string EncodeMultiLine(string value) => value.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");
    private static string DecodeMultiLine(string value) => value.Replace("\\n", Environment.NewLine);
    private static string EscapeIni(string value) => value.Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n");
    private static string UnescapeIni(string value) => value.Replace("\\n", "\n").Replace("\\\\", "\\");
}


internal sealed record DashboardSnapshot(IReadOnlyList<ApiEpgEvent> Epg, IReadOnlyList<ApiReservation> Reservations, string SourceNote);

internal sealed class ApiEpgEvent
{
    public string ServiceName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public string Genre { get; init; } = string.Empty;
    public int NetworkId { get; init; } = -1;
    public int TransportStreamId { get; init; } = -1;
    public int ServiceId { get; init; } = -1;
    public int ChannelSpace { get; init; } = -1;
    public int ChannelIndex { get; init; } = -1;
    public bool HasTriplet => NetworkId >= 0 && TransportStreamId >= 0 && ServiceId >= 0;

    public static ApiEpgEvent FromPlugin(PluginEpgEvent item) => new()
    {
        ServiceName = item.ServiceName,
        Title = item.Title,
        Description = string.IsNullOrWhiteSpace(item.Description) ? item.ExtendedDescription : item.Description,
        StartTime = item.Start,
        EndTime = item.End,
        Genre = item.Genre,
        NetworkId = item.NetworkId,
        TransportStreamId = item.TransportStreamId,
        ServiceId = item.ServiceId
    };
}

internal sealed class ApiReservation
{
    public int Id { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public bool IsConflicted { get; init; }

    public static ApiReservation FromPlugin(PluginReservation item) => new()
    {
        Id = item.Id,
        ServiceName = item.ServiceName,
        Title = item.Title,
        StartTime = item.StartTime,
        EndTime = item.EndTime,
        Status = item.Status,
        Source = item.Source,
        IsEnabled = item.IsEnabled,
        IsConflicted = item.IsConflicted
    };
}

internal sealed record TrendBar(string Label, int Value);
internal sealed record ScoredProgram(ApiEpgEvent Program, int Score, string Reason);

internal sealed class ViewingTrendProfile
{
    public IReadOnlyList<WeightedToken> FavoriteGenres { get; private init; } = Array.Empty<WeightedToken>();
    public IReadOnlyList<WeightedToken> FavoriteServices { get; private init; } = Array.Empty<WeightedToken>();
    public IReadOnlyList<WeightedToken> FavoriteKeywords { get; private init; } = Array.Empty<WeightedToken>();
    public IReadOnlySet<string> PositiveTitleKeys { get; private init; } = new HashSet<string>();
    public IReadOnlySet<string> NegativeTitleKeys { get; private init; } = new HashSet<string>();
    public IReadOnlySet<string> SystemTitleKeys { get; private init; } = new HashSet<string>();
    public int LearningSamples { get; private init; }

    public IReadOnlyList<string> FavoriteGenreLabels => FavoriteGenres.Select(x => x.Value).ToArray();
    public IReadOnlyList<string> FavoriteServiceLabels => FavoriteServices.Select(x => x.Value).ToArray();
    public IReadOnlyList<string> FavoriteKeywordLabels => FavoriteKeywords.Select(x => x.Value).ToArray();

    public static ViewingTrendProfile Build(IReadOnlyList<ApiReservation> reservations, IReadOnlyList<ApiEpgEvent> epg)
    {
        var userReservations = reservations.Where(IsUserLearningReservation).ToArray();
        var positive = userReservations.Where(r => ReservationWeight(r) > 0).ToArray();
        var negative = userReservations.Where(r => ReservationWeight(r) < 0).ToArray();

        var genreWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in positive)
        {
            var w = ReservationWeight(r);
            foreach (var e in epg.Where(e => IsNearSameProgram(r, e) && !string.IsNullOrWhiteSpace(e.Genre)))
                AddWeight(genreWeights, e.Genre.Trim(), w);
        }

        var serviceWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in userReservations.Where(r => !string.IsNullOrWhiteSpace(r.ServiceName)))
            AddWeight(serviceWeights, r.ServiceName.Trim(), Math.Max(-0.75, ReservationWeight(r)));

        var keywordWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in userReservations)
        {
            var w = ReservationWeight(r);
            foreach (var k in ExtractKeywords(r.Title)) AddWeight(keywordWeights, k, w);
        }

        return new ViewingTrendProfile
        {
            FavoriteGenres = ToTokens(genreWeights, 8),
            FavoriteServices = ToTokens(serviceWeights, 8),
            FavoriteKeywords = ToTokens(keywordWeights, 24),
            PositiveTitleKeys = positive.Select(r => NormalizeTitleKey(r.Title)).Where(k => k.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase),
            NegativeTitleKeys = negative.Select(r => NormalizeTitleKey(r.Title)).Where(k => k.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase),
            SystemTitleKeys = reservations.Where(IsSystemReservationForSettings).Select(r => NormalizeTitleKey(r.Title)).Where(k => k.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase),
            LearningSamples = positive.Length
        };
    }

    public double GenreAffinity(string? genre) => WeightOf(FavoriteGenres, genre);
    public double ServiceAffinity(string? service) => WeightOf(FavoriteServices, service);
    public double KeywordAffinity(ApiEpgEvent program, out string[] hits)
    {
        var text = (program.Title + " " + program.Description).Trim();
        var matched = FavoriteKeywords.Where(k => text.Contains(k.Value, StringComparison.OrdinalIgnoreCase)).Take(5).ToArray();
        hits = matched.Select(m => m.Value).ToArray();
        return matched.Sum(m => Math.Max(0, m.Weight));
    }

    public bool IsPositiveTitleLike(string title) => ContainsSimilarTitle(PositiveTitleKeys, title);
    public bool IsNegativeTitleLike(string title) => ContainsSimilarTitle(NegativeTitleKeys, title);
    public bool IsSystemTitleLike(string title) => ContainsSimilarTitle(SystemTitleKeys, title);

    private static bool ContainsSimilarTitle(IReadOnlySet<string> keys, string title)
    {
        var titleKey = NormalizeTitleKey(title);
        if (titleKey.Length == 0) return false;
        return keys.Any(k => k.Length > 0 && (titleKey.Contains(k, StringComparison.OrdinalIgnoreCase) || k.Contains(titleKey, StringComparison.OrdinalIgnoreCase)));
    }

    private static double WeightOf(IEnumerable<WeightedToken> tokens, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        return tokens.FirstOrDefault(t => string.Equals(t.Value, value.Trim(), StringComparison.OrdinalIgnoreCase))?.Weight ?? 0;
    }

    private static WeightedToken[] ToTokens(Dictionary<string, double> weights, int take)
        => weights.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(take).Select(kv => new WeightedToken(kv.Key, kv.Value)).ToArray();

    private static void AddWeight(Dictionary<string, double> weights, string key, double weight)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        weights[key.Trim()] = weights.TryGetValue(key.Trim(), out var current) ? current + weight : weight;
    }

    private static bool IsNearSameProgram(ApiReservation r, ApiEpgEvent e)
    {
        if (!string.Equals(r.ServiceName.Trim(), e.ServiceName.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (Math.Abs((r.StartTime - e.StartTime).TotalMinutes) > 15) return false;
        return NormalizeTitleKey(r.Title).Length > 0 && NormalizeTitleKey(e.Title).Length > 0;
    }

    private static bool IsUserLearningReservation(ApiReservation r) => !IsSystemReservationForSettings(r) && !string.IsNullOrWhiteSpace(r.Title);

    internal static bool IsSystemReservationForSettings(ApiReservation r)
    {
        var text = (r.Title + " " + r.Source + " " + r.Status).Trim();
        return text.Contains("番組表確認", StringComparison.OrdinalIgnoreCase)
            || text.Contains("システム", StringComparison.OrdinalIgnoreCase)
            || text.Contains("system", StringComparison.OrdinalIgnoreCase);
    }

    private static double ReservationWeight(ApiReservation r)
    {
        var status = (r.Status ?? string.Empty).Trim();
        if (status.Contains("cancel", StringComparison.OrdinalIgnoreCase) || status.Contains("キャンセル", StringComparison.OrdinalIgnoreCase) || status.Contains("cancelled", StringComparison.OrdinalIgnoreCase)) return -1.20;
        if (status.Contains("failed", StringComparison.OrdinalIgnoreCase) || status.Contains("失敗", StringComparison.OrdinalIgnoreCase)) return -0.45;
        if (status.Contains("completed", StringComparison.OrdinalIgnoreCase) || status.Contains("完了", StringComparison.OrdinalIgnoreCase)) return 1.35;
        if (!r.IsEnabled) return -0.75;
        return 1.00;
    }

    internal static string NormalizeTitleKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var s = title.Trim();
        foreach (var marker in new[] { "[字]", "[再]", "[新]", "[終]", "[映]", "[二]", "[多]", "[デ]", "[解]", "[SS]" }) s = s.Replace(marker, "", StringComparison.OrdinalIgnoreCase);
        var cut = s.IndexOfAny(new[] { '＃', '#', '▽', '◇', '◆', '「', '『', '第' });
        if (cut > 1) s = s[..cut];
        return s.Replace(" ", "", StringComparison.OrdinalIgnoreCase).Replace("　", "", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractKeywords(string title)
    {
        var normalized = NormalizeTitleKey(title);
        if (normalized.Length >= 4) yield return normalized.Length > 18 ? normalized[..18] : normalized;
        foreach (var token in new[] { "映画", "ドラマ", "新番組", "初放送", "字幕", "吹替", "アニメ", "特撮", "スポーツ", "音楽", "ドキュメンタリー", "ニュース", "バラエティ", "WOWOW", "ＮＨＫ", "NHK", "劇場版", "生中継", "FORMULA", "フォーミュラ" })
            if (title.Contains(token, StringComparison.OrdinalIgnoreCase)) yield return token;
    }
}

internal sealed record WeightedToken(string Value, double Weight);

internal static class ProgramScorer
{
    public static IEnumerable<ScoredProgram> Score(IReadOnlyList<ApiEpgEvent> epg, IReadOnlyList<ApiReservation> reservations, ViewingTrendProfile trends, AirhythmLocalSettings settings)
    {
        var now = DateTime.Now.AddMinutes(-10);
        foreach (var program in epg.Where(e => e.StartTime >= now && !string.IsNullOrWhiteSpace(e.Title)))
        {
            if (reservations.Any(r => r.IsEnabled && SameServiceAndTime(r, program))) continue;
            if (trends.IsSystemTitleLike(program.Title)) continue;
            var text = (program.Title + " " + program.Description + " " + program.Genre + " " + program.ServiceName).Trim();
            if (settings.ExcludedKeywordList.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
            var isRerun = IsRerunLike(program);
            if (isRerun && settings.RerunHandling == "exclude") continue;

            var score = 0.0;
            var reasons = new List<string>();

            if (trends.IsPositiveTitleLike(program.Title)) { score += settings.RecommendationMode == "stable" ? 42 : 34; reasons.Add("予約履歴と近い番組名"); }
            if (trends.IsNegativeTitleLike(program.Title)) { score -= 28; reasons.Add("過去に除外/失敗扱いの近似番組"); }

            var genreAffinity = trends.GenreAffinity(program.Genre);
            if (genreAffinity > 0) { score += Math.Min(settings.RecommendationMode == "genre" ? 34 : 26, 10 + genreAffinity * (settings.RecommendationMode == "genre" ? 7 : 5)); reasons.Add("録画傾向ジャンル: " + program.Genre.Trim()); }

            var serviceAffinity = trends.ServiceAffinity(program.ServiceName);
            if (serviceAffinity > 0) { score += Math.Min(18, 5 + serviceAffinity * 3); reasons.Add("よく使う局: " + program.ServiceName.Trim()); }

            var keywordAffinity = trends.KeywordAffinity(program, out var keywordHits);
            if (keywordHits.Length > 0) { score += Math.Min(24, keywordAffinity * 5); reasons.Add("履歴キーワード: " + string.Join(" / ", keywordHits.Take(3))); }

            var preferredHits = settings.PreferredKeywordList.Where(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)).Take(5).ToArray();
            if (preferredHits.Length > 0) { score += Math.Min(28, preferredHits.Length * 8); reasons.Add("優先キーワード: " + string.Join(" / ", preferredHits.Take(3))); }
            if (program.Title.Contains("[新]", StringComparison.OrdinalIgnoreCase) || program.Title.Contains("新番組", StringComparison.OrdinalIgnoreCase)) { score += settings.RecommendationMode == "new" ? 20 : 12; reasons.Add("新番組"); }
            if (program.Title.Contains("初放送", StringComparison.OrdinalIgnoreCase) || program.Description.Contains("初放送", StringComparison.OrdinalIgnoreCase)) { score += settings.RecommendationMode == "new" ? 18 : 10; reasons.Add("初放送"); }
            if (program.Title.Contains("映画", StringComparison.OrdinalIgnoreCase) || program.Genre.Contains("映画", StringComparison.OrdinalIgnoreCase)) { score += 6; reasons.Add("映画枠"); }
            if (settings.RecommendationMode == "soon") { var hours = Math.Max(0, (program.StartTime - DateTime.Now).TotalHours); if (hours <= 24) { score += 10; reasons.Add("直近放送"); } else if (hours <= 72) { score += 4; reasons.Add("近日放送"); } }
            if (program.StartTime.Hour >= 18 || program.StartTime.Hour < 4) score += settings.BoostLateNight ? 7 : 2;
            if (isRerun && settings.RerunHandling == "down") { score -= 12; reasons.Add("再放送らしき番組を弱める"); }
            if (program.EndTime > program.StartTime && (program.EndTime - program.StartTime).TotalMinutes >= 90) score += 3;
            if (trends.LearningSamples < 5) { score *= 0.75; reasons.Add("学習材料少なめ"); }

            var finalScore = Math.Max(0, Math.Min(100, (int)Math.Round(score)));
            if (finalScore < 12) continue;
            yield return new ScoredProgram(program, finalScore, reasons.Count == 0 ? "AI-rhythm推定一致" : string.Join("、", reasons));
        }
    }

    private static bool IsRerunLike(ApiEpgEvent program)
    {
        var text = (program.Title + " " + program.Description).Trim();
        return text.Contains("[再]", StringComparison.OrdinalIgnoreCase)
            || text.Contains("再放送", StringComparison.OrdinalIgnoreCase)
            || text.Contains("アンコール", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameServiceAndTime(ApiReservation r, ApiEpgEvent e)
        => string.Equals(r.ServiceName.Trim(), e.ServiceName.Trim(), StringComparison.OrdinalIgnoreCase) && Math.Abs((r.StartTime - e.StartTime).TotalMinutes) <= 5;
}
internal static class StringExtensions
{
    public static string DefaultIfBlank(this string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
