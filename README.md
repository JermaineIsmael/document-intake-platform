# DocIntake - document intake platform

This repository contains the Sprint 1 API foundation and the Sprint 2
blob-to-extraction ingestion pipeline: authenticated uploads, Azure Blob
Storage, an Event Grid-triggered .NET 8 isolated Function, Azure AI Document
Intelligence, Application Insights, and a Service Bus queue. There is no queue
consumer, AI Search, SQL, or `/ask` endpoint.

## Projects

- [DocIntake.Shared](src/DocIntake.Shared): shared response models.
- [DocIntake.Api](src/DocIntake.Api): ASP.NET Core minimal API.
- [DocIntake.Extractor](src/DocIntake.Extractor): .NET 8 isolated Azure Function.
- [DocIntake.Tests](tests/DocIntake.Tests): xUnit and FluentAssertions integration tests.
- [Infrastructure](infra/main.bicep): resource-group-scoped Azure deployment.
- [Container App module](infra/modules/containerApp.bicep): reusable managed-identity container app.

The existing Git repository is used as the solution root. Projects reference
Shared from Api; both Api and Shared from Tests; and Shared from Extractor.
The `DocumentMessage` contract was not present in the Sprint 1 checkout. With
approval to infer the missing Sprint 2 contract, Extractor reuses the new
shared record defined in [DocumentMessage.cs](src/DocIntake.Shared/DocumentMessage.cs).

## Local development

Prerequisites:

- .NET 8 SDK (includes the ASP.NET Core runtime).
- Docker Engine / Docker Desktop, running Linux containers.
- Azurite, either its Docker image below or the npm package (requires Node.js).
- Azure CLI 2.53.0 or newer and Bicep CLI 0.22 or newer; current releases are
  recommended for the resource API versions used here.
- Azure Functions Core Tools v4 (`winget install Microsoft.Azure.FunctionsCoreTools`
  or `npm install --global azure-functions-core-tools@4 --unsafe-perm true`) to
  run the extractor locally.
- An Azure subscription and Entra API registration for deployment.

Clone the repository and run all following commands from its root:

```powershell
git clone https://github.com/JermaineIsmael15/DocIntakePlatform.git
Set-Location DocIntakePlatform
dotnet --info
az version
docker version
```

From the repository root, start Azurite **before** starting the API:

```powershell
docker run --rm --name docintake-azurite `
  -p 10000:10000 -p 10001:10001 -p 10002:10002 `
  mcr.microsoft.com/azure-storage/azurite `
  azurite --blobHost 0.0.0.0 --queueHost 0.0.0.0 `
  --tableHost 0.0.0.0 --location /data
```

This uses ephemeral emulator data. Alternatively, install Azurite with
`npm install --global azurite`, then run
`azurite --blobHost 127.0.0.1 --queueHost 127.0.0.1 --tableHost 127.0.0.1
--location .azurite` in a separate terminal. All three endpoints are needed
for the Functions host's local runtime storage.

```powershell
dotnet build --warnaserror
dotnet test
dotnet run --project src\DocIntake.Api --launch-profile http
```

The HTTP launch profile sets `ASPNETCORE_ENVIRONMENT=Development` and listens
on `http://localhost:5067`. [Development settings](src/DocIntake.Api/appsettings.Development.json)
set `Storage:BlobEndpoint` to `http://127.0.0.1:10000/devstoreaccount1` and
`Development:BypassAuth` to `true`.

Azurite does not emit Azure Event Grid events. For a complete end-to-end
pipeline test, publish the Function to a development Function App and upload
through its real Azure storage/Event Grid resources, as described below. This
is more practical and representative than manually constructing an Event Grid
webhook invocation. A local `func start` is still useful for debugging: use
Azurite only for the Functions host's local bookkeeping and configure the Blob,
Document Intelligence, and Service Bus endpoints to dev Azure resources. The
developer's `DefaultAzureCredential` must have Blob Data Owner on the storage
account, Cognitive Services User, and Service Bus Data Sender:

