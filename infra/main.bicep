// Permission Insight - core infrastructure.
//
// Everything here can be deployed by an account with rights over the resource
// group. The tenant-wide application role grants live in app-roles.bicep,
// which needs a privileged account; see docs/adr/0004-*.md for why they are
// split.
//
// There is no Key Vault and no client secret anywhere in this file. If a
// change appears to need one, reread the Credentials section of
// docs/01-architecture.md.

targetScope = 'resourceGroup'

extension microsoftGraphV1

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Prefix for generated resource names. Kept short because the storage account name is this plus a 13 character hash and may not exceed 24 characters.')
@minLength(3)
@maxLength(11)
param namePrefix string = 'perminsight'

@description('Identifier URI for the backend API, under a domain the tenant has already verified, for example api://yourtenant.onmicrosoft.com/permission-insight. No default: it is tenant specific, and Entra cannot resolve the sign-in scope without it. See docs/adr/0003-*.md.')
param apiIdentifierUri string

@description('Root URL of the SharePoint tenant, for example https://yourtenant.sharepoint.com. No default, so a deployment fails rather than silently targeting somebody else\'s tenant.')
param sharePointRootUrl string

@description('Display name of the Entra security group whose members may use the application. Membership of this group is the real access control.')
param usersGroupDisplayName string = 'sg-permission-insight-users'

@description('Object id of the principal running the deployment. When supplied it is granted Storage Blob Data Contributor so the deployment script can upload the front end without an account key. Leave empty in CI if the identity already holds the role.')
param deployerPrincipalId string = ''

@description('Retention in days for the audit log. This is the only record of who searched, scanned or looked up a person, so it is a governance decision rather than a cost one.')
@minValue(30)
@maxValue(730)
param auditRetentionDays int = 365

@description('Tags applied to every Azure resource.')
param tags object = {}

// ---------------------------------------------------------------- names

var suffix = uniqueString(resourceGroup().id)
var storageAccountName = toLower('${namePrefix}${suffix}')
var functionAppName = '${namePrefix}-api-${suffix}'
var planName = '${namePrefix}-plan-${suffix}'
var identityName = '${namePrefix}-backend-${suffix}'
var workspaceName = '${namePrefix}-logs-${suffix}'
var insightsName = '${namePrefix}-insights-${suffix}'
var applicationUniqueName = '${namePrefix}-${suffix}'

var deploymentContainerName = 'function-releases'

// ------------------------------------------------------------- storage

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    // The static website endpoint is served independently of anonymous blob
    // access, so the SPA is still reachable with this off.
    allowBlobPublicAccess: false
    // allowSharedKeyAccess is deliberately left at its default of true.
    // Enabling static website hosting is a Set Blob Service Properties call,
    // and that operation does not support Entra ID authorization. Nothing
    // stores a key: the function app uses identity based connections and
    // scripts/deploy.ps1 uploads with --auth-mode login.
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deploymentContainerName
  properties: {
    publicAccess: 'None'
  }
}

// The static website origin, without the trailing slash the endpoint carries.
var webEndpoint = storage.properties.primaryEndpoints.web
var spaOrigin = substring(webEndpoint, 0, max(length(webEndpoint) - 1, 0))

// ------------------------------------------------------------ identity

// User assigned rather than system assigned: it survives recreating the
// function app, and it can be granted permissions before the compute exists.
resource backendIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

var storageBlobDataOwnerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var storageBlobDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

// The Flex Consumption host reads its own deployment package and writes lease
// blobs as this identity, which is what keeps AzureWebJobsStorage free of a
// connection string.
resource backendStorageAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, backendIdentity.id, storageBlobDataOwnerRoleId)
  properties: {
    roleDefinitionId: storageBlobDataOwnerRoleId
    principalId: backendIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerStorageAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  scope: storage
  name: guid(storage.id, deployerPrincipalId, storageBlobDataContributorRoleId)
  properties: {
    roleDefinitionId: storageBlobDataContributorRoleId
    principalId: deployerPrincipalId
  }
}

// ----------------------------------------------------------- audit sink

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: auditRetentionDays
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: insightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// ---------------------------------------------------------------- entra

var useAppRoleId = guid(applicationUniqueName, 'PermissionInsight.Use')
var accessAsUserScopeId = guid(applicationUniqueName, 'access_as_user')

