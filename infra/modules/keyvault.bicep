@description('Azure region.')
param location string

@description('Resource name prefix.')
param namePrefix string

@description('Suffix for globally unique names.')
param resourceToken string

param tags object

@description('Principal id of the shared managed identity that reads the secret.')
param readerPrincipalId string

@description('Object id of the human administrator, who has to be able to set the secret value.')
param administratorObjectId string = ''

@description('Name of the secret holding the OpenAI API key. Its VALUE is never set here.')
param openAiSecretName string = 'openai-api-key'

// The only vault in this system, and it exists for exactly one secret.
//
// It was deleted once, when the AI provider moved from Anthropic to Azure OpenAI and Entra
// authentication removed the need for any credential at all. It is back for a narrower reason
// than the one it originally served: OpenAI's Batch API carries the gpt-5.6 family, which
// Azure's batch matrix does not, and gives corpus-wide extraction a rate pool separate from the
// interactive deployment's. Reaching api.openai.com needs a key; there is no identity-based
// path to it.
//
// Everything else here remains keyless and should stay that way. Azure SQL is Entra-only,
// Cosmos and the Foundry account both set disableLocalAuth, storage uses identity-based
// connections, CI federates with OIDC. This is the exception, it is scoped to job adverts
// alone - candidate profiles never go through it - and a deployment that does not want it
// simply does not set aiOpenAiKeyEnabled, in which case no vault is created.
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

resource secretsReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, readerPrincipalId, keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: readerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Without this, the deploy succeeds and then nobody can put the key in: the vault is
// RBAC-authorised, and creating a resource does not grant its creator data-plane access. The
// same reasoning that makes the administrator a Cognitive Services OpenAI User on the Foundry
// account and an Entra admin on SQL - a template that provisions something unusable is not
// finished.
@description('Built-in Key Vault Secrets Officer: set and read secret values.')
var keyVaultSecretsOfficer = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource administratorAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(administratorObjectId)) {
  scope: vault
  name: guid(vault.id, administratorObjectId, keyVaultSecretsOfficer)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficer)
    principalId: administratorObjectId
    // Deliberately 'User'. Declaring a human as ServicePrincipal fails with a
    // principal-not-found error that reads as though the object id were wrong.
    principalType: 'User'
  }
}

output vaultName string = vault.name
output vaultUri string = vault.properties.vaultUri

@description('URI the function app references. Versionless, so rotating the secret needs no redeploy.')
output openAiSecretUri string = '${vault.properties.vaultUri}secrets/${openAiSecretName}'

output openAiSecretName string = openAiSecretName
