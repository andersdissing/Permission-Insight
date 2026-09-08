<#
.SYNOPSIS
    Grants the organisation-wide claims in the test site, and reports what the
    tenant calls them.

.DESCRIPTION
    "Everyone" and "Everyone except external users" are claim principals that
    open an object to the whole organisation in a single row. They are the
    widest findings the tool reports and the quietest: there are no members to
    enumerate, so nothing about the object looks unusual, and a grant at the
    site breaks no inheritance at all.

    Their display names are localised. On a Danish tenant the principal reads
    "Alle undtagen eksterne brugere"; on a German one "Jeder außer externen
    Benutzern". Permission Insight therefore matches on the claim, never on the
    name, and this script exists to make that testable.

    It grants at three scopes, because each is a different code path:

      1. the site      — reaches everything that inherits, and breaks nothing,
                         so an item scan alone would report a clean site
      2. a library     — MediumLib, which already has unique permissions
      3. an item       — a folder, using the wider "Everyone" claim, which
                         includes guests

    provision-test-data.ps1 does the same as part of a full provision. This
    exists so the scenario can be added without re-running a script that
    uploads twelve thousand files.

    Idempotent: re-running re-grants the same roles, which SharePoint treats as
    a no-op.

.NOTES
    Requires PnP.PowerShell and a client id for interactive sign-in — see
    docs/10-test-data.md.

    The last section answers the VERIFY that CLAUDE.md requires before building
    on a SharePoint shape: it prints the claim exactly as SharePoint stores it,
    and the display name this tenant renders it with, so the two can be
    compared against what the code matches on.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $SiteUrl,

    [string] $PnPClientId,

    # Left out by default. It reaches guests as well as employees, so it is the
    # more alarming of the two to create even in a test tenant.
    [switch] $IncludeEveryoneWithGuests,

    [string] $Library = 'MediumLib',

    [string] $FolderPath = 'Documents/Area 5/Team 4'
)

$ErrorActionPreference = 'Stop'

