<#
.SYNOPSIS
    Answers the VERIFY items in the specification against a real tenant.

.DESCRIPTION
    Several endpoints this application depends on are uncertain enough that
    building on them without checking would be inventing an API shape. This
    script asks the tenant instead, and prints answers to record in the
    documents.

    It checks:

      1. Does Sites.Read.All permit reading item roleassignments?
         Blocks phase 3. There is no escalation if it fails:
         Sites.FullControl.All is ruled out, so phase 3 would report that
         inheritance is broken without saying who has access. See
         docs/adr/0009-no-full-control-escalation.md.

      2. Does it permit getusereffectivepermissions?
         Blocks phase 5, which is built entirely on this call.

      3. Does Graph /sites?search honour $select and $top?
         Phase 1 sends neither, on the assumption that it might not. If both
         work, GraphClient can stop over-fetching.

      4. Does the GUID in a SharingLinks group name resolve through
         GetFileById? Blocks phase 2.

      5. Can a SharingLinks group be matched to its Graph permission, and by
         which of the three identifications SharingLinksFunction.MatchLink
         tries? If branch 1 always wins, branches 2 and 3 can be deleted. If
         none does, rows resolve as ScopeUnavailable and phase 2 needs another
         way to tell one link on an item from another.

    Question 1 and 2 are about an APPLICATION permission. A delegated
    connection answers what the endpoint returns but not whether app-only
    Sites.Read.All is enough, which is the part that decides the permission
    model. Supply -ClientId and a certificate to get the answer that counts.

.NOTES
    Requires PnP.PowerShell. Use the test tenant from docs/10-test-data.md.
    No parameter has a tenant-specific default.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SiteUrl,

    # A library on that site holding at least one item with unique
    # permissions. provision-test-data.ps1 breaks inheritance in Documents.
    [string] $LibraryTitle = 'Documents',

    # The user to ask getusereffectivepermissions about, for example
    # pi-test-beta@yourtenant.onmicrosoft.com.
    [Parameter(Mandatory)] [string] $UserPrincipalName,

    # App-only. Without these the script connects interactively and can only
    # answer the shape of each response, not the permission question.
    [string] $ClientId,
    [string] $Tenant,
    [string] $CertificateThumbprint,

    # For the interactive connection. PnP.PowerShell 2.0 and later have no app
    # registration of their own; run Register-PnPEntraIDAppForInteractiveLogin
    # once and pass the client id it prints, or set PNPPOWERSHELL_CLIENTID.
    [string] $PnPClientId
)

$ErrorActionPreference = 'Stop'

$results = [ordered]@{}

function Write-Check {
    param([string] $Name, [bool] $Passed, [string] $Detail)

    $results[$Name] = @{ passed = $Passed; detail = $Detail }

    $label = if ($Passed) { 'PASS' } else { 'FAIL' }
    $colour = if ($Passed) { 'Green' } else { 'Red' }

    Write-Host ("  [{0}] {1}" -f $label, $Name) -ForegroundColor $colour
    if ($Detail) { Write-Host "         $Detail" -ForegroundColor DarkGray }
}

# ------------------------------------------------------------- connect

$appOnly = -not [string]::IsNullOrWhiteSpace($ClientId)

