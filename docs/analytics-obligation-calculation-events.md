# Analytics obligation calculation events

Obligation calculation messages use the [shared analytics envelope and transport](analytics-compliance-declaration-events.md). They report shadow calculations for a submitter organisation and obligation year; they do not replace public obligation reads from the PRN Common Backend.

## Consumer contract

| Field | Calculation contract |
| --- | --- |
| `entity` | `obligation_calculation` |
| `entityId` | `oblcalc_<calculation Mongo ObjectId>`, stable across versions for the submitter and obligation year |
| `schemaVersion` | `obligation_calculation_v1.0` |
| `version` | The newly saved calculation's incrementing entity version |
| `actor` | `system:waste-obligations` |
| `piiKeyRef`, `deletedReason` | `null` |
| `correlationId` | The originating request ID when available, otherwise the instruction's scheduled-run ID; retained across retries |
| `occurredAt` | Calculation completion time |
| `recordedAt` | Outbox recording time, no earlier than `occurredAt` |

Both envelope timestamps use UTC ISO-8601 with millisecond precision, for example `2026-01-02T03:04:06.123Z`.

| Operation | Event type | `before` | `after` |
| --- | --- | --- | --- |
| `create` | `obligation_calculation.created` | `null` | First saved calculation |
| `update` | `obligation_calculation.updated` | Actual previous saved calculation | New saved calculation |

Both snapshots follow the embedded [calculation v1.0 schema](../src/Api/Schemas/ObligationCalculation/obligation-calculation.v1.0.schema.json). The raw payload schema version is `v1.0`; it is distinct from the entity version. Payload IDs are unprefixed Mongo ObjectIds. Producer and material values, including the separate `GL` and `GR` glass results, are preserved rather than recomputed for publication. The [SNS contract tests](../tests/Api.IntegrationTests/AuditEvents/Analytics/SnsAnalyticsEventSenderTests.cs) show create and update envelopes with schema-bound snapshots.

Equivalent recalculations, including timestamp or ordering differences alone, produce no new version or event. Recalculation emits no delete events. Calculation, prior-state history and outbox writes share a Mongo transaction: a cancelled, failed or conflicting write must not leave a partial change or event.

## Delivery and rollout

The existing asynchronous outbox dispatcher provides retries and dead-letter handling. Dispatch succeeds only after SNS accepts the message. Delivery is at least once: a retry can publish the same `eventId` more than once, and consumers must deduplicate by event identity. `sequence` belongs to the global analytics event stream, including declaration events; calculation consumers must not interpret gaps as missing calculation versions.

Before enabling publication, register `obligation_calculation`, `obligation_calculation.created`, `obligation_calculation.updated`, `obligation_calculation_v1.0` and the `system` actor prefix in the shared governed vocabulary, and confirm A&I consumer readiness. Local vocabulary validation does not prove that shared registration or readiness is complete.

There is no initial backfill. An existing calculation can first appear as an update, or remain absent until materially changed. Older application instances can write calculations without events during a mixed-version rollout, so activation must be coordinated with the consumer and the stream must not be treated as complete across that interval.

Rolling back the publisher cannot retract messages already accepted by SNS. Consumers must coordinate any correction or replay. Existing outbox retry and dead-letter operating procedures continue to apply.
