# Coding standards

These are repository-aligned contribution guidelines as of 22 October 2026.
They are not a claim that an automated formatter enforces every guideline.

## C# conventions

- Target the existing .NET 8 runtime and preserve nullable reference checking.
- Follow the surrounding file's namespace, indentation, and naming patterns:
  PascalCase types/methods, camelCase parameters/locals, Async suffix for
  asynchronous operations.
- Use dependency injection and typed Azure clients; reuse existing contracts,
  configuration options, and helpers before introducing new abstractions.
- Pass CancellationToken through I/O. Make retry and message-settlement
  ownership explicit rather than layering hidden retries.
- Validate configuration at startup and untrusted data at ingress boundaries.
  Preserve the difference between malformed input and infrastructure failure.
- Log actionable failures with document/message identifiers. Avoid credentials,
  raw document text, and full user questions in routine logs.
- Do not silently truncate data, swallow service failures, or report success
  after incomplete work. Preserve intended cancellation/shutdown behavior.
- Use parameterized SQL and the existing token callback/upsert pattern.

## Contracts and compatibility

Changing DocumentMessage affects Extractor, Worker, replayed messages, tests,
and documentation. Coordinate deployment order and old-message compatibility.
Preserve deterministic Search IDs and same-document upsert semantics.
Do not equate a fresh upload GUID with duplicate-content detection.

Configuration keys must match consumer code: Worker uses a Service Bus FQDN,
`Sql__Server`, and `Sql__Database`; the KEDA namespace stays bare.
Keep package references in project manifests. Validate Functions telemetry
compatibility when updating Application Insights packages.

## Infrastructure and scripts

- Use Bicep symbolic references, explicit deployment conditions, managed
  identities, and necessary ordering dependencies.
- Document broad-access development opt-ins; default them off in the template.
- Understand incremental-deployment deletion behavior before claiming a
  disabled condition revokes an existing resource.
- Preserve Bash fail-fast behavior and the original failed deployment exit code
  while collecting best-effort diagnostics.
- Do not rerun sample-image bootstrap against an existing environment.
- Keep secrets out of parameter files, scripts, generated artifacts, and commits.

## Tests and change review

Add focused regression tests for behavior/dependency fixes. Run the smallest
relevant suite first, then the build/pipeline gates. Review skipped platform
tests rather than equating a green test command with complete coverage.
Document behavior shifts, manual migration steps, and unverified Azure outcomes.

Useful commands from the repository root:

```powershell
dotnet build DocIntake.sln --configuration Release --warnaserror
dotnet test DocIntake.sln --configuration Release --no-build
az bicep build --file infra\main.bicep --stdout
```

See [testing strategy](testing-strategy.md) and
[implementation status](implementation-status.md).
