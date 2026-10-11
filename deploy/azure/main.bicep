// Margo remote host: one Ubuntu 24.04 LTS VM with a system-assigned managed
// identity and no public IP, Bastion for the manager's Entra SSH login, a Key
// Vault (RBAC, soft delete, purge protection) holding the deployment anchor and
// the Copilot credential, a data disk for /var/lib/margo, an NSG that denies
// every inbound flow, a NAT gateway for egress (or an optional Azure Firewall
// with an FQDN allow-list), Azure Monitor alerts, and the role assignments the
// design needs: the manager may log in to the VM and manage vault secrets; the
// VM identity may only read them.
//
// No GUID literal lives in this repository. Built-in role definition ids come
// from the private parameters file (roleDefinitionIds) so ./tools/check-clean.sh
// never sees a tenant, subscription or role id. Deploy with
// main.local.bicepparam (gitignored); main.example.bicepparam is the template.
//
// Compiled in CI (bicep build). Not deployed by CI: that needs a tenant.

targetScope = 'resourceGroup'

// ------------------------------------------------------------ parameters ----

@description('Azure region for every resource.')
param location string = resourceGroup().location

@description('VM name, also its computer name and the prefix for network resources.')
@minLength(1)
@maxLength(24)
param vmName string = 'vm-margo'

@description('VM size. Two vCPUs and 4 GiB are enough for the gate, the harness and one Copilot session.')
param vmSize string = 'Standard_B2s'

@description('Entra object id of the manager. Gets Virtual Machine Administrator Login on the VM and Key Vault Secrets Officer on the vault; nothing else.')
param adminObjectId string

@description('Principal type of adminObjectId.')
@allowed(['User', 'Group', 'ServicePrincipal'])
param adminPrincipalType string = 'User'

@description('Local break-glass login on the VM, owned by the manager and reachable only through Bastion with the SSH key below. Everyday access is Entra SSH login; list both names in control.cli_logins.')
@minLength(1)
@maxLength(32)
param managerVmLogin string

@description('SSH public key for managerVmLogin. Password login is disabled.')
param managerSshPublicKey string

@description('Key Vault name, globally unique.')
@minLength(3)
@maxLength(24)
param keyVaultName string

@description('Public IPv4 ranges (CIDR) allowed to reach the vault, for the operator who uploads the secrets. Empty keeps the vault open to the public endpoint (RBAC still applies).')
param keyVaultOperatorIpRanges array = []

@description('Virtual network name.')
param vnetName string = '${vmName}-vnet'

@description('Name of the subnet that holds the VM.')
param subnetName string = 'margo'

@description('Bastion host name.')
param bastionName string = '${vmName}-bastion'

param vnetAddressPrefix string = '10.60.0.0/24'
param subnetAddressPrefix string = '10.60.0.0/27'
@description('AzureBastionSubnet needs at least a /26.')
param bastionSubnetAddressPrefix string = '10.60.0.64/26'
@description('AzureFirewallSubnet needs at least a /26. Only used when deployFirewall is true.')
param firewallSubnetAddressPrefix string = '10.60.0.128/26'

@description('Deploy Azure Bastion (Standard SKU with native-client tunnelling). Without it there is no way to reach the VM; keep it unless you bring your own.')
param deployBastion bool = true

@description('Route all VM egress through an Azure Firewall with an FQDN allow-list (modules/firewall.bicep). This is the only way to get a real egress allow-list; NSG service tags cannot name GitHub, npm or nodejs.org.')
param deployFirewall bool = false

@description('With deployFirewall false: add outbound NSG rules that allow only Azure service tags and deny the rest. WARNING: this blocks GitHub, npm and nodejs.org, so provisioning and Copilot itself stop working. It exists for a steady state that an operator has measured; leave it false otherwise.')
param restrictOutboundToServiceTags bool = false

@description('Extra FQDNs the firewall allows (Work IQ endpoints recorded in Phase 0, for example). Only used when deployFirewall is true.')
param firewallExtraFqdns array = []

@description('Built-in role definition ids, supplied privately. Keys: virtualMachineAdministratorLogin, keyVaultSecretsOfficer, keyVaultSecretsUser. Look each one up with `az role definition list --name "<role name>" --query "[].name" -o tsv`.')
param roleDefinitionIds object

@description('Git repository the host clones at first boot.')
param repoUrl string = 'https://github.com/tolgaki/margo.git'

@description('Pinned revision of that repository: a 40-character commit id. Tags work but are not reproducible.')
param repoRevision string

@description('Pinned Node.js version (major 22), installed from the official tarball.')
param nodeVersion string = '22.22.0'

