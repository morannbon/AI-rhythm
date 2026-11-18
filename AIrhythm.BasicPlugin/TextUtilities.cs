namespace AIrhythm.BasicPlugin;

internal static class AIrhythmText
{
    internal static bool ContainsAny(string value, params string[] words)
        => words.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));

    // One role-vocabulary authority for broadcast metadata and saved contributor evaluation.
    internal static readonly IReadOnlyDictionary<string, string> ContributorRoleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["出演"] = "cast", ["出演者"] = "cast", ["キャスト"] = "cast", ["主演"] = "cast",
        ["声の出演"] = "voice", ["声優"] = "voice",
        ["司会"] = "host", ["mc"] = "host", ["ＭＣ"] = "host", ["ゲスト"] = "guest",
        ["解説"] = "commentator", ["実況"] = "commentator",
        ["原作"] = "author", ["原作者"] = "author",
        ["監督"] = "director", ["脚本"] = "writer", ["演出"] = "director",
        ["プロデューサー"] = "producer", ["制作"] = "production", ["製作"] = "production",
        ["音楽"] = "music", ["アーティスト"] = "artist", ["歌"] = "artist"
    };
    private static readonly HashSet<string> ContributorLabelTokens = new(
        ContributorRoleMap.Keys.Concat(new[] { "スケーター", "選手", "アナウンサー", "ほか", "他", "ほか多数", "その他" })
            .Select(label => label.Normalize(NormalizationForm.FormKC).ToLowerInvariant()),
        StringComparer.OrdinalIgnoreCase);

    internal static bool IsKnownContributorLabel(string normalizedIdentity)
        => ContributorLabelTokens.Contains(normalizedIdentity);

    // Preserve an explicit middle-initial boundary before compact identity removes punctuation.
    // Without the source dots, a historical compact Japanese-Latin-Japanese key is still ambiguous.
    // Keep the source-proven boundary in the canonical key; never infer it from a saved compact key.
    internal static string? CanonicalDelimitedMiddleInitial(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var normalized = source.Normalize(NormalizationForm.FormKC).Trim();
        var match = Regex.Match(normalized,
            @"^(?<first>[\p{IsKatakana}]{2,})\s*・\s*(?<initial>[A-Za-z])\s*・\s*(?<last>[\p{IsKatakana}]{2,})$",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        return $"{match.Groups["first"].Value}・{match.Groups["initial"].Value.ToLowerInvariant()}・{match.Groups["last"].Value}";
    }

    // A credit must identify one entity, not a programme role or an opaque concatenation. This gate is
    // shared by current metadata extraction and the reversible scoring projection for saved credits.
    // SearchCast intent is explicitly exempted at its own caller; no saved observation is deleted.
    internal static bool IsUnsupportedContributorIdentity(string entity, string role)
    {
        if (string.IsNullOrWhiteSpace(entity)) return true;
        if (IsKnownContributorLabel(entity)) return true;
        if (HasAmbiguousContributorBoundary(entity)) return true;
        // A broadcast location, studio or live relay label is context, not an identifiable contributor.
        // Apply to both current metadata and the scoring-only projection of saved observations.
        if (Regex.IsMatch(entity,
                @"^(?:現地|中継|会場|スタジオ|[\p{L}\p{N}]{2,}(?:現地|現地中継|会場中継|中継会場|スタジオ中継))$",
                RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(entity,
                @"^(?:(?:スタジオ|特別|スペシャル|番組|今週の|本日の)?(?:ゲスト|出演者|キャスト|司会|mc|解説|実況|キャスター))$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(entity,
                @"^(?:(?:気象|ニュース|スポーツ)?キャスター)[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]{2,}$",
                RegexOptions.CultureInvariant))
            return true;
        // "主題歌 + song/artist" without a delimiter cannot establish a person or artist identity.
        // Do not guess a split from a role prefix; retain the original evidence for later re-evaluation.
        if (Regex.IsMatch(entity, @"^(?:主題歌|挿入歌|劇伴|テーマ曲|エンディングテーマ|オープニングテーマ)[\p{L}\p{N}]+$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        // Long, unseparated all-kanji cast credits can contain two fused names; the original
        // delimiters have been lost in historic compact keys. Quarantine them only for actor/voice roles.
        if ((role is "cast" or "voice")
            && Regex.IsMatch(entity, @"^[\p{IsCJKUnifiedIdeographs}]{7,}$", RegexOptions.CultureInvariant))
            return true;
        return false;
    }

    // A long katakana character label fused with a kanji performer, or multiple
    // unseparated script families, cannot establish a single contributor identity.
    internal static bool HasAmbiguousContributorBoundary(string entity)
    {
        if (string.IsNullOrWhiteSpace(entity)) return false;
        return Regex.IsMatch(entity,
                @"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}][A-Za-z][A-Za-z.'-]*[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(entity,
                @"^[\p{IsKatakana}]{5,}[\p{IsCJKUnifiedIdeographs}]{3,}$",
                RegexOptions.CultureInvariant);
    }

}
