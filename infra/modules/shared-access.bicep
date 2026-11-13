// Access to the existing Key Vault and data storage account, which may sit in another resource group (ADR 0020).

targetScope = 'resourceGroup'

param keyVaultName string
param dataStorageAccountName string
param blobConnectionStringSecretName string

@description('Principal ID of the Function App managed identity.')
param principalId string

// Built-in role: Key Vault Secrets User.
var vaultReaderRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource dataStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: dataStorageAccountName
}

resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, principalId, vaultReaderRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', vaultReaderRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

// Stopgap until AzureCardBlobStore and AzureEvidenceBlobStore build BlobServiceClient from a URI and the identity
// (playbook checklist, "Managed identity"). Then delete this secret and grant Storage Blob Data Contributor instead.
resource blobConnectionString 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: blobConnectionStringSecretName
  properties: {
    contentType: 'Storage connection string (account key)'
    value: 'DefaultEndpointsProtocol=https;AccountName=${dataStorage.name};AccountKey=${dataStorage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
  }
}
