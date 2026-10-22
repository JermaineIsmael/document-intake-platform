# Implementation status

Baseline: 22 October 2026. "Implemented" describes repository code;
"verified" requires evidence from a specific execution. This is not a live
Azure drift report.

| Capability | Implementation | Verification / remaining boundary |
|---|---|---|
| Authenticated upload | Implemented | Successful 202 and invalid-token 401 observed |
| PDF/DOCX input validation | Implemented | Automated tests exist; full deployed negative matrix pending |
| Private original storage | Implemented | Successful upload and downstream source evidence |
| Event Grid extraction | Implemented | Deployed Function invocations observed |
| Inline extracted-text messaging | Implemented | Small messages processed; large text exceeds batch/message limit |
| Worker queue processing/scaling | Implemented | Queue activity, dead letters, and scale 0-to-1 observed |
| Chunking/embeddings/Search upload | Implemented | Supported by later SQL success; direct Search check pending |
| SQL metadata upsert | Implemented | One small-document row with ChunkCount 1 observed |
| Message completion for successful upload | Implemented | Post-success settlement evidence pending |
| Dead-letter recovery tooling/procedure | Manual Portal inspection available | Six historical failed messages; recovery not confirmed |
| Chat model provisioning | Optional | Disabled due to quota |
| `/ask` route | Implemented | Disabled-chat behavior tested; full live RAG not available |
| RAG retrieval and answer generation | Placeholder/incomplete | Embedding extraction/retrieval return null; enabling model is insufficient |
| Document status/read API | Not implemented | Upload Location points to unimplemented path |
| Per-document ownership/tenant isolation | Not implemented | Must not claim multi-tenant security |
| SQL restricted production networking | Not implemented | Broad Azure-services rule explicitly enabled for development |
| SQL schema/user bootstrap | Manual SQL script | Pipeline does not execute database grants/migrations |

## Confirmed ingestion milestone

Supplied SQL evidence: document `6debfb11-d680-48f6-ac64-3973276b8869`,
ChunkCount `1`, IndexedAt `2026-10-22 12:58:58.6803004`.
The code writes SQL only after embeddings and Search upload. This establishes
a useful dev milestone, not load, vector-quality, or production certification.

## Implemented deployment fixes

Flex Consumption replaces quota-blocked Y1 hosting. Docker agent builds avoid
ACR Tasks restrictions. Worker image pulls use a preauthorized user-assigned
identity. Function readiness polls ARM state and indexed functions. Compatible
Functions telemetry dependencies remove the observed type-load failure.
Worker application settings now match startup validation. Event Grid
dead-letter writes use account-scoped Blob Data Contributor. SQL has an explicit
development Azure-services firewall opt-in.

Existing-server SQL authentication changes and accidental bootstrap of a
populated environment remain operational risks.

## Evidence and updates

See [deployment verification](deployment-verification.md) for the detailed
test history and resource inventory. Direct Search results, queue settlement,
and historical dead-letter disposition must be recorded before closing the
[MVP verification gate](../product/mvp-definition.md).
