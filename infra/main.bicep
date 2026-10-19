// Week 8: infrastructure as code for the Functions app and everything it depends on.
// Week 5: no connection strings or keys anywhere. The app's managed identity gets RBAC roles instead.
//
// Deploy:
//   az group create -n rg-orderhub-dev -l eastus2
//   az deployment group create -g rg-orderhub-dev -f infra/main.bicep -p shippingApiBaseUrl=https://partner.example.com/ shippingApiKey=<secret>

@description('Short lowercase name used to derive resource names.')
@maxLength(10)
param appName string = 'orderhub'

param location string = resourceGroup().location

@description('Base URL of the shipping partner API.')
param shippingApiBaseUrl string

@secure()
@description('Shipping partner API key. Stored in Key Vault; the app reads it through a Key Vault reference.')
param shippingApiKey string

@description('Queue-level retry backstop. The app dead-letters itself at OrderHub__MaxDeliveryAttempts, which should be lower.')
param maxDeliveryCount int = 10

var suffix = uniqueString(resourceGroup().id)
var storageName = take('st${appName}${suffix}', 24)
var ordersQueueName = 'orders'

// Built-in role definition IDs: https://learn.microsoft.com/azure/role-based-access-control/built-in-roles
var roles = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
}

// ---------- Observability (Week 6) ----------
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${appName}-${suffix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${appName}-${suffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------- Storage: host state, orders/idempotency tables, lock blobs, deployment packages ----------
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowSharedKeyAccess: false // identity only: account keys can't be used even if leaked
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource deploymentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'deployments'
}

resource locksContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'locks'
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource ordersTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'orders'
}

resource processedTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'processedmessages'
}

// ---------- Service Bus (Week 2) ----------
resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: 'sb-${appName}-${suffix}'
  location: location
  sku: { name: 'Standard', tier: 'Standard' } // sessions and duplicate detection need Standard or Premium
  properties: {
    disableLocalAuth: true // no SAS keys; Entra ID (managed identity) only
    minimumTlsVersion: '1.2'
  }
}

resource ordersQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: serviceBus
  name: ordersQueueName
  properties: {
    requiresSession: true                         // per-customer FIFO
    requiresDuplicateDetection: true              // same MessageId inside the window is dropped
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    maxDeliveryCount: maxDeliveryCount            // broker backstop then DLQ
    lockDuration: 'PT1M'
    defaultMessageTimeToLive: 'P7D'
    deadLetteringOnMessageExpiration: true
  }
}

// ---------- Key Vault (Week 5) ----------
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take('kv-${appName}-${suffix}', 24)
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource shippingKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'ShippingApiKey'
  properties: { value: shippingApiKey }
}

// ---------- Function app on Flex Consumption ----------
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-${appName}-${suffix}'
  location: location
  kind: 'functionapp'
  sku: { tier: 'FlexConsumption', name: 'FC1' }
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: 'func-${appName}-${suffix}'
  location: location
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentsContainer.name}'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
      runtime: { name: 'dotnet-isolated', version: '10.0' }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: [
        // Identity-based connections: the "__" suffix tells the Functions host and our code to use the managed identity.
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'OrderStorage__accountName', value: storage.name }
        { name: 'ServiceBus__fullyQualifiedNamespace', value: '${serviceBus.name}.servicebus.windows.net' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'OrderHub__OrdersQueueName', value: ordersQueueName }
        { name: 'OrderHub__StaleSweepSchedule', value: '0 */15 * * * *' }
        { name: 'OrderHub__MaxDeliveryAttempts', value: '5' }
        { name: 'ShippingApi__BaseUrl', value: shippingApiBaseUrl }
        // Key Vault reference: the platform resolves it with the app's identity. The secret never appears in config or code.
        // The unversioned SecretUri picks up rotations automatically (within ~24h, or on restart).
        { name: 'ShippingApi__ApiKey', value: '@Microsoft.KeyVault(SecretUri=${shippingKeySecret.properties.secretUri})' }
      ]
    }
  }
}

// ---------- RBAC for the function app's identity (least privilege, scoped to each resource) ----------
resource blobOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionApp.id, roles.storageBlobDataOwner)
  scope: storage
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataOwner)
  }
}

resource tableContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionApp.id, roles.storageTableDataContributor)
  scope: storage
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageTableDataContributor)
  }
}

resource sbReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, functionApp.id, roles.serviceBusDataReceiver)
  scope: serviceBus
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
  }
}

resource sbSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, functionApp.id, roles.serviceBusDataSender)
  scope: serviceBus
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
  }
}

resource kvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, functionApp.id, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
  }
}

output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output serviceBusNamespace string = serviceBus.name
