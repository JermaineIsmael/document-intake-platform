# Cost Guardrails and Spend Management

## Overview

This document describes the cost-bounding mechanisms configured in DocIntake across compute, messaging, and AI services to prevent unexpected Azure spending.

## Cost Control Settings

### 1. Service Bus Message Redelivery

**Setting**: `maxDeliveryCount: 5` in `extracted-documents` queue ([main.bicep](../../infra/main.bicep))

**Effect**: A failed message is attempted up to 5 times before being dead-lettered. Each failed attempt incurs a service bus operation cost (typically very small ~$0.0000005/op).

**Cost Impact**: Bounds retry costs by limiting repeat attempts. With maxDeliveryCount=5, a single failing message costs ~5x the base operation cost instead of unlimited retries.

**Verification**: 
```bicep
resource extractedDocuments 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  properties: {
    maxDeliveryCount: 5
    // ...
  }
}
```

### 2. Worker Container App Replica Scaling

**Setting**: `maxReplicas: 5` in Worker Container App module ([main.bicep](../../infra/main.bicep))

**Effect**: The Worker Container App scales from 0 to maximum 5 concurrent replicas based on Service Bus queue depth (KEDA scaling). Each replica is a container instance consuming CPU and memory.

**Cost Impact**: At max scale, 5 replicas running at Consumption tier cost ~$0.0000694/sec per vCPU. With 1 vCPU per default instance, maximum sustained cost is bounded at 5x single-instance cost. Scales back to 0 when idle.

**Verification**:
```bicep
module worker 'modules/containerApp.bicep' = if (!empty(workerImage)) {
  params: {
    maxReplicas: 5
    // ...
  }
}
```

**KEDA Configuration**: Scales when queue depth exceeds 10 messages per replica (messageCount: 10 in scaleRules).

### 3. Azure Function App (Extractor)

**Setting**: Consumption (Y1) plan ([main.bicep](../../infra/main.bicep))

**Effect**: Function execution is billed per 100ms of compute time and memory allocation (GB-seconds). No minimum spend.

**Cost Impact**: Consumption plan is inherently bounded by execution time and frequency. At typical extraction speeds (~2-5 seconds/document), cost per document is roughly $0.000001-0.000002 in compute charges.

**Verification**:
```bicep
resource functionPlan 'Microsoft.Web/serverfarms@2022-03-01' = {
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  // ...
}
```

**Note**: Function storage account uses a separate storage account (AzureWebJobsStorage) for function host bookkeeping; this has minimal cost (typically <$1/month for the diagnostic tables and queues).

### 4. Azure AI Search

**Setting**: Basic tier, 1 replica, 1 partition ([main.bicep](../../infra/main.bicep))

**Effect**: Basic tier supports up to 15 million documents total and is a fixed monthly cost (~$75/month regardless of usage). Single replica and partition limit concurrent queries and indexing throughput but keep the tier cost stable.

**Cost Impact**: Monthly cost is fixed at tier SKU rate (~$75/month for Basic). No per-query or per-indexed-document charge; scale is bounded by partition/replica topology.

**Verification**:
```bicep
resource search 'Microsoft.Search/searchServices@2025-05-01' = {
  sku: {
    name: 'basic'
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
  }
}
```

### 5. Azure OpenAI

**Setting**: S0 capacity tier with explicit TPM (tokens per minute) limits:
- Embedding deployment: 30K TPM ([main.bicep](../../infra/main.bicep))
- Chat deployment: 10K TPM (Sprint 4, default)

**Effect**: TPM capacity limits prevent runaway token consumption. Exceeding the limit triggers HTTP 429 (throttling); requests are queued and retried.

**Cost Impact**: Capacity is pre-allocated monthly (~$2/K TPM for S0 Standard SKU). At 30K TPM embedding and 10K TPM chat, monthly cost is roughly $60-80 regardless of usage up to those limits. Exceeding causes 429s, not overage charges.

**Verification**:
```bicep
@description('Embedding deployment capacity in thousands of tokens per minute; must fit your subscription quota.')
@minValue(1)
param embeddingCapacity int = 30

param chatCapacity int = 10
```

### 6. Azure SQL Database

**Setting**: Basic tier (5 DTUs), 2 GB storage ([main.bicep](../../infra/main.bicep))

**Effect**: Basic tier is a fixed monthly cost (~$5-6/month). Supports ~100 concurrent connections and ~15 transactions per second, sufficient for a small worker process inserting document metadata.

**Cost Impact**: Fixed monthly cost, no per-query or per-storage overage charges. Storage is included up to 2 GB; additional storage is ~$0.20/GB/month.

**Verification**:
```bicep
resource sqlDatabase 'Microsoft.Sql/servers/databases@2025-01-01' = {
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
}
```

### 7. Azure Blob Storage

**Setting**: Standard_LRS (Locally Redundant Storage), with default pricing tiers

**Effect**: Storage is charged per GB stored (~$0.024/GB/month for hot tier) plus small per-operation charges. No explicit size limit in Bicep.

**Cost Impact**: Unbounded by configuration; cost scales with document storage. Estimated at typical compression ~10% of original file size.

**Recommendation**: Monitor blob storage size monthly; use Azure Storage lifecycle policies to move old blobs to cool/archive tiers after 30-90 days to reduce cost.

### 8. Application Insights

**Setting**: Workspace-linked, PerGB2018 pricing, 30-day retention ([main.bicep](../../infra/main.bicep))

