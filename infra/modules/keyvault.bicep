// Key Vault (specification sections 9.1 and 13.1): the key that encrypts people's GitHub refresh tokens is made
// here. The GitHub App's private key and its two secrets are put in by hand, once (see the README), under the
// names below; nothing in this template ever holds or overwrites a secret value.

param env string
param location string
param suffix string
param principalId string

@description('Purge protection cannot be switched off again and keeps a deleted vault for 90 days. Leave it off for environments you may tear down.')
param purgeProtection bool

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-kh-${env}-${suffix}'
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: purgeProtection ? true : null
  }

  resource tokenKey 'keys' = {
    name: 'github-token-key'
    properties: {
      kty: 'RSA'
      keySize: 2048
      keyOps: ['encrypt', 'decrypt']
    }
  }
}

var keyVaultCryptoUser = '12338af0-0e69-4776-bea7-57ae8d297424'
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

resource vaultRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for role in [keyVaultCryptoUser, keyVaultSecretsUser]: {
    name: guid(vault.id, principalId, role)
    scope: vault
    properties: {
      principalId: principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    }
  }
]

output name string = vault.name
output tokenKeyId string = vault::tokenKey.properties.keyUri

@description('Where the GitHub App\'s private key must be imported, as a key with this name.')
output appKeyId string = '${vault.properties.vaultUri}keys/github-app-key'

@description('Key Vault references for the two secrets that are stored by hand.')
output webhookSecretReference string = '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/github-webhook-secret)'
output clientSecretReference string = '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/github-client-secret)'
