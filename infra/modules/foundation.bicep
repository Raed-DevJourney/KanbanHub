// What everything else leans on: the identity both function apps run as, logging, and the storage account the
// Functions host uses. Nothing here has a key: the identity gets roles instead (specification section 18.2).

param env string
param location string
param suffix string

@description('Log Analytics stops taking data for the day at this many GB, so a mistake cannot run up a bill.')
param logDailyCapGb int

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-kh-${env}'
  location: location
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-kh-${env}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: logDailyCapGb }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-kh-${env}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stkh${env}${suffix}'
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }

  resource blobs 'blobServices' = {
    name: 'default'

    resource apiPackages 'containers' = {
      name: 'deployments-api'
    }

    resource workerPackages 'containers' = {
      name: 'deployments-workers'
    }
  }
}

var storageBlobDataOwner = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageQueueDataContributor = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'

resource storageRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for role in [storageBlobDataOwner, storageQueueDataContributor]: {
    name: guid(storage.id, identity.id, role)
    scope: storage
    properties: {
      principalId: identity.properties.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    }
  }
]

output identityId string = identity.id
output identityName string = identity.name
output identityClientId string = identity.properties.clientId
output identityPrincipalId string = identity.properties.principalId
output storageName string = storage.name
output blobEndpoint string = storage.properties.primaryEndpoints.blob
output insightsConnectionString string = insights.properties.ConnectionString
