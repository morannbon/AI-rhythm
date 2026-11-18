namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmRecommendationEngine
{
    private static AIrhythmEventIdentity EventIdentityOf(TvAirProgramEventDto value)
        => new(value.NetworkId, value.TransportStreamId, value.ServiceId, value.EventNumber, value.Start);



    internal static string StripNonIdentityBroadcastAnnotations(string value)
    {
        // 文字列解析 — The pattern below can only match ASCII '['. Most titles carry no such broadcast tag, so bypass
        // Regex entirely when a match is impossible. This is an allocation-only fast path.
        if (value.IndexOf('[') < 0)
            return value;

        // 作品識別 — 括弧内を一律に捨てない。Season2、作品番号、固有副題など作品識別に必要な情報まで 消してしまうため、放送属性として意味が確定している短いタグだけを除去する。 ARIB decode /
        // EPG presentation attributes are not work identity. Retain this list explicit and conservative: only
        // tags whose meaning is known as broadcast/presentation/classification metadata are removed here.
        return Regex.Replace(
            value,
            @"\[\s*(?:字|解|デ|双|多|二|S|SS|B|N|天|交|映|新|終|再|初|生|録|HV|SD|無|料|前|後|手|吹|字幕|PG12|R15\+)\s*\]",
            " ",
            RegexOptions.IgnoreCase);
    }

    // 外部情報 — A programme whose declared purpose is to advertise a quoted Work is a different broadcast item
    // from that Work. Classify its title structure here so Work Identity, recommendation admission and
    // external relation reporting share the same decision. Unqualified words resembling PR are not enough;
    // quote and marker boundaries must both be explicit. This is not a broadcaster or programme-title
    // exception list.
    private static bool TryGetQuotedPromotionWork(string normalized, out string advertisedWork)
    {
        advertisedWork = string.Empty;
        if (normalized.IndexOf('「') < 0 && normalized.IndexOf('『') < 0)
            return false;
        if (normalized.IndexOf("PR", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        // 作品識別 — e.g. "1分PR『...』 次回放送". The variable duration is programme metadata, not a Work. The quoted
        // title may be followed by scheduling text.
        var leading = Regex.Match(normalized,
            @"^(?:(?:[0-9一二三四五六七八九十]+)\s*分\s*)?PR\s*[「『](?<work>[^」』]{2,120})[」』]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (leading.Success)
        {
            advertisedWork = leading.Groups["work"].Value.Trim();
            return true;
        }

        // 作品識別 — e.g. "【ドラマ】夜ドラ『...』PR (第2週)". A quoted Work and a terminal standalone PR marker are required;
        // the week tag is scheduling metadata, never a numbered episode or a separate Work.
        var trailing = Regex.Match(normalized,
            @"^(?<prefix>[^「『]{0,100})[「『](?<work>[^」』]{2,120})[」』]\s*PR(?:\s*\(\s*第\s*[0-9]+\s*週\s*\))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!trailing.Success)
            return false;
        var prefix = trailing.Groups["prefix"].Value.Trim();
        var quoted = trailing.Groups["work"].Value.Trim();
        // 作品識別 — A broadcaster slot/content-type wrapper may be discarded, but a title prefix of unknown role
        // may be the real Work with a quoted subtitle. Retain that prefix instead of rewriting a Work to its
        // subtitle.
        var bracketSlot = Regex.Match(prefix, @"^【[^【】]{2,}】");
        var prefixIsContainer = prefix.Length == 0
            || bracketSlot.Success
            || LooksLikeBroadcastContainerLabel(prefix)
            || prefix.Equals("映画", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("アニメ", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("ドラマ", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("ドキュメンタリー", StringComparison.OrdinalIgnoreCase);
        advertisedWork = prefixIsContainer ? quoted : (prefix + " " + quoted).Trim();
        return true;
    }

    private readonly record struct CanonicalTitleParts(
        string Container,
        string ContentType,
        string Series,
        string Work,
        string SeasonOrEdition,
        string Episode,
        string Part,
        string Subtitle,
        string Event,
        string Stage,
        string Location,
        string BroadcastAttribute,
        string HostAttribute);

    // 作品識別 — 共通Work Identity正本の内部パーサー。画面・スコア・集計から直接使わず、 CanonicalWorkKey 経由で利用する。Work だけを削り出すのではなく、
    // Container / Work / Episode / Part / Event / Stage などを別軸として保持する。
    // 個別タイトル辞書ではなく、明示括弧・区切り・ラウンド/セッション等の構造だけを扱う。
    private static bool TrySimpleEvidenceSeriesKeyFastPath(string value, out string key)
    {
        key = string.Empty;
        if (value.Length == 0)
            return true;

        // 作品識別 — Do not bypass the Work parser when a tournament day/part may need a separate axis.
        if (MayContainTournamentScheduleSuffix(value))
            return false;

        // 作品識別 — Admit only characters whose FormKC normalization cannot create a structural marker. This
        // deliberately leaves ambiguous punctuation/spacing on the established parser path.
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                continue;
            if (ch is '!' or '！' or '?' or '？' or 'ー')
                continue;
            return false;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC);
        if (normalized.Contains("新番組", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("初回", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("最終回", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("再放送", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("アンコール", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("リピート", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("一挙放送", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("生中継", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("生放送", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("中継", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("準々決勝", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("準決勝", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("決勝", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("予選", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("決定戦", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("話", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("回", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("前編", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("後編", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("部", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ep", StringComparison.OrdinalIgnoreCase))
            return false;

        key = CompactIdentity(normalized);
        return true;
    }

    // 作品識別 — This is a lexical fallback gate only. The canonical parser below exclusively decides whether
    // a tournament Work has an explicit day/session or broadcast-part suffix. No sport-specific title list is used.
    private static bool MayContainTournamentScheduleSuffix(string value)
        => value.Contains("Part", StringComparison.OrdinalIgnoreCase)
            || value.Contains("パート", StringComparison.Ordinal)
            || value.Contains("最終日", StringComparison.Ordinal)
            || value.Contains("初日", StringComparison.Ordinal)
            || value.Contains("日目", StringComparison.Ordinal)
            || (value.IndexOf('第') >= 0 && value.IndexOf('日') >= 0);

    // 作品識別 — The parser owns whether Part is a broadcast session or the Work itself.
    // A numbered film/edition in Work is a distinct work, not a recurring headline to be
    // inferred from a shared word prefix. Both selected-evidence aliases and persisted
    // normalization proposals must obey this same decision before changing a Work key.
    private static readonly Regex NumberedStandaloneWorkEdition = new(
        @"(?<![\p{L}\p{N}])(?:part|パート|(?:the\s+)?movie|vol(?:ume)?\.?)\s*[-‐‑–—−－]?\s*[0-9０-９]{1,3}(?=$|[^\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool MustPreserveCompleteWorkIdentity(string? title, bool explicitFilm, string? recordedGenre = null)
    {
        if (explicitFilm || NormalizeGenre(recordedGenre) == "映画")
            return true;
        if (string.IsNullOrWhiteSpace(title) || !MayContainStandaloneWorkEdition(title))
            return false;
        // Only the Work axis is protected. A real tournament Part remains on the
        // separate broadcast-session axis and continues to share its tournament Work.
        return NumberedStandaloneWorkEdition.IsMatch(ParseCanonicalTitleParts(title).Work);
    }

    private static bool MayContainStandaloneWorkEdition(string title)
        => title.Contains("Part", StringComparison.OrdinalIgnoreCase)
            || title.Contains("パート", StringComparison.Ordinal)
            || title.Contains("Movie", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Vol", StringComparison.OrdinalIgnoreCase);

    // 作品識別 — The structural probe operates on FormKC + canonical whitespace, so terminal derivative markers
    // can be checked without constructing a regex match for each scored EPG title. The canonical parser
    // remains the only owner of derivative classification and the separate PR/preview Work identity.
    private static readonly string[] TerminalDerivativeMarkers = { "PR", "予告", "みどころ", "総集編" };

    private static bool HasTerminalDerivativeMarker(string normalized)
    {
        ReadOnlySpan<char> value = normalized.AsSpan();
        foreach (var marker in TerminalDerivativeMarkers)
        {
            if (value.Length > marker.Length
                && value[^(marker.Length + 1)] == ' '
                && value.EndsWith(marker.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TryStructurallyInertEvidenceSeriesKeyFastPath(string value, out string key, out string normalizedForFallback)
    {
        key = string.Empty;
        normalizedForFallback = string.Empty;
        if (value.Length == 0)
            return true;

        // 作品識別 — FormKC can turn compatibility characters into parser-significant ASCII/Japanese markers, so
        // structural proof must use the same normalized text as the canonical parser. Unlike
        // TrySimpleEvidenceSeriesKeyFastPath, ordinary spaces and punctuation are allowed here when none of the
        // existing parser branches can change Work.
        var normalized = NormalizeCanonicalWhitespace(value.Normalize(NormalizationForm.FormKC));
        // 作品識別 — Pass this immutable NFKC/whitespace-normalized value through to the fallback parser, avoiding a
        // second NFKC + whitespace normalization for every rejected title.
        normalizedForFallback = normalized;

        // 作品識別 — The parser is the only owner of tournament day/part separation. This fast path may
        // shortcut only titles for which that separation is structurally impossible.
        if (MayContainTournamentScheduleSuffix(normalized))
            return false;

        // 作品識別 — Explicit event numbers retain their distinct Work/episode structure.
        if (normalized.IndexOf('第') >= 0 && normalized.IndexOf('戦') >= 0
            && normalized.Any(ch => ch is >= '0' and <= '9' or >= '０' and <= '９')
            && Regex.IsMatch(normalized, @"第\s*[0-9０-９]{1,3}\s*戦"))
            return false;

        // 作品識別 — Any bracket/quote/container/episode/route marker can activate an established structural branch
        // or broadcast-annotation stripping. Fall back instead of duplicating that logic.
        if (normalized.IndexOf('[') >= 0 || normalized.IndexOf(']') >= 0
            || normalized.IndexOf('【') >= 0 || normalized.IndexOf('】') >= 0
            || normalized.IndexOf('<') >= 0 || normalized.IndexOf('>') >= 0
            || normalized.IndexOf('「') >= 0 || normalized.IndexOf('」') >= 0
            || normalized.IndexOf('『') >= 0 || normalized.IndexOf('』') >= 0
            || MayHaveAiredSupplementaryDetail(normalized)
            || normalized.IndexOf('(') >= 0 || normalized.IndexOf(')') >= 0
            || normalized.IndexOf('（') >= 0 || normalized.IndexOf('）') >= 0
            || normalized.IndexOf('#') >= 0 || normalized.IndexOf('＃') >= 0 || normalized.IndexOf('♯') >= 0
            || normalized.IndexOf('・') >= 0
            || normalized.IndexOf('~') >= 0 || normalized.IndexOf('～') >= 0
            || normalized.IndexOf('〜') >= 0 || normalized.IndexOf('→') >= 0)
            return false;

        // 作品識別 — These terms are exactly the generic semantic gates that can remove or split text in
        // ParseCanonicalTitleParts. Presence is only a reason to use the canonical parser; it is never itself a
        // Work-identity decision.
        if (normalized.Contains("新番組", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("初回", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("最終回", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("再放送", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("アンコール", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("リピート", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("一挙放送", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("生中継", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("生放送", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("中継", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("準々決勝", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("準決勝", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("決勝", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("予選", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("決定戦", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("フリー走行", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("スプリント", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("話", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("回", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("前編", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("後編", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("部", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ep", StringComparison.OrdinalIgnoreCase))
            return false;

        // 作品識別 — Terminal programme-derivative markers are meaningful even without quotes. Never bypass the
        // common parser and inadvertently conflate a PR with Work.
        if (HasTerminalDerivativeMarker(normalized))
            return false;

        // 作品識別 — Space-separated broadcast containers are the one parser branch that can change Work without a
        // dedicated punctuation marker. Prove that the first field is not a known generic container label before
        // bypassing the parser.
        var firstSpace = normalized.IndexOf(' ');
        if (firstSpace >= 2 && firstSpace <= 28 && firstSpace < normalized.Length - 2)
        {
            var prefixSpan = normalized.AsSpan(0, firstSpace);
            foreach (var suffix in BroadcastContainerSuffixes)
                if (prefixSpan.EndsWith(suffix.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    return false;
        }

        // 作品識別 — The plain-tail subtitle branch is intentionally conservative. If any of its generic
        // promo/feature signals are present, preserve the established parser path. With no such signal,
        // whitespace and punctuation are discarded by CompactIdentity exactly as they would be after the
        // parser's final whitespace/trim normalization.
        if (normalized.Contains('!') || normalized.Contains('！')
            || normalized.Contains('?') || normalized.Contains('？')
            || normalized.Contains('＆') || normalized.Contains('&')
            || normalized.Contains("VS", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("参戦", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("大暴れ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("密着", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("特集", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("クイズ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ランキング", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("スペシャル", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("SP", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("初公開", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("徹底", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("衝撃", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("爆笑", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("今夜", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("今週", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ゲスト", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ロケ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("挑戦", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("対決", StringComparison.OrdinalIgnoreCase))
            return false;

        key = CompactIdentity(normalized);
        return true;
    }

    private static string EvidenceSeriesKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // 保存・読込 — EvidenceSeriesKey is a pure title-only transform. Retain its result across render snapshots so
        // an external-evidence generation change does not discard bf252's Work Identity allocation savings.
        // Dynamic aliases/promotions are applied after this key.
        if (SharedEvidenceSeriesKeys.TryGetValue(value, out var cached))
            return cached;

        string computed;
        if (TrySimpleEvidenceSeriesKeyFastPath(value, out var simpleKey))
            computed = simpleKey;
        else
        {
            var inert = TryStructurallyInertEvidenceSeriesKeyFastPath(value, out var inertKey, out var normalizedForFallback);
            if (inert)
                computed = inertKey;
            else
            {
                // 作品識別 — The structural fast path has already performed FormKC and normalized whitespace. Reuse that
                // exact text; the canonical parser still owns every annotation, attribute and work-boundary decision.
                var parts = ParseCanonicalTitleParts(normalizedForFallback, alreadyNormalized: true);
                // 作品識別 — Derived broadcast items retain the same underlying Work, but must not collapse into the actual
                // programme when facts/recommendations are keyed. This preserves the historical suffix representation
                // without teaching a programme-specific alias or silently merging a promotion with an episode.
                computed = CompactIdentity(parts.Work);
                if (parts.BroadcastAttribute.StartsWith("derived:", StringComparison.Ordinal))
                    computed += parts.BroadcastAttribute["derived:".Length..];
            }
        }

        if (SharedEvidenceSeriesKeys.Count < SharedTitleTransformCacheLimit)
            SharedEvidenceSeriesKeys.TryAdd(value, computed);
        return computed;
    }

    private static readonly string[] BroadcastContainerSuffixes =
    {
        "アワー", "劇場", "ロードショー", "プレミアム", "シネマ", "アニメイズム",
        "ドラマ", "映画", "シアター", "セレクション", "コレクション", "特集", "映画祭", "ショーケース", "サスペンス"
    };

    // 作品識別 — Status-only EPG entries are not identifiable works. No per-programme exceptions: only
    // full-title, common unassigned-broadcast status labels are recognized.
    private static bool IsUnspecifiedEpgTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        var span = title.AsSpan().Trim();
        return span.Equals("放送番組未定".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Equals("番組未定".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Equals("番組情報未定".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Equals("放送内容未定".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Equals("番組内容未定".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBroadcastContainerLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.IndexOf(' ') >= 0 || normalized.IndexOf('　') >= 0)
            normalized = normalized.Replace(" ", string.Empty).Replace("　", string.Empty);
        if (normalized.Length < 2 || normalized.Length > 28) return false;

        // 作品識別 — 独立した編成枠の接尾語、または「連続ドラマ＋一文字の枠識別子」という
        // 文法だけを識別する。作品固有名や放送局名は登録しない。
        return BroadcastContainerSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            || Regex.IsMatch(normalized,
                @"^(?:連続|朝|夜)(?:テレビ|TV)?ドラマ[A-Z]$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // 作品識別 — A fully delimited broadcast-slot category is stronger evidence than an episode count.
    // Classify only a grammatical category followed by an independently identifiable Work. A bracketed
    // Work name, a genre-like substring, or an unknown bracket label does not establish this boundary.
    private static bool IsExplicitLeadingBroadcastSlot(string container, string remainder)
    {
        if (string.IsNullOrWhiteSpace(container) || remainder.Count(char.IsLetterOrDigit) < 3)
            return false;
        var label = container.Normalize(NormalizationForm.FormKC).Trim();
        return Regex.IsMatch(label,
            @"^(?:(?:連続|朝|夜)(?:テレビ|TV)?(?:小説|ドラマ)|(?:テレビ|TV)(?:小説|ドラマ))$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || LooksLikeBroadcastContainerLabel(label);
    }

    private readonly record struct CanonicalTitleStructureSignals(
        bool MayHaveExplicitBroadcastBracket,
        bool MayHaveLiveBroadcast,
        bool MayHaveTerminalStage,
        bool MayHaveLifecycleAttribute);

    private static CanonicalTitleStructureSignals DetectCanonicalTitleStructureSignals(string normalized)
    {
        // 作品識別 — This pass is the single cheap structural gate for regex branches whose markers cannot be
        // introduced by later parsing. It never decides Work Identity itself; it only proves when an expensive
        // regex branch is impossible for this title.
        var mayHaveExplicitBroadcastBracket = normalized.IndexOf('【') >= 0
            && (normalized.Contains("初放送", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("初登場", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("初公開", StringComparison.OrdinalIgnoreCase));
        var mayHaveLiveBroadcast = normalized.Contains("生中継", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("生放送", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("中継", StringComparison.OrdinalIgnoreCase);
        var mayHaveTerminalStage = normalized.Contains("準々決勝", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("準決勝", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("決勝", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("予選", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("決定戦", StringComparison.OrdinalIgnoreCase);
        var mayHaveLifecycleAttribute = normalized.Contains("新番組", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("初回", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("最終回", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("再放送", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("アンコール", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("リピート", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("一挙放送", StringComparison.OrdinalIgnoreCase);
        return new(
            mayHaveExplicitBroadcastBracket,
            mayHaveLiveBroadcast,
            mayHaveTerminalStage,
            mayHaveLifecycleAttribute);
    }

    private static string NormalizeCanonicalWhitespace(string value)
    {
        if (value.Length == 0)
            return string.Empty;

        // 文字列解析 — Regex.Replace(@"\s+", " ").Trim() is semantically required only when the title actually
        // contains non-ASCII whitespace or repeated internal ASCII spaces. Most EPG titles are already canonical
        // single-space text, so keep that hot path allocation-light while preserving the established Regex path
        // for every non-canonical whitespace shape.
        var first = 0;
        while (first < value.Length && value[first] == ' ') first++;
        var last = value.Length - 1;
        while (last >= first && value[last] == ' ') last--;
        if (last < first)
            return string.Empty;

        var previousAsciiSpace = false;
        for (var i = first; i <= last; i++)
        {
            var ch = value[i];
            if (ch == ' ')
            {
                if (previousAsciiSpace)
                    return Regex.Replace(value, @"\s+", " ").Trim();
                previousAsciiSpace = true;
                continue;
            }

            previousAsciiSpace = false;
            if (char.IsWhiteSpace(ch))
                return Regex.Replace(value, @"\s+", " ").Trim();
        }

        return first == 0 && last == value.Length - 1
            ? value
            : value[first..(last + 1)];
    }


    private static bool TryStripTrailingHostWeekday(string value, out string withoutWeekday, out string hostAttribute)
    {
        withoutWeekday = value;
        hostAttribute = string.Empty;
        if (value.Length == 0)
            return false;

        var close = value.Length - 1;
        while (close >= 0 && char.IsWhiteSpace(value[close])) close--;
        if (close < 0 || (value[close] != ')' && value[close] != '）'))
            return false;

        var day = close - 1;
        while (day >= 0 && char.IsWhiteSpace(value[day])) day--;
        if (day < 0 || value[day] is not ('月' or '火' or '水' or '木' or '金' or '土' or '日'))
            return false;

        var open = day - 1;
        while (open >= 0 && char.IsWhiteSpace(value[open])) open--;
        if (open < 0 || (value[open] != '(' && value[open] != '（'))
            return false;

        // 処理条件 — This grammar permits whitespace only around the weekday token.
        var matchStart = open;
        while (matchStart > 0 && char.IsWhiteSpace(value[matchStart - 1])) matchStart--;

        hostAttribute = value[open..(close + 1)].Trim();
        withoutWeekday = value[..matchStart].TrimEnd();
        return true;
    }


    private static bool TrySplitTrailingBracketSubtitle(string value, bool requirePrefix, out string prefix, out string subtitle)
    {
        prefix = value;
        subtitle = string.Empty;
        if (value.Length == 0)
            return false;

        var close = value.Length - 1;
        while (close >= 0 && char.IsWhiteSpace(value[close])) close--;
        if (close < 0)
            return false;

        char openChar;
        char closeChar;
        if (value[close] == '】')
        {
            openChar = '【';
            closeChar = '】';
        }
        else if (value[close] == ']')
        {
            openChar = '[';
            closeChar = ']';
        }
        else
        {
            return false;
        }

        var open = value.LastIndexOf(openChar, close - 1);
        if (open < 0 || close - open - 1 < 2)
            return false;
        if (requirePrefix && open == 0)
            return false;

        // 文字列解析 — The established regex forbids the same bracket pair inside the subtitle body.
        // LastIndexOf(openChar) already proves there is no later opening bracket; reject any earlier closing
        // bracket inside the selected suffix as the regex did.
        for (var i = open + 1; i < close; i++)
        {
            if (value[i] == openChar || value[i] == closeChar)
                return false;
        }

        prefix = value[..open].TrimEnd();
        subtitle = value[open..(close + 1)].Trim();
        return true;
    }

    // 作品識別 — Parenthesized episode headlines are presentation details when the title itself
    // has an independently named programme followed by complete 【...】 blocks, an explicit "ほか",
    // or a ▼ segment inside the block. Do not treat leading 【...】 title/slot labels as episodes.
    // Only recognize complete balanced groups; ambiguous brackets remain in Work unchanged.
    private static bool TrySplitProgrammeEpisodeBrackets(string value, out string work, out string detail)
    {
        work = value;
        detail = string.Empty;
        var open = value.IndexOf('【');
        if (open < 2) return false;
        var prefix = value[..open].TrimEnd();
        if (prefix.Count(char.IsLetterOrDigit) < 3) return false;
        var close = value.IndexOf('】', open + 1);
        if (close < open + 3 || value.AsSpan(open + 1, close - open - 1).IndexOfAny('【', '】') >= 0)
            return false;
        var firstBody = value[(open + 1)..close];
        var suffix = value[(close + 1)..].Trim();
        // An explicitly numbered airing after the bracket belongs to Episode, not Subtitle.
        // Carry it forward to the same canonical episode parser instead of creating a competing path.
        var numberedTail = Regex.IsMatch(suffix,
            @"^(?:[#＃♯]\s*[0-9]{1,3}|第\s*[0-9]{1,3}\s*話)$");
        var hasEpisodeBoundary = firstBody.IndexOf('▼') >= 0
            || suffix.Equals("ほか", StringComparison.Ordinal) || numberedTail;
        var scan = suffix;
        if (scan.StartsWith("【", StringComparison.Ordinal))
        {
            hasEpisodeBoundary = true;
            while (scan.Length > 0)
            {
                if (scan[0] != '【') return false;
                var end = scan.IndexOf('】', 1);
                if (end < 3 || scan.AsSpan(1, end - 1).IndexOfAny('【', '】') >= 0)
                    return false;
                scan = scan[(end + 1)..].Trim();
            }
        }
        else if (suffix.Length > 0 && !suffix.Equals("ほか", StringComparison.Ordinal) && !numberedTail)
        {
            // A substantive trailing phrase may belong to the Work; never guess its role.
            return false;
        }
        if (!hasEpisodeBoundary) return false;
        work = numberedTail ? $"{prefix} {suffix}" : prefix;
        detail = numberedTail ? value[open..(close + 1)] : value[open..].Trim();
        return true;
    }

    // 作品識別 — A standalone exclamation/question boundary followed by whitespace and an
    // independent description separates a recurring programme's title from that day's topic.
    // Punctuation internal to a title or a terminal exclamation without a following segment
    // is not a Work boundary. The punctuation itself remains in the programme name.
    private static bool TrySplitPunctuatedProgrammeTopic(string value, out string work, out string topic)
    {
        work = value;
        topic = string.Empty;
        for (var i = 2; i < value.Length - 7; i++)
        {
            if (value[i] is not ('!' or '！' or '?' or '？')
                || !char.IsWhiteSpace(value[i + 1]))
                continue;
            var prefix = value[..(i + 1)].TrimEnd();
            var suffix = value[(i + 1)..].TrimStart();
            if (prefix.Count(char.IsLetterOrDigit) < 4 || suffix.Count(char.IsLetterOrDigit) < 6)
                continue;
            work = prefix;
            topic = suffix;
            return true;
        }
        return false;
    }

    // 作品識別 — Explicit <slot> / ＜slot＞ syntax does not make the slot the Work.
    // Only widely identifiable programme-container labels are recognized.
    private static bool TrySplitAngleBracketBroadcastContainer(string value, out string container, out string work)
    {
        container = string.Empty;
        work = value;
        if (value.Length < 8 || value[0] is not ('<' or '＜')) return false;
        var end = value.AsSpan().IndexOfAny('>', '＞');
        if (end < 3 || end > 32) return false;
        var label = value[1..end].Trim();
        var remainder = value[(end + 1)..].Trim();
        if (!LooksLikeBroadcastContainerLabel(label) || remainder.Count(char.IsLetterOrDigit) < 3)
            return false;
        container = label;
        work = remainder;
        return true;
    }

    // 保存・読込 — Fixed canonical-title patterns are reused instead of competing for the process-wide Regex
    // static cache. The same patterns/options are retained; only their lifetime changes.
    private static readonly Regex CanonicalTitleRegex01 = new(@"^(?<container>[^「『]{2,28}?)[「『](?<work>[^」』]{2,120})[」』](?<tail>.*)$", RegexOptions.Compiled);
    // 作品識別 — A programme genre followed by a separately credited creator and a quoted work is
    // not the work title. Only the complete genre + person + quotation structure proves this boundary.
    // Optional broadcast-brand letters belong to the genre wrapper. The creator remains available
    // in the original EPG title; no work-specific exceptions are used.
    private static readonly Regex CanonicalAttributedDramaPrefixRegex = new(@"^(?<type>連続ドラマ(?:\s*W)?|テレビドラマ|TVドラマ|ドラマ)\s+(?:(?:原作|脚本)\s*[:：]?\s*)?(?<credit>[\p{IsCJKUnifiedIdeographs}]{2,8})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex02 = new(@"^(?<type>アニメ|ドラマ|映画|ドキュメンタリー)\s+(?<work>\S.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex03 = new(@"^(?<work>.+?)\s+(?<slot>(?:MONDAY|TUESDAY|WEDNESDAY|THURSDAY|FRIDAY|SATURDAY|SUNDAY)\s+(?:ANIME|DRAMA|MOVIE)\s+NIGHT)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex04 = new(@"^(?<type>[^「『]{1,12})[「『](?<work>[^」』]{2,120})[」』](?<tail>.*)$", RegexOptions.Compiled);
    private static readonly Regex CanonicalTitleRegex05 = new(@"(?:[!！?？]|＆|&|\bVS\b|参戦|大暴れ|密着|特集|クイズ|ランキング|スペシャル|\bSP\b|初公開|徹底|衝撃|爆笑|今夜|今週|ゲスト|ロケ|挑戦|対決)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex06 = new(@"(?:第\s*[0-9０-９]+|[#＃♯]\s*[0-9０-９]+)$", RegexOptions.Compiled);
    private static readonly Regex CanonicalTitleRegex07 = new(@"(?<attr>【\s*(?:TV|テレビ|地上波|BS|CS)?\s*(?:初放送|初登場|初公開)\s*】)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex08 = new(@"(?:旅|紀行|路線バス|鉄道|街道|散歩|ドライブ|ツアー)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex09 = new(@"^(?<location>[^ ~～〜→]{2,24}(?:~|～|〜|→)[^ ~～〜→]{2,24})\s+(?<work>.+)$", RegexOptions.Compiled);
    private static readonly Regex CanonicalTitleRegex10 = new(@"\s*(?<attr>(?:生中継|生放送|中継)(?:\s*[（(]仮[）)])?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex11 = new(@"(?<prefix>.+?)(?<stage>準々決勝|準決勝|決勝|予選|決定戦|3位決定戦|３位決定戦)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex12 = new(@"(?:選手権|大会|カップ|リーグ|オープン|グランプリ|杯|シリーズ)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex13 = new(@"^(?<work>.+?[\p{L}\p{N}])\s+第\s*(?<number>[0-9０-９]{1,3})\s*戦(?<tail>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex14 = new(@"(?:^|[\s:：・/／（(【\[「『])(?<stage>フリー走行\s*[0-9０-９]+|FP\s*[0-9０-９]+|予選|決勝|スプリント(?:予選|シュートアウト)?|準々決勝|準決勝)(?=$|[\s:：・/／）)】\]」』])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex15 = new(@"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃♯]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex16 = new(@"(?:【[^【】]{2,}】|\[[^\[\]]{2,}\])\s*$", RegexOptions.Compiled);
    private static readonly Regex CanonicalTitleRegex17 = new(@"^(?<work>.+?)\s+第[一二三四五六七八九十百千零〇]+(?:話|章)(?:\s*[:：].*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex18 = new(@"\s+(?<part>第\s*[0-9０-９]+\s*部|前編|後編)(?<tail>(?:\s+.+)?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalTitleRegex19 = new(@"(?:新番組|初回|最終回|再放送|アンコール|リピート|一挙放送)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // 作品識別 — Day and part identify a tournament broadcast, not the underlying tournament Work. Require
    // an explicit separated suffix AND an independently named tournament; do not split generic programme parts.
    private static readonly Regex TournamentWorkMarker = new(@"(?:選手権|トーナメント|クラシック|グランプリ|オープン|カップ|大会|杯|ツアー)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TournamentBroadcastPartSuffix = new(@"^(?<work>.+?)\s+(?<part>(?:Part|パート)\s*[0-9０-９]{1,2}(?:\s*[+＋/&＆／〜～~-]\s*(?:(?:Part|パート)\s*)?[0-9０-９]{1,2})?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TournamentBroadcastDaySuffix = new(@"^(?<work>.+?)\s+(?<day>第\s*[0-9０-９]{1,2}\s*日(?:目)?|[0-9０-９]{1,2}\s*日目|初日|最終日)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Bracketed EPG schedule variants are ordinary day metadata, including a broadcaster's
    // replacement-square marker. Require a complete bracket pair and a true tournament Work.
    private static readonly Regex TournamentBracketedDaySuffix = new(@"^(?<work>.+?)\s*[<＜]\s*(?<day>第\s*[0-9０-９]{1,2}\s*日(?:目)?|[0-9０-９]{1,2}\s*日目|初日|最終日)\s*[□]?[>＞]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool HasTournamentWorkBoundary(string title)
        => title.Count(char.IsLetterOrDigit) >= 6
            && title.Any(char.IsLetter)
            && TournamentWorkMarker.IsMatch(title);

    // 作品識別 — The canonical parser is the only authority for recognized tournament schedule attributes.
    // It is safe to keep the parsed Work against a legacy alias or learner promotion that still embeds its day.
    private static bool HasParsedTournamentBroadcastSchedule(CanonicalTitleParts parts)
        => HasTournamentWorkBoundary(parts.Work)
            && (parts.Event.EndsWith("日", StringComparison.Ordinal)
                || parts.Event.EndsWith("日目", StringComparison.Ordinal)
                || parts.Part.StartsWith("Part", StringComparison.OrdinalIgnoreCase)
                || parts.Part.StartsWith("パート", StringComparison.Ordinal));


    // 作品識別 — A structurally settled on-air Work must not be shortened again by an older
    // learned alias or a long-term learner promotion. This applies only when the new parser has
    // actually separated a full bracketed episode, programme headline or broadcast container,
    // or deliberately retained a complete quoted work in an otherwise ambiguous announcement.
    private static bool HasSettledAiringWorkBoundary(string source, CanonicalTitleParts parts)
    {
        if (parts.Work.Length < 3) return false;
        if (parts.Work.IndexOf('「') >= 0 || parts.Work.IndexOf('『') >= 0)
            return true;
        if (parts.Container.Length > 0
            && source.AsSpan().TrimStart().StartsWith("<".AsSpan(), StringComparison.Ordinal))
            return true;
        if (parts.Subtitle.Length == 0) return false;
        var normalized = NormalizeCanonicalWhitespace(StripNonIdentityBroadcastAnnotations(
            source.Normalize(NormalizationForm.FormKC)));
        if (TrySplitProgrammeEpisodeBrackets(normalized, out var bracketedWork, out _)
            && string.Equals(CompactIdentity(bracketedWork), CompactIdentity(parts.Work), StringComparison.OrdinalIgnoreCase))
            return true;
        if (TrySplitAiredTitleAndDetail(normalized, out var airedTitle, out _)
            && string.Equals(CompactIdentity(ParseCanonicalTitleParts(airedTitle).Work),
                CompactIdentity(parts.Work), StringComparison.OrdinalIgnoreCase))
            return true;
        return TrySplitPunctuatedProgrammeTopic(normalized, out var punctuatedWork, out _)
            && string.Equals(CompactIdentity(punctuatedWork), CompactIdentity(parts.Work), StringComparison.OrdinalIgnoreCase);
    }

    // 文字列解析 — A subtitle may include reading parentheses (e.g. 輿(こし)入れ). Check nesting without allocating
    // regex captures for each nested pair.
    private static bool HasBalancedParentheses(string value)
    {
        var depth = 0;
        foreach (var ch in value)
        {
            if (ch is '(' or '（') depth++;
            else if (ch is ')' or '）')
            {
                if (--depth < 0) return false;
            }
        }
        return depth == 0;
    }

    // 作品識別 — A single structural Work/supplement boundary for recorded titles,
    // aliases, scoring, normalization learning and external lookup. An announcement
    // for another quoted Work and an explicitly marked broadcast presentation are
    // not part of this Work. Plain decoration and quoted official titles stay intact.
    private static readonly Regex MarkedAiredSupplement = new(
        @"[★☆]\s*(?:(?:最新作|次回作|新作|公開記念|放送決定)\s*[「『]|(?:出演|主演|監督|原作)\s*[:：])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BroadcastPresentationSuffix = new(
        @"(?:[（(【\[]\s*(?:完全ノーカット(?:版|放送)?|ノーカット版|(?:日本語)?(?:字幕|吹替)(?:版)?)\s*[）)】\]]|(?<=\s)(?:完全ノーカット(?:版|放送)?|ノーカット版|(?:日本語)?(?:字幕|吹替)版))(?=$|\s|\[|【)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // An explicitly delimited credit role (including multiple roles joined by a middle dot)
    // or a named list is supplementary metadata. A bare diamond is insufficient to split a Work.
    private static readonly Regex BroadcastCreditSuffix = new(
        @"(?<=\s)◆\s*(?:(?:[\p{L}]{2,8}(?:[・／/][\p{L}]{2,8})?)\s*[:：]\s*[^\s◆]{2,}|[一-龯々]{2,6}(?:\s*[、,／/]\s*[一-龯々]{2,6})+)(?=$|\s|\[)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A structurally marked broadcast feature or descriptive cast sentence is
    // editorial metadata. Neither a title-specific dictionary nor stripping every
    // bracket/spaced phrase can establish such a boundary.
    private static readonly Regex BroadcastEditorialSuffix = new(
        @"(?:\s*【\s*(?:特集|出演|主演|監督|原作)\s*[:：][^【】]{3,100}】|\s+(?:特集|出演|主演|監督|原作)\s*[:：]\s*[^\r\n]{3,100})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BroadcastCastDescriptionSuffix = new(
        @"(?<=\s)[一-龯々]{2,8}\s*[×＆&]\s*[一-龯々]{2,8}(?:\s+[^\s]{2,}){1,8}[!！。](?=\s*$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool MayHaveAiredSupplementaryDetail(string value)
        => value.AsSpan().IndexOfAny('▼', '★', '☆') >= 0
            || value.IndexOf('◆') >= 0
            || value.Contains("ノーカット", StringComparison.Ordinal)
            || value.Contains("字幕版", StringComparison.Ordinal)
            || value.Contains("吹替版", StringComparison.Ordinal)
            || value.Contains("日本語吹替", StringComparison.Ordinal)
            || value.Contains("日本語字幕", StringComparison.Ordinal)
            || (value.IndexOfAny([':', '：']) >= 0
                && (value.Contains("特集", StringComparison.Ordinal)
                    || value.Contains("出演", StringComparison.Ordinal)
                    || value.Contains("主演", StringComparison.Ordinal)
                    || value.Contains("監督", StringComparison.Ordinal)
                    || value.Contains("原作", StringComparison.Ordinal)))
            || (value.IndexOfAny(['×', '＆', '&']) >= 0
                && value.IndexOfAny(['!', '！', '。']) >= 0
                && value.IndexOf(' ') >= 0);

    // A suffix delimiter within the original quoted Work is not a broadcast
    // boundary. This common guard applies to every kind of supplementary suffix.
    private static bool IsOutsideQuotedWork(string title, int separatorIndex)
    {
        var quoteDepth = 0;
        for (var i = 0; i < separatorIndex; i++)
        {
            if (title[i] is '「' or '『')
                quoteDepth++;
            else if (title[i] is '」' or '』')
                quoteDepth = Math.Max(0, quoteDepth - 1);
        }
        return quoteDepth == 0;
    }

    private static string AiredTitleEvidenceText(string? title)
    {
        var raw = title ?? string.Empty;
        if (!MayHaveAiredSupplementaryDetail(raw))
            return raw;
        var normalized = StripNonIdentityBroadcastAnnotations(raw.Normalize(NormalizationForm.FormKC));
        return TrySplitAiredTitleAndDetail(normalized, out var airedTitle, out _)
            ? airedTitle : raw;
    }

    private static bool TrySplitAiredTitleAndDetail(
        string normalized, out string airedTitle, out string detail)
    {
        airedTitle = normalized;
        detail = string.Empty;
        if (!MayHaveAiredSupplementaryDetail(normalized))
            return false;

        // Earliest *proved* outside-quote boundary wins; no per-title fallback or
        // later display overwrite. The same original Work is used by every consumer.
        var divider = -1;
        var dividerWidth = 0;
        for (var i = normalized.IndexOf('▼'); i >= 0; i = normalized.IndexOf('▼', i + 1))
        {
            if (!IsOutsideQuotedWork(normalized, i))
                continue;
            divider = i;
            dividerWidth = 1;
            break;
        }

        if (normalized.AsSpan().IndexOfAny('★', '☆') >= 0)
        {
            for (var marked = MarkedAiredSupplement.Match(normalized);
                 marked.Success; marked = marked.NextMatch())
            {
                if (!IsOutsideQuotedWork(normalized, marked.Index))
                    continue;
                if (divider < 0 || marked.Index < divider)
                {
                    divider = marked.Index;
                    dividerWidth = 1;
                }
                break;
            }
        }

        if (normalized.Contains("ノーカット", StringComparison.Ordinal)
            || normalized.Contains("字幕版", StringComparison.Ordinal)
            || normalized.Contains("吹替版", StringComparison.Ordinal)
            || normalized.Contains("日本語吹替", StringComparison.Ordinal)
            || normalized.Contains("日本語字幕", StringComparison.Ordinal))
        {
            for (var presentation = BroadcastPresentationSuffix.Match(normalized);
                 presentation.Success; presentation = presentation.NextMatch())
            {
                if (!IsOutsideQuotedWork(normalized, presentation.Index))
                    continue;
                if (divider < 0 || presentation.Index < divider)
                {
                    divider = presentation.Index;
                    dividerWidth = 0;
                }
                break;
            }
        }

        if (normalized.IndexOf('◆') >= 0)
        {
            for (var credit = BroadcastCreditSuffix.Match(normalized);
                 credit.Success; credit = credit.NextMatch())
            {
                if (!IsOutsideQuotedWork(normalized, credit.Index))
                    continue;
                if (divider < 0 || credit.Index < divider)
                {
                    divider = credit.Index;
                    dividerWidth = 0;
                }
                break;
            }
        }

        if (normalized.Contains("特集", StringComparison.Ordinal)
            || normalized.Contains("出演", StringComparison.Ordinal)
            || normalized.Contains("主演", StringComparison.Ordinal)
            || normalized.Contains("監督", StringComparison.Ordinal)
            || normalized.Contains("原作", StringComparison.Ordinal))
        {
            var editorial = BroadcastEditorialSuffix.Match(normalized);
            if (editorial.Success && IsOutsideQuotedWork(normalized, editorial.Index)
                && (divider < 0 || editorial.Index < divider))
            {
                divider = editorial.Index;
                dividerWidth = 0;
            }
        }
        if (normalized.IndexOfAny(['×', '＆', '&']) >= 0)
        {
            var castSentence = BroadcastCastDescriptionSuffix.Match(normalized);
            if (castSentence.Success && IsOutsideQuotedWork(normalized, castSentence.Index)
                && (divider < 0 || castSentence.Index < divider))
            {
                divider = castSentence.Index;
                dividerWidth = 0;
            }
        }

        if (divider < 2 || divider >= normalized.Length - 2)
            return false;
        var prefix = normalized[..divider].Trim();
        var tail = normalized[(divider + dividerWidth)..].TrimStart();
        if (prefix.Count(char.IsLetterOrDigit) < 2 || tail.Count(char.IsLetterOrDigit) < 2)
            return false;
        airedTitle = prefix;
        detail = tail;
        return true;
    }

    // A standalone station-labelled on-air schedule guide is an announcement, not
    // an episode of the films it advertises. Require two independent structural
    // signals: an explicit BS/CS station token and a final guide-role marker.
    // A normal programme such as "料理ナビ" or a quoted film title is not enough.
    private static bool IsStandaloneBroadcastGuideTitle(string normalized)
    {
        if (normalized.IndexOfAny(['「', '」', '『', '』']) >= 0)
            return false;
        return Regex.IsMatch(normalized,
            @"^(?:BS|CS)\s*[0-9]{1,3}\s*[\p{L}\p{N}・ー\s]{0,40}(?:NAVI|ナビ|番組案内|番組ガイド)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static CanonicalTitleParts ParseCanonicalTitleParts(string? value, bool alreadyNormalized = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

        // 作品識別 — Structural probes provide normalized text; other callers normalize here.
        var normalized = alreadyNormalized ? value : value.Normalize(NormalizationForm.FormKC);
        normalized = StripNonIdentityBroadcastAnnotations(normalized);
        if (!alreadyNormalized || !ReferenceEquals(normalized, value))
            normalized = NormalizeCanonicalWhitespace(normalized);
        // 録画実績・復旧 — The quoted Work remains attributable to the advertised series, but the broadcast itself has
        // a derivative identity (Work + derived:pr). This prevents both scoring from a generic "1分PR"
        // pseudo-series and accidental credit to the actual episode. Retain the raw title for display/history.
        // A quoted title in the separately marked announcement must not turn
        // this aired film into a PR for another work, even if that announcement
        // itself ends in PR.  Preserve the original title for later subtitle parsing.
        if (TryGetQuotedPromotionWork(AiredTitleEvidenceText(normalized), out var advertisedWork))
            return new(string.Empty, string.Empty, string.Empty, advertisedWork,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, "derived:pr", string.Empty);
        if (IsStandaloneBroadcastGuideTitle(normalized))
            return new(string.Empty, string.Empty, string.Empty, normalized,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, "derived:pr", string.Empty);
        var structureSignals = DetectCanonicalTitleStructureSignals(normalized);

        // 文字列解析 — The structural parser accepts the same whitespace grammar.
        var hostAttribute = string.Empty;
        if (TryStripTrailingHostWeekday(normalized, out var withoutHostWeekday, out var detectedHostAttribute))
        {
            hostAttribute = detectedHostAttribute;
            normalized = withoutHostWeekday;
        }

        var container = string.Empty;
        var contentType = string.Empty;
        var series = string.Empty;
        var seasonOrEdition = string.Empty;
        var episode = string.Empty;
        var part = string.Empty;
        var subtitle = string.Empty;
        var eventName = string.Empty;
        var stage = string.Empty;
        var location = string.Empty;
        var broadcastAttribute = string.Empty;
        var workRoot = normalized;

        // 作品識別 — Programme brackets describe individual segments, even when ▼ appears inside
        // the bracket; split this complete structure before the general on-air ▼ announcement.
        if (TrySplitProgrammeEpisodeBrackets(workRoot, out var bracketedWork, out var bracketedDetail))
        {
            workRoot = bracketedWork;
            subtitle = bracketedDetail;
        }

        // 作品識別 — Interpret the aired title before parsing quotations. Any later announcement is supplementary
        // content, never a replacement Work.
        if (TrySplitAiredTitleAndDetail(workRoot, out var airedTitle, out var detail))
        {
            workRoot = airedTitle;
            subtitle = subtitle.Length == 0 ? detail : $"{subtitle} {detail}";
        }

        // 作品識別 — The final complete 【...】 block is a separately delimited episode topic only when the
        // preceding on-air programme name is independently present. Separate it before any promotional
        // whitespace heuristic: the latter must never promote a subtitle fragment to the Work itself.
        // Broadcast-status brackets with an explicit meaning remain on the attribute route below.
        if (workRoot.Length > 0 && (workRoot[^1] == '】' || workRoot[^1] == ']')
            && TrySplitTrailingBracketSubtitle(workRoot, requirePrefix: true,
                out var bracketPrefix, out var bracketTopic)
            && bracketPrefix.Count(char.IsLetterOrDigit) >= 2
            && !CanonicalTitleRegex07.IsMatch(bracketTopic))
        {
            workRoot = bracketPrefix;
            subtitle = subtitle.Length == 0 ? bracketTopic : $"{subtitle} {bracketTopic}";
        }

        // 作品識別 — An explicit programme-ending punctuation and whitespace separates the episode's
        // editorial headline, independently of whether another airing corroborates the series title.
        if (TrySplitPunctuatedProgrammeTopic(workRoot, out var punctuatedWork, out var punctuatedTopic))
        {
            workRoot = punctuatedWork;
            subtitle = subtitle.Length == 0 ? punctuatedTopic : $"{subtitle} {punctuatedTopic}";
        }

        // 作品識別 — An explicit angular container precedes the programme Work.
        if (TrySplitAngleBracketBroadcastContainer(workRoot, out var angularContainer, out var angularWork))
        {
            container = angularContainer;
            workRoot = angularWork;
        }

        // 作品識別 — 1) 「枠名『作品名』」は最も強いローカル構造。引用内を Work とし、前置きを Container とする。 例: 日曜劇場「VIVANT」第18話 ...
        var quotedWork = workRoot.IndexOf('「') >= 0 || workRoot.IndexOf('『') >= 0
            ? CanonicalTitleRegex01.Match(workRoot)
            : Match.Empty;
        var hasQuotedEpisodeSubtitle = false;
        var originalQuotedTitle = workRoot;
        if (quotedWork.Success)
        {
            var quotedPrefix = quotedWork.Groups["container"].Value.Trim(' ', '・', '-', '－', ':', '：');
            var quotedBody = quotedWork.Groups["work"].Value.Trim();
            var tail = quotedWork.Groups["tail"].Value.Trim();
            // 作品識別 — A fully bracketed lead-in is explicit broadcast-container syntax, even when the label has no
            // conventional container suffix (e.g. a time slot). Only accept a complete bracket pair and a nonempty
            // quoted Work.  Never infer a container from an unbracketed marketing or sponsor prefix.
            var explicitBracketContainer = quotedPrefix.Length >= 4
                && quotedPrefix[0] == '【' && quotedPrefix[^1] == '】'
                && quotedPrefix.AsSpan(1, quotedPrefix.Length - 2).IndexOfAny('【', '】') < 0;
            var attributedDrama = quotedPrefix.IndexOf(' ') >= 0
                ? CanonicalAttributedDramaPrefixRegex.Match(quotedPrefix)
                : Match.Empty;
            if (attributedDrama.Success && quotedBody.Count(char.IsLetterOrDigit) >= 2)
            {
                // 作品識別 — Genre and credited creator are separate from the quoted Work at the same
                // parser authority used by ranking, recommendation, learning, search, and evidence.
                contentType = attributedDrama.Groups["type"].Value;
                workRoot = quotedBody;
                if (tail.Length > 0)
                    workRoot = $"{workRoot} {tail}";
            }
            else if (explicitBracketContainer || LooksLikeBroadcastContainerLabel(quotedPrefix))
            {
                if (explicitBracketContainer)
                {
                    container = quotedPrefix[1..^1].Trim();
                    workRoot = quotedBody;
                    if (tail.Length > 0)
                        workRoot = $"{workRoot} {tail}";
                }
                else
                {
                    // 作品識別 — 「映画『作品』」のように型ラベルだけが前置きされる場合は Container ではなく ContentType。
                if (string.Equals(quotedPrefix, "映画", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(quotedPrefix, "ドラマ", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(quotedPrefix, "アニメ", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(quotedPrefix, "ドキュメンタリー", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = quotedPrefix;
                }
                else
                {
                    // 作品識別 — 「土曜プレミアム・映画『作品』」のように、枠名の後ろへ型ラベルが 連結される場合は Container と ContentType を分ける。
                    var separator = quotedPrefix.LastIndexOf('・');
                if (separator >= 2 && separator < quotedPrefix.Length - 1)
                {
                    var possibleContainer = quotedPrefix[..separator].Trim();
                    var possibleType = quotedPrefix[(separator + 1)..].Trim();
                    if (LooksLikeBroadcastContainerLabel(possibleContainer)
                        && (string.Equals(possibleType, "映画", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(possibleType, "ドラマ", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(possibleType, "アニメ", StringComparison.OrdinalIgnoreCase)))
                    {
                        container = possibleContainer;
                        contentType = possibleType;
                    }
                    else
                    {
                        container = quotedPrefix;
                    }
                }
                    else
                    {
                        container = quotedPrefix;
                    }
                }
                workRoot = quotedBody;
                if (tail.Length > 0)
                    workRoot = $"{workRoot} {tail}";
                }
            }
            else
            {
                // 作品識別 — An unrecognized prefix is not evidence that the quoted portion is only an episode subtitle.  In
                // particular, a quoted name followed by an edition/broadcast announcement cannot safely be collapsed to
                // the prefix. Retain the complete identity pending corroboration instead of inventing an alias or losing
                // the quoted Work. Ordinary episode subtitles retain the established prefix handling.
                var editionOnlyTail = Regex.IsMatch(tail,
                    @"^(?:スペシャル|特別編|完全版|ディレクターズカット)(?:\s|$)",
                    RegexOptions.IgnoreCase);
                if (editionOnlyTail)
                {
                    workRoot = originalQuotedTitle;
                }
                else if (HasWorkBeforeExplicitEpisodeMarker(quotedPrefix)
                    || Regex.IsMatch(quotedPrefix, @"\s+第\s*[0-9]{1,3}\s*夜$")
                    || (tail.Length == 0 && quotedPrefix.Count(char.IsLetterOrDigit) >= 3
                        && quotedPrefix.IndexOf('、') < 0))
                {
                    // The airing has a verified episode marker BEFORE the quoted subtitle.
                    workRoot = quotedPrefix;
                    hasQuotedEpisodeSubtitle = true;
                    subtitle = subtitle.Length == 0 ? quotedBody : $"{quotedBody} {subtitle}";
                    if (tail.Length > 0)
                        subtitle = $"{subtitle} {tail}";
                }
                else
                {
                    // A one-off announcement with a quoted subject is not proved to be a series.
                    // Keep the quoted subject and original tail as part of Work until evidence exists.
                    workRoot = originalQuotedTitle;
                }
            }
        }
        else
        {
            // 作品識別 — 2) 「放送枠・作品名」または「放送枠 作品名」。 前置き側が一般的な放送枠語尾を持つ時だけ分離し、曖昧な作品名は触らない。
            var middleDot = workRoot.IndexOf('・');
            if (middleDot >= 2 && middleDot <= 28 && middleDot < workRoot.Length - 2)
            {
                var prefix = workRoot[..middleDot].Trim();
                var remainder = workRoot[(middleDot + 1)..].Trim();
                if (LooksLikeBroadcastContainerLabel(prefix) && remainder.Count(char.IsLetterOrDigit) >= 2)
                {
                    container = prefix;
                    workRoot = remainder;
                }
            }

            if (container.Length == 0)
            {
                var spaceIndex = workRoot.IndexOf(' ');
                if (spaceIndex >= 2 && spaceIndex <= 28 && spaceIndex < workRoot.Length - 2)
                {
                    var prefix = workRoot[..spaceIndex].Trim();
                    var remainder = workRoot[(spaceIndex + 1)..].Trim();
                    if (LooksLikeBroadcastContainerLabel(prefix) && remainder.Count(char.IsLetterOrDigit) >= 2)
                    {
                        container = prefix;
                        workRoot = remainder;
                    }
                }
            }
        }

        // 作品識別 — 明示された番組種別を作品名から分ける。「アニメ 作品名」等は 引用符がない場合も ContentType として扱う。作品名そのものが「アニメ」で
        // 始まる連続語（「アニメーション...」等）には適用しない。
        if (contentType.Length == 0 && container.Length == 0)
        {
            // 文字列解析 — Only the four supported leading content-type labels enter this parser.
            var hasTypedPrefix = workRoot.StartsWith("アニメ", StringComparison.OrdinalIgnoreCase)
                || workRoot.StartsWith("ドラマ", StringComparison.OrdinalIgnoreCase)
                || workRoot.StartsWith("映画", StringComparison.OrdinalIgnoreCase)
                || workRoot.StartsWith("ドキュメンタリー", StringComparison.OrdinalIgnoreCase);
            var typedPrefix = hasTypedPrefix
                ? CanonicalTitleRegex02.Match(workRoot)
                : Match.Empty;
            if (typedPrefix.Success && typedPrefix.Groups["work"].Value.Count(char.IsLetterOrDigit) >= 2)
            {
                contentType = typedPrefix.Groups["type"].Value;
                workRoot = typedPrefix.Groups["work"].Value.Trim();
            }
        }

        // 作品識別 — A typed programme whose quoted subtitle follows an immediately preceding parenthesized number
        // has an explicit episode structure: "アニメ Work(6)『話題』". Bare "Work(6)" remains ambiguous and still
        // requires same-service evidence; year/edition numbers and untyped quoted Works are never inferred as
        // episodes.
        if (contentType.Length > 0 && subtitle.Count(char.IsLetterOrDigit) >= 2
            && workRoot.IndexOf('(') >= 0 && workRoot.EndsWith(')'))
        {
            var quotedNumber = Regex.Match(workRoot,
                @"^(?<work>.+?)\s*\(\s*(?<number>[0-9]{1,3})\s*\)$");
            if (quotedNumber.Success
                && int.TryParse(quotedNumber.Groups["number"].Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out var numberedEpisode)
                && numberedEpisode > 0 && !IsLikelyCalendarYear(numberedEpisode)
                && quotedNumber.Groups["work"].Value.Trim().Count(char.IsLetterOrDigit) >= 3)
            {
                episode = $"({numberedEpisode})";
                workRoot = quotedNumber.Groups["work"].Value.Trim();
            }
        }

        // 作品識別 — 明示的な曜日 + 編成ジャンル + NIGHT の末尾は放送枠ラベル。 独立した空白境界を必須とし、作品固有の副題や英語の固有名は残す。
        if (container.Length == 0)
        {
            // 文字列解析 — The suffix is mandatory in the established anchored expression. Avoid materializing failed
            // Regex matches for unrelated programmes.
            var trailingSlot = workRoot.EndsWith("NIGHT", StringComparison.OrdinalIgnoreCase)
                ? CanonicalTitleRegex03.Match(workRoot)
                : Match.Empty;
            if (trailingSlot.Success && trailingSlot.Groups["work"].Value.Count(char.IsLetterOrDigit) >= 3)
            {
                container = trailingSlot.Groups["slot"].Value;
                workRoot = trailingSlot.Groups["work"].Value.Trim();
            }
        }

        // 作品識別 — Container 分離後に「映画『作品』」等の型ラベルが残る場合は、型ラベルを Work へ混ぜない。
        if (container.Length > 0 && (workRoot.IndexOf('「') >= 0 || workRoot.IndexOf('『') >= 0))
        {
            var typedQuotedWork = CanonicalTitleRegex04.Match(workRoot);
            if (typedQuotedWork.Success && LooksLikeBroadcastContainerLabel(typedQuotedWork.Groups["type"].Value))
            {
                contentType = typedQuotedWork.Groups["type"].Value.Trim();
                workRoot = typedQuotedWork.Groups["work"].Value.Trim();
                var typedTail = typedQuotedWork.Groups["tail"].Value.Trim();
                if (typedTail.Length > 0)
                    subtitle = subtitle.Length == 0 ? typedTail : $"{subtitle} {typedTail}";
            }
        }

        // 作品識別 — 3) 先頭【...】は既存の安全判定を継承する。明示Episodeを後ろに持つ場合だけ Container として分離する。数値括弧系列は LocalWorkKey 側の
        // corroboration を使う。
        if (container.Length == 0 && workRoot.IndexOf('【') >= 0)
        {
            var leading = ParseLeadingContainerParts(workRoot);
            if (!string.IsNullOrWhiteSpace(leading.Container)
                && (HasWorkBeforeExplicitEpisodeMarker(leading.Remainder)
                    || IsExplicitLeadingBroadcastSlot(leading.Container, leading.Remainder)))
            {
                container = leading.Container;
                workRoot = leading.Remainder;
            }
        }

        // 作品識別 — 4) The strong content divider was applied before quotation parsing, so its secondary quotations
        // cannot become this broadcast's Work.

        // 作品識別 — 5) 「番組名 今回内容」のように空白だけで連結される場合は、後半に明確な 告知/企画/出演者プロモーションの手掛かりがある時だけ Subtitle へ分離する。
        // 個別番組名は列挙せず、曖昧な英題・正式副題・作品名は触らない。
        if (subtitle.Length == 0)
        {
        // 処理条件 — The leading field must be 3–32 characters followed by an ASCII space.
            var plainTailSpace = workRoot.IndexOf(' ');
            if (plainTailSpace >= 3
                && plainTailSpace <= 32
                && plainTailSpace + 1 < workRoot.Length
                && workRoot.Length - plainTailSpace - 1 >= 8)
            {
                var candidateWork = workRoot[..plainTailSpace];
                var candidateTail = workRoot[(plainTailSpace + 1)..];
                // A promotional cue somewhere INSIDE the remainder does not prove that all earlier
                // words are editorial copy. Require a cue at the start of an independently separated
                // topic, not a terminal ! or an embedded "特集" in a programme's proper name.
                var promoLikeTail = CanonicalTitleRegex05.Match(candidateTail) is { Success: true, Index: 0 };
                // 作品識別 — A terminal bracketed announcement is not a standalone promotional cue.
                // Complete episode-topic brackets are already separated at the common title boundary.
                if (promoLikeTail
                    && TrySplitTrailingBracketSubtitle(candidateTail, requirePrefix: true,
                        out var unbracketedTail, out _))
                    promoLikeTail = CanonicalTitleRegex05.Match(unbracketedTail) is { Success: true, Index: 0 };
                // 作品識別 — A competition/series title in the tail is not mere promotional copy. Treat an explicit
                // season/year + named event/series as identity-bearing, even when the same text contains promo terms.
                // This is a generic structural veto and never learns or hardcodes a sponsor or work name.
                var hasNamedEditionInTail = Regex.IsMatch(candidateTail,
                    @"(?:20[0-9]{2}|(?:第\s*[0-9]+\s*(?:回|期|部|シリーズ))|(?:season|series)\s*[0-9]+)",
                    RegexOptions.IgnoreCase)
                    && candidateTail.Count(char.IsLetterOrDigit) >= 10;
                if (promoLikeTail
                    && !hasNamedEditionInTail
                    && candidateWork.Count(char.IsLetterOrDigit) >= 3
                    && candidateWork.Any(char.IsLetter)
                    && !CanonicalTitleRegex06.IsMatch(candidateWork))
                {
                    workRoot = candidateWork;
                    subtitle = candidateTail;
                }
            }
        }

        // 作品識別 — Separately identify broadcast derivatives in the canonical structure.  The relation stays
        // separate from Work and the identity key retains a distinct derivative suffix; promotion episodes are
        // never credited as the main work. Only an exact terminal marker after an existing work is accepted.
        var derivedTail = Regex.Match(workRoot,
            @"^(?<work>.+?)[\s　]+(?<kind>PR|ＰＲ|予告|みどころ|総集編)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (derivedTail.Success
            && derivedTail.Groups["work"].Value.Trim().Count(char.IsLetterOrDigit) >= 3)
        {
            var derivedWork = derivedTail.Groups["work"].Value.TrimEnd();
            var derivedKind = derivedTail.Groups["kind"].Value.Normalize(NormalizationForm.FormKC);
            var derivedCode = derivedKind.Equals("PR", StringComparison.OrdinalIgnoreCase)
                ? "pr"
                : derivedKind == "予告" ? "予告"
                : derivedKind == "みどころ" ? "みどころ" : "総集編";
            broadcastAttribute = "derived:" + derivedCode;
            workRoot = derivedWork;
        }

        // 作品識別 — 6) 明示的な放送属性を囲む角括弧は Work から外して BroadcastAttribute へ。 ARIB短タグとは別に、局側が人間向けに書く「TV初放送」等を扱う。
        var explicitBroadcastBracket = structureSignals.MayHaveExplicitBroadcastBracket
            ? CanonicalTitleRegex07.Match(workRoot)
            : Match.Empty;
        if (explicitBroadcastBracket.Success)
        {
            var attr = explicitBroadcastBracket.Groups["attr"].Value.Trim();
            broadcastAttribute = broadcastAttribute.Length == 0 ? attr : $"{broadcastAttribute}/{attr}";
            workRoot = (workRoot[..explicitBroadcastBracket.Index] + " "
                + workRoot[(explicitBroadcastBracket.Index + explicitBroadcastBracket.Length)..]).Trim();
        }

        // 処理条件 — 8) 「場所A～場所B 番組名」のような先頭ルート表現は、旅行・紀行・移動系の番組構造が 後続本文から明確な場合だけ Location へ分離する。地名辞書や個別タイトル登録は使わない。
        if (workRoot.IndexOf(' ') > 0
            && (workRoot.IndexOf('~') > 0 || workRoot.IndexOf('～') > 0 || workRoot.IndexOf('〜') > 0 || workRoot.IndexOf('→') > 0)
            && CanonicalTitleRegex08.IsMatch(workRoot))
        {
            // 文字列解析 — FormKC は全角チルダ U+FF5E を ASCII '~' へ正規化するため、構造判定側でも 正規化後の '~' を同じルート区切りとして扱う。raw title
            // の切り出しには使わない。
            var route = CanonicalTitleRegex09.Match(workRoot);
            if (route.Success && route.Groups["work"].Value.Count(char.IsLetterOrDigit) >= 4)
            {
                location = route.Groups["location"].Value.Trim();
                workRoot = route.Groups["work"].Value.Trim();
            }
        }

        // 作品識別 — 9) 生中継/中継/生放送などの明示的な放送形態は Work から外して BroadcastAttribute へ。 「（仮）」は放送側の暫定表記として属性側へ含める。
        var liveBroadcast = structureSignals.MayHaveLiveBroadcast
            ? CanonicalTitleRegex10.Match(workRoot)
            : Match.Empty;
        if (liveBroadcast.Success)
        {
            broadcastAttribute = liveBroadcast.Groups["attr"].Value.Trim();
            workRoot = workRoot[..liveBroadcast.Index].TrimEnd();
        }

        // 作品識別 — 10) 大会・競技系タイトルの末尾に付くラウンド/ステージは Stage へ分離する。 個別競技名や大会名ではなく、一般的なステージ語だけを扱う。F1 の第N戦セッション処理より先に
        // 切り離しても Work 本体を壊さないよう、前側に十分な識別文字が残る場合だけ採用する。
        if (stage.Length == 0 && structureSignals.MayHaveTerminalStage)
        {
            var terminalStage = CanonicalTitleRegex11.Match(workRoot);
            if (terminalStage.Success)
            {
                var prefix = terminalStage.Groups["prefix"].Value.TrimEnd();
                if (prefix.Count(char.IsLetterOrDigit) >= 4
                    && CanonicalTitleRegex12.IsMatch(prefix))
                {
                    stage = terminalStage.Groups["stage"].Value.Trim();
                    workRoot = prefix;
                }
            }
        }

        // 作品識別 — 11) 大会の「第N戦」を Work と Event に分離する。 作品名と「第N戦」の間の空白を必須境界とする一方、「戦」と開催地が
        // 密着する表記も認める（「第16戦バーレーン」など）。後方の空白を境界条件に すると、同一大会のセッションだけが異なる作品へ分裂する。 独立した作品名の前方境界・十分な文字数・非数字条件は維持する。
        // 特定競技や作品名の辞書・例外は使用しない。
        var mayContainRound = workRoot.IndexOf('戦') >= 0
            && workRoot.IndexOf('第') >= 0;
        var round = mayContainRound
            ? CanonicalTitleRegex13.Match(workRoot)
            : Match.Empty;
        if (round.Success)
        {
            var candidateWork = round.Groups["work"].Value.Trim();
            // 作品識別 — Numeric-only programme prefixes (e.g. a broadcast year alone) are not works.
            if (candidateWork.Count(char.IsLetterOrDigit) >= 4
                && candidateWork.Any(char.IsLetter))
            {
                workRoot = candidateWork;
                var tail = round.Groups["tail"].Value.Trim(' ', ':', '：', '・', '/', '／');
                var session = CanonicalTitleRegex14.Match(tail);
                if (session.Success)
                {
                    stage = session.Groups["stage"].Value.Trim();
                    tail = (tail[..session.Index] + " " + tail[(session.Index + session.Length)..]).Trim();
                }
                eventName = "第" + round.Groups["number"].Value + "戦";
                if (tail.Length > 0)
                    eventName += " " + tail;
            }
        }

        // 作品識別 — Competition programme schedules are separate from Work identity. Remove a terminal
        // broadcast part first, then its tournament day, so "第2日 Part1+2" and "最終日 Part2" resolve to
        // the same tournament Work while retaining both details on their own canonical axes. A non-tournament
        // series with "Part2" retains that text as part of Work. Different events and years remain distinct.
        if (MayContainTournamentScheduleSuffix(workRoot))
        {
            var broadcastPart = TournamentBroadcastPartSuffix.Match(workRoot);
            if (broadcastPart.Success && HasTournamentWorkBoundary(broadcastPart.Groups["work"].Value))
            {
                part = broadcastPart.Groups["part"].Value.Trim();
                workRoot = broadcastPart.Groups["work"].Value.TrimEnd();
            }

            var tournamentDay = TournamentBroadcastDaySuffix.Match(workRoot);
            if (!tournamentDay.Success)
                tournamentDay = TournamentBracketedDaySuffix.Match(workRoot);
            if (tournamentDay.Success && HasTournamentWorkBoundary(tournamentDay.Groups["work"].Value))
            {
                eventName = tournamentDay.Groups["day"].Value.Trim();
                workRoot = tournamentDay.Groups["work"].Value.TrimEnd();
            }
        }

        // 文字列解析 — A parenthesized number followed immediately by a substantive subtitle is distinguishable from
        // an isolated ambiguous numeric suffix.  Retain a standalone (4), production year (2023), and embedded
        // series number untouched.
        if (episode.Length == 0 && (workRoot.IndexOf('(') >= 0 || workRoot.IndexOf('（') >= 0))
        {
            var numberedSubtitle = Regex.Match(workRoot,
                @"^(?<work>.+?)[（(](?<number>[0-9０-９]{1,3})[）)]\s*(?<subtitle>.{3,})$",
                RegexOptions.CultureInvariant);
            if (numberedSubtitle.Success
                && numberedSubtitle.Groups["work"].Value.Count(char.IsLetterOrDigit) >= 3
                && numberedSubtitle.Groups["work"].Value.Any(char.IsLetter)
                && numberedSubtitle.Groups["subtitle"].Value.Any(char.IsLetter)
                // 作品識別 — Subtitles can contain their own reading/furigana parentheses. Never discard a clear episode
                // boundary solely for that reason, but reject malformed trailing bracket structures conservatively.
                && HasBalancedParentheses(numberedSubtitle.Groups["subtitle"].Value))
            {
                episode = "(" + numberedSubtitle.Groups["number"].Value + ")";
                var episodeSubtitle = numberedSubtitle.Groups["subtitle"].Value.Trim();
                subtitle = subtitle.Length == 0 ? episodeSubtitle : $"{subtitle} {episodeSubtitle}";
                workRoot = numberedSubtitle.Groups["work"].Value.TrimEnd();
            }
        }

        // 作品識別 — A numbered "night" followed by an explicit episode subtitle is a numbered installment, not part
        // of the Work/season name. In contrast, an isolated "第N夜" without an independently quoted subtitle
        // remains ambiguous (event/edition) and must never be stripped by this rule.
        if (episode.Length == 0 && hasQuotedEpisodeSubtitle
            && subtitle.Count(char.IsLetterOrDigit) >= 2
            && workRoot.IndexOf('第') >= 0 && workRoot.IndexOf('夜') >= 0)
        {
            var numberedNight = Regex.Match(workRoot,
                @"^(?<work>.+?)\s+第\s*(?<number>[0-9]{1,3})\s*夜$",
                RegexOptions.CultureInvariant);
            if (numberedNight.Success
                && int.TryParse(numberedNight.Groups["number"].Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out var numberedNightEpisode)
                && numberedNightEpisode > 0
                && numberedNight.Groups["work"].Value.Count(char.IsLetterOrDigit) >= 3
                && numberedNight.Groups["work"].Value.Any(char.IsLetter))
            {
                episode = $"第{numberedNightEpisode}夜";
                workRoot = numberedNight.Groups["work"].Value.TrimEnd();
            }
        }

        // 作品識別 — 12) 明示的な話数だけを Episode として分離する。
        var mayContainEpisodeMarker = workRoot.IndexOf('#') >= 0
            || workRoot.IndexOf('＃') >= 0 || workRoot.IndexOf('♯') >= 0
            || workRoot.IndexOf('話') >= 0
            || workRoot.IndexOf('回') >= 0
            || workRoot.Contains("ep", StringComparison.OrdinalIgnoreCase);
        var marker = mayContainEpisodeMarker
            ? CanonicalTitleRegex15.Match(workRoot)
            : Match.Empty;
        if (marker.Success)
        {
            episode = marker.Value.Trim();
            var prefix = workRoot[..marker.Index].TrimEnd();
            var suffix = workRoot[(marker.Index + marker.Length)..].Trim();

            var bracketSubtitle = CanonicalTitleRegex16.Match(prefix);
            if (bracketSubtitle.Success)
            {
                subtitle = bracketSubtitle.Value.Trim();
                prefix = prefix[..bracketSubtitle.Index].TrimEnd();
            }
            if (suffix.Length > 0)
                subtitle = subtitle.Length == 0 ? suffix : $"{subtitle} {suffix}";

            // 作品識別 — Never collapse an event/title into a numeric-only prefix such as a calendar year. Shapes like
            // "2026 第57回 <event name>" use 第N回 as an edition marker, not an episode whose Work identity is the
            // leading year. Retain the full title unless the text before the marker contains at least one letter.
            // This is structural and title-agnostic.
            if (prefix.Count(char.IsLetterOrDigit) >= 2
                && prefix.Any(char.IsLetter))
                workRoot = prefix;
        }

        // 作品識別 — 第十八話・第二十章など漢数字の話数・章番号を Episode に分離する。 章だけを根拠にシリーズ名を削らず、先行する作品名と明示の「第」が ある終端構造に限定する。
        if (episode.Length == 0 && workRoot.IndexOf('第') >= 0
            && (workRoot.IndexOf('話') >= 0 || workRoot.IndexOf('章') >= 0))
        {
            var kanjiEpisode = CanonicalTitleRegex17.Match(workRoot);
            if (kanjiEpisode.Success && kanjiEpisode.Groups["work"].Value.Count(char.IsLetterOrDigit) >= 3)
            {
                episode = workRoot[kanjiEpisode.Groups["work"].Length..].Trim();
                workRoot = kanjiEpisode.Groups["work"].Value.Trim();
            }
        }

        // 作品識別 — 13) 第N部 / 前編 / 後編は原則 Part 側。独立作品か不明な場合に Work を壊さないよう、 Work 本体が十分に残る形だけを採用する。
        if (episode.Length == 0
            && (workRoot.IndexOf('部') >= 0 || workRoot.Contains("前編", StringComparison.OrdinalIgnoreCase) || workRoot.Contains("後編", StringComparison.OrdinalIgnoreCase)))
        {
            var partMarker = CanonicalTitleRegex18.Match(workRoot);
            if (partMarker.Success)
            {
                var prefix = workRoot[..partMarker.Index].TrimEnd();
                if (prefix.Count(char.IsLetterOrDigit) >= 4)
                {
                    part = partMarker.Groups["part"].Value.Trim();
                    var tail = partMarker.Groups["tail"].Value.Trim();
                    if (tail.Length > 0)
                        subtitle = tail;
                    workRoot = prefix;
                }
            }
        }

        // 作品識別 — 放送ライフサイクル語は Work Identity ではなく BroadcastAttribute へ退避する。
        if (structureSignals.MayHaveLifecycleAttribute)
        {
            var lifecycleMatches = CanonicalTitleRegex19.Matches(workRoot);
            if (lifecycleMatches.Count > 0)
            {
                var lifecycleAttribute = string.Join("/", lifecycleMatches.Cast<Match>().Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase));
                broadcastAttribute = broadcastAttribute.Length == 0
                    ? lifecycleAttribute
                    : $"{broadcastAttribute}/{lifecycleAttribute}";
                workRoot = CanonicalTitleRegex19.Replace(workRoot, " ");
            }
        }

        workRoot = NormalizeCanonicalWhitespace(workRoot).Trim(' ', '・', '-', '－', ':', '：');
        return new(container, contentType, series, workRoot, seasonOrEdition, episode, part, subtitle,
            eventName, stage, location, broadcastAttribute, hostAttribute);
    }

    private static string CompactIdentity(string value)
    {
        var count = 0;
        var requiresRewrite = false;
        foreach (var ch in value)
        {
            if (!char.IsLetterOrDigit(ch))
            {
                requiresRewrite = true;
                continue;
            }

            count++;
            if (char.ToLowerInvariant(ch) != ch)
                requiresRewrite = true;
        }

        if (count == 0)
            return string.Empty;

        // 作品識別 — If compaction and invariant lower-casing would reproduce the exact input, reuse the canonical
        // parser's existing Work string instead of allocating an identical copy.
        if (!requiresRewrite)
            return value;

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

    // 外部情報 — ExternalLookup is supplemental evidence only. This boundary deliberately does not alter
    // canonical Work Identity or recommendation score. It only identifies title shapes for which local text
    // alone may be insufficient.
    internal static AIrhythmExternalEvidenceNeed EvaluateExternalEvidenceNeed(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new(false, AIrhythmExternalEvidenceNeedReason.None);

        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC)).Trim();
        if (normalized.Length == 0)
            return new(false, AIrhythmExternalEvidenceNeedReason.None);

        // 作品識別 — A confirmed derivative takes precedence over container/numeric ambiguity. The canonical Work
        // parser owns that role even for bracketed slot prefixes.
        if (ClassifyDerivedProgramRelation(normalized) != AIrhythmDerivedProgramRelation.None)
            return new(true, AIrhythmExternalEvidenceNeedReason.DerivedProgramMarker);

        // 外部情報 — A trailing parenthesized number is structurally ambiguous. It may correlate with a recurring
        // Work sequence, but it is not Episode Identity by itself. Retain it as external ambiguity evidence and
        // require the explicit episode contract for episode follow-up.
        if (Regex.IsMatch(normalized, @"[（(]\s*[0-9０-９]+\s*[）)](?:\s*)$"))
            return new(true, AIrhythmExternalEvidenceNeedReason.NumericParenthesizedSuffix);

        // 作品識別 — A leading long corner-bracket block often represents a broadcast container, but bracket text
        // can also belong to a work title. Mark it ambiguous instead of stripping it.
        if (Regex.IsMatch(normalized, @"^【[^【】]{2,}】\s*\S+"))
            return new(true, AIrhythmExternalEvidenceNeedReason.LeadingContainerCandidate);

        // 外部情報 — Explicit episode/ordinal markers are deterministic local structure. They are handled internally
        // as episode or Work context and never, by themselves, justify an external lookup.
        if (Regex.IsMatch(normalized, @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃♯]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)", RegexOptions.IgnoreCase))
            return new(false, AIrhythmExternalEvidenceNeedReason.None);

        return new(false, AIrhythmExternalEvidenceNeedReason.None);
    }

    internal static AIrhythmDerivedProgramRelation ClassifyDerivedProgramRelation(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return AIrhythmDerivedProgramRelation.None;
        // 外部情報 — CanonicalTitleParts is the single source for Work/derivative classification; scoring and
        // external evidence no longer apply independent PR regexes.
        var attribute = ParseCanonicalTitleParts(title).BroadcastAttribute;
        if (attribute.StartsWith("derived:pr", StringComparison.Ordinal))
            return AIrhythmDerivedProgramRelation.Promo;
        if (attribute.StartsWith("derived:予告", StringComparison.Ordinal))
            return AIrhythmDerivedProgramRelation.Promo;
        if (attribute.StartsWith("derived:みどころ", StringComparison.Ordinal))
            return AIrhythmDerivedProgramRelation.Preview;
        if (attribute.StartsWith("derived:総集編", StringComparison.Ordinal))
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

        // External evidence must search the aired Work, not a different title quoted
        // in an appended broadcast announcement.  Share the canonical boundary.
        var root = Clean(StripNonIdentityBroadcastAnnotations(
            AiredTitleEvidenceText(title).Normalize(NormalizationForm.FormKC)).Trim());
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
        var derivativeParts = ParseCanonicalTitleParts(root);
        if (derivativeParts.BroadcastAttribute.StartsWith("derived:", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(derivativeParts.Work))
            Add(derivativeParts.Work);
        if (!string.IsNullOrWhiteSpace(containerParts.WorkCandidate))
            Add(containerParts.WorkCandidate);
        while (queue.Count > 0 && results.Count < 8)
        {
            var value = queue.Dequeue();
            Add(Regex.Replace(value, @"^【[^【】]{2,}】\s*", string.Empty).Trim());
            Add(Regex.Replace(value, @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃♯]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)(?:\s*[/／].*)?$", string.Empty, RegexOptions.IgnoreCase).Trim());
            Add(Regex.Replace(value, @"[（(]\s*[0-9０-９]+\s*[）)]\s*$", string.Empty).Trim());
            Add(Regex.Replace(value, @"(?:\s|　)*(?:PR|ＰＲ|予告|みどころ|総集編)(?:\s|　)*$", string.Empty, RegexOptions.IgnoreCase).Trim());

            var quoted = Regex.Match(value, @"[「『]([^」』]{2,80})[」』]");
            if (quoted.Success)
                Add(quoted.Groups[1].Value);
        }

        // 作品識別 — Prefer the structurally extracted work candidate for a leading container. A quoted item after a
        // colon/descriptor is useful supplemental context, but must not outrank the surrounding programme
        // identity merely because it is shorter. Retain the original title as a broad fallback.
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

        // 作品識別 — A quoted phrase introduced after a colon commonly describes an item/topic within the programme
        // instead of replacing the programme identity. This is structural only: the quoted text is retained as
        // supplemental evidence and no canonical title is rewritten.
        var topicMatch = Regex.Match(remainder, @"^(.{2,80}?)[：:]\s*[「『]([^」』]{2,80})[」』](?:\s+ほか.*)?$");
        if (topicMatch.Success)
        {
            work = topicMatch.Groups[1].Value.Trim();
            topic = topicMatch.Groups[2].Value.Trim();
        }

        // 推薦・学習 — Remove only deterministic suffix forms already handled elsewhere. This yields a search
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

    private static IReadOnlyDictionary<string, int[]> BuildNumericParenthesizedEvidenceValuesByService(
        AIrhythmRuntimeSnapshot snapshot)
    {
        var groups = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        void Observe(string? title, AIrhythmServiceIdentity service)
        {
            if (!TryParseTrailingParenthesizedNumber(title, out var stem, out var value)
                || IsLikelyCalendarYear(value)
                || value <= 0
                || value > 999
                || !service.IsValid)
                return;

            var key = BuildNumericServiceSequenceKey(stem, service);
            if (!groups.TryGetValue(key, out var values))
            {
                values = new HashSet<int>();
                groups[key] = values;
            }
            values.Add(value);
        }

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

        return groups.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(value => value).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsEvidenceCorroboratedParenthesizedEpisode(
        AIrhythmExplicitEpisodeAnalysis analysis,
        AIrhythmServiceIdentity service,
        IReadOnlyDictionary<string, int[]>? numericEvidenceValuesByService)
    {
        var value = analysis.ParenthesizedValue;
        if (numericEvidenceValuesByService is null
            || !analysis.HasTrailingParenthesizedNumber
            || IsLikelyCalendarYear(value)
            || value <= 0
            || value > 999
            || !service.IsValid
            || !numericEvidenceValuesByService.TryGetValue(BuildNumericServiceSequenceKey(analysis.ParenthesizedStem, service), out var evidenceValues)
            || evidenceValues.Length < 2
            || !evidenceValues.Any(candidate => candidate != value && Math.Abs(candidate - value) == 1))
            return false;

        return true;
    }

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

    private static bool IsCorroboratedParenthesizedWorkSequence(
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

        // 作品識別 — Parenthesized Work-sequence corroboration is valid only on the stable service that supplied the
        // corroborating local sequence. Never collapse a stem observed on one service into a global work
        // identity for another service.
        if (values.Length >= 3)
            return true;

        return values.Any(candidate => candidate != value && Math.Abs(candidate - value) == 1);
    }

    internal static bool IsCorroboratedParenthesizedWorkSequence(
        TvAirProgramEventDto item,
        IReadOnlyDictionary<string, int[]> localValuesByService)
        => IsCorroboratedParenthesizedWorkSequence(item.Title, ServiceIdentityOf(item), localValuesByService);

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

        // 作品識別 — A trailing parenthesized number cannot exist without an opening AND a closing parenthesis in
        // the source. Fullwidth parentheses normalize to their ASCII forms, but broadcast annotations cannot
        // invent either delimiter. Skip the expensive NFKC, annotation stripping and Regex path for ordinary EPG
        // titles. This is a necessary- condition guard only; all titles that could match still use the original
        // parser.
        if ((title.IndexOf('(') < 0 && title.IndexOf('（') < 0)
            || (title.IndexOf(')') < 0 && title.IndexOf('）') < 0))
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
        // 外部情報 — External episode follow-up shares the same single-episode contract as local continuity.
        // Parenthesized numbers, 第N回, ranges, years, and other structural numerics are not promoted to an
        // episode solely to query a provider.
        var analysis = AnalyzeExplicitEpisode(title);
        if (analysis.HasSingleEpisode)
            return analysis.EpisodeNumber;
        if (title is not null && title.IndexOf('第') >= 0 && title.IndexOf('夜') >= 0
            && (title.IndexOf('「') >= 0 || title.IndexOf('『') >= 0)
            && TryGetCanonicalQuotedEpisodeNumber(ParseCanonicalTitleParts(title).Episode, out var episodeNumber))
            return episodeNumber;
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

}
