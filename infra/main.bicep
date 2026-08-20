targetScope = 'resourceGroup'

// ---------------------------------------------------------------------------
// job-platform ingestion infrastructure.
//
// This repository is public, so no parameter has a real default. Supply values via
// infra/main.bicepparam, which reads them from the environment (see README).
// ---------------------------------------------------------------------------

@description('Azure region for new resources. Must support Flex Consumption, Cosmos DB and Azure SQL.')
param location string = resourceGroup().location

@description('Short name used as a prefix for every resource, e.g. "jobplatform".')
@minLength(3)
@maxLength(16)
param namePrefix string

// Separate from `location` because the SQL free offer is not provisionable everywhere:
// Spain Central and West Europe reject it, France Central accepts it. Probe before changing.
@description('Region for the Azure SQL server. Must be one that supports the free offer.')
param sqlLocation string = location

@description('Existing storage account that the scraper uploads into.')
param landingStorageAccountName string

@description('Container the scraper writes to, and the ingest function watches.')
param landingContainerName string = 'jobs-landing'

@description('Object id of the Microsoft Entra principal to make SQL admin and Cosmos data reader (i.e. you).')
param administratorObjectId string

@description('Display name (UPN) of that principal, shown as the SQL server Entra admin.')
param administratorLoginName string

@description('Entra tenant id.')
param tenantId string = subscription().tenantId

@description('Container image for the API. The default is the public image this repo CI publishes.')
param apiContainerImage string = 'ghcr.io/pa741/job-platform-api:latest'

@description('Application (client) id of the API app registration. Empty leaves the API without an identity provider, so every protected route answers 401.')
param apiClientId string = ''

@description('Serve API read endpoints without a token. For local or demo use only.')
param apiAllowAnonymousReads bool = false

@description('Browser origins allowed to call the API, e.g. the Static Web App.')
param apiAllowedOrigins array = []

// Defaults to keyword so a fresh deploy needs no Key Vault, no key and no third-party
// account, and still has a working matching endpoint.
@description('Which CV ranker the API uses. "anthropic" additionally provisions a Key Vault.')
@allowed([
  'keyword'
  'anthropic'
])
param matchingProvider string = 'keyword'

// Separate from `location` for the same reason `sqlLocation` is, and with the same trap:
// Static Web Apps is offered in a handful of regions, and a region can additionally stop
// accepting new customers. On this subscription westeurope rejects creation outright
// ("The selected region is currently not accepting new customers"), while eastus2,
// centralus, westus2 and eastasia all validate. Probe before changing it. The region is a
// control-plane choice - the content itself is served from Microsoft's global edge - so
// being outside Europe costs nothing at request time.
// Defaults to the free offer so a fresh clone of this public repository deploys at zero
// cost. Switching to 'basic' costs a few euros a month and removes the cold start; it is
// also a one-way move for that database - see infra/modules/sql.bicep.
@description('Database compute model: the free serverless offer, or always-on Basic.')
@allowed([
  'free-serverless'
  'basic'
])
param sqlSku string = 'free-serverless'

@description('Region for the Static Web App. Offered only in some regions; probe before changing.')
param webLocation string = 'eastus2'

@description('Deploy the dashboard Static Web App.')
param deployWeb bool = true

@description('Deterministic suffix keeping globally unique names stable across deployments.')
param resourceToken string = toLower(uniqueString(subscription().id, resourceGroup().id, namePrefix))

var tags = {
  application: 'job-platform'
  component: 'ingestion'
  managedBy: 'bicep'
}

// ---------------------------------------------------------------------------
// Landing zone: a container on the storage account the scraper already uses.
// ---------------------------------------------------------------------------

resource landingStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: landingStorageAccountName
}

resource landingBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: landingStorage
  name: 'default'
}

resource landingBlobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: landingBlobService
  name: landingContainerName
  properties: {
    publicAccess: 'None'
  }
}

// Events Event Grid could not deliver land here rather than being dropped silently.
resource deadLetterContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: landingBlobService
  name: 'eventgrid-deadletter'
  properties: {
    publicAccess: 'None'
  }
}

// ---------------------------------------------------------------------------
// Modules
// ---------------------------------------------------------------------------

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
  }
}

module identity 'modules/identity.bicep' = {
  name: 'identity'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
  }
}

module cosmos 'modules/cosmos.bicep' = {
  name: 'cosmos'
  params: {
    location: location
    namePrefix: namePrefix
    resourceToken: resourceToken
    tags: tags
    ingestPrincipalId: identity.outputs.principalId
    administratorObjectId: administratorObjectId
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    location: sqlLocation
    namePrefix: namePrefix
    resourceToken: resourceToken
    tags: tags
    administratorObjectId: administratorObjectId
    administratorLoginName: administratorLoginName
    tenantId: tenantId
    sqlSku: sqlSku
  }
}

