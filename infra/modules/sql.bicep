// Azure SQL: one serverless General Purpose database, Entra sign-in only (specification sections 13.1 and 18.4).
// The apps' identity is given its database access by the dacpac's post-deployment script, not here.

param env string
param location string
param suffix string

@description('The Entra group (or user, or application) that administers the server. The deployment pipeline must be in it.')
param adminObjectId string
param adminName string
@allowed(['Group', 'User', 'Application'])
param adminType string

@description('The smallest compute the database scales down to while it runs, as a string because Bicep has no decimals: 0.5, 0.75, 1 ...')
param minVCores string
param maxVCores int

@description('Minutes without activity before the database pauses and stops costing compute. -1 never pauses.')
param autoPauseMinutes int

@description('Uses the free monthly allowance Azure gives serverless databases; when it is used up the database pauses until next month.')
param useFreeOffer bool

resource server 'Microsoft.Sql/servers@2023-08-01' = {
  name: 'sql-kh-${env}-${suffix}'
  location: location
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: adminName
      sid: adminObjectId
      principalType: adminType
      tenantId: tenant().tenantId
    }
  }

  // No virtual network in this cost-minimal setup: Azure-hosted callers (the function apps, GitHub's runners)
  // may connect, and every connection still needs an Entra token the database accepts.
  resource azureServices 'firewallRules' = {
    name: 'AllowAllWindowsAzureIps'
    properties: {
      startIpAddress: '0.0.0.0'
      endIpAddress: '0.0.0.0'
    }
  }

  resource database 'databases' = {
    name: 'kanbanhub'
    location: location
    sku: {
      name: 'GP_S_Gen5'
      tier: 'GeneralPurpose'
      family: 'Gen5'
      capacity: maxVCores
    }
    properties: {
      minCapacity: json(minVCores)
      autoPauseDelay: autoPauseMinutes
      useFreeLimit: useFreeOffer
      freeLimitExhaustionBehavior: useFreeOffer ? 'AutoPause' : null
      requestedBackupStorageRedundancy: 'Local'
      zoneRedundant: false
    }
  }
}

output serverFqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = server::database.name
