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
output sqlConnectionString string = sql.outputs.connectionString
output landingContainerName string = landingContainerName