```powershell
az login --tenant '<tenant-id>'
if (!(Test-Path src\DocIntake.Extractor\local.settings.json)) {
    Copy-Item src\DocIntake.Extractor\local.settings.example.json `
      src\DocIntake.Extractor\local.settings.json
}
# Edit DocIntel__Endpoint and ServiceBus__Namespace in local.settings.json.
dotnet build src\DocIntake.Extractor\DocIntake.Extractor.csproj
func start --script-root src\DocIntake.Extractor
```

The local settings file is intentionally gitignored. `UseDevelopmentStorage=true`
is used only by the local Functions host to access Azurite. Set
`Storage__BlobEndpoint` to the HTTPS endpoint of the dev storage account, and
set the Document Intelligence endpoint and Service Bus namespace to deployed
dev resources. `az login` supplies the local developer credential. There is no
local Event Grid emulator in this workflow; uploading to Azurite alone does
not trigger extraction. For an end-to-end test, publish to the dev Function
App, whose host uses AzureWebJobsStorage and other services through its
managed identity.

```powershell
Invoke-RestMethod http://localhost:5067/
Invoke-WebRequest http://localhost:5067/health
```

`GET /` returns `{"service":"DocIntake.Api"}`. It requires authentication
except when the environment is exactly Development and the bypass setting is
true. A startup warning makes local bypass visible. Production and Staging
ignore the bypass setting, even if someone sets it to true. New endpoints are
also protected by the fallback authorization policy. The bypass handler is
registered only when both conditions hold, and independently rechecks both
conditions when handling authorization. Restart the process after changing
the bypass flag.

`GET /health` returns HTTP 200 with no authentication required.
`GET /health/live` is retained as an anonymous compatibility alias. These
endpoints report process health, not storage or downstream availability.

### Upload a document

```powershell
curl.exe -i -F "file=@C:\documents\sample.pdf" http://localhost:5067/documents
```

`POST /documents` requires authorization (subject to the Development-only
bypass) and accepts exactly one multipart/form-data file named `file`.
The default `Upload:MaxFileSizeMb` is 25 (25 MiB = 26,214,400 bytes);
`Upload:AllowedExtensions` defaults to `[".pdf", ".docx"]`. Use
`Upload__MaxFileSizeMb` and indexed settings such as
`Upload__AllowedExtensions__0` for environment overrides, or edit the Upload
section in [appsettings.json](src/DocIntake.Api/appsettings.json). Indexed
environment overrides replace individual entries, not the whole array; to
shorten the allow list, edit the base JSON array instead of leaving another
default index in place. The configured
allow list may restrict PDF/DOCX, but cannot add types without a corresponding
signature validator. Invalid upload configuration fails startup.

Validation responses have the JSON shape `{"error":"..."}`:

| Condition | HTTP status |
| --- | --- |
| Missing/empty file, wrong field, multiple files, malformed/non-multipart request | 400 |
| File exceeds the configured limit | 413 |
| Disallowed extension or signature mismatch | 415 |
| Blob Storage upload failure | 503 (no acceptance response) |

Extensions are case-insensitive. PDF bytes must begin with `%PDF-`; DOCX bytes
must begin with `50 4B 03 04` (ZIP local-file signature). The client's
Content-Type is ignored. These are signature checks, not malware scanning or
full PDF/Office validation; a generic ZIP with the DOCX signature passes this
Sprint 1 rule.

MultipartBodyLengthLimit enforces the exact file limit while Kestrel's
MaxRequestBodySize enforces the file limit plus 64 KiB for multipart headers,
boundaries, and other form data. Exactly-at-limit files are allowed, not reduced
by framing overhead. Requests exceeding the framing allowance also return 413.
Both Content-Length and chunked requests are bounded. Multipart parsing may
buffer larger files to temporary disk; ensure the runtime temporary directory
is writable and has capacity. Filename/path separators and characters outside
`[A-Za-z0-9._-]` are removed; excessively long names are rejected with 400.

Success returns HTTP 202, `Location: /documents/{documentId}`, and:

```json
{"documentId":"<guid>","status":"queued"}
```

The full original bytes are stored at `{documentId}/{sanitizedFileName}` with
the validated PDF/DOCX Content-Type. Metadata contains `documentId` and
`originalName`. The latter is percent-encoded for Azure's ASCII metadata-header
requirement; use `Uri.UnescapeDataString` to recover the original filename.
Each upload gets a new GUID and writes with an If-None-Match condition.

`queued` is the upload API's acceptance status; extraction and queue delivery
happen asynchronously through the Sprint 2 pipeline described below. The API
does not wait for Document Intelligence or Service Bus, and
`GET /documents/{documentId}` is not implemented.

### Testing Entra authentication locally

In the Azure portal, open **Microsoft Entra ID > App registrations > New
registration**. Register a single-tenant API, record its Directory (tenant) ID
and Application (client) ID, and set its manifest's
`api.requestedAccessTokenVersion` to `2`. Under **Expose an API**, set the
Application ID URI (for example `api://<api-client-id>`) and add a delegated
scope named `access_as_user`. Obtain tenant admin consent if required by your
organization.

