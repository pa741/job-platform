// Often not the same region as the rest of the stack. The free offer is only
// provisionable in a subset of regions - Spain Central and West Europe reject it,
// France Central accepts it. Being cross-region costs a few ms per round trip, which is
// immaterial for a once-daily batch.
@description('Azure region for the SQL server. Must support the free offer.')
param location string

@description('Resource name prefix.')
param namePrefix string

@description('Suffix for globally unique names.')
param resourceToken string

param tags object

@description('Object id of the Entra principal to make server administrator.')
param administratorObjectId string

@description('Display name of that principal.')
param administratorLoginName string

param tenantId string

var databaseName = 'jobsdb'

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-${namePrefix}-${resourceToken}'
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: administratorLoginName
      sid: administratorObjectId
      tenantId: tenantId
      // No SQL login exists at all. There is no password to rotate, store, or leak
      // into a public repository.
      azureADOnlyAuthentication: true
    }
  }
}

// Serverless General Purpose on the free offer.
//
// Cost model worth understanding: serverless bills wall-clock time *online*, not CPU. At
// minCapacity 0.5 with the minimum 60-minute auto-pause delay, one daily ingest keeps the
// database awake about an hour, roughly 1,800 vCore-seconds a day or ~54k a month against
// the free grant of 100k. Several runs a day would exceed it - which is why exhaustion is
// set to pause rather than bill.
resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 34359738368 // 32 GB, the free-offer ceiling.
    minCapacity: json('0.5')
    autoPauseDelay: 60 // Minutes; 60 is the minimum the platform allows.
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
    useFreeLimit: true
    // The cost guarantee: when the monthly grant runs out the database pauses until the
    // first of the next month instead of falling through to paid rates.
    freeLimitExhaustionBehavior: 'AutoPause'
  }
}

// The function app has no fixed outbound IP on Flex Consumption, so this is the rule that
// lets it connect. Start and end of 0.0.0.0 is the documented "Azure services" marker; it
// does not open the server to the internet, and Entra-only auth still gates every login.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output serverName string = sqlServer.name
output serverFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = databaseName

@description('Passwordless connection string. Contains no secret: authentication is Entra-based.')
output connectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${databaseName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connect Timeout=60;'
