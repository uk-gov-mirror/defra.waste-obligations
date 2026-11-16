# Analytics compliance declaration events

The analytics topic receives generic analytics event envelopes. Each envelope identifies the entity, operation, event type, entity version, and the versioned payload schema. The service publishes compliance declaration and [obligation calculation events](analytics-obligation-calculation-events.md) using this envelope.

Compliance declaration create, update, and delete operations are captured internally in the same transaction as the declaration change. The analytics processor reads undispatched changes, serialises them as analytics events, and publishes them to the analytics SNS topic configured by `AnalyticsAuditEventProcessor:TopicArn`.

The nested compliance declaration payload is serialised using the embedded [compliance declaration schema](../src/Api/Schemas/ComplianceDeclaration/compliance-declaration.v1.3.schema.json). Its version history is recorded in the [compliance declaration schema changelog](../src/Api/Schemas/ComplianceDeclaration/CHANGELOG.md). For compliance declarations, the analytics message `schemaVersion` is currently `compliance_declaration_v1.3`.

## Message transport

Messages are published with the SNS message attribute `Content-Type` set to `application/json`.

If the JSON body is too large for the SNS message size budget, the body is gzip-compressed and base64-encoded. Compressed messages include the SNS message attribute `Content-Encoding` set to `gzip+base64`.

## Analytics event envelope

All analytics events use this generic envelope:

```json
{
  "eventId": "01JZ8RXBMTY2K15SJB3PCFN3D5",
  "sequence": 123,
  "entity": "entity_name",
  "entityId": "entity_name_entity-id",
  "operation": "create",
  "eventType": "domain.event",
  "deletedReason": null,
  "piiKeyRef": null,
  "occurredAt": "2026-01-02T03:04:05.000Z",
  "recordedAt": "2026-01-02T03:04:06.000Z",
  "actor": "user:e72be574-8b5b-4836-af47-dd7e0c0d1d87",
  "correlationId": "cdp-request-id",
  "version": 1,
  "before": null,
  "after": {},
  "schemaVersion": "entity_name_v1.0"
}
```

| Field | Description |
| --- | --- |
| `eventId` | ULID generated for the analytics event. |
| `sequence` | Global analytics event sequence allocated when the event is written. |
| `entity` | Logical entity type for the payload. |
| `entityId` | Unique identifier for the entity instance as emitted by the entity mapper. |
| `operation` | Event operation. See the operation values below. |
| `eventType` | Business event name. |
| `deletedReason` | Reason the entity was deleted. This is only set when `operation` is `delete`; otherwise it is `null`. |
| `piiKeyRef` | Always `null`; PII classification and protection are outside the scope of this change. |
| `occurredAt` | Time the entity change occurred, as a UTC ISO 8601 timestamp with millisecond precision. |
| `recordedAt` | Time the analytics event was recorded, as a UTC ISO 8601 timestamp with millisecond precision. |
| `actor` | `user:<UUID>` for user submissions and status amendments; `service:waste-obligations` for system-driven deletions; calculations use `system:waste-obligations`. |
| `correlationId` | The incoming `x-cdp-request-id` when present; declaration events omit it when unavailable. Calculation events retain the instruction run correlation, including a scheduled-run fallback. |
| `version` | Entity version after the operation. |
| `before` | Previous entity state. This is `null` for creates. |
| `after` | New entity state. This is `null` for deletes. |
| `schemaVersion` | Entity-qualified schema version for the `before` and `after` payloads. |

## Operation values

| Operation | Meaning | `before` | `after` | `deletedReason` |
| --- | --- | --- | --- | --- |
| `create` | Entity was created. | `null` | Created entity state. | `null` |
| `update` | Entity was changed. | Previous entity state. | Updated entity state. | `null` |
| `delete` | Entity was deleted. | Previous entity state. | `null` | Delete reason. |

## Controlled vocabulary

The publisher validates the entity, envelope operation, event type, actor prefix, deletion reason, and schema version before serialising a message. The supported operations are `create`, `update`, and `delete`; actor prefixes are `service`, `user`, `system`, and `integration`; and the supported declaration event types are `submission.created`, `submission.amended`, and `submission.removed`. Request-driven create and update events use the responsible audit user as `user:<UUID>`; system-driven corrections and deletes use `service:waste-obligations`.