For the command-line verification below, authorize the Microsoft Azure CLI
client (`04b07795-8ddb-461a-bbee-02f9e1bf7b46`) for that scope under **Expose an
API > Authorized client applications**, according to your organization's
consent policy. Alternatively use an approved client with permission to request
this API's access tokens. No API client secret is needed for this delegated
public-client flow.

Configure the tenant-specific v2 authority and the audience actually present
in its access tokens (the API's client ID for v2 tokens), not an ID token or a
token issued for Storage, ARM, or Microsoft Graph.

```powershell
$env:Development__BypassAuth = 'false'
$env:Auth__Authority = 'https://login.microsoftonline.com/<tenant-id>/v2.0'
$env:Auth__Audience = '<api-client-id>'
dotnet run --project src\DocIntake.Api --launch-profile http
```

Without a valid bearer access token, `/` and `/documents` return 401. Pass a token issued for
this API in `Authorization: Bearer <access-token>` to obtain 200. The API
validates token signature, issuer, audience, and lifetime. Missing authority or
audience fails startup when authentication is enforced. This foundation
requires authentication, not a particular application role or scope.

## Azure identity and telemetry

The container runs in Production with a system-assigned managed identity.
Storage shared-key access and ACR admin credentials are disabled. The identity
receives Storage Blob Data Contributor on the storage account and AcrPull on
the registry. Blob uploads use `DefaultAzureCredential` for all Azure storage
endpoints, which must be HTTPS. The `documents` container is provisioned by
Bicep; the API does not create Azure containers.

For local Development **only**, the exact configured endpoint
`http://127.0.0.1:10000/devstoreaccount1` uses the SDK's built-in
`UseDevelopmentStorage=true` public emulator credentials and creates the private
container if absent. This Azurite exception is never used for Azure or outside
Development; ordinary Azurite does not accept DefaultAzureCredential over HTTP.

When `APPLICATIONINSIGHTS_CONNECTION_STRING` is configured, Azure Monitor
OpenTelemetry reads that environment variable automatically (no connection
string is passed in application code), collects API telemetry, and authenticates ingestion with
`DefaultAzureCredential`. The API receives Monitoring Metrics Publisher on the
component.
**Approved exception to Definition of done item 11:** the API has three
narrowly scoped roles, not two. Monitoring Metrics Publisher is required for
identity-authenticated ingestion; it is not Contributor/Owner on the resource
group or subscription. The other roles remain Storage Blob Data Contributor
on the storage account and AcrPull on the registry.
The Application Insights connection string identifies the telemetry endpoint;
it is not a storage connection string or an account-key credential. For the
Function, App Insights local authentication remains enabled because the
selected exact-three-role Function identity cannot also receive
`Monitoring Metrics Publisher`.

Telemetry export is not enabled locally without that setting. To export
locally, use `az login`, configure the setting, and grant your developer identity
Monitoring Metrics Publisher on the Application Insights component.

The managed environment's **platform** log integration uses the Log Analytics
workspace key required by Container Apps' `logAnalyticsConfiguration` API. It is
resolved inside the deployment and is never passed to the application or
written to source. Application Azure SDK authentication remains identity-based.
Do not add storage connection strings or account keys to Azure configuration.

### Verify request and custom-log telemetry in Application Insights

After deploying the real API, send an authenticated successful PDF upload and
an authenticated upload with an invalid signature. Expect 202 and 415,
respectively, then allow a few minutes for telemetry ingestion. Do not use an
unauthenticated request to test validation logging: it stops at authentication.

Open the **Application Insights resource's Logs** blade and run:

```kusto
requests
| where timestamp > ago(30m)
| where url has "/documents"
| project timestamp, name, resultCode, success, duration, operation_Id
| order by timestamp desc
```

For custom logs, success records must include `DocumentId` and `FileSizeBytes`,
and warnings must include `Reason`, never file contents:

```kusto
traces
| where timestamp > ago(30m)
| where message startswith "Document upload accepted:"
    or message startswith "Document upload validation failed:"
| project timestamp, severityLevel, message,
    DocumentId=tostring(customDimensions.DocumentId),
    FileSizeBytes=tolong(customDimensions.FileSizeBytes),
    Reason=tostring(customDimensions.Reason), operation_Id
| order by timestamp desc
```

Correlate upload logs and request spans using the shared operation ID:

```kusto
requests
| where timestamp > ago(30m) and url has "/documents"
| project RequestTime=timestamp, resultCode, operation_Id
| join kind=inner (
    traces
    | where timestamp > ago(30m)
    | where message startswith "Document upload accepted:"
        or message startswith "Document upload validation failed:"
    | project operation_Id, severityLevel, message, customDimensions
) on operation_Id
| order by RequestTime desc
```

If querying the Log Analytics **workspace** instead, use `AppRequests` /
`AppTraces` and their workspace columns (`TimeGenerated`, `Url`, `ResultCode`,
`OperationId`, `Message`, `Properties`, `SeverityLevel`). If results are missing,
check the environment variable on the active revision, the identity's
Monitoring Metrics Publisher assignment/RBAC propagation, outbound telemetry
connectivity, and the application category's Information log level.
The queries above are the live-deployment verification steps; local tests alone
cannot confirm ingestion into Application Insights.

## Container

Build with the repository root as Docker context:

```powershell
docker build -f src\DocIntake.Api\Dockerfile -t docintake-api:sprint1 .
docker run --rm -p 8080:8080 `
  -e Auth__Authority='https://login.microsoftonline.com/<tenant-id>/v2.0' `
  -e Auth__Audience='<api-client-id>' `
  -e Storage__BlobEndpoint='https://<storage-account>.blob.core.windows.net' `
  docintake-api:sprint1
```

This standalone Production container can be used for health/authentication
smoke checks. Actual Azure blob uploads also require a credential source
supported by DefaultAzureCredential; a local container does not automatically
inherit the host's Azure CLI login or an Azure managed identity. The Container
Apps deployment supplies its system-assigned identity. For local uploads use
the API/Azurite development workflow above.

The multi-stage Dockerfile copies project files first for cached restore, then
publishes both projects with the .NET 8 SDK. The final ASP.NET Core 8 image uses
`USER app`, `EXPOSE 8080`, and `ASPNETCORE_URLS=http://+:8080`, and defaults to
Production. Local settings and build artifacts are excluded from the Docker
context.

## Infrastructure and deployment

[main.bicep](infra/main.bicep) provisions:

- Log Analytics (PerGB2018, 30-day retention) and workspace-linked web Application Insights.
- RBAC Key Vault with soft delete, 90-day retention, and purge protection.
- Standard_LRS StorageV2 account requiring HTTPS/TLS 1.2, with shared-key access
  and public blob access disabled, and a private `documents` container.
- Basic ACR with its admin user disabled.
- A Consumption Container Apps managed environment linked to Log Analytics.
- A system-assigned Container App, identity-based ACR pull, and the three RBAC
  assignments described above.
- A Service Bus Standard namespace with local authentication disabled and the
  `extracted-documents` queue (5 deliveries, 1-minute lock, expiry dead-letter,
  10-minute duplicate detection).
- An S0 FormRecognizer Azure AI Document Intelligence account with a custom
  subdomain and local authentication disabled.
- An Event Grid system topic on the private document storage account, with a
  BlobCreated subscription to the extractor filtered to the `documents`
  container and a managed-identity blob dead-letter destination.
- A Linux Consumption (Y1) Function App running the built-in .NET 8 isolated
  stack, with a separate shared-key-disabled host-storage account.
- Extractor RBAC: Storage Blob Data Owner on the document/host storage account,
  Service Bus Data Sender on the namespace, and Cognitive Services User on
  Document Intelligence.
- Event Grid system topic RBAC: Storage Blob Data Contributor on the storage
  dead-letter container only, solely to write exhausted events.

The container app module accepts name, location, environmentId, registryServer,
image, external, minReplicas, maxReplicas, envVars, and secrets. `envVars` is an
array of `{ name, value }` or `{ name, secretRef }` objects. `secrets` is a secure
object wrapping an array as `{ values: [{ name, value }] }` (empty by default),
since ARM cannot represent a secure array parameter. The module outputs principalId and
fqdn, serves HTTPS ingress on port 8080, and uses TCP startup/liveness/readiness
probes. The main deployment outputs apiFqdn, acrName, storageName,
serviceBusNamespace, documentIntelligenceEndpoint, and extractorName.

### 1. Prepare

Select the intended Azure subscription and a resource-group location that
supports Container Apps. The deployer needs resource creation permissions and
permission to create role assignments (for example, Contributor plus Role
Based Access Control Administrator, or Owner, at the resource-group scope).
The image publisher needs permission to build/push into ACR.
Ensure the Microsoft.App, Microsoft.ContainerRegistry, Microsoft.Storage,
Microsoft.OperationalInsights, Microsoft.Insights, and Microsoft.KeyVault
resource providers are registered in the subscription. For a new subscription,
after selecting it, an administrator can register them with:

```powershell
foreach ($provider in @('Microsoft.App', 'Microsoft.ContainerRegistry',
    'Microsoft.Storage', 'Microsoft.OperationalInsights', 'Microsoft.Insights',
    'Microsoft.KeyVault', 'Microsoft.ServiceBus', 'Microsoft.CognitiveServices',
    'Microsoft.EventGrid', 'Microsoft.Web')) {
    az provider register --namespace $provider --wait
    if ($LASTEXITCODE -ne 0) { throw "Provider registration failed: $provider" }
}
```

Replace the authority and audience placeholders in
[main.bicepparam](infra/main.bicepparam) with real Entra values. Do not deploy
placeholder authentication values.

```powershell
az login
az account set --subscription '<subscription-id>'
az extension add --name containerapp --upgrade
az bicep install
az bicep build --file infra\main.bicep
az bicep build-params --file infra\main.bicepparam

$resourceGroup = 'rg-docintake-dev'
$location = '<azure-region>'
az group create --name $resourceGroup --location $location
```

Compiled JSON is ignored by Git. Azure deployment creates billable resources;
compile/test success alone does not mean these resources have been deployed.

### 2. Bootstrap the managed identity and ACR permissions

A new system identity cannot pull a private image before it exists and has
AcrPull. The initial deployment therefore uses a public Microsoft ASP.NET sample
image with **internal-only ingress** (`bootstrap=true`, the default). This is
not the DocIntake API, is not an externally exposed application, and must not
be used as the final deployment.

```powershell
az deployment group what-if --resource-group $resourceGroup `
  --parameters infra\main.bicepparam

az deployment group create --name docintake-bootstrap `
  --resource-group $resourceGroup `
  --parameters infra\main.bicepparam

$acrName = az deployment group show --name docintake-bootstrap `
  --resource-group $resourceGroup --query properties.outputs.acrName.value -o tsv
$registryServer = az acr show --name $acrName --query loginServer -o tsv
```

### 3. Publish and deploy the actual API

Use a unique immutable image tag for each build; the following is an example.
Run from the repository root: the Dockerfile needs both projects. ACR Tasks
builds and pushes the image without requiring a local Docker engine. This
command uploads the build context to your Azure registry; review the context
and [.dockerignore](.dockerignore) before you run it.

```powershell
$tag = 'sprint1-001'
$image = "${registryServer}/docintake-api:${tag}"
az acr build --registry $acrName `
  --image "docintake-api:${tag}" `
  --file src\DocIntake.Api\Dockerfile .
```

Now update [main.bicepparam](infra/main.bicepparam): change `bootstrap` to
`false` and add `apiImage` using the exact value of `$image`, for example:

```bicep
param bootstrap = false
param apiImage = '<registry-name>.azurecr.io/docintake-api:sprint1-001'
```

Keep the real authority and audience values. Deploy directly from the Bicep
parameters file; do not combine it with `--template-file` or a second
`--parameters` argument.

```powershell
az deployment group what-if --resource-group $resourceGroup `
  --parameters infra\main.bicepparam

az deployment group create --name docintake-api `
  --resource-group $resourceGroup `
  --parameters infra\main.bicepparam

$apiName = az containerapp list --resource-group $resourceGroup `
  --query "[?contains(name, '-api')].name | [0]" -o tsv
if ([string]::IsNullOrWhiteSpace($apiName)) { throw 'The API Container App was not found.' }

# The Bicep deployment already sets the initial image. This exact command
# updates the running app for later images and creates a revision on change.
az containerapp update --name $apiName --resource-group $resourceGroup `
  --image $image --min-replicas 1 --max-replicas 3

$apiFqdn = az deployment group show --name docintake-api `
  --resource-group $resourceGroup --query properties.outputs.apiFqdn.value -o tsv
Invoke-WebRequest "https://${apiFqdn}/health"
curl.exe -i "https://${apiFqdn}/"
# Expected: 401 without a token, 200 with a valid API access token.
```

The final app has external HTTPS ingress, one minimum replica, three maximum
replicas, Production authentication, and storage/auth/telemetry environment
settings. Keep `bootstrap=false` and supply the real ACR image on every
subsequent deployment; reusing the bootstrap parameters alone would revert
the app to the private sample. RBAC propagation can take several minutes;
if the first private-image pull fails, verify the AcrPull assignment and retry
after propagation rather than enabling ACR admin credentials.

### 4. Subsequent image revisions

From the same repository root and PowerShell session:

```powershell
$tag = 'sprint1-002'
$image = "${registryServer}/docintake-api:${tag}"
az acr build --registry $acrName `
  --image "docintake-api:${tag}" `
  --file src\DocIntake.Api\Dockerfile .
az containerapp update --name $apiName --resource-group $resourceGroup `
  --image $image --min-replicas 1 --max-replicas 3
az containerapp revision list --name $apiName --resource-group $resourceGroup `
  --query "[].{name:name,active:properties.active,health:properties.healthState}" -o table
```

Also update `apiImage` in [main.bicepparam](infra/main.bicepparam) to the new
image, keeping `bootstrap=false`, so the next infrastructure deployment does
not roll back the image. In Single revision mode, Container Apps switches
traffic to the new revision after it becomes ready. If health remains
unhealthy, inspect `az containerapp logs show --name $apiName --resource-group
$resourceGroup --type system --tail 50` and resolve image pull/configuration
errors before testing the API.

### 5. Verify the deployed API, blobs, and roles

Set these variables to the Entra values prepared earlier. The public-client
permission/consent described above must be in place:

```powershell
$tenantId = '<tenant-id>'
$apiClientId = '<api-client-id>'
$applicationIdUri = "api://${apiClientId}"
az login --tenant $tenantId
$accessToken = az account get-access-token --tenant $tenantId `
  --scope "${applicationIdUri}/access_as_user" --query accessToken -o tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accessToken)) {
    throw 'Could not acquire an access token for the DocIntake API.'
}

