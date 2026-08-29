# TvAIr Plugin SDK 1.1.3 Service Identity Contract

## Purpose

TvAIr treats a broadcast service (station) as a stable service identity plus mutable display metadata.
This contract applies uniformly to Host reservations, recording history, program-guide projection,
content discovery, viewer/service metadata, typed events, and plugin-facing APIs.

## Stable identity

The canonical service identity is the exact triplet:

- `NetworkId`
- `TransportStreamId`
- `ServiceId`

Plugins MUST use all three values when they need a durable station/service key, join key, learning key,
cache key, history aggregation key, or equality check.

`ServiceName` MUST NOT be used as a station/service identity key.

## ServiceName semantics

`ServiceName` is mutable display metadata for the service identity. A broadcaster may rename a station
without changing the NID/TSID/SID triplet.

For current Host projections, TvAIr resolves `ServiceName` from the current channel metadata by exact
NID/TSID/SID match. If the identity can no longer be resolved (for example historical data for a removed
service), TvAIr may fall back to the name snapshot stored with that historical record.

Therefore:

- the same NID/TSID/SID may be returned with a different `ServiceName` after a station rename;
- a `ServiceName` change alone does not mean a different station;
- plugins must not split history, learning, statistics, or caches solely because `ServiceName` changed;
- plugins should render the `ServiceName` supplied by the current Host projection as the current label.

## Query semantics

Where a Host API exposes a `ServiceName` text filter, matching is against the same current resolved
`ServiceName` used in returned DTOs. Identity filtering must use NID/TSID/SID where those fields are
available.

## Content discovery

`TvAirAvailableContentDto` in SDK 1.1.3 exposes `NetworkId`, `TransportStreamId`, and `ServiceId` in
addition to `ServiceName`, so live and recorded discovery items can be grouped without using station name
text as identity.

## Typed events

Reservation and recording typed-event projections retain NID/TSID/SID as identity and expose the current
resolved `ServiceName` at dispatch time when the identity is resolvable. Historical stored names are only
fallback display metadata.

## Reservation planning and chain queries

Reservation preview and chain-candidate filtering use the same exact service identity. SDK 1.1.3 exposes
`NetworkId`, `TransportStreamId`, and `ServiceId` together on these requests. A plugin must not send or
interpret a SID-only station filter. If an optional identity filter is used, all three components must be
supplied. This affects planning/projection only; final allocation and recording execution remain Host-owned.

## Legacy DTO surfaces

Older SDK surface types that expose a station label also carry NID/TSID/SID in SDK 1.1.3 where the type can
represent a station identity. `ServiceName` on those types follows the same mutable-display-label rule and
must not be promoted back into an identity key by plugin code.

## Compatibility

Older Host data may contain legacy station selections that were stored by SID alone. The Host may migrate
or compatibly resolve those records only when the current channel metadata identifies one service
unambiguously. If a legacy station selection is ambiguous or temporarily unresolvable, the Host must not
guess another station and must not use that uncertainty as authority to delete an already-scheduled
reservation. New automatic projections remain suppressed until identity is resolved or the user edits the
rule. New plugin code must not introduce SID-only service identity keys.
