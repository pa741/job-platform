@description('Azure region.')
param location string

@description('Resource name prefix.')
param namePrefix string

@description('Suffix for globally unique names.')
param resourceToken string

param tags object

@description('Principal id of the shared managed identity that reads the secret.')
param apiPrincipalId string

@description('Name of the secret holding the Anthropic API key. Its VALUE is never set here.')
param anthropicSecretName string = 'anthropic-api-key'

// The only vault in this system, and it exists for exactly one secret.
//
// Everything else in job-platform is authenticated by managed identity and has no secret to
// store - Azure SQL is Entra-only, Cosmos has local auth disabled, storage uses
// identity-based connections, CI uses OIDC. A third-party API key is the one credential that
// genuinely cannot be replaced by an identity, so it gets a vault rather than an app setting.
//
// This template never receives or emits the key. It creates the vault and grants the identity
// read access; the value is set out of band with `az keyvault secret set`, so it exists in
// exactly one place and appears in no deployment history, template, parameter file or output.
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take('kv-${namePrefix}-${resourceToken}', 24)
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    // RBAC rather than access policies: the same authorisation model as every other resource
    // here, and the only one that composes with a user-assigned identity cleanly.
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    // Deliberately off. Purge protection cannot be disabled once enabled, and a name blocked
    // for 90 days after a teardown is a poor trade for a prototype's single key.
    enablePurgeProtection: null
    publicNetworkAccess: 'Enabled'
  }
}

@description('Built-in Key Vault Secrets User: read secret values, nothing else.')
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

resource apiSecretsReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, apiPrincipalId, keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output vaultName string = vault.name
output vaultUri string = vault.properties.vaultUri

@description('URI the container app references. Versionless, so rotating the secret does not need a redeploy.')
output anthropicSecretUri string = '${vault.properties.vaultUri}secrets/${anthropicSecretName}'

output anthropicSecretName string = anthropicSecretName
