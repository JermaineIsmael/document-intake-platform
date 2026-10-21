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
- [DocIntake.Worker](src/DocIntake.Worker): .NET 8 worker service for chunking, embedding, and indexing (Sprint 3).
- [DocIntake.Tests](tests/DocIntake.Tests): xUnit and FluentAssertions integration tests.
- [Infrastructure](infra/main.bicep): resource-group-scoped Azure deployment.
- [SQL schema](sql/schema.sql): one-time Entra-admin script for metadata tables and the worker user.
- [Container App module](infra/modules/containerApp.bicep): reusable managed-identity container app.

The existing Git repository is used as the solution root. Projects reference
Shared from Api, Extractor, and Worker; Tests reference Api, Extractor, Worker,
and Shared.
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
- A Linux Flex Consumption (FC1) Function App running .NET 8 isolated,
  with identity-based host storage and a private `extractor-packages` deployment
  container in the shared-key-disabled document storage account.
- Extractor RBAC: Storage Blob Data Owner on the document/host storage account,
  Service Bus Data Sender on the namespace, and Cognitive Services User on
  Document Intelligence.
- Event Grid system topic RBAC: Storage Blob Data Contributor on the storage
  dead-letter container only, solely to write exhausted events.

The container app module accepts name, location, environmentId, registryServer,
image, external, ingressEnabled, minReplicas, maxReplicas, envVars, and secrets. `ingressEnabled`
defaults to true for the API and is false for the background Worker, which has
no ingress or HTTP probes. `envVars` is an
array of `{ name, value }` or `{ name, secretRef }` objects. `secrets` is a secure
object wrapping an array as `{ values: [{ name, value }] }` (empty by default),
since ARM cannot represent a secure array parameter. The module outputs principalId and
fqdn, serves HTTPS ingress on port 8080, and uses TCP startup/liveness/readiness
probes. The main deployment outputs apiFqdn, acrName, storageName,
serviceBusNamespace, documentIntelligenceEndpoint, and extractorName.

### Deploy with Azure Pipelines

The [azure-pipelines.yml](infra/azure-pipelines.yml) pipeline is the deployment
entrypoint. It builds and tests the solution, provisions the Bicep resources,
builds the API and Worker images with Docker on the pipeline agent and pushes
them to ACR, deploys the Extractor package, and only
then enables the Event Grid subscription.

Configure these Azure DevOps pipeline variables before running it:

- `azureServiceConnection`: the name of an Azure Resource Manager service
  connection scoped to the target subscription.
- `resourceGroup`: the target resource group.

When creating the Azure DevOps pipeline, select the YAML path
`infra/azure-pipelines.yml`. Create the target resource group before the first
run.

The Extractor uses Flex Consumption (`FC1`), .NET 8 isolated, 2 GiB instances,
and a maximum of 40 instances, with no always-ready instances configured.
Its system-assigned identity uses the existing Storage Blob Data Owner
assignment for host storage and deployment packages. The pipeline archives
published files directly at the ZIP root and uses Azure CLI's Flex-aware
`config-zip` command without remote build (One Deploy).
After ZIP deployment, the pipeline polls the ARM site's `properties.state`
and waits for `ExtractDocument` to be indexed, for up to 30 attempts with
10-second intervals. A missing or non-Running state is not treated as ready.
Event Grid remains disabled if readiness times out or an Azure CLI check fails.
Event Grid's system-topic identity receives Storage Blob Data Contributor on
the storage account for dead-letter authorization validation, not just on the
`eventgrid-deadletter` container. This grants blob access across that account;
the subscription still writes dead letters to the private dead-letter container.
The subscription depends on the role assignment, but new RBAC permissions may
take time to propagate. After a first-run authorization failure, verify the
account-scoped assignment and retry once permissions have propagated.
Incremental deployments do not remove a previous container-scoped assignment.
The Extractor pins `Microsoft.ApplicationInsights.WorkerService` to 2.23.0
to match the Application Insights 2.x components used by the Functions
Application Insights integration. Upgrading it to 3.x independently can cause
an `ITelemetryInitializer` type-load failure before functions are indexed.
Verify Flex Consumption availability and regional memory quota before deployment;
FC1 removes the Y1 quota dependency but has its own quotas. Existing Y1 plans/apps
cannot be converted by simply redeploying this template: use a supported migration
or an explicitly approved recreation of the Extractor resources first. The
pipeline does not delete existing apps or plans.

