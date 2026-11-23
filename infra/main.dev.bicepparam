using 'main.bicep'

// The dev environment, at the lowest cost every service allows (the defaults of main.bicep). The ids come from
// the environment so that none of them is written into the repository: the pipeline sets them from the GitHub
// environment's variables, and on a workstation you set them yourself before running az deployment.

param env = 'dev'

param apiClientId = readEnvironmentVariable('KH_API_CLIENT_ID')
param sqlAdminObjectId = readEnvironmentVariable('KH_SQL_ADMIN_OBJECT_ID')
param sqlAdminName = readEnvironmentVariable('KH_SQL_ADMIN_NAME')

// To be told when the resource group costs more than expected, give an amount, who to write to, and the first
// day of the month you add this in (Azure does not let the start date change afterwards):
// param budget = { amount: 20, contactEmails: [readEnvironmentVariable('KH_BUDGET_EMAIL')], startDate: '2026-12-01' }

// To switch the GitHub sync on, register the GitHub App for this environment and name it here:
// param gitHub = { clientId: 'Iv23...', botLogin: 'kanbanhub-dev[bot]', principalId: '<object id the app acts as>' }
// param agentsEnabled = true

// To switch AI on, name the models your region offers (take names and versions from the model catalog):
// param aiChatModel = { name: '<chat model>', version: '<version>', sku: 'GlobalStandard', capacity: 10 }
// param aiEmbeddingModel = { name: 'text-embedding-3-small', version: '1', sku: 'GlobalStandard', capacity: 10 }
