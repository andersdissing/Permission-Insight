<#
.SYNOPSIS
    Deploys Permission Insight into an Azure subscription and an Entra tenant.

.DESCRIPTION
    Runs in this order, and each step explains itself as it goes:

      1. infra/main.bicep         Azure resources and the Entra objects.
      2. Static website           A data-plane setting ARM cannot express.
      3. infra/app-roles.bicep    The tenant-wide read grants. Privileged.
      4. Front end                Built, configured from the outputs, uploaded.
      5. Backend                  Published to the function app.

    Step 3 needs Privileged Role Administrator or Global Administrator. If the
    signed-in account is not privileged enough the script does not stop: it
    finishes the deployment, prints exactly what an administrator has to run,
    and exits non-zero so nobody reads it as success.

.NOTES
    No parameter carries a tenant-specific default. This file is published in a
    public repository.

    There is no client secret and no Key Vault anywhere in this deployment. If
    a change appears to need one, reread docs/01-architecture.md.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $Location,

    # Must sit under a domain the tenant has already verified.
    # {tenant}.onmicrosoft.com is verified everywhere, so it always works.
    # See docs/adr/0003-api-identifier-uri-is-a-parameter.md.
    [Parameter(Mandatory)] [string] $ApiIdentifierUri,

    [Parameter(Mandatory)] [string] $SharePointRootUrl,

    [string] $NamePrefix = 'perminsight',
    [string] $UsersGroupDisplayName = 'sg-permission-insight-users',

    # Skips step 3 when you already know you are not privileged enough, so the
    # script prints the instructions without attempting the deployment first.
    [switch] $SkipAppRoles,

    # Infrastructure only. Useful when iterating on Bicep.
    [switch] $SkipPublish,

    # Sites to grant the tool access to, so it can read their permissions.
    # Tenant-wide access is read-only and cannot see who has access to what;
    # each site is granted deliberately. See docs/adr/0010. Sites can also be
    # onboarded later with scripts/grant-site.ps1.
    [string[]] $GrantSites = @()
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot

