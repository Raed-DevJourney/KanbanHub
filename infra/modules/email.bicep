// Email through Azure Communication Services (specification section 13.1), on the domain Azure provides for
// free. There is no fixed cost: only emails that are sent are billed. A verified custom domain, which the
// specification asks for, needs DNS records and is set up by hand when one is wanted.

param env string
param suffix string
param principalId string

@description('Where Communication Services keeps its data at rest, for example Europe or United States.')
param dataLocation string

resource emailService 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: 'email-kh-${env}-${suffix}'
  location: 'global'
  properties: { dataLocation: dataLocation }

  resource domain 'domains' = {
    name: 'AzureManagedDomain'
    location: 'global'
    properties: { domainManagement: 'AzureManaged' }
  }
}

resource communication 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: 'acs-kh-${env}-${suffix}'
  location: 'global'
  properties: {
    dataLocation: dataLocation
    linkedDomains: [emailService::domain.id]
  }
}

var communicationAndEmailServiceOwner = '09976791-48a7-449e-bb21-39d1a415f350'

resource sendRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(communication.id, principalId, communicationAndEmailServiceOwner)
  scope: communication
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', communicationAndEmailServiceOwner)
  }
}

output endpoint string = 'https://${communication.properties.hostName}'
output senderAddress string = 'DoNotReply@${emailService::domain.properties.mailFromSenderDomain}'
