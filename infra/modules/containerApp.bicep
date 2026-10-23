targetScope = 'resourceGroup'

type environmentVariable = {
  name: string
  value: string?
  secretRef: string?
}

type scaleRule = {
  name: string
  custom: {
    type: string
    identity: string?
    metadata: object
    auth: {
      secretRef: string
    }[]?
  }?
  http: {
    metadata: {
      concurrentRequests: string
    }
  }?
}

param name string
param location string
param environmentId string
param registryServer string
param image string
param external bool
param ingressEnabled bool = true
param registryAuthenticationEnabled bool = true

@description('Optional user-assigned identity resource ID for image pulls. Empty uses the system-assigned identity.')
param registryIdentityResourceId string = ''

@description('Additional user-assigned identity resource IDs, for example a dedicated Azure SQL identity.')
param additionalUserAssignedIdentityIds string[] = []

@minValue(0)
param minReplicas int = 0

@minValue(1)
param maxReplicas int = 3

param envVars environmentVariable[] = []

@secure()
param secrets {
  values: {
    name: string
    value: string
  }[]?
} = {}

param scaleRules scaleRule[] = []

var userAssignedIdentityIds = union(
  empty(registryIdentityResourceId) ? [] : [registryIdentityResourceId],
  additionalUserAssignedIdentityIds
)

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: name
  location: location
  identity: {
    type: empty(userAssignedIdentityIds) ? 'SystemAssigned' : 'SystemAssigned,UserAssigned'
    userAssignedIdentities: empty(userAssignedIdentityIds) ? null : toObject(userAssignedIdentityIds, id => id, id => {})
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: ingressEnabled ? {
        external: external
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      } : null
      registries: registryAuthenticationEnabled ? [
        {
          server: registryServer
          identity: empty(registryIdentityResourceId) ? 'system' : registryIdentityResourceId
        }
      ] : []
      secrets: secrets.?values ?? []
    }
    template: {
      containers: [
        {
          name: 'app'
          image: image
          env: envVars
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          probes: ingressEnabled ? [
            {
              type: 'Startup'
              tcpSocket: {
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 10
              failureThreshold: 30
            }
            {
              type: 'Liveness'
              tcpSocket: {
                port: 8080
              }
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              tcpSocket: {
                port: 8080
              }
              periodSeconds: 5
              failureThreshold: 3
            }
          ] : []
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: scaleRules
      }
    }
  }
}

output principalId string = app.identity.principalId
output fqdn string = ingressEnabled ? app.properties.configuration.ingress.fqdn : ''
