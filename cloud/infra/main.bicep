// Margo Cloud (hosted Margo, the autopilot), self-hosted for Agent 365: an Azure AI Services account (chat model and Voice Live),
// registry, monitoring, state storage, and one container per hired Margo
// (docs/autopilot-instance-containers.md): a Container Apps environment, the public router that is the
// blueprint's messaging endpoint, the provisioner job, its queue and route table.
// The agent blueprint is created with the Agent 365 CLI, not here (docs/autopilot-deployment.md).
// Role assignments are made by name in scripts/postprovision.sh, so no role-definition IDs live here: the script
// looks the four instance roles up by name and records them in the azd environment for the provisioner job.
targetScope = 'resourceGroup'

@minLength(1)
@maxLength(40)
@description('azd environment name; used to derive resource names.')
param environmentName string

@minLength(1)
@description('Region. Must offer the chat model (and, for voice, Voice Live).')
param location string

@description('Chat model to deploy for Margo.')
param modelName string = 'gpt-5-mini'

@description('Model version.')
param modelVersion string = '2025-08-07'

@minValue(1)
@description('Model deployment capacity (thousands of tokens per minute). A quota, not a cost: usage is billed per token.')
param modelCapacity int = 100

@allowed(['GlobalStandard', 'DataZoneStandard', 'Standard'])
@description('Model deployment type: GlobalStandard (any region), DataZoneStandard (stays in the data zone) or Standard (stays in the region).')
param modelSku string = 'GlobalStandard'

param tags object = {}

@allowed(['obo', 'delegation'])
@description('How each Margo reads her manager\'s work: obo (on-behalf-of) or delegation (mailbox permissions).')
param principalAccess string = 'delegation'

@description('On-behalf-of user-authorization handler name (obo mode), e.g. PRINCIPAL; empty leaves principal sources blocked.')
param principalAuthHandler string = ''

@description('obo mode: the OAuth connection on the agent\'s Azure Bot that signs the manager in (Teams single sign-on); the handler exchanges that sign-in for a Work IQ token through the blueprint connection.')
param principalOAuthConnection string = ''

@description('The managers\' time zone (IANA name); Margo states every time in it.')
param timezone string = 'America/Los_Angeles'

@description('The agent blueprint client ID (from the Agent 365 CLI) that activities and voice tokens are issued for. Empty on the first provision.')
param blueprintClientId string = ''

@allowed([0, 1])
@description('0 lets an idle instance scale to zero (cold starts); 1 keeps it warm.')
param instanceMinReplicas int = 0

@description('Voice Live model for spoken conversations (for example gpt-realtime-2.1).')
param voiceLiveModel string = ''

@description('Azure voice name (an HD voice, or the custom voice name once trained).')
param voiceName string = ''

@description('Deployment ID of a trained custom voice; empty uses voiceName as a standard voice.')
param voiceEndpointId string = ''

@description('App ID of the single-page app the voice page signs in with (agent blueprints cannot hold its redirect URI); empty signs in as the blueprint.')
param voiceClientId string = ''

@description('Longest spoken conversation in seconds (MARGO_VOICE_MAX_SECONDS); empty keeps the default.')
param voiceMaxSeconds string = ''

@description('Tool calls allowed in one spoken conversation (MARGO_VOICE_MAX_TOOL_CALLS); empty keeps the default.')
param voiceMaxToolCalls string = ''

@minValue(1)
@description('The most instances (hired Margos) that may exist at once; the provisioner refuses to deploy more.')
param maxInstances int = 50

@minValue(1)
@description('Days a replaced version of a state blob is kept (every turn writes a new version of state.zip).')
param stateVersionRetentionDays int = 30

@minValue(30)
@maxValue(730)
@description('Days the Log Analytics workspace keeps logs.')
param logRetentionDays int = 30

@description('Daily ingestion cap of the Log Analytics workspace in GB (-1: no cap). Logs stop for the day when it is reached.')
param logDailyQuotaGb int = 1

@description('Role definition GUIDs an instance is granted, looked up by name and recorded by scripts/postprovision.sh. Empty on the first provision.')
param storageRoleDefinitionId string = ''
param pullRoleDefinitionId string = ''
param modelRoleDefinitionId string = ''
param speechRoleDefinitionId string = ''

@description('Token issuers the router and instances accept besides Bot Framework and this tenant\'s Entra issuers, separated by spaces. Leave empty unless the router logs "JWT issuer not accepted" for genuine Agent 365 traffic.')
param extraTokenIssuers string = ''

