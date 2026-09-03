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

@description('Container holding the curated analysis surface. Never watched by Event Grid.')
param curatedContainerName string = 'jobs-curated'

@description('Container holding the scraper configuration the API publishes and the NAS reads.')
param scraperConfigContainerName string = 'scraper-config'

@description('Container holding rendered CVs and cover letters, handed out as short-lived signed URLs.')
param applicationPacksContainerName string = 'application-packs'

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

@description('Application principals that act for a candidate on the MCP surface, as "<principalObjectId>=<candidateObjectId>" pairs. Empty leaves the surface delegated-only: an app-only token then resolves to no candidate and every tool says so.')
param apiMcpAppPrincipals array = []

// Defaults to none so a fresh deploy provisions no model capacity and incurs no model spend.
// Selecting azureopenai adds a Foundry resource with two deployments - and, notably, no
// secret: the shared managed identity authenticates to it the same way it does to SQL, Cosmos
// and Storage, which is what removed the one exception this architecture used to carry.
@description('Which AI provider the platform is configured for. "azureopenai" additionally provisions a Foundry resource.')
@allowed([
  'none'
  'azureopenai'
])
param aiProvider string = 'none'

// A model name without its version is only half a pin: the two have to move together, and a
// name overridden against a stale version fails the deployment with an unhelpful
// "model not found". Both are parameters for the same reason - quota for a given family is
// per subscription, so the models a clone can actually deploy are not knowable here.
@description('Model behind the high-volume deployment: extraction and candidacy assessment.')
param aiBulkModelName string = 'gpt-5.6-luna'

@description('Version of the bulk model. Must match the model name.')
param aiBulkModelVersion string = '2026-07-09'

@description('Model behind the writing deployment: tailored CV and cover letter.')
param aiWritingModelName string = 'gpt-5.6-sol'

// Off by default, and the only thing in this template that provisions a place to keep a
// secret. It buys the gpt-5.6 family for batch extraction, which Azure's batch matrix does not
// carry, and a rate pool separate from the interactive deployment's. A clone that leaves this
// false deploys with no vault and nothing to leak, and the backfill falls back to the queue.
@description('Provision a Key Vault for an OpenAI API key, enabling the batch extraction path.')
param aiOpenAiBatchEnabled bool = false

@description('Version of the writing model. Must match the model name.')
param aiWritingModelVersion string = '2026-07-09'

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

// The analysis surface: partitioned Parquet written from SQL, read by DuckDB, pandas,
// Fabric or Synapse serverless without anything running.
//
// A separate container rather than a prefix under jobs-landing, for two reasons that both
// matter. The Event Grid subscription is scoped to the landing container, so a curated write
// can never trigger an ingest and loop. And the ingest identity holds Blob Data *Reader* on
// the landing account deliberately - writing curated output there would mean widening that to
// Contributor and giving the function the ability to modify or delete what the scraper
// uploaded, which is exactly the permission the read-only grant exists to withhold.
resource curatedBlobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: landingBlobService
  name: curatedContainerName
  properties: {
    publicAccess: 'None'
  }
}

// What to scrape, published by the API and read by the scraper on the NAS.
//
// A third container for the same two reasons the curated one is separate, plus one of its own.
// Event Grid watches jobs-landing, so a configuration write here can never be mistaken for an
// upload to ingest. The identity's account-wide grant stays Blob Data *Reader*, and this gets
// its own scoped Contributor - the API can rewrite the configuration and still cannot touch the
// only copy of the scraped data. And the scraper needs *read* here while it needs *write* on
// jobs-landing, so the two permissions the NAS holds stay separable.
resource scraperConfigContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: landingBlobService
  name: scraperConfigContainerName
  properties: {
    publicAccess: 'None'
  }
}

// Rendered application documents: the PDF and DOCX a browser uploads to an employer's form.
//
// A fourth container, for a reason the other three share and one they do not. Shared: Event Grid
// watches jobs-landing alone, so writing a rendered CV can never be mistaken for an upload to
// ingest, and the identity's account-wide grant stays Blob Data *Reader* while this gets its own
// scoped Contributor. Its own: this is the only container whose contents leave the tenant, as
// short-lived user-delegation SAS URLs handed to a browser. Nothing else may be reachable through
// a link, and a prefix under an existing container would have made the blast radius of a
// mis-scoped signature the whole of the scraped corpus.
//
// The signature needs no stored key. Storage Blob Data Reader at account scope already carries
// generateUserDelegationKey, so the identity signs with Entra credentials and the repository keeps
// its property that a fresh clone deploys with nothing to leak.
resource applicationPacksContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: landingBlobService
  name: applicationPacksContainerName
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

