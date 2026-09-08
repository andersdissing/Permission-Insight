<#
.SYNOPSIS
    Creates one orphaned sharing link group in the test site.

.DESCRIPTION
    An orphaned sharing link is a SharingLinks.* site group whose item no
    longer exists: somebody shared a file and then deleted it. The group
    survives, and if it still holds an external guest that is a finding — one
    Microsoft's own sharing reports do not show.

    It is the hardest scenario to produce by hand and the easiest to forget,
    so this makes it a single command. provision-test-data.ps1 creates one too;
    this exists so the scenario can be recreated without re-running a script
    that uploads twelve thousand files.

    Idempotent by default: it looks for an existing orphan first and does
    nothing if one is already there, because every extra orphan inflates the
    exact count the tool under test is meant to report.

.NOTES
    Requires PnP.PowerShell, and a client id for interactive sign-in — see
    docs/10-test-data.md.

    Reading the orphan back needs the SharePoint site groups endpoint, which an
    app-only read-only token cannot reach. The tool shows orphans only where
    the site has been granted; see docs/adr/0013.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $SiteUrl,

    [string] $PnPClientId,

    [string] $Library = 'Documents',

    [string] $FileName = 'to-be-deleted.txt',

    # Make another one even if the site already has an orphan.
    [switch] $Force
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

$webUrl = (Get-PnPWeb -Includes ServerRelativeUrl).ServerRelativeUrl

# An orphan is a SharingLinks group whose item id resolves to nothing. Same
# test as provision-test-data.ps1 and probe-verify.ps1 check 4.
function Test-ItemExists {
    param([string] $ItemGuid)

    foreach ($endpoint in 'GetFileById', 'GetFolderById') {
        try {
            Invoke-PnPSPRestMethod -Method Get `
                -Url "/_api/web/$endpoint('$ItemGuid')?`$select=ServerRelativeUrl" | Out-Null
            return $true
        }
        catch { }
    }

    return $false
}

Write-Step 'Looking for an existing orphan'

$existing = @(Get-PnPGroup | Where-Object Title -like 'SharingLinks.*' |
              Where-Object { -not (Test-ItemExists -ItemGuid $_.Title.Split('.')[1]) })

if ($existing -and -not $Force) {
    Write-Note "already present: $($existing[0].Title)"
    Write-Host ''
    Write-Host 'Nothing to do. Pass -Force to create another.' -ForegroundColor Green
    return
}

Write-Step "Creating one in $Library"

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) 'pi-orphan.txt'
'Deleted on purpose, so the sharing link behind it is orphaned.' |
    Set-Content -Path $scratch -Encoding UTF8

$libraryUrl = (Get-PnPList -Identity $Library -Includes RootFolder).RootFolder.ServerRelativeUrl
$fileUrl = "$libraryUrl/$FileName"

if (-not $PSCmdlet.ShouldProcess($fileUrl, 'Share then delete')) {
    return
}

Add-PnPFile -Path $scratch -Folder $libraryUrl.Substring($webUrl.Length).TrimStart('/') `
            -NewFileName $FileName | Out-Null
Write-Note "uploaded $fileUrl"

Add-PnPFileOrganizationalSharingLink -FileUrl $fileUrl -ShareType View | Out-Null
Write-Note 'shared with an organisation link'

Remove-PnPFile -ServerRelativeUrl $fileUrl -Force
Write-Note 'deleted the file'

# Until the recycle bin is emptied the item is only soft deleted. GetFileById
# already fails at that point, so the tool would report it as orphaned either
# way, but leaving it recoverable makes the scenario a half-truth.
Clear-PnPRecycleBinItem -All -Force -ErrorAction Continue
Write-Note 'emptied the recycle bin'

$orphans = @(Get-PnPGroup | Where-Object Title -like 'SharingLinks.*' |
             Where-Object { -not (Test-ItemExists -ItemGuid $_.Title.Split('.')[1]) })

Write-Host ''

if ($orphans) {
    Write-Host "Orphaned group left behind: $($orphans[-1].Title)" -ForegroundColor Green
    Write-Host ''
    Write-Host 'Permission Insight can only show this where the site has been'
    Write-Host 'granted: finding it means listing the site groups, which a'
    Write-Host 'read-only application token cannot do. See docs/adr/0013.'
}
else {
    Write-Warning 'No orphan resulted. The link may not have been created, or the file may not have been deleted.'
    exit 1
}