@description('Application (client) IDs the router and instances accept platform tokens from, separated by spaces. Empty accepts any application\'s own token for the blueprint, never a signed-in person\'s. Set it once the router has logged "Platform token accepted from application ...".')
param platformAppIds string = ''

@description('Hosts instances may send replies to besides Microsoft\'s own, separated by spaces. Leave empty unless an instance logs "ServiceUrl host is not in the configured allowed hosts" for genuine Agent 365 traffic.')
param extraServiceHosts string = ''

@description('Reasoning effort asked of the chat model: none, minimal, low, medium or high. Use none for a deployment that is not a reasoning model.')
@allowed([ 'none', 'minimal', 'low', 'medium', 'high' ])
param modelReasoningEffort string = 'low'

@description('The tenant\'s own SharePoint and OneDrive hosts (for example contoso.sharepoint.com contoso-my.sharepoint.com), separated by spaces: the only hosts a file link given to Copilot search may name. Empty accepts any one tenant under sharepoint.com.')
param sharePointHosts string = ''

@description('Days each manager\'s daily state copies are kept; 0 keeps them all.')
@minValue(0)
param stateDailyRetentionDays int = 30

@description('The Margo Cloud image pinned by digest, recorded by scripts/postprovision.sh (MARGO_PINNED_IMAGE). Empty on the first provision.')
param margoImage string = ''

// The public placeholder has no dotnet and listens on port 80, so it never gets the router's command or port: the
// router runs on it until an image is pinned and the blueprint exists, then provisioning keeps the router on Margo.
// The provisioner job follows the router: on the placeholder nothing queues work for it, so it never starts.
var placeholderImage = 'mcr.microsoft.com/k8se/quickstart:latest'
var routerOnMargo = !empty(margoImage) && !empty(blueprintClientId)

var token = toLower(uniqueString(subscription().id, resourceGroup().id, environmentName))
var compact = toLower(replace(replace(environmentName, '-', ''), '_', ''))
var accountName = take('${compact}${token}', 24)
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
    disableLocalAuth: true
  }
}

// The account takes one operation at a time (RequestConflict otherwise), so deploy the model last.
resource model 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: modelName
  sku: { name: modelSku, capacity: modelCapacity }
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
  properties: {
    retentionInDays: logRetentionDays
    sku: { name: 'PerGB2018' }
    workspaceCapping: { dailyQuotaGb: logDailyQuotaGb }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${environmentName}-appi'
  location: location
  tags: tags
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id }
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

// Every turn writes a new version of an instance's state.zip, and every route change one of routes.json. Replaced
// versions are deleted after stateVersionRetentionDays; the current version of a blob is never touched. The daily
// copies ({manager}/daily/YYYY-MM-DD.zip) are current blobs under a per-manager folder, which a prefix filter cannot
// select without also selecting state.zip, so they are not expired here.
resource stateLifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: storage
  name: 'default'
  dependsOn: [ blobService ]
  properties: {
    policy: {
      rules: [
        {
          name: 'delete-replaced-versions'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: [ 'blockBlob' ] }
            actions: { version: { delete: { daysAfterCreationGreaterThan: stateVersionRetentionDays } } }
          }
        }
      ]
    }
  }
}

// -- one container per instance --------------------------------------------------------------------
// Apps start from a public placeholder image; scripts/postprovision.sh assigns roles by name, builds
// and pins the Margo image and, once the blueprint exists, switches the router (from the next provision on, through
// margoImage) and the provisioner to it.

resource queues 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource provisionQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queues
  name: 'margo-provision'
}

resource routesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'margo-routes'
  properties: { publicAccess: 'None' }
}

resource routerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${environmentName}-router'
  location: location
  tags: tags
}

resource provisionerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${environmentName}-provisioner'
  location: location
  tags: tags
}

// Trusted by the blueprint's federated credential so instances can act as the agent; holds no Azure roles.
resource blueprintCredential 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${environmentName}-blueprint-credential'
  location: location
  tags: tags
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
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

