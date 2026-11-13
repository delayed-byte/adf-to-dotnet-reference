// Azure hosting for the CreditUnion.Batch Functions app (ADR 0020). Not deployed or verified against a subscription;
// see the ADR for what is unverified. `./build.ps1 infra` compiles and lints it; InfrastructureTemplateTests ties the
// app settings to the options classes, the Key Vault secret names to the ADF JSON and the time zone to the ADF trigger.

targetScope = 'resourceGroup'

@description('Environment suffix for resource names and the Durable task hub.')
@allowed([
  'dev'
  'test'
  'prod'
])
param environmentName string

@description('Azure region for the resources this template creates.')
param location string = resourceGroup().location

@description('Resource group that holds the existing Key Vault and data storage account (LS_KeyVault, LS_Blob_Storage).')
param sharedResourceGroupName string = resourceGroup().name

@description('Existing Key Vault (LS_KeyVault) with the batch secrets. Must use the Azure RBAC permission model.')
param keyVaultName string

@description('Existing storage account with the card-landing, card-archive, card-quarantine and evidence containers (LS_Blob_Storage).')
param dataStorageAccountName string

@description('Key Vault secret with the whole KeyStone Db2 connection string, TLS keywords included (ADR 0019, option A).')
param keyStoneDb2ConnectionStringSecretName string = 'keystone-db2-extract-connection-string'

@description('KeyBridge API base URL (PL_MapAndAggregate parameter keyBridgeBaseUrl).')
param keyBridgeBaseUrl string = 'https://keybridge.cu.internal/api'

@description('KeyBridge Basic-auth user name.')
param keyBridgeUsername string = 'cu-batch'

@description('Resource ID of the subnet for VNet integration (delegated to Microsoft.Web/serverFarms). Required to reach Db2 privately; empty deploys without VNet integration.')
param vnetIntegrationSubnetId string = ''

@description('Maximum number of EP1 instances the plan scales out to.')
@minValue(1)
@maxValue(20)
param maximumElasticWorkerCount int = 3

@description('Blob name of the published, zipped Functions app in the deployments container.')
param packageBlobName string = 'CreditUnion.Batch.Functions.zip'

param tags object = {
  workload: 'cu-batch'
  environment: environmentName
}

// ---- names --------------------------------------------------------------------------------------

var suffix = 'cu-batch-${environmentName}'
var hostStorageName = 'stcubfn${environmentName}${take(uniqueString(resourceGroup().id), 8)}'
var deploymentsContainerName = 'deployments'
var taskHubName = 'CreditUnionBatch${toUpper(substring(environmentName, 0, 1))}${substring(environmentName, 1)}'
var blobConnectionStringSecretName = 'batch-blob-storage-connection-string'

// Built-in role definition IDs.
var roles = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  monitoringMetricsPublisher: '3913510d-42f4-4e42-8a64-420c390055eb'
}

// ---- identity -----------------------------------------------------------------------------------

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${suffix}'
  location: location
  tags: tags
}

// ---- host storage (AzureWebJobsStorage: Durable task hub, timer lease, keys, app package) --------

resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: hostStorageName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
  }
}

resource hostBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: hostStorage
  name: 'default'
}

resource deploymentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: hostBlobService
  name: deploymentsContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource hostStorageRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for role in [roles.storageBlobDataOwner, roles.storageQueueDataContributor, roles.storageTableDataContributor]: {
    name: guid(hostStorage.id, identity.id, role)
    scope: hostStorage
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
      principalId: identity.properties.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

// ---- monitoring ---------------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 90
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${suffix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    DisableLocalAuth: true
  }
}

resource appInsightsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, identity.id, roles.monitoringMetricsPublisher)
  scope: appInsights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.monitoringMetricsPublisher)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---- Key Vault access and the blob connection-string secret (existing resources) --------------

