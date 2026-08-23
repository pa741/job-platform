@description('Storage account the scraper uploads into.')
param landingStorageAccountName string

@description('Storage account backing the Functions host.')
param functionStorageAccountName string

param applicationInsightsName string

@description('Principal id of the ingest managed identity.')
param ingestPrincipalId string

@description('Container the curated export writes into. Scoped write access, nothing wider.')
param curatedContainerName string

// Built-in role ids, hard-coded because they are stable platform GUIDs.
var storageBlobDataReader = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
var storageBlobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageQueueDataContributor = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var monitoringMetricsPublisher = '3913510d-42f4-4e42-8a64-420c390055eb'

resource landingStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: landingStorageAccountName
}

resource functionStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: functionStorageAccountName
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: applicationInsightsName
}

// Read-only on the landing zone. The ingest never needs to modify or delete what the
// scraper uploaded, so it cannot.
resource landingBlobRead 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: landingStorage
  name: guid(landingStorage.id, ingestPrincipalId, storageBlobDataReader)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReader)
    principalId: ingestPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource functionQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: functionStorage
  name: guid(functionStorage.id, ingestPrincipalId, storageQueueDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributor)
    principalId: ingestPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The blob trigger's retry and poison queues live on the *trigger's* connection - the
// landing account - not on the host storage account. Without this the extension cannot
// create `webjobs-blobtrigger-poison`, and a failed invocation dies with a 403 that
// masks the original error.
resource landingQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: landingStorage
  name: guid(landingStorage.id, ingestPrincipalId, storageQueueDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributor)
    principalId: ingestPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Required whenever APPLICATIONINSIGHTS_AUTHENTICATION_STRING uses Authorization=AAD;
// without it telemetry is silently rejected.
resource metricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: applicationInsights
  name: guid(applicationInsights.id, ingestPrincipalId, monitoringMetricsPublisher)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringMetricsPublisher)
    principalId: ingestPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The one new grant in this work, and it is deliberately the narrowest thing that works:
// Blob Data Contributor on a single container, not on the account.
//
// The account-level grant above stays Reader. Widening it would let the ingest modify or
// delete the scraper's uploads, and the landing container is the only copy of the source data
// - every replay and every backfill reads from it. A write scope that cannot reach it is
// worth the extra six lines.
resource curatedContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  name: '${landingStorageAccountName}/default/${curatedContainerName}'
}

resource curatedBlobWrite 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: curatedContainer
  name: guid(curatedContainer.id, ingestPrincipalId, storageBlobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributor)
    principalId: ingestPrincipalId
    principalType: 'ServicePrincipal'
  }
}
