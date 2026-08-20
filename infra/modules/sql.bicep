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

@description('Compute model for the database. See the comment on the sku variables below.')
@allowed([
  'free-serverless'
  'basic'
])
param sqlSku string = 'free-serverless'

var databaseName = 'jobsdb'

// Two shapes, one database.
//
// `free-serverless` is the default so that a fresh clone of this repository deploys at zero
// cost: General Purpose serverless on the free offer, 100,000 vCore-seconds a month, paused
// when idle.
//
// `basic` trades that for always-on. The free offer bills per second *online*, so the grant
// buys about 55 hours a month - always-on and free are mutually exclusive by construction,
// and every wake costs a ~1 minute cold start plus the whole auto-pause delay in vCore
// seconds. Basic is the DTU model, which has no serverless option at all, so it simply never
// pauses. At 5 DTU and a 2 GB ceiling it is the cheapest always-on tier Azure sells.
//
// One way. Microsoft's documentation is explicit: "Once you convert a free offer database to
// a paid service tier, you can't revert to the free offer." Going back means creating a new
// database and reloading it - which is cheap here, because ingestion is idempotent and every
// source CSV is still in the landing container.
var useFreeServerless = sqlSku == 'free-serverless'

var serverlessSku = {
  name: 'GP_S_Gen5'
  tier: 'GeneralPurpose'
  family: 'Gen5'
  capacity: 2
}

var basicSku = {
  name: 'Basic'
  tier: 'Basic'
  capacity: 5
}

var serverlessProperties = {
  minCapacity: json('0.5')
  // Minimum is 15 minutes, maximum 7 days, and -1 disables pausing entirely. 60 is the
  // platform default rather than a floor. A shorter delay does not shorten the cold start -
  // it only reduces how many vCore seconds each wake spends.
  autoPauseDelay: 60
  useFreeLimit: true
  // The cost guarantee: when the monthly grant runs out the database pauses until the first
  // of the next month instead of falling through to paid rates.
  freeLimitExhaustionBehavior: 'AutoPause'
  maxSizeBytes: 34359738368 // 32 GB, the free-offer ceiling.
}

var basicProperties = {
  // 2 GB is the Basic ceiling. The database holds single-digit megabytes and grows by well
  // under a megabyte a day, so this is years of headroom rather than a tight fit.
  maxSizeBytes: 2147483648
}

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

// The database. Its shape is chosen by `sqlSku` - see the variables above.
//
// Cost model worth understanding for the serverless case: it bills wall-clock time *online*,
// not CPU. At minCapacity 0.5 with a 60-minute auto-pause delay, one daily ingest keeps the
// database awake about an hour, roughly 1,800 vCore-seconds a day or ~54k a month against
// the free grant of 100k. Several runs a day would exceed it - which is why exhaustion is
// set to pause rather than bill.
resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  tags: tags
  sku: useFreeServerless ? serverlessSku : basicSku
  properties: union(
    {
      collation: 'SQL_Latin1_General_CP1_CI_AS'
      zoneRedundant: false
      requestedBackupStorageRedundancy: 'Local'
    },
    useFreeServerless ? serverlessProperties : basicProperties
  )
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

output sqlSku string = sqlSku
output serverName string = sqlServer.name
output serverFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = databaseName

@description('Passwordless connection string. Contains no secret: authentication is Entra-based.')
output connectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${databaseName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connect Timeout=60;'
