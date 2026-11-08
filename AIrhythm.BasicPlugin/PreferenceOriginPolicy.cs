namespace AIrhythm.BasicPlugin;

// 推薦・学習 — The reservation intent and its Host source jointly determine whether a programme
// represents an explicit preference. Program-time-slot and system recordings are factual recordings,
// not votes for the programme that happened to occupy the slot.
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

    // Missing Host provenance is not evidence of an explicit selection.
    internal static bool AllowsRecordingPreference(bool known, TvAirReservationIntent intent, string? source)
        => known && !IsExcludedReservation(intent, source)
            && (intent != TvAirReservationIntent.Unspecified || !string.IsNullOrWhiteSpace(source));
}