@description('Optional SHA-256 of the Node tarball for this version and architecture, to verify it independently of nodejs.org. Empty means same-origin SHASUMS256.txt only, which bootstrap reports as a warning.')
param nodeSha256 string = ''

@description('Pinned @github/copilot version installed with npm.')
param copilotVersion string

@description('Key Vault secret holding the deployment anchor JSON (deployment.example.json filled in).')
param deploymentSecretName string = 'margo-deployment'

@description('Key Vault secret holding an environment file (KEY=value lines) with the Copilot CLI credential, installed as /etc/margo/copilot.env. Empty skips it.')
param copilotEnvSecretName string = 'margo-copilot-env'

@description('Data disk for /var/lib/margo, in GiB. Platform-managed encryption at rest always applies; see diskEncryptionSetId and encryptionAtHost for stronger options.')
@minValue(4)
param dataDiskSizeGb int = 32

@description('LUN of the data disk; bootstrap.sh formats and mounts /dev/disk/azure/scsi1/lun<N>.')
param dataDiskLun int = 0

@description('Optional disk encryption set (customer-managed keys) applied to both disks.')
param diskEncryptionSetId string = ''

@description('Encrypt data in transit to the disks at the host. Needs the EncryptionAtHost feature registered on the subscription.')
param encryptionAtHost bool = false

@description('Address that receives the health alerts.')
param alertEmail string

@description('Log Analytics retention for syslog and heartbeat, in days.')
@minValue(30)
@maxValue(730)
param logRetentionDays int = 30

@description('Tags applied to every resource.')
param tags object = {}

// ------------------------------------------------------------- variables ----

// cloud-init is rendered by token replacement rather than format(), because a
// YAML document is full of braces. Keep the token list in step with cloud-init.yaml.
var cloudInitTokens = [
  ['__KEY_VAULT_NAME__', keyVaultName]
  ['__VAULT_SUFFIX__', skip(environment().suffixes.keyvaultDns, 1)]
  ['__DEPLOYMENT_SECRET_NAME__', deploymentSecretName]
  ['__COPILOT_ENV_SECRET_NAME__', copilotEnvSecretName]
  ['__REPO_URL__', repoUrl]
  ['__REPO_REVISION__', repoRevision]
  ['__NODE_VERSION__', nodeVersion]
  ['__NODE_SHA256__', nodeSha256]
  ['__COPILOT_VERSION__', copilotVersion]
  ['__DATA_DISK_LUN__', string(dataDiskLun)]
]
var cloudInit = reduce(cloudInitTokens, loadTextContent('cloud-init.yaml'), (rendered, token) => replace(string(rendered), token[0], token[1]))

// Azure Firewall takes the first usable address of its subnet (the first four
// are reserved). The README tells the operator to compare this with the
// firewallPrivateIp output after the first deployment.
var firewallPrivateIp = cidrHost(firewallSubnetAddressPrefix, 3)

var inboundRules = concat([
  {
    name: 'DenyAllInbound'
    properties: {
      description: 'Nothing reaches the VM from anywhere except Bastion (allowed above when deployed).'
      priority: 4096
      direction: 'Inbound'
      access: 'Deny'
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: '*'
      destinationPortRange: '*'
    }
  }
], deployBastion ? [
  {
    name: 'AllowBastionSsh'
    properties: {
      description: 'SSH from the Bastion subnet only; the explicit DenyAllInbound below overrides the default VNet allow.'
      priority: 100
      direction: 'Inbound'
      access: 'Allow'
      protocol: 'Tcp'
      sourceAddressPrefix: bastionSubnetAddressPrefix
      sourcePortRange: '*'
      destinationAddressPrefix: subnetAddressPrefix
      destinationPortRange: '22'
    }
  }
] : [])

// Outbound: service tags for Entra, Key Vault, Monitor and Graph (behind Front
// Door / AzureCloud), the Azure platform address, and the virtual network (the
// firewall, when deployed). Only applied when restrictOutboundToServiceTags
// is true, because FQDN-level filtering needs the firewall module.
var outboundServiceTags = ['AzureActiveDirectory', 'AzureKeyVault', 'AzureMonitor', 'AzureFrontDoor.Frontend', 'AzureCloud']
var serviceTagRules = [for (tag, index) in outboundServiceTags: {
  name: 'Allow${replace(tag, '.', '')}Outbound'
  properties: {
    priority: 200 + index * 10
    direction: 'Outbound'
    access: 'Allow'
    protocol: 'Tcp'
    sourceAddressPrefix: '*'
    sourcePortRange: '*'
    destinationAddressPrefix: tag
    destinationPortRange: '443'
  }
}]
var outboundRules = restrictOutboundToServiceTags ? concat([
  {
    name: 'AllowVirtualNetworkOutbound'
    properties: {
      priority: 100
      direction: 'Outbound'
      access: 'Allow'
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'VirtualNetwork'
      destinationPortRange: '*'
    }
  }
  {
    name: 'AllowAzurePlatformOutbound'
    properties: {
      description: 'Azure DNS, IMDS and the wire server.'
      priority: 110
      direction: 'Outbound'
      access: 'Allow'
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefixes: ['168.63.129.16/32', '169.254.169.254/32']
      destinationPortRange: '*'
    }
  }
], serviceTagRules, [
  {
    name: 'DenyAllOutbound'
    properties: {
      priority: 4096
      direction: 'Outbound'
      access: 'Deny'
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: '*'
      destinationPortRange: '*'
    }
  }
]) : []

