namespace AIrhythm.BasicPlugin;

// All AI-rhythm aggregates and learning use verified original reservation intent/source.
// Program-time-slot, system and unverified recordings contribute to no product factor.
internal static class AIrhythmPreferenceOriginPolicy
{
    internal const double IncidentalContributorWeight = 0.25d;

    internal static bool IsProgramTimeSlot(TvAirReservationIntent intent, string? source)
        => intent == TvAirReservationIntent.ProgramTimeSlot
            || string.Equals(source, "Program", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "ProgramTimeSlot", StringComparison.OrdinalIgnoreCase);

    internal static bool IsExcludedReservation(TvAirReservationIntent intent, string? source)
        => intent == TvAirReservationIntent.System
            || string.Equals(source, "System", StringComparison.OrdinalIgnoreCase)
            || IsProgramTimeSlot(intent, source);

    internal static bool IsExcludedReservation(TvAirReservationDto reservation)
        => IsExcludedReservation(reservation.Intent, reservation.Source);

    // Unknown provenance cannot prove that the recording was not caused by a time-slot reservation.
    // All AI-rhythm projections, including factual totals, share this admission rule.
    internal static bool AllowsRecordingPreference(bool known, TvAirReservationIntent intent, string? source)
        => known && !IsExcludedReservation(intent, source)
            && (intent != TvAirReservationIntent.Unspecified || !string.IsNullOrWhiteSpace(source));

    internal static bool AllowsReservation(TvAirReservationDto reservation)
        => !IsExcludedReservation(reservation)
            && (reservation.Intent != TvAirReservationIntent.Unspecified
                || !string.IsNullOrWhiteSpace(reservation.Source));

    // A rule is one user intent. Its generated broadcasts are facts, not independent
    // user decisions. The rule's weak evidence is divided across its reservations.
    internal const double AutomatedRuleIntentWeight = 0.25d;

    internal static bool IsAutomatedReservation(TvAirReservationDto reservation)
        => reservation.Intent is TvAirReservationIntent.AutomaticSearch or TvAirReservationIntent.KeywordRule;

    internal static string AutomatedEvidenceCohort(TvAirReservationDto reservation)
    {
        if (!IsAutomatedReservation(reservation)) return string.Empty;
        if (reservation.SourceRuleId is int ruleId && ruleId > 0)
            return $"rule:{(int)reservation.Intent}:{ruleId}";
        // Missing rule provenance must never make every generated airing an independent choice.
        return $"unattributed:{(int)reservation.Intent}:{(reservation.Source ?? string.Empty).Trim().ToUpperInvariant()}";
    }

    internal static IReadOnlyDictionary<string, double> ReservationEvidenceScales(
        IReadOnlyList<TvAirReservationDto> reservations)
    {
        var eligible = reservations.Where(AllowsReservation)
            .Where(x => !string.IsNullOrWhiteSpace(x.ReservationId))
            .GroupBy(x => x.ReservationId, StringComparer.Ordinal)
            .Select(x => x.First()).ToArray();
        var counts = eligible.Where(IsAutomatedReservation)
            .GroupBy(AutomatedEvidenceCohort, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        return eligible.ToDictionary(x => x.ReservationId,
            x => IsAutomatedReservation(x)
                ? AutomatedRuleIntentWeight / counts[AutomatedEvidenceCohort(x)]
                : 1.0d,
            StringComparer.Ordinal);
    }
}
