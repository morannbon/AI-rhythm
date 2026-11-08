namespace AIrhythm.BasicPlugin;

internal static class AIrhythmText
{
    internal static bool ContainsAny(string value, params string[] words)
        => words.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));

    // 作品識別 — A compact metadata identity containing a long katakana character label directly fused to a
    // kanji performer, or several unseparated script families, is not a defensible single-person
    // observation. There is no basis for guessing where the identities split. This predicate is shared by
    // fresh extraction and reversible scoring-only quarantine of historical observations.
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
