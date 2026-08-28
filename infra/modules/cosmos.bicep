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
          // Daily rollups serialise their day as `date`, not `scrapeDate`, and their
          // freshness as `updatedAtUtc`. Without these two the API's rollup range query -
          // the dashboard's main time series - degrades to a scan of the partition.
          {
            path: '/date/?'
          }
          {
            path: '/updatedAtUtc/?'
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

// The AI call ledger: one document per model call, so a failure is something somebody can
// see rather than something a count silently absorbs. Separate from `metrics` because that
// container is partitioned by search term and a model call has no search term.
//
// Partitioned by UTC day. Every question worth asking of this data is "what happened
// recently", which a day partition answers by reading one partition, and it bounds partition
// growth without anybody having to think about it.
//
// The database's throughput is shared, so this container adds no cost against the free-tier
// 1000 RU/s ceiling - which is load-bearing, not incidental.
resource aiCallsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'aiCalls'
  properties: {
    resource: {
      id: 'aiCalls'
      partitionKey: {
        paths: [
          '/day'
        ]
        kind: 'Hash'
      }
      // Operational records, not a system of record. Ninety days is long enough to see a
      // pattern and short enough that the container never becomes a storage question.
      defaultTtl: 7776000
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [
          {
            path: '/type/?'
          }
          {
            path: '/day/?'
          }
          {
            path: '/occurredAtUtc/?'
          }
          {
            path: '/outcome/?'
          }
          {
            path: '/operation/?'
          }
        ]
        excludedPaths: [
          // Nothing queries inside the ids or the reason text; indexing them only costs RUs.
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
