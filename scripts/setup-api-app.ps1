<#
.SYNOPSIS
    Creates the Microsoft Entra app registration the API validates tokens against.

.DESCRIPTION
    The API is a protected resource: it accepts Entra bearer tokens and authorises them
    against a scope. That needs an app registration, which has no ARM representation, so it
    cannot live in Bicep alongside the rest of the stack.

    Creates or updates:
      - an application named <Name>, single-tenant
      - the identifier URI api://<appId>
      - one delegated scope, Jobs.Read
      - a pre-authorisation for the Azure CLI, so `az account get-access-token` returns a
        usable token without an interactive consent prompt
      - the matching service principal

    Reads no secrets and creates none. The API validates tokens with public metadata, and
    nothing here issues a client secret or certificate.

    Idempotent: re-running updates the existing registration rather than creating a second.

.EXAMPLE
    ./scripts/setup-api-app.ps1 -Repository pa741/job-platform
#>
[CmdletBinding()]
param(
    [string]$Name = 'job-platform-api',

    [string]$ScopeName = 'Jobs.Read',

    [string]$WebName = 'job-platform-web',

    # Origins the dashboard is served from. localhost is for `npm run dev`; add the Static
    # Web App URL once it exists (the provisioning output prints it).
    [string[]]$WebRedirectUris = @('http://localhost:5173'),

    # Set the JP_API_CLIENT_ID variable on this repository when supplied, so CI deploys the
    # API with authentication configured.
    [string]$Repository
)

$ErrorActionPreference = 'Stop'

# The Azure CLI's own first-party application id. Fixed and identical in every tenant.
$azureCliAppId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'

Write-Host '==> Looking for an existing registration' -ForegroundColor Cyan
$appId = az ad app list --display-name $Name --query '[0].appId' -o tsv

if (-not $appId) {
    Write-Host "    creating '$Name'"
    $appId = az ad app create --display-name $Name --sign-in-audience AzureADMyOrg --query appId -o tsv
    if (-not $appId) { throw "Could not create the application '$Name'." }
}
else {
    Write-Host "    reusing $appId"
}

$objectId = az ad app show --id $appId --query id -o tsv

# Reuse the existing scope id if there is one. Changing it would invalidate every consent
# already granted against this API.
$scopeId = az ad app show --id $appId `
    --query "api.oauth2PermissionScopes[?value=='$ScopeName'].id | [0]" -o tsv

if (-not $scopeId) { $scopeId = [guid]::NewGuid().ToString() }

Write-Host '==> Configuring the identifier URI and scope' -ForegroundColor Cyan

# Two passes, deliberately. Microsoft Graph validates preAuthorizedApplications against the
# scopes it already has stored, so a scope and a pre-authorisation referencing it cannot be
# written in the same request - the first attempt fails with "has a Permission Id that cannot
# be found in the AppPermissions sets".
$scopePatch = @{
    identifierUris = @("api://$appId")
    api            = @{
        # Version 2 tokens, matching the v2.0 issuer Microsoft.Identity.Web expects from the
        # Instance and TenantId settings the container app is configured with.
        requestedAccessTokenVersion = 2
        oauth2PermissionScopes      = @(
            @{
                id                      = $scopeId
                value                   = $ScopeName
                type                    = 'User'
                isEnabled               = $true
                adminConsentDisplayName = 'Read job-platform data'
                adminConsentDescription = 'Allows the app to read job postings and market metrics, and to run CV matching on behalf of the signed-in user.'
                userConsentDisplayName  = 'Read job market data'
                userConsentDescription  = 'Lets the app read job postings and market metrics, and match your CV against them.'
            }
        )
    }
} | ConvertTo-Json -Depth 10 -Compress

$scopeFile = New-TemporaryFile
try {
    Set-Content -Path $scopeFile -Value $scopePatch -Encoding utf8
    az rest --method PATCH --url "https://graph.microsoft.com/v1.0/applications/$objectId" `
        --headers 'Content-Type=application/json' --body "@$scopeFile" | Out-Null
}
finally {
    Remove-Item $scopeFile -ErrorAction SilentlyContinue
}

Write-Host '==> Pre-authorising the Azure CLI for that scope' -ForegroundColor Cyan
$preAuthPatch = @{
    api = @{
        preAuthorizedApplications = @(
            @{ appId = $azureCliAppId; delegatedPermissionIds = @($scopeId) }
        )
    }
} | ConvertTo-Json -Depth 10 -Compress

