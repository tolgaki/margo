// Health alerts for the Margo host. A broken Margo cannot be relied on to post
// her own alert, so Azure Monitor tells the manager: VM availability, agent
// heartbeat, and anything margo-harness logs at syslog severity err (the
// harness prefixes fatal lines with <3>, which journald maps to err; systemd's
// own "Failed" record for the unit is matched as well).

@description('Region for the workspace, data collection rule and log alerts.')
param location string

@description('Name of the existing VM in this resource group.')
param vmName string

@description('Address that receives the alerts.')
param alertEmail string

@description('Workspace retention in days.')
param retentionDays int = 30

@description('Tags applied to every resource.')
param tags object = {}

resource vm 'Microsoft.Compute/virtualMachines@2024-03-01' existing = {
  name: vmName
}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${vmName}-alerts'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'margo'
    enabled: true
    emailReceivers: [
      {
        name: 'manager'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${vmName}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: retentionDays
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// Syslog from the daemon facility (systemd units) and the usual system
// facilities, warning and above. Message bodies from Margo's units are status
// lines and digests, never mail content; the audit log stays on the host.
resource dataCollectionRule 'Microsoft.Insights/dataCollectionRules@2022-06-01' = {
  name: '${vmName}-syslog'
  location: location
  tags: tags
  kind: 'Linux'
  properties: {
    dataSources: {
      syslog: [
        {
          name: 'margo-syslog'
          streams: ['Microsoft-Syslog']
          facilityNames: ['auth', 'authpriv', 'daemon', 'kern', 'syslog', 'user']
          logLevels: ['Warning', 'Error', 'Critical', 'Alert', 'Emergency']
        }
      ]
    }
    destinations: {
      logAnalytics: [
        { name: 'workspace', workspaceResourceId: workspace.id }
      ]
    }
    dataFlows: [
      { streams: ['Microsoft-Syslog'], destinations: ['workspace'] }
    ]
  }
}

resource agent 'Microsoft.Compute/virtualMachines/extensions@2024-03-01' = {
  parent: vm
  name: 'AzureMonitorLinuxAgent'
  location: location
  tags: tags
  properties: {
    publisher: 'Microsoft.Azure.Monitor'
    type: 'AzureMonitorLinuxAgent'
    typeHandlerVersion: '1.0'
    autoUpgradeMinorVersion: true
    enableAutomaticUpgrade: true
  }
}

resource association 'Microsoft.Insights/dataCollectionRuleAssociations@2022-06-01' = {
  name: '${vmName}-syslog-association'
  scope: vm
  properties: {
    dataCollectionRuleId: dataCollectionRule.id
    description: 'Margo host syslog to the workspace'
  }
  dependsOn: [agent]
}

resource availabilityAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${vmName}-unavailable'
  location: 'global'
  tags: tags
  properties: {
    description: 'The Margo VM is not available (platform metric).'
    severity: 1
    enabled: true
    scopes: [vm.id]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'availability'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'Microsoft.Compute/virtualMachines'
          metricName: 'VmAvailabilityMetric'
          operator: 'LessThan'
          threshold: 1
          timeAggregation: 'Average'
        }
      ]
    }
    actions: [{ actionGroupId: actionGroup.id }]
  }
}

resource heartbeatAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${vmName}-no-heartbeat'
  location: location
  tags: tags
  properties: {
    displayName: 'Margo host: no agent heartbeat'
    description: 'The Azure Monitor agent on the Margo VM has not reported for 15 minutes.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [workspace.id]
    autoMitigate: true
    criteria: {
      allOf: [
        {
          query: 'Heartbeat | where Computer =~ "${vmName}"'
          timeAggregation: 'Count'
          operator: 'LessThan'
          threshold: 1
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: { actionGroups: [actionGroup.id] }
  }
}

resource harnessErrorAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${vmName}-harness-error'
  location: location
  tags: tags
  properties: {
    displayName: 'Margo harness: error logged'
    description: 'margo-harness logged at severity err (blocked, reauth_required or a failed cycle), or systemd recorded the unit as failed.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [workspace.id]
    autoMitigate: false
    criteria: {
      allOf: [
        {
          query: 'Syslog | where (ProcessName == "margo-harness" and SeverityLevel == "err") or (ProcessName == "systemd" and SyslogMessage has "margo-harness.service" and SyslogMessage has "Failed")'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: { actionGroups: [actionGroup.id] }
  }
}

output workspaceId string = workspace.id
output actionGroupId string = actionGroup.id
