@description('Azure region.')
param location string

@description('Resource name prefix.')
param namePrefix string

@description('Suffix for globally unique names.')
param resourceToken string

param tags object

@description('Principal id of the shared managed identity that sends and mints client tokens.')
param callerPrincipalId string

@description('Origins allowed to negotiate from a browser, e.g. the Static Web App.')
param allowedOrigins array = []

// ---------------------------------------------------------------------------
// The realtime transport. `model.md` names this piece "Azure Functions (Cosmos trigger),
// SignalR/Web PubSub", and this is the SignalR half of that choice.
//
// Serverless mode, deliberately. The three modes are not interchangeable: Default expects an
// ASP.NET Core app hosting the hub and holding the connections, Classic switches between them by
// guessing, and Serverless is the one where no server owns the hub and everything reaches the
// service through its REST API - which is exactly the shape here, where a Function reacts to a
// change feed and an API mints client tokens. In Default mode the negotiate below returns a URL
// that no hub is listening on, and the failure is a client that connects and never hears
// anything.
//
// Free tier: one unit, 20 concurrent connections, 20,000 messages a day. That ceiling is
// load-bearing in the same way Cosmos's 1000 RU/s is - this is a dashboard for one person, and
// the moment it needs more it needs a conversation rather than a bigger SKU.
// ---------------------------------------------------------------------------

resource signalR 'Microsoft.SignalRService/signalR@2023-02-01' = {
  name: 'sigr-${namePrefix}-${resourceToken}'
  location: location
  tags: tags
  sku: {
    name: 'Free_F1'
    tier: 'Free'
    capacity: 1
  }
  kind: 'SignalR'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    // The line that keeps the no-secret property true. Without it this resource mints two
    // connection strings with embedded access keys, and every sample on the internet tells you
    // to paste one into configuration - which would make this the second secret in a system
    // whose whole claim is that it has none.
    disableLocalAuth: true

    features: [
      {
        flag: 'ServiceMode'
        value: 'Serverless'
      }
      {
        // Off. It logs the content of every message to the service's own diagnostics, and these
        // messages carry which postings an extraction lost - operational detail that belongs in
        // the ledger and App Insights, not in a third place with different retention.
        flag: 'EnableMessagingLogs'
        value: 'false'
      }
    ]

    cors: {
      // The dashboard's origin, threaded from the Static Web App module's output the same way
      // the API's CORS list is. Never hard-coded: the hostname is generated at creation.
      allowedOrigins: allowedOrigins
    }
  }
}

// ---------------------------------------------------------------------------
// Access. Data-plane RBAC, exactly as Cosmos and the AI account do it.
// ---------------------------------------------------------------------------

@description('Built-in SignalR App Server: send to hubs and mint client access tokens, nothing else.')
var appServerRole = '420fcaa2-552c-430f-98ca-3264be4806c7'

// One assignment covering both callers, because both run under the shared user-assigned
// identity - the ingest function that broadcasts a failure and the API that negotiates a
// client connection. That shared identity has now paid off a fourth time.
resource callerAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: signalR
  name: guid(signalR.id, callerPrincipalId, appServerRole)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', appServerRole)
    principalId: callerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

@description('What the application configures as Realtime:ServiceUri. Not a secret - there is no key behind it.')
output serviceUri string = 'https://${signalR.properties.hostName}'

output name string = signalR.name
