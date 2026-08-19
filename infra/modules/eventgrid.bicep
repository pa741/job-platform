@description('Azure region.')
param location string

@description('Resource name prefix.')
param namePrefix string

param landingStorageAccountName string
param landingContainerName string
param deadLetterContainerName string

@description('Name of the function app hosting the ingest function.')
param functionAppName string

@description('Name of the function to deliver events to.')
param functionName string

param tags object

resource landingStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: landingStorageAccountName
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' existing = {
  name: functionAppName
}

// Flex Consumption supports only the event-based blob trigger, so this system topic is
// required rather than an optimisation. It also means the function fires on upload
// instead of waiting for a container scan.
resource systemTopic 'Microsoft.EventGrid/systemTopics@2024-06-01-preview' = {
  name: 'egst-${namePrefix}-landing'
  location: location
  tags: tags
  properties: {
    source: landingStorage.id
    topicType: 'Microsoft.Storage.StorageAccounts'
  }
}

// The destination is a WebHook, not an 'AzureFunction' endpoint. That endpoint type only
// accepts functions with an EventGridTrigger; a blob trigger using
// Source = BlobTriggerSource.EventGrid is fed through the host's blob extension webhook
// instead. Getting this wrong fails at deploy time with "unsupported Azure function
// triggers".
//
// The key is read at deploy time via listKeys and never stored in the repository. It only
// exists once the function code has been deployed, which is why this module runs last -
// on a first deploy from empty, expect to deploy code and re-run.
resource blobCreatedSubscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2024-06-01-preview' = {
  parent: systemTopic
  name: 'sub-${namePrefix}-job-digest'
  properties: {
    destination: {
      endpointType: 'WebHook'
      properties: {
        endpointUrl: 'https://${functionApp.properties.defaultHostName}/runtime/webhooks/blobs?functionName=Host.Functions.${functionName}&code=${listKeys('${functionApp.id}/host/default', '2023-12-01').systemKeys.blobs_extension}'
        maxEventsPerBatch: 1
        preferredBatchSizeInKilobytes: 64
      }
    }
    filter: {
      includedEventTypes: [
        'Microsoft.Storage.BlobCreated'
      ]
      // Only the scraper's CSVs in the landing container. Without this, every blob written
      // anywhere on the account - including the dead-letter container - would trigger a run.
      subjectBeginsWith: '/blobServices/default/containers/${landingContainerName}/blobs/jobs/'
      subjectEndsWith: '.csv'
      enableAdvancedFilteringOnArrays: false
    }
    eventDeliverySchema: 'EventGridSchema'
    retryPolicy: {
      maxDeliveryAttempts: 10
      eventTimeToLiveInMinutes: 1440
    }
    // Undeliverable events are kept rather than dropped, so a broken deploy is
    // recoverable instead of silently losing a day of scraping.
    deadLetterDestination: {
      endpointType: 'StorageBlob'
      properties: {
        resourceId: landingStorage.id
        blobContainerName: deadLetterContainerName
      }
    }
  }
}

output systemTopicName string = systemTopic.name
output subscriptionName string = blobCreatedSubscription.name
