# Mongo query profiling and index review

## Standard

`IntegrationTestBase` automatically profiles every direct MongoDB path in an integration test and fails teardown when a profiled query does not use an index. The profiler enables level 1 with a server-side `appName` filter for the application-under-test client, so MongoDB does not retain API-container operations in `system.profile`. Focused `MongoQueryProfiler` scopes remain useful when a test needs to prove a particular named index.

The API Mongo client identifies itself as `waste-obligations-api`; test fixtures use `waste-obligations-integration-fixtures`; and direct service subjects use `waste-obligations-integration-application`. This makes the scope precise even though they share the same MongoDB database. MongoDB records the client application name in profiler output when `appName` is configured.

MongoDB's profiler records read and write operations in `system.profile`, while `planSummary` and execution stats identify the winning plan. `$indexStats` reports how many user operations have used each index on the node. See the [MongoDB database profiler](https://www.mongodb.com/docs/v8.0/reference/database-profiler/), [index usage guidance](https://www.mongodb.com/docs/manual/tutorial/measure-index-use/), and [`$indexStats` reference](https://www.mongodb.com/docs/v7.0/reference/operator/aggregation/indexStats/).

`MongoQueryProfiler.Stop` returns:

- `Queries`, the scoped query operations with their index names and scan counts.
- `QueriesWithoutAnIndex`, which must be empty unless the test has an explicit, reviewed exception.
- `UnusedSecondaryIndexes`, the secondary indexes whose `$indexStats.accesses.ops` count did not increase during the scope. These are review candidates, not an instruction to remove an index: `$indexStats` is node-local and resets when MongoDB restarts or an index is recreated.

The proof test is `MongoQueryProfilerTests`. New direct-service scenarios are covered automatically. Focused profile tests should arrange all fixtures first, start the profiler immediately before the service invocation, stop it immediately afterwards, assert the behaviour, and assert `QueriesWithoutAnIndex` is empty. Profiled tests need realistic data volume and a matching document; otherwise the optimiser can legitimately choose a collection scan or return an `EOF` plan without evaluating the intended index. `EOF` plans are accepted because MongoDB returned without scanning a collection.

An unindexed plan may only be accepted with `AllowUnindexedMongoQuery(new MongoQueryProfileAllowance(...))` in the owning test. An allowance must match the Mongo operation, namespace, and value-independent filter shape, and must include a review ticket and reason. Teardown rejects unused allowances, preventing broad or stale exceptions. Current exceptions cover only unbounded exact counts/custom sorts, low-selectivity declaration status or registration-type counts, case-insensitive contains search across alternative organisation fields, and the deliberately unmigrated transaction-timeout database.

