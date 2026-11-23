// KanbanHub on Azure (specification sections 13.1 and 18.2), for one environment in one resource group.
//
// Every default here is the cheapest setting that still runs the whole system: free tiers where they exist,
// serverless everywhere, nothing kept warm, no private network. It is not sized or hardened for production.
// Each choice is a parameter, so an environment can be raised without touching the modules.

import { costBudget, gitHubApp, modelDeployment } from 'types.bicep'

targetScope = 'resourceGroup'

// ---- which environment

@description('Part of every resource name.')
@allowed(['dev', 'test', 'prod'])
param env string

param location string = resourceGroup().location

@description('Makes the names that must be unique worldwide unique; derived from the resource group unless given.')
@minLength(3)
@maxLength(8)
param suffix string = take(uniqueString(resourceGroup().id), 5)

// ---- who may sign in and administer

@description('The client id of the KanbanHub.Api app registration: the audience of the tokens the API accepts.')
param apiClientId string

@description('The Entra group that administers the SQL server. The identity the pipeline deploys with must be a member.')
param sqlAdminObjectId string
param sqlAdminName string
@allowed(['Group', 'User', 'Application'])
param sqlAdminType string = 'Group'

// ---- what it may cost

@description('Free_F1 allows 20 connections and has no SLA; Standard_S1 is billed per unit and day.')
@allowed(['Free_F1', 'Standard_S1'])
param signalRSku string = 'Free_F1'

@allowed(['Free', 'Standard'])
param staticWebAppSku string = 'Free'

@allowed(['westeurope', 'eastus2', 'centralus', 'westus2', 'eastasia'])
param staticWebAppLocation string = 'westeurope'

@description('The database scales between these while it runs, and pauses after the given minutes without activity.')
param sqlMinVCores string = '0.5'
param sqlMaxVCores int = 1
param sqlAutoPauseMinutes int = 60

@description('Uses Azure\'s free monthly allowance for a serverless database. When it is used up the database pauses until next month instead of billing.')
param sqlUseFreeOffer bool = true

@allowed([512, 2048, 4096])
param functionMemoryMB int = 512

@description('The most instances each function app may scale to: a hard cap on what compute can cost.')
param functionMaximumInstances int = 1

@description('API instances kept warm. 0 is the cheapest and means the first request after a quiet spell is slow.')
param apiAlwaysReady int = 0

@description('''
Runs the timers that fire every few minutes: the outbox sweeper, the standup-brief scheduler, the aging rule and
the hourly constraint check. Each run queries the database, so with them on the database never pauses and is
billed around the clock. Off, the API still publishes events as it writes them; only those four jobs stop.
''')
param frequentTimers bool = false

param logDailyCapGb int = 1

@description('A monthly spending alert for this resource group. Leave it out and no budget is created.')
param budget costBudget?

// ---- optional features

@description('The GitHub App for this environment. Leave it out and the GitHub sync, agents and Connect GitHub stay off.')
param gitHub gitHubApp?

@description('The feature flag for agent dispatch (specification section 10): GitHub\'s agent APIs are in public preview.')
param agentsEnabled bool = false

@description('The chat model for the AI features. Leave it out and no Azure OpenAI resource is created.')
param aiChatModel modelDeployment?
param aiEmbeddingModel modelDeployment?

@description('Creates Communication Services for notification email. It has no fixed cost.')
param emailEnabled bool = true
param emailDataLocation string = 'Europe'

@description('Purge protection for Key Vault cannot be undone; leave it off for an environment you may delete.')
param keyVaultPurgeProtection bool = false

// ---- resources

module foundation 'modules/foundation.bicep' = {
  name: 'foundation'
  params: { env: env, location: location, suffix: suffix, logDailyCapGb: logDailyCapGb }
}

module web 'modules/web.bicep' = {
  name: 'web'
  params: { env: env, suffix: suffix, location: staticWebAppLocation, sku: staticWebAppSku }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    env: env
    location: location
    suffix: suffix
    adminObjectId: sqlAdminObjectId
    adminName: sqlAdminName
    adminType: sqlAdminType
    minVCores: sqlMinVCores
    maxVCores: sqlMaxVCores
    autoPauseMinutes: sqlAutoPauseMinutes
    useFreeOffer: sqlUseFreeOffer
  }
}

module messaging 'modules/messaging.bicep' = {
  name: 'messaging'
  params: {
    env: env
    location: location
    suffix: suffix
    principalId: foundation.outputs.identityPrincipalId
    signalRSku: signalRSku
    webOrigin: web.outputs.origin
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    env: env
    location: location
    suffix: suffix
    principalId: foundation.outputs.identityPrincipalId
    purgeProtection: keyVaultPurgeProtection
  }
}

module email 'modules/email.bicep' = if (emailEnabled) {
  name: 'email'
  params: {
    env: env
    suffix: suffix
    principalId: foundation.outputs.identityPrincipalId
    dataLocation: emailDataLocation
  }
}

