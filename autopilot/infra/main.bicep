// Margo autopilot: Foundry account and project, model, registry, monitoring and state storage.
// Role assignments are made by name in scripts/postprovision.sh, so no role-definition IDs live here.
// hostingMode 'instance' adds one-container-per-instance hosting (docs/autopilot-instance-containers.md):
// a Container Apps environment, the public router, the provisioner job, its queue and route table.
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

@allowed(['foundry', 'instance'])
@description('foundry: one Foundry hosted agent serves every instance. instance: one container per hired instance.')
param hostingMode string = 'foundry'

@description('Instance mode: the agent blueprint client ID that activities and voice tokens are issued for.')
param blueprintClientId string = ''

@allowed([0, 1])
@description('Instance mode: 0 lets an idle instance scale to zero (cold starts); 1 keeps it warm.')
param instanceMinReplicas int = 0

@description('Instance mode: Voice Live model for spoken conversations (for example gpt-realtime-2.1).')
param voiceLiveModel string = ''

@description('Instance mode: Azure voice name (an HD voice, or the custom voice name once trained).')
param voiceName string = ''

@description('Instance mode: deployment ID of a trained custom voice; empty uses voiceName as a standard voice.')
param voiceEndpointId string = ''

var instanceMode = hostingMode == 'instance'
var placeholderImage = 'mcr.microsoft.com/k8se/quickstart:latest'

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

// The account takes one operation at a time (RequestConflict otherwise), so deploy the model last.
resource model 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: modelName
  dependsOn: [insightsConnection]
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

// -- one container per instance (hostingMode 'instance') ----------------------------------------
// Apps start from a public placeholder image; scripts/postprovision-instance.sh assigns roles by
// name, builds the Margo image and switches the router and provisioner to it.

resource queues 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = if (instanceMode) {
  parent: storage
  name: 'default'
}

resource provisionQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = if (instanceMode) {
  parent: queues
  name: 'margo-provision'
}

resource routesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (instanceMode) {
  parent: blobService
  name: 'margo-routes'
  properties: { publicAccess: 'None' }
}

resource routerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (instanceMode) {
  name: '${environmentName}-router'
  location: location
  tags: tags
}

resource provisionerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (instanceMode) {
  name: '${environmentName}-provisioner'
  location: location
  tags: tags
}

// Trusted by the blueprint's federated credential so instances can act as the agent; holds no Azure roles.
resource blueprintCredential 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (instanceMode) {
  name: '${environmentName}-blueprint-credential'
  location: location
  tags: tags
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = if (instanceMode) {
  name: '${environmentName}-env'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource router 'Microsoft.App/containerApps@2024-03-01' = if (instanceMode) {
  name: take('${compact}-router', 32)
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${routerIdentity!.id}': {} } }
  properties: {
    managedEnvironmentId: containerEnvironment!.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8088, transport: 'auto', allowInsecure: false }
    }
    template: {
      containers: [
        {
          name: 'router'
          image: placeholderImage
          command: [ 'python', '-m', 'margo_autopilot.router' ]
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'AZURE_CLIENT_ID', value: routerIdentity!.properties.clientId }
            { name: 'MARGO_ROUTES_URL', value: '${storage.properties.primaryEndpoints.blob}margo-routes/routes.json' }
            { name: 'MARGO_PROVISION_QUEUE_URL', value: '${storage.properties.primaryEndpoints.queue}margo-provision' }
            { name: 'CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID', value: blueprintClientId }
            { name: 'CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID', value: tenant().tenantId }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

var provisionerConfig = {
  environmentId: instanceMode ? containerEnvironment!.id : ''
  environmentDomain: instanceMode ? containerEnvironment!.properties.defaultDomain : ''
  storageAccountName: storage.name
  registryServer: registry.properties.loginServer
  registryId: registry.id
  foundryAccountName: account.name
  foundryProjectName: project.name
  foundryProjectEndpoint: project.properties.endpoints['AI Foundry API']
  modelDeployment: model.name
  publicBaseUrl: instanceMode ? 'https://${router!.properties.configuration.ingress.fqdn}' : ''
  tenantId: tenant().tenantId
  blueprintClientId: blueprintClientId
  blueprintCredentialIdentityId: instanceMode ? blueprintCredential!.id : ''
  blueprintCredentialClientId: instanceMode ? blueprintCredential!.properties.clientId : ''
  minReplicas: instanceMinReplicas
  voiceLiveModel: voiceLiveModel
  voiceName: voiceName
  voiceEndpointId: voiceEndpointId
}

resource provisioner 'Microsoft.App/jobs@2024-03-01' = if (instanceMode) {
  name: take('${compact}-provisioner', 32)
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${provisionerIdentity!.id}': {} } }
  properties: {
    environmentId: containerEnvironment!.id
    configuration: {
      triggerType: 'Schedule'
      scheduleTriggerConfig: { cronExpression: '*/2 * * * *', parallelism: 1, replicaCompletionCount: 1 }
      replicaTimeout: 1800
      replicaRetryLimit: 0
    }
    template: {
      containers: [
        {
          name: 'provisioner'
          image: placeholderImage
          command: [ 'python', '-m', 'margo_autopilot.provision', 'run' ]
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'AZURE_CLIENT_ID', value: provisionerIdentity!.properties.clientId }
            { name: 'AZURE_SUBSCRIPTION_ID', value: subscription().subscriptionId }
            { name: 'AZURE_RESOURCE_GROUP', value: resourceGroup().name }
            { name: 'MARGO_ROUTES_URL', value: '${storage.properties.primaryEndpoints.blob}margo-routes/routes.json' }
            { name: 'MARGO_PROVISION_QUEUE_URL', value: '${storage.properties.primaryEndpoints.queue}margo-provision' }
            { name: 'MARGO_PROVISIONER_CONFIG', value: string(provisionerConfig) }
          ]
        }
      ]
    }
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
output MARGO_HOSTING string = hostingMode
output ROUTER_NAME string = instanceMode ? router!.name : ''
output ROUTER_URL string = instanceMode ? 'https://${router!.properties.configuration.ingress.fqdn}' : ''
output ROUTER_PRINCIPAL_ID string = instanceMode ? routerIdentity!.properties.principalId : ''
output ROUTER_IDENTITY_ID string = instanceMode ? routerIdentity!.id : ''
output PROVISIONER_NAME string = instanceMode ? provisioner!.name : ''
output PROVISIONER_PRINCIPAL_ID string = instanceMode ? provisionerIdentity!.properties.principalId : ''
output PROVISIONER_IDENTITY_ID string = instanceMode ? provisionerIdentity!.id : ''
output BLUEPRINT_CREDENTIAL_CLIENT_ID string = instanceMode ? blueprintCredential!.properties.clientId : ''
output BLUEPRINT_CREDENTIAL_PRINCIPAL_ID string = instanceMode ? blueprintCredential!.properties.principalId : ''
output PROVISION_QUEUE_URL string = instanceMode ? '${storage.properties.primaryEndpoints.queue}margo-provision' : ''
output ROUTES_CONTAINER_ID string = instanceMode ? routesContainer!.id : ''
output PROVISION_QUEUE_ID string = instanceMode ? provisionQueue!.id : ''
