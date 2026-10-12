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

@description('First deployment only: use a public sample with internal ingress to create the system identity and RBAC before pulling from ACR.')
param bootstrap bool = true

@description('For the final deployment, supply the fully qualified ACR image with an immutable tag or digest and set bootstrap=false.')
param apiImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

var suffix = uniqueString(resourceGroup().id, env)
var prefix = 'di-${env}-${suffix}'
var apiName = '${prefix}-api'
var blobContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var acrPullRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
var telemetryPublisherRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')

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
    DisableLocalAuth: true
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

output apiFqdn string = api.outputs.fqdn
output acrName string = registry.name
output storageName string = storage.name
