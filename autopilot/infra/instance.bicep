// One hired Margo: her own identity, her own state container (scoped role), and one Container App
// with internal ingress and at most one replica. Deployed by the provisioner as instance.json.
// Regenerate after any change: bicep build autopilot/infra/instance.bicep --outfile autopilot/infra/instance.json
// Never deletes storage; retirement removes the app and identity only.

@minLength(20)
@maxLength(20)
param instanceKey string
param agentIdentityId string
param agentUserId string
@maxLength(32)
param appName string
param identityName string
@maxLength(63)
param stateContainerName string
param location string = resourceGroup().location
param environmentId string
@description('Pinned image reference, ideally by digest')
param image string
@allowed([0, 1])
param minReplicas int = 0
param storageAccountName string
param registryServer string
param registryId string
param foundryAccountName string
param foundryProjectName string
param foundryProjectEndpoint string
param modelDeployment string
// Role definition GUIDs are looked up by name at deployment time (postprovision-instance.sh), so
// no identifier lives in this file.
@description('Storage Blob Data Contributor role definition GUID')
param storageRoleDefinitionId string
@description('AcrPull role definition GUID')
param pullRoleDefinitionId string
@description('Foundry User / Azure AI User role definition GUID')
param modelRoleDefinitionId string
@description('Cognitive Services User role definition GUID, for Voice Live')
param speechRoleDefinitionId string
@description('The router public base URL; the voice page is {publicBaseUrl}/i/{instanceKey}/voice/')
param publicBaseUrl string
param tenantId string
param blueprintClientId string
@description('Shared identity trusted by the blueprint federated credential; it holds no Azure roles')
param blueprintCredentialIdentityId string
param blueprintCredentialClientId string
param voiceLiveModel string = ''
param voiceName string = ''
param voiceEndpointId string = ''
@description('Additional {name, value} environment entries; never secrets')
param extraEnv array = []

var storageBlobDataContributor = storageRoleDefinitionId
var acrPull = pullRoleDefinitionId

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: { 'margo-instance': instanceKey }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
  resource blobs 'blobServices' existing = {
    name: 'default'
  }
}

resource stateContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: storage::blobs
  name: stateContainerName
  properties: { publicAccess: 'None', metadata: { margoinstance: instanceKey } }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: last(split(registryId, '/'))
}

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName
  resource project 'projects' existing = {
    name: foundryProjectName
  }
}

// This instance's own state container only: no other manager's state is reachable.
resource stateRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(stateContainer.id, identity.id, storageBlobDataContributor)
  scope: stateContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource pullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, acrPull)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPull)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource modelRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account::project.id, identity.id, modelRoleDefinitionId)
  scope: account::project
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', modelRoleDefinitionId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource speechRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, identity.id, speechRoleDefinitionId)
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', speechRoleDefinitionId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

var baseEnv = [
  { name: 'MARGO_HOSTING', value: 'instance' }
  { name: 'MARGO_INSTANCE_KEY', value: instanceKey }
  { name: 'MARGO_INSTANCE_AGENT_ID', value: agentIdentityId }
  { name: 'MARGO_INSTANCE_USER_ID', value: agentUserId }
  { name: 'MARGO_STATE_BLOB_URL', value: storage.properties.primaryEndpoints.blob }
  { name: 'MARGO_STATE_CONTAINER', value: stateContainerName }
  { name: 'MARGO_PUBLIC_BASE_URL', value: publicBaseUrl }
  { name: 'MARGO_TENANT_ID', value: tenantId }
  { name: 'FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'FOUNDRY_PROJECT_ENDPOINT', value: foundryProjectEndpoint }
  { name: 'ModelDeployment', value: modelDeployment }
  { name: 'VOICE_LIVE_ENDPOINT', value: 'https://${foundryAccountName}.services.ai.azure.com' }
  { name: 'VOICE_LIVE_MODEL', value: voiceLiveModel }
  { name: 'MARGO_VOICE_NAME', value: voiceName }
  { name: 'MARGO_VOICE_ENDPOINT_ID', value: voiceEndpointId }
  { name: 'CONNECTIONS__SERVICE_CONNECTION__SETTINGS__AUTHTYPE', value: 'FederatedCredentials' }
  { name: 'CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID', value: blueprintClientId }
  { name: 'CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID', value: tenantId }
  { name: 'CONNECTIONS__SERVICE_CONNECTION__SETTINGS__FEDERATEDCLIENTID', value: blueprintCredentialClientId }
]

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  tags: { 'margo-instance': instanceKey }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
      '${blueprintCredentialIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: false, targetPort: 8088, transport: 'auto', allowInsecure: false }
      registries: [ { server: registryServer, identity: identity.id } ]
    }
    template: {
      containers: [
        {
          name: 'margo'
          image: image
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(baseEnv, extraEnv)
          probes: [
            { type: 'Liveness', httpGet: { path: '/liveness', port: 8088 }, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/readiness', port: 8088 }, periodSeconds: 10 }
          ]
        }
      ]
      scale: { minReplicas: minReplicas, maxReplicas: 1 }
    }
  }
  dependsOn: [ stateRole, pullRole ]
}

output appName string = app.name
output identityPrincipalId string = identity.properties.principalId
