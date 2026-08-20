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
    az ad sp create --id $appId --query id -o tsv | Out-Null
    Write-Host '    created'
}
else {
    Write-Host '    already present'
}

if ($Repository) {
    Write-Host '==> Setting JP_API_CLIENT_ID on the repository' -ForegroundColor Cyan
    gh variable set JP_API_CLIENT_ID --body $appId --repo $Repository
}

Write-Host ''
Write-Host 'App registration ready.' -ForegroundColor Green
Write-Host ''
Write-Host "    application id : $appId"
Write-Host "    identifier URI : api://$appId"
Write-Host "    scope          : api://$appId/$ScopeName"
Write-Host ''
Write-Host 'Get a token and call the API:' -ForegroundColor Yellow
Write-Host "  `$t = az account get-access-token --scope api://$appId/$ScopeName --query accessToken -o tsv"
Write-Host '  curl -H "Authorization: Bearer $t" https://<api-fqdn>/api/v1/search-terms'
Write-Host ''

if (-not $Repository) {
    Write-Host 'Set JP_API_CLIENT_ID so CI deploys with authentication enabled:' -ForegroundColor Yellow
    Write-Host "  gh variable set JP_API_CLIENT_ID --body $appId"
    Write-Host ''
}