# Anonymous: 200.
curl.exe -i "https://${apiFqdn}/health"
# No token: 401 (even with a valid file).
curl.exe -i -F "file=@C:\documents\sample.pdf" "https://${apiFqdn}/documents"
# Valid token/PDF: 202, JSON documentId/status, and Location header.
curl.exe -i -H "Authorization: Bearer $accessToken" `
  -F "file=@C:\documents\sample.pdf" "https://${apiFqdn}/documents"
# No file: 400. The empty form field makes this a valid multipart request.
curl.exe -i -H "Authorization: Bearer $accessToken" `
  -F "description=missing-file-test" "https://${apiFqdn}/documents"
# Content/extension mismatch: 415, using the same PDF renamed on the wire.
curl.exe -i -H "Authorization: Bearer $accessToken" `
  -F "file=@C:\documents\sample.pdf;filename=sample.docx" "https://${apiFqdn}/documents"
```

Use a real existing PDF at the example path. Do not commit or paste access
tokens into source/logs. Repeat the successful upload twice and record both
returned document IDs: they must differ. The successful requests must produce
one blob per ID, while the rejected requests must produce none.

The verification operator needs Storage Blob Data Reader (or a data role
already granting reads) on this storage account to list blobs. This is a
permission for the operator, **not an extra API identity role**. Verify using
Entra authentication only:

