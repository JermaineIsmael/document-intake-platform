using './main.bicep'

param env = 'dev'
param authAuthority = 'https://login.microsoftonline.com/b27b01b0-cd17-40c7-9469-e55b41387f1b/v2.0'
param authAudience = 'e88b2c02-bf33-49d5-9e65-9f6bbdbb6bf6'
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
// Chat quota is currently zero; ingestion and embeddings can deploy independently.
param deployChat = false
param chatSkuName = 'GlobalStandard'
param chatCapacity = 10
// Sprint 5: cost guardrails
param monthlyBudgetAmount = 50
param budgetNotificationEmail = 'testbudgetnotif@gmail.com'