The service connection identity needs permission to create/update the resources
in `main.bicep` and create role assignments (for example, Contributor plus
Role Based Access Control Administrator at the resource-group scope). It also
needs `AcrPush` on the registry (or inherited from the resource group) for an
RBAC-mode registry. If registry repository ABAC permissions are enabled, use the
appropriate Container Registry Repository Writer assignment instead. The
pipeline authenticates Docker with `az acr login` using the service connection;
ACR admin credentials and ACR Tasks are not required. Microsoft-hosted
`ubuntu-latest` agents provide Docker; self-hosted agents must have a running
Linux-container Docker daemon. This avoids subscription restrictions such as
`TasksOperationsNotAllowed` from `az acr build`. Ensure
[main.bicepparam](infra/main.bicepparam) contains valid environment values,
including the API Entra authority/audience, SQL administrator group name and
object ID, and local Worker principal ID. Do not leave placeholders in required
parameters.

The API explicitly sets `ASPNETCORE_URLS=http://+:8080` so the bootstrap sample
and the production API use the same listener as ingress and TCP health probes.
The Worker attaches a dedicated user-assigned identity named
`<prefix>-worker-pull` for private ACR image pulls. Its `AcrPull` assignment is
created before the Worker module runs, avoiding a first-deployment dependency
on the Worker's system identity becoming available. The system identity remains
in use for application access, Service Bus scaling, and SQL grants; existing
system-identity role assignments are preserved. The service connection also
needs permission to create and assign user-assigned managed identities (included
in Contributor at the resource-group scope). New RBAC assignments can take time
to propagate; if image pulls still fail, inspect Worker system logs and verify
the pull identity's registry role before retrying.
If either infrastructure deployment fails, the pipeline prints failed ARM
operations (including the nested API and Worker deployments), API/Worker revision provisioning
details, and up to 100 system log entries. System log collection is limited to
60 seconds per app. Unavailable diagnostics produce warnings without replacing
the original deployment failure exit code.

This environment sets `sqlLocation = 'westus3'` independently of other resource
locations and `sqlServerNameOverride = 'di-docintake-dev-sql-wus3-20261021'` to
validate a fresh SQL server name without attempting to relocate the previous
server. Both are candidates: validate regional provisioning access and global
name availability before deployment. An empty `sqlServerNameOverride` preserves
the original generated name. Changing the override creates a different server;
it does not migrate databases or delete the old server. Worker SQL connection
settings and the SQL server FQDN output follow the selected name. Local Worker
role assignments and chat remain disabled through `enableLocalWorkerAccess`
and `deployChat`.

For the **first run only**, queue the pipeline with
`provisionInfrastructure=true`. This creates the base resources and ACR, then
deploys all three projects. For normal releases, leave it false: the pipeline
expects the resource group to contain the infrastructure and exactly one ACR.
Image tags use the Azure DevOps build ID.

Chat deployment is optional: `deployChat` defaults to `true` in the template,
but this environment's parameters set it to `false` because chat quota is zero.
Uploads, extraction, and embedding/indexing remain enabled. The API logs a
startup warning and returns HTTP 503 with `{"error":"Chat is disabled for this environment."}`
for valid authenticated `/ask` requests while disabled; invalid questions still
return HTTP 400. The `chatDeploymentName` output is empty when disabled.
After obtaining quota, set `deployChat = true` and redeploy with the desired
`chatSkuName` and `chatCapacity`. Disabling chat does not delete a previously
created model deployment in an incremental deployment or reclaim its quota.

The Worker Container App uses its managed identity to scale from the Service
Bus queue via `custom.identity = 'system'` on the scale rule, not a
`useManagedIdentity` metadata property. The identity requires the existing
Azure Service Bus Data Receiver role assignment.
After deployment, create its SQL database user
and grant database roles using [sql/schema.sql](sql/schema.sql); that SQL
operation is not performed by the pipeline.

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

Bootstrap omits private ACR registry authentication while running the public
sample image. This lets the API identity provision before its `AcrPull` role
assignment is created. Final deployments restore system-identity registry
authentication. Configuring private registry credentials during bootstrap can
otherwise cause ACR token-exchange HTTP 401 errors and revision timeouts.

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
isolated runtime on a **Flex Consumption (FC1) plan**, not a custom container or ACR
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

$state = az resource show --name $extractorName --resource-group $resourceGroup `
  --resource-type Microsoft.Web/sites --api-version 2024-04-01 `
  --query properties.state -o tsv
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

## Sprint 3: search, embeddings, and metadata foundation

Sprint 3 adds `DocIntake.Worker` (a .NET 8 worker service, run locally this
sprint) plus shared chunking in
[TextChunker.cs](src/DocIntake.Shared/TextChunker.cs). The worker creates the
`document-chunks` Azure AI Search index idempotently through
[IndexSchema.cs](src/DocIntake.Worker/IndexSchema.cs).

New resources in [infra/main.bicep](infra/main.bicep):

