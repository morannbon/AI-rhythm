using TvAIrPlugin;
using TvAIrPlugin.Runtime;

namespace AIrhythm.BasicPlugin;

#if AIRHYTHM_DEVELOPER_DIAGNOSTICS
internal static partial class AIrhythmDataState
{
    private static void LogReserveRequest(string? interactionId, int networkId, int transportStreamId, int serviceId, int eventNumber)
        => WriteDeveloperLog($"reserve request interactionId={interactionId ?? string.Empty} event={networkId}:{transportStreamId}:{serviceId}:{eventNumber}");

    private static void LogReserveResult(string? interactionId, int networkId, int transportStreamId, int serviceId, int eventNumber, bool success, string message)
        => WriteDeveloperLog(success
            ? $"reserve result=OK interactionId={interactionId ?? string.Empty} event={networkId}:{transportStreamId}:{serviceId}:{eventNumber}"
            : $"reserve result=ERROR interactionId={interactionId ?? string.Empty} event={networkId}:{transportStreamId}:{serviceId}:{eventNumber} message={message}");

    private static void LogSnapshotInvalidated(string eventType)
        => WriteDeveloperLog($"snapshot invalidated event={eventType}");

    private static void LogExternalLookupCapability(TvAirExternalLookupCapabilityDto? capability)
    {
        if (capability is null)
        {
            WriteDeveloperLog("external lookup capability result=UNAVAILABLE");
            return;
        }
        var providers = string.Join(",", capability.Providers.Select(provider =>
            $"{provider.ProviderId}:[{string.Join("/", provider.Operations)}]"));
        WriteDeveloperLog($"external lookup capability result=OK declared={capability.PluginDeclaredPermission} hostAllowed={capability.UserAllowed} available={capability.Available} providers={capability.Providers.Count} contracts=[{providers}]");
    }

    private static void LogExternalLookupResult(string providerId, string operation, TvAirExternalLookupResultCode code, int evidenceCount, bool localFallback)
        => WriteDeveloperLog($"external lookup provider={providerId} operation={operation} result={code} evidence={evidenceCount} localFallback={localFallback}");

    private static void ReportDeveloperSnapshot(ITvAirPluginRuntimeContext context, AIrhythmRuntimeSnapshot snapshot)
    {
        var d = snapshot.Diagnostics;
        try
        {
            context.Logs.Write(new TvAirLogWriteDto
            {
                Level = "Info",
                Category = "AI-rhythm",
                Message = $"snapshot result={(snapshot.Ready ? "OK" : "ERROR")} program={d.ProgramAccepted}/{d.ProgramRaw} reservations={d.ReservationAccepted}/{d.ReservationRaw} history={d.HistoryAccepted}/{d.HistoryRaw} channels={d.ChannelAccepted}/{d.ChannelRaw} failed={string.Join(",", d.FailedStages)}"
            });

            var externalNeed = AIrhythmRecommendationEngine.SummarizeExternalEvidenceNeeds(snapshot.Events);
            var reasons = string.Join(",", externalNeed.Reasons.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"));
            context.Logs.Write(new TvAirLogWriteDto
            {
                Level = "Info",
                Category = "AI-rhythm",
                Message = $"external evidence policy=local_first_structural_ambiguity_v3 candidates={externalNeed.Needed}/{externalNeed.Total} reasons=[{reasons}] providerLookup=capability_gated"
            });
        }
        catch { }
    }

    internal static void WriteDeveloperLog(string message)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Gate) context = _runtimeContext;
        if (context is null) return;
        try
        {
            context.Logs.Write(new TvAirLogWriteDto
            {
                Level = "Info",
                Category = "AI-rhythm",
                Message = message
            });
        }
        catch { }
    }
}
#endif
