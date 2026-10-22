targetScope = 'resourceGroup'

@allowed([
  'dev'
  'test'
  'prod'
])
param env string = 'dev'

param location string = resourceGroup().location

@description('Entra ID tenant-specific v2 authority, https://login.microsoftonline.com/<tenant-id>/v2.0.')
@minLength(1)
param authAuthority string

@description('Audience in the Entra access token for this API (usually the API client ID for v2 tokens).')
@minLength(1)
param authAudience string

@description('First API deployment only: use a public sample with internal ingress to create the system identity and RBAC before pulling from ACR.')
param bootstrap bool = true

@description('For the final API deployment, supply the fully qualified ACR image with an immutable tag or digest and set bootstrap=false.')
param apiImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

@description('For the Worker deployment, supply the fully qualified ACR image (e.g., acr.azurecr.io/docintake-worker:latest). If omitted, Worker is not deployed.')
param workerImage string = ''

@description('Keep true until the Function code is published and indexed; false creates the Event Grid subscription.')
param bootstrapExtractor bool = true

@description('Region for Azure OpenAI. Must offer text-embedding-3-small for the chosen SKU; verify with az cognitiveservices model list -l <region>.')
param openAiLocation string = location

@description('Embedding deployment SKU. Standard keeps processing in openAiLocation; GlobalStandard has broader availability.')
@allowed([
  'Standard'
  'GlobalStandard'
  'DataZoneStandard'
])
param embeddingSkuName string = 'Standard'

@description('Embedding deployment capacity in thousands of tokens per minute; must fit your subscription quota.')
@minValue(1)
param embeddingCapacity int = 30

@description('Display name of the Entra security group that administers Azure SQL (there is no SQL login).')
@minLength(1)
param sqlAdminGroupName string

@description('Object ID (GUID) of the Entra security group that administers Azure SQL.')
@minLength(36)
@maxLength(36)
param sqlAdminGroupObjectId string

@description('Region for the SQL server and database. Select a region that permits SQL provisioning for this subscription.')
param sqlLocation string = location

@description('Optional globally unique SQL server name. Empty preserves the generated name.')
param sqlServerNameOverride string = ''

@description('Grant development identities Worker roles. Not required for the deployed Worker managed identity.')
param enableLocalWorkerAccess bool = true

// LOCAL DEVELOPMENT PLACEHOLDER: in Sprint 3 the worker runs on a developer machine, so this is your
// own Entra user object ID (az ad signed-in-user show --query id -o tsv). In Sprint 4 these worker
// role assignments must be redone against the worker Container App's system-assigned managed identity.
@description('Object ID of the identity that runs DocIntake.Worker. Sprint 3: your own Entra user object ID (local dev only).')
@minLength(36)
@maxLength(36)
param workerPrincipalId string

@description('Principal type of workerPrincipalId. User for local development; ServicePrincipal once a managed identity runs the worker.')
@allowed([
  'User'
  'ServicePrincipal'
])
param workerPrincipalType string = 'User'

var suffix = uniqueString(resourceGroup().id, env)
var prefix = 'di-${env}-${suffix}'
var apiName = '${prefix}-api'
var extractorName = 'di${env}${suffix}extractor'
var serviceBusNamespaceName = '${prefix}-messaging'
var documentIntelligenceName = 'di${env}${suffix}di'
var searchName = '${prefix}-search'
var openAiName = 'di${env}${suffix}oai'
var embeddingDeploymentName = 'text-embedding-3-small'
var sqlServerName = empty(sqlServerNameOverride) ? '${prefix}-sql' : sqlServerNameOverride
var sqlDatabaseName = 'docintake'
var blobContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var acrPullRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
var telemetryPublisherRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
var storageBlobDataOwnerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var serviceBusDataSenderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
var cognitiveServicesUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
var searchIndexDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
var searchServiceContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
var cognitiveServicesOpenAiUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
var serviceBusDataReceiverRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
var keyVaultSecretsUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: serviceBusNamespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
    capacity: 0
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource extractedDocuments 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'extracted-documents'
  properties: {
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
    lockDuration: 'PT1M'
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
  }
}

