# Testing strategy

Baseline: 22 October 2026. Separate code tests, deployment validation, and live
business-flow checks; none substitutes for the others.

## Test layers

| Layer | Existing coverage / evidence | Boundary |
|---|---|---|
| Validation/configuration | FileValidator, DocumentStoreConfiguration, authentication tests | No real cloud authorization established by mocks |
| API integration | Upload, request limit, auth, Ask endpoint tests | Live Entra consent/token acquisition is separate |
| Extractor | Event validation, failure propagation, telemetry initialization | Azure Function host indexing must also be tested live |
| Worker | TextChunker, IndexSchema, ChunkIndexer, IngestionWorker tests | Real quotas/network permissions are not mock behavior |
| SQL | DocumentMetadataRepository LocalDB tests, including replay/concurrency | Windows LocalDB tests skip when unavailable; Ubuntu CI does not prove these executed |
| RAG | Service/endpoint tests for current behavior | Do not interpret placeholder/null behavior as successful retrieval |
| Infrastructure | Bicep compilation and targeted compiled-ARM assertions | Compilation does not prove capacity, permission propagation, or runtime health |
| Deployment | Pipeline build/tests, image publish, Function indexing gate | Provisioned/Healthy resource state can coexist with app failure |

The [test project](../../tests/DocIntake.Tests/DocIntake.Tests.csproj) uses xUnit,
FluentAssertions, Moq, test hosting, and coverlet.

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
5. Verify Worker indexing, Search source/content/chunk count, SQL metadata,
   and successful message settlement.
6. Confirm no new dead-letter for this ID. Existing dead letters remain separate.

The observed one-chunk SQL row supports the successful pipeline path; direct
Search inspection and final settlement confirmation remain pending.

## Negative and recovery cases

- Missing file, wrong content/extension, oversized upload, invalid token.
- Empty extracted text and inline message-size overflow.
- Search/OpenAI throttling or permission failure; SQL firewall/login/schema failure.
- Lock expiry, concurrent same-ID replay, partial Search-before-SQL completion.
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