**Effect**: Pay-per-GB ingestion pricing (~$2.99/GB ingested). 30-day retention retains up to 30 days of telemetry.

**Cost Impact**: Depends on application verbosity. Typical API + Worker telemetry ~100-500 MB/month = ~$0.30-1.50/month. Sampling at 20% (Function host.json) reduces ingestion.

**Verification**:
```bicep
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}
```

### 9. Azure Document Intelligence

**Setting**: S0 (Standard) tier ([main.bicep](../../infra/main.bicep))

**Effect**: S0 is a fixed monthly cost (~$100/month) and includes the "prebuilt-read" model used for PDF/DOCX extraction. No per-page overage.

**Cost Impact**: Fixed monthly cost. Extraction is billed per 1,000 pages processed (~$1.50-2.50/1000 pages depending on region). With S0, cost is bounded at monthly fee + per-page usage.

**Verification**:
```bicep
resource documentIntelligence 'Microsoft.CognitiveServices/accounts@2026-09-01' = {
  kind: 'FormRecognizer'
  sku: {
    name: 'S0'
  }
}
```

## Monthly Cost Estimate

| Service | Cost/Month | Notes |
| --- | --- | --- |
| Service Bus (Standard, 0 capacity) | ~$5-10 | Small namespace with minimal throughput |
| Worker Container App (Consumption, 0-5 replicas) | ~$5-20 | Scales to 0 when idle |
| Function App (Y1 Consumption) | ~$0.20 | If <1M executions/month |
| AI Search (Basic) | ~$75 | Fixed |
| OpenAI (S0 + 30K TPM embedding + 10K TPM chat) | ~$60-80 | Fixed capacity allocation |
| SQL Database (Basic) | ~$5-6 | Fixed |
| Blob Storage | ~$1-5 | Scales with document volume |
| Application Insights | ~$0.50-1.50 | PerGB, ~100-500 MB/month typical |
| Document Intelligence (S0) | ~$100 | Fixed + per-page overages |
| **Total (dev environment)** | **~$250-300/month** | Assuming <100 documents/month, <1M total pages extracted |

**Note**: This estimate assumes light usage (development/testing). Production environments may need to scale components and would see costs scale accordingly.

## Budget Alerts

A monthly budget alert has been configured in [main.bicep](../../infra/main.bicep):

```bicep
resource budget 'Microsoft.Consumption/budgets@2021-10-01' = if (monthlyBudgetAmount > 0 && !empty(budgetNotificationEmail)) {
  properties: {
    displayName: 'DocIntake Monthly Budget'
    category: 'Cost'
    amount: monthlyBudgetAmount
    timeGrain: 'Monthly'
    notifications: {
      NotificationForExceededBudget80Percent: {
        threshold: 80
        contactEmails: [ budgetNotificationEmail ]
      }
      NotificationForExceededBudget100Percent: {
        threshold: 100
        contactEmails: [ budgetNotificationEmail ]
      }
    }
  }
}
```

**Configuration**:
- Default limit: $50/month (overridable via `monthlyBudgetAmount` parameter)
- Notifications at 80% ($40) and 100% ($50) of budget
- Email must be configured in `main.bicepparam` and the recipient must accept the Azure notification subscription

**Important**: Budget alerts are **advisory only**. They do not automatically stop resources; they are notifications to alert you of spending trends. To enforce hard limits, additionally configure:
- Azure spending limits in your subscription (Azure Portal > Cost Management + Billing)
- KEDA scale rules with explicit maxReplicas (already in place: 5)
- Service Bus maxDeliveryCount to prevent infinite retries (already in place: 5)

## Monitoring and Cost Optimization

### Check Current Spending

```powershell
$resourceGroup = '<your-resource-group>'
az costmanagement query --timeperiod-from 2024-10-01T00:00:00Z --timeperiod-to 2024-10-31T23:59:59Z `
  --scope "/subscriptions/$(az account show --query id -o tsv)/resourcegroups/$resourceGroup" `
  --dataset '{ "granularity": "Monthly", "aggregation": { "totalCost": { "name": "PreTaxCost" } } }' `
  --query "properties.rows"
```

### Reduce Costs

1. **Disable Worker when not needed**: Set `workerImage = ''` in main.bicepparam to skip Worker deployment
2. **Reduce OpenAI capacity**: Lower `embeddingCapacity` and `chatCapacity` if TPM limits are not needed
3. **Use Azure Storage lifecycle policies**: Move documents to archive tier after 90 days
4. **Implement sampling in Application Insights**: Reduce telemetry volume (already at 20% in Function host.json)
5. **Right-size SQL database**: Monitor actual DTU usage; may be able to downgrade to Basic if usage is light

## Known Unbounded Costs

- **Blob storage volume**: No Bicep limit; configure lifecycle policies separately
- **Document Intelligence per-page charges**: Beyond the S0 flat rate; monitor page volume
- **Data egress**: Any egress from Azure (though minimal for internal operations)

## Sprint 5 Verification Checklist

- [ ] Service Bus maxDeliveryCount = 5
- [ ] Worker maxReplicas = 5
- [ ] Function App = Y1 Consumption
- [ ] AI Search = Basic, 1 replica, 1 partition
- [ ] OpenAI embeddings = 30K TPM capacity
- [ ] OpenAI chat = 10K TPM capacity
- [ ] SQL Database = Basic tier
- [ ] Budget alert resource deployed with email configured
- [ ] All cost estimates reviewed and acceptable for intended use case

