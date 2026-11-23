// Service Bus and SignalR Service (specification sections 9.7 and 13.1). Both refuse keys: the apps' identity
// gets data roles instead.

param env string
param location string
param suffix string
param principalId string

@allowed(['Free_F1', 'Standard_S1'])
param signalRSku string

@description('The web app, so browsers may open their SignalR connection from it.')
param webOrigin string

// Standard is the smallest tier with topics and sessions; Basic has neither.
resource bus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: 'sb-kh-${env}-${suffix}'
  location: location
  sku: { name: 'Standard', tier: 'Standard' }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }

  // Webhook deliveries: one session per issue keeps its events in order; the delivery id drops duplicates.
  resource gitHubInbound 'queues' = {
    name: 'github-inbound'
    properties: {
      requiresSession: true
      requiresDuplicateDetection: true
      duplicateDetectionHistoryTimeWindow: 'PT1H'
      maxDeliveryCount: 10
      deadLetteringOnMessageExpiration: true
    }
  }

  // Card changes on their way to GitHub: one session per card.
  resource gitHubOutbound 'queues' = {
    name: 'github-outbound'
    properties: {
      requiresSession: true
      maxDeliveryCount: 10
      deadLetteringOnMessageExpiration: true
    }
  }

  resource aiJobs 'queues' = {
    name: 'ai-jobs'
    properties: {
      maxDeliveryCount: 5
      deadLetteringOnMessageExpiration: true
    }
  }

  resource cardEvents 'topics' = {
    name: 'card-events'

    resource notifications 'subscriptions' = {
      name: 'notifications'
      properties: { maxDeliveryCount: 10 }
    }

    resource duplicates 'subscriptions' = {
      name: 'duplicates'
      properties: { maxDeliveryCount: 5 }
    }

    // The GitHub writer reads card events per card, in order: the subscription forwards to the session queue.
    resource gitHub 'subscriptions' = {
      name: 'github'
      properties: { forwardTo: gitHubOutbound.name }
    }
  }
}

resource signalR 'Microsoft.SignalRService/signalR@2024-03-01' = {
  name: 'sigr-kh-${env}-${suffix}'
  location: location
  kind: 'SignalR'
  sku: {
    name: signalRSku
    tier: signalRSku == 'Free_F1' ? 'Free' : 'Standard'
    capacity: 1
  }
  properties: {
    disableLocalAuth: true
    features: [{ flag: 'ServiceMode', value: 'Serverless' }]
    cors: { allowedOrigins: [webOrigin] }
  }
}

var serviceBusDataOwner = '090c5cfd-751d-490a-894a-3ce6f1109419'
var signalRAppServer = '420fcaa2-552c-430f-98ca-3264be4806c7'
var signalRRestApiOwner = 'fd53cd77-2268-407a-8f46-7e7863d0f521'

resource busRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(bus.id, principalId, serviceBusDataOwner)
  scope: bus
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataOwner)
  }
}

// App Server lets the API hand browsers their connection; REST API Owner lets both apps push to groups and users.
resource signalRRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for role in [signalRAppServer, signalRRestApiOwner]: {
    name: guid(signalR.id, principalId, role)
    scope: signalR
    properties: {
      principalId: principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    }
  }
]

output serviceBusNamespace string = '${bus.name}.servicebus.windows.net'
output signalRUri string = 'https://${signalR.properties.hostName}'
