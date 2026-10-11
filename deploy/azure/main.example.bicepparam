// Template for the PRIVATE parameters file. Copy it to main.local.bicepparam
// (gitignored, like every deploy/**/*.local.*), replace every {placeholder},
// and never commit the copy: it names your tenant's objects.
//
// Role definition ids are Azure's built-in GUIDs. They are parameters so that
// no GUID is in the repository; look them up once per cloud:
//   az role definition list --name "Virtual Machine Administrator Login" --query "[].name" -o tsv
//   az role definition list --name "Key Vault Secrets Officer" --query "[].name" -o tsv
//   az role definition list --name "Key Vault Secrets User" --query "[].name" -o tsv
using './main.bicep'

param location = 'eastus'
param vmName = 'vm-margo-example'
param vmSize = 'Standard_B2s'

// The manager: Entra object id, how the local break-glass login is named, its key.
param adminObjectId = '{manager-object-id}'
param adminPrincipalType = 'User'
param managerVmLogin = 'dana-admin'
param managerSshPublicKey = 'ssh-ed25519 {public-key-base64} dana@example.com'

param keyVaultName = 'kv-margo-example'
param keyVaultOperatorIpRanges = []

param deployBastion = true
param deployFirewall = false
param restrictOutboundToServiceTags = false
param firewallExtraFqdns = []

param roleDefinitionIds = {
  virtualMachineAdministratorLogin: '{role-definition-id}'
  keyVaultSecretsOfficer: '{role-definition-id}'
  keyVaultSecretsUser: '{role-definition-id}'
}

// What the host installs. Pin a commit, not a branch.
param repoUrl = 'https://github.com/tolgaki/margo.git'
param repoRevision = '{40-character-commit-id}'
param nodeVersion = '22.22.0'
param nodeSha256 = ''
param copilotVersion = '{copilot-cli-version}'

param deploymentSecretName = 'margo-deployment'
param copilotEnvSecretName = 'margo-copilot-env'

param dataDiskSizeGb = 32
param dataDiskLun = 0
param diskEncryptionSetId = ''
param encryptionAtHost = false

param alertEmail = 'dana@example.com'
param logRetentionDays = 30
param tags = {
  workload: 'margo'
  environment: 'pilot'
}