The only current deletion reason is `elevated_system_allowed_removal`. This replaces the legacy free-text value `elevated system allowed removal`, which remains normalised for undispatched historical outbox events. The governed list maintained with the shared event schema must include `elevated_system_allowed_removal` before additional deletion paths are introduced.

## Compliance declaration events

Compliance declaration events use `entity` set to `compliance_declaration`. The current service identity is a Mongo ObjectId, so `entityId` is emitted as `cdec_65f1f6570bb08052a8a27b01`.

The current compliance declaration event types are:

| Event type | Operation | Description |
| --- | --- | --- |
| `submission.created` | `create` | Compliance declaration was created. |
| `submission.amended` | `update` | Compliance declaration was updated. |
| `submission.removed` | `delete` | Compliance declaration was deleted. |

## Obligation coverage and schema compatibility

New declarations use raw schema version `v1.3`, published as `compliance_declaration_v1.3`. The [coverage calculator](../src/Api/Data/Entities/ObligationCoveragePercentageCalculator.cs) and its [tests](../tests/Api.Tests/Data/Entities/ObligationCoveragePercentageCalculatorTests.cs) define the calculation. The [schema changelog](../src/Api/Schemas/ComplianceDeclaration/CHANGELOG.md) records contract changes and compatibility.

The one-time [application data correction](application-data-corrections.md) updates affected declarations through the normal service, creating new analytics events; it does not rewrite historical events. Old hosts can still write the previous formula during a rolling deployment. Rolling back code does not restore corrected values, and there is no operational reversal.

## Created event

When a compliance declaration is created, the analytics topic receives a `create` event with `eventType` set to `submission.created`.

The `before` value is `null`. The `after` value is the created compliance declaration, serialised according to the linked compliance declaration schema.

```json
{
  "eventId": "01JZ8RXBMTY2K15SJB3PCFN3D5",
  "sequence": 123,
  "entity": "compliance_declaration",
  "entityId": "cdec_65f1f6570bb08052a8a27b01",
  "operation": "create",
  "eventType": "submission.created",
  "deletedReason": null,
  "piiKeyRef": null,
  "occurredAt": "2026-01-02T03:04:05.000Z",
  "recordedAt": "2026-01-02T03:04:06.000Z",
  "actor": "user:e72be574-8b5b-4836-af47-dd7e0c0d1d87",
  "correlationId": "cdp-request-id",
  "version": 1,
  "before": null,
  "after": {
    "id": "65f1f6570bb08052a8a27b01",
    "version": 1,
    "created": "2026-01-02T03:04:05+00:00",
    "updated": "2026-01-02T03:04:05+00:00",
    "status": "Submitted",
    "organisation": {
      "id": "5dbef606-3611-42f4-b39f-cad828badc12",
      "registrationType": "DirectProducer",
      "name": "Org Name",
      "complianceSchemeName": null,
      "schemeOperatorName": null,
      "referenceNumber": "123456",
      "address": {
        "addressLine1": "Test Name Ltd",
        "addressLine2": "123 Street",
        "town": "Town",
        "county": "County",
        "postcode": "UK1",
        "country": "UK"
      },
      "businessCountry": "GB-ENG",
      "regulator": "Regulator",
      "regulatorEmail": "regulator@email.com"
    },
    "obligationYear": 2026,
    "obligations": [
      {
        "material": "Plastic",
        "recyclingTarget": 0.75,
        "tonnages": {
          "material": 100,
          "awaitingAcceptance": 10,
          "accepted": 2,
          "outstanding": 20,
          "obligated": 5
        },
        "status": "NoDataYet"
      }
    ],
    "obligationStatus": "NotMet",
    "submitterName": "Submitter Name",
    "audit": [
      {
        "action": "Submitted",
        "user": {
          "id": "e72be574-8b5b-4836-af47-dd7e0c0d1d87",
          "email": "submitter@email.com",
          "name": "Submitter Name",
          "locale": "en"
        },
        "timestamp": "2026-01-02T03:04:05+00:00"
      }
    ],
    "isRegulation43Compliant": true,
    "obligationCoveragePercentage": 0
  },
  "schemaVersion": "compliance_declaration_v1.3"
}
```

## Updated event

When a compliance declaration is updated, the analytics topic receives an `update` event with `eventType` set to `submission.amended`.

The `before` value is the declaration state before the update. The `after` value is the declaration state after the update. Both payloads use the same compliance declaration schema.

