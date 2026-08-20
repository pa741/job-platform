@description('Azure region. Static Web Apps is available in a subset of regions; westeurope covers Europe.')
param location string

@description('Resource name prefix.')
param namePrefix string

param tags object

// Free tier. Sufficient because the dashboard is a pure static bundle that talks to the
// Container Apps API directly with a bearer token - it needs no managed functions and no
// linked backend, and a linked backend is what would force the Standard plan (~$9/month).
resource site 'Microsoft.Web/staticSites@2023-12-01' = {
  name: 'stapp-${namePrefix}-web'
  location: location
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    // The build is driven by the GitHub Actions workflow in this repository rather than by
    // the provider's own auto-generated one, so no repository is linked here.
    provider: 'Custom'
    allowConfigFileUpdates: true
    stagingEnvironmentPolicy: 'Enabled'
    enterpriseGradeCdnStatus: 'Disabled'
  }
}

output name string = site.name
output defaultHostname string = site.properties.defaultHostname
output url string = 'https://${site.properties.defaultHostname}'
