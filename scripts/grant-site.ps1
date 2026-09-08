<#
.SYNOPSIS
    Grants Permission Insight access to one or more SharePoint sites.

.DESCRIPTION
    The application reads every site in the tenant with read-only permission,
    but SharePoint refuses to say WHO has access — site groups, role
    assignments and effective permissions all need rights that no read-only
    permission carries. Rather than hold write access across the tenant, the
    application is granted elevated access one site at a time.

    This script performs that grant. Run it once per site the governance team
    needs to report on.

    It cannot be done by the application itself, and that is the point. An
    application that can grant itself access to any site has tenant-wide access
    with extra steps, and Sites.Selected stops being a control. Granting is a
    deliberate act by an administrator, with their own credentials.

.EXAMPLE
    ./grant-site.ps1 -SiteUrl https://contoso.sharepoint.com/sites/finance `
                     -AppId 00000000-0000-0000-0000-000000000000

.EXAMPLE
    Onboard several at once, and see what is already granted:

    ./grant-site.ps1 -AppId <id> -List `
        -SiteUrl https://contoso.sharepoint.com/sites/finance,
                 https://contoso.sharepoint.com/sites/hr

.NOTES
    Requires the Microsoft.Graph PowerShell module. No app registration is
    needed: Connect-MgGraph requests the scope interactively.

    The operator needs to be able to grant permissions on the site, which in
    practice means SharePoint Administrator or Global Administrator. The scope
    requested is delegated, so this script can never do more than the person
    running it already could.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string[]] $SiteUrl,

    # The backend managed identity's CLIENT id, printed by deploy.ps1 as
    # backendIdentityClientId. Not the principal (object) id.
    [Parameter(Mandatory)] [string] $AppId,

    [string] $DisplayName = 'Permission Insight backend',

    # FullControl is the floor, not a convenience. Reading role assignments
    # needs EnumeratePermissions, which belongs to Full Control alone: a
    # 'manage' grant was tested against a real tenant and SharePoint still
    # refused with 403. The lower levels remain selectable so the finding can
    # be rechecked if Microsoft ever changes this, not because they work.
    [ValidateSet('Read', 'Write', 'Manage', 'FullControl')]
    [string] $Permissions = 'FullControl',

    # Show what each site already grants and change nothing.
    [switch] $List,

    # Remove this application's access instead of granting it.
    [switch] $Revoke
)

$ErrorActionPreference = 'Stop'

function Write-Step { param($Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note { param($Message) Write-Host "    $Message" -ForegroundColor DarkGray }

# ------------------------------------------------------------- connect

# Sites.FullControl.All is what POST /sites/{id}/permissions requires. It is
# delegated, so it grants this script nothing the operator did not already
# have; it is the only scope Graph accepts for this operation.
Write-Step 'Signing in to Microsoft Graph'
Connect-MgGraph -Scopes 'Sites.FullControl.All' -NoWelcome

$failed = @()

foreach ($url in $SiteUrl) {
    Write-Host ''
    Write-Step $url

    try {
        # Graph addresses a site as {hostname}:{server-relative-path}, never as
        # a full URL. Passing the URL returns a 400 that reads like a
        # permission problem.
        $uri = [uri] $url
        $siteId = (Get-MgSite -SiteId "$($uri.Host):$($uri.AbsolutePath)").Id

        if (-not $siteId) {
            throw 'Site not found.'
        }

        $existing = Get-MgSitePermission -SiteId $siteId -All |
            Where-Object { $_.GrantedToIdentities.Application.Id -contains $AppId }

        if ($List) {
            if ($existing) {
                foreach ($grant in $existing) {
                    Write-Note "granted: $($grant.Roles -join ', ')  (permission id $($grant.Id))"
                }
            }
            else {
                Write-Note 'not granted'
            }
            continue
        }

        if ($Revoke) {
            if (-not $existing) {
                Write-Note 'nothing to revoke'
                continue
            }

            foreach ($grant in $existing) {
                if ($PSCmdlet.ShouldProcess($url, "Revoke $($grant.Roles -join ', ')")) {
                    Remove-MgSitePermission -SiteId $siteId -PermissionId $grant.Id
                    Write-Note "revoked: $($grant.Roles -join ', ')"
                }
            }
            continue
        }

        $role = $Permissions.ToLowerInvariant()

        # Raise an existing grant rather than adding a second one, so a site
        # never ends up with two grants for the same application and nobody has
        # to work out which one is in effect.
        if ($existing) {
            foreach ($grant in $existing) {
                if ($grant.Roles -contains $role) {
                    Write-Note "already granted: $($grant.Roles -join ', ')"
                    continue
                }

                if ($PSCmdlet.ShouldProcess($url, "Change $($grant.Roles -join ', ') to $role")) {
                    Update-MgSitePermission -SiteId $siteId -PermissionId $grant.Id `
                        -BodyParameter @{ roles = @($role) } | Out-Null
                    Write-Note "raised to: $role"
                }
            }
            continue
        }

        if ($PSCmdlet.ShouldProcess($url, "Grant $role")) {
            New-MgSitePermission -SiteId $siteId -BodyParameter @{
                roles               = @($role)
                grantedToIdentities = @(
                    @{ application = @{ id = $AppId; displayName = $DisplayName } }
                )
            } | Out-Null

            Write-Note "granted: $role"
        }
    }
    catch {
        Write-Warning "$url : $($_.Exception.Message)"
        $failed += $url
    }
}

Write-Host ''

if ($failed) {
    Write-Host '--------------------------------------------------------------'
    Write-Host 'Some sites were not changed:' -ForegroundColor Yellow
    $failed | ForEach-Object { Write-Host "  $_" }
    Write-Host ''
    Write-Host 'A 403 here means the signed-in account cannot grant permissions'
    Write-Host 'on that site. This script is delegated: it can do no more than'
    Write-Host 'the person running it.'
    exit 1
}

if ($List -or $Revoke) {
    return
}

Write-Host 'Done. Rescan the site in Permission Insight.' -ForegroundColor Green
Write-Host ''
Write-Host 'A grant does not expire. When the audit is finished, take it back:' -ForegroundColor Yellow
Write-Host ''
Write-Host ("  ./scripts/grant-site.ps1 -Revoke -AppId {0} ``" -f $AppId)
Write-Host ("      -SiteUrl {0}" -f ($SiteUrl -join ',' ))
Write-Host ''
Write-Host 'Scan results already live in the browser, so revoking loses nothing'
Write-Host 'that has been collected. There is no API that lists every site an'
Write-Host 'application has been granted, so the reliable way to know what is'
Write-Host 'outstanding is to keep the standing set empty.'