$preAuthFile = New-TemporaryFile
try {
    Set-Content -Path $preAuthFile -Value $preAuthPatch -Encoding utf8
    az rest --method PATCH --url "https://graph.microsoft.com/v1.0/applications/$objectId" `
        --headers 'Content-Type=application/json' --body "@$preAuthFile" | Out-Null
}
finally {
    Remove-Item $preAuthFile -ErrorAction SilentlyContinue
}

# Tokens are issued to the service principal, not to the application object, so without this
# every request for the API's scope fails even though the registration looks complete.
Write-Host '==> Ensuring the service principal exists' -ForegroundColor Cyan
$spId = az ad sp list --filter "appId eq '$appId'" --query '[0].id' -o tsv
if (-not $spId) {
    $spId = az ad sp create --id $appId --query id -o tsv
    Write-Host '    created'
}
else {
    Write-Host '    already present'
}

# --- The dashboard's own registration -------------------------------------------------
#
# A separate application from the API, deliberately. The API is a protected resource; the
# dashboard is a public client that asks for access to it. Collapsing them into one
# registration would mean the thing validating tokens and the thing requesting them share an
# identity, which makes the audience check meaningless.
Write-Host '==> Configuring the dashboard registration' -ForegroundColor Cyan

$webAppId = az ad app list --display-name $WebName --query '[0].appId' -o tsv
if (-not $webAppId) {
    $webAppId = az ad app create --display-name $WebName --sign-in-audience AzureADMyOrg --query appId -o tsv
    if (-not $webAppId) { throw "Could not create the application '$WebName'." }
    Write-Host "    created $webAppId"
}
else {
    Write-Host "    reusing $webAppId"
}

$webObjectId = az ad app show --id $webAppId --query id -o tsv

# The `spa` platform, not `web`: it is what enables the authorisation-code flow with PKCE,
# which is the only flow a browser app may use. Registering the URIs under `web` instead
# would hand back an implicit-flow token, or nothing at all.
$webPatch = @{
    spa                    = @{ redirectUris = $WebRedirectUris }
    requiredResourceAccess = @(
        @{
            resourceAppId  = $appId
            resourceAccess = @(@{ id = $scopeId; type = 'Scope' })
        }
    )
} | ConvertTo-Json -Depth 10 -Compress

$webFile = New-TemporaryFile
try {
    Set-Content -Path $webFile -Value $webPatch -Encoding utf8
    az rest --method PATCH --url "https://graph.microsoft.com/v1.0/applications/$webObjectId" `
        --headers 'Content-Type=application/json' --body "@$webFile" | Out-Null
}
finally {
    Remove-Item $webFile -ErrorAction SilentlyContinue
}

$webSpId = az ad sp list --filter "appId eq '$webAppId'" --query '[0].id' -o tsv
if (-not $webSpId) {
    $webSpId = az ad sp create --id $webAppId --query id -o tsv
}

# Consent granted for the tenant so no user meets a permission prompt. Written through
# Graph rather than `az ad app permission admin-consent`, which goes via the legacy AAD
# Graph and fails against a service principal created moments earlier.
$existingGrant = az rest --method GET `
    --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants?`$filter=clientId eq '$webSpId'" `
    --query "value[?resourceId=='$spId'].id | [0]" -o tsv 2>$null

if (-not $existingGrant) {
    $grant = @{
        clientId    = $webSpId
        consentType = 'AllPrincipals'
        resourceId  = $spId
        scope       = $ScopeName
    } | ConvertTo-Json -Compress

    $grantFile = New-TemporaryFile
    try {
        Set-Content -Path $grantFile -Value $grant -Encoding utf8
        az rest --method POST --url 'https://graph.microsoft.com/v1.0/oauth2PermissionGrants' `
            --headers 'Content-Type=application/json' --body "@$grantFile" | Out-Null
        Write-Host '    granted tenant-wide consent'
    }
    finally {
        Remove-Item $grantFile -ErrorAction SilentlyContinue
    }
}
else {
    Write-Host '    consent already granted'
}

if ($Repository) {
    Write-Host '==> Setting repository variables' -ForegroundColor Cyan
    gh variable set JP_API_CLIENT_ID --body $appId --repo $Repository
    gh variable set JP_WEB_CLIENT_ID --body $webAppId --repo $Repository
}

Write-Host ''
Write-Host 'App registration ready.' -ForegroundColor Green
Write-Host ''
Write-Host "    application id : $appId"
Write-Host "    identifier URI : api://$appId"
Write-Host "    scope          : api://$appId/$ScopeName"
Write-Host "    dashboard app  : $webAppId"
Write-Host "    redirect URIs  : $($WebRedirectUris -join ', ')"
Write-Host ''
Write-Host 'Get a token and call the API:' -ForegroundColor Yellow
Write-Host "  `$t = az account get-access-token --scope api://$appId/$ScopeName --query accessToken -o tsv"
Write-Host '  curl -H "Authorization: Bearer $t" https://<api-fqdn>/api/v1/search-terms'
Write-Host ''

if (-not $Repository) {
    Write-Host 'Set these so CI deploys with authentication enabled:' -ForegroundColor Yellow
    Write-Host "  gh variable set JP_API_CLIENT_ID --body $appId"
    Write-Host "  gh variable set JP_WEB_CLIENT_ID --body $webAppId"
    Write-Host ''
}
