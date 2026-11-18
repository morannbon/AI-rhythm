namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmRecommendationEngine
{
    internal static AIrhythmEvidenceIdentityContext BuildEvidenceIdentityContext(AIrhythmRuntimeSnapshot snapshot)
    {
        var epgNumeric = BuildNumericParenthesizedLocalSequenceValuesByService(snapshot.Events);
        var numericEvidence = BuildNumericParenthesizedEvidenceValuesByService(snapshot);
        // 録画実績・復旧 — The EPG is a moving window. Successful recordings and established reservations are
        // independent historical corroboration of a same-service episode sequence. Retain both evidence sets
        // before resolving any Work identity; neither changes the fact count.
        var numeric = MergeNumericWorkSequenceEvidence(epgNumeric, numericEvidence);
        var aliases = BuildLocalWorkAliases(snapshot, numeric);
        var externalCanonicalTitles = AIrhythmDataState.GetExternalPreEvaluationCanonicalTitles();
        var normalizationPromotions = AIrhythmDataState.GetNormalizationPromotions();
        var context = new AIrhythmEvidenceIdentityContext(numeric, numericEvidence, aliases, externalCanonicalTitles, normalizationPromotions);
        context.CorroboratedWorkGenres = BuildCorroboratedWorkGenres(snapshot.Events, context);
        context.HistoricalWorkGenres = BuildHistoricalWorkGenreEvidence(snapshot.History, context);
        // Earlier evidence gathering must not retain pre-assessment Genre values.
        context.ProgramFacts.Clear();
        context.HistoryFacts.Clear();
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var corrected = snapshot.History
            .Select(item => (Item: item, Facts: CanonicalEvidenceFacts(item, context)))
            .Where(x => x.Facts.GenreKey.Length > 0
                && !string.Equals(x.Facts.GenreKey, NormalizeGenre(x.Item.Genre), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var examples = corrected.Take(6).Select(x =>
            $"{x.Item.ServiceName} {x.Item.ProgramTitle} {x.Item.Start:yyyy-MM-dd HH:mm}"
            + $" [{NormalizeGenre(x.Item.Genre)}=>{x.Facts.GenreKey}]");
        var disputed = context.HistoricalWorkGenres
            .Where(x => x.Value.Ambiguous)
            .Select(x => x.Key.Work)
            .Take(6);
        var genreProvenance = snapshot.History
            .Select(item => CanonicalEvidenceFacts(item, context).GenreEvidenceSource)
            .GroupBy(x => x)
            .OrderBy(x => x.Key)
            .Select(x => $"{x.Key}:{x.Count()}");
        AIrhythmDataState.WriteDeveloperLog(
            $"CANONICAL_GENRE_CORROBORATION supportedProgrammeForms={context.CorroboratedWorkGenres.Count}"
            + $" correctedRecordings={corrected.Length} samples=[{string.Join(";", examples)}]"
            + $" historyGenreSeries={context.HistoricalWorkGenres.Count} disputedWorks=[{string.Join(";", disputed)}]"
            + $" genreEvidence=[{string.Join(";", genreProvenance)}]"
            + " storageMutation=False fallback=unknown_when_conflicting_unproven");
#endif
        return context;
    }

    // A genre can be revised only by independent evidence of programme form, never by a
    // majority of EPG genre labels. Repeated broadcasts of the same wrong category are
    // correlated reports, not independent confirmation. Do not cross film/drama/promo forms.
    internal readonly record struct BroadcastGenreEvidenceKey(string Work, string Form, AIrhythmServiceIdentity Service);
    // Evidence is scoped to the particular broadcast form and service. The raw
    // EPG category is provenance, not independent proof of a genre correction.
    internal readonly record struct HistoricalWorkGenreKey(string Work, AIrhythmServiceIdentity Service);
    internal readonly record struct HistoricalWorkGenreAssessment(string Genre, bool Ambiguous);
    internal enum GenreEvidenceSource
    {
        Unknown,
        BroadcastMetadata,
        ExplicitTitle,
        CorroboratedProgramme,
        CorroboratedHistory,
        Conflicting
    }
    internal readonly record struct CanonicalGenreDecision(string GenreKey, GenreEvidenceSource Source);

    // The broadcast form and explicit genre depend on the immutable on-air title alone.
    // Evidence or preference state must never be part of this per-snapshot cache.
    internal readonly record struct BroadcastTitleEvidence(
        AIrhythmDerivedProgramRelation Relation, string ExplicitGenre, string Form);

    private static BroadcastTitleEvidence TitleEvidence(string? title, AIrhythmEvidenceIdentityContext context)
    {
        var key = title ?? string.Empty;
        if (context.BroadcastTitles.TryGetValue(key, out var existing))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            context.BroadcastTitleHits++;
#endif
            return existing;
        }
        // Keep the existing canonical parser and precedence intact. Only the repeated
        // result is shared; genre reconciliation still uses the recording/EPG context.
        var relation = ClassifyDerivedProgramRelation(title);
        var explicitGenre = relation == AIrhythmDerivedProgramRelation.Promo
            ? string.Empty : ExplicitProgrammeGenre(title);
        var form = BroadcastFormFromEvidence(relation, explicitGenre);
        var evidence = new BroadcastTitleEvidence(relation, explicitGenre, form);
        context.BroadcastTitles.Add(key, evidence);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        context.BroadcastTitleMisses++;
#endif
        return evidence;
    }

    // All learning routes consume the one genre-proof policy. Broadcast metadata
    // alone is not reliable enough to rewrite an existing preference receipt.
    private static bool CanReconcileGenreLearning(GenreEvidenceSource source)
        => source is GenreEvidenceSource.ExplicitTitle
            or GenreEvidenceSource.CorroboratedProgramme
            or GenreEvidenceSource.CorroboratedHistory
            or GenreEvidenceSource.Conflicting;

    private static string HistoricalGenreFormCached(string? title, string? sourceGenre,
        AIrhythmEvidenceIdentityContext context)
        => HistoricalGenreFormFromForm(TitleEvidence(title, context).Form, sourceGenre);

    private static string HistoricalGenreFormFromForm(string form, string? sourceGenre)
    {
        if (form == "promotion") return "promotion";
        if (form == "drama") return "ordinary";
        if (form == "film" || NormalizeGenre(sourceGenre) == "映画") return "film";
        if (form != "drama" && NormalizeGenre(sourceGenre) is ("ニュース/報道" or "ドキュメンタリー/教養"))
            return "factual";
        return "ordinary"; // Ordinary and explicitly marked drama are the same narrative form.
    }

    private static string BroadcastForm(string? title)
    {
        var relation = ClassifyDerivedProgramRelation(title);
        var explicitGenre = relation == AIrhythmDerivedProgramRelation.Promo
            ? string.Empty : ExplicitProgrammeGenre(title);
        return BroadcastFormFromEvidence(relation, explicitGenre);
    }

    private static string BroadcastFormFromEvidence(AIrhythmDerivedProgramRelation relation, string explicitGenre)
        => relation == AIrhythmDerivedProgramRelation.Promo ? "promotion" : explicitGenre switch
        {
            "映画" => "film",
            "ドラマ" => "drama",
            _ => "ordinary"
        };

    // A series boundary is learned only from multiple useful user recordings or
    // reservations. Apply that same verified boundary to current EPG titles whose
    // editorial episode subtitles or cast credits differ from the stored titles.
    // EPG repetition alone cannot establish a series, and an explicit film/promo
    // must never borrow the identity of the underlying broadcast series.
    private static bool TryResolveEvidenceBackedSeriesWork(
        string analysisTitle, AIrhythmServiceIdentity service,
        AIrhythmEvidenceIdentityContext context, out string work)
    {
        work = string.Empty;
        if (!service.IsValid || context.WorkAliases.Count == 0
            || (!analysisTitle.Contains('～') && !analysisTitle.Contains('〜') && !analysisTitle.Contains('~')))
            return false;
        var normalized = analysisTitle.Normalize(NormalizationForm.FormKC);
        var frame = Regex.Match(normalized,
            @"^(?<series>.+?)[～~〜][^～~〜]{3,100}(?:[～~〜](?:\s+.*)?|$)");
        if (!frame.Success) return false;
        var series = EvidenceSeriesKey(frame.Groups["series"].Value);
        if (series.Length < 6) return false;
        // The complete series name must already be corroborated as a Work by
        // multiple user facts on THIS service. A prefix alone is insufficient.
        if (!context.WorkAliases.Any(x => x.Key.ServiceIdentity == service
            && string.Equals(x.Value, series, StringComparison.OrdinalIgnoreCase)))
            return false;
        var titleEvidence = TitleEvidence(analysisTitle, context);
        if (titleEvidence.Relation == AIrhythmDerivedProgramRelation.Promo
            || string.Equals(titleEvidence.ExplicitGenre, "映画", StringComparison.Ordinal))
            return false;
        work = series;
        return true;
    }

    // Production labels and format tags are evidence about THIS broadcast, not an
    // assumed genre of all broadcasts sharing the canonical work name.
    private static string ExplicitProgrammeGenre(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var normalized = title.Normalize(NormalizationForm.FormKC).Trim();
        if (ClassifyDerivedProgramRelation(normalized) == AIrhythmDerivedProgramRelation.Promo)
            return string.Empty;
        if (Regex.IsMatch(normalized, @"^(?:\[[^\]]{1,8}\]\s*)*\[映\]", RegexOptions.IgnoreCase))
            return "映画";
        var parts = ParseCanonicalTitleParts(normalized, alreadyNormalized: true);
        if (string.Equals(parts.ContentType, "映画", StringComparison.OrdinalIgnoreCase))
            return "映画";
        if (string.Equals(parts.ContentType, "ドラマ", StringComparison.OrdinalIgnoreCase))
            return "ドラマ";
        if (Regex.IsMatch(normalized, @"^(?:\[[^\]]{1,8}\]\s*)*(?:(?:【ドラマ】)|(?:連続|テレビ|TV|韓国|中国|海外|時代|夜|朝)ドラマ)(?:\s|[「『【])", RegexOptions.IgnoreCase))
            return "ドラマ";
        // Broadcast history may omit the cast credits appended to a current EPG
        // title. A framed fictional case together with either cast credits or
        // a serialized investigative-story label is evidence of programme form;
        // neither a crime-related word nor a cast name alone is sufficient.
        // Explicit news/documentary labels veto inference from the title frame.
        if (Regex.IsMatch(normalized, @"[～~〜][^～~〜]{2,60}(?:殺人事件|誘拐事件|失踪事件|怪事件|事件)[～~〜]")
            && !Regex.IsMatch(normalized, @"(?:ニュース|報道|ドキュメンタリー|ノンフィクション|実録)", RegexOptions.IgnoreCase)
            && (Regex.IsMatch(normalized, @"出演\s*[:：]\s*\S{2,}")
                || (Regex.IsMatch(normalized, @"(?:事件ファイル|事件簿|捜査ファイル|推理ファイル)")
                    && Regex.IsMatch(normalized, @"[～~〜][^～~〜]{2,60}(?:殺人事件|誘拐事件|失踪事件|怪事件)[～~〜]"))))
            return "ドラマ";
        return string.Empty;
    }

    // Programme descriptions can supply a second, content-oriented source of
    // evidence. The categorical EPG field is deliberately NOT inspected here.
    private static string DescribedProgrammeGenre(TvAirProgramEventDto item, BroadcastTitleEvidence titleEvidence)
    {
        if (titleEvidence.ExplicitGenre.Length > 0)
            return titleEvidence.ExplicitGenre;
        if (titleEvidence.Form == "promotion")
            return string.Empty;
        var description = $"{item.Summary} {item.Detail} {item.ExtendedItems}";
        if (Regex.IsMatch(description, @"(?:サスペンス|ミステリー|連続|テレビ|2時間|二時間)\s*ドラマ|ドラマ\s*(?:作品|シリーズ|仕立て)", RegexOptions.IgnoreCase))
            return "ドラマ";
        return string.Empty;
    }

    // Select a Work's historical genre once, before building any recording fact. It is
    // legitimate to fill an omitted title marker from an independently recorded
    // sibling on the same service only when the SAME already-confirmed Work boundary
    // has two distinct aliases. A single programme or EPG repetition cannot invent
    // a genre. Conflicting uncorroborated raw labels remain unknown, not a vote.
    private static IReadOnlyDictionary<HistoricalWorkGenreKey, HistoricalWorkGenreAssessment>
        BuildHistoricalWorkGenreEvidence(IReadOnlyList<TvAirRecordingHistoryDto> history,
            AIrhythmEvidenceIdentityContext context)
    {
        var aliasCounts = context.WorkAliases
            .GroupBy(x => new HistoricalWorkGenreKey(x.Value, x.Key.ServiceIdentity))
            .ToDictionary(x => x.Key, x => x.Count());
        var results = new Dictionary<HistoricalWorkGenreKey, HistoricalWorkGenreAssessment>();
        var grouped = history
            .Where(AIrhythmDataState.IsUsefulHistory)
            .Select(item => new
            {
                Item = item,
                Service = ServiceIdentityOf(item),
                Work = CanonicalWorkKey(item.ProgramTitle, ServiceIdentityOf(item), context),
                Form = HistoricalGenreFormCached(item.ProgramTitle, item.Genre, context)
            })
            .Where(x => x.Service.IsValid && x.Work.Length > 0 && x.Form == "ordinary")
            .GroupBy(x => new HistoricalWorkGenreKey(x.Work, x.Service));
        foreach (var group in grouped)
        {
            var entries = group.ToArray();
            if (entries.Length < 2) continue;
            var rawGenres = entries.Select(x => NormalizeGenre(x.Item.Genre))
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var explicitGenres = entries.Select(x => TitleEvidence(x.Item.ProgramTitle, context).ExplicitGenre)
                .Where(x => x.Length > 0)
                .Select(NormalizeGenre)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var distinctTitleCount = entries.Select(x => CompactIdentity(x.Item.ProgramTitle))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var independentBroadcasts = entries.Select(x => x.Item.Start.UtcDateTime.Ticks)
                .Distinct().Count();
            var independentlyEstablished = distinctTitleCount >= 2 && independentBroadcasts >= 2
                && aliasCounts.TryGetValue(group.Key, out var aliases) && aliases >= 2;
            if (independentlyEstablished && explicitGenres.Length == 1
                // A film version requires its own film-form proof. Never infer
                // that an unmarked same-name broadcast is also the film.
                && explicitGenres[0] == "ドラマ"
                && !rawGenres.Any(x => x is "映画" or "ニュース/報道" or "ドキュメンタリー/教養")
                && entries.All(x => TitleEvidence(x.Item.ProgramTitle, context).Relation
                    != AIrhythmDerivedProgramRelation.Promo))
            {
                results[group.Key] = new HistoricalWorkGenreAssessment(explicitGenres[0], false);
            }
            else if (rawGenres.Length > 1 || explicitGenres.Length > 1)
            {
                // This is deliberately an unknown genre for the shared historical
                // Work. It must never become a learned preference for "その他".
                results[group.Key] = new HistoricalWorkGenreAssessment(string.Empty, true);
            }
        }
        return results;
    }

    private static IReadOnlyDictionary<BroadcastGenreEvidenceKey, string> BuildCorroboratedWorkGenres(
        IReadOnlyList<TvAirProgramEventDto> events,
        AIrhythmEvidenceIdentityContext context)
    {
        var candidates = new Dictionary<BroadcastGenreEvidenceKey, Dictionary<string, HashSet<string>>>();
        foreach (var item in events)
        {
            var titleEvidence = TitleEvidence(item.Title, context);
            var genre = DescribedProgrammeGenre(item, titleEvidence);
            if (genre.Length == 0) continue;
            var work = CanonicalWorkKey(item, context);
            if (work.Length == 0) continue;
            var service = ServiceIdentityOf(item);
            if (!service.IsValid) continue;
            var key = new BroadcastGenreEvidenceKey(work, titleEvidence.Form, service);
            if (!candidates.TryGetValue(key, out var byGenre))
                candidates[key] = byGenre = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (!byGenre.TryGetValue(genre, out var airings))
                byGenre[genre] = airings = new HashSet<string>(StringComparer.Ordinal);
            // An EPG title change alone must not turn a repeated generic synopsis
            // into multiple independent supporting descriptions. Explicit form
            // labels are individually verifiable on the actual broadcast title.
            var explicitForm = titleEvidence.ExplicitGenre.Length > 0;
            var evidenceText = explicitForm ? item.Title : $"{item.Summary} {item.Detail} {item.ExtendedItems}";
            var editorialKey = CompactIdentity(evidenceText);
            if (editorialKey.Length > 0)
                airings.Add($"{(explicitForm ? "title" : "description")}:{editorialKey}");
        }
        var resolved = new Dictionary<BroadcastGenreEvidenceKey, string>();
        foreach (var (key, byGenre) in candidates)
        {
            var counts = byGenre.Select(x => (Genre: x.Key, Count: x.Value.Count))
                .OrderByDescending(x => x.Count).ThenBy(x => x.Genre, StringComparer.OrdinalIgnoreCase).ToArray();
            if (counts.Length == 0 || counts[0].Count < 2) continue;
            var total = counts.Sum(x => x.Count);
            if (counts[0].Count * 5 < total * 4) continue;
            if (counts.Length > 1 && counts[0].Count == counts[1].Count) continue;
            resolved[key] = counts[0].Genre;
        }
        return resolved;
    }

    private static CanonicalGenreDecision CanonicalBroadcastGenre(string? title, string? rawGenre,
        string canonicalWorkKey, AIrhythmServiceIdentity service, AIrhythmEvidenceIdentityContext context,
        bool isRecording)
    {
        var sourceGenre = NormalizeGenre(rawGenre);
        var sourceDecision = new CanonicalGenreDecision(sourceGenre,
            sourceGenre.Length > 0 ? GenreEvidenceSource.BroadcastMetadata : GenreEvidenceSource.Unknown);
        var titleEvidence = TitleEvidence(title, context);
        var form = titleEvidence.Form;
        if (form == "promotion")
            return new CanonicalGenreDecision(string.Empty, GenreEvidenceSource.Unknown);
        var explicitGenre = titleEvidence.ExplicitGenre;
        if (explicitGenre.Length > 0)
            return new CanonicalGenreDecision(NormalizeGenre(explicitGenre), GenreEvidenceSource.ExplicitTitle);
        // A recorded film/factual broadcast cannot inherit an unrelated drama.
        // This applies before looking at other broadcasts of a shared Work.
        if (sourceGenre is "映画" or "ニュース/報道" or "ドキュメンタリー/教養")
            return sourceDecision;
        var historicalKey = new HistoricalWorkGenreKey(canonicalWorkKey, service);
        if (isRecording && canonicalWorkKey.Length > 0 && service.IsValid
            && context.HistoricalWorkGenres.TryGetValue(historicalKey, out var disputedWork)
            && disputedWork.Ambiguous)
            return new CanonicalGenreDecision(string.Empty, GenreEvidenceSource.Conflicting);
        if (canonicalWorkKey.Length > 0 && service.IsValid
            && context.CorroboratedWorkGenres.TryGetValue(
                new BroadcastGenreEvidenceKey(canonicalWorkKey, form, service), out var supportedGenre))
            return new CanonicalGenreDecision(supportedGenre, GenreEvidenceSource.CorroboratedProgramme);
        if (form == "ordinary" && canonicalWorkKey.Length > 0 && service.IsValid
            && context.HistoricalWorkGenres.TryGetValue(historicalKey, out var historical)
            && historical.Genre.Length > 0)
            return new CanonicalGenreDecision(historical.Genre, GenreEvidenceSource.CorroboratedHistory);
        // An ordinary title can borrow a proved drama classification only
        // after separate user facts established the exact same-service series.
        if (form == "ordinary" && canonicalWorkKey.Length > 0 && service.IsValid
            && !Regex.IsMatch(title ?? string.Empty,
                @"(?:ニュース|報道|ドキュメンタリー|ノンフィクション|実録)", RegexOptions.IgnoreCase)
            && context.WorkAliases.Any(x => x.Key.ServiceIdentity == service
                && string.Equals(x.Value, canonicalWorkKey, StringComparison.OrdinalIgnoreCase))
            && context.CorroboratedWorkGenres.TryGetValue(
                new BroadcastGenreEvidenceKey(canonicalWorkKey, "drama", service), out var corroboratedDrama)
            && !context.CorroboratedWorkGenres.ContainsKey(
                new BroadcastGenreEvidenceKey(canonicalWorkKey, "film", service)))
            return new CanonicalGenreDecision(corroboratedDrama, GenreEvidenceSource.CorroboratedProgramme);
        return sourceDecision;
    }

    private static IReadOnlyDictionary<string, int[]> MergeNumericWorkSequenceEvidence(
        IReadOnlyDictionary<string, int[]> epg,
        IReadOnlyDictionary<string, int[]> historical)
    {
        var result = epg.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in historical)
        {
            if (result.TryGetValue(entry.Key, out var previous))
                result[entry.Key] = previous.Concat(entry.Value).Distinct().OrderBy(x => x).ToArray();
            else
                result[entry.Key] = entry.Value;
        }
        return result;
    }

    // 録画実績・復旧 — Pure title transforms are reused across render snapshots. Manual external-evidence refresh
    // invalidates the score snapshot, but it must not force the same title-only normalization and canonical
    // parsing work to allocate again for every EPG row. These caches contain no reservation/history/provider
    // decision state, so reuse cannot change recommendation semantics. The soft cap prevents long-running
    // hosts from retaining an unbounded catalogue of old titles.
    private const int SharedTitleTransformCacheLimit = 24576;
    private static readonly ConcurrentDictionary<string, string> SharedExternalPreEvaluationTitleKeys = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> SharedEvidenceSeriesKeys = new(StringComparer.Ordinal);

    private static string PreEvaluationTitle(string? title, AIrhythmEvidenceIdentityContext context)
    {
        var raw = (title ?? string.Empty).Trim();
        if (raw.Length == 0)
            return string.Empty;
        if (context.ExternalCanonicalTitles.Count == 0)
            return raw;
        // 外部情報 — External catalog aliases cannot turn a promotional broadcast into the advertised programme: its
        // derived identity is a local structural fact.
        if (TitleEvidence(raw, context).Relation == AIrhythmDerivedProgramRelation.Promo)
            return raw;
        // 外部情報 — A composite title containing an on-air show and a different show's announcement is not an
        // unambiguous external catalogue alias.
        if (!string.Equals(AiredTitleEvidenceText(raw), raw, StringComparison.Ordinal))
            return raw;
        var key = ExternalPreEvaluationTitleKey(raw);
        return key.Length > 0 && context.ExternalCanonicalTitles.TryGetValue(key, out var canonical) && !string.IsNullOrWhiteSpace(canonical)
            ? canonical
            : raw;
    }

    internal static string ExternalPreEvaluationTitleKey(string? title)
    {
        var source = title ?? string.Empty;
        if (source.Length == 0)
            return string.Empty;
        if (SharedExternalPreEvaluationTitleKeys.TryGetValue(source, out var cached))
            return cached;

        var computed = new string(source.Normalize(NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        if (SharedExternalPreEvaluationTitleKeys.Count < SharedTitleTransformCacheLimit)
            SharedExternalPreEvaluationTitleKeys.TryAdd(source, computed);
        return computed;
    }

    private static bool LocalWorkKeyRequiresServiceScope(string value)
    {
        // 保存・読込 — LocalWorkKey is service-dependent only for the trailing-parenthesized-number path, where
        // same-service corroboration decides whether the numeric suffix belongs to Work. Every such title must
        // end in a closing parenthesis. Retain all closing-parenthesis titles service-scoped conservatively;
        // ordinary titles can safely share one snapshot cache entry across services without changing Work
        // identity semantics.
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(value[i]))
                continue;
            return value[i] is ')' or '）';
        }
        return false;
    }

    private static string[] TitleTokensCached(string analysisTitle, AIrhythmEvidenceIdentityContext context)
    {
        if (context.TitleTokens.TryGetValue(analysisTitle, out var tokens))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            context.TitleTokenCacheHits++;
#endif
            return tokens;
        }
        tokens = Tokens(analysisTitle).ToArray();
        context.TitleTokens.Add(analysisTitle, tokens);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        context.TitleTokenCacheMisses++;
#endif
        return tokens;
    }

    private static string PromotionLookupKeyCached(string analysisTitle, AIrhythmEvidenceIdentityContext context)
    {
        if (context.PromotionLookupKeys.TryGetValue(analysisTitle, out var key))
        {
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
            context.PromotionKeyCacheHits++;
#endif
            return key;
        }
        key = CompactIdentity(NormalizationLearnerComparableTitle(AiredTitleEvidenceText(analysisTitle)));
        context.PromotionLookupKeys.Add(analysisTitle, key);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        context.PromotionKeyCacheMisses++;
#endif
        return key;
    }

    private static string LocalWorkKeyCached(
        string? title,
        AIrhythmServiceIdentity service,
        AIrhythmEvidenceIdentityContext context)
    {
        var source = title ?? string.Empty;
        var cacheService = LocalWorkKeyRequiresServiceScope(source) ? service : default;
        var cacheKey = new AIrhythmLocalWorkCacheKey(source, cacheService);
        if (context.LocalWorkKeys.TryGetValue(cacheKey, out var cached))
        {
            context.LocalWorkKeyCacheHits++;
            return cached;
        }

        var computed = LocalWorkKey(source, service, context.NumericLocalValuesByService);
        context.LocalWorkKeys[cacheKey] = computed;
        context.LocalWorkKeyCacheMisses++;
        return computed;
    }

    internal static string CanonicalWorkKey(string? title, AIrhythmServiceIdentity service, AIrhythmEvidenceIdentityContext context)
    {
        var analysisTitle = PreEvaluationTitle(title, context);
        return CanonicalWorkIdentity(analysisTitle, service, context);
    }

    // 録画実績・復旧 — Recordings, reservations, events, and title lookups resolve the same Work identity.
    private static string CanonicalWorkIdentity(
        string analysisTitle,
        AIrhythmServiceIdentity service,
        AIrhythmEvidenceIdentityContext context)
    {
        return CanonicalWorkIdentity(analysisTitle, service, context, LocalWorkKeyCached(analysisTitle, service, context));
    }

    // 保存・読込 — When canonical facts already computed the service-scoped local key, pass it through. The
    // output cache is limited to this immutable evidence snapshot: it never retains past EPG titles or
    // persists any promoted/alias decision across a refresh.
    private static string CanonicalWorkIdentity(
        string analysisTitle,
        AIrhythmServiceIdentity service,
        AIrhythmEvidenceIdentityContext context,
        string localWorkKey)
    {
        var resolutionKey = new AIrhythmLocalWorkCacheKey(analysisTitle, service);
        if (context.CanonicalWorkResolutions.TryGetValue(resolutionKey, out var resolved))
            return resolved;

        // 作品識別 — Promotion broadcasts never borrow the underlying programme's evidence, regardless of saved
        // aliases or learner proposals. Both have different canonical Work identities even when their quoted
        // advertised title matches.
        if (TitleEvidence(analysisTitle, context).Relation == AIrhythmDerivedProgramRelation.Promo)
        {
            context.CanonicalWorkResolutions[resolutionKey] = localWorkKey;
            return localWorkKey;
        }

        // 作品識別 — The explicit tournament day/part belongs to Event/Part rather than Work. Earlier
        // persisted aliases or learner proposals can still contain the discarded suffix; neither may
        // re-expand a structurally settled Work. Resolve this at the shared Work authority, not in rankings.
        if (MayContainTournamentScheduleSuffix(analysisTitle))
        {
            var scheduleParts = ParseCanonicalTitleParts(analysisTitle);
            if (HasParsedTournamentBroadcastSchedule(scheduleParts)
                && string.Equals(CompactIdentity(scheduleParts.Work), localWorkKey, StringComparison.OrdinalIgnoreCase))
            {
                context.CanonicalWorkResolutions[resolutionKey] = localWorkKey;
                return localWorkKey;
            }
        }

        // 作品識別 — The shared Work authority rejects shortened aliases and learned
        // promotions for a complete film or numbered standalone edition. Do this before
        // consulting either source, rather than rewriting a damaged identity in a dashboard.
        if (MustPreserveCompleteWorkIdentity(
                analysisTitle, TitleEvidence(analysisTitle, context).Form == "film"))
        {
            context.CanonicalWorkResolutions[resolutionKey] = localWorkKey;
            return localWorkKey;
        }

        var canonical = CanonicalizeLocalWorkKey(localWorkKey, service, context.WorkAliases);
        var evidencedSeriesBoundary = false;
        if (string.Equals(canonical, localWorkKey, StringComparison.OrdinalIgnoreCase)
            && TryResolveEvidenceBackedSeriesWork(analysisTitle, service, context, out var seriesWork))
        {
            canonical = seriesWork;
            evidencedSeriesBoundary = true;
        }
        // 作品識別 — Aliases learned before a structural parser correction may shorten a full Work
        // again. Resolve known on-air boundaries before projecting aliases to any consumer.
        // Ordinary ambiguous work titles retain their evidence-backed aliases unchanged.
        if (canonical.Length < localWorkKey.Length && !evidencedSeriesBoundary)
        {
            var settledParts = ParseCanonicalTitleParts(analysisTitle);
            if (string.Equals(CompactIdentity(settledParts.Work), localWorkKey, StringComparison.OrdinalIgnoreCase)
                && HasSettledAiringWorkBoundary(analysisTitle, settledParts))
                canonical = localWorkKey;
        }
        // 作品識別 — Both evidence-backed aliases and learner promotions must respect the identical complete-Work
        // boundary. A learner must not reinstate a shorter prefix that a locally parsed, structurally
        // inseparable work rejected. Parse only when a shortening candidate actually exists.
        CanonicalTitleParts parsed = default;
        var parsedReady = false;
        bool WouldDiscardCompleteWork(string candidate)
        {
            if (candidate.Length < 3 || candidate.Length >= localWorkKey.Length
                || localWorkKey.Length - candidate.Length < 3
                || !localWorkKey.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!parsedReady)
            {
                parsed = ParseCanonicalTitleParts(analysisTitle);
                parsedReady = true;
            }
            return string.Equals(CompactIdentity(parsed.Work), localWorkKey, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(parsed.Episode)
                && string.IsNullOrWhiteSpace(parsed.Part)
                && string.IsNullOrWhiteSpace(parsed.Subtitle)
                && string.IsNullOrWhiteSpace(parsed.Event)
                && string.IsNullOrWhiteSpace(parsed.Stage)
                && string.IsNullOrWhiteSpace(parsed.BroadcastAttribute);
        }
        // Two distinct selected recordings with a shared strong title boundary establish a
        // recurring programme. Do not veto that evidence with the fallback complete-work rule.
        // Uncorroborated learner-only shortening is still rejected by the same conservative gate.
        var localEvidenceAliasConfirmed = !string.Equals(canonical, localWorkKey, StringComparison.OrdinalIgnoreCase)
            && context.WorkAliases.ContainsKey(new AIrhythmLocalWorkAliasKey(localWorkKey, service));
        if (!localEvidenceAliasConfirmed && !evidencedSeriesBoundary && WouldDiscardCompleteWork(canonical))
            canonical = localWorkKey;

        var promotionKey = context.NormalizationPromotions.Count == 0
            ? string.Empty
            : PromotionLookupKeyCached(analysisTitle, context);
        if (promotionKey.Length > 0
            && context.NormalizationPromotions.TryGetValue(promotionKey, out var promoted)
            && !string.IsNullOrWhiteSpace(promoted))
        {
            // 作品識別 — A same-service parenthesized episode sequence takes precedence. Otherwise a structurally
            // complete Work must not be reduced merely because the learned prefix has survived across observation
            // days.
            var promotionDiscardsSettledWork = false;
            if (promoted.Length < localWorkKey.Length)
            {
                var settledParts = ParseCanonicalTitleParts(analysisTitle);
                promotionDiscardsSettledWork = string.Equals(
                        CompactIdentity(settledParts.Work), localWorkKey, StringComparison.OrdinalIgnoreCase)
                    && HasSettledAiringWorkBoundary(analysisTitle, settledParts);
            }
            if (!(canonical.Length > 0
                && !string.Equals(canonical, promoted, StringComparison.OrdinalIgnoreCase)
                && (localEvidenceAliasConfirmed || evidencedSeriesBoundary || IsCorroboratedParenthesizedWorkSequence(
                    analysisTitle, service, context.NumericLocalValuesByService)))
                && !WouldDiscardCompleteWork(promoted)
                && !promotionDiscardsSettledWork)
                canonical = promoted;
        }
        context.CanonicalWorkResolutions[resolutionKey] = canonical;
        return canonical;
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
        bool UsesExternalCanonicalTitle,
        GenreEvidenceSource GenreEvidenceSource);

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
        AIrhythmEvidenceIdentityContext context,
        bool isRecording = false)
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
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var workKeyMissesBefore = context.LocalWorkKeyCacheMisses;
#endif
        var localWorkKey = LocalWorkKeyCached(analysisTitle, service, context);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterLocalWorkKey = GC.GetAllocatedBytesForCurrentThread();
        var localWorkAllocation = canonicalAfterLocalWorkKey - canonicalAfterPreEvaluation;
        context.CanonicalLocalWorkKeyBytes += localWorkAllocation;
        if (context.LocalWorkKeyCacheMisses != workKeyMissesBefore)
        {
            context.CanonicalLocalWorkKeyMissCalls++;
            context.CanonicalLocalWorkKeyMissBytes += localWorkAllocation;
        }
        else
        {
            context.CanonicalLocalWorkKeyHitCalls++;
            context.CanonicalLocalWorkKeyHitBytes += localWorkAllocation;
        }
