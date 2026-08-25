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
// Every default below is applied with a ternary on empty(), not with
// readEnvironmentVariable's own fallback argument. GitHub Actions maps an undefined
// `vars.X` to an env var that is set-and-empty rather than absent, so the fallback never
// fires and the empty string is passed through - which failed a deploy with BCP033,
// "Expected a value of type 'azureopenai' | 'none' but the provided value is of type ''".
param apiContainerImage = empty(readEnvironmentVariable('JP_API_IMAGE', ''))
  ? 'ghcr.io/pa741/job-platform-api:latest'
  : readEnvironmentVariable('JP_API_IMAGE', '')
param apiClientId = readEnvironmentVariable('JP_API_CLIENT_ID', '')
param apiAllowAnonymousReads = empty(readEnvironmentVariable('JP_API_ALLOW_ANONYMOUS_READS', ''))
  ? false
  : bool(readEnvironmentVariable('JP_API_ALLOW_ANONYMOUS_READS', 'false'))
// 'basic' buys an always-on database for a few euros a month; the default keeps a fresh
// clone free. Empty-guarded like every other API parameter - see the note above.
param sqlSku = empty(readEnvironmentVariable('JP_SQL_SKU', ''))
  ? 'free-serverless'
  : readEnvironmentVariable('JP_SQL_SKU', '')
param aiProvider = empty(readEnvironmentVariable('JP_AI_PROVIDER', ''))
  ? 'none'
  : readEnvironmentVariable('JP_AI_PROVIDER', '')
// Which models sit behind the two deployments. Overridable so that pointing at a newer
// release, or at a smaller model where quota is short, is a repository variable rather than
// a code change. Empty-guarded like everything else here.
param aiBulkModelName = empty(readEnvironmentVariable('JP_AI_BULK_MODEL', ''))
  ? 'gpt-5.6-luna'
  : readEnvironmentVariable('JP_AI_BULK_MODEL', '')
param aiWritingModelName = empty(readEnvironmentVariable('JP_AI_WRITING_MODEL', ''))
  ? 'gpt-5.6-sol'
  : readEnvironmentVariable('JP_AI_WRITING_MODEL', '')
// Comma-separated in the environment, e.g. "https://app.example.net,https://localhost:5173".
param apiAllowedOrigins = empty(readEnvironmentVariable('JP_API_ALLOWED_ORIGINS', ''))
  ? []
  : split(readEnvironmentVariable('JP_API_ALLOWED_ORIGINS', ''), ',')
