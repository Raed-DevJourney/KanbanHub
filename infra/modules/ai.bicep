// Azure OpenAI for the AI features (specification section 11.2). Deployed only when main.bicep is given models:
// the account itself costs nothing, tokens do, and each board must still opt in before any are spent.

import { modelDeployment } from '../types.bicep'

param env string
param location string
param suffix string
param principalId string

param chatModel modelDeployment

@description('The embedding model for the duplicate finder; its vectors must have 1536 dimensions. Leave it out to keep that feature off.')
param embeddingModel modelDeployment?

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: 'oai-kh-${env}-${suffix}'
  location: location
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: 'oai-kh-${env}-${suffix}'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }

  resource chat 'deployments' = {
    name: 'chat'
    sku: { name: chatModel.sku, capacity: chatModel.capacity }
    properties: {
      model: { format: 'OpenAI', name: chatModel.name, version: chatModel.version }
    }
  }

  // One deployment at a time: the service refuses two changes to an account at once.
  resource embedding 'deployments' = if (embeddingModel != null) {
    name: 'embedding'
    dependsOn: [chat]
    sku: { name: embeddingModel!.sku, capacity: embeddingModel!.capacity }
    properties: {
      model: { format: 'OpenAI', name: embeddingModel!.name, version: embeddingModel!.version }
    }
  }
}

var cognitiveServicesOpenAiUser = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource useRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, cognitiveServicesOpenAiUser)
  scope: account
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAiUser)
  }
}

output endpoint string = account.properties.endpoint
output chatDeployment string = account::chat.name
output embeddingDeployment string = embeddingModel != null ? 'embedding' : ''