// The realtime transport. Provisioned unconditionally: it is free-tier, it carries no data of
// its own, and making it optional would mean the dashboard has to handle a missing feed as well
// as a quiet one - two states that look identical to a user and behave differently in code.
module signalR 'modules/signalr.bicep' = {
  name: 'signalR'
  params: {
    location: location
    namePrefix: namePrefix
    resourceToken: resourceToken
    tags: tags
    callerPrincipalId: identity.outputs.principalId
    // Same origin list the API gets, and for the same reason: the browser negotiates against
    // this service directly, so its CORS must match the API's or the connection is refused
    // after a negotiate that looked fine.
    allowedOrigins: deployWeb
      ? union(apiAllowedOrigins, [staticWebApp!.outputs.url])
      : apiAllowedOrigins
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
    curatedContainerName: curatedBlobContainer.name
    applicationPacksContainerName: applicationPacksContainer.name
    cosmosAccountEndpoint: cosmos.outputs.accountEndpoint
    cosmosDatabaseName: cosmos.outputs.databaseName
    sqlConnectionString: sql.outputs.connectionString
    aiProvider: aiProvider
    openAiEndpoint: aiProvider == 'azureopenai' ? openAi!.outputs.endpoint : ''
    openAiBulkDeployment: aiProvider == 'azureopenai' ? openAi!.outputs.bulkDeployment : ''
    openAiWritingDeployment: aiProvider == 'azureopenai' ? openAi!.outputs.writingDeployment : ''
    openAiEmbeddingDeployment: aiProvider == 'azureopenai' ? openAi!.outputs.embeddingDeployment : ''
    openAiApiKeySecretUri: aiOpenAiBatchEnabled ? keyVault!.outputs.openAiSecretUri : ''
  }
}

// Provisioned only when an AI provider is selected. The default deploys nothing, so a fresh
// clone of this public repository still stands up the whole pipeline at no model cost.
//
// Note what is NOT here any more: a Key Vault. It existed for one Anthropic API key, and
// Azure OpenAI's Entra authentication removed the need for it entirely.
// The one vault, for the one secret. See the module for why it came back after being deleted.
module keyVault 'modules/keyvault.bicep' = if (aiOpenAiBatchEnabled) {
  name: 'keyVault'
  params: {
    location: location
    namePrefix: namePrefix
    resourceToken: resourceToken
    tags: tags
    readerPrincipalId: identity.outputs.principalId
    administratorObjectId: administratorObjectId
  }
}

module openAi 'modules/openai.bicep' = if (aiProvider == 'azureopenai') {
  name: 'openAi'
  params: {
    location: location
    namePrefix: namePrefix
    resourceToken: resourceToken
    tags: tags
    callerPrincipalId: identity.outputs.principalId
    administratorObjectId: administratorObjectId
    bulkModelName: aiBulkModelName
    bulkModelVersion: aiBulkModelVersion
    writingModelName: aiWritingModelName
    writingModelVersion: aiWritingModelVersion
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
    mcpAppPrincipals: apiMcpAppPrincipals
    aiProvider: aiProvider
    openAiEndpoint: aiProvider == 'azureopenai' ? openAi!.outputs.endpoint : ''
    openAiBulkDeployment: aiProvider == 'azureopenai' ? openAi!.outputs.bulkDeployment : ''
    openAiWritingDeployment: aiProvider == 'azureopenai' ? openAi!.outputs.writingDeployment : ''
    openAiEmbeddingDeployment: aiProvider == 'azureopenai' ? openAi!.outputs.embeddingDeployment : ''
    signalRServiceUri: signalR.outputs.serviceUri
    landingStorageAccountName: landingStorageAccountName
    scraperConfigContainerName: scraperConfigContainer.name
    applicationPacksContainerName: applicationPacksContainer.name
  }
}

module rbac 'modules/rbac.bicep' = {
  name: 'rbac'
  params: {
    landingStorageAccountName: landingStorageAccountName
    functionStorageAccountName: functionApp.outputs.storageAccountName
    applicationInsightsName: monitoring.outputs.applicationInsightsName
    ingestPrincipalId: identity.outputs.principalId
    curatedContainerName: curatedBlobContainer.name
    scraperConfigContainerName: scraperConfigContainer.name
    applicationPacksContainerName: applicationPacksContainer.name
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
// Outputs. There are no secrets to omit: SQL is Entra-only, Cosmos and the Foundry
// resource both have local auth disabled, storage uses identity-based connections,
// and CI federates with OIDC. Every value below is safe in deployment history.
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
output scraperConfigContainerName string = scraperConfigContainer.name
output apiName string = containerApp.outputs.name
output apiUrl string = containerApp.outputs.url
output apiFqdn string = containerApp.outputs.fqdn
output aiProvider string = aiProvider
output aiEndpoint string = aiProvider == 'azureopenai' ? openAi!.outputs.endpoint : ''
output aiBulkDeployment string = aiProvider == 'azureopenai' ? openAi!.outputs.bulkDeployment : ''
output aiWritingDeployment string = aiProvider == 'azureopenai' ? openAi!.outputs.writingDeployment : ''

@description('Deployment name for the embedding pass. Empty where no AI provider is configured.')
output aiEmbeddingDeployment string = aiProvider == 'azureopenai' ? openAi!.outputs.embeddingDeployment : ''

@description('What the API and the ingest function reach the realtime service on. Not a secret.')
output signalRServiceUri string = signalR.outputs.serviceUri
output keyVaultName string = aiOpenAiBatchEnabled ? keyVault!.outputs.vaultName : ''
output openAiSecretName string = aiOpenAiBatchEnabled ? keyVault!.outputs.openAiSecretName : ''
output webName string = deployWeb ? staticWebApp!.outputs.name : ''
output webUrl string = deployWeb ? staticWebApp!.outputs.url : ''
