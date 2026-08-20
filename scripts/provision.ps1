<#
.SYNOPSIS
    Provisions the job-platform ingestion stack and completes the steps Bicep cannot.

.DESCRIPTION
    Registers the required resource providers, deploys infra/main.bicep, then performs the
    two post-deploy steps that have no ARM equivalent:

      1. Applying the EF Core schema.
      2. Mapping the ingest managed identity to a database user
         (CREATE USER ... FROM EXTERNAL PROVIDER runs inside the database, so the
         control plane cannot do it).

    Reads no secrets and writes none. Authentication is your own `az login`; the
    identifiers it needs come from the CLI or from -Parameters you pass explicitly.

.EXAMPLE
    ./scripts/provision.ps1 -ResourceGroup job-platform -LandingStorageAccount mystorage
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ResourceGroup,

    [Parameter(Mandatory)]
    [string]$LandingStorageAccount,

    [string]$NamePrefix = 'jobplatform',
    [string]$Location = 'spaincentral',
    [string]$LandingContainer = 'jobs-landing',
    [string]$DeploymentName = 'jobplatform-ingest',

    [ValidateSet('keyword', 'anthropic')]
    [string]$MatchingProvider = 'keyword',

    [string]$ApiClientId = '',
    [string[]]$ApiAllowedOrigins = @(),

    [switch]$SkipProviderRegistration,
    [switch]$SkipMigrations
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host '==> Resolving your Entra identity' -ForegroundColor Cyan
$adminObjectId = az ad signed-in-user show --query id -o tsv
$adminLoginName = az ad signed-in-user show --query userPrincipalName -o tsv
$tenantId = az account show --query tenantId -o tsv

if (-not $adminObjectId) { throw 'Could not resolve the signed-in user. Run `az login` first.' }

if (-not $SkipProviderRegistration) {
    Write-Host '==> Registering resource providers' -ForegroundColor Cyan
    $providers = @(
        'Microsoft.Web', 'Microsoft.DocumentDB', 'Microsoft.Sql', 'Microsoft.EventGrid',
        'Microsoft.Insights', 'Microsoft.OperationalInsights', 'Microsoft.ManagedIdentity',
        'Microsoft.App', 'Microsoft.KeyVault'
    )
    foreach ($provider in $providers) {
        $state = az provider show -n $provider --query registrationState -o tsv 2>$null
        if ($state -ne 'Registered') {
            Write-Host "    registering $provider (currently $state)"
            az provider register -n $provider | Out-Null
        }
    }
}

# The bicepparam file reads these rather than hard-coding identifiers, because this
# repository is public.
$env:JP_NAME_PREFIX = $NamePrefix
$env:JP_LOCATION = $Location
$env:JP_LANDING_STORAGE_ACCOUNT = $LandingStorageAccount
$env:JP_LANDING_CONTAINER = $LandingContainer
$env:JP_ADMIN_OBJECT_ID = $adminObjectId
$env:JP_ADMIN_LOGIN_NAME = $adminLoginName
$env:JP_TENANT_ID = $tenantId
$env:JP_MATCHING_PROVIDER = $MatchingProvider
$env:JP_API_CLIENT_ID = $ApiClientId
$env:JP_API_ALLOWED_ORIGINS = ($ApiAllowedOrigins -join ',')

Write-Host '==> Deploying infrastructure (Cosmos and SQL take several minutes)' -ForegroundColor Cyan
$outputsJson = az deployment group create `
    --resource-group $ResourceGroup `
    --name $DeploymentName `
    --template-file (Join-Path $repoRoot 'infra/main.bicep') `
    --parameters (Join-Path $repoRoot 'infra/main.bicepparam') `
    --no-prompt `
    --query properties.outputs -o json

if ($LASTEXITCODE -ne 0) { throw 'Deployment failed.' }

$outputs = $outputsJson | ConvertFrom-Json
$sqlConnectionString = $outputs.sqlConnectionString.value
$identityName = $outputs.ingestIdentityName.value