resource router 'Microsoft.App/containerApps@2024-03-01' = {
  name: take('${compact}-router', 32)
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${routerIdentity.id}': {} } }
  properties: {
    managedEnvironmentId: containerEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: routerOnMargo ? 8088 : 80, transport: 'auto', allowInsecure: false }
      registries: routerOnMargo ? [ { server: registry.properties.loginServer, identity: routerIdentity.id } ] : []
    }
    template: {
      containers: [
        union({
          name: 'router'
          image: routerOnMargo ? margoImage : placeholderImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'AZURE_CLIENT_ID', value: routerIdentity.properties.clientId }
            { name: 'MARGO_ROUTES_URL', value: '${storage.properties.primaryEndpoints.blob}margo-routes/routes.json' }
            { name: 'MARGO_PROVISION_QUEUE_URL', value: '${storage.properties.primaryEndpoints.queue}margo-provision' }
            { name: 'CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID', value: blueprintClientId }
            { name: 'CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID', value: tenant().tenantId }
            { name: 'MARGO_EXTRA_TOKEN_ISSUERS', value: extraTokenIssuers }
            { name: 'MARGO_PLATFORM_APP_IDS', value: platformAppIds }
          ]
        }, routerOnMargo ? { command: [ 'dotnet', '/app/router/Margo.Cloud.Router.dll' ] } : {})
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

var provisionerConfig = {
  environmentId: containerEnvironment.id
  environmentDomain: containerEnvironment.properties.defaultDomain
  storageAccountName: storage.name
  registryServer: registry.properties.loginServer
  registryId: registry.id
  aiAccountName: account.name
  modelDeployment: model.name
  appInsightsConnectionString: insights.properties.ConnectionString
  publicBaseUrl: 'https://${router.properties.configuration.ingress.fqdn}'
  tenantId: tenant().tenantId
  blueprintClientId: blueprintClientId
  blueprintCredentialIdentityId: blueprintCredential.id
  blueprintCredentialClientId: blueprintCredential.properties.clientId
  // The image instances run and the roles each one is granted; scripts/postprovision.sh moves the job to a newly
  // built image at once (MARGO_INSTANCE_IMAGE), and the next provision keeps it here.
  image: margoImage
  storageRoleDefinitionId: storageRoleDefinitionId
  pullRoleDefinitionId: pullRoleDefinitionId
  modelRoleDefinitionId: modelRoleDefinitionId
  speechRoleDefinitionId: speechRoleDefinitionId
  minReplicas: instanceMinReplicas
  voiceLiveModel: voiceLiveModel
  voiceName: voiceName
  voiceEndpointId: voiceEndpointId
  // Settings every instance container gets; never secrets.
  // obo mode: the manager's sign-in handler (Azure Bot token service, single sign-on in Teams), exchanged on behalf of
  // the manager for a Work IQ token through the blueprint's own connection.
  extraEnv: concat([
    { name: 'MARGO_PRINCIPAL_ACCESS', value: principalAccess }
    { name: 'MARGO_TIMEZONE', value: timezone }
  ], empty(principalAuthHandler) ? [] : [ { name: 'PRINCIPAL_AUTH_HANDLER', value: principalAuthHandler } ], empty(principalAuthHandler) || empty(principalOAuthConnection) ? [] : [ { name: 'AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__${toUpper(principalAuthHandler)}__TYPE', value: 'AzureBotUserAuthorization' }, { name: 'AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__${toUpper(principalAuthHandler)}__SETTINGS__AZUREBOTOAUTHCONNECTIONNAME', value: principalOAuthConnection }, { name: 'AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__${toUpper(principalAuthHandler)}__SETTINGS__OBOCONNECTIONNAME', value: 'SERVICE_CONNECTION' }, { name: 'AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__${toUpper(principalAuthHandler)}__SETTINGS__OBOSCOPES__0', value: 'api://workiq.svc.cloud.microsoft/.default' }, { name: 'AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__${toUpper(principalAuthHandler)}__SETTINGS__ENABLESSO', value: 'true' } ], empty(voiceClientId) ? [] : [ { name: 'MARGO_VOICE_CLIENT_ID', value: voiceClientId } ], empty(extraTokenIssuers) ? [] : [ { name: 'MARGO_EXTRA_TOKEN_ISSUERS', value: extraTokenIssuers } ], empty(voiceMaxSeconds) ? [] : [ { name: 'MARGO_VOICE_MAX_SECONDS', value: voiceMaxSeconds } ], empty(voiceMaxToolCalls) ? [] : [ { name: 'MARGO_VOICE_MAX_TOOL_CALLS', value: voiceMaxToolCalls } ], empty(platformAppIds) ? [] : [ { name: 'MARGO_PLATFORM_APP_IDS', value: platformAppIds } ], empty(extraServiceHosts) ? [] : [ { name: 'MARGO_EXTRA_SERVICE_HOSTS', value: extraServiceHosts } ], empty(sharePointHosts) ? [] : [ { name: 'MARGO_SHAREPOINT_HOSTS', value: sharePointHosts } ], [ { name: 'MARGO_MODEL_REASONING_EFFORT', value: modelReasoningEffort }, { name: 'MARGO_STATE_DAILY_RETENTION_DAYS', value: string(stateDailyRetentionDays) } ])
}

