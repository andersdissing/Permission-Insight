// Permission Insight - the privileged half of the deployment.
//
// These four assignments give the backend's managed identity read access to
// every SharePoint site in the tenant. Creating them requires Privileged Role
// Administrator or Global Administrator, which is why they are separated from
// main.bicep: everything else can be redeployed by an ordinary account, and a
// pull request that changes tenant-wide permissions touches only this file.
//
// Every one of them is a read. There is no write-capable permission here, and
// adding one is a change to the threat model rather than a bug fix — see
// SECURITY.md and docs/setup-guide.md.
//
// There is no consent URL that substitutes for this. A managed identity has no
// consent screen; its permissions exist only as appRoleAssignedTo objects.
// See docs/adr/0004-app-role-grants-are-a-second-deployment.md.

targetScope = 'resourceGroup'

extension microsoftGraphV1

@description('Object id of the backend user-assigned managed identity\'s service principal. Output backendIdentityPrincipalId from main.bicep.')
param backendIdentityPrincipalId string

// Well known first party application ids, identical in every tenant.
var microsoftGraphAppId = '00000003-0000-0000-c000-000000000000'
var sharePointOnlineAppId = '00000003-0000-0ff1-ce00-000000000000'

resource microsoftGraph 'Microsoft.Graph/servicePrincipals@v1.0' existing = {
  appId: microsoftGraphAppId
}

resource sharePointOnline 'Microsoft.Graph/servicePrincipals@v1.0' existing = {
  appId: sharePointOnlineAppId
}

// Role ids are resolved from each resource service principal. A hardcoded GUID
// that is subtly wrong grants a different permission and still deploys.
var graphSitesReadAllId = first(filter(microsoftGraph.appRoles, role => role.value == 'Sites.Read.All'))!.id
var graphUserReadBasicAllId = first(filter(microsoftGraph.appRoles, role => role.value == 'User.ReadBasic.All'))!.id
var graphGroupMemberReadAllId = first(filter(microsoftGraph.appRoles, role => role.value == 'GroupMember.Read.All'))!.id
var sharePointSitesReadAllId = first(filter(sharePointOnline.appRoles, role => role.value == 'Sites.Read.All'))!.id

// The permission the whole product rests on.
//
// Site search, library and item enumeration — and, the part that was not
// obvious, the permission data itself. Graph documents Sites.Read.All as the
// least privileged application permission for listItem/permissions, so who has
// access to what is readable without any write-capable role. See
// docs/adr/0011 and docs/setup-guide.md.
resource graphSitesReadAll 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphSitesReadAllId
  principalId: backendIdentityPrincipalId
  resourceId: microsoftGraph.id
}

// The people picker in Search for person, and nothing else. ReadBasic rather
// than User.Read.All because the picker needs a name and an address.
resource graphUserReadBasicAll 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphUserReadBasicAllId
  principalId: backendIdentityPrincipalId
  resourceId: microsoftGraph.id
}

// Opening an Entra group in the detail panel.
//
// 02-permissions.md refused this, because getusereffectivepermissions resolved
// the whole group chain server-side and answered the question better. That
// endpoint is unreachable on read-only permissions, so the chain is walked one
// level at a time instead and this is what makes that possible.
//
// GroupMember.Read.All rather than Group.Read.All: it reads membership and
// nothing else, where Group.Read.All would also read every group's properties.
resource graphGroupMemberReadAll 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphGroupMemberReadAllId
  principalId: backendIdentityPrincipalId
  resourceId: microsoftGraph.id
}

// SharePoint's own Sites.Read.All, for the REST endpoints Graph does not
// expose: the flat list-items enumeration the scan pages through, and the site
// groups collection that finds sharing links.
//
// Note what is *not* in that list any more. roleassignments and
// getusereffectivepermissions refuse an app-only read-only token whatever is
// granted here — they need EnumeratePermissions, which lives in Full Control.
// The tool reads permissions through Graph instead, and the dead code that
// called them has been removed rather than left to look load-bearing.
resource sharePointSitesReadAll 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: sharePointSitesReadAllId
  principalId: backendIdentityPrincipalId
  resourceId: sharePointOnline.id
}

// Sites.Selected was here and has been removed.
//
// It was the gate for adr/0010, which granted the identity elevated access one
// site at a time so that permission data could be read. adr/0011 found that
// Graph reads permissions on Sites.Read.All alone, so no site ever needed
// onboarding and the assignment granted nothing. A permission that grants
// nothing is still a permission somebody has to explain, and it invites the
// question "which sites are onboarded?" when the answer is that the mechanism
// no longer exists. See docs/setup-guide.md.

output grantedRoles array = [
  'Microsoft Graph / Sites.Read.All'
  'Microsoft Graph / User.ReadBasic.All'
  'Microsoft Graph / GroupMember.Read.All'
  'SharePoint / Sites.Read.All'
]
