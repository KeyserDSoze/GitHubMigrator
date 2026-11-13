param location string = resourceGroup().location

resource workspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: 'log-orion-prod'
  location: location
  properties: {
    retentionInDays: 90
  }
}
