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

@description('Keep true until the Function code is published and indexed; false creates the Event Grid subscription.')
param bootstrapExtractor bool = true

var suffix = uniqueString(resourceGroup().id, env)
var prefix = 'di-${env}-${suffix}'
var apiName = '${prefix}-api'
var extractorName = 'di${env}${suffix}extractor'
var serviceBusNamespaceName = '${prefix}-sb'
var documentIntelligenceName = 'di${env}${suffix}di'
var blobContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var acrPullRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
var telemetryPublisherRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
var storageBlobDataOwnerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var serviceBusDataSenderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
var cognitiveServicesUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')

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

resource functionPlan 'Microsoft.Web/serverfarms@2022-03-01' = {
  name: '${prefix}-extractor-plan'
  location: location
  kind: 'functionapp'
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
    size: 'Y1'
    family: 'Y'
  }
  properties: {
    reserved: true
  }
}

resource extractor 'Microsoft.Web/sites@2022-03-01' = {
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
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
    }
  }
}

resource extractorAppSettings 'Microsoft.Web/sites/config@2022-03-01' = {
  parent: extractor
  name: 'appsettings'
  properties: {
    FUNCTIONS_EXTENSION_VERSION: '~4'
    FUNCTIONS_WORKER_RUNTIME: 'dotnet-isolated'
    FUNCTIONS_WORKER_RUNTIME_VERSION: '8.0'
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
  name: guid(eventGridDeadLetter.id, eventGridTopic.name, blobContributorRoleId)
  scope: eventGridDeadLetter
  properties: {
    roleDefinitionId: blobContributorRoleId
    principalId: eventGridTopic.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output apiFqdn string = api.outputs.fqdn
output acrName string = registry.name
output storageName string = storage.name
output serviceBusNamespace string = serviceBus.name
output documentIntelligenceEndpoint string = documentIntelligence.properties.endpoint
output extractorName string = extractor.name
