@description('Azure region for the Foundry resource. Global Standard deployments serve from anywhere; this is the control-plane home.')
param location string

@description('Resource name prefix.')
param namePrefix string

@description('Suffix for globally unique names.')
param resourceToken string

param tags object

@description('Principal id of the shared managed identity that calls the models.')
param callerPrincipalId string

@description('Object id of the human administrator, so a developer can call the deployments as themselves.')
param administratorObjectId string = ''

@description('Model behind the high-volume deployment: extraction and candidacy assessment.')
param bulkModelName string = 'gpt-5.6-luna'

@description('Version of the bulk model. Pinned, so a new release is a deliberate change.')
param bulkModelVersion string = '2026-07-09'

@description('Model behind the writing deployment: tailored CV and cover letter.')
param writingModelName string = 'gpt-5.6-sol'

param writingModelVersion string = '2026-07-09'

@description('Model behind the embedding deployment: the profile and every advert, as vectors.')
param embeddingModelName string = 'text-embedding-3-small'

@description('Version of the embedding model. Pinned - a new version is a new vector space.')
param embeddingModelVersion string = '1'

// Tokens per minute, in thousands, and a rate ceiling rather than a reservation: Global
// Standard bills per token, so a higher number costs nothing until tokens actually flow. It
// was 100, and the first real backfill spent most of its calls collecting HTTP 429s.
@description('Thousands of tokens per minute for the bulk deployment.')
param bulkCapacity int = 250

@description('Thousands of tokens per minute for the writing deployment. Small: one call per application.')
param writingCapacity int = 20

// Generous, and it costs nothing to be. Global Standard bills per token and this model is priced
// two orders of magnitude below the chat deployments, so the whole corpus is a few pence a night;
// what the number buys is a first pass over several thousand adverts that does not spend itself
// collecting 429s, which is exactly how the first extraction backfill was lost.
@description('Thousands of tokens per minute for the embedding deployment.')
param embeddingCapacity int = 350

// ---------------------------------------------------------------------------
// The Foundry resource.
//
// THE SECRET IS GONE. This module is what removed the one exception the architecture used to
// carry. The previous AI provider needed an API key, which needed a Key Vault, a Container
// Apps secret reference, and an out-of-band `az keyvault secret set` that a fresh clone had to
// know about. Azure OpenAI authenticates with Microsoft Entra, so the shared managed identity
// that already reaches SQL, Cosmos and Storage reaches the models too - and `disableLocalAuth`
// below means the keys this resource would otherwise mint cannot be used even if they leak.
//
// The property that makes this repository publishable is now unqualified: there is no
// password, key or connection secret anywhere in the system.
// ---------------------------------------------------------------------------

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  // 'AIServices' rather than 'OpenAI': the same OpenAI models, on the resource kind Foundry
  // has standardised on. The endpoint shape and the RBAC roles are identical.
  name: 'aoai-${namePrefix}-${resourceToken}'
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    // Required for Entra authentication. Without a custom subdomain the resource is only
    // reachable at the regional endpoint, which does not accept bearer tokens - the failure is
    // a 401 that looks like a missing role assignment.
    customSubDomainName: 'aoai-${namePrefix}-${resourceToken}'

    // The line that makes the no-secret property real rather than aspirational. With local
    // auth disabled the account still has keys in the portal, but nothing will accept them,
    // so a leaked one is inert. The same treatment Cosmos already gets.
    disableLocalAuth: true

    publicNetworkAccess: 'Enabled'
  }
}

// ---------------------------------------------------------------------------
// Deployments.
//
// Three, because the jobs have different shapes. Extraction and assessment are high-volume,
// structured and cheap per item, so they run on the smallest chat model that can do the work.
// Writing a CV happens once, for one person, and is the thing they are judged on, so it runs
// on the best model available. The price ratio between those two runs one way and the call ratio
// runs the other. Embedding is neither: it answers with a vector rather than a completion, so it
// needs a deployment of its own whatever the quota looks like.
//
// Deployed sequentially: the control plane rejects concurrent writes to deployments under one
// account with a conflict, which surfaces as an intermittently red pipeline rather than a
// clear error.
// ---------------------------------------------------------------------------

