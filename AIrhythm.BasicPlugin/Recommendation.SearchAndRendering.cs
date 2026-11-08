namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmRecommendationEngine
{
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
        var form = $"<form class='rhythm-form' method='get' action='/plugin/{AIrhythmIdentity.Route}#rhythmSearch'><label for='rhythmInput'>気分・時間・目的を言葉で入力</label><div class='rhythm-row'><input id='rhythmInput' name='rhythm' maxlength='160' autocomplete='off' value='{AIrhythmHtml.Encode(normalized)}'><button type='submit'>探す</button></div></form>";

        if (!string.IsNullOrWhiteSpace(validationError))
            return $"{form}<div class='rhythm-chips'>{chipHtml}</div>{recentHtml}{interestHtml}<div class='rhythm-alert'>{AIrhythmHtml.Encode(validationError)}</div>";
        if (string.IsNullOrWhiteSpace(normalized))
            return $"{form}<div class='rhythm-chips'>{chipHtml}</div>{recentHtml}{interestHtml}<div class='rhythm-guide'>番組名が決まっていなくても、今の気分や空き時間から探せます。</div>";

        var now = DateTimeOffset.Now;
        var preferenceRecordings = PreferenceRecordingHistory(snapshot).ToArray();
        var preferenceReservations = snapshot.Reservations
            .Where(x => !AIrhythmPreferenceOriginPolicy.IsExcludedReservation(x)).ToArray();
        var familiarServices = preferenceRecordings.Select(ServiceIdentityOf)
            .Concat(preferenceReservations.Select(ServiceIdentityOf))
            .Where(x => x.IsValid)
            .GroupBy(x => x)
            .OrderByDescending(x => x.Count())
            .Take(8)
            .Select(x => x.Key)
            .ToHashSet();

        var timeIntent = ResolveRecommendationTimeIntent(normalized);
        var timeWindow = timeIntent == RecommendationTimeIntent.None
            ? (RecommendationTimeWindow?)null
            : GetRecommendationTimeWindow(now, timeIntent);
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

        var reserved = preferenceReservations.Select(x => CanonicalWorkKey(x, identityContext)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recorded = preferenceRecordings.Select(x => CanonicalWorkKey(x, identityContext)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

            if (timeWindow.HasValue)
            {
                if (!timeWindow.Value.Contains(item.Start)) continue;
                score += 12;
                reasons.Add(timeIntent switch
                {
                    RecommendationTimeIntent.Today => "今日中に放送",
                    RecommendationTimeIntent.Soon => "これから放送",
                    RecommendationTimeIntent.Bedtime => "寝る前の時間に放送",
                    _ => "今夜に放送"
                });
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

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var corroboratedHistory = snapshot.History
            .Where(item => IsCorroboratedParenthesizedWorkSequence(
                item.ProgramTitle, ServiceIdentityOf(item), identityContext.NumericLocalValuesByService))
            .Select(item => new
            {
                Key = CanonicalWorkKey(item, identityContext),
                Title = CanonicalWorkDisplayTitle(item, identityContext),
                Raw = item.ProgramTitle
            })
            .Where(item => item.Key.Length > 0)
            .ToArray();
        var corroboratedGroups = corroboratedHistory
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(group => $"{CompactIdentity(group.First().Title)}:{group.Count()}");
        // 録画実績・復旧 — This audit includes every successful recording, even when no sequence was corroborated. It
        // exposes fragmented work identities without rewriting historical facts or learning.
        var recordingIdentityGroups = snapshot.History
            .Select(item => new
            {
                Raw = item.ProgramTitle,
                Work = CanonicalWorkKey(item, identityContext),
                Genre = CanonicalEvidenceFacts(item, identityContext).GenreKey
            })
            .Where(item => item.Work.Length > 0)
            .GroupBy(item => item.Work, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .Select(group => $"{group.Key}:{group.Count()}");
        // 録画実績・復旧 — Exact, read-only stage trace for EVERY persisted successful recording. Output is batched to
        // bound log event count; no title filter, guessed sibling, display-only remapping, fact mutation, or
        // additional normalization policy. All keys are emitted per fact so an apparent ranking split can be
        // traced to the exact stage without needing access to the user's private store.
        var recordingTrace = snapshot.History.Select((item, ordinal) =>
        {
            var service = ServiceIdentityOf(item);
            var raw = item.ProgramTitle ?? string.Empty;
            var evaluation = PreEvaluationTitle(raw, identityContext);
            var local = LocalWorkKeyCached(evaluation, service, identityContext);
            var alias = CanonicalizeLocalWorkKey(local, service, identityContext.WorkAliases);
            var promotionLookup = CompactIdentity(NormalizationLearnerComparableTitle(evaluation));
            string? candidate = null;
            var hasPromotion = promotionLookup.Length > 0
                && identityContext.NormalizationPromotions.TryGetValue(promotionLookup, out candidate)
                && !string.IsNullOrWhiteSpace(candidate);
            var final = CanonicalWorkKey(item, identityContext);
            var chosen = hasPromotion && string.Equals(final, candidate, StringComparison.OrdinalIgnoreCase)
                ? "promotion" : string.Equals(final, alias, StringComparison.OrdinalIgnoreCase)
                    ? "local_or_alias" : "other";
            return new
            {
                n = ordinal,
                station = item.ServiceName,
                service = service.ToString(),
                start = (item.ActualStart ?? item.Start).ToString("O"),
                raw,
                evaluation,
                local,
                alias,
                promotionLookup,
                promotion = hasPromotion ? candidate : string.Empty,
                selected = chosen,
                final,
                display = CanonicalWorkDisplayTitle(item, identityContext)
            };
        }).ToArray();
        const int traceBatchSize = 16;
        for (var offset = 0; offset < recordingTrace.Length; offset += traceBatchSize)
        {
            var batch = recordingTrace.Skip(offset).Take(traceBatchSize).ToArray();
            AIrhythmDataState.WriteDeveloperLog(
                $"CANONICAL_RECORDING_IDENTITY_STAGE_TRACE range={offset}-{offset + batch.Length - 1} " +
                $"rows={JsonSerializer.Serialize(batch)} storageMutation=False");
        }
        AIrhythmDataState.WriteDeveloperLog(
            $"CANONICAL_RECORDING_IDENTITY_STAGE_TRACE_SUMMARY traced={recordingTrace.Length} " +
            $"total={snapshot.History.Count} groups={recordingTotals.Count} batchSize={traceBatchSize} " +
            $"scope=all_persisted_success_facts exactStageKeys=True readonly=True");
        AIrhythmDataState.WriteDeveloperLog(
            $"CANONICAL_RECORDING_ALL_FACT_IDENTITY_AUDIT total={snapshot.History.Count} grouped={recordingTotals.Sum(x => x.RecordingCount)} uniqueWorks={recordingTotals.Count} top24=[{string.Join(",", recordingIdentityGroups)}] numericEpgServices={BuildNumericParenthesizedLocalSequenceValuesByService(snapshot.Events).Count} numericHistoryServices={identityContext.NumericEvidenceValuesByService.Count} numericMergedServices={identityContext.NumericLocalValuesByService.Count} storageMutation=False");
        AIrhythmDataState.WriteDeveloperLog(
            $"CANONICAL_RECORDING_EPISODE_IDENTITY_AUDIT corroboratedFacts={corroboratedHistory.Length} uniqueWorks={corroboratedHistory.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()} top=[{string.Join(",", corroboratedGroups)}] historyTotal={totalRecordingCount} purpose=detect_numeric_episode_fragmentation_no_title_rules storageMutation=False");
#endif

        #if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog(
            $"CANONICAL_RECORDING_DASHBOARD_SUMMARY total={totalRecordingCount} totalTop10=[{string.Join(",", recordingTotals.Take(10).Select(x => $"{CompactIdentity(x.DisplayTitle)}:{x.RecordingCount}"))}] genreTop10=[{string.Join(",", recordingAggregates.Take(10).Select(x => $"{x.GenreKey}:{x.RecordingCount}"))}] expandedGenres=[{string.Join(",", topGenres.Select(x => x.GenreKey))}] titleIdentity=common_canonical_work recordingCount=persistent_success_recording_facts layout=station_donut_weekday_line,total_horizontal_genre_vertical,genre1_6x10");
        #endif

        var html = new StringBuilder();
        // 処理条件 — Row 1: two large distribution cards. Never dilute these with a third time-of-day card.
        html.Append($"<article class='chart-card dashboard-span-3 distribution-card chart-accent-orange'><div class='chart-head'><h3>よく録画する放送局</h3><span>分析できる録画 {totalRecordingCount}本</span></div>{Donut(stationTop, palette, "本", "放送局別の録画情報がまだありません", "large-donut")}</article>");
        html.Append($"<article class='chart-card dashboard-span-3 distribution-card chart-accent-cyan'><div class='chart-head'><h3>録画する曜日</h3><span>録画実績</span></div>{WeekdayLine(weekdays, "曜日別の録画情報がまだありません")}</article>");

        // 文字列解析 — Row 2: the factual all-title ranking is intentionally the left/main card; genre composition is
        // beside it.
        html.Append($"<article class='chart-card dashboard-span-3 total-ranking-card chart-accent-blue'><div class='chart-head'><h3>総合録画 Top10</h3><span>録画本数</span></div>{RankedBars(totalTop10, palette, "録画した番組がまだありません", true, false, totalRecordingCount, 10)}</article>");
        html.Append($"<article class='chart-card dashboard-span-3 genre-ranking-card chart-accent-green'><div class='chart-head'><h3>よく録画するジャンル Top10</h3><span>本数・割合</span></div>{VerticalBars(genreTop10, palette.Reverse().ToArray(), "ジャンル別の録画情報がまだありません", totalRecordingCount, 10)}</article>");

        // 文字列解析 — Rows 3-4: top six genres, three equal cards per row, each with factual title Top10.
        for (var i = 0; i < topGenres.Length; i++)
            html.Append(GenreWorkCard(topGenres[i], i + 1, totalRecordingCount, palette));

        return html.ToString();
    }

    private static string SeriesDisplayTitle(string? value)
    {
        var parts = ParseCanonicalTitleParts(value);
        var work = parts.Work;
        if (string.IsNullOrWhiteSpace(work)) return string.Empty;
        var display = Regex.Replace(work, @"\s+", " ").Trim(' ', '-', '－', ':', '：');
        // 録画実績・復旧 — Saved promotional recordings retain a visible programme-role suffix instead of being
        // presented as the advertised main episode in work rankings.
        return parts.BroadcastAttribute.StartsWith("derived:pr", StringComparison.Ordinal)
            ? display + " PR"
            : display;
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
        // 処理条件 — Tokens are consecutive Unicode letters/digits, minimum length two, invariant lowercase.
        var source = value ?? string.Empty;
        var start = -1;
        for (var i = 0; i <= source.Length; i++)
        {
            var isTokenChar = i < source.Length && char.IsLetterOrDigit(source[i]);
            if (isTokenChar)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start < 0) continue;
            var length = i - start;
            if (length >= 2)
                yield return source.Substring(start, length).ToLowerInvariant();
            start = -1;
        }
    }


}
