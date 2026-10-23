# Testing strategy

Baseline: 22 October 2026. Separate code tests, deployment validation, and live
business-flow checks; none substitutes for the others.

## Test layers

| Layer | Existing coverage / evidence | Boundary |
|---|---|---|
| Validation/configuration | FileValidator, DocumentStoreConfiguration, authentication tests | No real cloud authorization established by mocks |
| API integration | Upload, request limit, auth, ownership, delete/restore, and Ask endpoint tests | Live Entra consent/token acquisition and cross-owner checks are separate |
| Extractor | Event validation, claim-check storage/message contract, failure propagation, telemetry initialization | Azure Function host indexing and large-document behavior must also be tested live |
| Worker | TextChunker, IndexSchema, claim-check validation, ChunkIndexer, IngestionWorker, retention tests | Real quotas/network permissions are not mock behavior |
| SQL | DocumentMetadataRepository LocalDB tests applying the real `sql/migrations` scripts, including replay/concurrency, forward-only status transitions, stale timeouts, and CHECK constraints; migration runner checksum/ordering tests | Windows LocalDB tests skip when unavailable; Ubuntu CI does not prove these executed |
| RAG | Service/endpoint tests for current behavior | Do not interpret placeholder/null behavior as successful retrieval |
| Infrastructure | Bicep compilation and targeted compiled-ARM assertions | Compilation does not prove capacity, permission propagation, or runtime health |
| Deployment | Pipeline build/tests, image publish, Function indexing gate | Provisioned/Healthy resource state can coexist with app failure |

The [test project](../../tests/DocIntake.Tests/DocIntake.Tests.csproj) uses xUnit,
FluentAssertions, Moq, test hosting, and coverlet.

Latest blocker-change validation: Release solution build succeeded with
warnings as errors; **137 passed, 0 failed, 0 skipped**. Bicep template and
parameter compilation succeeded. These are repository-level checks only and
do not verify deployed Azure behavior.

## Execution

```powershell
dotnet build DocIntake.sln --configuration Release --warnaserror
dotnet test DocIntake.sln --configuration Release --no-build
```

For an Extractor-only change:

```powershell
dotnet test tests\DocIntake.Tests\DocIntake.Tests.csproj --configuration Release `
  --filter FullyQualifiedName~ExtractorFunctionTests
```

Record passed/failed/skipped counts and test environment. Historical counts in
the deployment report are not current suite totals. No new execution is implied
by this strategy document.

## Live smoke-test acceptance

Use a non-sensitive, one-page text PDF. Record build/image tag, document ID,
UTC timestamps, Search count, SQL row, and before/after queue counts.

1. Verify real API image/ingress, 200 health, and 401 on an anonymous protected route.
2. Obtain an Entra token for the API scope; never log the token.
3. Upload multipart `file`; expect 202, GUID, and original blob.
4. Verify Extractor invocation and queue publication.
5. Verify Worker claim-check download, Search source/content/chunk count,
   owner ID, SQL metadata, and successful message settlement.
6. Confirm no new dead-letter for this ID. Existing dead letters remain separate.

The previously observed one-chunk SQL row and Search evidence support the
small-document pipeline path. Owner isolation, large claim-check ingestion,
private SQL DNS/connectivity, and live delete/restore/retention remain pending.

## Negative and recovery cases

- Missing file, wrong content/extension, oversized upload, invalid token.
- `noText`, extraction-failure, and stale-timeout status transitions; large claim-check processing limits.
- Migration job execution and SQL authentication convergence against an existing server.
- Search/OpenAI throttling or permission failure; SQL firewall/login/schema failure.
- Lock expiry, concurrent same-ID replay, partial Search-before-SQL completion.
- Delete/restore within and beyond the recovery deadline; retention retries and
  final purge after 90-day expiry.
- Dead-letter inspection/replay without duplicate suppression or data loss.
- Zero-replica console unavailability versus real scaling/processing failure.
- Disabled chat returns 503; do not use this as a live RAG pass.

Replay requires understanding the queue's 10-minute duplicate-detection window.
Do not delete originals until recovery is demonstrated.

## Diagnostics and release gate

Use Application Insights for app traces/exceptions, Log Analytics
`ContainerAppConsoleLogs_CL` for stopped replicas, Event Grid metrics for delivery,
and dead-letter reason/description for processing failures.
Inspect queue `countDetails`, not only aggregate resource health.

Before closing a release, attach evidence for all acceptance checks, list
skipped tests, and explicitly retain outstanding failures. See
[verification commands](deployment-verification.md) and
[MVP exit criteria](../product/mvp-definition.md).