function Write-Step { param($Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note { param($Message) Write-Host "    $Message" -ForegroundColor DarkGray }

$clientId = $PnPClientId
if (-not $clientId) { $clientId = $env:PNPPOWERSHELL_CLIENTID }
if (-not $clientId) { $clientId = $env:ENTRAID_CLIENT_ID }

if (-not $clientId) {
    throw @"
PnP.PowerShell has no app registration of its own, so an interactive sign-in
needs a client id from one in your tenant.

    Register-PnPEntraIDAppForInteractiveLogin ``
        -ApplicationName 'PnP PowerShell' -Tenant <tenant>.onmicrosoft.com -Interactive

Then rerun with -PnPClientId <the id>, or set PNPPOWERSHELL_CLIENTID.
"@
}

Write-Step "Connecting to $SiteUrl"
Connect-PnPOnline -Url $SiteUrl -Interactive -ClientId $clientId

# The claim carries the directory id, so it is never a fixed string. This is
# the reason the tool matches on a segment of the claim rather than the whole
# of it.
$tenantId = $null
try { $tenantId = Get-PnPTenantId -ErrorAction Stop } catch { }
if (-not $tenantId) {
    $tenantId = (Invoke-PnPGraphMethod -Url 'v1.0/organization?$select=id').value[0].id
}

$everyoneExceptExternal = "c:0-.f|rolemanager|spo-grid-all-users/$tenantId"
$everyone               = 'c:0(.s|true'

Write-Note "tenant $tenantId"

function Grant-Claim {
    param(
        [Parameter(Mandatory)] [string] $Scope,
        [Parameter(Mandatory)] [scriptblock] $Grant
    )

    if (-not $PSCmdlet.ShouldProcess($Scope, 'grant an organisation-wide claim')) { return }

    try {
        & $Grant
        Write-Host "    granted on $Scope" -ForegroundColor Green
    }
    catch {
        Write-Warning "    could not grant on ${Scope}: $($_.Exception.Message)"
    }
}

# 1. The site. The case the tool was blind to: nothing here has unique
#    permissions, so a scan of every item in every library finds nothing while
#    the whole site is readable by every employee.
Write-Step 'Site level'
Grant-Claim -Scope 'the site (Read)' -Grant {
    $user = New-PnPUser -LoginName $everyoneExceptExternal -ErrorAction Stop
    Set-PnPWebPermission -User $user.LoginName -AddRole 'Read' -ErrorAction Stop
}

# 2. A library that already has unique permissions.
Write-Step "Library level: $Library"
Grant-Claim -Scope "$Library (Read)" -Grant {
    $list = Get-PnPList -Identity $Library -Includes HasUniqueRoleAssignments -ErrorAction Stop
    if (-not $list.HasUniqueRoleAssignments) {
        Set-PnPList -Identity $Library -BreakRoleInheritance -CopyRoleAssignments | Out-Null
    }
    $user = New-PnPUser -LoginName $everyoneExceptExternal -ErrorAction Stop
    Set-PnPListPermission -Identity $Library -User $user.LoginName -AddRole 'Read' -ErrorAction Stop
}

# 3. An item, with the wider claim. Modern people pickers hide "Everyone", so
#    finding one in a tenant nearly always means a legacy or scripted grant —
#    which is exactly the case worth detecting.
if ($IncludeEveryoneWithGuests) {
    Write-Step "Item level: $FolderPath"
    Grant-Claim -Scope "$FolderPath (Edit, includes guests)" -Grant {
        Resolve-PnPFolder -SiteRelativePath $FolderPath | Out-Null
        $folder = Get-PnPFolder -Url $FolderPath -Includes ListItemAllFields -ErrorAction Stop
        $user = New-PnPUser -LoginName $everyone -ErrorAction Stop
        Set-PnPListItemPermission -List ($FolderPath -split '/')[0] `
            -Identity $folder.ListItemAllFields.Id `
            -User $user.LoginName -AddRole 'Edit' -ClearExisting -ErrorAction Stop
    }
}
else {
    Write-Note 'Skipped the "Everyone" (with guests) grant. Pass -IncludeEveryoneWithGuests to add it.'
}

# ------------------------------------------------------------------- verify

# What the code has to match on, printed beside what a person would see. If the
# Title below is not English, that is the point being demonstrated: matching it
# would have found nothing.
Write-Step 'How this tenant stores and names these principals'

$claims = @($everyoneExceptExternal)
if ($IncludeEveryoneWithGuests) { $claims += $everyone }

foreach ($claim in $claims) {
    try {
        $resolved = Get-PnPUser | Where-Object { $_.LoginName -eq $claim } | Select-Object -First 1
        if ($resolved) {
            Write-Host ''
            Write-Host "    display name : $($resolved.Title)"
            Write-Host "    claim        : $($resolved.LoginName)"
            Write-Host "    principal id : $($resolved.Id)"
        }
        else {
            Write-Warning "    $claim is not in the site's user list"
        }
    }
    catch {
        Write-Warning "    could not read back $claim : $($_.Exception.Message)"
    }
}

# The shape the backend actually parses. GraphClient reads grantedToV2, and
# which facet a claim lands in — siteUser, user, siteGroup — decides which
# field carries the claim. TenantWideClaim.Classify is handed all of them, so
# this is a check rather than a dependency, but it is worth seeing.
Write-Step 'How Microsoft Graph reports the site level grant'

try {
    $host_ = ([uri]$SiteUrl).Host
    $path  = ([uri]$SiteUrl).AbsolutePath
    $site  = Invoke-PnPGraphMethod -Url "v1.0/sites/${host_}:${path}"

    # A list that inherits has exactly the site's permissions, which is how the
    # backend derives site level access without Full Control.
    $lists = Invoke-PnPGraphMethod -Url "v1.0/sites/$($site.id)/lists?`$select=id,displayName"
    $inheriting = $null

    foreach ($list in $lists.value) {
        $unique = (Get-PnPList -Identity $list.displayName -Includes HasUniqueRoleAssignments -ErrorAction SilentlyContinue)
        if ($unique -and -not $unique.HasUniqueRoleAssignments) { $inheriting = $list; break }
    }

    if (-not $inheriting) {
        Write-Warning '    every list has unique permissions, so site level access cannot be derived'
        Write-Warning '    the tool reports this as undetermined rather than as empty'
    }
    else {
        Write-Note "reading through '$($inheriting.displayName)', which inherits"
        $perms = Invoke-PnPGraphMethod -Url "v1.0/sites/$($site.id)/lists/$($inheriting.id)/permissions"

        foreach ($p in $perms.value) {
            $identity = $p.grantedToV2
            if (-not $identity) { $identity = $p.grantedTo }
            $json = ($identity | ConvertTo-Json -Depth 6 -Compress)
            if ($json -match 'spo-grid-all-users' -or $json -match 'c:0\(\.s') {
                Write-Host ''
                Write-Host '    FOUND the claim in the Graph response:' -ForegroundColor Green
                Write-Host "    roles    : $($p.roles -join ', ')"
                Write-Host "    identity : $json"
            }
        }
    }
}
catch {
    Write-Warning "    could not read the Graph view: $($_.Exception.Message)"
}

Write-Host ''
Write-Step 'Done'
Write-Note 'Open the site in Permission Insight. The site level grant shows above the tabs.'