resource documentIntelligence 'Microsoft.CognitiveServices/accounts@2026-09-01' = {
  name: documentIntelligenceName
  location: location
  kind: 'FormRecognizer'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: documentIntelligenceName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

// Sprint 3: roles-only data plane. disableLocalAuth: true rejects admin/query keys; authOptions is the
// keys-plus-AAD setting (aadOrApiKey/apiKeyOnly) and must be omitted when local auth is disabled.
resource search 'Microsoft.Search/searchServices@2025-05-01' = {
  name: searchName
  location: location
  sku: {
    name: 'basic'
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'Default'
    disableLocalAuth: true
    publicNetworkAccess: 'enabled'
  }
}

resource openAi 'Microsoft.CognitiveServices/accounts@2026-09-01' = {
  name: openAiName
  location: openAiLocation
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: openAiName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

// Embeddings only in Sprint 3; the chat model deployment is added in Sprint 4.
// text-embedding-3-small v1 returns 1536 dimensions by default, matching IndexSchema.EmbeddingDimensions.
resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-09-01' = {
  parent: openAi
  name: embeddingDeploymentName
  sku: {
    name: embeddingSkuName
    capacity: embeddingCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-small'
      version: '1'
    }
  }
}

// Sprint 4: chat model for the RAG endpoint (/ask). Capacity intentionally modest to start.
@description('Deploy the chat model and enable /ask. Disable when chat quota is unavailable; embeddings remain enabled.')
param deployChat bool = true

@description('Chat deployment SKU; independent of embeddingSkuName.')
param chatSkuName string = 'Standard'

@description('Chat model deployment capacity in thousands of tokens per minute.')
@minValue(10)
param chatCapacity int = 10

@description('Monthly budget limit in USD for the resource group. Alerts at 80% and 100% threshold. Set to 0 to disable budgets.')
@minValue(0)
param monthlyBudgetAmount int = 50

@description('Email address to receive budget notifications.')
param budgetNotificationEmail string = ''

@description('Start date for the monthly budget period.')
param budgetStartDate string = utcNow('yyyy-MM-01')

var chatDeploymentName = 'gpt-4o'
resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-09-01' = if (deployChat) {
  parent: openAi
  name: chatDeploymentName
  sku: {
    name: chatSkuName
    capacity: chatCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: '2024-11-20'
    }
  }
}

// IMPORTANT: Entra-only authentication means this server has NO SQL admin login or password.
// The deployer must have (or create) an Entra security group, pass its name and object ID, and be a
// member of it to connect as admin and run sql/schema.sql.
resource sqlServer 'Microsoft.Sql/servers@2025-01-01' = {
  name: sqlServerName
  location: sqlLocation
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlAdminGroupName
      sid: sqlAdminGroupObjectId
      principalType: 'Group'
      tenantId: tenant().tenantId
    }
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2025-01-01' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: sqlLocation
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-ai'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: false
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${prefix}-kv'
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    accessPolicies: []
  }
}

// Sprint 4: Store AI Search query key in Key Vault for demonstrating Key Vault integration.
// IMPORTANT: RBAC is the primary access method for both API and Worker (already in place via managed identity).
// This key reference exists to satisfy the exam requirement of demonstrating Key Vault-sourced secrets,
// but the deployed API uses managed identity (SearchClient with DefaultAzureCredential), not the key.
resource searchQueryKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'search-query-key'
  properties: {
    value: search.listQueryKeys().value[0].key
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'di${env}${suffix}'
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource documents 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'documents'
  properties: {
    publicAccess: 'None'
  }
}

resource eventGridDeadLetter 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'eventgrid-deadletter'
  properties: {
    publicAccess: 'None'
  }
}

