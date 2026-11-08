namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmRecommendationEngine
{
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

            // 保存・読込 — External evidence can change while Score is running (manual provider refresh). Cache only when
            // the evidence generation stayed stable for the whole calculation.
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
                CachedCalibrationCandidates = new();
            }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            AIrhythmDataState.WriteDeveloperLog(
                $"SCORE_RESULT_CACHE result=MISS scored={scored.Length} externalEvidenceGeneration={externalEvidenceGeneration}->{completedGeneration} cached={(completedGeneration == externalEvidenceGeneration)} scope=runtime_snapshot_exact semantics=unchanged");
#endif
            return scored;
        }
    }

    private static string[] ContributorRuleAlternatives(string? pattern)
        => (pattern ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool RuleTextMatch(string text, string pattern, bool useRegex)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        if (!useRegex)
        {
            // 文字列解析 — Non-regex alternatives are case-insensitive OR terms; regex uses a bounded timeout.
            ReadOnlySpan<char> remaining = pattern.AsSpan();
            while (true)
            {
                var separator = remaining.IndexOf('|');
                var token = (separator < 0 ? remaining : remaining[..separator]).Trim();
                if (!token.IsEmpty && text.AsSpan().Contains(token, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (separator < 0) break;
                remaining = remaining[(separator + 1)..];
            }
            return false;
        }
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string ContributorEntityKey(string? value)
        => CompactIdentity((value ?? string.Empty).Normalize(NormalizationForm.FormKC));

    private static string ContributorRoleKey(string role, string? entity)
    {
        var normalized = ContributorEntityKey(entity);
        return normalized.Length == 0 ? string.Empty : $"{role}|{normalized}";
    }

    private static IReadOnlyList<string> ContributorTrackedRuleKeys(TvAirKeywordRuleDto rule, string extendedItems)
    {
        // 作品識別 — Host non-regex keyword rules use | as OR. Retain the actually matched alternative as the
        // contributor identity instead of collapsing the whole rule text into one entity.
        if (rule.UseRegex)
        {
            var raw = (rule.Pattern ?? string.Empty).Trim();
            if (raw.Length == 0) return Array.Empty<string>();
            try
            {
                var matchedKeys = new List<string>(8);
                foreach (Match match in Regex.Matches(extendedItems, raw, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
                {
                    if (!match.Success || string.IsNullOrWhiteSpace(match.Value)) continue;
                    var value = NormalizeTrackedContributorValue(match.Value);
                    if (value.Length == 0) continue;
                    var key = $"tracked|{value}";
                    if (!matchedKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) matchedKeys.Add(key);
                    if (matchedKeys.Count >= 8) break;
                }
                return matchedKeys;
            }
            catch (ArgumentException)
            {
                return Array.Empty<string>();
            }
            catch (RegexMatchTimeoutException)
            {
                return Array.Empty<string>();
            }
        }

        var result = new List<string>(8);
        foreach (var token in ContributorRuleAlternatives(rule.Pattern))
        {
            if (!extendedItems.Contains(token, StringComparison.OrdinalIgnoreCase)) continue;
            var normalized = ContributorEntityKey(token);
            if (normalized.Length == 0) continue;
            var key = $"tracked|{normalized}";
            if (!result.Contains(key, StringComparer.OrdinalIgnoreCase)) result.Add(key);
        }
        return result;
    }

    private static string NormalizeTrackedContributorValue(string raw)
    {
        var value = ContributorTrailingNoteRegex.Replace((raw ?? string.Empty).Trim(), string.Empty).Trim();
        for (var i = 0; i < 3 && ContributorNestedRolePrefixRegex.IsMatch(value); i++)
            value = ContributorNestedRolePrefixRegex.Replace(value, string.Empty, 1).Trim();

        var compact = ContributorEntityKey(value);
        if (compact.Length < 2 || compact.Length > 80) return string.Empty;
        foreach (var compactRole in ContributorCompactRoleLabels)
        {
            if (compact.Length > compactRole.Length + 1 && compact.StartsWith(compactRole, StringComparison.OrdinalIgnoreCase))
            {
                compact = compact[compactRole.Length..].Trim();
                break;
            }
        }
        if (compact.Length < 2 || compact.Length > 80) return string.Empty;
        if (ContributorCompactNonEntityTokens.Contains(compact))
            return string.Empty;
        return compact;
    }

    private static readonly Regex ContributorExplicitVoiceCreditRegex = new(
        @"^.+?[（(]\s*(?:CV|ＣＶ|声|声優|声の出演)\s*[:：]\s*(?<person>[^()（）:：]+)\s*[）)]$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
    // 作品識別 — Aggregate only; never log raw personal metadata or alter the contributor identity contract.
    private static long _contributorParsedPairs;
    private static long _contributorAcceptedTokens;
    private static long _contributorRejectedTokens;
    private static long _contributorExplicitVoiceCredits;
    private static long _contributorColonCredits;
    private static long _contributorRolePairs;
    private static long _contributorDeferredMultipleChunks;
    private static long _contributorDeferredMixedScript;
    private static long _contributorDeferredInvalidLength;
    private static long _contributorDeferredNonEntity;
    private static long _contributorExplicitSeparatorGroups;
    private static long _contributorExplicitSeparatorPersons;
    private static readonly object ContributorDeferredSamplesGate = new();
    private static readonly Dictionary<string, List<string>> ContributorDeferredSamples = new(StringComparer.Ordinal);

    // 保存・読込 — Developer-only bounded samples preserve the original parser input for diagnosis. Never persist
    // these values or emit uncontrolled newlines / control characters.
    private static void CaptureContributorDeferredSample(string reason, string role, string? raw)
    {
        lock (ContributorDeferredSamplesGate)
        {
            if (!ContributorDeferredSamples.TryGetValue(reason, out var samples))
                ContributorDeferredSamples[reason] = samples = new List<string>(4);
            if (samples.Count >= 4) return;
            var safe = Regex.Replace(raw ?? string.Empty, @"[\p{Cc}\p{Cf}]", " ")
                .Replace('|', '／').Trim();
            if (safe.Length > 100) safe = safe[..100] + "…";
            var sample = $"role={role},length={(raw ?? string.Empty).Length},raw={safe}";
            if (!samples.Contains(sample, StringComparer.Ordinal)) samples.Add(sample);
        }
    }



    internal static void LogContributorExtractionDiagnostics()
    {
        AIrhythmDataState.WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_EXTRACTION_DIAGNOSTIC rolePairs={System.Threading.Interlocked.Read(ref _contributorRolePairs)} rawTokens={System.Threading.Interlocked.Read(ref _contributorParsedPairs)} acceptedTokens={System.Threading.Interlocked.Read(ref _contributorAcceptedTokens)} deferredTokens={System.Threading.Interlocked.Read(ref _contributorRejectedTokens)} deferredMultipleChunks={System.Threading.Interlocked.Read(ref _contributorDeferredMultipleChunks)} deferredMixedScript={System.Threading.Interlocked.Read(ref _contributorDeferredMixedScript)} deferredInvalidLength={System.Threading.Interlocked.Read(ref _contributorDeferredInvalidLength)} deferredNonEntity={System.Threading.Interlocked.Read(ref _contributorDeferredNonEntity)} explicitSeparatorGroups={System.Threading.Interlocked.Read(ref _contributorExplicitSeparatorGroups)} explicitSeparatorPersons={System.Threading.Interlocked.Read(ref _contributorExplicitSeparatorPersons)} explicitVoiceCredits={System.Threading.Interlocked.Read(ref _contributorExplicitVoiceCredits)} colonCredits={System.Threading.Interlocked.Read(ref _contributorColonCredits)} unit=parser_invocations cumulativeSincePluginLoad=True storageMutation=False scoreMutation=False");
        lock (ContributorDeferredSamplesGate)
        {
            foreach (var reason in ContributorDeferredSamples.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                foreach (var sample in ContributorDeferredSamples[reason])
                    AIrhythmDataState.WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_DEFERRED_SAMPLE reason={reason} {sample} sampleScope=first_four_distinct_parser_inputs developerOnly=True storageMutation=False scoreMutation=False");
            }
        }

    }
#endif

    // 保存・読込 — A middle dot is also used *inside* a single transliterated name. Split only clearly labelled
    // multi-person credits with at least three structurally distinct segments, including a Latin full name
    // between Japanese names. Ambiguous inputs remain deferred instead of becoming merged persistent
    // contributor identities.
    private static IReadOnlyList<string> ContributorExplicitlyDelimitedParts(string role, string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (role != "music" && role != "cast" && role != "voice" && role != "artist")
            return new[] { value };
        // 作品識別 — A repeated 【character】performer structure is an explicit boundary supplied by EPG, not an
        // inferred Japanese-name split. Consume it only when the entire value consists of complete
        // bracket/performer pairs. Otherwise defer to the existing conservative parser; never create a guessed
        // merged identity.
        if ((role is "cast" or "voice") && value.StartsWith('【') && value.IndexOf('【', 1) >= 0)
        {
            // 文字列解析 — A complete pair sequence requires at least two opening brackets. The canonical regex and
            // completeness check still own acceptance.
            var credits = Regex.Matches(value, @"【[^【】\r\n]{1,40}】\s*(?<performer>[^【】\r\n]+)", RegexOptions.CultureInvariant);
            if (credits.Count >= 2 && credits.Count <= 16)
            {
                var cursor = 0;
                var people = new List<string>(credits.Count);
                var complete = true;
                foreach (Match credit in credits)
                {
                    if (!string.IsNullOrWhiteSpace(value[cursor..credit.Index])) { complete = false; break; }
                    var performer = credit.Groups["performer"].Value.Trim();
                    if (performer.Length < 2 || performer.Length > 80
                        || Regex.IsMatch(performer, @"\S+[\s　]+\S+", RegexOptions.CultureInvariant))
                    { complete = false; break; }
                    people.Add(performer);
                    cursor = credit.Index + credit.Length;
                }
                if (complete && string.IsNullOrWhiteSpace(value[cursor..]))
                    return people;
            }
        }
        // 処理条件 — Without a leading bracket, this anchored grammar cannot match.
        if (!value.StartsWith('【'))
            return new[] { value };
        var label = Regex.Match(value, @"^【\s*(?<label>[^】]+)\s*】\s*(?<body>.+)$", RegexOptions.CultureInvariant);
        if (!label.Success || !ContributorRoleMap.TryGetValue(label.Groups["label"].Value.Trim(), out var labelledRole)
            || !string.Equals(labelledRole, role, StringComparison.Ordinal))
            return new[] { value };
        var body = label.Groups["body"].Value.Trim();
        var parts = body.Split('・', StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || parts.Length > 8 || parts.Any(x => x.Length < 2))
            return new[] { value };
        var hasLatin = parts.Any(x => Regex.IsMatch(x, @"[A-Za-z]", RegexOptions.CultureInvariant));
        var hasJapanese = parts.Any(x => Regex.IsMatch(x, @"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]", RegexOptions.CultureInvariant));
        if (!hasLatin || !hasJapanese)
            return new[] { value };
        // 処理条件 — Never infer a split if a segment still contains a second unmarked person.
        if (parts.Any(x => Regex.IsMatch(x, @"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}].*\s+.*[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]", RegexOptions.CultureInvariant)))
            return new[] { value };
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        System.Threading.Interlocked.Increment(ref _contributorExplicitSeparatorGroups);
        System.Threading.Interlocked.Add(ref _contributorExplicitSeparatorPersons, parts.Length);
#endif
        return parts;
    }

    private static string NormalizeContributorMetadataValue(string role, string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        // 作品識別 — A parenthesized, explicitly labelled voice credit carries reliable person identity. Resolve
        // that label before removing trailing parentheses; otherwise the character name survives and is
        // incorrectly learned as the performer.
        if ((role is "cast" or "voice") && (value.Contains('（') || value.Contains('('))
            && (value.Contains(':') || value.Contains('：')))
        {
            // 処理条件 — The anchored voice-credit grammar requires parentheses and a colon.
            var voiceCredit = ContributorExplicitVoiceCreditRegex.Match(value);
            if (voiceCredit.Success)
            {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                System.Threading.Interlocked.Increment(ref _contributorExplicitVoiceCredits);
#endif
                value = voiceCredit.Groups["person"].Value.Trim();
            }
        }
        // 処理条件 — A broadcaster may nest an explicit commentator/credit label in a cast field, followed by a
        // biographical parenthesis. This is structural metadata, not a second person's name. Only accept a
        // single, bracket-labelled name; any unmarked multi-person remainder continues through the conservative
        // gate.
        if ((role is "cast" or "voice" or "host" or "guest" or "commentator") && value.StartsWith('【'))
        {
            var nestedRole = Regex.Match(value,
                @"^【\s*(?:センター解説|解説|実況|司会|MC|ＭＣ|キャスター|気象キャスター|出演|出演者|声の出演)\s*】\s*(?<person>[^【】\r\n]+)$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (nestedRole.Success)
                value = nestedRole.Groups["person"].Value.Trim();
        }
        // 処理条件 — Biographical explanations are metadata only when they form ONE balanced trailing parenthesis
        // after a nonempty credit. A multi-person line such as "A（経歴）・B（経歴）" remains ambiguous and is never
        // guessed here.
        if ((value.EndsWith('）') || value.EndsWith(')'))
            && (value.Contains('（') || value.Contains('('))
            && Regex.IsMatch(value, @"^[^（）()\r\n]{2,80}[（(][^（）()\r\n]{1,80}[）)]$", RegexOptions.CultureInvariant))
            value = Regex.Replace(value, @"[（(][^（）()\r\n]{1,80}[）)]$", string.Empty, RegexOptions.CultureInvariant).Trim();
        value = ContributorTrailingNoteRegex.Replace(value, string.Empty).Trim();
        // 処理条件 — Broadcasters sometimes repeat a role label inside the value, e.g. "出演者：出演：山田太郎" or
        // "音楽：音楽：山田太郎". Strip only known role labels.
        for (var i = 0; i < 2 && ContributorNestedRolePrefixRegex.IsMatch(value); i++)
            value = ContributorNestedRolePrefixRegex.Replace(value, string.Empty, 1).Trim();

        // 保存・読込 — The host supplies text, not structured contributor identities. Whitespace between independent
        // text chunks is not evidence that those chunks name ONE contributor. Reject ambiguous multi-chunk
        // values for every role (including music/staff), instead of compacting them into one persistent person
        // identity. Explicitly labelled character:performer values are handled below, before identity
        // compaction. An ordinary Latin full name has internal word spaces; those spaces alone are not proof of
        // multiple contributors. Retain the entity as a single identity. This is deliberately Latin-only:
        // Japanese names and mixed-script text still require an explicit source-level boundary instead of a
        // guessed split.
        var explicitLatinFullName = value.Contains(' ') && Regex.IsMatch(value,
            @"^[A-Za-z][A-Za-z'’.-]*(?:[ ]+[A-Za-z][A-Za-z'’.-]*){1,3}$",
            RegexOptions.CultureInvariant);
        if (!explicitLatinFullName && !value.Contains(':') && !value.Contains('：')
            && Regex.IsMatch(value, @"\S+[\s　]+\S+", RegexOptions.CultureInvariant))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            System.Threading.Interlocked.Increment(ref _contributorDeferredMultipleChunks);
            CaptureContributorDeferredSample("multiple_chunks", role, raw);
#endif
            return string.Empty;
        }

        // 作品識別 — Multiple scripts run together without a separator are ambiguous (e.g. a Japanese credit
        // followed immediately by a Latin credit). Never infer identity boundaries. A single script transition
        // can be a legitimate person's name, so only reject Japanese -> Latin -> Japanese sequences with no
        // structural separator.
        if (Regex.IsMatch(value, @"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}][A-Za-z][A-Za-z .'-]*[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]", RegexOptions.CultureInvariant))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            System.Threading.Interlocked.Increment(ref _contributorDeferredMixedScript);
            CaptureContributorDeferredSample("mixed_script", role, raw);
#endif
            return string.Empty;
        }

        // 処理条件 — Cast/voice metadata often uses "役名：演者". A contributor axis must learn the person, not a
        // concatenated character+person token, so prefer the final labeled segment.
        if ((role == "cast" || role == "voice") && (value.Contains(':') || value.Contains('：')))
        {
            var segments = value.Split(new[] { ':', '：' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length >= 2)
            {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                System.Threading.Interlocked.Increment(ref _contributorColonCredits);
#endif
                value = segments[^1];
            }
        }

        value = ContributorTrailingNoteRegex.Replace(value.Trim(), string.Empty).Trim();
        var compact = ContributorEntityKey(value);
        if (compact.Length < 2 || compact.Length > 80)
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            System.Threading.Interlocked.Increment(ref _contributorDeferredInvalidLength);
            CaptureContributorDeferredSample("invalid_length", role, raw);
#endif
            return string.Empty;
        }
        foreach (var compactRole in ContributorCompactRoleLabels)
        {
            if (compact.Length > compactRole.Length + 1 && compact.StartsWith(compactRole, StringComparison.OrdinalIgnoreCase))
            {
                compact = compact[compactRole.Length..].Trim();
                break;
            }
        }
        if (compact.Length < 2 || compact.Length > 80)
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            System.Threading.Interlocked.Increment(ref _contributorDeferredInvalidLength);
            CaptureContributorDeferredSample("invalid_length", role, raw);
#endif
            return string.Empty;
        }
        if (ContributorCompactNonEntityTokens.Contains(compact))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            System.Threading.Interlocked.Increment(ref _contributorDeferredNonEntity);
            CaptureContributorDeferredSample("non_entity", role, raw);
#endif
            return string.Empty;
        }
        // 推薦・学習 — Apply the same evidence boundary to new metadata and historical scoring. Raw programme credits
        // are retained; never invent a split or rewrite facts.
        if (AIrhythmText.HasAmbiguousContributorBoundary(compact))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            System.Threading.Interlocked.Increment(ref _contributorDeferredMixedScript);
            CaptureContributorDeferredSample("unseparated_identity", role, raw);
#endif
            return string.Empty;
        }
        return compact;
    }

    internal static string[] ContributorKeysForInterest(TvAirProgramEventDto eventRow)
        => MetadataContributorKeys(eventRow.ExtendedItems).Take(24).ToArray();

    // 保存・読込 — Metadata extraction depends only on the exact broadcast description and the immutable
    // contributor grammar. Retain a strictly bounded, cross-render memo; rule/service-dependent SearchCast
    // matching remains in the per-score cache. This also prevents reparsing unchanged descriptions after EPG
    // invalidation.
    private const int ContributorMetadataCacheEntryLimit = 8192;
    private const int ContributorMetadataCacheCharacterLimit = 8 * 1024 * 1024;
    private const int ContributorMetadataCacheMaxEntryLength = 8192;
    private static readonly object ContributorMetadataCacheGate = new();
    private static readonly Dictionary<string, IReadOnlyList<string>> ContributorMetadataCache =
        new(StringComparer.Ordinal);
    private static readonly Queue<string> ContributorMetadataCacheOrder = new();
    private static int ContributorMetadataCacheCharacters;

    internal static void ClearContributorMetadataCache()
    {
        lock (ContributorMetadataCacheGate)
        {
            ContributorMetadataCache.Clear();
            ContributorMetadataCacheOrder.Clear();
            ContributorMetadataCacheCharacters = 0;
        }
    }

    private static IReadOnlyList<string> MetadataContributorKeys(string? extendedItems)
    {
        if (string.IsNullOrWhiteSpace(extendedItems))
            return Array.Empty<string>();
        if (extendedItems.Length > ContributorMetadataCacheMaxEntryLength)
            return ParseMetadataContributorKeys(extendedItems);

        lock (ContributorMetadataCacheGate)
            if (ContributorMetadataCache.TryGetValue(extendedItems, out var cached))
                return cached;

        var parsed = ParseMetadataContributorKeys(extendedItems);
        lock (ContributorMetadataCacheGate)
        {
            if (ContributorMetadataCache.TryGetValue(extendedItems, out var cached))
                return cached;
            while (ContributorMetadataCacheOrder.Count > 0
                   && (ContributorMetadataCache.Count >= ContributorMetadataCacheEntryLimit
                       || ContributorMetadataCacheCharacters + extendedItems.Length > ContributorMetadataCacheCharacterLimit))
            {
                var oldest = ContributorMetadataCacheOrder.Dequeue();
                if (ContributorMetadataCache.Remove(oldest))
                    ContributorMetadataCacheCharacters -= oldest.Length;
            }
            ContributorMetadataCache[extendedItems] = parsed;
            ContributorMetadataCacheOrder.Enqueue(extendedItems);
            ContributorMetadataCacheCharacters += extendedItems.Length;
        }
        return parsed;
    }

    // 文字列解析 — Allocation-free necessary-condition check for ContributorMetadataRegex. Its grammar requires a
    // known role label, optional \s, and an ASCII or full-width colon. Never enforce the left boundary here:
    // false positives are harmless, while the regex remains the single owner of valid pairs.
    private static bool MayContainContributorRoleValuePair(string text)
    {
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            var colonOffset = remaining.IndexOfAny(':', '：');
            if (colonOffset < 0) return false;
            var colonIndex = text.Length - remaining.Length + colonOffset;
            var roleEnd = colonIndex;
            while (roleEnd > 0 && char.IsWhiteSpace(text[roleEnd - 1]))
                roleEnd--;
            foreach (var label in ContributorRoleMap.Keys)
            {
                if (roleEnd >= label.Length
                    && text.AsSpan(roleEnd - label.Length, label.Length)
                        .Equals(label.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            remaining = text.AsSpan(colonIndex + 1);
        }
        return false;
    }

    private static IReadOnlyList<string> ParseMetadataContributorKeys(string? extendedItems)
    {
        if (string.IsNullOrWhiteSpace(extendedItems)
            || (extendedItems.IndexOf(':') < 0 && extendedItems.IndexOf('：') < 0))
        {
            return Array.Empty<string>();
        }

        // 推薦・学習 — TvAIr currently exposes ExtendedItems as text, not a structured cast/staff DTO. A role/value
        // pair accepted by ContributorMetadataRegex necessarily contains ':' or '：', so texts without either
        // separator cannot produce contributor evidence. Avoid entering the regex/list path for that common
        // empty-result case while keeping the accepted grammar exact. The capture regex requires a known role
        // immediately before ':' or '：' (allowing whitespace). Inspect only separator positions first: a long
        // EPG synopsis that mentions "出演" or "歌" elsewhere must not invoke the full capture regex for an
        // unrelated URL or explanatory colon. This is only a necessary-condition check; accepted pairs still use
        // the canonical regex.
        if (!MayContainContributorRoleValuePair(extendedItems))
            return Array.Empty<string>();

        List<string>? result = null;
        foreach (Match match in ContributorMetadataRegex.Matches(extendedItems))
        {
            if (!match.Success || !ContributorRoleMap.TryGetValue(match.Groups["role"].Value.Trim(), out var role))
                continue;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            System.Threading.Interlocked.Increment(ref _contributorRolePairs);
#endif
            // 文字列解析 — No separator means Regex.Split would return the original single value; avoid allocating a
            // Regex runner and match objects in that common case.
            var rawValue = match.Groups["value"].Value;
            var rawGroups = rawValue.AsSpan().IndexOfAny("、,，/／＆&".AsSpan()) < 0
                ? new[] { rawValue }
                : ContributorNameSplitRegex.Split(rawValue);
            foreach (var rawGroup in rawGroups)
            foreach (var raw in ContributorExplicitlyDelimitedParts(role, rawGroup))
            {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                System.Threading.Interlocked.Increment(ref _contributorParsedPairs);
#endif
                var value = NormalizeContributorMetadataValue(role, raw);
                if (value.Length == 0)
                {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                    System.Threading.Interlocked.Increment(ref _contributorRejectedTokens);
#endif
                    continue;
                }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                System.Threading.Interlocked.Increment(ref _contributorAcceptedTokens);
#endif
                var key = ContributorRoleKey(role, value);
                if (key.Length == 0) continue;
                result ??= new List<string>(16);
                if (!result.Contains(key, StringComparer.OrdinalIgnoreCase))
                    result.Add(key);
                if (result.Count >= 24) return result;
            }
        }
        return result is null ? Array.Empty<string>() : result;
    }

    private static bool RuleServiceAllowed(TvAirKeywordRuleDto rule, TvAirProgramEventDto eventRow)
    {
        if (rule.UseAllChannels || string.IsNullOrWhiteSpace(rule.TargetServices)) return true;
        var exact = $"{eventRow.NetworkId}:{eventRow.TransportStreamId}:{eventRow.ServiceId}";
        // 処理条件 — Channel selection accepts service triplets and numeric SID entries; empty tokens are ignored.
        ReadOnlySpan<char> remaining = rule.TargetServices.AsSpan();
        while (true)
        {
            var separator = remaining.IndexOf(',');
            var token = (separator < 0 ? remaining : remaining[..separator]).Trim();
            if (!token.IsEmpty)
            {
                if (token.Equals(exact.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true;
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacySid)
                    && legacySid == eventRow.ServiceId) return true;
            }
            if (separator < 0) break;
            remaining = remaining[(separator + 1)..];
        }
        return false;
    }

    private static IReadOnlyList<string> ContributorKeysForEvent(
        TvAirProgramEventDto? eventRow,
        IReadOnlyList<TvAirKeywordRuleDto> activeSearchCastRules)
    {
        if (eventRow is null)
            return Array.Empty<string>();

        List<string>? result = null;
        foreach (var key in MetadataContributorKeys(eventRow.ExtendedItems))
        {
            result ??= new List<string>(24);
            if (!result.Contains(key, StringComparer.OrdinalIgnoreCase)) result.Add(key);
        }

        if (!string.IsNullOrWhiteSpace(eventRow.ExtendedItems))
        {
            foreach (var rule in activeSearchCastRules)
            {
                if (!RuleServiceAllowed(rule, eventRow)) continue;
                if (!RuleTextMatch(eventRow.ExtendedItems!, rule.Pattern, rule.UseRegex)) continue;
                if (!string.IsNullOrWhiteSpace(rule.ExcludePattern)
                    && RuleTextMatch(eventRow.ExtendedItems!, rule.ExcludePattern, rule.UseRegex)) continue;
                foreach (var tracked in ContributorTrackedRuleKeys(rule, eventRow.ExtendedItems!))
                {
                    result ??= new List<string>(24);
                    if (!result.Contains(tracked, StringComparer.OrdinalIgnoreCase)) result.Add(tracked);
                }
                if (result is { Count: >= 24 }) break;
            }
        }
        return result is null ? Array.Empty<string>() : result;
    }

    // 保存・読込 — Per-score cache only: a broadcast description and its service determine metadata and
    // SearchCast contributor identity for the current immutable rule snapshot. Never cache between
    // refreshes, where the enabled rules or service eligibility may have changed.
    private readonly record struct ContributorEventCacheKey(
        string? ExtendedItems, int NetworkId, int TransportStreamId, int ServiceId);

    private static IReadOnlyList<string> ContributorKeysForEvent(
        TvAirProgramEventDto eventRow,
        IReadOnlyList<TvAirKeywordRuleDto> activeSearchCastRules,
        Dictionary<ContributorEventCacheKey, IReadOnlyList<string>> cache)
    {
        var key = new ContributorEventCacheKey(eventRow.ExtendedItems,
            eventRow.NetworkId, eventRow.TransportStreamId, eventRow.ServiceId);
        if (!cache.TryGetValue(key, out var result))
        {
            result = ContributorKeysForEvent(eventRow, activeSearchCastRules);
            cache.Add(key, result);
        }
        return result;
    }

    // 推薦・学習 — Every meaningful reservation leaves positive factual evidence. Provenance changes the
    // *weight*, not the existence, of that evidence. These are uniform policy coefficients, never
    // person/title-specific overrides.
    private const double IncidentalPreferenceWeight = AIrhythmPreferenceOriginPolicy.IncidentalContributorWeight;
    private const double UnknownProvenancePreferenceWeight = 0.15d;

    private static (double Work, double Genre, double Terms, double Service, double Hour) PreferenceWeightsForReservation(
        TvAirReservationDto reservation,
        IReadOnlyDictionary<int, TvAirKeywordRuleDto> rulesById)
    {
        if (!AIrhythmDataState.IsUsefulReservation(reservation)
            || AIrhythmPreferenceOriginPolicy.IsExcludedReservation(reservation))
            return (0, 0, 0, 0, 0);
        if (reservation.Intent == TvAirReservationIntent.InteractiveProgramEvent)
            return (1, 1, 1, 1, 1);
        if (reservation.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule)
        {
            if (reservation.SourceRuleId is int ruleId && rulesById.TryGetValue(ruleId, out var rule))
            {
                return (rule.SearchTitle ? 1 : IncidentalPreferenceWeight,
                    !string.IsNullOrWhiteSpace(rule.TargetGenres) ? 1 : IncidentalPreferenceWeight,
                    IncidentalPreferenceWeight, IncidentalPreferenceWeight, IncidentalPreferenceWeight);
            }
            return (UnknownProvenancePreferenceWeight, UnknownProvenancePreferenceWeight,
                UnknownProvenancePreferenceWeight, UnknownProvenancePreferenceWeight, UnknownProvenancePreferenceWeight);
        }
        return (UnknownProvenancePreferenceWeight, UnknownProvenancePreferenceWeight,
            UnknownProvenancePreferenceWeight, UnknownProvenancePreferenceWeight, UnknownProvenancePreferenceWeight);
    }

    private static TvAirReservationDto? FindOriginatingReservation(
        TvAirRecordingHistoryDto recording,
        IReadOnlyDictionary<string, TvAirReservationDto> reservationById)
        => !string.IsNullOrWhiteSpace(recording.ReservationId)
            && reservationById.TryGetValue(recording.ReservationId, out var reservation)
            && AIrhythmDataState.RecordingBelongsToReservationOccurrence(recording, reservation)
                ? reservation : null;

    private static bool IsRecordingPreferenceEvidence(
        TvAirRecordingHistoryDto recording,
        IReadOnlyDictionary<string, TvAirReservationDto> reservationById)
    {
        var reservation = FindOriginatingReservation(recording, reservationById);
        if (reservation is not null)
            return !AIrhythmPreferenceOriginPolicy.IsExcludedReservation(reservation);

        return AIrhythmDataState.TryGetRecordedPreferenceOrigin(recording, out var intent, out var source)
            && AIrhythmPreferenceOriginPolicy.AllowsRecordingPreference(true, intent, source);
    }

    private static AIrhythmBehaviorEvidenceAxes BehaviorEvidenceAxesForRecording(
        TvAirRecordingHistoryDto recording,
        IReadOnlyDictionary<string, TvAirReservationDto> reservationById,
        IReadOnlyDictionary<int, TvAirKeywordRuleDto> rulesById)
    {
        var reservation = FindOriginatingReservation(recording, reservationById);
        if (reservation is not null)
            return BehaviorEvidenceAxesForReservation(reservation, rulesById);
        return IsRecordingPreferenceEvidence(recording, reservationById)
            ? AIrhythmBehaviorEvidenceAxes.DirectSelection
            : AIrhythmBehaviorEvidenceAxes.None;
    }

    private static IEnumerable<TvAirRecordingHistoryDto> PreferenceRecordingHistory(AIrhythmRuntimeSnapshot snapshot)
    {
        var reservationById = BuildLatestReservationIndex(snapshot.ReservationRecords);
        return snapshot.History.Where(recording => IsRecordingPreferenceEvidence(recording, reservationById));
    }

    private static AIrhythmBehaviorEvidenceAxes BehaviorEvidenceAxesForReservation(
        TvAirReservationDto reservation,
        IReadOnlyDictionary<int, TvAirKeywordRuleDto> rulesById)
    {
        var weights = PreferenceWeightsForReservation(reservation, rulesById);
        return new AIrhythmBehaviorEvidenceAxes(weights.Work > 0, weights.Genre > 0,
            weights.Terms > 0, weights.Service > 0, weights.Hour > 0,
            !AIrhythmPreferenceOriginPolicy.IsExcludedReservation(reservation));
    }

    private static IReadOnlyList<AIrhythmContributorObservation> ContributorObservationsForReservation(
        TvAirReservationDto reservation,
        TvAirProgramEventDto? eventRow,
        IReadOnlyDictionary<int, TvAirKeywordRuleDto> activeSearchCastRulesById)
    {
        if (eventRow is null || AIrhythmPreferenceOriginPolicy.IsExcludedReservation(reservation))
            return Array.Empty<AIrhythmContributorObservation>();
        // 処理条件 — Metadata is a true observation even when the person was not the search motive.
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in MetadataContributorKeys(eventRow.ExtendedItems))
            weights[key] = IncidentalPreferenceWeight;

        var automatic = reservation.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule;
        if (automatic && reservation.SourceRuleId is int sourceRuleId
            && activeSearchCastRulesById.TryGetValue(sourceRuleId, out var rule)
            && RuleServiceAllowed(rule, eventRow)
            && !string.IsNullOrWhiteSpace(eventRow.ExtendedItems)
            && RuleTextMatch(eventRow.ExtendedItems!, rule.Pattern, rule.UseRegex)
            && (string.IsNullOrWhiteSpace(rule.ExcludePattern)
                || !RuleTextMatch(eventRow.ExtendedItems!, rule.ExcludePattern, rule.UseRegex)))
        {
            foreach (var tracked in ContributorTrackedRuleKeys(rule, eventRow.ExtendedItems!))
                weights[tracked] = 1.0d;
        }
        // 処理条件 — An explicit search target must not be displaced by the metadata limit. Retain one positive
        // observation per distinct contributor key.
        return weights.OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .Select(x => new AIrhythmContributorObservation(x.Key, x.Value)).ToArray();
    }

    private static Dictionary<(int NetworkId, int TransportStreamId, int ServiceId, int EventNumber), TvAirProgramEventDto> BuildReservationEventLookup(
        AIrhythmRuntimeSnapshot snapshot,
        IReadOnlyList<TvAirReservationDto> userModelReservations)
    {
        var needed = new HashSet<(int NetworkId, int TransportStreamId, int ServiceId, int EventNumber)>();
        foreach (var reservation in userModelReservations)
            needed.Add((reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId, reservation.EventNumber));

        var lookup = new Dictionary<(int NetworkId, int TransportStreamId, int ServiceId, int EventNumber), TvAirProgramEventDto>(needed.Count);
        if (needed.Count == 0)
            return lookup;

        foreach (var eventRow in snapshot.Events)
        {
            var key = (eventRow.NetworkId, eventRow.TransportStreamId, eventRow.ServiceId, eventRow.EventNumber);
            if (!needed.Contains(key))
                continue;
            if (!lookup.TryGetValue(key, out var current) || eventRow.Start < current.Start)
                lookup[key] = eventRow;
        }
        return lookup;
    }

    private static AIrhythmLearnedUserModelState BuildOrUpdateLearnedUserModel(
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmEvidenceIdentityContext identityContext,
        IReadOnlyDictionary<int, TvAirKeywordRuleDto> rulesById,
        IReadOnlyDictionary<int, TvAirKeywordRuleDto> activeSearchCastRulesById)
    {
        // Only a single confirmed work for a given observation ID may authorize correction of old learned
        // contributions. Reused reservation IDs with conflicting works are deliberately left untouched.
        var excludedReservationOrigins = snapshot.ReservationRecords
            .Where(AIrhythmPreferenceOriginPolicy.IsExcludedReservation)
            .Where(x => !string.IsNullOrWhiteSpace(x.ReservationId))
            .Select(x => (Id: $"reservation:{x.ReservationId}", Work: CanonicalEvidenceFacts(x, identityContext).WorkKey))
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .Where(g => g.Select(x => x.Work).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Work, StringComparer.Ordinal);
        var userModelReservations = snapshot.ReservationRecords
            .Where(AIrhythmDataState.IsUsefulReservation)
            .Where(x => !AIrhythmPreferenceOriginPolicy.IsExcludedReservation(x))
            .Where(x => !string.IsNullOrWhiteSpace(x.ReservationId))
            .ToArray();
        var eventByIdentity = BuildReservationEventLookup(snapshot, userModelReservations);
        var observations = new List<AIrhythmUserModelObservation>();

        var baseEligibleReservations = 0;
        var contributorOnlyReservations = 0;
        var provenanceDeferredReservations = 0;
        foreach (var reservation in userModelReservations)
        {
            var reservationFacts = CanonicalEvidenceFacts(reservation, identityContext);
            var genreKey = reservationFacts.GenreKey;
            IReadOnlyList<string> terms = reservationFacts.Terms;
            TvAirProgramEventDto? matchedEvent = null;
            if (eventByIdentity.TryGetValue((reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId, reservation.EventNumber), out var eventRow))
            {
                matchedEvent = eventRow;
                var eventFacts = CanonicalEvidenceFacts(eventRow, identityContext);
                if (eventFacts.GenreKey.Length > 0) genreKey = eventFacts.GenreKey;
                terms = eventFacts.Terms;
            }
            IReadOnlyList<AIrhythmContributorObservation> contributors = ContributorObservationsForReservation(reservation, matchedEvent, activeSearchCastRulesById);
            var axes = BehaviorEvidenceAxesForReservation(reservation, rulesById);
            var preferenceWeights = PreferenceWeightsForReservation(reservation, rulesById);

            if (axes.HasBaseAxis) baseEligibleReservations++;
            if (!axes.HasBaseAxis && contributors.Count > 0) contributorOnlyReservations++;
            if (!axes.HasBaseAxis && contributors.Count == 0) provenanceDeferredReservations++;

            // 作品識別 — One programme occurrence is one additive observation. Repeated episodes selected by the same
            // rule are intentionally retained as separate facts; the same ReservationId is still one observation so
            // retries/reloads cannot multiply it.
            var observationId = $"reservation:{reservation.ReservationId}";
            observations.Add(new AIrhythmUserModelObservation(
                observationId,
                reservationFacts.WorkKey,
                genreKey,
                reservationFacts.ServiceIdentity.IsValid ? reservationFacts.ServiceIdentity.ToString() : string.Empty,
                reservationFacts.Hour,
                terms,
                contributors,
                LearnWork: axes.Work,
                LearnGenre: axes.Genre,
                LearnTerms: axes.Terms,
                LearnService: axes.Service,
                LearnHour: axes.Hour,
                WorkEvidenceWeight: preferenceWeights.Work,
                GenreEvidenceWeight: preferenceWeights.Genre,
                TermsEvidenceWeight: preferenceWeights.Terms,
                ServiceEvidenceWeight: preferenceWeights.Service,
                HourEvidenceWeight: preferenceWeights.Hour));
        }

        var interestSignals = AIrhythmDataState.GetInterestSignals();
        foreach (var signal in interestSignals)
        {
            var service = new AIrhythmServiceIdentity(signal.NetworkId, signal.TransportStreamId, signal.ServiceId);
            var contributorKeys = signal.ContributorKeys ?? Array.Empty<string>();
            if (contributorKeys.Length == 0)
            {
                var currentEvent = snapshot.Events.FirstOrDefault(x => string.Equals(x.EventId, signal.EventId, StringComparison.Ordinal));
                if (currentEvent is not null)
                    contributorKeys = ContributorKeysForInterest(currentEvent);
            }
            var contributors = contributorKeys
                .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 160)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .Select(x => new AIrhythmContributorObservation(x, 1.0d))
                .ToArray();
            observations.Add(new AIrhythmUserModelObservation(
                $"interest:{signal.EventId}:{signal.SelectedAt.UtcDateTime.Ticks}",
                signal.SeriesKey,
                NormalizeGenre(signal.Genre),
                service.IsValid ? service.ToString() : string.Empty,
                null,
                Tokens(signal.SeriesKey).ToArray(),
                contributors));
        }

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog($"USER_MODEL_SOURCE_SUMMARY baseEligibleReservations={baseEligibleReservations} contributorOnlyReservations={contributorOnlyReservations} provenanceDeferredReservations={provenanceDeferredReservations} interests={interestSignals.Count} basePreference=causal_weighted_positive_evidence automaticReservationLearning=all_observed_axes_weighted_by_provenance searchCastContributorLearning=originating_rule_strong_metadata_incidental repeatedRuleEpisodes=independent_programme_facts programTimeSlotLearning=zero notConditionLearning=zero");
        var activeSearchCastRules = snapshot.KeywordRules.Where(x => x.Enabled && x.SearchCast).ToArray();
        AIrhythmDataState.WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_RULE_SUMMARY activeSearchCastRules={activeSearchCastRules.Length} regexRules={activeSearchCastRules.Count(x => x.UseRegex)} semantics=current_explicit_intent_scored_on_matching_extendeditems persistence=reservation_provenance_only missingMatchNotNegative=True");
        foreach (var rule in activeSearchCastRules.Take(16))
        {
            var diagnosticPattern = rule.UseRegex
                ? $"regex:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rule.Pattern ?? string.Empty))).ToLowerInvariant()[..16]}"
                : (rule.Pattern ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/').Trim();
            AIrhythmDataState.WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_RULE_SAMPLE ruleId={rule.RuleId} entity={diagnosticPattern} source=SearchCast enabled=True useRegex={rule.UseRegex} allChannels={rule.UseAllChannels} scoring=current_candidate_match provenance=persist_after_matching_reservation");
        }
#endif
        return AIrhythmDataState.ObserveLearnedUserModel(observations, excludedReservationOrigins);
    }

    private static double MaxLearnedWeight(Dictionary<string, double>? weights)
    {
        if (weights is not { Count: > 0 }) return 0.0d;
        var max = 0.0d;
        foreach (var value in weights.Values)
            if (value > max) max = value;
        return max;
    }

    private static AIrhythmLearnedAffinityContext BuildLearnedAffinityContext(AIrhythmLearnedUserModelState model)
    {
        var serviceWeightsByIdentity = new Dictionary<AIrhythmServiceIdentity, double>();
        foreach (var pair in model.ServiceWeights)
        {
            var parts = pair.Key.Split(':');
            if (parts.Length == 3
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var networkId)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var transportStreamId)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var serviceId))
            {
                var identity = new AIrhythmServiceIdentity(networkId, transportStreamId, serviceId);
                if (identity.IsValid)
                    serviceWeightsByIdentity[identity] = pair.Value;
            }
        }

        var hourWeightsByHour = new Dictionary<int, double>();
        foreach (var pair in model.HourWeights)
        {
            if (int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour))
                hourWeightsByHour[hour] = pair.Value;
        }

        return new AIrhythmLearnedAffinityContext(
            model,
            MaxLearnedWeight(model.WorkWeights),
            MaxLearnedWeight(model.GenreWeights),
            MaxLearnedWeight(model.TermWeights),
            MaxLearnedWeight(model.ServiceWeights),
            MaxLearnedWeight(model.HourWeights),
            MaxLearnedWeight(model.ContributorWeights),
            serviceWeightsByIdentity,
            hourWeightsByHour);
    }

    private static AIrhythmLearnedAffinityBreakdown LearnedUserAffinityBreakdown(
        AIrhythmLearnedAffinityContext context,
        AIrhythmCanonicalEvidenceFacts facts,
        IReadOnlyList<string>? contributorKeys = null)
    {
        var model = context.Model;
        if (model.ObservationCount <= 0)
            return new AIrhythmLearnedAffinityBreakdown(0.0d, 0.0d, 0.0d, false, 0.0d, false, 0.0d, 0.0d);

        static double Axis(Dictionary<string, double> map, string key, double max)
        {
            if (max <= 0.0d || map.Count == 0 || string.IsNullOrWhiteSpace(key) || !map.TryGetValue(key, out var value))
                return 0.0d;
            return Math.Clamp(value / max, 0.0d, 1.0d);
        }

        var workAffinity = Axis(model.WorkWeights, facts.WorkKey, context.MaxWorkWeight);
        var serviceAffinity = 0.0d;
        if (context.MaxServiceWeight > 0.0d
            && facts.ServiceIdentity.IsValid
            && context.ServiceWeightsByIdentity.TryGetValue(facts.ServiceIdentity, out var serviceWeight))
        {
            serviceAffinity = Math.Clamp(serviceWeight / context.MaxServiceWeight, 0.0d, 1.0d);
        }
        var hourAffinity = 0.0d;
        if (context.MaxHourWeight > 0.0d && context.HourWeightsByHour.TryGetValue(facts.Hour, out var hourWeight))
            hourAffinity = Math.Clamp(hourWeight / context.MaxHourWeight, 0.0d, 1.0d);
        var hasGenre = facts.GenreKey.Length > 0;
        var genreAffinity = hasGenre ? Axis(model.GenreWeights, facts.GenreKey, context.MaxGenreWeight) : 0.0d;

        static bool IsDuplicateEarlier(IReadOnlyList<string> keys, int index)
        {
            var key = keys[index];
            for (var i = 0; i < index; i++)
            {
                if (string.Equals(keys[i], key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static double AverageTopThreeTermWeights(
            IReadOnlyList<string> keys,
            Dictionary<string, double> weights,
            double maxWeight,
            out bool hasTerms)
        {
            hasTerms = keys.Count > 0 && maxWeight > 0.0d;
            if (!hasTerms)
                return 0.0d;

            double top1 = 0.0d;
            double top2 = 0.0d;
            double top3 = 0.0d;
            var distinctCount = 0;
            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                if (string.IsNullOrWhiteSpace(key) || IsDuplicateEarlier(keys, i))
                    continue;
                distinctCount++;
                var value = weights.TryGetValue(key, out var rawWeight) ? rawWeight / maxWeight : 0.0d;
                if (value > top1)
                {
                    top3 = top2;
                    top2 = top1;
                    top1 = value;
                }
                else if (value > top2)
                {
                    top3 = top2;
                    top2 = value;
                }
                else if (value > top3)
                {
                    top3 = value;
                }
            }
            var denominator = Math.Min(3, distinctCount);
            return denominator == 0
                ? 0.0d
                : Math.Clamp((top1 + top2 + top3) / denominator, 0.0d, 1.0d);
        }

        static double AverageTopThreePositiveContributorWeights(
            IReadOnlyList<string> keys,
            Dictionary<string, double>? weights,
            double maxWeight)
        {
            if (keys.Count == 0 || maxWeight <= 0.0d || weights is null || weights.Count == 0)
                return 0.0d;

            double top1 = 0.0d;
            double top2 = 0.0d;
            double top3 = 0.0d;
            var positiveCount = 0;
            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                if (string.IsNullOrWhiteSpace(key)
                    || IsDuplicateEarlier(keys, i)
                    || !weights.TryGetValue(key, out var rawWeight))
                {
                    continue;
                }
                var value = rawWeight / maxWeight;
                if (value <= 0.0d)
                    continue;
                positiveCount++;
                if (value > top1)
                {
                    top3 = top2;
                    top2 = top1;
                    top1 = value;
                }
                else if (value > top2)
                {
                    top3 = top2;
                    top2 = value;
                }
                else if (value > top3)
                {
                    top3 = value;
                }
            }
            var denominator = Math.Min(3, positiveCount);
            return denominator == 0
                ? 0.0d
                : Math.Clamp((top1 + top2 + top3) / denominator, 0.0d, 1.0d);
        }

        var termAffinity = AverageTopThreeTermWeights(
            facts.Terms,
            model.TermWeights,
            context.MaxTermWeight,
            out var hasTerms);

        var contributorAffinity = 0.0d;
        if (contributorKeys is { Count: > 0 })
        {
            // 保存・読込 — An enabled SearchCast rule is current explicit user intent, so it is useful immediately.
            // Persisted contributor weights then let the same entity/role generalize beyond one Work.
            var currentTracked = false;
            foreach (var key in contributorKeys)
            {
                if (key.StartsWith("tracked|", StringComparison.OrdinalIgnoreCase))
                {
                    currentTracked = true;
                    break;
                }
            }
            var learned = AverageTopThreePositiveContributorWeights(
                contributorKeys,
                model.ContributorWeights,
                context.MaxContributorWeight);
            if (currentTracked || learned > 0.0d)
                contributorAffinity = Math.Clamp(Math.Max(currentTracked ? 1.0d : 0.0d, learned), 0.0d, 1.0d);
        }

        // 作品識別 — Work and Contributor form one correlated identity family in learned affinity.
        return new AIrhythmLearnedAffinityBreakdown(
            workAffinity,
            contributorAffinity,
            genreAffinity,
            hasGenre,
            termAffinity,
            hasTerms,
            serviceAffinity,
            hourAffinity);
    }

    // 処理条件 — Identical to reasons.Distinct().Take(4).ToArray(): preserve insertion order and
    // EqualityComparer<string>.Default semantics without per-candidate LINQ iterators/HashSet. Compare at
    // most four accepted reasons, retaining the first occurrence.
    private static string[] TakeFirstFourDistinctReasons(List<string> reasons)
    {
        if (reasons.Count == 0)
            return Array.Empty<string>();

        string? first = null, second = null, third = null, fourth = null;
        var count = 0;
        foreach (var reason in reasons)
        {
            if (count > 0 && EqualityComparer<string>.Default.Equals(first, reason)) continue;
            if (count > 1 && EqualityComparer<string>.Default.Equals(second, reason)) continue;
            if (count > 2 && EqualityComparer<string>.Default.Equals(third, reason)) continue;
            if (count > 3 && EqualityComparer<string>.Default.Equals(fourth, reason)) continue;
            switch (count)
            {
                case 0: first = reason; break;
                case 1: second = reason; break;
                case 2: third = reason; break;
                case 3: fourth = reason; break;
            }
            if (++count == 4) break;
        }
        var result = new string[count];
        if (count > 0) result[0] = first!;
        if (count > 1) result[1] = second!;
        if (count > 2) result[2] = third!;
        if (count > 3) result[3] = fourth!;
        return result;
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
        // 録画実績・復旧 — Parenthesized episode candidates keep their canonical evidence key unchanged. A separate
        // local-series bridge is populated from history/reservations and is consulted only when the current EPG
        // supplies corroborating neighboring numbers. This avoids globally collapsing titles such as work years
        // or numbered installments.
        var numericServiceHistoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var numericServiceReservationWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var numericServiceAutomatedReservationWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var numericLocalValuesByService = identityContext.NumericLocalValuesByService;
        var localWorkAliases = identityContext.WorkAliases;
        var ruleIndices = BuildScoringRuleIndices(snapshot.KeywordRules);
        var rulesById = ruleIndices.EnabledById;
        var reservationById = BuildLatestReservationIndex(snapshot.ReservationRecords);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreSetupAfterIndices = GC.GetAllocatedBytesForCurrentThread();
#endif
        var localEvidenceState = BuildLocalEvidenceState(snapshot, identityContext, rulesById, reservationById);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreSetupAfterLocalEvidence = GC.GetAllocatedBytesForCurrentThread();
        var localEvidenceShadowRows = new List<AIrhythmLocalEvidenceShadowRow>();
#endif
        var parenthesizedWorkSequenceCandidates = 0;
        var parenthesizedWorkHistoryBridges = 0;
        var parenthesizedWorkReservationBridges = 0;
        var numericHistoryBridgeSamples = new List<string>();
        var numericReservationBridgeSamples = new List<string>();
        var historyEvidence = GetHistoryEvidence(snapshot.History, identityContext);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreSetupAfterHistory = GC.GetAllocatedBytesForCurrentThread();
#endif
        var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var genreTotalCount = 0;
        var interestSignals = AIrhythmDataState.GetInterestSignals();
        var now = DateTimeOffset.Now;
        var learnedUserModel = BuildOrUpdateLearnedUserModel(snapshot, identityContext, rulesById, ruleIndices.SearchCastById);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreSetupAfterLearnedModel = GC.GetAllocatedBytesForCurrentThread();
#endif
        var learnedAffinityContext = BuildLearnedAffinityContext(learnedUserModel);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        // 保存・読込 — Same snapshot / same learned model, with ONLY the contributor weight projection reverted.
        var baselineLearnedAffinityContext = BuildLearnedAffinityContext(learnedUserModel with
        {
            ContributorWeights = learnedUserModel.PreCalibrationContributorWeights ?? learnedUserModel.ContributorWeights
        });
        var contributorComparisonAffinities = new Dictionary<AIrhythmEventIdentity, double>();
        var contributorExplicitIntentsPreserved = 0;
#endif
        var activeSearchCastRules = snapshot.KeywordRules
            .Where(rule => rule.Enabled && rule.SearchCast)
            .ToArray();
        // 保存・読込 — The service is part of this key: identical EPG text on different services may match different
        // SearchCast rules. Cache lives for this Score call only.
        var contributorKeysByEventContent = new Dictionary<ContributorEventCacheKey, IReadOnlyList<string>>();
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.ReportEvidenceCalibrationMemory();
#endif
        var learnedUserModelMaturity = learnedUserModel.ObservationCount <= 0
            ? 0.0d
            : learnedUserModel.ObservationCount / (learnedUserModel.ObservationCount + 100.0d);
        // 作品識別 — Interest signals are capped at 24. Normalize the stable signal side once per Score run and
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
        // 録画実績・復旧 — Reservation -> successful recording is one causal programme occurrence for preference
        // accumulation. Retain the facts in both stores, but count the preference point once.
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreBehaviorBeforeHistory = GC.GetAllocatedBytesForCurrentThread();
#endif
        var seenBehaviorCausalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var historyIndex = 0; historyIndex < historyEvidence.Count; historyIndex++)
        {
            var item = historyEvidence[historyIndex];
            var recording = snapshot.History[historyIndex];
            var axes = BehaviorEvidenceAxesForRecording(recording, reservationById, rulesById);

            var causalKey = item.CausalIdentity;
            if (!axes.HasBaseAxis || !seenBehaviorCausalKeys.Add(causalKey))
                continue;

            // 処理条件 — Stored facts never decay or turn negative. Each distinct recorded programme contributes one
            // point to every observable positive axis.
            const double weight = 1.0d;
            if (axes.Service && item.ServiceIdentity.IsValid) Add(serviceWeights, item.ServiceIdentity, weight);
            if (axes.Hour) Add(hourWeights, item.Hour, weight);
            if (axes.Terms) foreach (var token in item.Terms) Add(termWeights, token, weight);
            if (axes.Work)
            {
                Increment(seriesHistoryCounts, item.SeriesKey);
                if (TryGetNumericParenthesizedServiceEvidenceKey(item.ProgramTitle, item.ServiceIdentity, out var historyNumericServiceKey))
                    Increment(numericServiceHistoryCounts, historyNumericServiceKey);
            }
            if (axes.Genre && !string.IsNullOrWhiteSpace(item.GenreKey))
            {
                genreCounts[item.GenreKey] = genreCounts.TryGetValue(item.GenreKey, out var count) ? count + 1 : 1;
                genreTotalCount++;
            }
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreBehaviorAfterHistory = GC.GetAllocatedBytesForCurrentThread();
#endif
        foreach (var item in snapshot.Reservations)
        {
            var facts = CanonicalEvidenceFacts(item, identityContext);
            var reservationWeight = ReservationEvidenceWeight(item);
            if (reservationWeight <= 0) continue;
            var axes = BehaviorEvidenceAxesForReservation(item, rulesById);
            var causalKey = AIrhythmDataState.ReservationEvidenceCausalIdentity(item);
            if (!seenBehaviorCausalKeys.Add(causalKey))
                continue;

            var reservationService = facts.ServiceIdentity;
            if (axes.Service && reservationService.IsValid) Add(serviceWeights, reservationService, 1.0 * reservationWeight);
            if (axes.Hour) Add(hourWeights, facts.Hour, 1.0 * reservationWeight);
            if (axes.Terms) foreach (var token in facts.Terms) Add(termWeights, token, 1.0 * reservationWeight);
            var reservationSeriesKey = facts.WorkKey;
            if (axes.Work && reservationSeriesKey.Length > 0)
            {
                Add(seriesReservationWeights, reservationSeriesKey, reservationWeight);
                if (item.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule)
                    Add(seriesAutomatedReservationWeights, reservationSeriesKey, reservationWeight);
            }
            if (axes.Work && TryGetNumericParenthesizedServiceEvidenceKey(item.ProgramTitle, reservationService, out var reservationNumericServiceKey))
            {
                Add(numericServiceReservationWeights, reservationNumericServiceKey, reservationWeight);
                if (item.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule)
                    Add(numericServiceAutomatedReservationWeights, reservationNumericServiceKey, reservationWeight);
            }
            if (axes.Genre && !string.IsNullOrWhiteSpace(facts.GenreKey))
            {
                genreCounts[facts.GenreKey] = genreCounts.TryGetValue(facts.GenreKey, out var count) ? count + 1 : 1;
                genreTotalCount++;
            }
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreSetupAfterBehaviorWeights = GC.GetAllocatedBytesForCurrentThread();
#endif
        var genreTotal = Math.Max(1, genreTotalCount);
        foreach (var tuner in snapshot.Tuners.Where(x =>
            x.IsInUse && string.Equals(x.UsageKind, "Viewing", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryResolveCurrentServiceIdentity(snapshot.Channels, tuner.ServiceName, out var viewingService))
                Add(serviceWeights, viewingService, 1.0);
            foreach (var token in Tokens(AiredTitleEvidenceText(tuner.ProgramTitle))) Add(termWeights, token, 1.0);
        }

        // 推薦・学習 — Term evidence is a Boolean substring match with a weighted top-three. Group by the first
        // UTF-16 unit to avoid an all-terms scan for every programme. The final Contains remains the canonical
        // decision owner.
        var termPrefixIndex = new Dictionary<char, List<KeyValuePair<string, double>>>();
        var termUnicodeFallback = new List<KeyValuePair<string, double>>();
        // 作品識別 — Reuse the working set for the entire score pass; never allocate a HashSet for each of the
        // thousands of programme candidates.
        var candidateTermPrefixes = new HashSet<char>();
        foreach (var entry in termWeights)
        {
            if (entry.Key.Length < 2)
                continue;
            var first = entry.Key[0];
            var upper = char.ToUpperInvariant(first);
            var lower = char.ToLowerInvariant(first);
            var asciiLetter = first is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
            if (!asciiLetter && (upper != first || lower != first))
            {
                termUnicodeFallback.Add(entry);
                continue;
            }
            if (!termPrefixIndex.TryGetValue(upper, out var bucket))
                termPrefixIndex[upper] = bucket = new List<KeyValuePair<string, double>>();
            bucket.Add(entry);
        }

        var preferred = Words(snapshot.Settings.Preferred).ToArray();
        var excluded = Words(snapshot.Settings.Excluded).ToArray();
        var channelMap = new Dictionary<AIrhythmServiceIdentity, TvAirServiceDto>();
        foreach (var channel in snapshot.Channels)
        {
            var identity = ServiceIdentityOf(channel);
            if (!identity.IsValid)
                continue;
            if (!channelMap.TryGetValue(identity, out var current) || channel.DisplayOrder < current.DisplayOrder)
                channelMap[identity] = channel;
        }
        var reserved = new HashSet<AIrhythmReservedWorkKey>();
        foreach (var reservation in snapshot.Reservations)
            reserved.Add(new AIrhythmReservedWorkKey(CanonicalWorkKey(reservation, identityContext), ServiceIdentityOf(reservation)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreAllocatedAfterSetup = GC.GetAllocatedBytesForCurrentThread();
        // 録画実績・復旧 — Candidate-only canonical breakdown: exclude setup/history/reservation canonicalization.
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
        long candidateLocalDecisionBytes = 0;
        long candidateContributorExtractionBytes = 0;
        long candidateLearnedAffinityBytes = 0;
        long candidateSemanticDiagnosticsBytes = 0;
        long candidateRecommendationBytes = 0;
        var candidateLoopVisited = 0;
        var candidateLoopExcluded = 0;
        var candidateLoopDeduped = 0;
        var candidateLoopBelowMinimum = 0;
        var evidenceVectorCheckCount = 0;
        var evidenceVectorMaxDelta = 0.0d;
        long evidenceVectorAllocatedBytes = 0;
        double semanticIdentityBaseTotal = 0.0d;
        double semanticContentBaseTotal = 0.0d;
        double semanticContextBaseTotal = 0.0d;
        double semanticExplicitBaseTotal = 0.0d;
        double semanticLearnedIdentityTotal = 0.0d;
        double normalizedIdentityStrengthTotal = 0.0d;
        double normalizedIdentityConfidenceTotal = 0.0d;
        double normalizedContentStrengthTotal = 0.0d;
        double normalizedContentConfidenceTotal = 0.0d;
        double normalizedContextStrengthTotal = 0.0d;
        double normalizedContextConfidenceTotal = 0.0d;
        double normalizedExplicitStrengthTotal = 0.0d;
        double normalizedExplicitConfidenceTotal = 0.0d;
        var normalizedIdentityPresent = 0;
        var normalizedContentPresent = 0;
        var normalizedContextPresent = 0;
        var normalizedExplicitPresent = 0;
        var provenanceWorkHistory = 0;
        var provenanceWorkReservation = 0;
        var provenanceContributorSearchCast = 0;
        var provenanceContributorMetadata = 0;
        var provenanceGenreHistory = 0;
        var provenanceTermHistory = 0;
        var provenanceServiceHistory = 0;
        var provenanceHourHistory = 0;
        var provenanceInterestSelection = 0;
        var provenancePreferredSetting = 0;
        var semanticContributorHitCandidates = 0;
        var semanticEvidenceCount = 0;
        var calibrationGroupsByEvent = new Dictionary<AIrhythmCalibrationEventKey, AIrhythmNormalizedEvidenceGroups>();
#endif

        foreach (var item in snapshot.Events)
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseStart = GC.GetAllocatedBytesForCurrentThread();
            candidateLoopVisited++;
#endif
            // 作品識別 — An EPG placeholder has no broadcast Work to recommend. Gate it before canonical evidence
            // evaluation so it cannot acquire a fictitious new-Work identity, and avoid per-candidate
            // normalization/regex allocations. The title must consist entirely of a placeholder status: never reject
            // real programmes merely because their title contains similar words.
            if (IsUnspecifiedEpgTitle(item.Title))
            {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                candidateLoopExcluded++;
#endif
                continue;
            }
            var facts = CanonicalEvidenceFacts(item, identityContext);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterCanonicalFacts = GC.GetAllocatedBytesForCurrentThread();
            candidateCanonicalFactsBytes += candidatePhaseAfterCanonicalFacts - candidatePhaseStart;
#endif
            // 作品識別 — Recommendation identity is the single owner of score admission and duplicate selection. Score()
            // always kept the earliest non-excluded occurrence for a Work+service (or the exact event fallback); all
            // subsequent occurrences were discarded *after* expensive evaluation. Look up only here, but never claim
            // the key until the exclusion gate accepts the candidate. An excluded earlier event must never suppress
            // a later admissible event sharing the same Work+service identity.
            var seriesKey = facts.WorkKey;
            var itemService = ServiceIdentityOf(item);
            var duplicateKey = seriesKey.Length > 0
                ? $"{seriesKey}|{itemService}"
                : $"event:{item.NetworkId}:{item.TransportStreamId}:{item.ServiceId}:{item.EventNumber}:{item.Start.UtcDateTime.Ticks}";
            if (seen.Contains(duplicateKey))
            {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                var candidatePhaseAfterDuplicate = GC.GetAllocatedBytesForCurrentThread();
                candidateCanonicalTextFilterBytes += candidatePhaseAfterCanonicalFacts - candidatePhaseStart;
                candidateDedupeBytes += candidatePhaseAfterDuplicate - candidatePhaseAfterCanonicalFacts;
                candidateLocalExternalBytes += candidatePhaseAfterDuplicate - candidatePhaseAfterCanonicalFacts;
                candidateLoopDeduped++;
#endif
                continue;
            }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterDuplicateCheck = GC.GetAllocatedBytesForCurrentThread();
            candidateDedupeBytes += candidatePhaseAfterDuplicateCheck - candidatePhaseAfterCanonicalFacts;
            candidateLocalExternalBytes += candidatePhaseAfterDuplicateCheck - candidatePhaseAfterCanonicalFacts;
#endif
            // 文字列解析 — All consumers below already use OrdinalIgnoreCase. Avoid creating a second lower-cased copy of
            // the complete title/summary/detail/genre text for every EPG candidate. This combined text is shared by
            // excluded-term, learned-term, and preferred-term matching.
            var hay = $"{item.Title} {item.Summary} {item.Detail} {item.Genre}";
            var excludedMatch = excluded.Any(x => hay.Contains(x, StringComparison.OrdinalIgnoreCase));
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterTextBuild = GC.GetAllocatedBytesForCurrentThread();
            candidateTextBuildBytes += candidatePhaseAfterTextBuild - candidatePhaseAfterCanonicalFacts;
#endif
            if (excludedMatch)
            {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
                var candidatePhaseAfterExcludedFilter = GC.GetAllocatedBytesForCurrentThread();
                candidateExcludedFilterBytes += candidatePhaseAfterExcludedFilter - candidatePhaseAfterTextBuild;
                candidateCanonicalTextFilterBytes += candidatePhaseAfterCanonicalFacts - candidatePhaseStart
                    + candidatePhaseAfterExcludedFilter - candidatePhaseAfterDuplicateCheck;
                candidateLoopExcluded++;
#endif
                continue;
            }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterCanonicalText = GC.GetAllocatedBytesForCurrentThread();
            candidateExcludedFilterBytes += candidatePhaseAfterCanonicalText - candidatePhaseAfterTextBuild;
            candidateCanonicalTextFilterBytes += candidatePhaseAfterCanonicalFacts - candidatePhaseStart
                + candidatePhaseAfterCanonicalText - candidatePhaseAfterDuplicateCheck;
#endif

            // 推薦・学習 — Claim only after the exclusion gate. No other place in this scoring pass mutates the seen set,
            // so the first admissible occurrence wins.
            seen.Add(duplicateKey);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterAdmission = GC.GetAllocatedBytesForCurrentThread();
            candidateDedupeBytes += candidatePhaseAfterAdmission - candidatePhaseAfterCanonicalText;
            candidateLocalExternalBytes += candidatePhaseAfterAdmission - candidatePhaseAfterCanonicalText;
#endif
            var score = 10.0;
            var reasons = new List<string>();
            var evidenceSeriesKey = seriesKey;
            var historySeriesCount = FindSeriesEvidence(seriesHistoryCounts, evidenceSeriesKey);
            var reservationSeriesWeight = FindSeriesEvidence(seriesReservationWeights, evidenceSeriesKey);
            var automatedReservationSeriesWeight = FindSeriesEvidence(seriesAutomatedReservationWeights, evidenceSeriesKey);
            var parenthesizedWorkSequence = IsCorroboratedParenthesizedWorkSequence(item, numericLocalValuesByService);
            var usedNumericHistoryBridge = false;
            var usedNumericReservationBridge = false;
            if (parenthesizedWorkSequence
                && TryGetNumericParenthesizedStemEvidenceKey(item.Title, out var numericStemKey)
                && TryGetNumericParenthesizedServiceEvidenceKey(item.Title, ServiceIdentityOf(item), out var numericServiceKey))
            {
                parenthesizedWorkSequenceCandidates++;
                var numericHistoryCount = FindSeriesEvidence(numericServiceHistoryCounts, numericServiceKey);
                if (numericHistoryCount > historySeriesCount)
                {
                    historySeriesCount = numericHistoryCount;
                    usedNumericHistoryBridge = numericHistoryCount > 0;
                    if (usedNumericHistoryBridge)
                    {
                        parenthesizedWorkHistoryBridges++;
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
                        parenthesizedWorkReservationBridges++;
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

            // 処理条件 — Retain the existing OrdinalIgnoreCase substring semantics and top-3-by-weight contract, but
            // avoid a Where -> OrderByDescending -> Take -> ToArray allocation chain for every candidate.
            var matchedTermCount = 0;
            var topTermWeight1 = double.NegativeInfinity;
            var topTermWeight2 = double.NegativeInfinity;
            var topTermWeight3 = double.NegativeInfinity;
            void AccountMatchingTerm(KeyValuePair<string, double> termWeight)
            {
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

            // 処理条件 — A non-ASCII cased character can have OrdinalIgnoreCase aliases different from an invariant
            // single-char fold. In that case retain the original exhaustive comparison, including its Unicode rules.
            var unusualCase = false;
            foreach (var c in hay)
            {
                if (c > 127 && (char.ToUpperInvariant(c) != c || char.ToLowerInvariant(c) != c))
                {
                    unusualCase = true;
                    break;
                }
            }
            if (unusualCase)
            {
                foreach (var termWeight in termWeights)
                    if (termWeight.Key.Length >= 2 && hay.Contains(termWeight.Key, StringComparison.OrdinalIgnoreCase))
                        AccountMatchingTerm(termWeight);
            }
            else
            {
                candidateTermPrefixes.Clear();
                foreach (var c in hay)
                    candidateTermPrefixes.Add(char.ToUpperInvariant(c));
                foreach (var initial in candidateTermPrefixes)
                    if (termPrefixIndex.TryGetValue(initial, out var bucket))
                        foreach (var termWeight in bucket)
                            if (hay.Contains(termWeight.Key, StringComparison.OrdinalIgnoreCase))
                                AccountMatchingTerm(termWeight);
                foreach (var termWeight in termUnicodeFallback)
                    if (hay.Contains(termWeight.Key, StringComparison.OrdinalIgnoreCase))
                        AccountMatchingTerm(termWeight);
            }

            // 録画実績・復旧 — Series identity and title-term similarity are two representations of the same title
            // evidence. When a direct Work/series identity match already exists, Terms become a fallback instead of
            // a second additive vote from the same recording/reservation action. This preserves Terms for discovery
            // across different Works while preventing one event from multiplying its own identity.
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

            var serviceWeight = 0.0d;
            if (itemService.IsValid && serviceWeights.TryGetValue(itemService, out serviceWeight))
            {
                serviceComponentScore = Math.Min(6, Math.Log2(serviceWeight + 1) * 2.4);
                score += serviceComponentScore;
                if (serviceWeight >= 5) reasons.Add("よく録る放送局");
            }
            var hourWeight = 0.0d;
            if (hourWeights.TryGetValue(facts.Hour, out hourWeight))
            {
                hourComponentScore = Math.Min(5, Math.Log2(hourWeight + 1) * 1.8);
                score += hourComponentScore;
                if (hourWeight >= 5) reasons.Add("よく録る時間帯");
            }

            var exactInterest = false;
            var interestScore = 0.0;
            foreach (var signal in preparedInterestSignals)
            {
                // 録画実績・復旧 — 「気になる」も録画・予約と同じ強系列キーの完全一致だけを強い証拠にする。 部分一致は派生番組やコンテナ枠へ本編嗜好を借用させるため、強証拠には使わない。
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
            if (reserved.Contains(new AIrhythmReservedWorkKey(seriesKey, itemService)))
            {
                // 録画実績・復旧 — 予約済みは候補の状態であり、ユーザー嗜好そのものを弱める根拠ではない。 表示スコアは録画・予約・利用傾向との適合度を示すため、予約状態による減点は行わない。
                reasons.Add("予約済み");
            }

            // 作品識別 — Retain each factual evidence axis intact, but expose it through semantic families: Identity,
            // Content, Context and ExplicitBehavior. Contributor joins Identity in the learned-affinity projection
            // below; the established base-score arithmetic stays exact. The vector remains hot-loop local so future
            // calibration does not enlarge retained rows.
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var evidenceVectorAllocationStart = GC.GetAllocatedBytesForCurrentThread();
#endif
            var evidenceVector = new AIrhythmEvidenceVector(
                genreComponentScore,
                seriesComponentScore,
                termComponentScore,
                serviceComponentScore,
                hourComponentScore,
                interestComponentScore,
                preferredComponentScore);
            var reconstructedScore = 10.0d + evidenceVector.InternalStrength;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            evidenceVectorAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - evidenceVectorAllocationStart;
            var evidenceVectorDelta = Math.Abs(reconstructedScore - score);
            if (evidenceVectorDelta > evidenceVectorMaxDelta) evidenceVectorMaxDelta = evidenceVectorDelta;
            evidenceVectorCheckCount++;
#endif
            score = reconstructedScore;

            // 処理条件 — 放送までの近さは番組への嗜好ではない。嗜好スコアには混ぜず、必要な棚・検索側で時刻条件として扱う。

            // 録画実績・復旧 — 録画実績がないジャンルは、別の強い根拠がない限り高得点にしない。 発見枠へ出すことと嗜好スコアは分離し、未知ジャンルの点数を偽装しない。
            var confidenceCap = 98;
            if (genreCount == 0 && !hasStrongSeriesEvidence && !preferredHit && !exactInterest)
                confidenceCap = 44;
            else if (genreCount <= 1 && genreShare < 0.02 && !hasStrongSeriesEvidence && !preferredHit)
                confidenceCap = 55;
            else if (genreShare < 0.05 && historySeriesCount == 0 && !preferredHit)
                confidenceCap = 72;

            var contentEvidence = 0;
            if (genreShare >= 0.12) contentEvidence++;
            if (matchedTermCount > 0) contentEvidence++;
            if (interestScore > 0) contentEvidence++;
            var contextualEvidence = 0;
            if (itemService.IsValid && serviceWeights.TryGetValue(itemService, out var trustServiceWeight) && trustServiceWeight >= 5) contextualEvidence++;
            if (hourWeights.TryGetValue(facts.Hour, out var trustHourWeight) && trustHourWeight >= 5) contextualEvidence++;

            // 処理条件 — 局・時間帯は「それらしい」補助材料にはなるが、それだけで納得候補にはしない。 番組内容側の根拠が複数ある、または内容根拠に利用傾向が重なる場合だけ納得側へ寄せる。
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

            // 推薦・学習 — 偏差表示も既存の信頼度上限を共有し、丸めだけを行わない。 これにより推薦判定と表示スコアで評価基準が分岐しない。
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
                localEvidenceState.ContinuitySources,
                identityContext,
                facts);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterLocalContinuity = GC.GetAllocatedBytesForCurrentThread();
            candidateLocalContinuityBytes += candidatePhaseAfterLocalContinuity - candidatePhaseAfterContextInterest;
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
            var externalEvidenceProjection = AIrhythmDataState.GetExternalEvidenceProjection(item);
            var externalEvidenceSummary = externalEvidenceProjection.Item1;
            var externalEvidenceEvaluationFlags = ToExternalEvidenceEvaluationFlags(externalEvidenceSummary);
            var externalEvidenceConfidenceGate = ToExternalEvidenceConfidenceGate(externalEvidenceEvaluationFlags);
            var externalEvidenceAdjustmentCandidate = ToExternalEvidenceAdjustmentCandidate(externalEvidenceConfidenceGate);
            var externalEvidenceAdjustmentKind = NormalizeExternalEvidenceAdjustmentKind(externalEvidenceAdjustmentCandidate, externalEvidenceProjection.Item2);
            var externalEvidenceAdjustmentStrength = ToExternalEvidenceAdjustmentStrength(externalEvidenceAdjustmentCandidate, externalEvidenceAdjustmentKind);
            var externalEvidenceSupportingVerdictReason = externalEvidenceAdjustmentCandidate == AIrhythmExternalEvidenceAdjustmentCandidate.Eligible
                ? externalEvidenceProjection.Item3
                : AIrhythmExternalEvidenceVerdictReason.NotApplicable;
            // 外部情報 — External evidence affects scoring only when supported, allowed, and eligible.
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
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidateBeforeContributorExtraction = GC.GetAllocatedBytesForCurrentThread();
#endif
            var contributorKeys = ContributorKeysForEvent(item, activeSearchCastRules, contributorKeysByEventContent);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidateAfterContributorExtraction = GC.GetAllocatedBytesForCurrentThread();
#endif
            var learnedAffinity = LearnedUserAffinityBreakdown(learnedAffinityContext, facts, contributorKeys);
            var learnedUserAffinity = learnedAffinity.Overall;
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidateAfterLearnedAffinity = GC.GetAllocatedBytesForCurrentThread();
#endif
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var baselineBreakdown = LearnedUserAffinityBreakdown(baselineLearnedAffinityContext, facts, contributorKeys);
            contributorComparisonAffinities[identity] = baselineBreakdown.Overall;
            if (contributorKeys.Any(key => key.StartsWith("tracked|", StringComparison.OrdinalIgnoreCase))
                && Math.Abs(baselineBreakdown.Contributor - learnedAffinity.Contributor) < 0.0000001d)
                contributorExplicitIntentsPreserved++;
            var identityProvenance = AIrhythmEvidenceProvenance.None;
            if (historySeriesCount > 0) identityProvenance |= AIrhythmEvidenceProvenance.WorkHistory;
            if (reservationSeriesWeight > 0) identityProvenance |= AIrhythmEvidenceProvenance.WorkReservation;
            if (learnedAffinity.Contributor > 0.0d && contributorKeys.Any(x => x.StartsWith("tracked|", StringComparison.OrdinalIgnoreCase)))
                identityProvenance |= AIrhythmEvidenceProvenance.ContributorSearchCast;
            if (learnedAffinity.Contributor > 0.0d && contributorKeys.Any(x => !x.StartsWith("tracked|", StringComparison.OrdinalIgnoreCase)))
                identityProvenance |= AIrhythmEvidenceProvenance.ContributorMetadata;

            var contentProvenance = AIrhythmEvidenceProvenance.None;
            if (genreCount > 0) contentProvenance |= AIrhythmEvidenceProvenance.GenreHistory;
            if (termComponentScore > 0.0d) contentProvenance |= AIrhythmEvidenceProvenance.TermHistory;

            var contextProvenance = AIrhythmEvidenceProvenance.None;
            if (serviceComponentScore > 0.0d) contextProvenance |= AIrhythmEvidenceProvenance.ServiceHistory;
            if (hourComponentScore > 0.0d) contextProvenance |= AIrhythmEvidenceProvenance.HourHistory;

            var explicitProvenance = AIrhythmEvidenceProvenance.None;
            if (interestComponentScore > 0.0d) explicitProvenance |= AIrhythmEvidenceProvenance.InterestSelection;
            if (preferredHit) explicitProvenance |= AIrhythmEvidenceProvenance.PreferredSetting;

            var normalizedIdentityStrength = Math.Clamp(Math.Max(evidenceVector.IdentityStrength / 24.0d, learnedAffinity.Identity), 0.0d, 1.0d);
            var workSupportConfidence = Math.Clamp(Math.Max(historySeriesCount / 3.0d, reservationSeriesWeight), 0.0d, 1.0d);
            var contributorSupportConfidence = learnedAffinity.Contributor > 0.0d
                ? (identityProvenance.HasFlag(AIrhythmEvidenceProvenance.ContributorSearchCast) ? 1.0d : learnedUserModelMaturity)
                : 0.0d;
            var normalizedIdentityConfidence = Math.Clamp(Math.Max(workSupportConfidence, contributorSupportConfidence), 0.0d, 1.0d);

            var normalizedContentStrength = Math.Clamp(evidenceVector.ContentStrength / 32.0d, 0.0d, 1.0d);
            var genreSupportConfidence = Math.Clamp(genreCount / 6.0d, 0.0d, 1.0d);
            var termSupportConfidence = Math.Clamp(matchedTermCount / 3.0d, 0.0d, 1.0d);
            var normalizedContentConfidence = Math.Clamp(Math.Max(genreSupportConfidence, termSupportConfidence), 0.0d, 1.0d);

            var normalizedContextStrength = Math.Clamp(evidenceVector.ContextStrength / 11.0d, 0.0d, 1.0d);
            var serviceSupportConfidence = Math.Clamp(serviceWeight / 5.0d, 0.0d, 1.0d);
            var hourSupportConfidence = Math.Clamp(hourWeight / 5.0d, 0.0d, 1.0d);
            var normalizedContextConfidence = Math.Clamp(Math.Max(serviceSupportConfidence, hourSupportConfidence), 0.0d, 1.0d);

            var normalizedExplicitStrength = Math.Clamp(evidenceVector.ExplicitBehaviorStrength / 30.0d, 0.0d, 1.0d);
            var interestSupportConfidence = Math.Clamp(interestScore / 14.0d, 0.0d, 1.0d);
            var normalizedExplicitConfidence = preferredHit ? 1.0d : interestSupportConfidence;

            var normalizedGroups = new AIrhythmNormalizedEvidenceGroups(
                normalizedIdentityStrength, normalizedIdentityConfidence, identityProvenance,
                normalizedContentStrength, normalizedContentConfidence, contentProvenance,
                normalizedContextStrength, normalizedContextConfidence, contextProvenance,
                normalizedExplicitStrength, normalizedExplicitConfidence, explicitProvenance);

            normalizedIdentityStrengthTotal += normalizedGroups.IdentityStrength;
            normalizedIdentityConfidenceTotal += normalizedGroups.IdentityConfidence;
            normalizedContentStrengthTotal += normalizedGroups.ContentStrength;
            normalizedContentConfidenceTotal += normalizedGroups.ContentConfidence;
            normalizedContextStrengthTotal += normalizedGroups.ContextStrength;
            normalizedContextConfidenceTotal += normalizedGroups.ContextConfidence;
            normalizedExplicitStrengthTotal += normalizedGroups.ExplicitStrength;
            normalizedExplicitConfidenceTotal += normalizedGroups.ExplicitConfidence;
            if (normalizedGroups.IdentityStrength > 0.0d) normalizedIdentityPresent++;
            if (normalizedGroups.ContentStrength > 0.0d) normalizedContentPresent++;
            if (normalizedGroups.ContextStrength > 0.0d) normalizedContextPresent++;
            if (normalizedGroups.ExplicitStrength > 0.0d) normalizedExplicitPresent++;
            if (identityProvenance.HasFlag(AIrhythmEvidenceProvenance.WorkHistory)) provenanceWorkHistory++;
            if (identityProvenance.HasFlag(AIrhythmEvidenceProvenance.WorkReservation)) provenanceWorkReservation++;
            if (identityProvenance.HasFlag(AIrhythmEvidenceProvenance.ContributorSearchCast)) provenanceContributorSearchCast++;
            if (identityProvenance.HasFlag(AIrhythmEvidenceProvenance.ContributorMetadata)) provenanceContributorMetadata++;
            if (contentProvenance.HasFlag(AIrhythmEvidenceProvenance.GenreHistory)) provenanceGenreHistory++;
            if (contentProvenance.HasFlag(AIrhythmEvidenceProvenance.TermHistory)) provenanceTermHistory++;
            if (contextProvenance.HasFlag(AIrhythmEvidenceProvenance.ServiceHistory)) provenanceServiceHistory++;
            if (contextProvenance.HasFlag(AIrhythmEvidenceProvenance.HourHistory)) provenanceHourHistory++;
            if (explicitProvenance.HasFlag(AIrhythmEvidenceProvenance.InterestSelection)) provenanceInterestSelection++;
            if (explicitProvenance.HasFlag(AIrhythmEvidenceProvenance.PreferredSetting)) provenancePreferredSetting++;

            semanticIdentityBaseTotal += evidenceVector.IdentityStrength;
            semanticContentBaseTotal += evidenceVector.ContentStrength;
            semanticContextBaseTotal += evidenceVector.ContextStrength;
            semanticExplicitBaseTotal += evidenceVector.ExplicitBehaviorStrength;
            semanticLearnedIdentityTotal += learnedAffinity.Identity;
            if (learnedAffinity.Contributor > 0.0d) semanticContributorHitCandidates++;
            semanticEvidenceCount++;
            calibrationGroupsByEvent[CalibrationEventKey(item.NetworkId, item.TransportStreamId, item.ServiceId, item.EventNumber, item.Start)] = normalizedGroups;
#endif
            var externalEvidenceShadowAdjustmentValue = externalEvidenceAdjustmentValue; // 処理条件 — retained for developer telemetry compatibility
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            var candidatePhaseAfterLocalExternal = GC.GetAllocatedBytesForCurrentThread();
            candidateLocalDecisionBytes += candidateBeforeContributorExtraction - candidatePhaseAfterExternalProjection;
            candidateContributorExtractionBytes += candidateAfterContributorExtraction - candidateBeforeContributorExtraction;
            candidateLearnedAffinityBytes += candidateAfterLearnedAffinity - candidateAfterContributorExtraction;
            candidateSemanticDiagnosticsBytes += candidatePhaseAfterLocalExternal - candidateAfterLearnedAffinity;
            candidateLocalAggregateAdjustmentBytes += candidatePhaseAfterLocalExternal - candidatePhaseAfterExternalProjection;
            candidateLocalExternalBytes += candidatePhaseAfterLocalExternal - candidatePhaseAfterContextInterest;
#endif
            scored.Add(new(item.Title, item.ServiceName, broadcast, item.Genre ?? string.Empty, item.Start, rawScore, TakeFirstFourDistinctReasons(reasons), seriesKey, identity, isConvincing, isPlausibleDiscovery, rawScore, adjustedDeviationRawScore, externalEvidenceSummary, externalEvidenceEvaluationFlags, externalEvidenceConfidenceGate, externalEvidenceAdjustmentCandidate, externalEvidenceAdjustmentKind, externalEvidenceAdjustmentStrength, externalEvidenceSupportingVerdictReason, externalEvidenceShadowAdjustmentValue, externalEvidenceAdjustmentValue, learnedUserAffinity, 0.0d, learnedUserModelMaturity));
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            candidateRecommendationBytes += GC.GetAllocatedBytesForCurrentThread() - candidatePhaseAfterLocalExternal;
#endif
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var scoreAllocatedAfterCandidateLoop = GC.GetAllocatedBytesForCurrentThread();
#endif

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        // 推薦・学習 — Retain candidate-level Shadow diagnostics bounded. Recommendation scoring can run often and
        // the EPG candidate population can be large, so logging every row would make diagnostics scale with both
        // candidate count and refresh count. Summary statistics still cover the complete population; the
        // per-candidate logs are a deterministic evidence-focused sample.
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

        AIrhythmDataState.WriteDeveloperLog($"local parenthesized work evidence candidates={parenthesizedWorkSequenceCandidates} historyBridges={parenthesizedWorkHistoryBridges} reservationBridges={parenthesizedWorkReservationBridges} historySamples={numericHistoryBridgeSamples.Count} reservationSamples={numericReservationBridgeSamples.Count} policy=epg_sequence_work_only_user_evidence_neighbor_allows_episode");
        foreach (var sample in numericHistoryBridgeSamples)
            AIrhythmDataState.WriteDeveloperLog($"local parenthesized work history bridge {sample}");
        foreach (var sample in numericReservationBridgeSamples)
            AIrhythmDataState.WriteDeveloperLog($"local parenthesized work reservation bridge {sample}");
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
        var learnedAffinityMean = scored.Count == 0 ? 0.0d : scored.Average(x => x.LearnedUserAffinity);
        var learnedAffinityVariance = scored.Count == 0
            ? 0.0d
            : scored.Average(x => Math.Pow(x.LearnedUserAffinity - learnedAffinityMean, 2.0d));
        var learnedAffinityStd = Math.Sqrt(Math.Max(0.0d, learnedAffinityVariance));
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        // 推薦・学習 — Both sides use the final deduplicated, accepted candidates and their respective population
        // z-score. Ranking is over learned-adjusted deviation coordinates only; final shelf selection is not
        // falsely claimed to be equivalent to this rank.
        var compared = scored.Select(item => new
        {
            Item = item,
            BaselineAffinity = item.EventIdentity is not null
                && contributorComparisonAffinities.TryGetValue(item.EventIdentity, out var oldAffinity)
                    ? oldAffinity : item.LearnedUserAffinity
        }).ToArray();
        var baselineMean = compared.Length == 0 ? 0.0d : compared.Average(x => x.BaselineAffinity);
        var baselineStd = Math.Sqrt(compared.Length == 0 ? 0.0d
            : Math.Max(0.0d, compared.Average(x => Math.Pow(x.BaselineAffinity - baselineMean, 2.0d))));
        static double DiagnosticAdjustment(double affinity, double mean, double std, double maturity)
            => maturity > 0.0d && std > 0.000001d
                ? Math.Clamp((affinity - mean) / std, -2.5d, 2.5d) * maturity : 0.0d;
        var comparisons = compared.Select((x, index) => new
        {
            Index = index, x.Item,
            OldAffinity = x.BaselineAffinity,
            NewAffinity = x.Item.LearnedUserAffinity,
            OldAdjustment = DiagnosticAdjustment(x.BaselineAffinity, baselineMean, baselineStd, learnedUserModelMaturity),
            NewAdjustment = DiagnosticAdjustment(x.Item.LearnedUserAffinity, learnedAffinityMean, learnedAffinityStd, learnedUserModelMaturity)
        }).ToArray();
        var oldRanking = comparisons.OrderByDescending(x => x.Item.DeviationRawScore + x.OldAdjustment)
            .ThenBy(x => x.Index).Select((x, rank) => (x.Index, Rank: rank + 1))
            .ToDictionary(x => x.Index, x => x.Rank);
        var newRanking = comparisons.OrderByDescending(x => x.Item.DeviationRawScore + x.NewAdjustment)
            .ThenBy(x => x.Index).Select((x, rank) => (x.Index, Rank: rank + 1))
            .ToDictionary(x => x.Index, x => x.Rank);
        var affinityChanged = comparisons.Count(x => Math.Abs(x.NewAffinity - x.OldAffinity) > 0.0000001d);
        var adjustmentChanged = comparisons.Count(x => Math.Abs(x.NewAdjustment - x.OldAdjustment) > 0.0000001d);
        var rankChanged = comparisons.Count(x => oldRanking[x.Index] != newRanking[x.Index]);
        var top20Old = oldRanking.Where(x => x.Value <= 20).Select(x => x.Key).ToHashSet();
        var top20New = newRanking.Where(x => x.Value <= 20).Select(x => x.Key).ToHashSet();
        AIrhythmDataState.WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_SCORE_AB result=OBSERVED candidates={comparisons.Length} affinityChanged={affinityChanged} adjustmentChanged={adjustmentChanged} rankChanged={rankChanged} top20Entrants={top20New.Except(top20Old).Count()} top20Exits={top20Old.Except(top20New).Count()} explicitTrackedEqual={contributorExplicitIntentsPreserved} oldAffinityMean={baselineMean:0.000000} newAffinityMean={learnedAffinityMean:0.000000} meanAbsAdjustmentDelta={(comparisons.Length == 0 ? 0.0d : comparisons.Average(x => Math.Abs(x.NewAdjustment - x.OldAdjustment))):0.000000} maxAbsRankDelta={(comparisons.Length == 0 ? 0 : comparisons.Max(x => Math.Abs(oldRanking[x.Index] - newRanking[x.Index])))} baseline=pre_work_calibration_post_legacy_quarantine population=identical_accepted_candidates ranking=pre_shelf_adjusted_deviation storageMutation=False scoreMutation=False");
        foreach (var x in comparisons.OrderByDescending(x => Math.Abs(oldRanking[x.Index] - newRanking[x.Index]))
            .ThenByDescending(x => Math.Abs(x.NewAdjustment - x.OldAdjustment)).Take(12))
        {
            if (oldRanking[x.Index] == newRanking[x.Index] && Math.Abs(x.NewAdjustment - x.OldAdjustment) < 0.0000001d) continue;
            AIrhythmDataState.WriteDeveloperLog($"USER_MODEL_CONTRIBUTOR_SCORE_AB_SAMPLE work={x.Item.SeriesKey} oldRank={oldRanking[x.Index]} newRank={newRanking[x.Index]} oldAffinity={x.OldAffinity:0.000000} newAffinity={x.NewAffinity:0.000000} oldAdjustment={x.OldAdjustment:0.000000} newAdjustment={x.NewAdjustment:0.000000}");
        }
#endif
        if (learnedUserModelMaturity > 0.0d && learnedAffinityStd > 0.000001d)
        {
            scored = scored.Select(item =>
            {
                var z = (item.LearnedUserAffinity - learnedAffinityMean) / learnedAffinityStd;
                var adjustment = Math.Clamp(z, -2.5d, 2.5d) * learnedUserModelMaturity;
                return item with
                {
                    DeviationRawScore = item.DeviationRawScore + adjustment,
                    LearnedUserModelAdjustment = adjustment
                };
            }).ToList();
        }
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        AIrhythmDataState.WriteDeveloperLog(
            $"USER_MODEL_SCORE_SUMMARY observations={learnedUserModel.ObservationCount} maturity={learnedUserModelMaturity:0.000} candidates={scored.Count} affinityMean={learnedAffinityMean:0.000} affinityStd={learnedAffinityStd:0.000} adjustmentMean={(scored.Count == 0 ? 0.0d : scored.Average(x => x.LearnedUserModelAdjustment)):0.000} adjustmentMin={(scored.Count == 0 ? 0.0d : scored.Min(x => x.LearnedUserModelAdjustment)):0.000} adjustmentMax={(scored.Count == 0 ? 0.0d : scored.Max(x => x.LearnedUserModelAdjustment)):0.000} learningSource=explicit_behavior contributorModel=searchcast_explicit_plus_role_metadata populationCalibration=zscore_x_maturity titleSpecificRules=False recommendationRenderTeaches=False productionScoreMutation=True");
        foreach (var sample in scored
            .OrderByDescending(x => Math.Abs(x.LearnedUserModelAdjustment))
            .ThenByDescending(x => x.LearnedUserAffinity)
            .Take(12))
        {
            AIrhythmDataState.WriteDeveloperLog(
                $"USER_MODEL_SCORE_SAMPLE work={sample.SeriesKey} affinity={sample.LearnedUserAffinity:0.000} adjustment={sample.LearnedUserModelAdjustment:0.000} maturity={sample.LearnedUserModelMaturity:0.000} rawScore={sample.RawScore} deviationRawScore={sample.DeviationRawScore:0.000}");
        }
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

        var calibrationBackgroundIdentity = semanticEvidenceCount == 0 ? 0.0d : normalizedIdentityStrengthTotal / semanticEvidenceCount;
        var calibrationBackgroundContent = semanticEvidenceCount == 0 ? 0.0d : normalizedContentStrengthTotal / semanticEvidenceCount;
        var calibrationBackgroundContext = semanticEvidenceCount == 0 ? 0.0d : normalizedContextStrengthTotal / semanticEvidenceCount;
        var calibrationBackgroundExplicit = semanticEvidenceCount == 0 ? 0.0d : normalizedExplicitStrengthTotal / semanticEvidenceCount;
        var calibrationCandidates = calibrationGroupsByEvent.ToDictionary(
            pair => pair.Key,
            pair => new AIrhythmEvidenceCalibrationCandidate(
                pair.Value,
                calibrationBackgroundIdentity, calibrationBackgroundContent, calibrationBackgroundContext, calibrationBackgroundExplicit,
                semanticEvidenceCount));
        lock (ScoreResultCacheGate)
            CachedCalibrationCandidates = calibrationCandidates;

        AIrhythmDataState.WriteDeveloperLog(
            $"EVIDENCE_VECTOR_SUMMARY candidates={evidenceVectorCheckCount} axes=work,contributor,genre,terms,service,hour,interest,preferred groups=identity(work+contributor),content(genre+terms),context(service+hour),explicitBehavior(interest+preferred) identityAggregation=max_correlated_work_contributor baseIdentityMean={(semanticEvidenceCount == 0 ? 0.0d : semanticIdentityBaseTotal / semanticEvidenceCount):0.000} baseContentMean={(semanticEvidenceCount == 0 ? 0.0d : semanticContentBaseTotal / semanticEvidenceCount):0.000} baseContextMean={(semanticEvidenceCount == 0 ? 0.0d : semanticContextBaseTotal / semanticEvidenceCount):0.000} baseExplicitMean={(semanticEvidenceCount == 0 ? 0.0d : semanticExplicitBaseTotal / semanticEvidenceCount):0.000} learnedIdentityMean={(semanticEvidenceCount == 0 ? 0.0d : semanticLearnedIdentityTotal / semanticEvidenceCount):0.000} normalizedStrengthMean=[identity:{(semanticEvidenceCount == 0 ? 0.0d : normalizedIdentityStrengthTotal / semanticEvidenceCount):0.000},content:{(semanticEvidenceCount == 0 ? 0.0d : normalizedContentStrengthTotal / semanticEvidenceCount):0.000},context:{(semanticEvidenceCount == 0 ? 0.0d : normalizedContextStrengthTotal / semanticEvidenceCount):0.000},explicit:{(semanticEvidenceCount == 0 ? 0.0d : normalizedExplicitStrengthTotal / semanticEvidenceCount):0.000}] normalizedConfidenceMean=[identity:{(semanticEvidenceCount == 0 ? 0.0d : normalizedIdentityConfidenceTotal / semanticEvidenceCount):0.000},content:{(semanticEvidenceCount == 0 ? 0.0d : normalizedContentConfidenceTotal / semanticEvidenceCount):0.000},context:{(semanticEvidenceCount == 0 ? 0.0d : normalizedContextConfidenceTotal / semanticEvidenceCount):0.000},explicit:{(semanticEvidenceCount == 0 ? 0.0d : normalizedExplicitConfidenceTotal / semanticEvidenceCount):0.000}] normalizedPresent=[identity:{normalizedIdentityPresent},content:{normalizedContentPresent},context:{normalizedContextPresent},explicit:{normalizedExplicitPresent}] provenance=[workHistory:{provenanceWorkHistory},workReservation:{provenanceWorkReservation},contributorSearchCast:{provenanceContributorSearchCast},contributorMetadata:{provenanceContributorMetadata},genreHistory:{provenanceGenreHistory},termHistory:{provenanceTermHistory},serviceHistory:{provenanceServiceHistory},hourHistory:{provenanceHourHistory},interestSelection:{provenanceInterestSelection},preferredSetting:{provenancePreferredSetting}] normalization=group_theoretical_max confidence=behavioral_support provenance=source_flags semanticCandidates={semanticEvidenceCount} contributorHitCandidates={semanticContributorHitCandidates} allocation=value_type_local_only allocatedBytes={evidenceVectorAllocatedBytes} retainedPerRecommendation=False scoreRecomposition=maxDelta:{evidenceVectorMaxDelta:0.000000} comparison=pre_round_legacy_vs_semantic_vector persistence=derived_not_saved purpose=self_calibration_seam scoreMutation=False");

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

        // 推薦・学習 — Continuity and Explicit Interest now share one dominant-family product coordinate. These
        // summaries verify that each applied family stays aligned with the same canonical evaluation used by
        // scoring; correlated evidence is never added twice.
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
            $"SCORE_CANDIDATE_ALLOCATION_PHASES canonicalTextFilterBytes={candidateCanonicalTextFilterBytes} seriesGenreBytes={candidateSeriesGenreBytes} termsBytes={candidateTermsBytes} contextInterestBytes={candidateContextInterestBytes} localExternalBytes={candidateLocalExternalBytes} recommendationBytes={candidateRecommendationBytes} accountedBytes={candidateSubphaseAccountedBytes} candidateLoopBytes={candidateLoopBytes} unaccountedBytes={candidateLoopBytes - candidateSubphaseAccountedBytes} visited={candidateLoopVisited} excluded={candidateLoopExcluded} deduped={candidateLoopDeduped} belowMinimum={candidateLoopBelowMinimum} scored={deviationScored.Count} scope=current_thread_exact checkpoints=aggregate_only_no_midphase_logging semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_CANDIDATE_CANONICAL_DETAIL canonicalFactsBytes={candidateCanonicalFactsBytes} textBuildBytes={candidateTextBuildBytes} excludedFilterBytes={candidateExcludedFilterBytes} parentBytes={candidateCanonicalTextFilterBytes} accountedBytes={candidateCanonicalFactsBytes + candidateTextBuildBytes + candidateExcludedFilterBytes} unaccountedBytes={candidateCanonicalTextFilterBytes - (candidateCanonicalFactsBytes + candidateTextBuildBytes + candidateExcludedFilterBytes)} visited={candidateLoopVisited} excluded={candidateLoopExcluded} scope=current_thread_exact semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_CANONICAL_FACTS_INTERNAL preEvaluationTitleBytes={identityContext.CanonicalPreEvaluationTitleBytes} localWorkKeyBytes={identityContext.CanonicalLocalWorkKeyBytes} aliasResolutionBytes={identityContext.CanonicalAliasResolutionBytes} genreBytes={identityContext.CanonicalGenreBytes} tokensBytes={identityContext.CanonicalTokensBytes} replayBytes={identityContext.CanonicalReplayBytes} accountedBytes={identityContext.CanonicalPreEvaluationTitleBytes + identityContext.CanonicalLocalWorkKeyBytes + identityContext.CanonicalAliasResolutionBytes + identityContext.CanonicalGenreBytes + identityContext.CanonicalTokensBytes + identityContext.CanonicalReplayBytes} parentCanonicalFactsBytes={candidateCanonicalFactsBytes} unaccountedBytes={candidateCanonicalFactsBytes - (identityContext.CanonicalPreEvaluationTitleBytes + identityContext.CanonicalLocalWorkKeyBytes + identityContext.CanonicalAliasResolutionBytes + identityContext.CanonicalGenreBytes + identityContext.CanonicalTokensBytes + identityContext.CanonicalReplayBytes)} scope=candidate_loop_only_current_thread_exact semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_CANDIDATE_LOCAL_EXTERNAL_DETAIL dedupeBytes={candidateDedupeBytes} localContinuityBytes={candidateLocalContinuityBytes} localShadowBytes={candidateLocalShadowBytes} broadcastIdentityBytes={candidateBroadcastIdentityBytes} externalProjectionBytes={candidateExternalProjectionBytes} localAggregateAdjustmentBytes={candidateLocalAggregateAdjustmentBytes} parentBytes={candidateLocalExternalBytes} accountedBytes={candidateDedupeBytes + candidateLocalContinuityBytes + candidateLocalShadowBytes + candidateBroadcastIdentityBytes + candidateExternalProjectionBytes + candidateLocalAggregateAdjustmentBytes} unaccountedBytes={candidateLocalExternalBytes - (candidateDedupeBytes + candidateLocalContinuityBytes + candidateLocalShadowBytes + candidateBroadcastIdentityBytes + candidateExternalProjectionBytes + candidateLocalAggregateAdjustmentBytes)} deduped={candidateLoopDeduped} belowMinimum={candidateLoopBelowMinimum} scored={deviationScored.Count} scope=current_thread_exact semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_BEHAVIOR_ALLOCATION_DETAIL preHistoryBytes={scoreBehaviorBeforeHistory - scoreSetupAfterLearnedModel} recordedFactsBytes={scoreBehaviorAfterHistory - scoreBehaviorBeforeHistory} reservationFactsBytes={scoreSetupAfterBehaviorWeights - scoreBehaviorAfterHistory} accountedBytes={scoreSetupAfterBehaviorWeights - scoreSetupAfterLearnedModel} scope=current_thread_exact semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_LOCAL_AGGREGATE_ALLOCATION_DETAIL localDecisionBytes={candidateLocalDecisionBytes} contributorExtractionBytes={candidateContributorExtractionBytes} learnedAffinityBytes={candidateLearnedAffinityBytes} semanticDiagnosticsBytes={candidateSemanticDiagnosticsBytes} accountedBytes={candidateLocalDecisionBytes + candidateContributorExtractionBytes + candidateLearnedAffinityBytes + candidateSemanticDiagnosticsBytes} parentBytes={candidateLocalAggregateAdjustmentBytes} scope=current_thread_exact noMidphaseLogging=True semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_SETUP_ALLOCATION_DETAIL indicesBytes={scoreSetupAfterIndices - scoreAllocatedStart} localEvidenceBytes={scoreSetupAfterLocalEvidence - scoreSetupAfterIndices} historyEvidenceBytes={scoreSetupAfterHistory - scoreSetupAfterLocalEvidence} learnedModelBytes={scoreSetupAfterLearnedModel - scoreSetupAfterHistory} behaviorWeightsBytes={scoreSetupAfterBehaviorWeights - scoreSetupAfterLearnedModel} remainingSetupBytes={scoreAllocatedAfterSetup - scoreSetupAfterBehaviorWeights} accountedBytes={scoreAllocatedAfterSetup - scoreAllocatedStart} scope=current_thread_exact checkpoints=no_midphase_logging modelStorageAndReadbackIncluded=True indexReuse=local_score_model semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_ALLOCATION_PHASES setupBytes={setupBytes} candidateLoopBytes={candidateLoopBytes} preDeviationDiagnosticsBytes={preDeviationDiagnosticsBytes} deviationBytes={deviationBytes} postDeviationDiagnosticsBytes={postDeviationDiagnosticsBytes} accountedBytes={setupBytes + candidateLoopBytes + preDeviationDiagnosticsBytes + deviationBytes + postDeviationDiagnosticsBytes} scope=current_thread_exact checkpoints=no_midphase_logging interpretation=diagnostic_only_not_regression_gate semantics=unchanged");
        AIrhythmDataState.WriteDeveloperLog(
            $"SCORE_ALLOCATION allocatedBytes={scoreAllocatedBytes} events={snapshot.Events.Count} scored={deviationScored.Count} termWeights={termWeights.Count} interestSignals={preparedInterestSignals.Length} scope=current_thread_exact candidateTextLowerCopy=removed termMatchMaterialization=single_pass_top3 interestSignalNormalization=single_pass phaseBreakdown=enabled evaluation=total_allocated_bytes_primary phaseBreakdownInterpretation=diagnostic_only semantics=unchanged");
#endif
        return deviationScored;
    }

    private static int RelativeDisplayScoreFromZ(double z)
    {
        // 処理条件 — 平均50を中心とする標準偏差ベースの相対表示。 中央付近は「1標準偏差 ≒ 10点」の感覚を維持しつつ、外れ値側だけをtanhで穏やかに圧縮する。
        // 高得点を作ること自体を目的にせず、90台は母集団から極端に突出した候補に限って自然に現れる。 0/100を絶対評価として見せないため表示レンジは1..99とし、100は生成しない。 目安:
        // z=1→約60、z=2→約69、z=3→約77、z=4→約83、z=5→約88、z=6→約91。 係数4.9は原点付近の傾きを約10点/σにするため、通常候補を50周辺へ自然に分散させる。
        var compressed = 50.0d + 49.0d * Math.Tanh(z / 4.9d);
        return Math.Clamp((int)Math.Round(compressed), 1, 99);
    }

    private static IReadOnlyList<AIrhythmRecommendation> ApplyDeviationScores(IReadOnlyList<AIrhythmRecommendation> items)
    {
        if (items.Count == 0) return Array.Empty<AIrhythmRecommendation>();

        // 録画実績・復旧 — 偏差表示は、評価対象として残った全候補の未丸め・既存confidenceCap適用済みスコアを同じ母集団として扱う。
        // 特定の棚・番組・ジャンル・録画件数による補正や母集団の選別は行わない。 表示値だけを標準的な偏差値（平均50、標準偏差10）へ変換する。
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
            var displayScore = RelativeDisplayScoreFromZ(deviation);
            return x with { Score = displayScore };
        }).ToArray();
    }

    private static int FindSeriesEvidence(IReadOnlyDictionary<string, int> counts, string seriesKey)
    {
        // 録画実績・復旧 — 録画回数のような強い系列証拠は、同じ安定系列キーへ収束した場合だけ共有する。 部分一致は関連作品の発見には使えても、同一作品を追っている証拠にはしない。
        if (seriesKey.Length < 3) return 0;
        return counts.TryGetValue(seriesKey, out var exact) ? exact : 0;
    }

    private static double FindSeriesEvidence(IReadOnlyDictionary<string, double> weights, string seriesKey)
    {
        // 処理条件 — 自動検索・予約由来の継続意思も、同じ安定系列キーにだけ帰属させる。
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


}