```powershell
$storageName = az deployment group show --name docintake-api `
  --resource-group $resourceGroup --query properties.outputs.storageName.value -o tsv
$documentId = '<document-id-returned-by-upload>'
az storage blob list --account-name $storageName --container-name documents `
  --auth-mode login --prefix "${documentId}/" `
  --query "[].{name:name,contentType:properties.contentSettings.contentType,metadata:metadata}" -o json

$principalId = az containerapp show --name $apiName --resource-group $resourceGroup `
  --query identity.principalId -o tsv
az role assignment list --assignee $principalId --all --include-inherited `
  --query "[].{role:roleDefinitionName,scope:scope}" -o table
```

Expect exactly one `documentId/sample.pdf` for each successful ID. Check the
same list before and after invalid uploads to confirm no new blobs; test an
empty file (400), file larger than 25 MiB (413), and a PDF sent as `sample.exe`
(415) as well. Expected API identity assignments are Storage Blob Data
Contributor (storage account), AcrPull (registry), and the approved Monitoring
Metrics Publisher exception (Application Insights); investigate any broader
or inherited assignments. Finally run the request/log Kusto queries in the
telemetry section and verify results for your requests. Remove the token from
the PowerShell session when finished:

```powershell
Remove-Variable accessToken
```

The role assignment names are stable for the app/resource names;
if you delete and recreate the app with a new system identity, remove its old
role assignments before redeploying (Azure cannot change an existing role
assignment's principal). Key Vault purge protection prevents immediate purging
after deletion during the 90-day retention period.

## Validation coverage

Integration tests generate ephemeral signing keys and use static OIDC
metadata; they do not call Entra or Azure. They cover environment-specific
auth bypass, accepted tokens, wrong issuer/audience, expired/malformed tokens,
untrusted signatures, missing auth configuration, anonymous liveness, and the
Development Azurite configuration. Upload tests exercise missing/empty files,
extension/signature checks, configured allow lists, sanitized names, exact
size boundaries, response shape, logs, and Blob SDK wire requests (bytes,
Content-Type, metadata, and overwrite protection) using an in-process HTTP
transport. A separate real Kestrel fixture verifies at-limit and over-limit
Content-Length/chunked uploads with a configured 1 MiB limit. These tests do not
require Azure or Azurite.

## Sprint 2: blob-to-queue extraction pipeline

The `DocIntake.Extractor` project targets `net8.0`, `OutputType=Exe`, Functions
runtime v4, and the isolated worker model. It uses the current isolated-worker
`ConfigureFunctionsWorkerDefaults()` host API plus
`ConfigureFunctionsApplicationInsights()`. The SDKs use one injected
`DefaultAzureCredential` for Blob Storage, Document Intelligence, and Service
Bus; no Azure key or connection string is used for those clients.

When a blob is created in the private `documents` container, the Event Grid
system topic invokes `ExtractDocument`. The function validates the event's
storage host and container, reads the document ID from the second-to-last blob
path segment, downloads the blob with managed identity, and analyzes it with
the `prebuilt-read` model. It sends JSON for the shared
`DocumentMessage` to the `extracted-documents` queue. The queue message has
camel-case `sourceUrl`, `documentId`, and `content` properties. Its
`MessageId` is the document GUID; queue
duplicate detection suppresses duplicate deliveries for ten minutes. The
function does not log extracted text or document content.

Service Bus Standard has a finite 256 KiB message limit. The function measures
the serialized message against the sender's actual batch limit before sending;
if the full extraction does not fit, it logs only the document ID and fails the
Event Grid invocation (so it is not reported as queued). Event Grid retries
failed delivery and eventually expires events per the configured retry policy;
it does not route those failures to the Service Bus dead-letter queue. Large
extractions therefore require a later design such as storing extracted text in
Blob Storage and queueing a reference or splitting text into bounded chunks.

The Event Grid subscription is filtered to `Microsoft.Storage.BlobCreated`
events under `/blobServices/default/containers/documents/`, sends one event per
delivery batch, and writes exhausted events into the private
`eventgrid-deadletter` container using the Event Grid system topic's
system-assigned identity. That identity has Storage Blob Data Contributor
scoped to the dead-letter container for dead-letter writes; it is separate from
the Function identity.

The subscription is deliberately gated by `bootstrapExtractor`: Azure Function
destinations reference a deployed function resource, so publish and verify the
Function code (and confirm `ExtractDocument` is indexed) before setting
`bootstrapExtractor=false` and deploying the subscription. The Bicep resource
depends on the Function App settings and dead-letter resources, but ARM cannot
order itself after a manual code publish; deployment steps below enforce that
ordering. This avoids first-deployment endpoint-resolution failures.

The Function uses the document Standard_LRS account as its host storage,
configured by the identity-based `AzureWebJobsStorage__accountName` setting.
Its system identity receives exactly three data-plane roles: Storage Blob Data
Owner on that account (both document reads and host/lease blobs), Service Bus
Data Sender on the namespace, and Cognitive Services User on Document
Intelligence. The Function uses only an Event Grid trigger and no Storage Queue
or Table bindings, so it does not need queue/table data roles. Event Grid has a
separate system-assigned identity and Storage Blob Data Contributor on the
storage account, only for its dead-letter writes.
The Service Bus namespace is Standard and disables local authentication.
Document Intelligence is S0/FormRecognizer, has a custom subdomain, and
disables local authentication. The Function App uses the built-in Linux .NET
isolated stack on a **Consumption (Y1) plan**, not a custom container or ACR
pull. Per the selected exact-three-role constraint, Function telemetry uses the
Application Insights connection string generated dynamically from the
Application Insights resource; local-auth ingestion is enabled on that
workspace-based component, and no `Monitoring Metrics Publisher` role is
assigned to the Function identity. This resource-generated connection string
identifies the telemetry component and is not a storage or Service Bus
credential. The Event Grid and Service Bus namespaces use public Azure service
endpoints; network/private-endpoint restrictions are not included in this
sprint.

### Deploy Sprint 2 from the existing Sprint 1 deployment

The resource-group deployment uses the same
[infra/main.bicepparam](infra/main.bicepparam). Keep valid Entra API values and
the current API image (`bootstrap=false`, `apiImage=<current API image>`).
Initially keep `bootstrapExtractor=true` so the Function App and its identity
are created without the Event Grid subscription. First deploy the resources and
managed-identity roles:

```powershell
az deployment group what-if --resource-group $resourceGroup `
  --parameters infra\main.bicepparam

az deployment group create --name docintake-sprint2-bootstrap `
  --resource-group $resourceGroup `
  --parameters infra\main.bicepparam

$extractorName = az deployment group show --name docintake-sprint2-bootstrap `
  --resource-group $resourceGroup --query properties.outputs.extractorName.value -o tsv
