using './main.bicep'

param env = 'dev'
param authAuthority = 'https://login.microsoftonline.com/e0793d39-0939-496d-b129-198edd916feb/v2.0'
param authAudience = '<api-client-id>'
param bootstrap = true
param bootstrapExtractor = true
// Sprint 4: Worker image deployment (omit or leave empty to skip Worker deployment during development/testing).
// Example: 'acr.azurecr.io/docintake-worker:latest'
param workerImage = ''
param sqlAdminGroupName = 'sqlAdmin'
// Placeholder only (deployment fails until replaced). Get the real value with:
// az ad group show --group <name> --query id -o tsv
param sqlAdminGroupObjectId = 'b289bbca-0224-4517-8d7d-9d3692a12d01'
// Sprint 3 local development only: your own Entra user object ID, which runs the worker locally.
// az ad signed-in-user show --query id -o tsv
// Sprint 4 replaces this with the worker Container App's managed identity.
param workerPrincipalId = 'b289bbca-0224-4517-8d7d-9d3692a12d01'
param workerPrincipalType = 'User'
// Sprint 4: chat model deployment configuration
param chatSkuName = 'GlobalStandard'
param chatCapacity = 10
// Sprint 5: cost guardrails
param monthlyBudgetAmount = 50
param budgetNotificationEmail = 'testbudgetnotif@gmail.com'