module shared 'modules/shared-access.bicep' = {
  name: 'cu-batch-shared-access-${environmentName}'
  scope: resourceGroup(sharedResourceGroupName)
  params: {
    keyVaultName: keyVaultName
    dataStorageAccountName: dataStorageAccountName
    blobConnectionStringSecretName: blobConnectionStringSecretName
    principalId: identity.properties.principalId
  }
}

// ---- plan and Function App ----------------------------------------------------------------------

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-${suffix}'
  location: location
  tags: tags
  kind: 'elastic'
  sku: {
    name: 'EP1'
    tier: 'ElasticPremium'
  }
  properties: {
    reserved: false // Windows: Net.IBM.Data.Db2 is the Windows driver package (ADR 0020).
    maximumElasticWorkerCount: maximumElasticWorkerCount
  }
}

func keyVaultReference(vault string, secret string) string => '@Microsoft.KeyVault(VaultName=${vault};SecretName=${secret})'

var appSettings = [
  // Host
  { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
  { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
  { name: 'AzureWebJobsStorage__accountName', value: hostStorage.name }
  { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
  { name: 'AzureWebJobsStorage__clientId', value: identity.properties.clientId }
  { name: 'AzureFunctionsJobHost__extensions__durableTask__hubName', value: taskHubName }
  { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '${hostStorage.properties.primaryEndpoints.blob}${deploymentsContainerName}/${packageBlobName}' }
  { name: 'WEBSITE_RUN_FROM_PACKAGE_BLOB_MI_RESOURCE_ID', value: identity.id }
  { name: 'WEBSITE_TIME_ZONE', value: 'Pacific Standard Time' } // ADR 0016: Windows ID on a Windows plan.
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
  { name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING', value: 'ClientId=${identity.properties.clientId};Authorization=AAD' }

  // LS_KeyStone_Db2 (KeyStoneDb2Options)
  { name: 'KeyStoneDb2__ConnectionString', value: keyVaultReference(keyVaultName, keyStoneDb2ConnectionStringSecretName) }

  // LS_Reporting_Sql (ReportingSqlOptions)
  { name: 'ReportingSql__ConnectionString', value: keyVaultReference(keyVaultName, 'reporting-sql-writer-connection-string') }

  // LS_Blob_Storage (BlobStorageOptions); container names and RetainUntil keep their defaults.
  { name: 'BlobStorage__ConnectionString', value: keyVaultReference(keyVaultName, blobConnectionStringSecretName) }

  // KeyBridge (KeyBridgeOptions)
  { name: 'KeyBridge__BaseUrl', value: keyBridgeBaseUrl }
  { name: 'KeyBridge__Username', value: keyBridgeUsername }
  { name: 'KeyBridge__Password', value: keyVaultReference(keyVaultName, 'keybridge-api-password') }

  // Reconciliation alert webhook (ReconAlertWebhookOptions)
  { name: 'ReconAlertWebhook__Url', value: keyVaultReference(keyVaultName, 'recon-alert-webhook-url') }
]

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: 'func-${suffix}'
  location: location
  tags: tags
  kind: 'functionapp'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identity.id
    virtualNetworkSubnetId: empty(vnetIntegrationSubnetId) ? null : vnetIntegrationSubnetId
    vnetRouteAllEnabled: !empty(vnetIntegrationSubnetId)
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      use32BitWorkerProcess: false // the Db2 clidriver is 64-bit
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      minimumElasticInstanceCount: 1
      appSettings: appSettings
    }
  }
  dependsOn: [
    hostStorageRoles
    appInsightsPublisher
    shared
    deploymentsContainer
  ]
}

resource ftpCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: functionApp
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource scmCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: functionApp
  name: 'scm'
  properties: {
    allow: false
  }
}

output functionAppName string = functionApp.name
output hostStorageAccountName string = hostStorage.name
output deploymentsContainerName string = deploymentsContainerName
output taskHubName string = taskHubName
output identityPrincipalId string = identity.properties.principalId