| Resource | Configuration |
| --- | --- |
| Azure AI Search | `basic`, 1 replica, 1 partition, `disableLocalAuth: true` (Entra ID roles only; no admin/query keys) |
| Azure OpenAI | `S0`, `disableLocalAuth: true`, custom subdomain, one `text-embedding-3-small` (version `1`) deployment; no chat model yet |
| Azure SQL | Entra-only authentication (no SQL login/password), `Basic` database `docintake` |

The index vector field uses 1536 dimensions, the default (and maximum) output
of `text-embedding-3-small`. That model accepts a smaller `dimensions` request
option; do not set it unless `IndexSchema.EmbeddingDimensions` changes too.

### Deploy prerequisites

- An Entra **security group** to administer SQL, with you as a member. Set
  `sqlAdminGroupName` and `sqlAdminGroupObjectId` in
  [infra/main.bicepparam](infra/main.bicepparam):

  ```powershell
  az ad group show --group "<group name>" --query id -o tsv
  ```

- `text-embedding-3-small` availability and quota in `openAiLocation`
  (defaults to the resource-group region) for `embeddingSkuName`
  (default `Standard`) and `embeddingCapacity` (default 30K TPM):

  ```powershell
  az cognitiveservices model list -l <region> `
    --query "[?kind=='OpenAI' && model.name=='text-embedding-3-small'].{version:model.version,skus:model.skus[].name}"
  az cognitiveservices usage list -l <region> -o table
  ```

  If `Standard` is unavailable in your region, set `embeddingSkuName` to
  `GlobalStandard` or choose another `openAiLocation`.

- Your own Entra user object ID as `workerPrincipalId` (local development
  placeholder; Sprint 4 replaces it with the worker Container App identity).
  It receives Search Index Data Contributor, Search Service Contributor,
  Cognitive Services OpenAI User, and Azure Service Bus Data Receiver:

  ```powershell
  az ad signed-in-user show --query id -o tsv
  ```

Deploy with the same `az deployment group create` command used for previous
sprints, keeping the existing API image and `bootstrapExtractor` values.

### Create the SQL schema

The SQL server has no firewall rules by default and has no SQL logins, so
connect with Entra authentication as a member of the admin group. Allow your
client IP, then run [sql/schema.sql](sql/schema.sql).

Use the modern Go-based `sqlcmd` (`winget install sqlcmd`), which supports
Entra authentication methods. The legacy ODBC `sqlcmd` that ships with SQL
Server tools does **not** support `--authentication-method`; with it, use
`-G` plus a separately installed ODBC driver, or use a GUI client instead.

```powershell
$deployment = '<deployment name>'
$sqlServer = az deployment group show --name $deployment `
  --resource-group $resourceGroup --query properties.outputs.sqlServerFqdn.value -o tsv
$myIp = (Invoke-RestMethod https://api.ipify.org)
az sql server firewall-rule create --resource-group $resourceGroup `
  --server $sqlServer.Split('.')[0] --name local-dev `
  --start-ip-address $myIp --end-ip-address $myIp

# Uses your az login / Visual Studio identity (no prompt)...
sqlcmd -S $sqlServer -d docintake --authentication-method ActiveDirectoryDefault `
  -i sql\schema.sql
# ...or a browser sign-in:
sqlcmd -S $sqlServer -d docintake --authentication-method ActiveDirectoryInteractive `
  -U <your UPN> -i sql\schema.sql
```

GUI alternative: SQL Server Management Studio or the VS Code **SQL Server
(mssql)** extension, connecting to `$sqlServer`, database `docintake`, with
**Microsoft Entra ID - Universal with MFA** authentication, then running the
script. (Azure Data Studio was retired; use SSMS or the VS Code extension.)

Before running the script, replace `<worker-identity-name>` or remove the
worker-user section. In Sprint 3 the worker runs under your own Entra identity
(admin-group members already have access); in Sprint 4 the placeholder becomes
the worker Container App's managed identity name.

## Sprint 3: Running the worker locally

The worker consumes `extracted-documents`, splits `content` into 800-character
chunks with 100 characters of overlap, embeds them in batches, uploads them to
the `document-chunks` index, and upserts one row per document into
`dbo.Documents`.

There is **no fully local option** this sprint, unlike Sprint 1's Azurite
setup: Azure AI Search and Azure OpenAI have no practical local emulators,
and the worker's purpose is to exercise the real Entra-authenticated Service
Bus, Search, OpenAI, and SQL resources. Run the worker on your machine against
the deployed resources. `DefaultAzureCredential` uses your `az login` (or
Visual Studio) identity, which is `workerPrincipalId` in Bicep and an SQL
admin-group member.

### Configure and run

Complete the Sprint 3 deploy and schema steps above, then:

```powershell
az login --tenant <tenant-id>
$deployment = '<deployment name>'
function Get-Output($name) {
  az deployment group show --name $deployment --resource-group $resourceGroup `
    --query "properties.outputs.$name.value" -o tsv
}
$env:ServiceBus__Namespace = "$(Get-Output serviceBusNamespace).servicebus.windows.net"
$env:Search__Endpoint = Get-Output searchEndpoint
$env:OpenAI__Endpoint = Get-Output openAiEndpoint
$env:OpenAI__EmbeddingDeployment = Get-Output embeddingDeploymentName
$env:Sql__Server = Get-Output sqlServerFqdn
$env:Sql__Database = Get-Output sqlDatabaseName
# Optional: send worker traces/logs to Application Insights.
$env:APPLICATIONINSIGHTS_CONNECTION_STRING = az monitor app-insights component show `
  --resource-group $resourceGroup --query "[0].connectionString" -o tsv

dotnet run --project src\DocIntake.Worker
```

Role assignments can take a few minutes to propagate after deployment; 403
errors right after deploying usually clear on their own. Expected startup log
lines: `Search index document-chunks is ready` and `Processing queue
extracted-documents with MaxConcurrentCalls 4`. Restarting the worker is safe:
the index is created with `CreateOrUpdateIndexAsync` and existing documents
are kept.

Settings (all overridable with `Section__Key` environment variables, see
[appsettings.json](src/DocIntake.Worker/appsettings.json)):

| Setting | Default | Notes |
| --- | --- | --- |
| `ServiceBus__QueueName` | `extracted-documents` | |
| `ServiceBus__MaxConcurrentCalls` | `4` | Tuning knob: throughput vs. OpenAI TPM (429s) and SQL/Search concurrency |
| `ServiceBus__MaxDeliveryCount` | `5` | Must match the queue's `maxDeliveryCount` |
| `ServiceBus__MaxAutoLockRenewalDuration` | `00:10:00` | Renews the 1-minute message lock while a document is processed |
| `Indexing__EmbeddingBatchSize` | `100` | Inputs per embeddings request |
| `Indexing__SearchUploadBatchSize` | `250` | Documents per Search upload (service limit 1000 / 16 MB) |

### Message handling and redelivery

- Success: the message is completed.
- Failure (OpenAI, Search, or SQL): logged with the document ID and delivery
  count, then abandoned for redelivery. On delivery 5 it is dead-lettered with
  reason `IndexingFailed` and the exception message.
- A body that can never succeed (invalid JSON, missing `documentId`, or a
  `sourceUrl` that is not an absolute HTTPS URL up to 2048 characters) is
  dead-lettered immediately with reason `InvalidDocumentMessage`.
- Transport errors (connection, authorization, lock renewal) are logged by the
  processor error handler; the processor keeps reconnecting.

Redelivery is safe: chunk IDs are deterministic (`{documentId}-{chunkIndex}`),
so re-uploading overwrites the same Search documents, and the SQL write is an
`UPDLOCK, SERIALIZABLE` update-then-insert keyed on `Id`. If a lock is lost
despite renewal, the duplicate processing converges on the same final state.

### End-to-end verification