// --------------------------------------------------------------- network ----

resource nsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: '${vmName}-nsg'
  location: location
  tags: tags
  properties: {
    securityRules: concat(inboundRules, outboundRules)
  }
}

// New virtual networks have no default outbound access; without the firewall
// the subnet needs a NAT gateway, or the host can reach nothing.
resource natPublicIp 'Microsoft.Network/publicIPAddresses@2023-11-01' = if (!deployFirewall) {
  name: '${vmName}-nat-pip'
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}

resource natGateway 'Microsoft.Network/natGateways@2023-11-01' = if (!deployFirewall) {
  name: '${vmName}-nat'
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: {
    idleTimeoutInMinutes: 4
    publicIpAddresses: [{ id: natPublicIp.id }]
  }
}

resource routeTable 'Microsoft.Network/routeTables@2023-11-01' = if (deployFirewall) {
  name: '${vmName}-routes'
  location: location
  tags: tags
  properties: {
    disableBgpRoutePropagation: true
    routes: [
      {
        name: 'default-via-firewall'
        properties: {
          addressPrefix: '0.0.0.0/0'
          nextHopType: 'VirtualAppliance'
          nextHopIpAddress: firewallPrivateIp
        }
      }
    ]
  }
}

var vmSubnet = {
  name: subnetName
  properties: {
    addressPrefix: subnetAddressPrefix
    networkSecurityGroup: { id: nsg.id }
    natGateway: deployFirewall ? null : { id: natGateway.id }
    routeTable: deployFirewall ? { id: routeTable.id } : null
    defaultOutboundAccess: false
    serviceEndpoints: [{ service: 'Microsoft.KeyVault' }]
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: vnetName
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [vnetAddressPrefix] }
    subnets: concat([vmSubnet], deployBastion ? [
      { name: 'AzureBastionSubnet', properties: { addressPrefix: bastionSubnetAddressPrefix } }
    ] : [], deployFirewall ? [
      { name: 'AzureFirewallSubnet', properties: { addressPrefix: firewallSubnetAddressPrefix } }
    ] : [])
  }
}

resource bastionPublicIp 'Microsoft.Network/publicIPAddresses@2023-11-01' = if (deployBastion) {
  name: '${bastionName}-pip'
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}

resource bastion 'Microsoft.Network/bastionHosts@2023-11-01' = if (deployBastion) {
  name: bastionName
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: {
    enableTunneling: true
    disableCopyPaste: false
    ipConfigurations: [
      {
        name: 'bastion'
        properties: {
          subnet: { id: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'AzureBastionSubnet') }
          publicIPAddress: { id: bastionPublicIp.id }
        }
      }
    ]
  }
}

module firewall 'modules/firewall.bicep' = if (deployFirewall) {
  name: '${vmName}-firewall'
  params: {
    location: location
    firewallName: '${vmName}-fw'
    subnetId: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'AzureFirewallSubnet')
    sourceAddresses: [subnetAddressPrefix]
    extraFqdns: firewallExtraFqdns
    tags: tags
  }
}

resource nic 'Microsoft.Network/networkInterfaces@2023-11-01' = {
  name: '${vmName}-nic'
  location: location
  tags: tags
  properties: {
    ipConfigurations: [
      {
        name: 'primary'
        properties: {
          privateIPAllocationMethod: 'Dynamic'
          subnet: { id: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, subnetName) }
        }
      }
    ]
    enableAcceleratedNetworking: false
  }
}

// ------------------------------------------------------------- key vault ----

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: empty(keyVaultOperatorIpRanges) ? 'Allow' : 'Deny'
      ipRules: [for range in keyVaultOperatorIpRanges: { value: range }]
      virtualNetworkRules: [
        { id: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, subnetName) }
      ]
    }
  }
}

// --------------------------------------------------------------------- vm ----

