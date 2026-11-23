using 'main.bicep'

// The test environment. Same lowest-cost defaults as dev: it exists to rehearse a deployment, not to carry load.

param env = 'test'

param apiClientId = readEnvironmentVariable('KH_API_CLIENT_ID')
param sqlAdminObjectId = readEnvironmentVariable('KH_SQL_ADMIN_OBJECT_ID')
param sqlAdminName = readEnvironmentVariable('KH_SQL_ADMIN_NAME')