1. Start the worker as above and leave it running.
2. Upload a document with Sprint 1's API (see
   [Verify the deployed API](#5-verify-the-deployed-api-blobs-and-roles)) and
   note the `documentId` in the 202 response:

   ```powershell
   $response = curl.exe -s -H "Authorization: Bearer $accessToken" `
     -F "file=@C:\documents\sample.pdf" "https://${apiFqdn}/documents" | ConvertFrom-Json
   $documentId = $response.documentId
   ```

3. Within about 30 seconds Sprint 2's extractor queues the message; the worker
   then logs `Indexed <documentId> with <n> chunks`.
4. Query AI Search for that document's chunks. Your identity has Search Index
   Data Contributor; Search does not accept keys (`disableLocalAuth`):

   ```powershell
   $storageName = Get-Output storageName
   $sourceUrl = az storage blob list --account-name $storageName --container-name documents `
     --prefix "$documentId/" --auth-mode login --query "[0].name" -o tsv |
     ForEach-Object { "https://$storageName.blob.core.windows.net/documents/$_" }
   $searchToken = az account get-access-token --resource https://search.azure.com `
     --query accessToken -o tsv
   $body = @{ search = '*'; filter = "source eq '$sourceUrl'"; count = $true; top = 0 } |
     ConvertTo-Json
   Invoke-RestMethod -Method Post -ContentType 'application/json' `
     -Headers @{ Authorization = "Bearer $searchToken" } -Body $body `
     -Uri "$(Get-Output searchEndpoint)/indexes/document-chunks/docs/search?api-version=2024-07-01" |
     Select-Object '@odata.count'
   ```

   Listing blobs requires a Storage Blob Data Reader (or higher) role for your
   user; alternatively copy `sourceUrl` from the queue message or the portal.
   Blob names containing spaces or other reserved characters are URL-encoded
   in `sourceUrl`; copy the exact value in that case. In the portal, use the
   search service's **Search explorer** with the same filter.

5. Query the metadata row; `ChunkCount` must equal `@odata.count`:

   ```powershell
   sqlcmd -S $sqlServer -d docintake --authentication-method ActiveDirectoryDefault `
     -Q "SELECT Id, SourceUrl, ChunkCount, IndexedAt FROM dbo.Documents WHERE Id = '$documentId'"
   ```

**Redelivery check.** Stop the worker and upload another document. In the
portal, open the namespace's **Service Bus Explorer** on `extracted-documents`,
choose **Receive** in **PeekLock** mode, and **Abandon** the message
(`DeliveryCount` increases). Start the worker; it processes the redelivered
message. Repeat steps 4-5: the Search count equals `ChunkCount`, with exactly
one `dbo.Documents` row. Running the same message through the worker twice
leaves the same result.

**Large document check.** Upload a `.docx` or text PDF with at least 35,100
characters of text (50+ chunks) but well under ~200,000 characters (the
Sprint 2 extractor rejects messages over the 256 KiB Service Bus limit). For
example, with `pip install python-docx`:

```powershell
python -c "import docx; d=docx.Document(); [d.add_paragraph(f'Paragraph {i}: ' + 'The quick brown fox jumps over the lazy dog. ' * 12) for i in range(80)]; d.save('large.docx')"
```

Verify steps 3-5 report the same 50+ chunk count. If processing exceeds the
queue's 1-minute lock, the worker's automatic lock renewal keeps the message
locked (no `MessageLockLost` errors in the logs and `DeliveryCount` stays 1).

## Sprint 4: RAG /ask endpoint and Worker containerization

Sprint 4 adds a retrieval-augmented generation (RAG) `/ask` endpoint to the API
and containerizes + deploys the Worker to the managed Container Apps environment
with KEDA scaling. The Worker processes embedded documents from Sprint 3,
answering free-form questions over indexed content via Azure OpenAI chat.

### Changes

1. **API RAG service** — Adds `IRagService` (in [Services/RagService.cs](src/DocIntake.Api/Services/RagService.cs)).
   The `/ask` endpoint (`Endpoints/AskEndpoints.cs`) accepts POST with `{ "question": "..." }` and
  returns `{ "answer": "...", "sources": [...] }` or 404 if no configuration is present.

   **Note:** Full vector search integration is pending Azure.Search.Documents SDK API stabilization.
   Current implementation returns 404; once the SDK is updated, implement embedding generation,
   vector search configuration via `VectorSearchOptions`, and chat completion with retrieved context.
   The endpoint and interface are complete; only the internal embeddings/search calls remain.

2. **Key Vault secret for AI Search query key** — `main.bicep` stores the search service's
   query key in Key Vault, and the API managed identity receives the "Key Vault Secrets User"
   role to retrieve it. This demonstrates RBAC-based secret management (vs. hardcoded keys).
   In production, prefer direct RBAC on Search service; Key Vault secret access is optional.

3. **Worker Docker image** — [Dockerfile](src/DocIntake.Worker/Dockerfile) multi-stage build
   (SDK → publish → .NET runtime), non-root user, no exposed HTTP ports.
   The publish stage inherits the build stage so `--no-build` can reuse both
   build outputs and the restored NuGet package cache. Only published files
   are copied into the runtime image.

4. **Worker Container App deployment** — `main.bicep` conditionally deploys the Worker
   if the `workerImage` parameter is non-empty. The Worker:
   - Processes messages from the `extracted-documents` Service Bus queue.
   - Calls Azure AI Search to index document chunks (created by Sprint 3).
   - Updates SQL metadata with chunk counts and timestamp.
   - Uses KEDA custom autoscaling tied to queue depth (~10 messages per replica,
     minReplicas: 0, maxReplicas: 5).
   - Environment variables passed from config: `ServiceBus__Namespace`,
     `OpenAI__Endpoint`, `OpenAI__EmbeddingDeployment`, `Search__Endpoint`,
     `Sql__Server`, `Sql__Database`.

5. **Worker managed identity role assignments** — The Worker's Container App
   system-assigned identity receives:
   - "Search Index Data Contributor" and "Search Service Contributor" on Search (allows creating index).
   - "Cognitive Services OpenAI User" on Azure OpenAI.
   - "Azure Service Bus Data Receiver" on the Service Bus namespace.
   - "AcrPull" on the container registry (for pulling the image).
  - "Monitoring Metrics Publisher" on Application Insights (for telemetry export).

   **Sprint 3 compatibility note:** The `workerPrincipalId` role assignments (for local dev)
   remain in the Bicep; Sprint 4's Container App identity assignments are in addition.
   After verifying the Container App worker is functional, the Sprint 3 local-dev assignments
   can be removed.

6. **SQL database user for Worker** — [sql/schema.sql](sql/schema.sql) includes a placeholder
   for creating a contained database user from the Worker's managed identity:
   ```sql
   CREATE USER [worker-container-app-name] FROM EXTERNAL PROVIDER;
   ALTER ROLE db_datawriter ADD MEMBER [worker-container-app-name];
   ALTER ROLE db_datareader ADD MEMBER [worker-container-app-name];
   ```
   Run this once the Worker Container App is deployed (Sprint 4) with its real managed identity
   display name.

### Deploying Sprint 4

**Prerequisite:** Sprint 1, 2, and 3 are already deployed and functional. The API and Worker
must run against real Azure Search, OpenAI, Service Bus, and SQL resources (no local emulators
exist for these services).

#### Step 1: Add chat model to Azure OpenAI

The `main.bicep` already includes an Azure OpenAI resource with `text-embedding-3-small`
for the Worker (Sprint 3). Sprint 4 adds a chat model deployment. Before deploying Bicep:

1. If you deployed Sprints 1–3 with `main.bicep` (without `workerImage`), the OpenAI resource
   exists but only has the embedding deployment.
2. Redeploy `main.bicep` with your existing resource group and parameters to add the chat
   deployment. Or manually add a `gpt-4` or `gpt-35-turbo` deployment via the portal if you
   prefer to skip redeployment.

#### Step 2: Prepare Worker Docker image

From the repository root, build and push the Worker image to your container registry:

```powershell
# Log in to the registry (e.g. Azure Container Registry):
az acr login --name $acrName

# Option A: build locally and push
docker build -f src\DocIntake.Worker\Dockerfile -t docintake-worker:sprint4 .
docker tag docintake-worker:sprint4 "${acrName}.azurecr.io/docintake-worker:sprint4"
docker push "${acrName}.azurecr.io/docintake-worker:sprint4"

# Option B: build in the cloud (avoids local resources)
az acr build --registry $acrName --image "docintake-worker:sprint4" `
  --file src\DocIntake.Worker\Dockerfile .
```

#### Step 3: Deploy Worker via Bicep

Update [main.bicepparam](infra/main.bicepparam) with:

```bicepparam
param workerImage = "${acrName}.azurecr.io/docintake-worker:sprint4"
```

Then redeploy:

```powershell
$resourceGroup = 'docintake-rg'  # or your resource group name
az deployment group create --resource-group $resourceGroup `
  --template-file infra/main.bicep --parameters infra/main.bicepparam
```

Wait for the deployment to complete. Note the Worker's managed identity display name
(visible in the Container App's Identity section or from Bicep outputs).

#### Step 4: Create the Worker's SQL database user

Copy the Worker's managed identity display name from Step 3. Connect to the SQL database
as an Entra admin and run the contents of [sql/schema.sql](sql/schema.sql), replacing
`<worker-identity-name>` with that display name. For example, using `sqlcmd`:

```powershell
# Get the Worker's display name (usually the Container App name)
$workerDisplayName = 'docintake-worker'  # or the Container App resource name

# Connect with Entra auth
sqlcmd -S 'docintake-sqlserver.database.windows.net' -d docintake `
  -G -U '${env:USERNAME}@${env:USERDOMAIN}' `
  -i sql/schema.sql
```

Or in [Azure Data Studio](https://learn.microsoft.com/en-us/azure-data-studio/),
connect using **Azure Authentication** and paste the SQL from the file manually,
replacing the placeholder.

#### Step 5: Upload a document end-to-end

1. Upload a document via the Sprint 1 API:
   ```powershell
   $response = curl.exe -s -H "Authorization: Bearer $token" `
     -F "file=@C:\documents\sample.pdf" "https://${apiFqdn}/documents" | ConvertFrom-Json
   $documentId = $response.documentId
   ```

2. Wait ~30 seconds for Sprint 2's extractor to process it.

3. Verify the Worker logs in Application Insights:
   ```kusto
   traces
   | where timestamp > ago(5m)
   | where message startswith "Indexed"
   | project timestamp, message
   ```

4. Query the AI Search index to confirm chunks were indexed:
   ```powershell
   $searchToken = az account get-access-token --resource https://search.azure.com --query accessToken -o tsv
   $body = @{ search = '*'; count = $true } | ConvertTo-Json
   Invoke-RestMethod -Method Post -ContentType 'application/json' `
     -Headers @{ Authorization = "Bearer $searchToken" } -Body $body `
     -Uri "$(Get-Output searchEndpoint)/indexes/document-chunks/docs/search?api-version=2024-07-01" |
     Select-Object '@odata.count'
   ```

5. Verify the SQL metadata row (same as Sprint 3):
   ```powershell
   sqlcmd -S $sqlServer -d docintake --authentication-method ActiveDirectoryDefault `
     -Q "SELECT Id, SourceUrl, ChunkCount, IndexedAt FROM dbo.Documents WHERE Id = '$documentId'"
   ```

#### Step 6: Test the /ask endpoint

Once the Worker is running and documents are indexed, test the `/ask` endpoint.
**Current behavior:** Returns 404 until vector search SDK implementation is complete.
**Expected final behavior** (after SDK fix):

```powershell
$askBody = @{ question = "What is the main topic of this document?" } | ConvertTo-Json
$token = # your Entra token
Invoke-RestMethod -Method Post -ContentType 'application/json' `
  -Headers @{ Authorization = "Bearer $token" } -Body $askBody `
  -Uri "https://${apiFqdn}/ask"
```

Returns:
```json
{
  "answer": "...",
  "sources": ["https://storage.../documents/blob1.pdf", ...]
}
```

### Worker autoscaling

The Worker's Container App uses KEDA to autoscale based on Service Bus queue depth.
By default, replicas scale from 0 (no replicas when idle) to 5. The scale rule
triggers when the queue depth reaches ~10 messages per replica. To adjust:

1. Edit [modules/containerApp.bicep](infra/modules/containerApp.bicep) scale rule
   or pass a custom `scaleRule` object in the Worker's module invocation.
2. Redeploy.
3. Monitor in the Container Apps portal under **Revision management**; note that
   scale-down to zero may take 5–10 minutes after the queue empties.

### Troubleshooting

**Worker logs are missing:** Check that the managed identity has "Monitoring Metrics
Publisher" on the Application Insights resource, and that
`APPLICATIONINSIGHTS_CONNECTION_STRING` is configured in the Container App's
environment variables.

**Search index not created:** Verify the Worker's managed identity has "Search Service
Contributor". The first startup creates the index; restarting the Worker should not
error on index creation (idempotent via `CreateOrUpdateIndexAsync`).

**SQL insert/update fails:** Check that the `<worker-identity-name>` user was created
with `FROM EXTERNAL PROVIDER` and that both `db_datawriter` and `db_datareader` roles
are granted.

**Queue messages not being processed:** Check KEDA's state in the portal (Application
Insights → Logs → filter `type='Container App'`), and verify the queue is not full.
If queue messages are accumulating, scale up manually by editing the Container App's
replicas or adjust the KEDA threshold.

**/ask endpoint always returns 404:** This is expected while the Azure.Search.Documents
vector search API is pending finalization. Once the SDK is updated, regenerate the
`RagService` implementation and redeploy the API.

## Sprint 5: Hardening, Resilience, and Observability

Sprint 5 closes gaps before production use: prompt injection resistance, cost guardrails,
dead-letter recovery, end-to-end tracing, load testing, and a security audit.

### Key Changes

1. **Prompt Injection Resistance** ([docs/runbooks/prompt-injection-notes.md](docs/runbooks/prompt-injection-notes.md))
   - System prompt in [RagService.cs](src/DocIntake.Api/Services/RagService.cs) explicitly instructs the model
     that retrieved document text is untrusted data, not instructions
   - Model is forbidden from following embedded directives, revealing the system prompt, or acknowledging fake overrides
   - Tested against a payload within a document; hardening confirmed

2. **Access Scoping**
   - Single-tenant deployment: all authenticated users can access all indexed documents
   - Multi-tenant variant not implemented (would require owner field in AI Search and SQL, thread through upload/search)
   - Document this design in your deployment notes if scope isolation becomes a requirement

3. **Cost Guardrails** ([docs/runbooks/cost-guardrails.md](docs/runbooks/cost-guardrails.md))
   - Service Bus `maxDeliveryCount: 5` bounds retry costs
   - Worker Container App `maxReplicas: 5` bounds scale-out cost
   - Function App on Flex Consumption (FC1) plan: on-demand billing, no always-ready instances
   - AI Search Basic tier: fixed monthly cost (~$75), 1 replica, 1 partition
   - Azure OpenAI: 30K TPM (embeddings) + 10K TPM (chat) fixed capacity
   - SQL Database: Basic tier (~$5-6/month)
   - Budget alert configured at `$50/month` (80% and 100% notifications) with email (overridable via `monthlyBudgetAmount` parameter)
   - Estimated dev cost: ~$250-300/month at light usage

4. **Dead-Letter Recovery** ([docs/runbooks/dead-letter-recovery.md](docs/runbooks/dead-letter-recovery.md))
   - Service Bus and Event Grid dead-letter queues documented
   - Procedures to inspect, diagnose, and resubmit messages included
   - PowerShell script [scripts/resubmit-dead-letter.ps1](scripts/resubmit-dead-letter.ps1) automates message recovery

5. **Outage Recovery Test** ([docs/runbooks/outage-recovery-test.md](docs/runbooks/outage-recovery-test.md))
   - Procedure to temporarily revoke a dependency (AI Search or OpenAI) and verify recovery
   - No documents are lost; messages are queued and retried when access is restored
   - Recovery time: 1-5 minutes after role restoration

6. **End-to-End Trace Propagation** ([docs/runbooks/trace-propagation.md](docs/runbooks/trace-propagation.md))
   - W3C Trace Context (W3C W3C-Trace-Context) now embedded in Service Bus message ApplicationProperties
   - Extractor [ExtractDocument.cs](src/DocIntake.Extractor/ExtractDocument.cs) extracts Activity.Current.Id and embeds as "traceparent"
   - Worker [IngestionWorker.cs](src/DocIntake.Worker/IngestionWorker.cs) parses traceparent and starts linked Activity
   - Single operation_Id now links API request → Function execution → Worker processing in Application Insights
   - Kusto query to verify in Monitoring section below

7. **Load Test** ([scripts/load-test-upload.ps1](scripts/load-test-upload.ps1))
   - Upload N documents concurrently (default 20)
   - Reports HTTP 202 success count vs. failures
   - Follow-up SQL query [scripts/test-load-completion.sql](scripts/test-load-completion.sql) to verify indexing
   - Watch for HTTP 429 (throttling) from AI Search or OpenAI during burst

8. **Secrets Audit** ([docs/SECURITY_AUDIT.md](docs/SECURITY_AUDIT.md))
   - No hardcoded API keys, passwords, or connection strings in source
   - Managed identity + RBAC used exclusively for Azure service authentication
   - Storage shared keys disabled; SQL Entra-only auth; all services with `disableLocalAuth: true`
   - Local settings properly gitignored
   - Audit passed; system is production-ready from a credential security perspective

### Known Limitations

- **Prompt Injection**: Mitigation tested against one concrete case; other injection techniques may exist. System prompt hardening raises the bar but is not exhaustive.
- **Access Scoping**: Single-tenant; all authenticated users query all documents. Multi-tenant variant deferred.
- **Throttling**: If Worker load exceeds OpenAI or Search capacity, HTTP 429s occur. Increase TPM or reduce `MaxConcurrentCalls` in Worker settings.
- **Vector Search**: `/ask` endpoint still returns 404 pending Azure.Search.Documents SDK stabilization.

## Monitoring

### View End-to-End Request Trace

```kusto
union requests, dependencies, traces
| where operation_Id == "<operation-id-from-upload>"
| project timestamp, itemType, name, target, duration, success
| order by timestamp asc
```

Expected columns:
- API request (POST /documents)
- Document Intelligence dependency (extract text)
- Service Bus dependency (send message)
- Worker trace logs (Indexed document)

All share one operation_Id.

### Check Budget Spending

```powershell
az costmanagement query \
  --timeperiod-from 2024-10-01T00:00:00Z --timeperiod-to 2024-10-31T23:59:59Z \
  --scope "/subscriptions/$(az account show -q id -o tsv)/resourcegroups/<rg>" \
  --dataset '{ "granularity": "Monthly", "aggregation": { "totalCost": { "name": "PreTaxCost" } } }' \
  --query "properties.rows"
```

### Monitor Worker Scaling

```powershell
az containerapp revision list --name di-dev-<suffix>-worker \
  --resource-group <rg> \
  --query "[].{active:properties.active,replicas:properties.replicas,status:properties.healthState}"
```

During load test: replicas should increase; after queue empties, scale back toward 0 over 5-10 minutes.

### Inspect Dead-Letter Queue

```powershell
az servicebus queue show --resource-group <rg> --namespace-name <ns> \
  --name extracted-documents \
  --query "{ActiveMessages:messageCount, DeadLetter:deadLetterMessageCount}"
```

Expected: ActiveMessages=0 (or ≤ current batch), DeadLetter=0 (or resolved).

### Search Query with Throttling Alert

```kusto
requests
| where timestamp > ago(1h)
| where name contains "OpenAI" or name contains "Search"
| where resultCode == "429"
| summarize Count=count() by name, resultCode
```

If results exist, either increase TPM or reduce Worker concurrency.