```json
{
  "eventId": "01JZ8RXBMTY2K15SJB3PCFN3D6",
  "sequence": 124,
  "entity": "compliance_declaration",
  "entityId": "cdec_65f1f6570bb08052a8a27b01",
  "operation": "update",
  "eventType": "submission.amended",
  "deletedReason": null,
  "piiKeyRef": null,
  "occurredAt": "2026-01-02T03:05:05.000Z",
  "recordedAt": "2026-01-02T03:05:06.000Z",
  "actor": "user:e72be574-8b5b-4836-af47-dd7e0c0d1d87",
  "correlationId": "cdp-request-id",
  "version": 2,
  "before": {
    "id": "65f1f6570bb08052a8a27b01",
    "version": 1,
    "created": "2026-01-02T03:04:05+00:00",
    "updated": "2026-01-02T03:04:05+00:00",
    "status": "Submitted",
    "organisation": {
      "id": "5dbef606-3611-42f4-b39f-cad828badc12",
      "registrationType": "DirectProducer",
      "name": "Org Name",
      "complianceSchemeName": null,
      "schemeOperatorName": null,
      "referenceNumber": "123456",
      "address": {
        "addressLine1": "Test Name Ltd",
        "addressLine2": "123 Street",
        "town": "Town",
        "county": "County",
        "postcode": "UK1",
        "country": "UK"
      },
      "businessCountry": "GB-ENG",
      "regulator": "Regulator",
      "regulatorEmail": "regulator@email.com"
    },
    "obligationYear": 2026,
    "obligations": [
      {
        "material": "Plastic",
        "recyclingTarget": 0.75,
        "tonnages": {
          "material": 100,
          "awaitingAcceptance": 10,
          "accepted": 2,
          "outstanding": 20,
          "obligated": 5
        },
        "status": "NoDataYet"
      }
    ],
    "obligationStatus": "NotMet",
    "submitterName": "Submitter Name",
    "audit": [
      {
        "action": "Submitted",
        "user": {
          "id": "e72be574-8b5b-4836-af47-dd7e0c0d1d87",
          "email": "submitter@email.com",
          "name": "Submitter Name",
          "locale": "en"
        },
        "timestamp": "2026-01-02T03:04:05+00:00"
      }
    ],
    "isRegulation43Compliant": true,
    "obligationCoveragePercentage": 0
  },
  "after": {
    "id": "65f1f6570bb08052a8a27b01",
    "version": 2,
    "created": "2026-01-02T03:04:05+00:00",
    "updated": "2026-01-02T03:05:05+00:00",
    "status": "Accepted",
    "organisation": {
      "id": "5dbef606-3611-42f4-b39f-cad828badc12",
      "registrationType": "DirectProducer",
      "name": "Org Name",
      "complianceSchemeName": null,
      "schemeOperatorName": null,
      "referenceNumber": "123456",
      "address": {
        "addressLine1": "Test Name Ltd",
        "addressLine2": "123 Street",
        "town": "Town",
        "county": "County",
        "postcode": "UK1",
        "country": "UK"
      },
      "businessCountry": "GB-ENG",
      "regulator": "Regulator",
      "regulatorEmail": "regulator@email.com"
    },
    "obligationYear": 2026,
    "obligations": [
      {
        "material": "Plastic",
        "recyclingTarget": 0.75,
        "tonnages": {
          "material": 100,
          "awaitingAcceptance": 10,
          "accepted": 2,
          "outstanding": 20,
          "obligated": 5
        },
        "status": "NoDataYet"
      }
    ],
    "obligationStatus": "NotMet",
    "submitterName": "Submitter Name",
    "audit": [
      {
        "action": "Submitted",
        "user": {
          "id": "e72be574-8b5b-4836-af47-dd7e0c0d1d87",
          "email": "submitter@email.com",
          "name": "Submitter Name",
          "locale": "en"
        },
        "timestamp": "2026-01-02T03:04:05+00:00"
      },
      {
        "action": "Accepted",
        "user": {
          "id": "e72be574-8b5b-4836-af47-dd7e0c0d1d87",
          "email": "submitter@email.com",
          "name": "Submitter Name"
        },
        "timestamp": "2026-01-02T03:05:05+00:00",
        "reason": "Accepted reason"
      }
    ],
    "isRegulation43Compliant": true,
    "obligationCoveragePercentage": 0
  },
  "schemaVersion": "compliance_declaration_v1.3"
}
```

Correction execution and service update conventions are documented in [Application data corrections](application-data-corrections.md).
