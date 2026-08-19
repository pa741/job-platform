@description('Azure region.')
param location string

@description('Resource name prefix.')
param namePrefix string

@description('Suffix for globally unique names.')
param resourceToken string

param tags object

@description('Principal id of the ingest managed identity.')
param ingestPrincipalId string

@description('Object id of the human administrator, so Data Explorer still works with local auth off.')
param administratorObjectId string

var databaseName = 'jobplatform'

// Free tier: 1000 RU/s and 25 GB, one account per subscription, opt-in only at creation.
resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: 'cosmos-${namePrefix}-${resourceToken}'
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    enableFreeTier: true
    // No keys. The only way in is Entra plus a data-plane role assignment.
    disableLocalAuth: true
    minimalTlsVersion: 'Tls12'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    capabilities: []
    backupPolicy: {
      type: 'Periodic'
      periodicModeProperties: {
        backupIntervalInMinutes: 1440
        backupRetentionIntervalInHours: 48
        backupStorageRedundancy: 'Local'
      }
    }
  }
}

// Throughput is shared at the database level and capped at the free-tier ceiling, so
// every container below it stays free.
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: account
  name: databaseName
  properties: {
    resource: {
      id: databaseName
    }
    options: {
      autoscaleSettings: {
        maxThroughput: 1000
      }
    }
  }
}

// Run digests and daily rollups. Partitioned by search term, which is the axis every
// dashboard query filters on, and the only dimension recoverable from a blob name.
resource metricsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'metrics'
  properties: {
    resource: {
      id: 'metrics'
      partitionKey: {
        paths: [
          '/searchTerm'
        ]
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [
          {
            path: '/type/?'
          }
          {
            path: '/searchTerm/?'
          }
          {
            path: '/scrapeDate/?'
          }
          {
            path: '/scrapedAtUtc/?'
          }
        ]
        excludedPaths: [
          // Nothing queries inside the nested breakdowns; indexing them only costs RUs.
          {
            path: '/*'
          }
        ]
      }
    }
  }
}

// Checkpoint store for the change-feed processor the realtime piece will need.
// Provisioned now so adding that function is a code change, not an infrastructure one.
resource leasesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'leases'
  properties: {
    resource: {
      id: 'leases'
      partitionKey: {
        paths: [
          '/id'
        ]
        kind: 'Hash'
      }
    }
  }
}

@description('Built-in Cosmos DB Data Contributor (read and write documents, no control plane).')
resource dataContributorRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions@2024-11-15' existing = {
  parent: account
  name: '00000000-0000-0000-0000-000000000002'
}

resource ingestDataRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: account
  name: guid(account.id, ingestPrincipalId, 'data-contributor')
  properties: {
    roleDefinitionId: dataContributorRole.id
    principalId: ingestPrincipalId
    scope: account.id
  }
}

// Without this the portal's Data Explorer cannot read the account at all, because
// disableLocalAuth removes the key path it would otherwise use.
resource administratorDataRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: account
  name: guid(account.id, administratorObjectId, 'data-contributor')
  properties: {
    roleDefinitionId: dataContributorRole.id
    principalId: administratorObjectId
    scope: account.id
  }
}

output accountName string = account.name
output accountEndpoint string = account.properties.documentEndpoint
output databaseName string = databaseName
output metricsContainerName string = metricsContainer.name