```

Deploy the .NET isolated Function code to the new Function App using Functions
Core Tools. Run publish from the extractor project directory:

```powershell
Push-Location src\DocIntake.Extractor
func azure functionapp publish $extractorName --dotnet-isolated
Pop-Location

$state = az functionapp show --name $extractorName --resource-group $resourceGroup `
  --query state -o tsv
if ($LASTEXITCODE -ne 0 -or $state -ne 'Running') {
    throw "Extractor Function App is not running (state: $state)."
}
az functionapp function list --name $extractorName --resource-group $resourceGroup `
  --query "[].name" -o tsv
```

Wait for `ExtractDocument` to appear before enabling delivery. In
`infra/main.bicepparam`, set `bootstrapExtractor = false` while preserving the
API image value, then create the final deployment:

```powershell
az deployment group what-if --resource-group $resourceGroup `
  --parameters infra\main.bicepparam
az deployment group create --name docintake-sprint2-enable-events `
  --resource-group $resourceGroup `
  --parameters infra\main.bicepparam
```

This final deployment creates the Event Grid `BlobCreated` subscription and
resolves its Azure Function destination. If Event Grid cannot resolve
`ExtractDocument`, leave `bootstrapExtractor=true`, verify function indexing
and managed-identity role assignments, wait for the host to become ready, and
retry with `bootstrapExtractor=false`. Retain the real API image and
`bootstrap=false` on every full infrastructure redeployment.

