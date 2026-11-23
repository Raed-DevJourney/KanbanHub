// One function app on Flex Consumption (specification section 18.2), used twice: for the API and for the workers.
// It runs as the shared identity, takes its package from blob storage with that identity, and has no keys in its
// settings. Flex Consumption bills for what runs, so with no always-ready instances an idle app costs nothing.

param name string
param location string
param identityId string
param identityClientId string

@description('The blob container the deployment package is uploaded to, as a full URL.')
param packageContainerUrl string
param storageName string

@allowed([512, 2048, 4096])
param instanceMemoryMB int

@description('The most instances the app may scale out to. A low number caps what the app can cost.')
param maximumInstances int

@description('HTTP instances kept warm. Each one is billed around the clock, so 0 is the cheapest and means cold starts.')
param alwaysReadyHttp int

@description('Origins allowed to call the app from a browser; empty for an app that serves no browser.')
param allowedOrigins string[] = []

@description('Settings on top of the ones every app gets, as name and value pairs.')
param settings object

var runtime = { name: 'dotnet-isolated', version: '11.0' }   // the value Flex Consumption lists for .NET 11

// What the Functions host itself needs: its storage and its telemetry, both by identity.
var hostSettings = {
  AzureWebJobsStorage__accountName: storageName
  AzureWebJobsStorage__credential: 'managedidentity'
  AzureWebJobsStorage__clientId: identityClientId
  AZURE_CLIENT_ID: identityClientId
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-${name}'
  location: location
  kind: 'functionapp'
  sku: { tier: 'FlexConsumption', name: 'FC1' }
  properties: { reserved: true }
}

resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: name
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identityId
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: packageContainerUrl
          authentication: { type: 'UserAssignedIdentity', userAssignedIdentityResourceId: identityId }
        }
      }
      runtime: runtime
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstances
        instanceMemoryMB: instanceMemoryMB
        alwaysReady: alwaysReadyHttp > 0 ? [{ name: 'http', instanceCount: alwaysReadyHttp }] : []
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      cors: { allowedOrigins: allowedOrigins }
      appSettings: [
        for setting in items(union(hostSettings, settings)): { name: setting.key, value: setting.value }
      ]
    }
  }
}

output name string = app.name
output url string = 'https://${app.properties.defaultHostName}'