module functionApp 'modules/functionapp.bicep' = {
  name: 'functionApp'
  params: {
    location: location
    namePrefix: namePrefix
    resourceToken: resourceToken
    tags: tags
    identityResourceId: identity.outputs.resourceId
    identityClientId: identity.outputs.clientId
    identityPrincipalId: identity.outputs.principalId
    applicationInsightsConnectionString: monitoring.outputs.connectionString
    landingStorageAccountName: landingStorageAccountName
    landingContainerName: landingContainerName
    cosmosAccountEndpoint: cosmos.outputs.accountEndpoint
    cosmosDatabaseName: cosmos.outputs.databaseName
    sqlConnectionString: sql.outputs.connectionString
  }
}

// Provisioned only when an LLM ranker is selected. The keyword default deploys no vault at
// all, which keeps the "no secret exists to leak" property intact for anyone cloning this.
module keyVault 'modules/keyvault.bicep' = if (matchingProvider == 'anthropic') {
  name: 'keyVault'
  params: {
    location: location
    namePrefix: namePrefix
    resourceToken: resourceToken
    tags: tags
    apiPrincipalId: identity.outputs.principalId
  }
}

module staticWebApp 'modules/staticwebapp.bicep' = if (deployWeb) {
  name: 'staticWebApp'
  params: {
    location: webLocation
    namePrefix: namePrefix
    tags: tags
  }
}

module containerApp 'modules/containerapp.bicep' = {
  name: 'containerApp'
  params: {
    location: location
    namePrefix: namePrefix
    resourceToken: resourceToken
    tags: tags
    identityResourceId: identity.outputs.resourceId
    identityClientId: identity.outputs.clientId
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
    applicationInsightsConnectionString: monitoring.outputs.connectionString
    cosmosAccountEndpoint: cosmos.outputs.accountEndpoint
    cosmosDatabaseName: cosmos.outputs.databaseName
    sqlConnectionString: sql.outputs.connectionString
    containerImage: apiContainerImage
    tenantId: tenantId
    apiClientId: apiClientId
    allowAnonymousReads: apiAllowAnonymousReads
    // The dashboard's origin is appended here rather than configured by hand. A Static Web
    // App's hostname is generated at creation, so it cannot be known in advance - taking it
    // from the module's output is what keeps CORS correct without a second deploy pass.
    allowedOrigins: deployWeb
      ? union(apiAllowedOrigins, [staticWebApp!.outputs.url])
      : apiAllowedOrigins
    matchingProvider: matchingProvider
    anthropicSecretUri: matchingProvider == 'anthropic' ? keyVault!.outputs.anthropicSecretUri : ''
  }
}

module rbac 'modules/rbac.bicep' = {
  name: 'rbac'
  params: {
    landingStorageAccountName: landingStorageAccountName
    functionStorageAccountName: functionApp.outputs.storageAccountName
    applicationInsightsName: monitoring.outputs.applicationInsightsName
    ingestPrincipalId: identity.outputs.principalId
  }
}

module eventGrid 'modules/eventgrid.bicep' = {
  name: 'eventGrid'
  params: {
    location: location
    namePrefix: namePrefix
    landingStorageAccountName: landingStorageAccountName
    landingContainerName: landingContainerName
    deadLetterContainerName: deadLetterContainer.name
    functionAppName: functionApp.outputs.name
    functionName: 'JobDigestFunction'
    tags: tags
  }
  // The subscription cannot validate its endpoint until the function exists and RBAC
  // lets the host read the blob.
  dependsOn: [
    rbac
  ]
}

// ---------------------------------------------------------------------------
// Outputs. Deliberately no secrets: SQL is Entra-only, Cosmos has local auth
// disabled, and storage uses identity-based connections.
// ---------------------------------------------------------------------------

output functionAppName string = functionApp.outputs.name
output functionAppResourceId string = functionApp.outputs.resourceId
output ingestIdentityName string = identity.outputs.name
output ingestIdentityClientId string = identity.outputs.clientId
output cosmosAccountName string = cosmos.outputs.accountName
output sqlLocation string = sqlLocation
output sqlServerFqdn string = sql.outputs.serverFqdn
output sqlDatabaseName string = sql.outputs.databaseName
output sqlSku string = sql.outputs.sqlSku
output sqlConnectionString string = sql.outputs.connectionString
output landingContainerName string = landingContainerName
output apiName string = containerApp.outputs.name
output apiUrl string = containerApp.outputs.url
output apiFqdn string = containerApp.outputs.fqdn
output matchingProvider string = matchingProvider
output webName string = deployWeb ? staticWebApp!.outputs.name : ''
output webUrl string = deployWeb ? staticWebApp!.outputs.url : ''
output keyVaultName string = matchingProvider == 'anthropic' ? keyVault!.outputs.vaultName : ''
output anthropicSecretName string = matchingProvider == 'anthropic' ? keyVault!.outputs.anthropicSecretName : ''
