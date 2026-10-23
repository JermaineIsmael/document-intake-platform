# Architecture overview

Baseline: 22 October 2026. This describes the implemented development system,
not a production-readiness certification.

## Purpose and architectural style

DocIntake accepts authenticated documents and asynchronously converts them into
searchable text chunks, embeddings, and SQL metadata. Independently deployed
API, Extractor, and Worker components communicate through Blob Storage events
and Service Bus rather than a long-running upload request.

```mermaid
flowchart LR
    User[Authenticated client] --> API[Container App API]
    API --> Blob[Private documents container]
    Blob --> Events[Event Grid]
    Events --> Function[Extractor Function]
    Function --> DI[Document Intelligence]
    Function --> Text[Private extracted-text Blob]
    Function --> Queue[Service Bus claim-check]
    Queue --> Worker[Container App Worker]
    Worker --> AI[OpenAI embeddings]
    Worker --> Search[AI Search]
    Worker --> SQL[Private SQL metadata]
    Worker -. retention sweep .-> Blob
    Worker -. retention sweep .-> Search
```

## Technology and hosting

| Layer | Technology | Deployment |
|---|---|---|
| Upload API | C#, .NET 8, ASP.NET Core minimal APIs | Azure Container Apps, Docker |
| Extraction | .NET 8 isolated Azure Functions v4 | Flex Consumption FC1, ZIP package |
| Ingestion Worker | .NET 8 BackgroundService | Azure Container Apps, Docker |
| Contracts | C# shared records | Referenced project library |
| Events/messaging | BlobCreated events and JSON messages | Event Grid and Service Bus Standard |
| AI processing | prebuilt-read and text-embedding-3-small | Document Intelligence and Azure OpenAI |
| Persistence | Original/extracted text, vector chunks, owner metadata | Blob Storage, AI Search, Azure SQL |
| Observability | Azure Monitor, Application Insights, OpenTelemetry | Log Analytics-backed telemetry |
| Delivery | Bicep, Bash, Azure CLI, Docker, Azure DevOps | Ubuntu hosted pipeline agent |

## Infrastructure shape

- API: HTTPS ingress on port 8080, 1-3 replicas outside bootstrap.
- Worker: no ingress, 0-5 replicas driven by queue depth through KEDA.
- Both Container Apps request 0.5 CPU and 1 GiB per replica.
- Extractor: 2048 MiB Flex instances, maximum 40, no always-ready configuration.
- Service Bus: Standard queue with duplicate detection and dead-lettering.
- AI Search: Basic, one replica and one partition; HNSW vectors, 1536 dimensions.
- SQL: Basic database; SQL location/name independently configurable. Bicep
  configures a private endpoint and disables public network access.
- API and Worker share a VNet-integrated Container Apps environment for SQL
  private DNS/connectivity; adopting this for an existing environment may
  require migration or recreation.
- Extracted text is stored in a private Blob container; Service Bus carries a
  small reference so payload size is not bounded by extracted text size.
- Owner-scoped document lifecycle operations, 30-day restore window, and
  90-day automated retention are implemented in code/configuration but not yet
  live-verified.

## Key decisions and trade-offs

Asynchronous processing keeps upload latency independent of extraction and
indexing, but requires operational visibility into retries and dead letters.
Search is written before SQL; there is no cross-service transaction.
Managed identity separates application authorization from deployment rights.
The Worker has a separate image-pull identity to avoid first-deployment RBAC
ordering problems.

The claim-check removes extracted-text size from the Service Bus message, but
large-document operation has not been live-verified. The optional chat endpoint
is present, but retrieval/answer generation is incomplete and chat is disabled
in development. Backup/disaster recovery remains separate from soft deletion
and retention.

## Related material

- [Component design](component-design.md)
- [Data flow and contracts](data-flow.md)
- [Security boundaries](security.md)
- [System context](system-context.md)
- [Azure resource inventory and deployment evidence](../development/deployment-verification.md)
- [Bicep source of truth](../../infra/main.bicep)
