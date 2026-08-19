@description('Azure region.')
param location string

@description('Resource name prefix.')
param namePrefix string

param tags object

// A user-assigned identity, not system-assigned: the API in Container Apps will need the
// same database and Cosmos grants later, and a shared identity means granting them once.
resource ingestIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${namePrefix}-ingest'
  location: location
  tags: tags
}

output resourceId string = ingestIdentity.id
output name string = ingestIdentity.name
output clientId string = ingestIdentity.properties.clientId
output principalId string = ingestIdentity.properties.principalId
