@description('Azure region.')
param location string

@description('Resource name prefix.')
param namePrefix string

param tags object

@description('Daily ingestion cap in GB. Keeps observability from quietly becoming the largest line on the bill.')
param dailyQuotaGb int = 1

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${namePrefix}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      dailyQuotaGb: dailyQuotaGb
    }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${namePrefix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

output applicationInsightsName string = applicationInsights.name
output connectionString string = applicationInsights.properties.ConnectionString
output workspaceId string = workspace.id