resource vm 'Microsoft.Compute/virtualMachines@2024-03-01' = {
  name: vmName
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    hardwareProfile: { vmSize: vmSize }
    securityProfile: {
      securityType: 'TrustedLaunch'
      uefiSettings: { secureBootEnabled: true, vTpmEnabled: true }
      encryptionAtHost: encryptionAtHost ? true : null
    }
    storageProfile: {
      imageReference: {
        publisher: 'Canonical'
        offer: 'ubuntu-24_04-lts'
        sku: 'server'
        version: 'latest'
      }
      osDisk: {
        name: '${vmName}-os'
        createOption: 'FromImage'
        deleteOption: 'Delete'
        managedDisk: {
          storageAccountType: 'Premium_LRS'
          diskEncryptionSet: empty(diskEncryptionSetId) ? null : { id: diskEncryptionSetId }
        }
      }
      dataDisks: [
        {
          name: '${vmName}-data'
          lun: dataDiskLun
          createOption: 'Empty'
          diskSizeGB: dataDiskSizeGb
          caching: 'None'
          deleteOption: 'Detach'
          managedDisk: {
            storageAccountType: 'Premium_LRS'
            diskEncryptionSet: empty(diskEncryptionSetId) ? null : { id: diskEncryptionSetId }
          }
        }
      ]
    }
    osProfile: {
      computerName: vmName
      adminUsername: managerVmLogin
      customData: base64(cloudInit)
      linuxConfiguration: {
        disablePasswordAuthentication: true
        provisionVMAgent: true
        patchSettings: { patchMode: 'ImageDefault' }
        ssh: {
          publicKeys: [
            {
              path: '/home/${managerVmLogin}/.ssh/authorized_keys'
              keyData: managerSshPublicKey
            }
          ]
        }
      }
    }
    networkProfile: {
      networkInterfaces: [
        { id: nic.id, properties: { deleteOption: 'Delete' } }
      ]
    }
    diagnosticsProfile: { bootDiagnostics: { enabled: true } }
  }
}

// Entra SSH login: the manager signs in with their own identity; the RBAC
// assignment below is what grants the login, not a local account.
resource aadSshLogin 'Microsoft.Compute/virtualMachines/extensions@2024-03-01' = {
  parent: vm
  name: 'AADSSHLoginForLinux'
  location: location
  tags: tags
  properties: {
    publisher: 'Microsoft.Azure.ActiveDirectory'
    type: 'AADSSHLoginForLinux'
    typeHandlerVersion: '1.0'
    autoUpgradeMinorVersion: true
  }
}

// ------------------------------------------------------------------ rbac ----

resource managerVmLoginRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vm.id, adminObjectId, roleDefinitionIds.virtualMachineAdministratorLogin)
  scope: vm
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionIds.virtualMachineAdministratorLogin)
    principalId: adminObjectId
    principalType: adminPrincipalType
    description: 'Margo remote host: the manager may log in to the VM with Entra SSH.'
  }
}

resource managerVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, adminObjectId, roleDefinitionIds.keyVaultSecretsOfficer)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionIds.keyVaultSecretsOfficer)
    principalId: adminObjectId
    principalType: adminPrincipalType
    description: 'Margo remote host: the manager uploads and rotates the deployment and Copilot secrets.'
  }
}

resource vmVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, vm.id, roleDefinitionIds.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionIds.keyVaultSecretsUser)
    principalId: vm.identity.principalId
    principalType: 'ServicePrincipal'
    description: 'Margo remote host: the VM identity reads secrets and nothing else.'
  }
}

// ------------------------------------------------------------ monitoring ----

module monitoring 'modules/monitoring.bicep' = {
  name: '${vmName}-monitoring'
  params: {
    location: location
    vmName: vm.name
    alertEmail: alertEmail
    retentionDays: logRetentionDays
    tags: tags
  }
}

// --------------------------------------------------------------- outputs ----

@description('Object id of the VM system-assigned identity (Key Vault Secrets User).')
output principalId string = vm.identity.principalId
@description('Vault URI the host reads the secrets from.')
output keyVaultUri string = keyVault.properties.vaultUri
output vmId string = vm.id
output vmResourceGroup string = resourceGroup().name
output bastionHostName string = deployBastion ? bastion.name : ''
@description('Egress public IP: the NAT gateway without the firewall, the firewall otherwise.')
output egressPublicIp string = deployFirewall ? firewall.outputs.publicIpAddress : natPublicIp.properties.ipAddress
@description('Private IP the route table points at; must equal the firewall\'s actual private IP (also output) when deployFirewall is true.')
output routeNextHopIp string = deployFirewall ? firewallPrivateIp : ''
output firewallPrivateIp string = deployFirewall ? firewall.outputs.privateIpAddress : ''
output logAnalyticsWorkspaceId string = monitoring.outputs.workspaceId
