# Component design

Baseline: 22 October 2026. Configuration and code references are authoritative
for current behavior; proposed extensions are identified explicitly.

## API

[Program](../../src/DocIntake.Api/Program.cs) configures JWT authentication,
authorization, upload limits, Azure clients, telemetry, and routes.

| Surface | Implemented behavior |
|---|---|
| `POST /documents` | Authenticated multipart upload, one `file`, PDF/DOCX validation, GUID assignment, owner metadata, private blob upload, HTTP 202 |
| `GET /documents/{id}` | Owner-scoped lifecycle status (`queued`, `extracting`, `processing`, `indexed`, `noText`, `failed`, `deleted`) with caller-safe failure details |
| `DELETE /documents/{id}` | Owner-scoped soft delete with a 30-day recovery deadline |
| `POST /documents/{id}/restore` | Owner-scoped restore during the recovery window |
| `GET /` | Authenticated service identification |
| `GET /health`, `/health/live` | Anonymous health checks; no cloud dependency checks registered |
| `POST /ask` | Authenticated endpoint; valid requests receive 503 when chat is explicitly disabled |

Ownership is taken from the Entra `oid` claim. The development auth bypass uses
a fixed development-only owner ID; production requests without a valid object ID
are forbidden. SQL lookups enforce both document ID and owner ID.

[UploadOptions](../../src/DocIntake.Api/Configuration/UploadOptions.cs) defaults
to 25 MiB per file. File validation and request-body limits are distinct from
downstream extracted-text message limits.

## Extractor

[ExtractDocument](../../src/DocIntake.Extractor/ExtractDocument.cs) validates
BlobCreated events, downloads the document, calls prebuilt-read, stores the full
extracted text in the private `extracted-text` Blob container, then sends a
small `DocumentMessage` claim-check. Source ownership metadata is validated;
failures are logged and rethrown for Event Grid retries.

The Function uses managed identity for host/deployment storage and service
clients. Telemetry WorkerService is pinned to 2.23.0 for compatibility with
the Functions Application Insights integration.

## Worker

[Startup](../../src/DocIntake.Worker/Program.cs) requires a fully qualified
`ServiceBus__Namespace`, plus `Sql__Server` and `Sql__Database`. It builds the
SQL connection string and uses Entra tokens, not a SQL password.

[IngestionWorker](../../src/DocIntake.Worker/IngestionWorker.cs) ensures the
Search schema, starts a PeekLock processor, validates messages, and explicitly
completes, abandons, or dead-letters each message.

[ChunkIndexer](../../src/DocIntake.Worker/ChunkIndexer.cs) checks that the
document is active for the message owner, resolves the extracted-text reference,
splits text, generates embeddings, uploads owner-tagged Search documents, then
saves SQL metadata. It marks the document `processing` first; empty text is
saved with the terminal `noText` status and zero chunks.
[StatusEventWorker](../../src/DocIntake.Worker/StatusEventWorker.cs) applies
Extractor `extracting`/`failed` events from the `document-status` queue, and
the retention sweep fails documents stuck in flight beyond the stale timeout.

[DocumentMetadataRepository](../../src/DocIntake.Worker/DocumentMetadataRepository.cs)
uses parameterized SQL, a transactional update/insert with locking, and a
refreshable access-token callback. Same-ID replay updates a single row.

## Shared data and persistence

| Store/contract | Responsibilities |
|---|---|
| Shared `DocumentMessage` | Source URL, GUID, owner, creation time, source blob name, extracted-text blob reference |
| `documents` blobs | Original bytes, per-upload path, owner/creation metadata, soft deletion |
| `extracted-text` blobs | Full extracted text with document/owner metadata; private access |
| `document-chunks` Search index | `id`, `documentId`, `ownerId`, `isDeleted`, `source`, searchable `content`, `contentVector` |
| `dbo.Documents` | Source/blob names, owner, chunk count, indexed/deletion/recovery/cleanup timestamps |
| Event Grid dead-letter container | Failed event delivery evidence |
| Service Bus dead-letter subqueue | Invalid contracts and exhausted processing |

## RAG implementation boundary

[RagService](../../src/DocIntake.Api/Services/RagService.cs) orchestrates
question embedding ([AzureOpenAiQuestionEmbedder](../../src/DocIntake.Api/Services/AzureOpenAiQuestionEmbedder.cs)),
owner-filtered vector retrieval ([AzureSearchChunkRetriever](../../src/DocIntake.Api/Services/AzureSearchChunkRetriever.cs)),
bounded context assembly ([RagContextBuilder](../../src/DocIntake.Api/Services/RagContextBuilder.cs)), and
grounded chat completion ([AzureOpenAiAnswerGenerator](../../src/DocIntake.Api/Services/AzureOpenAiAnswerGenerator.cs)).
Enabling chat requires complete Search and OpenAI configuration at startup.
Development explicitly disables chat, producing 503 for valid authenticated
requests. When enabled, the endpoint returns 200 with `{ answer, sources }`, 404
when no owned, non-deleted chunk is retrieved, and 500 for dependency failures.
This path is unit-tested but not yet verified against live Azure resources.

## Retention and recovery

[DocumentRetentionWorker](../../src/DocIntake.Worker/DocumentRetentionWorker.cs)
runs an hourly configurable sweep. At 90-day expiry it removes visibility in
Search, soft-deletes original and extracted blobs, then purges Search chunks and
SQL metadata. Owner-initiated deletes are recoverable through the API for 30
days. Blob Storage's native soft-delete remains for up to 30 days after
automatic retention cleanup, but the application restore window has expired.
These paths are implemented but have not been live-tested; they do not replace
backups or disaster recovery.

## Infrastructure and pipeline

[Container App module](../../infra/modules/containerApp.bicep) shares identity,
registry, resources, scaling, and optional ingress/probes. The
[pipeline](../../infra/azure-pipelines.yml) builds/tests, deploys images,
runs the [database migration job](../../src/DocIntake.Migrations) before
updating application images, publishes Extractor, verifies indexing, then
enables Event Grid. Entra registration/consent and the one-time migrator
admin-group membership remain external steps.
Bicep configures VNet integration for Container Apps plus SQL private endpoint
and private DNS, but the changes are not deployed. Existing environments may
need migration/recreation; review CIDR overlap before deployment.

See [data flow](data-flow.md) and [implementation status](../development/implementation-status.md).
