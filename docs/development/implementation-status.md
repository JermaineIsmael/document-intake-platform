# Implementation status

Baseline: 22 October 2026. "Implemented in code" is distinct from "deployed"
and "live verified." The current local validation is a Release solution build
with warnings as errors, 199 passing automated tests (including LocalDB
migration tests), and successful Bicep
template/parameter compilation. No Azure deployment or live verification of
the blocker changes has occurred.

| Capability | Implementation | Verification / remaining boundary |
|---|---|---|
| Authenticated upload and input validation | Implemented | Existing development upload evidence; current automated suite passes |
| Owner-scoped document operations | Implemented in code | Entra `oid` is stored in Blob metadata, SQL, queue contract, and Search chunks. `GET`, `DELETE`, and `restore` check the caller's owner. Cross-owner live API tests pending |
| Claim-check for extracted text | Implemented in code | Extractor writes text to private Blob Storage and queues a compact reference; Worker checks path plus document/owner metadata. Large-document live ingestion pending |
| SQL private networking | Implemented in Bicep | SQL public access is disabled, with private endpoint/DNS and VNet-integrated Container Apps configuration. Not deployed; existing Container Apps environment migration and live DNS/connectivity checks remain |
| 30-day recoverable deletion | Implemented in code/configuration | Owner restore path, SQL recovery deadline, Search deletion marker, and Blob soft delete configured. Live delete/restore test pending |
| 90-day retention automation | Implemented in Worker | At 90-day expiry, hourly sweep hides Search chunks, soft-deletes blobs, then purges Search/SQL metadata; Blob soft-delete remains up to 30 days without application restore. Live expiry/retry test pending |
| SQL schema and identity grants | Implemented, not deployed | Versioned, checksummed migrations in `sql/migrations` run as a VNet-integrated Container Apps Job before application images update, creating least-privilege API/Worker users by client ID. One-time prerequisite: add the migrator identity to the SQL admin group |
| SQL authentication drift | Implemented, not deployed | Pipeline detects an existing server, logs admin/Entra-only state, converges it with child resources, and verifies the result |
| Document lifecycle status | Implemented in code | `GET /documents/{id}` reports queued/extracting/processing/indexed/noText/failed/deleted with caller-safe failure details; Extractor status events flow through the `document-status` queue; stale documents time out. Live verification pending |
| Small-document end-to-end ingestion | Complete for prior deployment | Seven accepted SQL rows each match one Search chunk; latest observed queue and dead-letter counts were zero |
| RAG retrieval and answer generation | Incomplete | Question embedding extraction/retrieval remain placeholders; no owner-filtered retrieval or working answer generation |
| Backup/disaster recovery | Not implemented | No cross-region recovery, Search backup, or exercised recovery objectives |
| Operational alerting and release approval | Partial | Runbooks and telemetry exist; alert delivery, private networking, live retention, performance/cost, and production sign-off remain |

## Critical blocker changes

1. **Document ownership / authorization** — API resolves the Entra object ID,
   records ownership, and restricts document lifecycle endpoints to the owner.
   Search chunks are tagged with owner ID; RAG is still incomplete and must
   apply owner filters when retrieval is implemented. Existing rows with no
   owner remain inaccessible by design.
2. **Production SQL/network isolation** — Bicep provisions private SQL access
   and places Container Apps in the VNet. Address-space overlap review, app
   environment migration/recreation, deployment, SQL DNS and connectivity
   verification remain operational tasks.
3. **Large-document messaging** — Extracted text is persisted in private Blob
   Storage; Service Bus carries a reference instead of inline document text.
   Live large-document and retry testing remains.
4. **Retention/deletion/recovery** — Owner-initiated deletion is recoverable
   through the API for 30 days. At 90-day retention expiry the Worker purges
   Search/SQL metadata after soft-deleting blobs; Blob Storage's own soft-delete
   remains for up to 30 days, but the application restore window has expired.
   This does not provide backup or disaster recovery, and must be live-tested.

## Previous ingestion milestone

Supplied SQL/Search evidence showed seven accepted documents with matching
one-chunk rows. Two intentionally deleted historical queue messages are
excluded from successful ingestion samples. See
[deployment verification](deployment-verification.md) for detailed evidence
and caveats.
