// Margo autopilot: Foundry account and project, model, registry, monitoring and state storage.
// Role assignments are made by name in scripts/postprovision.sh, so no role-definition IDs live here.
targetScope = 'resourceGroup'

@minLength(1)
@maxLength(40)
@description('azd environment name; used to derive resource names.')
param environmentName string

@minLength(1)
@description('Region. Must support Foundry hosted agents.')
param location string

@description('Chat model to deploy for Margo.')
param modelName string = 'gpt-chat-latest'

@description('Model version.')
param modelVersion string = '2026-05-28'

@description('Model deployment capacity (thousands of tokens per minute).')
param modelCapacity int = 30

@description('Hosted agent name in the Foundry project.')
param agentName string = 'margo'

param tags object = {}

var token = toLower(uniqueString(subscription().id, resourceGroup().id, environmentName))
var compact = toLower(replace(replace(environmentName, '-', ''), '_', ''))
var accountName = take('${compact}${token}', 24)
var projectName = 'margo'
var registryName = take('${compact}acr${token}', 50)
var storageName = take('${compact}st${token}', 24)

resource account 'Microsoft.CognitiveServices/accounts@2025-09-01' = {
  name: accountName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: accountName
    publicNetworkAccess: 'Enabled'
    allowProjectManagement: true
    disableLocalAuth: true
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2026-03-01' = {
  parent: account
  name: projectName
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: { displayName: 'Margo' }
}

resource model 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: modelName
  sku: { name: 'GlobalStandard', capacity: modelCapacity }
  properties: { model: { format: 'OpenAI', name: modelName, version: modelVersion } }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false, publicNetworkAccess: 'Enabled' }
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${environmentName}-logs'
  location: location
  tags: tags
  properties: { retentionInDays: 30, sku: { name: 'PerGB2018' } }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${environmentName}-appi'
  location: location
  tags: tags
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id }
}

resource insightsConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-09-01' = {
  parent: project
  name: insights.name
  properties: {
    category: 'AppInsights'
    target: insights.id
    #disable-next-line BCP036
    authType: 'ProjectManagedIdentity'
    isSharedToAll: false
    metadata: {
      ApiType: 'Azure'
      ResourceId: insights.id
      ApplicationInsightsConnectionString: insights.properties.ConnectionString
    }
  }
}

// Margo's private state: one container per hired instance and manager, created at runtime.
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: { enabled: true, days: 14 }
    containerDeleteRetentionPolicy: { enabled: true, days: 14 }
  }
}

output AZURE_AI_PROJECT_ENDPOINT string = project.properties.endpoints['AI Foundry API']
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = registry.properties.loginServer
output AZURE_CONTAINER_REGISTRY_ID string = registry.id
output ACCOUNT_ID string = account.id
output ACCOUNT_NAME string = account.name
output PROJECT_NAME string = project.name
output PROJECT_ID string = project.id
output PROJECT_PRINCIPAL_ID string = project.identity.principalId
output MODEL_NAME string = model.name
output AGENT_NAME string = agentName
output APPLICATIONINSIGHTS_ID string = insights.id
output STORAGE_ACCOUNT_ID string = storage.id
output MARGO_STATE_BLOB_URL string = storage.properties.primaryEndpoints.blob
output TENANT_ID string = tenant().tenantId
output SUBSCRIPTION_ID string = subscription().subscriptionId
output RESOURCE_GROUP string = resourceGroup().name
output LOCATION string = location