function Write-Step { param($Message) Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Note { param($Message) Write-Host "    $Message" -ForegroundColor DarkGray }

function New-ContentSecurityPolicy {
    <#
      The full policy, built here because it has to name the API origin and
      that is a per-deployment value index.html cannot know. The file ships
      with a narrow placeholder; this replaces it at upload time.

      Every source is one the application demonstrably uses:

        'self'                      the app, its CSS, and /config.json
        login.microsoftonline.com   MSAL: token requests, the sign-in
                                    navigation, and the hidden renewal iframe
        login.windows.net           some tenants are redirected here
        the API origin              every data call

      script-src stays strict. Vite emits an external module script and no
      inline script, so no 'unsafe-inline' and no nonce is needed.

      style-src cannot. The tree indents rows and the progress bar sets its
      width through the style attribute, which CSP counts as inline style.
      Style injection is a far smaller problem than script injection, and it
      buys keeping script-src exact.

      frame-ancestors is deliberately absent: it is ignored in a meta tag.
      Framing is refused in main.tsx instead, which mounts nothing unless the
      page is top level. See docs/adr/0016.
    #>
    param([Parameter(Mandatory)] [string] $ApiBaseUrl)

    $apiOrigin = ([uri]$ApiBaseUrl).GetLeftPart([System.UriPartial]::Authority)
    $entra = 'https://login.microsoftonline.com https://login.windows.net'

    return @(
        "default-src 'self'"
        "script-src 'self'"
        "style-src 'self' 'unsafe-inline'"
        "img-src 'self' data:"
        "font-src 'self' data:"
        "object-src 'none'"
        "base-uri 'self'"
        "form-action 'self' $entra"
        "frame-src $entra"
        "connect-src 'self' $entra $apiOrigin"
    ) -join '; '
}

function Set-ContentSecurityPolicy {
    <#
      Replaces the placeholder meta tag in the built index.html.

      Fails loudly rather than silently shipping an unprotected page: if the
      tag is not found, the build has changed shape and somebody needs to look
      at it, which is not something to discover from a header scan months
      later.
    #>
    param(
        [Parameter(Mandatory)] [string] $IndexPath,
        [Parameter(Mandatory)] [string] $Policy
    )

    $html = Get-Content $IndexPath -Raw
    $pattern = '<meta http-equiv="Content-Security-Policy"[^>]*>'

    if ($html -notmatch $pattern) {
        throw "No Content-Security-Policy meta tag found in $IndexPath. web/index.html must carry one for the deployment to replace."
    }

    $tag = '<meta http-equiv="Content-Security-Policy" content="' + $Policy + '" />'
    Set-Content -Path $IndexPath -Value ([regex]::Replace($html, $pattern, $tag)) -NoNewline -Encoding utf8
}

# ------------------------------------------------------------- preflight

Write-Step 'Checking the signed-in account'

$account = az account show --output json 2>$null | ConvertFrom-Json
if (-not $account) {
    throw 'Not signed in. Run az login first.'
}

Write-Note "Subscription: $($account.name)"
Write-Note "Tenant:       $($account.tenantId)"

$deployerPrincipalId = az ad signed-in-user show --query id --output tsv 2>$null
if (-not $deployerPrincipalId) {
    Write-Warning 'Could not read the signed-in user. Blob uploads will need Storage Blob Data Contributor granted another way.'
    $deployerPrincipalId = ''
}

# ------------------------------------------------------ 1. core resources

Write-Step "Resource group $ResourceGroup"
az group create --name $ResourceGroup --location $Location --output none

Write-Step 'Deploying infra/main.bicep'

$deployment = az deployment group create `
    --resource-group $ResourceGroup `
    --name "permission-insight-$(Get-Date -Format 'yyyyMMddHHmmss')" `
    --template-file (Join-Path $repository 'infra/main.bicep') `
    --parameters `
        location=$Location `
        namePrefix=$NamePrefix `
        apiIdentifierUri=$ApiIdentifierUri `
        sharePointRootUrl=$SharePointRootUrl `
        usersGroupDisplayName=$UsersGroupDisplayName `
        deployerPrincipalId=$deployerPrincipalId `
    --output json | ConvertFrom-Json

if (-not $deployment) {
    throw 'The deployment produced no output.'
}

$out = $deployment.properties.outputs

$staticSiteUrl   = $out.staticSiteUrl.value
$apiBaseUrl      = $out.apiBaseUrl.value
$functionAppName = $out.functionAppName.value
$storageAccount  = $out.storageAccountName.value
$tenantId        = $out.tenantId.value
$clientId        = $out.clientId.value
$apiScope        = $out.apiScope.value
$identityId      = $out.backendIdentityPrincipalId.value
# The client id, not the principal id. Site grants name the application by its
# client id and the two are easy to confuse, which produces a grant that looks
# right and does nothing.
$backendClientId = $out.backendIdentityClientId.value
$consentUrl      = $out.adminConsentUrl.value

Write-Note "Static site:  $staticSiteUrl"
Write-Note "API:          $apiBaseUrl"

# ----------------------------------------------------- 2. static website

# Enabling static website hosting is a Set Blob Service Properties call.
# ARM cannot express it and Entra ID authorization does not support it, which
# is the whole reason this script exists rather than a bare az deployment.
# The 404 document is index.html so the SPA's History API routes survive a
# reload.
Write-Step 'Enabling the static website'

az storage blob service-properties update `
    --account-name $storageAccount `
    --static-website `
    --index-document index.html `
    --404-document index.html `
    --output none

# ---------------------------------------------------- 3. privileged half

$appRolesGranted = $false

if ($SkipAppRoles) {
    Write-Step 'Skipping the application role grants (-SkipAppRoles)'
}
else {
    Write-Step 'Deploying infra/app-roles.bicep (needs Privileged Role Administrator)'
    try {
        az deployment group create `
            --resource-group $ResourceGroup `
            --name "permission-insight-roles-$(Get-Date -Format 'yyyyMMddHHmmss')" `
            --template-file (Join-Path $repository 'infra/app-roles.bicep') `
            --parameters backendIdentityPrincipalId=$identityId `
            --output none

        if ($LASTEXITCODE -ne 0) {
            throw "az deployment exited with $LASTEXITCODE"
        }

        $appRolesGranted = $true
        Write-Note 'Granted Sites.Read.All, User.ReadBasic.All and SharePoint Sites.Read.All.'
    }
    catch {
        Write-Warning "The application role grants were refused: $($_.Exception.Message)"
    }
}

# --------------------------------------------------------- 4. front end

if (-not $SkipPublish) {
    Write-Step 'Building the front end'

    Push-Location (Join-Path $repository 'web')
    try {
        if (Test-Path 'package-lock.json') { npm ci } else { npm install }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'The front-end build failed.' }
    }
    finally {
        Pop-Location
    }

    # Written from the deployment outputs rather than baked in at build time,
    # so no tenant id or client id ever reaches the repository and one build
    # serves any tenant.
    $configPath = Join-Path $repository 'web/dist/config.json'
    [ordered]@{
        tenantId       = $tenantId
        clientId       = $clientId
        apiBaseUrl     = $apiBaseUrl
        apiScope       = $apiScope
        usersGroupName = $UsersGroupDisplayName
    } | ConvertTo-Json | Set-Content -Path $configPath -Encoding utf8

    # The API origin is only known now, so the real policy is written now. The
    # static website endpoint cannot send response headers at all, which is why
    # this is a meta tag rather than infrastructure. See docs/adr/0016.
    Write-Step 'Writing the content security policy'

    $policy = New-ContentSecurityPolicy -ApiBaseUrl $apiBaseUrl
    Set-ContentSecurityPolicy -IndexPath (Join-Path $repository 'web/dist/index.html') -Policy $policy
    Write-Note $policy

    Write-Step 'Uploading the front end'

    az storage blob upload-batch `
        --account-name $storageAccount `
        --destination '$web' `
        --source (Join-Path $repository 'web/dist') `
        --auth-mode login `
        --overwrite `
        --output none

    # upload-batch never deletes, and asset file names carry a content hash, so
    # every past build stays published and anonymously readable for ever. That
    # is not only waste: it keeps superseded versions of the application
    # serveable, including ones whose bugs have since been fixed. Remove
    # anything the current build did not produce.
    Write-Step 'Removing superseded assets'

    $current = Get-ChildItem (Join-Path $repository 'web/dist/assets') -File |
               ForEach-Object { "assets/$($_.Name)" }

    $published = az storage blob list `
        --account-name $storageAccount `
        --container-name '$web' `
        --prefix 'assets/' `
        --auth-mode login `
        --query "[].name" --output tsv

    $stale = @($published | Where-Object { $_ -and $current -notcontains $_ })

    foreach ($blob in $stale) {
        az storage blob delete --account-name $storageAccount --container-name '$web' `
            --name $blob --auth-mode login --output none
    }

    Write-Note "removed $($stale.Count) superseded file(s)"

    # ------------------------------------------------------- 5. backend

    Write-Step 'Publishing the backend'

    Push-Location (Join-Path $repository 'api/src/PermissionInsight.Api')
    try {
        # --dotnet-isolated is not optional. Core Tools infers the language
        # from local.settings.json, which is gitignored and absent on a clean
        # checkout, and then fails with "Worker runtime cannot be 'None'".
        func azure functionapp publish $functionAppName --dotnet-isolated
        if ($LASTEXITCODE -ne 0) { throw 'The backend publish failed.' }
    }
    finally {
        Pop-Location
    }
}

# ------------------------------------------------------ 6. site grants

if ($GrantSites.Count -gt 0) {
    Write-Step "Granting $($GrantSites.Count) site(s) to the tool"

    # A separate interactive sign-in. Granting site permissions needs delegated
    # Sites.FullControl.All, which the Azure CLI's token does not carry, and
    # which the application deliberately does not hold at all.
    & (Join-Path $PSScriptRoot 'grant-site.ps1') -SiteUrl $GrantSites -AppId $backendClientId

    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'Some site grants failed. The tool will say so on screen and print the command.'
    }
}

# ------------------------------------------------------------- summary

Write-Host ''
Write-Host '--------------------------------------------------------------'
Write-Host 'Permission Insight' -ForegroundColor Green
Write-Host '--------------------------------------------------------------'
Write-Host "Site:           $staticSiteUrl"
Write-Host "API:            $apiBaseUrl"
Write-Host "Users group:    $UsersGroupDisplayName"
Write-Host "Backend app id: $backendClientId"
Write-Host ''
Write-Host 'Nobody can sign in yet, including you. Access is membership of that'
Write-Host 'group, and the deployment adds no one to it:'
Write-Host ''
Write-Host "  az ad group member add --group $UsersGroupDisplayName --member-id <user object id>"
Write-Host ''
Write-Host 'Treat that membership as privileged. It is equivalent to read access'
Write-Host 'over every SharePoint site in the tenant.'
Write-Host ''
Write-Host 'No site needs to be granted or onboarded. Permissions are read through'
Write-Host 'Microsoft Graph, which accepts the read-only Sites.Read.All the'
Write-Host 'identity already holds; see docs/adr/0011 and docs/setup-guide.md.'
Write-Host ''
Write-Host "Optional, to pre-consent this application's own access_as_user"
Write-Host 'scope so nobody meets a consent prompt on first sign-in:'
Write-Host "  $consentUrl"
Write-Host ''
Write-Host 'That URL does not grant the managed identity anything. A managed'
Write-Host 'identity has no consent screen; see docs/adr/0004.'

if (-not $appRolesGranted) {
    Write-Host ''
    Write-Host '--------------------------------------------------------------'
    Write-Host 'NOT FINISHED: the backend cannot read SharePoint yet' -ForegroundColor Yellow
    Write-Host '--------------------------------------------------------------'
    Write-Host 'The managed identity has no application permissions, so every'
    Write-Host 'search will fail with 502. Ask someone holding Privileged Role'
    Write-Host 'Administrator or Global Administrator to run this once:'
    Write-Host ''
    Write-Host @"
  Connect-MgGraph -Scopes 'AppRoleAssignment.ReadWrite.All','Application.Read.All'

  `$identity = '$identityId'
  `$graph    = Get-MgServicePrincipal -Filter "appId eq '00000003-0000-0000-c000-000000000000'"
  `$sharePoint = Get-MgServicePrincipal -Filter "appId eq '00000003-0000-0ff1-ce00-000000000000'"

  foreach (`$name in 'Sites.Read.All', 'User.ReadBasic.All') {
      `$role = `$graph.AppRoles | Where-Object Value -eq `$name
      New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId `$identity ``
          -PrincipalId `$identity -ResourceId `$graph.Id -AppRoleId `$role.Id
  }

  `$role = `$sharePoint.AppRoles | Where-Object Value -eq 'Sites.Read.All'
  New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId `$identity ``
      -PrincipalId `$identity -ResourceId `$sharePoint.Id -AppRoleId `$role.Id
"@
    Write-Host ''
    Write-Host 'Or rerun this script as a privileged account. Everything else is'
    Write-Host 'already deployed, so it will only add the grants.'
    exit 1
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
