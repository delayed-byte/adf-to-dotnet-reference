// Example parameters for production. The Key Vault and storage account names come from the ADF linked services
// (LS_KeyVault, LS_Blob_Storage). Replace the subnet with the credit union's integration subnet.
using 'main.bicep'

param environmentName = 'prod'
param keyVaultName = 'kv-cu-batch-prod'
param dataStorageAccountName = 'stcubatchprod'
param keyBridgeBaseUrl = 'https://keybridge.cu.internal/api'
param vnetIntegrationSubnetId = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-network-prod/providers/Microsoft.Network/virtualNetworks/vnet-cu-prod/subnets/snet-func-cu-batch'
