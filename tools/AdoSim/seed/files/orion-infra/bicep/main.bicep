targetScope = 'resourceGroup'

@allowed([
  'dev'
  'staging'
  'prod'
])
param environmentName string
param location string = resourceGroup().location

resource plan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: 'asp-orion-${environmentName}'
  location: location
  sku: {
    name: environmentName == 'prod' ? 'P1v3' : 'B1'
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}