// The deployment is named for the job it does, never for the model behind it. Naming it after
// the model looked tidier and was wrong twice over: pointing both deployments at one model -
// which is exactly what a subscription without frontier quota has to do - produced two
// resources with the same name and failed validation outright; and changing model would have
// renamed the resource, replacing it and churning the application configuration with it.
// A role name is stable, unique by construction, and is what AzureOpenAiOptions already says
// these are.
resource bulk 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: 'bulk'
  sku: {
    // Global Standard, not Global Batch. The batch matrix does not yet carry the GPT-5.6
    // family - it tops out at gpt-5.4 - so the 24-hour batch discount is not available for
    // this model. Batching in this system therefore means many documents per request rather
    // than the Batch API, which is where the saving actually is anyway: the concept vocabulary
    // is what dominates each call, and packing amortises it.
    name: 'GlobalStandard'
    capacity: bulkCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: bulkModelName
      version: bulkModelVersion
    }
    // Fail a request outright rather than degrading it silently when the quota is exhausted.
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

resource writing 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: 'writing'
  sku: {
    name: 'GlobalStandard'
    capacity: writingCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: writingModelName
      version: writingModelVersion
    }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
  // Sequential, deliberately. Two deployments created in parallel under one account conflict.
  dependsOn: [
    bulk
  ]
}

// The third deployment, and the one that is not a chat model at all. It returns a vector rather
// than a completion, so it cannot share a deployment with either of the others however the quota
// falls - and it is what MatchRanker orders the matches page with.
//
// text-embedding-3-small at 512 dimensions rather than its full 1,536. Matryoshka representation
// learning is what makes the truncation a real embedding rather than a lossy prefix, and the
// width is requested per call by the application rather than fixed here.
resource embeddings 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: 'embeddings'
  sku: {
    name: 'GlobalStandard'
    capacity: embeddingCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: embeddingModelName
      version: embeddingModelVersion
    }
    // A new model version is a new vector space, not a better answer in the old one: every
    // stored vector would silently stop being comparable with every new one. Pinned harder than
    // the chat deployments for that reason, and moving it means bumping
    // EmbeddingVector.EmbeddingVersion so the corpus is re-embedded rather than mixed.
    versionUpgradeOption: 'NoAutoUpgrade'
  }
  // Sequential, as above. Two deployments created in parallel under one account conflict.
  dependsOn: [
    writing
  ]
}

// ---------------------------------------------------------------------------
// Access. Data-plane RBAC, exactly as Cosmos does it.
// ---------------------------------------------------------------------------

@description('Built-in Cognitive Services OpenAI User: call deployments, nothing else. Cannot create or delete one.')
var openAiUser = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource callerAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: account
  name: guid(account.id, callerPrincipalId, openAiUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUser)
    principalId: callerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// So a developer running the API locally under `az login` can call the same deployments. The
// same reason the administrator is a Cosmos data reader and the SQL Entra admin: without it,
// local development against the real provider is impossible and the only way to exercise a
// prompt is to deploy.
resource administratorAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(administratorObjectId)) {
  scope: account
  name: guid(account.id, administratorObjectId, openAiUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUser)
    principalId: administratorObjectId
    // Deliberately 'User'. Declaring a human as ServicePrincipal makes the assignment fail
    // with a principal-not-found error that reads as though the object id were wrong.
    principalType: 'User'
  }
}

output accountName string = account.name

@description('What the application configures as Ai:AzureOpenAi:Endpoint. Not a secret - there is no key behind it.')
output endpoint string = account.properties.endpoint

@description('Deployment name for the high-volume pass. A role, not a model id - see the resource.')
output bulkDeployment string = bulk.name

@description('Deployment name for the writing pass.')
output writingDeployment string = writing.name

@description('Deployment name for the embedding pass.')
output embeddingDeployment string = embeddings.name