resource application 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: applicationUniqueName
  displayName: 'Permission Insight'
  description: 'Reports broken permission inheritance and sharing links in SharePoint Online. Read only.'
  signInAudience: 'AzureADMyOrg'
  identifierUris: [
    apiIdentifierUri
  ]
  spa: {
    redirectUris: [
      spaOrigin
      '${spaOrigin}/'
    ]
  }
  api: {
    // v2 only. The backend refuses v1 tokens, whose issuer differs.
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: accessAsUserScopeId
        value: 'access_as_user'
        type: 'User'
        isEnabled: true
        adminConsentDisplayName: 'Use Permission Insight'
        adminConsentDescription: 'Allows the signed-in user to call the Permission Insight API. The API checks the PermissionInsight.Use role separately.'
        userConsentDisplayName: 'Use Permission Insight'
        userConsentDescription: 'Lets Permission Insight call its own API on your behalf.'
      }
    ]
  }
  appRoles: [
    {
      id: useAppRoleId
      value: 'PermissionInsight.Use'
      allowedMemberTypes: [
        'User'
      ]
      isEnabled: true
      displayName: 'Use Permission Insight'
      description: 'Read permission and sharing data for every SharePoint site in the tenant. This is equivalent to tenant-wide read access; assign it as a privileged role.'
    }
  ]
  // No permissions on any other resource. The front end holds nothing
  // dangerous, which is the property docs/02-permissions.md is built on: a
  // delegated Sites.FullControl.All was written here to let the application
  // onboard sites itself, and removed again when the Graph driveItem
  // permissions endpoint turned out to work under plain Sites.Read.All. See
  // docs/adr/0011.
  requiredResourceAccess: []
}

// appRoleAssignmentRequired means Entra refuses sign-in for anyone outside the
// group before the application's own code runs. The code checks the role
// anyway; this is the outer of two layers, not a replacement for it.
resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: application.appId
  displayName: 'Permission Insight'
  appRoleAssignmentRequired: true
}

resource usersGroup 'Microsoft.Graph/groups@v1.0' = {
  uniqueName: usersGroupDisplayName
  displayName: usersGroupDisplayName
  description: 'Members may use Permission Insight, which grants read access to permission data for every SharePoint site in the tenant. Treat as a privileged assignment.'
  mailEnabled: false
  mailNickname: usersGroupDisplayName
  securityEnabled: true
}

resource usersGroupHasRole 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: useAppRoleId
  principalId: usersGroup.id
  resourceId: servicePrincipal.id
}

// ------------------------------------------------------------- backend

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${backendIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: backendIdentity.id
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '9.0'
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      cors: {
        allowedOrigins: [
          spaOrigin
        ]
        supportCredentials: false
      }
      appSettings: [
        // Identity based, so there is no connection string holding a key.
        {
          name: 'AzureWebJobsStorage__accountName'
          value: storage.name
        }
        {
          name: 'AzureWebJobsStorage__credential'
          value: 'managedidentity'
        }
        {
          name: 'AzureWebJobsStorage__clientId'
          value: backendIdentity.properties.clientId
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: insights.properties.ConnectionString
        }
        {
          name: 'TenantId'
          value: subscription().tenantId
        }
        {
          name: 'ApiClientId'
          value: application.appId
        }
        {
          name: 'ApiIdentifierUri'
          value: apiIdentifierUri
        }
        {
          name: 'ManagedIdentityClientId'
          value: backendIdentity.properties.clientId
        }
        {
          name: 'SharePointRootUrl'
          value: sharePointRootUrl
        }
        {
          name: 'AllowedOrigin'
          value: spaOrigin
        }
      ]
    }
  }
  dependsOn: [
    backendStorageAccess
  ]
}

// ------------------------------------------------- deployment credentials

// Password-based deployment, refused on both channels.
//
// These are a separate control from ftpsState, which only disables the FTP
// protocol. Left at their default of true, the app also accepts the publishing
// profile's username and password for SCM and Kudu deployment — a credential
// that can be downloaded by anyone with Contributor on the app, pasted into a
// pipeline, and used to deploy arbitrary code to a backend that holds
// tenant-wide SharePoint read.
//
// That would be the one password in an architecture built on having none. With
// both refused, deployment is possible only through Entra ID.
//
// Verified not to break the deployment path: Flex Consumption publishes through
// ARM rather than through the publishing profile, so `func azure functionapp
// publish` still works with these off.
resource scmBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: functionApp
  name: 'scm'
  properties: {
    allow: false
  }
}

resource ftpBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: functionApp
  name: 'ftp'
  properties: {
    allow: false
  }
}

// --------------------------------------------------------------- output

output staticSiteUrl string = spaOrigin
output apiBaseUrl string = 'https://${functionApp.properties.defaultHostName}/api'
output functionAppName string = functionApp.name
output storageAccountName string = storage.name
output resourceGroupName string = resourceGroup().name

output tenantId string = subscription().tenantId
output clientId string = application.appId
output apiScope string = '${apiIdentifierUri}/access_as_user'
output sharePointRootUrl string = sharePointRootUrl
output usersGroupObjectId string = usersGroup.id

// Feeds app-roles.bicep, and the fallback instructions if that deployment is
// refused for want of privilege.
output backendIdentityPrincipalId string = backendIdentity.properties.principalId
output backendIdentityClientId string = backendIdentity.properties.clientId
output backendIdentityName string = backendIdentity.name

// Pre-consents this application's own access_as_user scope for the tenant, so
// nobody sees a consent prompt on first sign-in. Optional.
// It does not and cannot grant the managed identity's application roles; see
// docs/adr/0004-*.md.
output adminConsentUrl string = '${environment().authentication.loginEndpoint}${subscription().tenantId}/adminconsent?client_id=${application.appId}'