### Verify extraction and queue delivery

Use the HTTPS upload commands in deployment verification. After a successful
202 response, wait for asynchronous extraction. Function execution logs can be
checked with:

```powershell
az functionapp log tail --name $extractorName --resource-group $resourceGroup
```

The `extracted-documents` queue should receive one `DocumentMessage` for each
document ID, with `MessageId` equal to that ID. Event Grid may deliver retries;
Service Bus duplicate detection makes repeated sends with the same ID
idempotent inside its ten-minute window. The queue is intentionally not
consumed in Sprint 2. Use an approved Service Bus Explorer authenticated with
Entra, or grant your operator Service Bus Data Receiver at the queue to inspect
and peek messages. Do not grant the extractor receive permissions. Confirm
message `content` contains recognized document text, `sourceUrl` and
`documentId` match the uploaded blob, and no text or blob contents appear in
Function logs.

The automated smoke-test script uploads a sample PDF and polls the active
Service Bus queue count for up to two minutes. It needs PowerShell 7, Azure CLI
logged in with permission to read queue properties, and a valid API bearer
token in `DOCINTAKE_API_TOKEN`:

```powershell
$env:DOCINTAKE_API_TOKEN = '<short-lived API access token>'
./scripts/test-extraction-pipeline.ps1 `
  -ApiBaseUrl 'https://<api-fqdn>' `
  -SamplePdf 'C:\documents\sample.pdf' `
  -ResourceGroup $resourceGroup `
  -ServiceBusNamespace '<service-bus-namespace>'
```

