// The Blazor WebAssembly app on Static Web Apps (specification section 13.1). The pipeline uploads the published
// files; nothing is built by the service. The Free tier has no linked backend, so the web app calls the API's
// own address and the API allows this origin.

param env string
param suffix string

@description('Static Web Apps exist in a handful of regions only; the content is served worldwide either way.')
@allowed(['westeurope', 'eastus2', 'centralus', 'westus2', 'eastasia'])
param location string

@allowed(['Free', 'Standard'])
param sku string

resource site 'Microsoft.Web/staticSites@2024-04-01' = {
  name: 'stapp-kh-${env}-${suffix}'
  location: location
  sku: { name: sku, tier: sku }
  properties: {
    allowConfigFileUpdates: true
    stagingEnvironmentPolicy: 'Disabled'
  }
}

output name string = site.name
output origin string = 'https://${site.properties.defaultHostname}'