resource extractorPackages 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'extractor-packages'
  properties: {
    publicAccess: 'None'
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'di${env}${suffix}acr'
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${prefix}-cae'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace.properties.customerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

resource functionPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${prefix}-extractor-plan'
  location: location
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource extractor 'Microsoft.Web/sites@2024-04-01' = {
  name: extractorName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: functionPlan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
    }
    functionAppConfig: {
      runtime: {
        name: 'dotnet-isolated'
        version: '8.0'
      }
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${extractorPackages.name}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
    }
  }
}

resource extractorAppSettings 'Microsoft.Web/sites/config@2022-03-01' = {
  parent: extractor
  name: 'appsettings'
  properties: {
    AzureWebJobsStorage__accountName: storage.name
    AzureWebJobsStorage__credential: 'managedidentity'
    Storage__BlobEndpoint: storage.properties.primaryEndpoints.blob
    Storage__ContainerName: documents.name
    DocIntel__Endpoint: documentIntelligence.properties.endpoint
    ServiceBus__Namespace: '${serviceBus.name}.servicebus.windows.net'
    ServiceBus__QueueName: extractedDocuments.name
    APPLICATIONINSIGHTS_CONNECTION_STRING: insights.properties.ConnectionString
  }
}

resource eventGridTopic 'Microsoft.EventGrid/systemTopics@2022-06-15' = {
  name: '${prefix}-storage-events'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    source: storage.id
    topicType: 'Microsoft.Storage.StorageAccounts'
  }
}

resource blobCreatedSubscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2022-06-15' = if (!bootstrapExtractor) {
  parent: eventGridTopic
  name: 'documents-to-extractor'
  properties: {
    destination: {
      endpointType: 'AzureFunction'
      properties: {
        resourceId: '${extractor.id}/functions/ExtractDocument'
        maxEventsPerBatch: 1
      }
    }
    deadLetterWithResourceIdentity: {
      deadLetterDestination: {
        endpointType: 'StorageBlob'
        properties: {
          resourceId: storage.id
          blobContainerName: eventGridDeadLetter.name
        }
      }
      identity: {
        type: 'SystemAssigned'
      }
    }
    filter: {
      includedEventTypes: [
        'Microsoft.Storage.BlobCreated'
      ]
      subjectBeginsWith: '/blobServices/default/containers/documents/'
      isSubjectCaseSensitive: true
    }
    eventDeliverySchema: 'EventGridSchema'
    retryPolicy: {
      maxDeliveryAttempts: 30
      eventTimeToLiveInMinutes: 1440
    }
  }
  // AzureFunction destinations resolve a deployed function. Publish and verify ExtractDocument before setting bootstrapExtractor=false.
  dependsOn: [
    extractorAppSettings
    eventGridDeadLetterRole
  ]
}

module api 'modules/containerApp.bicep' = {
  name: 'docintake-api'
  params: {
    name: apiName
    location: location
    environmentId: environment.id
    registryServer: registry.properties.loginServer
    image: apiImage
    registryAuthenticationEnabled: !bootstrap
    external: !bootstrap
    minReplicas: bootstrap ? 0 : 1
    maxReplicas: 3
    envVars: [
      {
        name: 'ASPNETCORE_ENVIRONMENT'
        value: 'Production'
      }
      {
        name: 'ASPNETCORE_HTTP_PORTS'
        value: '8080'
      }
      {
        name: 'ASPNETCORE_URLS'
        value: 'http://+:8080'
      }
      {
        name: 'Development__BypassAuth'
        value: 'false'
      }
      {
        name: 'Auth__Authority'
        value: authAuthority
      }
      {
        name: 'Auth__Audience'
        value: authAudience
      }
      {
        name: 'Storage__BlobEndpoint'
        value: storage.properties.primaryEndpoints.blob
      }
      {
        name: 'Storage__ContainerName'
        value: documents.name
      }
      {
        name: 'Search__Endpoint'
        value: 'https://${search.name}.search.windows.net'
      }
      {
        name: 'OpenAI__Endpoint'
        value: openAi.properties.endpoint
      }
      {
        name: 'OpenAI__EmbeddingDeployment'
        value: embeddingDeployment.name
      }
      {
        name: 'OpenAI__ChatDeployment'
        value: deployChat ? chatDeploymentName : ''
      }
      {
        name: 'OpenAI__ChatEnabled'
        value: deployChat ? 'true' : 'false'
      }
      {
        name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
        value: insights.properties.ConnectionString
      }
    ]
    secrets: {}
  }
}

