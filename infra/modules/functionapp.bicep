@description('Azure region.')
param location string

@description('Resource name prefix.')
param namePrefix string

@description('Suffix for globally unique names.')
param resourceToken string

param tags object

param identityResourceId string
param identityClientId string
param identityPrincipalId string

param applicationInsightsConnectionString string

param landingStorageAccountName string
param landingContainerName string

param cosmosAccountEndpoint string
param cosmosDatabaseName string

@description('Passwordless (Entra) SQL connection string.')
param sqlConnectionString string

var deploymentContainerName = 'deployment-package'

// A storage account of its own, separate from the landing zone. The Functions host keeps
// leases, the deployment package and internal state here; mixing that with scraper output
// would both muddle the data and risk the host's own writes tripping the blob trigger.
resource functionStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  // Storage account names cap at 24 characters and allow only lowercase alphanumerics,
  // so both halves are truncated rather than concatenated whole.
  name: take('st${namePrefix}fn${resourceToken}', 24)
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // No account keys are usable, so there is no connection string to leak.
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Enabled'
  }
}

resource functionBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: functionStorage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: functionBlobService
  name: deploymentContainerName
  properties: {
    publicAccess: 'None'
  }
}

// Storage Blob Data Owner on its own storage: the host needs to write the deployment
// package and its internal containers, not just read them.
resource hostStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: functionStorage
  name: guid(functionStorage.id, identityPrincipalId, 'blob-data-owner')
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
    )
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${namePrefix}'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    // Flex Consumption is Linux only.
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${namePrefix}-ingest-${resourceToken}'
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityResourceId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${functionStorage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identityResourceId
          }
        }
      }
      scaleAndConcurrency: {
        // One blob a day. Capping instances keeps a runaway retry loop from fanning out
        // across the serverless database.
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '9.0'
      }
    }
    siteConfig: {
      alwaysOn: false
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        // Identity-based host storage: no AzureWebJobsStorage connection string exists.
        {
          name: 'AzureWebJobsStorage__accountName'
          value: functionStorage.name
        }
        {
          name: 'AzureWebJobsStorage__credential'
          value: 'managedidentity'
        }
        {
          name: 'AzureWebJobsStorage__clientId'
          value: identityClientId
        }
        // The landing zone, as a separately named connection.
        {
          name: 'LandingStorage__serviceUri'
          value: 'https://${landingStorageAccountName}.blob.${environment().suffixes.storage}'
        }
        {
          name: 'LandingStorage__blobServiceUri'
          value: 'https://${landingStorageAccountName}.blob.${environment().suffixes.storage}'
        }
        {
          name: 'LandingStorage__queueServiceUri'
          value: 'https://${landingStorageAccountName}.queue.${environment().suffixes.storage}'
        }
        {
          name: 'LandingStorage__credential'
          value: 'managedidentity'
        }
        {
          name: 'LandingStorage__clientId'
          value: identityClientId
        }
        {
          name: 'LandingContainerName'
          value: landingContainerName
        }
        {
          name: 'ManagedIdentityClientId'
          value: identityClientId
        }
        {
          name: 'Cosmos__AccountEndpoint'
          value: cosmosAccountEndpoint
        }
        {
          name: 'Cosmos__DatabaseName'
          value: cosmosDatabaseName
        }
        {
          name: 'Cosmos__MetricsContainerName'
          value: 'metrics'
        }
        {
          // `Active Directory Default` cannot guess which identity to use when the app has
          // a user-assigned one, and fails with "Unable to load the proper Managed
          // Identity". The client id is appended here rather than baked into the sql
          // module's output, so that output stays usable by a developer signing in as
          // themselves.
          name: 'SqlConnectionString'
          value: '${sqlConnectionString}User Id=${identityClientId};'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: applicationInsightsConnectionString
        }
        {
          name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING'
          value: 'ClientId=${identityClientId};Authorization=AAD'
        }
      ]
    }
  }
  dependsOn: [
    deploymentContainer
    hostStorageRole
  ]
}

output name string = functionApp.name
output resourceId string = functionApp.id
output defaultHostName string = functionApp.properties.defaultHostName
output storageAccountName string = functionStorage.name
