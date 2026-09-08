<#
.SYNOPSIS
    Provisions a SharePoint test site for Permission Insight.

.DESCRIPTION
    Creates a site, four containers with different sizes, a nested folder
    structure, unique permissions and sharing links, plus two test users.

    The script is resumable. It checks what already exists and creates only
    what is missing, so an interrupted run can be started again.

    Creating 12,000 files means 12,000 uploads; Add-PnPFile cannot be batched.
    Expect 25 to 70 minutes for the Documents library alone.

.NOTES
    Requires PnP.PowerShell and Microsoft.Graph.Users.
    The operator needs SharePoint admin rights and User.ReadWrite.All.

    The site is a communication site by default, because that is created
    through SharePoint alone. -SiteType TeamSite is group-connected and so
    additionally needs delegated Group.ReadWrite.All, with admin consent, on
    the app registration passed as -PnPClientId.

    PnP.PowerShell 2.0 and later have no app registration of their own. Run
    Register-PnPEntraIDAppForInteractiveLogin once in the tenant and pass the
    client id it prints as -PnPClientId, or set PNPPOWERSHELL_CLIENTID.

    The script is resumable and detects what already exists, so an interrupted
    run can simply be started again.
    No parameter has a tenant-specific default. This file is published in a
    public repository.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TenantUrl,          # https://contoso.sharepoint.com
    [Parameter(Mandatory)] [string] $SiteAlias,          # pi-testdata
    [Parameter(Mandatory)] [string] $OwnerUpn,
    [Parameter(Mandatory)] [string] $UserDomain,         # contoso.onmicrosoft.com
    [Parameter(Mandatory)] [string] $ExternalGuestEmail, # for the specific-people link

    # PnP.PowerShell 2.x removed the multi-tenant app it used to sign in with,
    # so -Interactive needs an app registration in your own tenant. Either run
    # Register-PnPManagementShellAccess once, or pass its client id here.
    [string] $PnPClientId,

    # Nothing below this point depends on the site being group-connected, and
    # a communication site is created through SharePoint alone. A team site
    # goes through Microsoft Graph and needs a permission the interactive app
    # registration does not have by default.
    [ValidateSet('CommunicationSite', 'TeamSite')]
    [string] $SiteType = 'CommunicationSite',

    [int] $DocumentsTarget = 12000,
    [int] $FlatListTarget  = 7000,
    [int] $SmallLibTarget  = 500,
    [int] $MediumLibTarget = 1000,
    [switch] $SkipUsers
)

$ErrorActionPreference = 'Stop'
$siteUrl = "$TenantUrl/sites/$SiteAlias"
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) 'pi-testdata'
New-Item -ItemType Directory -Path $scratch -Force | Out-Null

function Write-Step { param($Message) Write-Host "==> $Message" -ForegroundColor Cyan }

function Connect-Site {
    param([string] $Url)

    $clientId = $PnPClientId
    if (-not $clientId) { $clientId = $env:PNPPOWERSHELL_CLIENTID }
    if (-not $clientId) { $clientId = $env:ENTRAID_CLIENT_ID }

    if (-not $clientId) {
        # PnP.PowerShell 2.0 removed the multi-tenant app it used to sign in
        # with, so -Interactive has nothing to authenticate as. Left to itself
        # it fails with "Specified method is not supported", which says
        # nothing about what to do next.
        throw @"
PnP.PowerShell $((Get-Module PnP.PowerShell).Version) has no app registration of its own, so an
interactive sign-in needs a client id from one in your tenant.

Create it once:

    Register-PnPEntraIDAppForInteractiveLogin ``
        -ApplicationName 'PnP PowerShell' ``
        -Tenant $UserDomain ``
        -Interactive

Then rerun this script with -PnPClientId <the id it prints>, or set
PNPPOWERSHELL_CLIENTID. Allow a minute or two after registering before the
app is usable.

Already created users and groups are detected and skipped, so rerunning is
safe.
"@
    }

    Connect-PnPOnline -Url $Url -Interactive -ClientId $clientId
}

