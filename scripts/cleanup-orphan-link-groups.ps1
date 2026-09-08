<#
.SYNOPSIS
    Reports, and optionally removes, surplus orphaned sharing link groups.

.DESCRIPTION
    Every sharing link creates a hidden site group named
    SharingLinks.{itemGuid}.{kind}.{linkGuid}. Deleting the shared item leaves
    the group behind with nothing to point at. That is a scenario the test data
    needs exactly one of, and earlier versions of provision-test-data.ps1
    created a fresh one on every run.

    A group is treated as orphaned when neither GetFileById nor GetFolderById
    resolves the item GUID in its name. Groups whose item still exists are
    never touched: removing one would revoke a live sharing link.

    Reports only unless -Remove is given.

.EXAMPLE
    ./cleanup-orphan-link-groups.ps1 -SiteUrl https://contoso.sharepoint.com/sites/pi-testdata

.EXAMPLE
    ./cleanup-orphan-link-groups.ps1 -SiteUrl https://contoso.sharepoint.com/sites/pi-testdata -Remove
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SiteUrl,

    # PnP.PowerShell 2.0 and later have no app registration of their own; run
    # Register-PnPEntraIDAppForInteractiveLogin once and pass the client id it
    # prints, or set PNPPOWERSHELL_CLIENTID.
    [string] $PnPClientId,

    # How many orphaned groups to leave in place. The test data calls for one.
    [int] $Keep = 1,

    [switch] $Remove
)

$ErrorActionPreference = 'Stop'

$clientId = $PnPClientId
if (-not $clientId) { $clientId = $env:PNPPOWERSHELL_CLIENTID }
if (-not $clientId) { $clientId = $env:ENTRAID_CLIENT_ID }
if (-not $clientId) {
    throw 'No client id. Pass -PnPClientId or set PNPPOWERSHELL_CLIENTID.'
}

Connect-PnPOnline -Url $SiteUrl -Interactive -ClientId $clientId

function Test-ItemExists {
    # The GUID in the group name is the item id. A live link resolves as a
    # file or, for a folder link, as a folder. Neither resolving is the
    # orphaned case.
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

$groups = @(Get-PnPGroup | Where-Object Title -like 'SharingLinks.*')

if (-not $groups) {
    Write-Host 'No SharingLinks.* groups on this site.'
    return
}

$examined = foreach ($g in $groups) {
    $itemGuid = $g.Title.Split('.')[1]
    [pscustomobject]@{
        Id       = $g.Id
        Title    = $g.Title
        ItemGuid = $itemGuid
        Orphaned = -not (Test-ItemExists -ItemGuid $itemGuid)
    }
}

$live    = @($examined | Where-Object { -not $_.Orphaned })
$orphans = @($examined | Where-Object { $_.Orphaned } | Sort-Object Id)

Write-Host ''
Write-Host "Live links:  $($live.Count)" -ForegroundColor Green
$live | ForEach-Object { Write-Host "    $($_.Id)  $($_.Title)" }

Write-Host ''
Write-Host "Orphaned:    $($orphans.Count)" -ForegroundColor Yellow
$orphans | ForEach-Object { Write-Host "    $($_.Id)  $($_.Title)" }

$surplus = @($orphans | Select-Object -Skip $Keep)

Write-Host ''
if (-not $surplus) {
    Write-Host "Nothing to remove; $Keep orphaned group(s) is what the test data wants."
    return
}

Write-Host "Surplus:     $($surplus.Count)  (keeping the $Keep oldest)" -ForegroundColor Yellow

if (-not $Remove) {
    Write-Host ''
    Write-Host 'Reporting only. Run again with -Remove to delete the surplus groups.'
    return
}

foreach ($s in $surplus) {
    Remove-PnPGroup -Identity $s.Id -Force
    Write-Host "    removed $($s.Id)  $($s.Title)"
}

Write-Host ''
Write-Host "Removed $($surplus.Count). $Keep orphaned group(s) left in place."
