using './main.bicep'

param env = 'dev'
param authAuthority = 'https://login.microsoftonline.com/b27b01b0-cd17-40c7-9469-e55b41387f1b/v2.0'
param authAudience = 'e88b2c02-bf33-49d5-9e65-9f6bbdbb6bf6'
param bootstrap = true
param bootstrapExtractor = true
// Sprint 4: Worker image deployment (omit or leave empty to skip Worker deployment during development/testing).
// Example: 'acr.azurecr.io/docintake-worker:latest'
param workerImage = ''
param sqlAdminGroupName = 'DocIntake-Sql-Admins'
// Placeholder only (deployment fails until replaced). Get the real value with:
// az ad group show --group <name> --query id -o tsv
param sqlAdminGroupObjectId = 'bfb28293-869e-4c78-85be-b85138312aa9'
// Candidate region/name for validation; subscription availability is not yet confirmed.
param sqlLocation = 'westus3'
param sqlServerNameOverride = 'di-docintake-dev-sql-wus3-20261021'
// SQL is private-only in every environment; local SQL administration needs VNet connectivity/DNS.
// Reserve non-overlapping address ranges before deploying or connecting to other networks.
param virtualNetworkAddressPrefix = '10.40.0.0/16'
param containerAppsSubnetAddressPrefix = '10.40.0.0/23'
param privateEndpointSubnetAddressPrefix = '10.40.2.0/27'
// Sprint 3 local development only: your own Entra user object ID, which runs the worker locally.
// az ad signed-in-user show --query id -o tsv
// Sprint 4 replaces this with the worker Container App's managed identity.
param workerPrincipalId = 'b289bbca-0224-4517-8d7d-9d3692a12d01'
param workerPrincipalType = 'User'
param enableLocalWorkerAccess = false
// Sprint 4: chat model deployment configuration
// Chat quota is currently zero; ingestion and embeddings can deploy independently.
param deployChat = false
param chatSkuName = 'GlobalStandard'
param chatCapacity = 10
// Sprint 5: cost guardrails
param monthlyBudgetAmount = 50
param budgetNotificationEmail = 'testbudgetnotif@gmail.com'