#endif
        var canonicalWorkKey = CanonicalWorkIdentity(analysisTitle, service, context, localWorkKey);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterAliasResolution = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalAliasResolutionBytes += canonicalAfterAliasResolution - canonicalAfterLocalWorkKey;
#endif
        var genreDecision = CanonicalBroadcastGenre(title, genre, canonicalWorkKey, service, context, isRecording);
#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
        var canonicalAfterGenre = GC.GetAllocatedBytesForCurrentThread();
        context.CanonicalGenreBytes += canonicalAfterGenre - canonicalAfterAliasResolution;
#endif
        var terms = TitleTokensCached(AiredTitleEvidenceText(analysisTitle), context);
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
            genreDecision.GenreKey,
            service,
            start.Hour,
            CanonicalTimeBand(start.Hour),
            terms,
            isReplay,
            usesExternalCanonicalTitle,
            genreDecision.Source);
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
        var facts = CanonicalEvidenceFacts(item.ProgramTitle, item.Genre, ServiceIdentityOf(item), start, context,
            isRecording: true);
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
        var localWorkKey = LocalWorkKeyCached(analysisTitle, service, context);
        var canonicalWorkKey = CanonicalWorkIdentity(analysisTitle, service, context, localWorkKey);

        var displayTitle = SeriesDisplayTitle(item.ProgramTitle);
        if (IsCorroboratedParenthesizedWorkSequence(item.ProgramTitle, service, context.NumericLocalValuesByService)
            && TryParseTrailingParenthesizedNumber(item.ProgramTitle, out var stem, out _))
        {
            var sequenceLabel = SeriesDisplayTitle(stem);
            if (sequenceLabel.Length > 0)
                displayTitle = sequenceLabel;
        }

        // 録画実績・復旧 — The canonical Work may have separated a leading 【...】 broadcast container.  Dashboard labels
        // must project that same decision instead of re-attaching the container from the raw history title.
        // Accept the remainder only when it reproduces the already-decided canonical Work key exactly; this is
        // presentation projection, not a second identity rule.
        if (canonicalWorkKey.Length >= 3)
        {
            var containerParts = ParseLeadingContainerParts(displayTitle);
            if (!string.IsNullOrWhiteSpace(containerParts.Container))
            {
                var remainderDisplay = SeriesDisplayTitle(containerParts.Remainder);
                if (remainderDisplay.Length > 0
                    && string.Equals(CompactIdentity(remainderDisplay), canonicalWorkKey, StringComparison.OrdinalIgnoreCase))
                    return remainderDisplay;
            }
        }

        // 作品識別 — WorkAliases are part of the common canonical Work Identity.  Dashboard labels must not
        // re-expand an alias-resolved Work back to one evidence row's longer variant.  When the canonical key is
        // an evidence-backed prefix of the human-readable title, project that exact prefix as the display label.
        // This changes presentation only; Work grouping, Score, Continuity and all other canonical facts remain
        // owned by CanonicalWorkKey.
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

            var boundary = i + 1;
            if (boundary + 1 < displayTitle.Length
                && (displayTitle[boundary] is '!' or '！' or '?' or '？')
                && char.IsWhiteSpace(displayTitle[boundary + 1]))
                boundary++;
            var candidate = displayTitle[..boundary]
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

    // A single recording-fact projection supplies both overall and genre rankings. Per-airing
    // EPG genres may differ for the same programme, but a Work must have one category within
    // the dashboard. Preserve the raw genre in every fact for factual provenance.
    private sealed record CanonicalRecordingDashboardRow(
        string WorkKey, string DisplayTitle, string GenreKey, AIrhythmServiceIdentity ServiceIdentity,
        string BroadcastForm);

    private sealed class CanonicalDashboardWorkGroupComparer : IEqualityComparer<(string WorkKey, string BroadcastForm)>
    {
        public bool Equals((string WorkKey, string BroadcastForm) left, (string WorkKey, string BroadcastForm) right)
            => StringComparer.OrdinalIgnoreCase.Equals(left.WorkKey, right.WorkKey)
                && StringComparer.Ordinal.Equals(left.BroadcastForm, right.BroadcastForm);

        public int GetHashCode((string WorkKey, string BroadcastForm) key)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.WorkKey),
                StringComparer.Ordinal.GetHashCode(key.BroadcastForm));
    }
    private static readonly CanonicalDashboardWorkGroupComparer DashboardWorkGroupComparer = new();

    private static CanonicalRecordingDashboardRow[] BuildCanonicalRecordingDashboardRows(
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmEvidenceIdentityContext context)
        => snapshot.History
            .Select(item =>
            {
                var facts = CanonicalEvidenceFacts(item, context);
                return new CanonicalRecordingDashboardRow(
                    facts.WorkKey, CanonicalWorkDisplayTitle(item, context), facts.GenreKey, facts.ServiceIdentity,
                    HistoricalGenreFormCached(item.ProgramTitle, item.Genre, context));
            })
            .Where(x => x.WorkKey.Length > 0 && x.DisplayTitle.Length > 0)
            .ToArray();

    private static string CanonicalRecordingDashboardLabel(
        IEnumerable<CanonicalRecordingDashboardRow> workRows)
    {
        var rows = workRows.ToArray();
        var label = rows.GroupBy(x => x.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key.Length)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .First().Key;
        // Work title and broadcast form are independent canonical facts. The
        // aggregate key already preserves form; do not inject a genre into the title.
        return label;
    }

    internal static IReadOnlyList<AIrhythmCanonicalRecordingGenreAggregate> BuildCanonicalRecordingAggregates(
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmEvidenceIdentityContext context)
    {
        // Film, promotion and factual broadcasts are separate from narrative
        // airings even if a shared normalized Work name was emitted upstream.
        // Each per-airing genre comes from CanonicalEvidenceFacts, never votes.
        var works = BuildCanonicalRecordingDashboardRows(snapshot, context)
            .GroupBy(x => (x.WorkKey, x.BroadcastForm), DashboardWorkGroupComparer)
            .Select(workGroup =>
            {
                // Dashboard is a read-only projection of the SAME per-recording
                // canonical genre used by learning, search and recommendations.
                // Never perform a second majority vote or privilege lexical order.
                var distinctGenres = workGroup.Select(x => x.GenreKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // Display unresolved data as 未分類 rather than misrepresenting
                // it as the genuine EPG genre その他. No preference is learned.
                var genreKey = distinctGenres.Length == 1 && distinctGenres[0].Length > 0
                    ? distinctGenres[0] : "未分類";
                var serviceCounts = workGroup
                    .Where(x => x.ServiceIdentity.IsValid)
                    .GroupBy(x => x.ServiceIdentity)
                    .ToDictionary(x => x.Key, x => x.Count());
                return new AIrhythmCanonicalRecordingWorkAggregate(
                    genreKey, workGroup.Key.WorkKey, CanonicalRecordingDashboardLabel(workGroup),
                    workGroup.Count(), serviceCounts.Count, serviceCounts);
            })
            .Where(x => x.GenreKey.Length > 0)
            .ToArray();

        return works
            .GroupBy(x => x.GenreKey, StringComparer.OrdinalIgnoreCase)
            .Select(genreGroup =>
            {
                var groupedWorks = genreGroup
                    .OrderByDescending(x => x.RecordingCount)
                    .ThenBy(x => x.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new AIrhythmCanonicalRecordingGenreAggregate(
                    genreGroup.Key, groupedWorks.Sum(x => x.RecordingCount), groupedWorks);
            })
            .OrderByDescending(x => x.RecordingCount)
            .ThenBy(x => x.GenreKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<AIrhythmCanonicalRecordingTotalAggregate> BuildCanonicalRecordingWorkTotals(
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmEvidenceIdentityContext context)
        => BuildCanonicalRecordingDashboardRows(snapshot, context)
            .GroupBy(x => (x.WorkKey, x.BroadcastForm), DashboardWorkGroupComparer)
            .Select(workGroup => new AIrhythmCanonicalRecordingTotalAggregate(
                workGroup.Key.WorkKey, CanonicalRecordingDashboardLabel(workGroup), workGroup.Count()))
            .OrderByDescending(x => x.RecordingCount)
            .ThenBy(x => x.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
        var corroboratedGenres = string.Join(";", context.CorroboratedWorkGenres
            .OrderBy(x => x.Key.Work, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key.Form, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key.Service.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key.Work}|{x.Key.Form}|{x.Key.Service}->{x.Value}"));
        var historicalGenres = string.Join(";", context.HistoricalWorkGenres
            .OrderBy(x => x.Key.Work, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key.Service.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key.Work}|{x.Key.Service}->{x.Value.Genre}|{x.Value.Ambiguous}"));
        return $"{numeric}#{aliases}#{externalTitles}#{corroboratedGenres}#{historicalGenres}";
    }

    internal readonly record struct AIrhythmLocalEpisodeIdentity(
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

    private readonly record struct AIrhythmExplicitEpisodeAnalysis(
        bool HasSingleEpisode,
        int EpisodeNumber,
        bool HasTrailingParenthesizedNumber,
        string ParenthesizedStem,
        int ParenthesizedValue);


    private static bool HasExplicitEpisodeMarkerSignal(string title)
    {
        // 作品識別 — Retain this prefilter exactly aligned with AnalyzeExplicitEpisode's accepted syntax. A generic
        // 'e'/'E' is not sufficient: the canonical regex only accepts episode/ep, while '#'/＃/♯ and 話 cover the
        // other deterministic episode forms. Avoiding false positives here prevents ordinary Latin-script titles
        // from paying FormKC + regex costs.
        if (title.IndexOf('#') >= 0 || title.IndexOf('＃') >= 0 || title.IndexOf('♯') >= 0 || title.IndexOf('話') >= 0)
            return true;

        for (var i = 0; i + 1 < title.Length; i++)
        {
            var first = title[i];
            if (first is not ('e' or 'E' or 'ｅ' or 'Ｅ'))
                continue;

            var second = title[i + 1];
            if (second is 'p' or 'P' or 'ｐ' or 'Ｐ')
                return true;
        }

        return false;
    }

    private static AIrhythmExplicitEpisodeAnalysis AnalyzeExplicitEpisode(
        string? title,
        bool includeTrailingParenthesizedStructure = false)
    {
        if (string.IsNullOrWhiteSpace(title))
            return default;

        var hasMarkerSignal = HasExplicitEpisodeMarkerSignal(title);
        var mayHaveTrailingParenthesizedNumber = includeTrailingParenthesizedStructure
            && (title.IndexOf('(') >= 0 || title.IndexOf('（') >= 0);
        if (!hasMarkerSignal && !mayHaveTrailingParenthesizedNumber)
            return default;

        // 作品識別 — Episode structure has one canonical parser. Normalize and strip broadcast-only annotations
        // once, then derive explicit episode and trailing-parenthesized structure from the same text. Evidence
        // corroboration remains a separate decision over this parsed structure; it must not reparse the title.
        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC));
        // 作品識別 — Episode numbers in a later announcement belong to a different Work. Apply the same
        // on-air/title-detail boundary as the canonical Work parser.
        if (TrySplitAiredTitleAndDetail(normalized, out var airedTitle, out _))
            normalized = airedTitle;

        var parenthesizedStem = string.Empty;
        var parenthesizedValue = 0;
        var hasTrailingParenthesizedNumber = false;
        if (mayHaveTrailingParenthesizedNumber)
        {
            var parenthesizedMatch = Regex.Match(normalized.Trim(), @"^(.*?)[（(]\s*([0-9]+)\s*[）)]\s*$");
            if (parenthesizedMatch.Success
                && int.TryParse(parenthesizedMatch.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parenthesizedValue))
            {
                parenthesizedStem = Regex.Replace(parenthesizedMatch.Groups[1].Value, @"\s+", " ").Trim();
                hasTrailingParenthesizedNumber = parenthesizedStem.Length >= 2;
            }
        }

        if (!hasMarkerSignal)
            return new AIrhythmExplicitEpisodeAnalysis(false, 0, hasTrailingParenthesizedNumber, parenthesizedStem, parenthesizedValue);

        var mayHaveRange = normalized.IndexOf('-') >= 0
            || normalized.IndexOf('~') >= 0
            || normalized.IndexOf('〜') >= 0
            || normalized.IndexOf('～') >= 0
            || normalized.IndexOf('–') >= 0
            || normalized.IndexOf('—') >= 0;
        if (mayHaveRange && Regex.IsMatch(
                normalized,
                @"(?:[#＃♯]\s*[0-9]+|(?:episode|ep\.?)\s*[0-9]+|第\s*[0-9]+\s*話|[0-9]+\s*話)\s*[-~〜～–—]\s*(?:(?:[#＃♯]|(?:episode|ep\.?)|第)\s*)?[0-9]+\s*(?:話)?",
                RegexOptions.IgnoreCase))
        {
            return new AIrhythmExplicitEpisodeAnalysis(false, 0, hasTrailingParenthesizedNumber, parenthesizedStem, parenthesizedValue);
        }

        // 作品識別 — `第N回` is structurally ambiguous: it can mean an episode number, but it is also widely used for
        // event/edition ordinals. Deterministic episode identity is limited here to 第N話 / #N / EP N / N話.
        var match = Regex.Match(
            normalized,
            @"(?:第\s*([0-9]+)\s*話|[#＃♯]\s*([0-9]+)|(?:episode|ep\.?)\s*([0-9]+)|([0-9]+)\s*話)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return new AIrhythmExplicitEpisodeAnalysis(false, 0, hasTrailingParenthesizedNumber, parenthesizedStem, parenthesizedValue);

        for (var groupIndex = 1; groupIndex < match.Groups.Count; groupIndex++)
        {
            var group = match.Groups[groupIndex];
            if (group.Success
                && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var episodeNumber)
                && episodeNumber > 0)
            {
                return new AIrhythmExplicitEpisodeAnalysis(true, episodeNumber, hasTrailingParenthesizedNumber, parenthesizedStem, parenthesizedValue);
            }
        }

        return new AIrhythmExplicitEpisodeAnalysis(false, 0, hasTrailingParenthesizedNumber, parenthesizedStem, parenthesizedValue);
    }


    // 録画実績・復旧 — A replacement must identify the *same broadcast content*, not just a related Work.
    // Series episodes use the existing single-episode evidence parser and its snapshot-scoped Work aliases.
    // Only explicitly identified films can use an unnumbered full-title match: daily programmes and specials
    // without an episode number cannot prove an identical re-airing from a shared title alone.
    // 録画実績・復旧 — An episode number identifies one broadcast only within its season/edition.
    // The canonical Work key is for learning continuity and intentionally omits such variants.
    // Keep the proven season as a separate recovery-only identity axis; zero means unknown,
    // not first season. A known season must never match an unspecified one.
    internal readonly record struct AIrhythmRecoveryBroadcastIdentity(
        string Work, string Form, int EpisodeNumber, string CompleteFilmTitle, int SeasonNumber);

    private static bool TryRecoverySeasonNumber(string title, out int seasonNumber)
    {
        seasonNumber = 0;
        // Accept only explicit ordinal season wording; plain year/episode/Part numbers never
        // establish a season. Inspect the entire on-air title, including text after #N,
        // because broadcasters often put "第2期" in their episode description.
        var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC));
        var matches = Regex.Matches(normalized,
            @"(?<![A-Za-z0-9])(?:第\s*(?<ordinal>[1-9][0-9]?)\s*(?:期|シーズン|シリーズ)|(?<ordinal>[1-9][0-9]?)\s*(?:期|シーズン)|(?:シーズン|season)\s*[:：-]?\s*(?<ordinal>[1-9][0-9]?)(?![A-Za-z0-9]))",
            RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            var numberText = match.Groups["ordinal"].Value;
            if (!int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number == 0)
                return false;
            // Contradictory explicit season markers make a broadcast uncertain, not season 0.
            if (seasonNumber != 0 && seasonNumber != number) return false;
            seasonNumber = number;
        }
        return true;
    }

    internal static bool TryCanonicalRecoveryBroadcastIdentity(
        string? title, string? rawGenre, AIrhythmServiceIdentity service,
        AIrhythmEvidenceIdentityContext context, out AIrhythmRecoveryBroadcastIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(title)) return false;
        var form = TitleEvidence(title, context).Form;
        if (form == "promotion") return false;

        var isFilm = form == "film" || NormalizeGenre(rawGenre) == "映画";
        if (isFilm)
        {
            // A numbered film or a combined-episode film listing is not a proven standalone replacement.
            if (AIrhythmDataState.HasContractedRecoveryEpisodeGroup(title)
                || AIrhythmDataState.HasRecoveryEpisodeSyntax(title)
                || AIrhythmDataState.TryRecoveryEpisodeKey(title, out _)
                || AIrhythmDataState.TryRecoveryMultiEpisodeRange(title, out _, out _, out _))
                return false;
            var normalized = StripNonIdentityBroadcastAnnotations(title.Normalize(NormalizationForm.FormKC)).Trim();
            // Remove the classification *label*, never the film's title, sequel number or edition.
            normalized = Regex.Replace(normalized, @"^映画(?=\s|[:：]|[「『])\s*[:：]?\s*", string.Empty);
            var completeTitle = CompactIdentity(normalized);
            if (completeTitle.Length < 5) return false;
            identity = new AIrhythmRecoveryBroadcastIdentity(string.Empty, "film", 0, completeTitle, 0);
            return true;
        }

        // The recovery journal owns this conservative marker gate. Multi-episode ranges, extra markers,
        // unnumbered titles and year/edition numbers must not turn into an assumed #N replacement.
        if (!AIrhythmDataState.TryRecoveryEpisodeKey(title, out var episodeKey)
            || !TryBuildLocalEpisodeIdentityForContinuityCached(title, service, context, out var episodeIdentity))
            return false;
        var hash = episodeKey.LastIndexOf('#');
        if (hash < 0 || !int.TryParse(episodeKey.AsSpan(hash + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var number)
            || number != episodeIdentity.EpisodeNumber || episodeIdentity.WorkKey.Length < 4)
            return false;
        if (!TryRecoverySeasonNumber(title, out var seasonNumber))
            return false;
        identity = new AIrhythmRecoveryBroadcastIdentity(episodeIdentity.WorkKey, "series", number,
            string.Empty, seasonNumber);
        return true;
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

        // 作品識別 — Some broadcasters encode an episode number with programme-specific wording instead of a generic
        // #N / 第N話 marker. Never teach those words to the parser. Instead, only after useful user evidence has
        // already established a shorter Work alias on this same service, accept a numeric suffix when the same
        // non-year number is repeated at least twice in the alias remainder. Example shape: <canonical work> 14
        // ... 14. This is deliberately evidence-backed and cannot bootstrap its own alias because alias
        // construction calls the episode parser with workAliases=null.
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
            @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃♯]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)",
            RegexOptions.IgnoreCase);
        if (!marker.Success) return false;

        var prefix = normalized[..marker.Index].Trim();
        if (prefix.Length == 0) return false;

        // 作品識別 — A remainder such as "第2期 第7話" or "Season 2 EP7" contains an episode marker, but the text before
        // it is only hierarchy metadata. In that shape the leading 【...】 block can still be the work title
        // itself, so never classify it as a broadcast container.
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

        // 作品識別 — ParseLeadingContainerParts normalizes and runs several regexes. A leading-container result is
        // impossible when the raw title contains no Japanese corner bracket at all, so ordinary titles go
        // straight to the canonical title parser.
        if (title.IndexOf('【') < 0)
            return EvidenceSeriesKey(title);

        var containerParts = ParseLeadingContainerParts(title);
        if (string.IsNullOrWhiteSpace(containerParts.Container))
            return EvidenceSeriesKey(title);

        // 作品識別 — A leading 【...】 block is ambiguous: it can be a broadcast container (e.g. a drama slot) or the
        // work title itself. Separate it only when the remainder independently contains a work before an
        // explicit episode marker, or when the caller has already corroborated a parenthesized Work sequence on
        // this exact service.
        var canSeparate = allowLeadingContainerSeparation
            || HasWorkBeforeExplicitEpisodeMarker(containerParts.Remainder)
            || IsExplicitLeadingBroadcastSlot(containerParts.Container, containerParts.Remainder);
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
                && IsCorroboratedParenthesizedWorkSequence(title, service, numericLocalValuesByService))
            {
                var numericWorkKey = LocalWorkIdentityKey(numericStem, allowLeadingContainerSeparation: true);
                if (numericWorkKey.Length >= 3)
                    return numericWorkKey;
            }
            // 作品識別 — A year, installment number, or otherwise uncorroborated suffix remains part of local identity.
            // Work-stem collapse must remain corroborated on this exact service.
            return LocalWorkIdentityKey(title, preserveTrailingNumericDetail: true);
        }
        return LocalWorkIdentityKey(title);
    }

    // 作品識別 — Interpret only an Episode that the canonical title parser already split away from Work. This
    // does not decide if an arbitrary number is an episode.
    private static bool TryGetCanonicalQuotedEpisodeNumber(string episode, out int number)
    {
        number = 0;
        if (episode.Length < 3)
            return false;
        var digits = episode[0] == '(' && episode[^1] == ')'
            ? episode.AsSpan(1, episode.Length - 2)
            : episode.StartsWith("第", StringComparison.Ordinal) && episode.EndsWith("夜", StringComparison.Ordinal)
                ? episode.AsSpan(1, episode.Length - 2)
                : ReadOnlySpan<char>.Empty;
        return digits.Length is >= 1 and <= 3
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number > 0;
    }

    private static bool TryBuildLocalEpisodeIdentity(
        string? title,
        AIrhythmServiceIdentity service,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string>? workAliases,
        IReadOnlyDictionary<string, int[]>? numericEvidenceValuesByService,
        out AIrhythmLocalEpisodeIdentity identity)
    {
        var explicitEpisodeAnalysis = AnalyzeExplicitEpisode(
            title,
            includeTrailingParenthesizedStructure: numericEvidenceValuesByService is not null);
        var parenthesizedEpisodeCorroborated = !explicitEpisodeAnalysis.HasSingleEpisode
            && IsEvidenceCorroboratedParenthesizedEpisode(
                explicitEpisodeAnalysis, service, numericEvidenceValuesByService);
        return TryBuildLocalEpisodeIdentity(
            title,
            service,
            workAliases,
            explicitEpisodeAnalysis,
            parenthesizedEpisodeCorroborated,
            out identity);
    }

    private static bool TryBuildLocalEpisodeIdentity(
        string? title,
        AIrhythmServiceIdentity service,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string>? workAliases,
        AIrhythmExplicitEpisodeAnalysis explicitEpisodeAnalysis,
        bool parenthesizedEpisodeCorroborated,
        out AIrhythmLocalEpisodeIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var isReplay = IsReplayTitle(title);
        if (explicitEpisodeAnalysis.HasSingleEpisode)
        {
            var explicitEpisode = explicitEpisodeAnalysis.EpisodeNumber;
            var workKey = CanonicalizeLocalWorkKey(LocalWorkIdentityKey(title), service, workAliases);
            if (workKey.Length < 3)
                return false;

            identity = new AIrhythmLocalEpisodeIdentity(
                workKey, explicitEpisode, isReplay, service);
            return true;
        }

        // 作品識別 — The canonical title parser owns structurally explicit quoted-subtitle episodes, including a
        // typed (N) and a "第N夜" installment. Consume its Work/Episode projection for continuity, instead of
        // inferring another episode or Work boundary in the learner.
        if (title is not null && (title.IndexOf('「') >= 0 || title.IndexOf('『') >= 0))
        {
            var parsed = ParseCanonicalTitleParts(title);
            if (TryGetCanonicalQuotedEpisodeNumber(parsed.Episode, out var quotedEpisode))
            {
                var quotedWorkKey = CanonicalizeLocalWorkKey(
                    CompactIdentity(parsed.Work), service, workAliases);
                if (quotedWorkKey.Length >= 3)
                {
                    identity = new AIrhythmLocalEpisodeIdentity(
                        quotedWorkKey, quotedEpisode, isReplay, service);
                    return true;
                }
            }
        }

        if (TryGetAliasCorroboratedRepeatedEpisodeNumber(
                title, service, workAliases, out var corroboratedWorkKey, out var corroboratedEpisode))
        {
            identity = new AIrhythmLocalEpisodeIdentity(
                corroboratedWorkKey, corroboratedEpisode, isReplay, service);
            return true;
        }

        // 録画実績・復旧 — A trailing parenthesized number is not an episode by EPG shape alone. Promote it only when
        // useful recording/reservation evidence on this exact service already contains a neighboring number for
        // the same stable stem. This restores genuine episode sequences such as (9)/(10)/(11) without
        // reclassifying split sports broadcasts that have no user evidence.
        if (parenthesizedEpisodeCorroborated)
        {
            var numericWorkKey = CanonicalizeLocalWorkKey(
                LocalWorkIdentityKey(explicitEpisodeAnalysis.ParenthesizedStem, allowLeadingContainerSeparation: true),
                service,
                workAliases);
            if (numericWorkKey.Length >= 3)
            {
                identity = new AIrhythmLocalEpisodeIdentity(
                    numericWorkKey, explicitEpisodeAnalysis.ParenthesizedValue, isReplay, service);
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildLocalEpisodeIdentityForContinuityCached(
        string? title,
        AIrhythmServiceIdentity service,
        AIrhythmEvidenceIdentityContext context,
        out AIrhythmLocalEpisodeIdentity identity)
    {
        var source = title ?? string.Empty;
        var cacheKey = new AIrhythmLocalEpisodeCacheKey(source, service);
        if (context.LocalEpisodeIdentities.TryGetValue(cacheKey, out var cached))
        {
            context.LocalEpisodeIdentityCacheHits++;
            identity = cached.Identity;
            return cached.HasIdentity;
        }

        // 推薦・学習 — Score comparisons use the shared provenance boundary.
        var hasQuotedStructuralEpisodeCandidate = (source.IndexOf('「') >= 0 || source.IndexOf('『') >= 0)
            && (source.IndexOf('(') >= 0 || source.IndexOf('（') >= 0
                || (source.IndexOf('第') >= 0 && source.IndexOf('夜') >= 0));
        var mayNeedEpisodeAnalysis = hasQuotedStructuralEpisodeCandidate || source.IndexOf('#') >= 0
            || source.IndexOf('＃') >= 0 || source.IndexOf('♯') >= 0
            || source.IndexOf('話') >= 0
            || source.IndexOf('回') >= 0
            || source.IndexOf('(') >= 0
            || source.IndexOf('（') >= 0
            || source.Contains("ep", StringComparison.OrdinalIgnoreCase)
            || HasAtLeastTwoAsciiDigitRuns(source);
        if (!mayNeedEpisodeAnalysis)
        {
            identity = default;
            context.LocalEpisodeIdentities[cacheKey] = new AIrhythmLocalEpisodeCacheValue(false, identity);
            context.LocalEpisodeIdentityCacheMisses++;
            return false;
        }

        // 保存・読込 — Only titles with an episode-shaped signal pay the full parser cost. Cache the negative result
        // as well so repeated EPG rows avoid both the prefilter and parser on later hits.
        var explicitEpisodeAnalysis = AnalyzeExplicitEpisode(source, includeTrailingParenthesizedStructure: true);
        var mayHaveAliasCorroboratedRepeatedEpisode = !explicitEpisodeAnalysis.HasSingleEpisode
            && HasAtLeastTwoAsciiDigitRuns(source);
        var mayHaveEvidenceCorroboratedParenthesizedEpisode = !explicitEpisodeAnalysis.HasSingleEpisode
            && IsEvidenceCorroboratedParenthesizedEpisode(
                explicitEpisodeAnalysis, service, context.NumericEvidenceValuesByService);
        if (!explicitEpisodeAnalysis.HasSingleEpisode
            && !mayHaveAliasCorroboratedRepeatedEpisode
            && !mayHaveEvidenceCorroboratedParenthesizedEpisode
            && !hasQuotedStructuralEpisodeCandidate)
        {
            identity = default;
            context.LocalEpisodeIdentities[cacheKey] = new AIrhythmLocalEpisodeCacheValue(false, identity);
            context.LocalEpisodeIdentityCacheMisses++;
            return false;
        }

        var hasIdentity = TryBuildLocalEpisodeIdentity(
            source,
            service,
            context.WorkAliases,
            explicitEpisodeAnalysis,
            mayHaveEvidenceCorroboratedParenthesizedEpisode,
            out identity);
        context.LocalEpisodeIdentities[cacheKey] = new AIrhythmLocalEpisodeCacheValue(hasIdentity, identity);
        context.LocalEpisodeIdentityCacheMisses++;
        return hasIdentity;
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

        // Use the same aired Work boundary before learning a series alias.  A
        // separately advertised work is not evidence of a shared series prefix.
        var normalized = StripNonIdentityBroadcastAnnotations(
            AiredTitleEvidenceText(title).Normalize(NormalizationForm.FormKC)).Trim();
        if (normalized.Length == 0)
            return false;

        // 作品識別 — TvAIr-managed weekday suffix is not programme identity.
        normalized = Regex.Replace(normalized, @"\s*[（(]\s*(?:月|火|水|木|金|土|日)\s*[）)]\s*$", " ").Trim();

        // 作品識別 — Explicit episode markers are already handled by Local Episode Identity. Never create a second
        // title-shortening path for those rows.
        if (Regex.IsMatch(
                normalized,
                @"(?:第\s*[0-9０-９]+\s*(?:話|回)|[#＃♯]\s*[0-9０-９]+|(?:episode|ep\.?)\s*[0-9０-９]+|[0-9０-９]+\s*話)",
                RegexOptions.IgnoreCase))
            return false;

        // A programme headline boundary must be outside a quoted Work. Otherwise
        // punctuation or spaces in an official film/edition title become a false
        // recurring-series prefix and erase sequel/part identity.
        var boundary = -1;
        var quoteDepth = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] is '「' or '『')
            {
                quoteDepth++;
                continue;
            }
            if (normalized[i] is '」' or '』')
            {
                quoteDepth = Math.Max(0, quoteDepth - 1);
                continue;
            }
            if (quoteDepth != 0 || i == 0)
                continue;
            if (normalized[i] is '！' or '!' or '？' or '?' or '▼' or '▽')
            {
                boundary = i;
                strongBoundary = true;
                break;
            }
            if (char.IsWhiteSpace(normalized[i]))
            {
                boundary = i;
                strongBoundary = false;
                break;
            }
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

        // 作品識別 — A longest-common-prefix cut can stop immediately after the Japanese ordinal introducer when
        // every evidence-backed variant continues with a different number, e.g. "...第1部..." / "...第2部...".  A
        // bare terminal "第" is not work identity; remove it only when all observed full keys prove that the next
        // character is numeric.  This keeps the rule evidence-backed and generic instead of introducing a
        // title-specific exception.
        if (common.EndsWith("第", StringComparison.Ordinal)
            && ordered.All(fullKey => fullKey.Length > common.Length && char.IsDigit(fullKey[common.Length])))
        {
            common = common[..^1].TrimEnd();
        }

        // 作品識別 — A stable text prefix must never end inside a numbered chapter/episode marker. Kanji numerals
        // have shared leading characters (第二十章 / 第二十一章), so a character-level LCP can falsely learn "第二十" as
        // part of the programme identity. Accept the boundary only when every observed variant has a *complete*
        // ordinal followed by its explicit episode/chapter unit, not a series/edition label.
        var partialOrdinal = Regex.Match(common, @"第[一二三四五六七八九十百千零〇0-9]+$");
        if (partialOrdinal.Success
            && ordered.All(fullKey =>
                fullKey.Length > partialOrdinal.Index
                && Regex.IsMatch(fullKey[partialOrdinal.Index..],
                    @"^第[一二三四五六七八九十百千零〇0-9]+(?:章|話)(?=$|[^一二三四五六七八九十百千零〇0-9])")))
        {
            common = common[..partialOrdinal.Index].TrimEnd();
        }

        if (common.Length < initialPrefix.Length)
            return string.Empty;

        // 作品識別 — Never collapse to a bare scheduling/container prefix when the evidence-backed variants share a
        // longer stable programme identity.  This keeps e.g. a broadcast slot plus an actual work title intact,
        // without any title-specific deny list.
        return common.Length >= initialPrefix.Length + 4 ? common : initialPrefix;
    }

    private static IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string> BuildLocalWorkAliases(
        AIrhythmRuntimeSnapshot snapshot,
        IReadOnlyDictionary<string, int[]> numericLocalValuesByService)
    {
        var episodesByWorkAndService = new Dictionary<AIrhythmLocalWorkAliasKey, HashSet<int>>();

        void Observe(string? title, AIrhythmServiceIdentity service)
        {
            if (!TryBuildLocalEpisodeIdentity(
                    title,
                    service,
                    workAliases: null,
                    numericEvidenceValuesByService: null,
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

        // 作品識別 — Alias candidates come only from useful user evidence. Parenthesized-number Episode
        // corroboration is intentionally not used while aliases are being learned, preventing a candidate from
        // bootstrapping its own Work alias.
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

                // 作品識別 — Never strip a leading phrase merely because the remaining title is a suffix. Require both forms
                // to be backed by useful user evidence and to carry neighboring episode numbers. Work identity itself
                // may cross services; service difference is handled later by Continuity confidence instead of splitting
                // the work identity.
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

        // 推薦・学習 — For non-episodic recurring programmes, learn only from useful user evidence. The same service
        // must contain at least two distinct evidence-backed title variants that share a stable prefix. EPG
        // repetition alone can never create this alias.
        var recurringPrefixGroups = new Dictionary<(AIrhythmServiceIdentity Service, string Prefix, bool StrongBoundary), HashSet<string>>();
        var exactEvidenceKeysByService = new Dictionary<AIrhythmServiceIdentity, HashSet<string>>();

        void ObserveRecurringPrefix(string? title, AIrhythmServiceIdentity service, string? recordedGenre = null)
        {
            if (!service.IsValid || string.IsNullOrWhiteSpace(title)
                || MustPreserveCompleteWorkIdentity(title, BroadcastForm(title) == "film", recordedGenre))
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
                ObserveRecurringPrefix(item.ProgramTitle, ServiceIdentityOf(item), item.Genre);
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

            // 作品識別 — An exact useful-evidence title on the same service is the strongest local proof that the same
            // text is a complete programme identity instead of only a scheduling prefix. This lets one longer
            // evidence-backed topic/subtitle variant collapse to that exact Work without requiring a second long
            // variant. Otherwise retain the established multi-variant evidence rule and its conservative
            // whitespace-prefix handling.
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

        // 作品識別 — A numbered episode can carry an unquoted story subtitle before its #N marker. A different
        // episode on the SAME service broadcasting the bare series name establishes the Work boundary without
        // any title-specific dictionary. Neither one-off EPG text nor mere string-prefix similarity is
        // sufficient.
        var numberedWorkEpisodes = new Dictionary<AIrhythmLocalWorkAliasKey, HashSet<int>>();
        var numberedWorkText = new Dictionary<AIrhythmLocalWorkAliasKey, string>();
        // EPG is an application target; only completed recordings and useful reservations prove a series.
        var selectedNumberedWorkEpisodes = new Dictionary<AIrhythmLocalWorkAliasKey, HashSet<int>>();
        var selectedNumberedWorkText = new Dictionary<AIrhythmLocalWorkAliasKey, string>();

        void ObserveNumberedEpisode(string? title, AIrhythmServiceIdentity service, bool selectedEvidence)
        {
            if (!service.IsValid || string.IsNullOrWhiteSpace(title)
                || (title.IndexOf('#') < 0 && title.IndexOf('＃') < 0 && title.IndexOf('♯') < 0)
                || BroadcastForm(title) is "promotion" or "film")
                return;
            var parts = ParseCanonicalTitleParts(title);
            var marker = Regex.Match(parts.Episode, @"^[#＃♯]\s*(?<number>[0-9]{1,3})$",
                RegexOptions.CultureInvariant);
            if (!marker.Success || !int.TryParse(marker.Groups["number"].Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out var episodeNumber)
                || episodeNumber <= 0 || episodeNumber > 999)
                return;
            var rawWork = NormalizeCanonicalWhitespace(parts.Work).Trim();
            if (rawWork.Count(char.IsLetterOrDigit) < 4) return;
            var workKey = CompactIdentity(rawWork);
            if (workKey.Length < 4) return;
            var key = new AIrhythmLocalWorkAliasKey(workKey, service);
            if (!numberedWorkEpisodes.TryGetValue(key, out var episodes))
                numberedWorkEpisodes[key] = episodes = new HashSet<int>();
            episodes.Add(episodeNumber);
            numberedWorkText.TryAdd(key, rawWork);
            if (selectedEvidence)
            {
                if (!selectedNumberedWorkEpisodes.TryGetValue(key, out var selectedEpisodes))
                    selectedNumberedWorkEpisodes[key] = selectedEpisodes = new HashSet<int>();
                selectedEpisodes.Add(episodeNumber);
                selectedNumberedWorkText.TryAdd(key, rawWork);
            }
        }

        foreach (var item in snapshot.Events)
            ObserveNumberedEpisode(item.Title, ServiceIdentityOf(item), selectedEvidence: false);
        foreach (var item in snapshot.History)
            if (AIrhythmDataState.IsUsefulHistory(item))
                ObserveNumberedEpisode(item.ProgramTitle, ServiceIdentityOf(item), selectedEvidence: true);
        foreach (var item in snapshot.Reservations)
            if (AIrhythmDataState.IsUsefulReservation(item) && ReservationEvidenceWeight(item) > 0)
                ObserveNumberedEpisode(item.ProgramTitle, ServiceIdentityOf(item), selectedEvidence: true);

        foreach (var (longKey, longEpisodes) in numberedWorkEpisodes)
        {
            if (aliases.ContainsKey(longKey)) continue;
            var fullTitle = numberedWorkText[longKey];
            string? matchedShortKey = null;
            var ambiguous = false;
            for (var space = fullTitle.IndexOf(' '); space >= 0;
                 space = fullTitle.IndexOf(' ', space + 1))
            {
                var baseTitle = fullTitle[..space];
                var trailingText = fullTitle[(space + 1)..];
                if (baseTitle.Count(char.IsLetterOrDigit) < 4
                    || trailingText.Count(char.IsLetterOrDigit) < 8)
                    continue;
                var shortKey = CompactIdentity(baseTitle);
                if (shortKey.Length < 4 || shortKey.Length >= longKey.WorkKey.Length) continue;
                var shortAliasKey = new AIrhythmLocalWorkAliasKey(shortKey, longKey.ServiceIdentity);
                if (!numberedWorkEpisodes.TryGetValue(shortAliasKey, out var shortEpisodes)
                    || !shortEpisodes.Any(shortEpisode => longEpisodes.Any(longEpisode =>
                        longEpisode != shortEpisode && Math.Abs(longEpisode - shortEpisode) <= 2)))
                    continue;
                if (matchedShortKey is not null && !string.Equals(matchedShortKey, shortKey, StringComparison.OrdinalIgnoreCase))
                {
                    ambiguous = true;
                    break;
                }
                matchedShortKey = shortKey;
            }
            if (!ambiguous && matchedShortKey is not null)
                aliases[longKey] = matchedShortKey;
        }

        // A pair of independently selected neighboring #N episodes on the same service may reveal a
        // stable multi-word Work even when neither recorded title is the bare series name. EPG-only
        // variants cannot teach this boundary. Project verified aliases to every current episode through
        // the existing WorkAliases authority, shared by scoring, learning, dashboard and continuity.
        foreach (var serviceEvidence in selectedNumberedWorkEpisodes.GroupBy(x => x.Key.ServiceIdentity))
        {
            var selected = serviceEvidence.ToArray();
            var supportedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < selected.Length; i++)
            {
                for (var j = i + 1; j < selected.Length; j++)
                {
                    if (!selected[i].Value.Any(first => selected[j].Value.Any(second =>
                            first != second && Math.Abs(first - second) <= 2)))
                        continue;
                    var prefix = SharedNumberedProgrammePrefix(
                        selectedNumberedWorkText[selected[i].Key],
                        selectedNumberedWorkText[selected[j].Key]);
                    if (prefix.Length > 0)
                        supportedPrefixes.Add(prefix);
                }
            }
            // Where several series share an opening word, use the longest complete supported title.
            var orderedPrefixes = supportedPrefixes.OrderByDescending(x => x.Length).ToArray();
            foreach (var entry in numberedWorkText)
            {
                if (entry.Key.ServiceIdentity != serviceEvidence.Key || aliases.ContainsKey(entry.Key))
                    continue;
                var matched = orderedPrefixes.FirstOrDefault(prefix =>
                    MatchesEvidenceBackedNumberedSeriesPrefix(entry.Value, prefix));
                if (matched is null) continue;
                var canonical = CompactIdentity(matched);
                if (canonical.Length >= 6 && canonical.Length < entry.Key.WorkKey.Length)
                    aliases[entry.Key] = canonical;
            }
        }

        return aliases;
    }

    // Only independently observed, distinct #N episodes enter this common title boundary.
    // Entire whitespace-delimited words are required; never infer an arbitrary character prefix.
    private static string SharedNumberedProgrammePrefix(string firstWork, string secondWork)
    {
        var left = NormalizeCanonicalWhitespace(firstWork.Normalize(NormalizationForm.FormKC)).Trim();
        var right = NormalizeCanonicalWhitespace(secondWork.Normalize(NormalizationForm.FormKC)).Trim();
        var common = 0;
        while (common < Math.Min(left.Length, right.Length)
            && char.ToUpperInvariant(left[common]) == char.ToUpperInvariant(right[common]))
            common++;
        for (var end = common; end > 0;)
        {
            var boundary = left.LastIndexOf(' ', end - 1);
            if (boundary < 0) break;
            var prefix = left[..boundary].TrimEnd();
            if (MatchesEvidenceBackedNumberedSeriesPrefix(left, prefix)
                && MatchesEvidenceBackedNumberedSeriesPrefix(right, prefix))
                return prefix;
            end = boundary;
        }
        return string.Empty;
    }

    private static bool MatchesEvidenceBackedNumberedSeriesPrefix(string title, string prefix)
    {
        if (prefix.Length < 6 || prefix.Count(char.IsLetterOrDigit) < 6
            || !prefix.Any(char.IsLetter)
            || LooksLikeBroadcastContainerLabel(prefix)
            || title.Length <= prefix.Length + 1
            || !title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || title[prefix.Length] != ' ')
            return false;
        return title[(prefix.Length + 1)..].Count(char.IsLetterOrDigit) >= 8;
    }

    // 録画実績・復旧 — One scoring snapshot uses one observation of keyword-rule provenance and reservation
    // history. Never rebuild these indices separately for local continuity, base score and learned
    // preference: all projections must see the same matching source record.
    private readonly record struct AIrhythmScoringRuleIndices(
        IReadOnlyDictionary<int, TvAirKeywordRuleDto> EnabledById,
        IReadOnlyDictionary<int, TvAirKeywordRuleDto> SearchCastById);

    private static AIrhythmScoringRuleIndices BuildScoringRuleIndices(
        IReadOnlyList<TvAirKeywordRuleDto> rules)
    {
        var enabled = new Dictionary<int, TvAirKeywordRuleDto>();
        var searchCast = new Dictionary<int, TvAirKeywordRuleDto>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            // 推薦・学習 — The first enabled occurrence wins for base evidence; the first enabled SearchCast occurrence
            // independently wins for cast intent. Those can differ when duplicate rule IDs have different SearchCast
            // flags.
            enabled.TryAdd(rule.RuleId, rule);
            if (rule.SearchCast)
                searchCast.TryAdd(rule.RuleId, rule);
        }
        return new(enabled, searchCast);
    }

    private static Dictionary<string, TvAirReservationDto> BuildLatestReservationIndex(
        IReadOnlyList<TvAirReservationDto> records)
    {
        var index = new Dictionary<string, TvAirReservationDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var reservation in records)
        {
            if (string.IsNullOrWhiteSpace(reservation.ReservationId))
                continue;
            // 処理条件 — Strictly greater preserves the first row on equal timestamps, matching stable
            // OrderByDescending(...).First() within each case-insensitive group.
            if (!index.TryGetValue(reservation.ReservationId, out var current)
                || reservation.UpdatedAt > current.UpdatedAt)
                index[reservation.ReservationId] = reservation;
        }
        return index;
    }

    private readonly record struct AIrhythmLocalEvidenceState(
        IReadOnlyDictionary<string, AIrhythmLocalWorkAggregate> WorkAggregates,
        IReadOnlyDictionary<string, AIrhythmLocalContinuitySource[]> ContinuitySources);

    private static AIrhythmLocalEvidenceState BuildLocalEvidenceState(
        AIrhythmRuntimeSnapshot snapshot,
        AIrhythmEvidenceIdentityContext identityContext,
        IReadOnlyDictionary<int, TvAirKeywordRuleDto> rulesById,
        IReadOnlyDictionary<string, TvAirReservationDto> reservationById)
    {
        var aggregates = new Dictionary<string, AIrhythmLocalWorkAggregate>(StringComparer.OrdinalIgnoreCase);
        var continuitySources = new Dictionary<string, List<AIrhythmLocalContinuitySource>>(StringComparer.OrdinalIgnoreCase);
        var seenCausalWorkEvidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            AIrhythmCanonicalEvidenceFacts canonicalFacts,
            bool isRecording,
            bool isAutomatedReservation,
            bool allowWorkEvidence,
            string causalKey,
            string sourceKind)
        {
            var hasEpisodeSource = TryBuildLocalEpisodeIdentityForContinuityCached(
                title,
                service,
                identityContext,
                out var sourceIdentity);
            var workKey = hasEpisodeSource
                ? sourceIdentity.WorkKey
                : !canonicalFacts.UsesExternalCanonicalTitle
                    ? canonicalFacts.WorkKey
                    : CanonicalizeLocalWorkKey(
                        LocalWorkKeyCached(title, service, identityContext),
                        service,
                        identityContext.WorkAliases);

            if (!allowWorkEvidence || workKey.Length < 3)
                return;

            var firstCausalObservation = seenCausalWorkEvidence.Add(causalKey);
            if (firstCausalObservation)
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

            // Continuity keeps episode positions without creating more preference votes:
            // one originating automatic rule contributes only one work-activity fact.
            if (hasEpisodeSource)
                AddContinuitySource(sourceIdentity, sourceKind, when);
        }

        foreach (var item in snapshot.History)
        {
            if (!AIrhythmDataState.IsUsefulHistory(item))
                continue;
            var facts = CanonicalEvidenceFacts(item, identityContext);
            var sourceReservation = FindOriginatingReservation(item, reservationById);
            var axes = BehaviorEvidenceAxesForRecording(item, reservationById, rulesById);
            var automated = sourceReservation is not null
                ? sourceReservation.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule
                : AIrhythmDataState.TryGetRecordedPreferenceOrigin(item, out var persistedIntent, out _)
                    && persistedIntent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule;
            var causalKey = AIrhythmDataState.RecordingEvidenceCausalIdentity(item);
            AddEvidence(
                item.ProgramTitle,
                facts.ServiceIdentity,
                item.ActualStart ?? item.Start,
                facts,
                isRecording: true,
                isAutomatedReservation: automated,
                allowWorkEvidence: axes.Work,
                causalKey: causalKey,
                sourceKind: automated ? "automated_recording" : "recording");
        }

        foreach (var item in snapshot.Reservations)
        {
            if (!AIrhythmDataState.IsUsefulReservation(item) || ReservationEvidenceWeight(item) <= 0)
                continue;
            var isAutomated = item.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule;
            var facts = CanonicalEvidenceFacts(item, identityContext);
            var axes = BehaviorEvidenceAxesForReservation(item, rulesById);
            var causalKey = isAutomated
                ? $"{AIrhythmPreferenceOriginPolicy.AutomatedEvidenceCohort(item)}|work:{facts.WorkKey}"
                : AIrhythmDataState.ReservationEvidenceCausalIdentity(item);
            AddEvidence(
                item.ProgramTitle,
                facts.ServiceIdentity,
                item.Start,
                facts,
                isRecording: false,
                isAutomatedReservation: isAutomated,
                allowWorkEvidence: axes.Work,
                causalKey: causalKey,
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
        IReadOnlyDictionary<string, int[]> numericLocalValuesByService,
        IReadOnlyDictionary<string, AIrhythmLocalContinuitySource[]> continuitySources,
        IReadOnlyDictionary<AIrhythmLocalWorkAliasKey, string> localWorkAliases,
        IReadOnlyDictionary<string, int[]> numericEvidenceValuesByService)
    {
        var replay = IsReplayTitle(candidate.Title);
        var service = ServiceIdentityOf(candidate);
        var hasIdentity = TryBuildLocalEpisodeIdentity(
            candidate.Title,
            service,
            localWorkAliases,
            numericEvidenceValuesByService,
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
        IReadOnlyDictionary<string, AIrhythmLocalContinuitySource[]> continuitySources,
        AIrhythmEvidenceIdentityContext identityContext,
        AIrhythmCanonicalEvidenceFacts facts)
    {
        // 外部情報 — External canonical-title substitution can intentionally differ from the local continuity
        // identity, so those candidates use the full continuity evaluation path.
        if (facts.UsesExternalCanonicalTitle)
        {
            return EvaluateLocalContinuity(
                candidate,
                identityContext.NumericLocalValuesByService,
                continuitySources,
                identityContext.WorkAliases,
                identityContext.NumericEvidenceValuesByService);
        }

        // 保存・読込 — Episode parsing is invariant for the same title + service inside one immutable snapshot. Cache
        // both positive and negative parse results so repeated EPG rows never rerun the same normalization /
        // regex pipeline. Continuity itself remains time-sensitive and is evaluated below for every candidate
        // against source timestamps.
        if (!TryBuildLocalEpisodeIdentityForContinuityCached(
                candidate.Title,
                facts.ServiceIdentity,
                identityContext,
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
            // 録画実績・復旧 — Weak means only one useful recording/reservation occurrence. Retain it as evidence, but a
            // single occurrence does not move the product ranking.
            _ => 0.0d
        };

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
            CachedCalibrationCandidates = new();
        }
    }

    internal static bool TryStagePreChoiceCalibrationCandidate(int networkId, int transportStreamId, int serviceId, int eventNumber)
    {
        lock (ScoreResultCacheGate)
        {
            var now = DateTimeOffset.Now;
            foreach (var stale in PendingPreChoiceCalibrationCandidates
                         .Where(x => now - x.Value.CapturedAt > PendingPreChoiceCalibrationLifetime)
                         .Select(x => x.Key)
                         .ToArray())
                PendingPreChoiceCalibrationCandidates.Remove(stale);

            var scored = CachedScoreRecommendations;
            if (scored is null)
                return false;

            foreach (var recommendation in scored)
            {
                var identity = recommendation.EventIdentity;
                if (identity is null
                    || identity.NetworkId != networkId
                    || identity.TransportStreamId != transportStreamId
                    || identity.ServiceId != serviceId
                    || identity.EventNumber != eventNumber)
                    continue;

                var key = CalibrationEventKey(networkId, transportStreamId, serviceId, eventNumber, identity.Start);
                if (!CachedCalibrationCandidates.TryGetValue(key, out var candidate))
                    return false;

                PendingPreChoiceCalibrationCandidates[key] = (candidate, now);
                return true;
            }
            return false;
        }
    }

    internal static bool TryGetPreChoiceCalibrationCandidate(TvAirReservationDto reservation, out AIrhythmEvidenceCalibrationCandidate candidate)
    {
        candidate = default;
        var key = CalibrationEventKey(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId, reservation.EventNumber, reservation.Start);
        lock (ScoreResultCacheGate)
        {
            var now = DateTimeOffset.Now;
            foreach (var stale in PendingPreChoiceCalibrationCandidates
                         .Where(x => now - x.Value.CapturedAt > PendingPreChoiceCalibrationLifetime)
                         .Select(x => x.Key)
                         .ToArray())
                PendingPreChoiceCalibrationCandidates.Remove(stale);

            if (PendingPreChoiceCalibrationCandidates.Remove(key, out var staged)
                && now - staged.CapturedAt <= PendingPreChoiceCalibrationLifetime)
            {
                candidate = staged.Candidate;
                return true;
            }
            return CachedCalibrationCandidates.TryGetValue(key, out candidate);
        }
    }

    private static AIrhythmCalibrationEventKey CalibrationEventKey(int networkId, int transportStreamId, int serviceId, int eventNumber, DateTimeOffset start)
        => new(networkId, transportStreamId, serviceId, eventNumber, start.UtcDateTime.Ticks);

    private readonly record struct AIrhythmReservedWorkKey(string WorkKey, AIrhythmServiceIdentity ServiceIdentity)
    {
        public bool Equals(AIrhythmReservedWorkKey other)
            => ServiceIdentity.Equals(other.ServiceIdentity)
                && string.Equals(WorkKey, other.WorkKey, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode()
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(WorkKey ?? string.Empty), ServiceIdentity);
    }

}
