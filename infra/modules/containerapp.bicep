@description('Azure region.')
param location string

@description('Resource name prefix.')
param namePrefix string

@description('Suffix for globally unique names.')
param resourceToken string

param tags object

param identityResourceId string
param identityClientId string

@description('Log Analytics workspace the environment sends console and system logs to.')
param logAnalyticsWorkspaceId string

param applicationInsightsConnectionString string

param cosmosAccountEndpoint string
param cosmosDatabaseName string

@description('Passwordless (Entra) SQL connection string.')
param sqlConnectionString string

@description('Container image. Defaults to the public GHCR image built by this repo CI.')
param containerImage string

@description('Entra tenant id, for validating bearer tokens.')
param tenantId string

@description('Application (client) id of the API app registration. Empty disables authentication.')
param apiClientId string = ''

@description('Serve read endpoints without a token. Never enable this on a public deployment.')
param allowAnonymousReads bool = false

@description('Origins allowed to call the API from a browser, e.g. the Static Web App.')
param allowedOrigins array = []

@description('Which AI provider the API resolves: none, or azureopenai.')
@allowed([
  'none'
  'azureopenai'
])
param aiProvider string = 'none'

@description('Azure OpenAI endpoint. Not a secret - the identity is what authenticates.')
param openAiEndpoint string = ''

@description('Deployment name for the high-volume pass: extraction and candidacy assessment.')
param openAiBulkDeployment string = ''

@description('Deployment name for the writing pass: tailored CV and cover letter.')
param openAiWritingDeployment string = ''

@description('Deployment name for the embedding pass: the profile and every advert, as vectors.')
param openAiEmbeddingDeployment string = ''

@description('Realtime service endpoint. Not a secret - the identity is what authenticates.')
param signalRServiceUri string = ''

@description('Storage account holding the scraper configuration container.')
param landingStorageAccountName string = ''

@description('Container the API publishes the scraper configuration into.')
param scraperConfigContainerName string = 'scraper-config'

var useAzureOpenAi = aiProvider == 'azureopenai' && !empty(openAiEndpoint)

// Array-typed configuration binds by index, so each origin becomes its own variable. Hoisted
// out of the container definition because a for-expression cannot appear inside concat().
var allowedOriginEnv = [
  for (origin, index) in allowedOrigins: {
    name: 'Api__AllowedOrigins__${index}'
    value: origin
  }
]

// Logs go to Azure Monitor rather than straight to the workspace, because the direct
// logAnalyticsConfiguration path requires the workspace's shared key. That key would be
// resolved by listKeys() into the deployment, which is precisely the kind of standing
// credential this architecture does not otherwise have. The diagnostic setting below reaches
// the same workspace using the control plane instead.
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${namePrefix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'azure-monitor'
    }
    zoneRedundant: false
  }
}

resource environmentDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: environment
  name: 'to-log-analytics'
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      {
        category: 'ContainerAppConsoleLogs'
        enabled: true
      }
      {
        category: 'ContainerAppSystemLogs'
        enabled: true
      }
    ]
  }
}

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${namePrefix}-api-${resourceToken}'
  location: location
  tags: tags
  identity: {
    // The same user-assigned identity the ingest function runs under. That was the reason it
    // is user-assigned rather than system-assigned: the SQL database user, the Cosmos data
    // role and the Application Insights grant were all made once, and this app inherits them
    // without a single new role assignment.
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityResourceId}': {}
    }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      // No registry credential: the image is public. A private registry would mean either a
      // stored password or an AcrPull grant, and the image contains no secrets to protect.
      //
      // And no secrets at all any more. This block used to carry a Key Vault reference to an
      // Anthropic API key - the single exception in the whole architecture. Azure OpenAI
      // authenticates with the same managed identity as everything else, so the vault, the
      // reference and the key are gone rather than merely well-handled.
      secrets: []
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat([
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
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
              // `Active Directory Default` cannot pick between identities when the app has a
              // user-assigned one and fails with "Unable to load the proper Managed
              // Identity". The client id is appended here for the same reason it is in the
              // function app, and for the same reason it is not baked into the sql module's
              // output: that output stays usable by a developer signing in as themselves.
              name: 'SqlConnectionString'
              value: '${sqlConnectionString}User Id=${identityClientId};'
            }
            {
              name: 'AzureAd__Instance'
              value: az.environment().authentication.loginEndpoint
            }
            {
              name: 'AzureAd__TenantId'
              value: tenantId
            }
            {
              name: 'AzureAd__ClientId'
              value: apiClientId
            }
            {
              name: 'Api__AllowAnonymousReads'
              value: string(allowAnonymousReads)
            }
            {
              name: 'Ai__Provider'
              value: aiProvider
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: applicationInsightsConnectionString
            }
            {
              name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING'
              value: 'ClientId=${identityClientId};Authorization=AAD'
            }
          ], allowedOriginEnv, useAzureOpenAi ? [
            {
              // Configuration, not a credential. The identity named above is what the token is
              // issued for; this only says which resource to ask.
              name: 'Ai__AzureOpenAi__Endpoint'
              value: openAiEndpoint
            }
            {
              name: 'Ai__AzureOpenAi__BulkDeployment'
              value: openAiBulkDeployment
            }
            {
              name: 'Ai__AzureOpenAi__WritingDeployment'
              value: openAiWritingDeployment
            }
            {
              name: 'Ai__AzureOpenAi__EmbeddingDeployment'
              value: openAiEmbeddingDeployment
            }
          ] : [], [
            {
              // Configuration, not a credential: the identity above is what mints client tokens
              // against this service. disableLocalAuth means its keys cannot be used at all.
              name: 'Realtime__ServiceUri'
              value: signalRServiceUri
            }
            {
              // Identity-based, like every other storage connection here. Empty leaves the
              // publisher unregistered, the searches endpoints report that the scraper has not
              // been told, and the scraper falls back to its own config.yaml - a deployment
              // without this is degraded, never broken.
              name: 'ScraperConfig__serviceUri'
              value: empty(landingStorageAccountName)
                ? ''
                : 'https://${landingStorageAccountName}.blob.core.windows.net'
            }
            {
              name: 'ScraperConfigContainerName'
              value: scraperConfigContainerName
            }
          ])
          probes: [
            {
              // Points at /health, which touches nothing. The readiness endpoint checks
              // Cosmos, and probing anything that reaches Azure SQL would hold the serverless
              // database awake around the clock and spend the whole free grant on health
              // checks - see the readiness check's own comment.
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        // Scale to zero when idle. Container Apps bills only for active replicas, so an API
        // nobody is calling costs nothing and stays inside the monthly free grant.
        minReplicas: 0
        // Capped low deliberately: every replica is a potential connection to a serverless
        // database billed by the second, so a traffic spike must not fan out across it.
        maxReplicas: 3
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '40'
              }
            }
          }
        ]
      }
    }
  }
}

output name string = api.name
output resourceId string = api.id
output fqdn string = api.properties.configuration.ingress.fqdn
output url string = 'https://${api.properties.configuration.ingress.fqdn}'
output environmentName string = environment.name
