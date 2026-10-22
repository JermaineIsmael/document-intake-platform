# Component design

Baseline: 22 October 2026. Configuration and code references are authoritative
for current behavior; proposed extensions are identified explicitly.

## API

[Program](../../src/DocIntake.Api/Program.cs) configures JWT authentication,
authorization, upload limits, Azure clients, telemetry, and routes.

| Surface | Implemented behavior |
|---|---|
| `POST /documents` | Authenticated multipart upload, one `file`, PDF/DOCX validation, GUID assignment, private blob upload, HTTP 202 |
| `GET /` | Authenticated service identification |
| `GET /health`, `/health/live` | Anonymous health checks; no cloud dependency checks registered |
| `POST /ask` | Authenticated endpoint; valid requests receive 503 when chat is explicitly disabled |
| `GET /documents/{id}` | Not implemented despite upload Location header |

[UploadOptions](../../src/DocIntake.Api/Configuration/UploadOptions.cs) defaults
to 25 MiB per file. File validation and request-body limits are distinct from
downstream extracted-text message limits.

## Extractor

[ExtractDocument](../../src/DocIntake.Extractor/ExtractDocument.cs) validates
BlobCreated events, downloads the document, calls prebuilt-read, and sends a
`DocumentMessage`. Failures are logged and rethrown for Event Grid retries.
The complete extracted text is serialized inline; failed batch admission
raises an explicit error rather than truncating content.

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

[ChunkIndexer](../../src/DocIntake.Worker/ChunkIndexer.cs) splits text,
generates embeddings, uploads Search documents, then saves SQL metadata.
Empty text produces a warning and no metadata row.

[DocumentMetadataRepository](../../src/DocIntake.Worker/DocumentMetadataRepository.cs)
uses parameterized SQL, a transactional update/insert with locking, and a
refreshable access-token callback. Same-ID replay updates a single row.

## Shared data and persistence

| Store/contract | Responsibilities |
|---|---|
| Shared `DocumentMessage` | Source URL, document GUID, inline extracted content |
| `documents` blobs | Original bytes, per-upload path and metadata |
| `document-chunks` Search index | `id`, `source`, searchable `content`, `contentVector` |
| `dbo.Documents` | `Id`, `SourceUrl`, `ChunkCount`, `IndexedAt` |
| Event Grid dead-letter container | Failed event delivery evidence |
| Service Bus dead-letter subqueue | Invalid contracts and exhausted processing |

## RAG implementation boundary

[RagService](../../src/DocIntake.Api/Services/RagService.cs) is a partial
implementation: embedding extraction returns null, retrieval is a placeholder,
and no complete answer-generation flow runs. Enabling a chat model alone does
not complete RAG. Development explicitly disables chat, producing 503 for valid
authenticated requests; configured placeholder paths can produce 404.

## Infrastructure and pipeline

[Container App module](../../infra/modules/containerApp.bicep) shares identity,
registry, resources, scaling, and optional ingress/probes. The
[pipeline](../../infra/azure-pipelines.yml) builds/tests, deploys images,
publishes Extractor, verifies indexing, then enables Event Grid.
SQL schema/user grants and Entra registration/consent remain external steps.

See [data flow](data-flow.md) and [implementation status](../development/implementation-status.md).