The script compares the active message count before and after upload. This
detects a new queued message when there is no concurrent sender/receiver; it
does not peek message bodies or prove the new message contains the expected
document ID. Use an Entra-authenticated Service Bus Explorer to validate
`documentId`, `sourceUrl`, and non-empty `content` in the message itself.

### Monitoring

Run these in the Application Insights **Logs** blade. Successful extraction
traces should appear in the last hour:

```kusto
traces
| where timestamp > ago(1h)
| where message startswith "Extracted "
| where message contains "and queued for indexing"
| project timestamp, message, operation_Id
| order by timestamp desc
```

Failures log the document ID and blob URL, include the exception, and are
re-thrown for Event Grid retries. Query them with:

```kusto
traces
| where timestamp > ago(1h)
| where severityLevel >= 3
| where message startswith "Failed to extract and queue document "
| project timestamp, message, operation_Id, severityLevel
| order by timestamp desc
```

After delivery retries are exhausted, inspect the
`eventgrid-deadletter` container for Event Grid dead-letter blobs containing
the original event and delivery failure metadata. Dead-lettering requires the
Event Grid topic identity's Storage Blob Data Contributor assignment to have
propagated.

Successful function runs emit `Extracted {documentId} and queued for indexing`.
On any blob download, extraction, or send failure, the function logs an error
with the document ID and source URL, then rethrows so Event Grid's delivery
retry policy can retry it. It does not add another manual retry loop around
Document Intelligence. Function sampling is enabled in
[host.json](src/DocIntake.Extractor/host.json), capped at 20 telemetry items per
second, with Request telemetry excluded from sampling. Application Insights
traces and dead-letter delivery must be confirmed against deployed resources;
compilation and automated tests do not verify Azure role propagation,
Document Intelligence availability, Event Grid delivery, queue contents, or
live telemetry ingestion.