function New-TestPassword {
    <#
      System.Web.Security.Membership.GeneratePassword is .NET Framework only
      and throws on PowerShell 7, which is where this script is most likely to
      be run. These accounts are throwaway and their passwords must be reset
      before use, but they still have to satisfy the tenant's complexity rules,
      so one character is taken from each required class before the rest is
      filled in and shuffled.
    #>
    $classes = @(
        'ABCDEFGHJKLMNPQRSTUVWXYZ',
        'abcdefghijkmnopqrstuvwxyz',
        '23456789',
        '!@#$%^&*-_'
    )

    $pool = -join $classes
    $characters = foreach ($class in $classes) {
        $class[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($class.Length)]
    }

    $characters += 1..16 | ForEach-Object {
        $pool[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($pool.Length)]
    }

    -join ($characters | Sort-Object { [System.Security.Cryptography.RandomNumberGenerator]::GetInt32(1000) })
}

# ---------------------------------------------------------------- test users

if (-not $SkipUsers) {
    Write-Step 'Test users'
    Connect-MgGraph -Scopes 'User.ReadWrite.All','Group.ReadWrite.All' -NoWelcome

    foreach ($u in @(
        @{ Nick = 'pi-test-alpha'; Name = 'PI Test Alpha' },
        @{ Nick = 'pi-test-beta';  Name = 'PI Test Beta'  }
    )) {
        $upn = "$($u.Nick)@$UserDomain"
        if (Get-MgUser -Filter "userPrincipalName eq '$upn'" -ErrorAction SilentlyContinue) {
            Write-Host "    exists: $upn"
            continue
        }

        # One password per account rather than one shared between them.
        $password = @{
            Password                      = New-TestPassword
            ForceChangePasswordNextSignIn = $false
        }

        New-MgUser -DisplayName $u.Name -MailNickname $u.Nick `
                   -UserPrincipalName $upn -AccountEnabled `
                   -PasswordProfile $password | Out-Null
        Write-Host "    created: $upn  (password generated and not shown, reset it before use)"
    }

    # Entra group containing beta only. Beta must reach content ONLY through
    # this group; that is what proves getUserEffectivePermissions is needed.
    $groupName = 'sg-pi-test-nested'
    $group = Get-MgGroup -Filter "displayName eq '$groupName'" -ErrorAction SilentlyContinue
    if (-not $group) {
        $group = New-MgGroup -DisplayName $groupName -MailEnabled:$false `
                             -MailNickname $groupName -SecurityEnabled
        $beta = Get-MgUser -Filter "userPrincipalName eq 'pi-test-beta@$UserDomain'"
        New-MgGroupMember -GroupId $group.Id -DirectoryObjectId $beta.Id
    }
}

# ---------------------------------------------------------------------- site

Write-Step 'Site'
Connect-Site -Url $TenantUrl
if (-not (Get-PnPTenantSite -Identity $siteUrl -ErrorAction SilentlyContinue)) {
    if ($SiteType -eq 'TeamSite') {
        # Creates an M365 group, so PnP calls Graph. A 403
        # Authorization_RequestDenied here is the app registration behind
        # -PnPClientId lacking delegated Group.ReadWrite.All, not the operator
        # lacking rights. Grant and consent to it, or use the default
        # -SiteType CommunicationSite.
        New-PnPSite -Type TeamSite -Title 'PI Test Data' -Alias $SiteAlias `
                    -Owners $OwnerUpn -Wait | Out-Null
    }
    else {
        New-PnPSite -Type CommunicationSite -Title 'PI Test Data' -Url $siteUrl `
                    -SiteDesign Topic -Owner $OwnerUpn -Wait | Out-Null
    }
}
Connect-Site -Url $siteUrl

# ----------------------------------------------------------------- libraries

function Ensure-Library {
    param([string] $Title)
    if (-not (Get-PnPList -Identity $Title -ErrorAction SilentlyContinue)) {
        New-PnPList -Title $Title -Template DocumentLibrary -OnQuickLaunch | Out-Null
    }
    Get-PnPList -Identity $Title -Includes ItemCount
}

Write-Step 'Containers'
$docs   = Ensure-Library -Title 'Documents'
$small  = Ensure-Library -Title 'SmallLib'
$medium = Ensure-Library -Title 'MediumLib'
$clean  = Ensure-Library -Title 'CleanLib'   # zero findings, for the empty state

if (-not (Get-PnPList -Identity 'FlatList' -ErrorAction SilentlyContinue)) {
    New-PnPList -Title 'FlatList' -Template GenericList -OnQuickLaunch | Out-Null
}

# ------------------------------------------------------- flat list, batched

Write-Step "FlatList: target $FlatListTarget"
$existing = (Get-PnPList -Identity 'FlatList' -Includes ItemCount).ItemCount
if ($existing -lt $FlatListTarget) {
    $batch = New-PnPBatch
    for ($i = $existing + 1; $i -le $FlatListTarget; $i++) {
        Add-PnPListItem -List 'FlatList' -Values @{ Title = "Record $i" } -Batch $batch | Out-Null
        if ($i % 500 -eq 0) {
            Invoke-PnPBatch -Batch $batch -Details
            $batch = New-PnPBatch
            Write-Host "    $i"
        }
    }
    Invoke-PnPBatch -Batch $batch -Details
}

# -------------------------------------------------- document library filling

$sampleFile = Join-Path $scratch 'sample.txt'
'Permission Insight test document.' | Set-Content -Path $sampleFile -Encoding UTF8

function Fill-Library {
    <#
      Builds a three-level folder tree and uploads files until Target is
      reached. Folders count toward the target. Resumable: skips anything
      already present.
    #>
    param([string] $Library, [int] $Target, [int] $Depth = 3)

    $current = (Get-PnPList -Identity $Library -Includes ItemCount).ItemCount
    if ($current -ge $Target) { Write-Host "    $Library already at $current"; return }

    $folders = @()
    for ($a = 1; $a -le 5; $a++) {
        $l1 = "$Library/Area $a"
        Resolve-PnPFolder -SiteRelativePath $l1 | Out-Null
        $folders += $l1
        if ($Depth -lt 2) { continue }
        for ($b = 1; $b -le 4; $b++) {
            $l2 = "$l1/Team $b"
            Resolve-PnPFolder -SiteRelativePath $l2 | Out-Null
            $folders += $l2
            if ($Depth -lt 3) { continue }
            for ($c = 1; $c -le 6; $c++) {
                $l3 = "$l2/Case $c"
                Resolve-PnPFolder -SiteRelativePath $l3 | Out-Null
                $folders += $l3
            }
        }
    }

    # Required special cases for the tree tests.
    Resolve-PnPFolder -SiteRelativePath "$Library/Empty folder" | Out-Null
    Resolve-PnPFolder -SiteRelativePath "$Library/Only subfolders/Child" | Out-Null
    Resolve-PnPFolder -SiteRelativePath "$Library/Deep/One/Two/Three/Four" | Out-Null
    $folders += "$Library/Deep/One/Two/Three/Four"
    $folders += "$Library/Bulk"
    Resolve-PnPFolder -SiteRelativePath "$Library/Bulk" | Out-Null

    $i = $current
    $n = 0
    while ($i -lt $Target) {
        # 'Bulk' deliberately gets 800 inheriting files so pruning is visible.
        $folder = if ($n -lt 800) { "$Library/Bulk" } else { $folders | Get-Random }
        $name   = "doc-$i.txt"
        try {
            Add-PnPFile -Path $sampleFile -Folder $folder -NewFileName $name | Out-Null
            $i++; $n++
            if ($i % 250 -eq 0) { Write-Host "    $Library $i / $Target" }
        }
        catch {
            Write-Warning "    retrying after: $($_.Exception.Message)"
            Start-Sleep -Seconds 20
        }
    }
}

Write-Step "Documents: target $DocumentsTarget"
Fill-Library -Library 'Documents' -Target $DocumentsTarget -Depth 3

Write-Step "SmallLib: target $SmallLibTarget"
Fill-Library -Library 'SmallLib' -Target $SmallLibTarget -Depth 2

Write-Step "MediumLib: target $MediumLibTarget"
Fill-Library -Library 'MediumLib' -Target $MediumLibTarget -Depth 2

Write-Step 'CleanLib: 40 files, no findings'
Fill-Library -Library 'CleanLib' -Target 40 -Depth 1

# --------------------------------------------------- permissions and sharing

Write-Step 'Unique permissions'

# A SharePoint group holding the nested Entra group. This is the path that
# only getUserEffectivePermissions can resolve.
$spGroupName = 'PI Nested Access'
if (-not (Get-PnPGroup -Identity $spGroupName -ErrorAction SilentlyContinue)) {
    New-PnPGroup -Title $spGroupName | Out-Null
    Add-PnPGroupMember -Group $spGroupName -LoginName 'sg-pi-test-nested' -ErrorAction Continue
}

$alpha = "pi-test-alpha@$UserDomain"

# Direct grant, no sharing link.
$f = Get-PnPFolder -Url "Documents/Area 1" -Includes ListItemAllFields
Set-PnPListItemPermission -List 'Documents' -Identity $f.ListItemAllFields.Id `
    -User $alpha -AddRole 'Contribute' -ClearExisting

# Direct grant to the group that nests the Entra group.
$g = Get-PnPFolder -Url "Documents/Area 2" -Includes ListItemAllFields
Set-PnPListItemPermission -List 'Documents' -Identity $g.ListItemAllFields.Id `
    -Group $spGroupName -AddRole 'Read' -ClearExisting

# Library-level break. Set-PnPListPermission only edits role assignments; it
# cannot create them on an object that still inherits, and says so. The list
# has to be made unique first. Copy the existing assignments while doing it,
# which is what the SharePoint interface does: the library then starts out
# identical to its parent and drifts from here, which is the case worth
# testing.
$smallLib = Get-PnPList -Identity 'SmallLib' -Includes HasUniqueRoleAssignments
if (-not $smallLib.HasUniqueRoleAssignments) {
    Set-PnPList -Identity 'SmallLib' -BreakRoleInheritance -CopyRoleAssignments | Out-Null
}
Set-PnPListPermission -Identity 'SmallLib' -User $alpha -AddRole 'Read'

Write-Step 'Sharing links'

# PnP 3.x has no single Add-PnPFileSharingLink. The scope is part of the cmdlet
# name, the role is -ShareType, and -FileUrl is server relative rather than the
# site relative paths the content above was created with.
$webUrl = (Get-PnPWeb -Includes ServerRelativeUrl).ServerRelativeUrl
function Get-ServerRelativeUrl {
    # Built from the web the folders were resolved against, so this holds
    # whether the library sits at /Documents or /Shared Documents.
    param([string] $SiteRelativePath)
    "$webUrl/$SiteRelativePath" -replace '/{2,}', '/'
}

# Fill-Library scatters doc-N.txt across folders at random, and puts the first
# 800 in Bulk regardless, so no doc-N.txt is at a predictable path. Each link
# scenario gets its own named file at a known location instead. Add-PnPFile
# overwrites, so this is safe to run again.
$scenarios = @{
    Anonymous    = 'Documents/Deep/One/Two/Three/Four/link-anonymous.txt'
    Organization = 'Documents/Area 3/link-organization.txt'
    People       = 'Documents/Area 4/link-specific-people.txt'
    Both         = 'Documents/Area 5/link-and-direct-grant.txt'
}
foreach ($path in $scenarios.Values) {
    Add-PnPFile -Path $sampleFile `
                -Folder (Split-Path $path -Parent).Replace('\', '/') `
                -NewFileName (Split-Path $path -Leaf) | Out-Null
}

# Both scenarios below depend on the site's sharing capability, which this
# script does not change: raising external sharing is the tenant owner's
# decision, not a side effect of provisioning test data. Record what could not
# be created and carry on, rather than failing the whole run.
$untestable = [System.Collections.Generic.List[object]]::new()

# Anonymous, and deliberately without an expiry.
try {
    Add-PnPFileAnonymousSharingLink -ShareType Edit `
        -FileUrl (Get-ServerRelativeUrl $scenarios.Anonymous) | Out-Null
}
catch {
    Write-Warning "Anyone link not created: $($_.Exception.Message)"
    $untestable.Add([pscustomobject]@{
        Scenario = "Anyone link on $($scenarios.Anonymous)"
        Reason   = $_.Exception.Message
    })
}

Add-PnPFileOrganizationalSharingLink -ShareType View `
    -FileUrl (Get-ServerRelativeUrl $scenarios.Organization) | Out-Null

# Specific people, to the external guest. This creates a link; it does not
# invite anyone. SharePoint resolves the address against the directory, so it
# fails while external sharing is off, and fails with a bare sharingFailed for
# an address that is not already a guest. The domain is irrelevant: a consumer
# address works once it is a guest, and a corporate one does not until it is.
# Invite the recipient first, or pass someone who is already a guest.
$peopleLinkCreated = $false
try {
    Add-PnPFileUserSharingLink -ShareType View -Users $ExternalGuestEmail `
        -FileUrl (Get-ServerRelativeUrl $scenarios.People) | Out-Null
    $peopleLinkCreated = $true
}
catch {
    Write-Warning "Specific people link not created: $($_.Exception.Message)"
    $untestable.Add([pscustomobject]@{
        Scenario = "Specific people link to $ExternalGuestEmail on $($scenarios.People)"
        Reason   = $_.Exception.Message
    })
}

# The test data calls for that link to expire, and Add-PnPFileUserSharingLink
# has no -ExpirationDateTime. Graph can set it on the permission afterwards.
# The site group is named SharingLinks.{item}.Flexible.{link}; scope and expiry
# live on the permission, not in the name.
if ($peopleLinkCreated) {
    try {
        $graphSite = Invoke-PnPGraphMethod -Url "v1.0/sites/$(([uri]$TenantUrl).Host):/sites/$SiteAlias"
        $drives    = Invoke-PnPGraphMethod -Url "v1.0/sites/$($graphSite.id)/drives?`$select=id,name"
        $drive     = $drives.value | Where-Object { $_.name -eq 'Documents' } | Select-Object -First 1

        $inDrive = $scenarios.People -replace '^Documents/', ''
        $encoded = ($inDrive -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'

        $perms = Invoke-PnPGraphMethod -Url "v1.0/drives/$($drive.id)/root:/$($encoded):/permissions"
        $link  = $perms.value | Where-Object { $_.link.scope -eq 'users' } | Select-Object -First 1

        Invoke-PnPGraphMethod -Method Patch `
            -Url "v1.0/drives/$($drive.id)/root:/$($encoded):/permissions/$($link.id)" `
            -Content @{
                expirationDateTime = (Get-Date).AddDays(27).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
            } | Out-Null

        Write-Host '    expiry set on the specific people link'
    }
    catch {
        Write-Warning "Could not set the expiry on the specific people link: $($_.Exception.Message)"
        Write-Warning 'Set it by hand; the test data calls for a link that expires.'
    }
}

# ------------------------------------------------- organisation-wide claims

# The findings with the widest reach and the quietest appearance. A grant to
# "Everyone except external users" opens the object to every employee, and it
# renders as one ordinary row with no members to enumerate.
#
# The display name is localised — a Danish tenant shows "Alle undtagen eksterne
# brugere" — so the tool matches on the claim, and this data exists to prove
# that it does. The claim is the invariant part.
Write-Step 'Organisation-wide claims'

# The claim for "Everyone except external users" carries the directory id, so
# it is never a fixed string. Get-PnPTenantId is missing from some PnP
# versions, and Graph answers the same question on every one of them.
$tenantId = $null
try { $tenantId = Get-PnPTenantId -ErrorAction Stop } catch { }

if (-not $tenantId) {
    $tenantId = (Invoke-PnPGraphMethod -Url 'v1.0/organization?$select=id').value[0].id
}

$everyoneExceptExternal = "c:0-.f|rolemanager|spo-grid-all-users/$tenantId"
$everyone               = 'c:0(.s|true'

function Grant-Claim {
    param(
        [Parameter(Mandatory)] [string] $Claim,
        [Parameter(Mandatory)] [string] $Scope,   # a description, for the log
        [Parameter(Mandatory)] [scriptblock] $Grant
    )

    try {
        & $Grant
        Write-Host "    granted on $Scope"
    }
    catch {
        Write-Warning "    could not grant on ${Scope}: $($_.Exception.Message)"
        $untestable.Add([pscustomobject]@{
            Scenario = "Organisation-wide claim on $Scope"
            Reason   = $_.Exception.Message
        })
    }
}

# 1. Site level. The case that matters most and the one the tool could not see
#    at all before: nothing here breaks inheritance, so an item scan finds
#    nothing while the entire site is open to the organisation.
Grant-Claim -Claim $everyoneExceptExternal -Scope 'the site (Read)' -Grant {
    $user = New-PnPUser -LoginName $everyoneExceptExternal -ErrorAction Stop
    Set-PnPWebPermission -User $user.LoginName -AddRole 'Read' -ErrorAction Stop
}

# 2. Library level, on a library that already has unique permissions.
Grant-Claim -Claim $everyoneExceptExternal -Scope 'MediumLib (Read)' -Grant {
    $medium = Get-PnPList -Identity 'MediumLib' -Includes HasUniqueRoleAssignments
    if (-not $medium.HasUniqueRoleAssignments) {
        Set-PnPList -Identity 'MediumLib' -BreakRoleInheritance -CopyRoleAssignments | Out-Null
    }
    $user = New-PnPUser -LoginName $everyoneExceptExternal -ErrorAction Stop
    Set-PnPListPermission -Identity 'MediumLib' -User $user.LoginName -AddRole 'Read' -ErrorAction Stop
}

# 3. Item level, and with the wider claim: "Everyone" includes guests. Modern
#    people pickers hide it, so its presence in a tenant is nearly always a
#    legacy or scripted grant — which is exactly why it is worth detecting.
Grant-Claim -Claim $everyone -Scope 'Documents/Area 5/Team 4 (Edit, includes guests)' -Grant {
    Resolve-PnPFolder -SiteRelativePath 'Documents/Area 5/Team 4' | Out-Null
    $folder = Get-PnPFolder -Url 'Documents/Area 5/Team 4' -Includes ListItemAllFields -ErrorAction Stop
    $user = New-PnPUser -LoginName $everyone -ErrorAction Stop
    Set-PnPListItemPermission -List 'Documents' -Identity $folder.ListItemAllFields.Id `
        -User $user.LoginName -AddRole 'Edit' -ClearExisting -ErrorAction Stop
}

# What the tenant actually calls these, printed so the localisation the tool
# has to survive is visible rather than assumed. On an English tenant these
# read as "Everyone except external users"; on this tenant they may not.
Write-Host ''
Write-Host '  claim principals as this tenant names them:'
foreach ($claim in @($everyoneExceptExternal, $everyone)) {
    try {
        $resolved = Get-PnPUser | Where-Object { $_.LoginName -eq $claim } | Select-Object -First 1
        if ($resolved) {
            Write-Host ("    {0,-46} -> {1}" -f $resolved.Title, $resolved.LoginName)
        }
    }
    catch {
        Write-Warning "    could not read back $claim"
    }
}

# Both a direct grant and a link on the same item.
$bothUrl = Get-ServerRelativeUrl $scenarios.Both
$both = Get-PnPFile -Url $bothUrl -AsListItem
Set-PnPListItemPermission -List 'Documents' -Identity $both.Id -User $alpha -AddRole 'Contribute'
Add-PnPFileOrganizationalSharingLink -FileUrl $bothUrl -ShareType Edit | Out-Null

Write-Step 'Orphaned sharing link group'

# Without a check this step shares and deletes a fresh file on every run and
# leaves another orphan behind each time, inflating the exact count the tool
# under test is meant to report. The site already records whether the scenario
# exists: an orphan is a SharingLinks group whose item id no longer resolves.
# Same test as cleanup-orphan-link-groups.ps1, and as probe-verify.ps1 check 4.
function Test-ItemExists {
    param([string] $ItemGuid)

    foreach ($api in 'GetFileById', 'GetFolderById') {
        try {
            Invoke-PnPSPRestMethod -Method Get `
                -Url "/_api/web/$api('$ItemGuid')?`$select=ServerRelativeUrl" | Out-Null
            return $true
        }
        catch { }
    }
    return $false
}

$orphans = @(Get-PnPGroup | Where-Object Title -like 'SharingLinks.*' |
             Where-Object { -not (Test-ItemExists -ItemGuid $_.Title.Split('.')[1]) })

if ($orphans) {
    Write-Host "    already present: $($orphans[0].Title)"
}
else {
    $doomed = Get-ServerRelativeUrl 'Documents/to-be-deleted.txt'
    Add-PnPFile -Path $sampleFile -Folder 'Documents' -NewFileName 'to-be-deleted.txt' | Out-Null
    Add-PnPFileOrganizationalSharingLink -FileUrl $doomed -ShareType View | Out-Null
    Remove-PnPFile -ServerRelativeUrl $doomed -Force
    Clear-PnPRecycleBinItem -All -Force -ErrorAction Continue
}

Write-Step 'Done'
Write-Host ""
Write-Host "Site:            $siteUrl"
Write-Host "Sharing groups:  $((Get-PnPGroup | Where-Object Title -like 'SharingLinks.*').Count)"

if ($untestable.Count) {
    Write-Host ""
    Write-Host "Not created:"
    foreach ($u in $untestable) {
        Write-Host "  - $($u.Scenario)"
        Write-Host "      $($u.Reason)" -ForegroundColor DarkGray
    }
    Write-Host ""
    Write-Host "These are required scenarios in docs/10-test-data.md. Check in order:"
    Write-Host ""
    Write-Host "  1. Sharing capability. A site cannot exceed the tenant, so if you"
    Write-Host "     raise the tenant you have to set the site afterwards, and read it"
    Write-Host "     back rather than trusting a silent command."
    Write-Host "       (Get-PnPTenant).SharingCapability"
    Write-Host "       (Get-PnPTenantSite -Identity $siteUrl -Detailed).SharingCapability"
    Write-Host ""
    Write-Host "  2. Whether the recipient is already a guest. A link cannot be made to"
    Write-Host "     an address the directory has never seen; invite them first."
    Write-Host ""
    Write-Host "  3. Time. sharingFailed immediately after external sharing is turned on"
    Write-Host "     usually means the change has not reached the sharing service yet."
    Write-Host ""
    Write-Host "This script changes none of them. Opening up external sharing and"
    Write-Host "inviting guests are decisions for the tenant, not side effects of"
    Write-Host "building test data. Rerun once fixed; the rest is detected and skipped."
}


Write-Host ""
Write-Host "Reset the generated passwords for pi-test-alpha and pi-test-beta before use."
Write-Host "Confirm pi-test-beta has NO direct assignment anywhere; its access must"
Write-Host "arrive only through sg-pi-test-nested inside '$spGroupName'."