if ($appOnly) {
    Write-Host "Connecting app-only as $ClientId" -ForegroundColor Cyan
    Connect-PnPOnline -Url $SiteUrl -ClientId $ClientId -Tenant $Tenant `
                      -Thumbprint $CertificateThumbprint
}
else {
    Write-Warning 'Connecting interactively. This answers what each endpoint returns, but NOT whether app-only Sites.Read.All is sufficient, which is the question that decides the permission model. Rerun with -ClientId, -Tenant and -CertificateThumbprint before treating checks 1 and 2 as answered.'

    $interactiveClientId = $PnPClientId
    if (-not $interactiveClientId) { $interactiveClientId = $env:PNPPOWERSHELL_CLIENTID }
    if (-not $interactiveClientId) { $interactiveClientId = $env:ENTRAID_CLIENT_ID }

    if (-not $interactiveClientId) {
        throw @"
PnP.PowerShell has no app registration of its own, so an interactive sign-in
needs a client id from one in your tenant.

Create it once:

    Register-PnPEntraIDAppForInteractiveLogin ``
        -ApplicationName 'PnP PowerShell' ``
        -Tenant <tenant>.onmicrosoft.com ``
        -Interactive

Then rerun with -PnPClientId <the id it prints>, or set PNPPOWERSHELL_CLIENTID.
"@
    }

    Connect-PnPOnline -Url $SiteUrl -Interactive -ClientId $interactiveClientId
}

# Graph addresses a site as {hostname}:{server-relative-path}, never as a full
# URL. Getting this wrong returns a 400 that reads like a permission problem.
$siteUri = [uri] $SiteUrl
$graphSiteId = "$($siteUri.Host):$($siteUri.AbsolutePath)"

Write-Host ''

# ------------------------------- 1. roleassignments on a broken item

Write-Host '1. roleassignments on an item with unique permissions' -ForegroundColor Cyan

$list = Get-PnPList -Identity $LibraryTitle
$listId = $list.Id

$broken = Invoke-PnPSPRestMethod -Method Get -Url (
    "/_api/web/lists(guid'$listId')/items" +
    '?$select=ID,HasUniqueRoleAssignments&$filter=HasUniqueRoleAssignments eq true&$top=1')

$brokenItemId = $broken.value | Select-Object -First 1 -ExpandProperty ID

if (-not $brokenItemId) {
    Write-Check 'roleassignments' $false "No item in '$LibraryTitle' has unique permissions. Run provision-test-data.ps1 first."
}
else {
    try {
        $assignments = Invoke-PnPSPRestMethod -Method Get -Url (
            "/_api/web/lists(guid'$listId')/items($brokenItemId)/roleassignments" +
            '?$expand=Member,RoleDefinitionBindings')

        $count = @($assignments.value).Count
        $roles = $assignments.value.RoleDefinitionBindings.Name | Sort-Object -Unique

        Write-Check 'roleassignments' $true "Item $brokenItemId returned $count assignments. Roles seen: $($roles -join ', ')."

        if ($roles -contains 'Limited Access') {
            Write-Host '         Limited Access is present, as expected. It is filtered out everywhere.' -ForegroundColor DarkGray
        }
    }
    catch {
        Write-Check 'roleassignments' $false "$($_.Exception.Message) -- phase 3 cannot report WHO has access. It can still report THAT inheritance is broken, from HasUniqueRoleAssignments. Sites.FullControl.All is not an option; see docs/adr/0009."
    }
}

# ----------------------------- 2. getusereffectivepermissions

Write-Host ''
Write-Host '2. getusereffectivepermissions' -ForegroundColor Cyan

$loginName = [uri]::EscapeDataString("i:0#.f|membership|$UserPrincipalName")

try {
    $effective = Invoke-PnPSPRestMethod -Method Get -Url (
        "/_api/web/getusereffectivepermissions(@u)?@u='$loginName'")

    $mask = "$($effective.High).$($effective.Low)"
    Write-Check 'getusereffectivepermissions' $true "Returned a permission mask ($mask) for $UserPrincipalName."
}
catch {
    Write-Check 'getusereffectivepermissions' $false "$($_.Exception.Message) -- phase 5 is built on this call and has no fallback that does not expand groups client-side."
}

# ---------------------------------- 3. Graph /sites?search shape

Write-Host ''
Write-Host '3. Graph /sites?search with $select and $top' -ForegroundColor Cyan

try {
    $plain = Invoke-PnPGraphMethod -Url 'v1.0/sites?search=a' -Method Get
    $properties = ($plain.value | Select-Object -First 1).PSObject.Properties.Name
    Write-Check 'sites-search' $true "Returned $(@($plain.value).Count) sites. Properties: $($properties -join ', ')."

    try {
        $shaped = Invoke-PnPGraphMethod -Method Get `
            -Url 'v1.0/sites?search=a&$select=id,displayName,webUrl&$top=3'
        Write-Check 'sites-search-select-top' $true "Accepted, returned $(@($shaped.value).Count) sites. GraphClient can stop over-fetching."
    }
    catch {
        Write-Check 'sites-search-select-top' $false "Rejected: $($_.Exception.Message). Leave GraphClient sending search only."
    }
}
catch {
    # The interactive app registration needs delegated Sites.Read.All on
    # Microsoft Graph for this. Checks 1 and 2, which are the ones that block
    # phases 3 and 5, use SharePoint REST and are unaffected.
    $hint = if ($_.Exception.Message -match '403|Forbidden|Authorization_RequestDenied') {
        "$($_.Exception.Message) -- the app registration behind the connection lacks delegated Sites.Read.All on Microsoft Graph. Checks 1 and 2 do not need it."
    }
    else { $_.Exception.Message }

    Write-Check 'sites-search' $false $hint
}

# ------------------------------------- 4. GetFileById on a link GUID