Write-Host ''
Write-Host "    function app : $($outputs.functionAppName.value)"
Write-Host "    api          : $($outputs.apiUrl.value)"
Write-Host "    cosmos       : $($outputs.cosmosAccountName.value)"
Write-Host "    sql server   : $($outputs.sqlServerFqdn.value)"
Write-Host ''

if (-not $SkipMigrations) {
    $serverName = ($outputs.sqlServerFqdn.value -split '\.')[0]
    $firewallRule = 'temp-provisioning-client'

    # The server's only standing rule allows Azure services; this machine needs a rule of
    # its own to connect. It is removed again in the finally block so no home IP address
    # is left behind on the server.
    Write-Host '==> Opening the SQL firewall for this machine' -ForegroundColor Cyan
    $clientIp = (Invoke-RestMethod -Uri 'https://api.ipify.org?format=json').ip
    az sql server firewall-rule create -g $ResourceGroup -s $serverName -n $firewallRule `
        --start-ip-address $clientIp --end-ip-address $clientIp -o none

    try {
        # Both steps run as you, the Entra admin. The function's identity has no rights
        # until the grant below, and no rights to change the schema even afterwards.
        Write-Host '==> Applying database schema' -ForegroundColor Cyan
        dotnet run --project (Join-Path $repoRoot 'tools/JobPlatform.DbAdmin') -- migrate $sqlConnectionString
        if ($LASTEXITCODE -ne 0) { throw 'Migration failed.' }

        Write-Host '==> Granting the ingest identity access to the database' -ForegroundColor Cyan
        dotnet run --project (Join-Path $repoRoot 'tools/JobPlatform.DbAdmin') -- grant-identity $sqlConnectionString $identityName
        if ($LASTEXITCODE -ne 0) { throw 'Identity grant failed.' }
    }
    finally {
        Write-Host '==> Removing the temporary firewall rule' -ForegroundColor Cyan
        az sql server firewall-rule delete -g $ResourceGroup -s $serverName -n $firewallRule -o none
    }
}

# The vault is created empty and the key is set by hand, deliberately: passing it as a
# parameter would put it in the deployment history, where it would outlive any rotation.
if ($MatchingProvider -eq 'anthropic') {
    $vaultName = $outputs.keyVaultName.value
    $secretName = $outputs.anthropicSecretName.value

    $existing = az keyvault secret show --vault-name $vaultName --name $secretName --query id -o tsv 2>$null

    if (-not $existing) {
        Write-Host ''
        Write-Host 'Matching provider is "anthropic" but the key vault has no key yet.' -ForegroundColor Yellow
        Write-Host 'The API will fall back to the keyword ranker until you set it:' -ForegroundColor Yellow
        Write-Host "  az keyvault secret set --vault-name $vaultName --name $secretName --value '<your-key>'" -ForegroundColor Yellow
        Write-Host 'Then restart the container app to pick it up:' -ForegroundColor Yellow
        Write-Host "  az containerapp revision restart -g $ResourceGroup -n $($outputs.apiName.value)" -ForegroundColor Yellow
    }
    else {
        Write-Host "    key vault    : $vaultName (secret '$secretName' present)"
    }
}

Write-Host ''
Write-Host 'Provisioning complete.' -ForegroundColor Green
Write-Host ''
Write-Host 'Note: on a first deploy from empty the Event Grid subscription cannot validate' -ForegroundColor Yellow
Write-Host 'its endpoint until the function code exists. Deploy the code, then re-run this' -ForegroundColor Yellow
Write-Host 'script - it is idempotent.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Remaining manual step: point the scraper at the new landing container.' -ForegroundColor Yellow
Write-Host "  Set AZURE_CONTAINER_NAME=$LandingContainer in the job-scrapper .env and in the" -ForegroundColor Yellow
Write-Host '  NAS docker-compose.yml. Until then, uploads land in the old container and' -ForegroundColor Yellow
Write-Host '  nothing triggers the ingest.' -ForegroundColor Yellow