module ai 'modules/ai.bicep' = if (aiChatModel != null) {
  name: 'ai'
  params: {
    env: env
    location: location
    suffix: suffix
    principalId: foundation.outputs.identityPrincipalId
    chatModel: aiChatModel!
    embeddingModel: aiEmbeddingModel
  }
}

module spending 'modules/budget.bicep' = if (budget != null) {
  name: 'budget'
  params: { env: env, budget: budget! }
}

// ---- settings: names are the configuration keys the apps read, with __ for the colon

// What both apps need to reach the database, the bus and SignalR. No value here is a secret.
var sharedSettings = {
  APPLICATIONINSIGHTS_CONNECTION_STRING: foundation.outputs.insightsConnectionString
  Sql__ConnectionString: 'Server=tcp:${sql.outputs.serverFqdn},1433;Database=${sql.outputs.databaseName};Encrypt=True;'
  ServiceBus__fullyQualifiedNamespace: messaging.outputs.serviceBusNamespace
  ServiceBus__credential: 'managedidentity'
  ServiceBus__clientId: foundation.outputs.identityClientId
  SignalR__serviceUri: messaging.outputs.signalRUri
  Agents__Enabled: string(agentsEnabled)
}

// The two secrets are Key Vault references: the platform reads them with the app's identity.
var gitHubSettings = gitHub == null ? {} : {
  GitHub__ClientId: gitHub!.clientId
  GitHub__BotLogin: gitHub!.botLogin
  GitHub__PrincipalId: gitHub!.principalId
  GitHub__WebBaseUrl: '${web.outputs.origin}/'
  GitHub__KeyId: keyVault.outputs.appKeyId
  GitHub__TokenKeyId: keyVault.outputs.tokenKeyId
  GitHub__WebhookSecret: keyVault.outputs.webhookSecretReference
  GitHub__ClientSecret: keyVault.outputs.clientSecretReference
}

var aiSettings = aiChatModel == null ? {} : {
  Ai__Endpoint: ai!.outputs.endpoint
  Ai__ChatDeployment: ai!.outputs.chatDeployment
  Ai__EmbeddingDeployment: ai!.outputs.embeddingDeployment
}

var emailSettings = !emailEnabled ? {} : {
  Email__Endpoint: email!.outputs.endpoint
  Email__SenderAddress: email!.outputs.senderAddress
}

// A function is switched off by a setting named after it. These are the timers that would keep the database awake.
var quietTimers = frequentTimers ? {} : {
  'AzureWebJobs.SweepOutbox.Disabled': 'true'
  'AzureWebJobs.QueueStandupBriefs.Disabled': 'true'
  'AzureWebJobs.RaiseAgingAlerts.Disabled': 'true'
  'AzureWebJobs.QueueBottleneckChecks.Disabled': 'true'
}

module api 'modules/functionapp.bicep' = {
  name: 'api'
  params: {
    name: 'func-kh-api-${env}-${suffix}'
    location: location
    identityId: foundation.outputs.identityId
    identityClientId: foundation.outputs.identityClientId
    storageName: foundation.outputs.storageName
    packageContainerUrl: '${foundation.outputs.blobEndpoint}deployments-api'
    instanceMemoryMB: functionMemoryMB
    maximumInstances: functionMaximumInstances
    alwaysReadyHttp: apiAlwaysReady
    allowedOrigins: [web.outputs.origin]
    settings: union(sharedSettings, gitHubSettings, aiSettings, {
      Entra__TenantId: tenant().tenantId
      Entra__ApiClientId: apiClientId
    })
  }
}

module workers 'modules/functionapp.bicep' = {
  name: 'workers'
  params: {
    name: 'func-kh-workers-${env}-${suffix}'
    location: location
    identityId: foundation.outputs.identityId
    identityClientId: foundation.outputs.identityClientId
    storageName: foundation.outputs.storageName
    packageContainerUrl: '${foundation.outputs.blobEndpoint}deployments-workers'
    instanceMemoryMB: functionMemoryMB
    maximumInstances: functionMaximumInstances
    alwaysReadyHttp: 0
    settings: union(sharedSettings, gitHubSettings, aiSettings, emailSettings, quietTimers)
  }
}

// ---- what the pipeline needs to finish the job

output apiAppName string = api.outputs.name
output apiUrl string = api.outputs.url
output workersAppName string = workers.outputs.name
output staticWebAppName string = web.outputs.name
output webUrl string = web.outputs.origin
output sqlServerFqdn string = sql.outputs.serverFqdn
output sqlDatabaseName string = sql.outputs.databaseName
output appIdentityName string = foundation.outputs.identityName
output appIdentityClientId string = foundation.outputs.identityClientId
output keyVaultName string = keyVault.outputs.name
