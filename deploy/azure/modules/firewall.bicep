// Optional egress allow-list for the Margo host. NSG service tags cannot name
// GitHub, npm or nodejs.org, so a real allow-list needs Azure Firewall with
// application (FQDN) rules. main.bicep routes the VM subnet's default route here.
//
// The default list covers what bootstrap.sh, Copilot CLI, Work IQ sign-in via
// Entra, Microsoft Graph, Key Vault, Azure Monitor and Ubuntu updates need as
// far as this repository can know. Work IQ's own service endpoints are a
// Phase 0 finding: record them in firewallExtraFqdns from the firewall's
// application-rule logs during the pilot, and tighten from there.

@description('Region.')
param location string

@description('Firewall name; the policy and public IP are derived from it.')
param firewallName string

@description('Resource id of AzureFirewallSubnet.')
param subnetId string

@description('Source address prefixes allowed through the rules (the VM subnet).')
param sourceAddresses array

@description('Additional FQDNs to allow on 443.')
param extraFqdns array = []

@description('Tags applied to every resource.')
param tags object = {}

// Entra and Key Vault hosts come from environment() so the list follows the
// cloud the template is deployed to.
var loginHost = replace(replace(environment().authentication.loginEndpoint, 'https://', ''), '/', '')
var vaultHosts = '*${environment().suffixes.keyvaultDns}'
var defaultFqdns = [
  // Repository, Copilot CLI and npm packages
  'github.com'
  'api.github.com'
  'codeload.github.com'
  'objects.githubusercontent.com'
  'raw.githubusercontent.com'
  'api.githubcopilot.com'
  'copilot-proxy.githubusercontent.com'
  'registry.npmjs.org'
  'nodejs.org'
  // Entra, Graph, Key Vault
  loginHost
  'login.microsoft.com'
  'graph.microsoft.com'
  vaultHosts
  // Azure Monitor agent
  '*.ods.opinsights.azure.com'
  '*.oms.opinsights.azure.com'
  '*.monitoring.azure.com'
  '*.handler.control.monitor.azure.com'
  'global.handler.control.monitor.azure.com'
  // Ubuntu and Microsoft package repositories
  'azure.archive.ubuntu.com'
  'archive.ubuntu.com'
  'security.ubuntu.com'
  'esm.ubuntu.com'
  'packages.microsoft.com'
]

resource publicIp 'Microsoft.Network/publicIPAddresses@2023-11-01' = {
  name: '${firewallName}-pip'
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}

resource policy 'Microsoft.Network/firewallPolicies@2023-11-01' = {
  name: '${firewallName}-policy'
  location: location
  tags: tags
  properties: {
    sku: { tier: 'Standard' }
    threatIntelMode: 'Alert'
    dnsSettings: { enableProxy: false }
  }
}

resource rules 'Microsoft.Network/firewallPolicies/ruleCollectionGroups@2023-11-01' = {
  parent: policy
  name: 'margo-egress'
  properties: {
    priority: 200
    ruleCollections: [
      {
        ruleCollectionType: 'FirewallPolicyFilterRuleCollection'
        name: 'allow-https-fqdns'
        priority: 200
        action: { type: 'Allow' }
        rules: [
          {
            ruleType: 'ApplicationRule'
            name: 'margo-https'
            sourceAddresses: sourceAddresses
            protocols: [{ protocolType: 'Https', port: 443 }]
            targetFqdns: concat(defaultFqdns, extraFqdns)
            terminateTLS: false
          }
          {
            ruleType: 'ApplicationRule'
            name: 'ubuntu-http'
            sourceAddresses: sourceAddresses
            protocols: [{ protocolType: 'Http', port: 80 }]
            targetFqdns: ['azure.archive.ubuntu.com', 'archive.ubuntu.com', 'security.ubuntu.com']
            terminateTLS: false
          }
        ]
      }
      {
        ruleCollectionType: 'FirewallPolicyFilterRuleCollection'
        name: 'allow-time'
        priority: 300
        action: { type: 'Allow' }
        rules: [
          {
            ruleType: 'NetworkRule'
            name: 'ntp'
            ipProtocols: ['UDP']
            sourceAddresses: sourceAddresses
            destinationAddresses: ['*']
            destinationPorts: ['123']
          }
        ]
      }
    ]
  }
}

resource firewall 'Microsoft.Network/azureFirewalls@2023-11-01' = {
  name: firewallName
  location: location
  tags: tags
  dependsOn: [rules]
  properties: {
    sku: { name: 'AZFW_VNet', tier: 'Standard' }
    firewallPolicy: { id: policy.id }
    ipConfigurations: [
      {
        name: 'primary'
        properties: {
          subnet: { id: subnetId }
          publicIPAddress: { id: publicIp.id }
        }
      }
    ]
  }
}

output privateIpAddress string = firewall.properties.ipConfigurations[0].properties.privateIPAddress
output publicIpAddress string = publicIp.properties.ipAddress
output policyId string = policy.id
