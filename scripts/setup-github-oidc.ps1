<#
.SYNOPSIS
    Configures passwordless GitHub Actions deployment via Entra workload identity federation.

.DESCRIPTION
    Creates an app registration and service principal, federates it to this repository's
    main branch, grants it the minimum roles needed to run the Bicep, and records the
    non-secret identifiers as GitHub Actions variables and secrets.

    No client secret is ever created. GitHub exchanges a short-lived OIDC token for an
    Azure token at run time, so there is no credential to rotate or leak - which matters
    because this repository is public.

    The federated credential is pinned to `repo:<owner>/<repo>:ref:refs/heads/main`, so a
    fork or a pull request from a fork cannot obtain a token.

.EXAMPLE
    ./scripts/setup-github-oidc.ps1 -Repository pa741/job-platform -ResourceGroup job-platform -LandingStorageAccount mystorage
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Repository,

    [Parameter(Mandatory)]
    [string]$ResourceGroup,

    [Parameter(Mandatory)]
    [string]$LandingStorageAccount,

    [string]$AppName = 'job-platform-deploy',
    [string]$Branch = 'main',
    [string]$NamePrefix = 'jobplatform',
    [string]$Location = 'spaincentral',
    [string]$LandingContainer = 'jobs-landing'
)

$ErrorActionPreference = 'Stop'

$subscriptionId = az account show --query id -o tsv
$tenantId = az account show --query tenantId -o tsv
$scope = "/subscriptions/$subscriptionId/resourceGroups/$ResourceGroup"

Write-Host "==> Ensuring app registration '$AppName'" -ForegroundColor Cyan
$appId = az ad app list --display-name $AppName --query '[0].appId' -o tsv
if (-not $appId) {
    $appId = az ad app create --display-name $AppName --query appId -o tsv
    Write-Host "    created $appId"
}
else {
    Write-Host "    reusing $appId"
}

$servicePrincipalId = az ad sp list --filter "appId eq '$appId'" --query '[0].id' -o tsv
if (-not $servicePrincipalId) {
    $servicePrincipalId = az ad sp create --id $appId --query id -o tsv
    Write-Host "    created service principal $servicePrincipalId"
}

Write-Host '==> Federating to the repository' -ForegroundColor Cyan
$credentialName = "gh-$Branch"
$existing = az ad app federated-credential list --id $appId --query "[?name=='$credentialName'].name" -o tsv
if (-not $existing) {
    $parameters = @{
        name      = $credentialName
        issuer    = 'https://token.actions.githubusercontent.com'
        subject   = "repo:${Repository}:ref:refs/heads/$Branch"
        audiences = @('api://AzureADTokenExchange')
    } | ConvertTo-Json -Compress

    $tempFile = New-TemporaryFile
    Set-Content -Path $tempFile -Value $parameters -Encoding utf8
    az ad app federated-credential create --id $appId --parameters "@$tempFile" | Out-Null
    Remove-Item $tempFile -Force
    Write-Host "    federated repo:${Repository}:ref:refs/heads/$Branch"
}
else {
    Write-Host "    '$credentialName' already exists"
}

Write-Host '==> Assigning roles on the resource group' -ForegroundColor Cyan
# Contributor alone is not enough: main.bicep creates role assignments, which requires a
# principal that can write them.
foreach ($role in @('Contributor', 'Role Based Access Control Administrator')) {
    az role assignment create `
        --assignee-object-id $servicePrincipalId `
        --assignee-principal-type ServicePrincipal `
        --role $role `
        --scope $scope 2>$null | Out-Null
    Write-Host "    $role"
}

Write-Host '==> Recording identifiers in GitHub' -ForegroundColor Cyan
# Secrets: things worth keeping out of public logs. Variables: plain configuration.
$adminObjectId = az ad signed-in-user show --query id -o tsv
$adminLoginName = az ad signed-in-user show --query userPrincipalName -o tsv

gh secret set AZURE_CLIENT_ID --repo $Repository --body $appId
gh secret set AZURE_TENANT_ID --repo $Repository --body $tenantId
gh secret set AZURE_SUBSCRIPTION_ID --repo $Repository --body $subscriptionId
gh secret set JP_ADMIN_OBJECT_ID --repo $Repository --body $adminObjectId
gh secret set JP_ADMIN_LOGIN_NAME --repo $Repository --body $adminLoginName

gh variable set AZURE_RESOURCE_GROUP --repo $Repository --body $ResourceGroup
gh variable set JP_NAME_PREFIX --repo $Repository --body $NamePrefix
gh variable set JP_LOCATION --repo $Repository --body $Location
gh variable set JP_LANDING_STORAGE_ACCOUNT --repo $Repository --body $LandingStorageAccount
gh variable set JP_LANDING_CONTAINER --repo $Repository --body $LandingContainer

Write-Host ''
Write-Host 'OIDC configured. No client secret was created.' -ForegroundColor Green
Write-Host ''
Write-Host 'Note: the migrate job in deploy.yml runs as this service principal, which is' -ForegroundColor Yellow
Write-Host 'not the SQL Entra admin. Either run migrations from a workstation with' -ForegroundColor Yellow
Write-Host 'scripts/provision.ps1, or make the SQL admin an Entra group containing both' -ForegroundColor Yellow
Write-Host 'you and this principal.' -ForegroundColor Yellow