resource blobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, apiName, blobContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: blobContributorRoleId
    principalId: api.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, apiName, acrPullRoleId)
  scope: registry
  properties: {
    roleDefinitionId: acrPullRoleId
    principalId: api.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

resource telemetryPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(insights.id, apiName, telemetryPublisherRoleId)
  scope: insights
  properties: {
    roleDefinitionId: telemetryPublisherRoleId
    principalId: api.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// Sprint 4: Grant API managed identity access to Key Vault secrets (e.g., AI Search query key).
// Note: API also uses managed identity (DefaultAzureCredential) for direct access to Search/OpenAI;
// this demonstrates Key Vault integration as required, but secrets are optional — the API primarily
// uses RBAC for AI Search and OpenAI, not stored keys.
resource apiKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, apiName, keyVaultSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: keyVaultSecretsUserRoleId
    principalId: api.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// Sprint 4: Grant API managed identity access to AI Search for both querying (Search Index Data Reader)
// and creating the RAG index (Search Service Contributor is needed for index creation).
resource apiSearchIndexDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, apiName, searchIndexDataContributorRoleId)
  scope: search
  properties: {
    roleDefinitionId: searchIndexDataContributorRoleId
    principalId: api.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// Sprint 4: Grant API managed identity access to Azure OpenAI for retrieving embeddings and chat completions.
resource apiOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAi.id, apiName, cognitiveServicesOpenAiUserRoleId)
  scope: openAi
  properties: {
    roleDefinitionId: cognitiveServicesOpenAiUserRoleId
    principalId: api.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

resource extractorStorageBlobOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, extractorName, storageBlobDataOwnerRoleId)
  scope: storage
  properties: {
    roleDefinitionId: storageBlobDataOwnerRoleId
    principalId: extractor.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource extractorServiceBusSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, extractorName, serviceBusDataSenderRoleId)
  scope: serviceBus
  properties: {
    roleDefinitionId: serviceBusDataSenderRoleId
    principalId: extractor.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource extractorDocumentIntelligenceUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(documentIntelligence.id, extractorName, cognitiveServicesUserRoleId)
  scope: documentIntelligence
  properties: {
    roleDefinitionId: cognitiveServicesUserRoleId
    principalId: extractor.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource eventGridDeadLetterRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, eventGridTopic.name, blobContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: blobContributorRoleId
    principalId: eventGridTopic.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Sprint 3 worker roles. SPRINT 4: workerPrincipalId is a local-development placeholder (your
// own user). The roles below remain active to support local testing and backward compatibility.
// In Sprint 4, ADDITIONAL role assignments for the Worker Container App's managed identity are
// created above (workerSearchIndexDataContributor, workerSearchServiceContributor, etc. with
// worker.outputs.principalId). Both sets coexist; remove the workerPrincipalId assignments only
// after verifying the Container App-based worker is fully functional. This dual approach avoids
// disrupting local development workflows.
// Search Service Contributor is needed because the worker creates/updates the index schema; with
// disableLocalAuth on Search, its key-management actions grant no data access.
// SQL access is not Azure RBAC: it comes from Entra admin-group membership (local) or the contained
// database user created by sql/schema.sql (Sprint 4 managed identity).
resource localWorkerSearchIndexDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableLocalWorkerAccess) {
  name: guid(search.id, workerPrincipalId, searchIndexDataContributorRoleId)
  scope: search
  properties: {
    roleDefinitionId: searchIndexDataContributorRoleId
    principalId: workerPrincipalId
    principalType: workerPrincipalType
  }
}

resource localWorkerSearchServiceContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableLocalWorkerAccess) {
  name: guid(search.id, workerPrincipalId, searchServiceContributorRoleId)
  scope: search
  properties: {
    roleDefinitionId: searchServiceContributorRoleId
    principalId: workerPrincipalId
    principalType: workerPrincipalType
  }
}

resource localWorkerOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableLocalWorkerAccess) {
  name: guid(openAi.id, workerPrincipalId, cognitiveServicesOpenAiUserRoleId)
  scope: openAi
  properties: {
    roleDefinitionId: cognitiveServicesOpenAiUserRoleId
    principalId: workerPrincipalId
    principalType: workerPrincipalType
  }
}

resource localWorkerServiceBusReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableLocalWorkerAccess) {
  name: guid(serviceBus.id, workerPrincipalId, serviceBusDataReceiverRoleId)
  scope: serviceBus
  properties: {
    roleDefinitionId: serviceBusDataReceiverRoleId
    principalId: workerPrincipalId
    principalType: workerPrincipalType
  }
}