// Started by messages on the provisioning queue (a KEDA azure-queue rule read with the job's own identity, which
// scripts/postprovision.sh lets read the queue), one execution at a time; nothing runs while the queue is empty.
// A job's trigger type cannot be relied on to change in place: scripts/preprovision.sh removes a job that an earlier
// version created with a schedule, and this recreates it. Like the router, the job runs the Margo image, with its
// command and registry, once an image is pinned and the blueprint exists; until then nothing queues work for it.
resource provisioner 'Microsoft.App/jobs@2025-01-01' = {
  name: take('${compact}-provisioner', 32)
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${provisionerIdentity.id}': {} } }
  properties: {
    environmentId: containerEnvironment.id
    configuration: {
      triggerType: 'Event'
      eventTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
        scale: {
          minExecutions: 0
          maxExecutions: 1
          pollingInterval: 30
          rules: [
            {
              name: 'provision-queue'
              type: 'azure-queue'
              metadata: {
                accountName: storage.name
                queueName: provisionQueue.name
                queueLength: '1'
                // A message being retried is hidden for 15 minutes; it must not start an execution every poll.
                queueLengthStrategy: 'visibleonly'
              }
              identity: provisionerIdentity.id
            }
          ]
        }
      }
      replicaTimeout: 1800
      replicaRetryLimit: 0
      registries: routerOnMargo ? [ { server: registry.properties.loginServer, identity: provisionerIdentity.id } ] : []
    }
    template: {
      containers: [
        union({
          name: 'provisioner'
          image: routerOnMargo ? margoImage : placeholderImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'AZURE_CLIENT_ID', value: provisionerIdentity.properties.clientId }
            { name: 'AZURE_SUBSCRIPTION_ID', value: subscription().subscriptionId }
            { name: 'AZURE_RESOURCE_GROUP', value: resourceGroup().name }
            { name: 'MARGO_ROUTES_URL', value: '${storage.properties.primaryEndpoints.blob}margo-routes/routes.json' }
            { name: 'MARGO_PROVISION_QUEUE_URL', value: '${storage.properties.primaryEndpoints.queue}margo-provision' }
            { name: 'MARGO_PROVISIONER_CONFIG', value: string(provisionerConfig) }
            { name: 'MARGO_MAX_INSTANCES', value: string(maxInstances) }
          ]
        }, routerOnMargo ? { command: [ 'dotnet', '/app/provisioner/Margo.Cloud.Provisioner.dll', 'run' ] } : {})
      ]
    }
  }
}

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = registry.properties.loginServer
output AZURE_CONTAINER_REGISTRY_ID string = registry.id
output ACCOUNT_ID string = account.id
output ACCOUNT_NAME string = account.name
output MODEL_NAME string = model.name
output APPLICATIONINSIGHTS_ID string = insights.id
output STORAGE_ACCOUNT_ID string = storage.id
output MARGO_STATE_BLOB_URL string = storage.properties.primaryEndpoints.blob
output TENANT_ID string = tenant().tenantId
output SUBSCRIPTION_ID string = subscription().subscriptionId
output RESOURCE_GROUP string = resourceGroup().name
output LOCATION string = location
output ROUTER_NAME string = router.name
output ROUTER_URL string = 'https://${router.properties.configuration.ingress.fqdn}'
output MESSAGING_ENDPOINT string = 'https://${router.properties.configuration.ingress.fqdn}/api/messages'
output ROUTER_PRINCIPAL_ID string = routerIdentity.properties.principalId
output ROUTER_IDENTITY_ID string = routerIdentity.id
output PROVISIONER_NAME string = provisioner.name
output PROVISIONER_PRINCIPAL_ID string = provisionerIdentity.properties.principalId
output PROVISIONER_IDENTITY_ID string = provisionerIdentity.id
output BLUEPRINT_CREDENTIAL_CLIENT_ID string = blueprintCredential.properties.clientId
output BLUEPRINT_CREDENTIAL_PRINCIPAL_ID string = blueprintCredential.properties.principalId
output PROVISION_QUEUE_URL string = '${storage.properties.primaryEndpoints.queue}margo-provision'
output ROUTES_CONTAINER_ID string = routesContainer.id
output PROVISION_QUEUE_ID string = provisionQueue.id
