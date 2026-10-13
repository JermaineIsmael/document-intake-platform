using './main.bicep'

param env = 'dev'
param authAuthority = 'https://login.microsoftonline.com/<tenant-id>/v2.0'
param authAudience = '<api-client-id>'
param bootstrap = true
param bootstrapExtractor = true
param sqlAdminGroupName = '<sql-admin-entra-group-name>'
// Placeholder only (deployment fails until replaced). Get the real value with:
// az ad group show --group <name> --query id -o tsv
param sqlAdminGroupObjectId = '00000000-0000-0000-0000-000000000000'
// Sprint 3 local development only: your own Entra user object ID, which runs the worker locally.
// az ad signed-in-user show --query id -o tsv
// Sprint 4 replaces this with the worker Container App's managed identity.
param workerPrincipalId = '00000000-0000-0000-0000-000000000000'
param workerPrincipalType = 'User'