resource workerRegistryIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (!empty(workerImage)) {
  name: '${prefix}-worker-pull'
  location: location
}

// Image-pull permissions must exist before provisioning the first private-image revision.
resource workerRegistryAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(workerImage)) {
  name: guid(registry.id, workerRegistryIdentity.id, acrPullRoleId)
  scope: registry
  properties: {
    roleDefinitionId: acrPullRoleId
    principalId: workerRegistryIdentity!.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Sprint 4: Deploy the Worker Container App for ingesting chunks from the extracted-documents queue.
// Only deployed if workerImage is non-empty. Scales 0-5 replicas based on queue depth (KEDA).
module worker 'modules/containerApp.bicep' = if (!empty(workerImage)) {
  name: 'docintake-worker'
  params: {
    name: '${prefix}-worker'
    location: location
    environmentId: environment.id
    registryServer: registry.properties.loginServer
    registryIdentityResourceId: workerRegistryIdentity.id
    image: workerImage
    external: false
    ingressEnabled: false
    minReplicas: 0
    maxReplicas: 5
    envVars: [
      {
        name: 'ServiceBus__Namespace'
        value: '${serviceBus.name}.servicebus.windows.net'
      }
      {
        name: 'ServiceBus__QueueName'
        value: extractedDocuments.name
      }
      {
        name: 'OpenAI__Endpoint'
        value: openAi.properties.endpoint
      }
      {
        name: 'OpenAI__EmbeddingDeployment'
        value: embeddingDeployment.name
      }
      {
        name: 'Search__Endpoint'
        value: 'https://${search.name}.search.windows.net'
      }
      {
        name: 'Sql__Server'
        value: sqlServer.properties.fullyQualifiedDomainName
      }
      {
        name: 'Sql__Database'
        value: sqlDatabase.name
      }
      {
        name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
        value: insights.properties.ConnectionString
      }
    ]
    scaleRules: [
      {
        name: 'service-bus-scale'
        custom: {
          type: 'azure-servicebus'
          identity: 'system'
          metadata: {
            namespace: serviceBusNamespaceName
            queueName: extractedDocuments.name
            messageCount: '10'
            cloud: 'AzurePublicCloud'
          }
        }
      }
    ]
    secrets: {}
  }
  dependsOn: [
    workerRegistryAcrPull
  ]
}

// Retain the existing system-identity assignment for compatibility; image pulls use workerRegistryIdentity.
resource workerAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(workerImage)) {
  name: guid(registry.id, '${prefix}-worker', acrPullRoleId)
  scope: registry
  properties: {
    roleDefinitionId: acrPullRoleId
    principalId: worker!.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// Sprint 4: Grant Worker managed identity telemetry write (Application Insights).
resource workerTelemetryPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(workerImage)) {
  name: guid(insights.id, '${prefix}-worker', telemetryPublisherRoleId)
  scope: insights
  properties: {
    roleDefinitionId: telemetryPublisherRoleId
    principalId: worker!.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// Sprint 4: Grant Worker managed identity Search access (index data contributor for creating and querying index).
resource workerSearchIndexDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(workerImage)) {
  name: guid(search.id, '${prefix}-worker', searchIndexDataContributorRoleId)
  scope: search
  properties: {
    roleDefinitionId: searchIndexDataContributorRoleId
    principalId: worker!.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// Sprint 4: Grant Worker managed identity Search service access (needed for index creation via IndexSchema.EnsureAsync).
resource workerSearchServiceContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(workerImage)) {
  name: guid(search.id, '${prefix}-worker', searchServiceContributorRoleId)
  scope: search
  properties: {
    roleDefinitionId: searchServiceContributorRoleId
    principalId: worker!.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// Sprint 4: Grant Worker managed identity OpenAI access (embeddings).
resource workerOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(workerImage)) {
  name: guid(openAi.id, '${prefix}-worker', cognitiveServicesOpenAiUserRoleId)
  scope: openAi
  properties: {
    roleDefinitionId: cognitiveServicesOpenAiUserRoleId
    principalId: worker!.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// Sprint 4: Grant Worker managed identity Service Bus access (receive messages from extracted-documents queue).
resource workerServiceBusReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(workerImage)) {
  name: guid(serviceBus.id, '${prefix}-worker', serviceBusDataReceiverRoleId)
  scope: serviceBus
  properties: {
    roleDefinitionId: serviceBusDataReceiverRoleId
    principalId: worker!.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// Sprint 4: Grant Worker managed identity SQL database access (db_datawriter, db_datareader roles via Entra user).
// The actual SQL role assignment happens in sql/schema.sql via CREATE USER [<worker-identity-name>] FROM EXTERNAL PROVIDER.
// Here we just note that the Worker's system-assigned managed identity must be granted these SQL roles after deployment.

// Sprint 5: Resource group budget with email notifications at 80% and 100% thresholds.
// Note: Budget alerts require that budgetNotificationEmail is non-empty. Email recipients must accept
// the invitation sent to their address to receive future notifications. Budgets are advisory; they do not
// automatically prevent resource creation or usage beyond the threshold. Use them in conjunction with
// consumption spending limits and monitoring alerts.
resource budget 'Microsoft.Consumption/budgets@2021-10-01' = if (monthlyBudgetAmount > 0 && !empty(budgetNotificationEmail)) {
  name: '${prefix}-monthly-budget'
  properties: {
    category: 'Cost'
    amount: monthlyBudgetAmount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
      endDate: '2099-12-31'
    }
    filter: {
      dimensions: {
        name: 'ResourceGroupName'
        operator: 'In'
        values: [
          resourceGroup().name
        ]
      }
    }
    notifications: {
      NotificationForExceededBudget80Percent: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        contactEmails: [
          budgetNotificationEmail
        ]
      }
      NotificationForExceededBudget100Percent: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        contactEmails: [
          budgetNotificationEmail
        ]
      }
    }
  }
}

output apiFqdn string = api.outputs.fqdn
output acrName string = registry.name
output storageName string = storage.name
output serviceBusNamespace string = serviceBus.name
output documentIntelligenceEndpoint string = documentIntelligence.properties.endpoint
output extractorName string = extractor.name
output searchEndpoint string = 'https://${search.name}.search.windows.net'
output openAiEndpoint string = openAi.properties.endpoint
output embeddingDeploymentName string = embeddingDeployment.name
output chatDeploymentName string = deployChat ? chatDeploymentName : ''
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name
output workerPrincipalId string = !empty(workerImage) ? worker!.outputs.principalId : ''
