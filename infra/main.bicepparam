using './main.bicep'

// ---------------------------------------------------------------------------
// This file is committed to a PUBLIC repository, so it must not contain real
// identifiers. Every value is read from the environment.
//
// Locally:   scripts/provision.ps1 exports these from your az login / .env.local
// In CI:     deploy.yml maps them from GitHub repo secrets
//
// See README.md, "Deploy your own", for the full list.
// ---------------------------------------------------------------------------

param namePrefix = readEnvironmentVariable('JP_NAME_PREFIX', 'jobplatform')
param location = readEnvironmentVariable('JP_LOCATION', 'spaincentral')
// The SQL free offer is region-restricted; see infra/modules/sql.bicep.
param sqlLocation = readEnvironmentVariable('JP_SQL_LOCATION', 'francecentral')
param landingStorageAccountName = readEnvironmentVariable('JP_LANDING_STORAGE_ACCOUNT')
param landingContainerName = readEnvironmentVariable('JP_LANDING_CONTAINER', 'jobs-landing')
param administratorObjectId = readEnvironmentVariable('JP_ADMIN_OBJECT_ID')
param administratorLoginName = readEnvironmentVariable('JP_ADMIN_LOGIN_NAME')
param tenantId = readEnvironmentVariable('JP_TENANT_ID')

// --- API ---------------------------------------------------------------------
param apiContainerImage = readEnvironmentVariable('JP_API_IMAGE', 'ghcr.io/pa741/job-platform-api:latest')
param apiClientId = readEnvironmentVariable('JP_API_CLIENT_ID', '')
param apiAllowAnonymousReads = bool(readEnvironmentVariable('JP_API_ALLOW_ANONYMOUS_READS', 'false'))
param matchingProvider = readEnvironmentVariable('JP_MATCHING_PROVIDER', 'keyword')
// Comma-separated in the environment, e.g. "https://app.example.net,https://localhost:5173".
param apiAllowedOrigins = empty(readEnvironmentVariable('JP_API_ALLOWED_ORIGINS', ''))
  ? []
  : split(readEnvironmentVariable('JP_API_ALLOWED_ORIGINS', ''), ',')
