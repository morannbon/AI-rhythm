namespace AIrhythm.BasicPlugin;

internal static partial class AIrhythmRecommendationEngine
{
    // 処理条件 — One canonical time interpretation for the discovery shelf and natural-language search. The
    // broadcast-night window follows a local 18:00–04:00 cycle, including the early-morning portion of the
    // previous evening. End is always exclusive.
    private enum RecommendationTimeIntent
    {
        None,
        Tonight,
        Today,
        Soon,
        Bedtime
    }

    private readonly record struct RecommendationTimeWindow(DateTimeOffset Start, DateTimeOffset EndExclusive)
    {
        public bool Contains(DateTimeOffset programmeStart)
            => programmeStart >= Start && programmeStart < EndExclusive;
    }

    private static RecommendationTimeIntent ResolveRecommendationTimeIntent(string query)
    {
        // 処理条件 — More specific wording wins if several time expressions are present.
        if (ContainsAny(query, "寝る前")) return RecommendationTimeIntent.Bedtime;
        if (ContainsAny(query, "今夜")) return RecommendationTimeIntent.Tonight;
        if (ContainsAny(query, "今日")) return RecommendationTimeIntent.Today;
        if (ContainsAny(query, "これから")) return RecommendationTimeIntent.Soon;
        return RecommendationTimeIntent.None;
    }

    private static RecommendationTimeWindow GetRecommendationTimeWindow(
        DateTimeOffset now,
        RecommendationTimeIntent intent)
    {
        var day = now.Date;
        switch (intent)
        {
            case RecommendationTimeIntent.Tonight:
            case RecommendationTimeIntent.Bedtime:
            {
                // 処理条件 — 00:00–03:59 belongs to the night that started yesterday.
                var previousNight = now.TimeOfDay < TimeSpan.FromHours(4);
                var nightStart = new DateTimeOffset(day.AddDays(previousNight ? -1 : 0).AddHours(18), now.Offset);
                var nightEnd = new DateTimeOffset(day.AddDays(previousNight ? 0 : 1).AddHours(4), now.Offset);
                return new RecommendationTimeWindow(now > nightStart ? now : nightStart, nightEnd);
            }
            case RecommendationTimeIntent.Today:
                // 処理条件 — Today's remaining calendar date, not the next broadcast night.
                return new RecommendationTimeWindow(now, new DateTimeOffset(day.AddDays(1), now.Offset));
            case RecommendationTimeIntent.Soon:
                // 処理条件 — A near-future request has its own rolling window, independent of midnight.
                return new RecommendationTimeWindow(now, now.AddHours(6));
            default:
                throw new ArgumentOutOfRangeException(nameof(intent));
        }
    }
}
