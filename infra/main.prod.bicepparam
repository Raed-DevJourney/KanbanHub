using 'main.bicep'

// The environment called prod. It keeps the lowest-cost defaults on purpose: KanbanHub is not run in production
// (see DECISIONS). Before real use, raise at least what is listed below; main.bicep explains each parameter.

param env = 'prod'

param apiClientId = readEnvironmentVariable('KH_API_CLIENT_ID')
param sqlAdminObjectId = readEnvironmentVariable('KH_SQL_ADMIN_OBJECT_ID')
param sqlAdminName = readEnvironmentVariable('KH_SQL_ADMIN_NAME')

// For real use:
// param signalRSku = 'Standard_S1'          // more than 20 connections, and an SLA
// param staticWebAppSku = 'Standard'        // an SLA
// param sqlUseFreeOffer = false             // the free allowance pauses the database when it runs out
// param sqlMaxVCores = 4
// param sqlAutoPauseMinutes = -1            // never pause
// param functionMemoryMB = 2048
// param functionMaximumInstances = 100
// param apiAlwaysReady = 1                  // no cold starts
// param frequentTimers = true               // the sweeper, standup briefs, aging alerts and the constraint check
// param keyVaultPurgeProtection = true