`insert`, transaction-control, and cursor continuation commands have no query predicate or query plan. They remain visible to the profiler but are not included in `QueriesWithoutAnIndex`. For a query needing deeper investigation, MongoDB can run the recorded `find`, `count`, `update`, `delete`, or `findAndModify` command through `explain` without applying write changes; see the [`explain` command](https://www.mongodb.com/docs/manual/reference/command/explain/).

## Applied index set

The application query review adds exactly three secondary indexes, all on `OrganisationComplianceDeclarationEligibility`:

- `OrganisationId_ObligationYear_RegistrationType` serves visibility updates across retained generations and, by prefix, hydration metric propagation by organisation and year.
- `Generation_ObligationYear_RegistrationStatus_ReferenceNumberResolutionState_OrganisationId` serves the hydration work-selection read and covers its projected organisation ID.
- `RefreshedAt` bounds expired-generation cleanup by its retention cutoff; `generation NOT IN (...)` remains a residual filter.

No status-only declaration index was added. The exclusion read now accepts the source rows it is evaluating and restricts its declaration read to their organisation IDs, years, and registration types, allowing the existing `OrganisationId_ObligationYear` index to be used. No audit-event index was added either: `AuditEvent.EventId` is mapped to MongoDB `_id`, so dispatch marking already uses `_id_`.

## Query inventory

This is the source inventory as at the introduction of the profiler. A row marked **review** has no targeted current index, has a dynamic shape that must be profiled per supported variant, or is a deliberate one-off migration scan. Automatic `_id_` indexes count as indexed.

| Area | Collection | Predicate / operation | Current index assessment |
| --- | --- | --- | --- |
| Compliance declaration read | `ComplianceDeclaration` | `_id` | `_id_` |
| Compliance declaration list by organisation | `ComplianceDeclaration` | `organisation.id`, `obligationYear`; sort `updated`, `_id` | `OrganisationId_ObligationYear` |
| Compliance declaration delete / optimistic replace | `ComplianceDeclaration` | `_id`, `version` | `_id_` |
| Compliance declaration search count and page | `ComplianceDeclaration` | optional year, status, registration type, regex term; dynamic sort | **review** — `ObligationYear_Status_OrganisationRegistrationType` only covers its equality-prefix filters; every sort/filter combination must be profiled |
| Eligibility visibility, initial exclusion read | `ComplianceDeclaration` | source-row organisation IDs and years, registration types, submitted/accepted status | `OrganisationId_ObligationYear` prefix; the query is bounded to relevant source rows |
| Eligibility visibility, declaration check | `ComplianceDeclaration` | organisation id, year, registration type, submitted/accepted status | `OrganisationId_ObligationYear` is usable but does not cover the rest |
| Eligibility snapshot reads and concurrency updates | `OrganisationEligibilitySnapshot` | `_id` plus snapshot values | `_id_` |
| Eligibility active-generation read / count | `OrganisationComplianceDeclarationEligibility` | `generation` | `Generation_OrganisationId_ObligationYear_RegistrationType` prefix |
| Unsubmitted search count and page | `OrganisationComplianceDeclarationEligibility` | generation, visible flag, optional year/type, optional regex; dynamic sort | **review** — four sort-oriented indexes exist; profile every supported sort and filter combination |
| Eligibility visibility updates | `OrganisationComplianceDeclarationEligibility` | organisation id, year, registration type, visibility inputs | `OrganisationId_ObligationYear_RegistrationType` |
| Eligibility garbage collection | `OrganisationComplianceDeclarationEligibility` | generation not in retained set; `refreshedAt` cutoff | `RefreshedAt`; generation is a residual filter |
| Hydration eligible-organisations read | `OrganisationComplianceDeclarationEligibility` | generation, year, registered, resolved | `Generation_ObligationYear_RegistrationStatus_ReferenceNumberResolutionState_OrganisationId` |
| Hydration metric propagation | `OrganisationComplianceDeclarationEligibility` | organisation id and year | `OrganisationId_ObligationYear_RegistrationType` prefix |
| Obligation metric lookup | `OrganisationObligationSummary` | organisation id set and year set | `OrganisationId_ObligationYear` |
| Hydration enqueue / persist | `OrganisationObligationSummary` | organisation id and year | `OrganisationId_ObligationYear` |
| Hydration due-work read | `OrganisationObligationSummary` | year, active, due time; sort priority and next refresh | `ObligationYear_IsHydrationActive_Priority_NextRefreshAt` |
| Hydration reconciliation / deactivation / staleness | `OrganisationObligationSummary` | year, active, priority or stale-time predicates | **review** — the due-work index has only a useful equality prefix for some variants |
| Worker, audit-dispatch, and migration leases | `_unsubmitted_organisation_worker_leases`, `_audit_event_dispatch_lease`, `_migrations_lease` | `_id` with owner / expiry condition | `_id_` |
| Audit event counter | `_audit_event_counter` | `_id` find-and-modify | `_id_` |
| Audit dispatch batch read | `AuditEvent` | missing dispatch or failed/due; sort sequence | **review** — live profiling selected `Sequence`; both dispatch indexes need usage evidence |
| Audit dispatch mark | `AuditEvent` | `eventId` and dispatch state | `_id_`; `eventId` is the BSON `_id` |
| Audit event insert | `AuditEvent` | insert | no predicate; profile for observability only |
| Schema migrations 003–005 | `ComplianceDeclaration` | `schemaVersion`, or missing schema version; per-document `_id` updates | **reviewed exception** — one-off migration scans have no schema-version index; profile them in migration integration tests and reassess if collection size makes deployment duration unsafe |

## How to add coverage

Add one focused integration scenario for each query shape, rather than one broad test that hides a failing plan. For dynamic search and sort endpoints, use a theory over every documented combination. A direct service test must derive from `IntegrationTestBase` and construct its real `MongoDbContext` from `GetMongoApplicationDatabase()`; the base class then profiles it automatically. Keep setup and assertions on `GetMongoDatabase()`, whose fixture application name is excluded. Docker Compose API scenarios are deliberately excluded because their `waste-obligations-api` operations never enter the profile collection. A standalone direct-Mongo test that cannot derive from the base class must start `MongoQueryProfiler` with the application client name and call `AssertIndexesUsed` itself.

When `UnusedSecondaryIndexes` keeps identifying the same index after the complete relevant query matrix has run, review production `$indexStats` over a representative period and on every replica-set node before proposing an index-removal migration. Conversely, any collection scan must be fixed with an index, a deliberately bounded exception, or a query redesign before the coverage test is accepted.