Write-Host ''
Write-Host '4. SharingLinks group GUID resolves through GetFileById' -ForegroundColor Cyan

$sharingGroup = Get-PnPGroup | Where-Object Title -like 'SharingLinks.*' | Select-Object -First 1

if (-not $sharingGroup) {
    Write-Check 'getfilebyid' $false 'No SharingLinks.* group on this site. Share a file first, or run provision-test-data.ps1.'
}
else {
    # SharingLinks.{itemGuid}.{kind}.{linkGuid}
    $itemGuid = $sharingGroup.Title.Split('.')[1]

    try {
        $file = Invoke-PnPSPRestMethod -Method Get -Url (
            "/_api/web/GetFileById('$itemGuid')?`$select=ServerRelativeUrl")
        Write-Check 'getfilebyid' $true "Resolved to $($file.ServerRelativeUrl)."

        # ------------ 5. does a Graph permission id carry the link GUID?

        Write-Host ''
        Write-Host '5. Matching a SharingLinks group to its Graph permission' -ForegroundColor Cyan

        # SharingLinks.{itemGuid}.{kind}.{linkGuid}
        $linkGuid = $sharingGroup.Title.Split('.')[3]
        $path = $file.ServerRelativeUrl

        try {
            $drives = Invoke-PnPGraphMethod -Method Get -Url "v1.0/sites/$graphSiteId/drives?`$select=id,webUrl"

            $drive = $drives.value |
                Where-Object { $path.StartsWith(([uri]$_.webUrl).AbsolutePath.Replace('%20',' ') + '/') } |
                Sort-Object { $_.webUrl.Length } -Descending |
                Select-Object -First 1

            if (-not $drive) {
                Write-Check 'permission-match' $false "No library covers $path. The backend reports ScopeUnavailable for this row."
            }
            else {
                $inDrive = $path.Substring(([uri]$drive.webUrl).AbsolutePath.Replace('%20',' ').Length).TrimStart('/')
                $encoded = ($inDrive -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'

                $permissions = Invoke-PnPGraphMethod -Method Get `
                    -Url "v1.0/drives/$($drive.id)/root:/$($encoded):/permissions"

                $links = @($permissions.value | Where-Object { $_.link })

                $byId       = $links | Where-Object { $_.id -eq $linkGuid }
                $byContains = $links | Where-Object { $_.id -like "*$linkGuid*" }

                $branch =
                    if ($byId)              { '1 (permission id equals the link GUID)' }
                    elseif ($byContains)    { '2 (permission id embeds the link GUID)' }
                    elseif ($links.Count -eq 1) { '3 (only one link on the item)' }
                    else                    { 'none -- the row resolves as ScopeUnavailable' }

                Write-Check 'permission-match' ($branch -notlike 'none*') `
                    "$($links.Count) link permission(s) on the item. MatchLink branch: $branch."

                if ($links.Count -gt 0) {
                    Write-Host "         Scope: $($links[0].link.scope), roles: $($links[0].roles -join ','), hasPassword: $($links[0].hasPassword)" -ForegroundColor DarkGray
                }
            }
        }
        catch {
            Write-Check 'permission-match' $false $_.Exception.Message
        }
    }
    catch {
        try {
            $folder = Invoke-PnPSPRestMethod -Method Get -Url (
                "/_api/web/GetFolderById('$itemGuid')?`$select=ServerRelativeUrl")
            Write-Check 'getfilebyid' $true "Not a file, resolved as a folder: $($folder.ServerRelativeUrl). Phase 2 must try both."
        }
        catch {
            Write-Check 'getfilebyid' $false 'Neither GetFileById nor GetFolderById resolved the GUID. Either the item is deleted, which is the orphaned case, or the GUID in the group name is not the item id.'
        }
    }
}

# ------------------------------------------------------------ summary

Write-Host ''
Write-Host '--------------------------------------------------------------'

$failed = $results.GetEnumerator() | Where-Object { -not $_.Value.passed }

if ($failed) {
    Write-Host 'Some checks failed. Record the answers before building on them:' -ForegroundColor Yellow
    $failed | ForEach-Object { Write-Host "  - $($_.Key)" }
}
else {
    Write-Host 'All checks passed.' -ForegroundColor Green
}

if (-not $appOnly) {
    Write-Host ''
    Write-Warning 'Delegated connection: checks 1 and 2 are not yet answered for the permission model. Rerun app-only.'
}

Write-Host ''
Write-Host 'Record the answers in docs/phase-1-deployment-and-search.md, under'
Write-Host '"Verify before phase 3".'
