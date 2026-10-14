# Outage Recovery Test Runbook

## Overview

This runbook documents testing the system's resilience to temporary outages of critical dependencies. The test simulates a temporary loss of access to a critical service (AI Search or Azure OpenAI), uploads documents during the outage, confirms messages are queued and retried, and verifies successful processing once access is restored.

## Test Objective

Verify that:
1. Messages queued during an outage are not lost
2. The Worker retries failed messages (up to maxDeliveryCount=5)
3. All documents are successfully processed once the service is restored
4. No manual intervention beyond restoring the role assignment is required

## Services to Test

Choose one:
- **AI Search** (recommended for initial test): Core indexing dependency
- **Azure OpenAI**: Embedding and chat dependencies

## Pre-Test Checklist

- [ ] Deployment is complete and functioning normally
- [ ] At least one successful document upload is confirmed end-to-end
- [ ] Application Insights is collecting logs
- [ ] Service Bus queue and dead-letter queue are empty or you know the baseline
- [ ] You have access to revoke/restore role assignments
- [ ] A test document is ready to upload

## Test Procedure

### Phase 1: Baseline (All Systems Healthy)

1. **Verify system health**:
   ```powershell
   $resourceGroup = "rg-docintake-dev"
   $deployment = "<deployment-name>"
   
   # Check Worker is running
   $workerApp = az containerapp list --resource-group $resourceGroup `
     --query "[?contains(name, 'worker')].name | [0]" -o tsv
   $replicas = az containerapp revision list --name $workerApp --resource-group $resourceGroup `
     --query "[0].properties.replicas" -o tsv
   Write-Host "Worker replicas: $replicas"
   
   # Check queue is empty
   $ns = az deployment group show --name $deployment --resource-group $resourceGroup `
     --query properties.outputs.serviceBusNamespace.value -o tsv
   az servicebus queue show --resource-group $resourceGroup --namespace-name $ns `
     --name extracted-documents --query "{ActiveMessages:messageCount, DLQ:deadLetterMessageCount}"
   ```

2. **Upload a baseline document**:
   ```powershell
   $token = # your bearer token
   $response = curl.exe -s -H "Authorization: Bearer $token" `
     -F "file=@baseline.pdf" "https://<api-fqdn>/documents" | ConvertFrom-Json
   $baselineDocId = $response.documentId
   Write-Host "Uploaded baseline document: $baselineDocId"
   ```

3. **Wait for processing** (30-60 seconds) and **verify in Application Insights**:
   ```kusto
   traces
   | where timestamp > ago(1h)
   | where message startswith "Indexed" or message contains $baselineDocId
   | project timestamp, message
   ```

### Phase 2: Simulate Outage (Revoke Access)

1. **Choose service to test**. For **AI Search**:

   ```powershell
   $searchName = az deployment group show --name $deployment --resource-group $resourceGroup `
     --query properties.outputs.searchName.value -o tsv
   
   # Get the Worker's managed identity principal ID
   $workerApp = "di-dev-<suffix>-worker"  # or query from deployment
   $workerPrincipalId = az containerapp show --name $workerApp --resource-group $resourceGroup `
     --query identity.principalId -o tsv
   
   # Revoke Search permissions
   $searchId = az search service show --name $searchName --resource-group $resourceGroup --query id -o tsv
   
   # List current role assignments
   $assignments = az role assignment list --assignee $workerPrincipalId `
     --scope $searchId --query "[].{name:roleDefinitionName,id:id}" -o json
   Write-Host "Current Search role assignments for Worker:" -ForegroundColor Cyan
   $assignments | ConvertFrom-Json | ForEach-Object { Write-Host "  $($_.name)" }
   
   # Delete the Search permissions (note: save the assignment names for restoration)
   az role assignment delete --role "Search Index Data Contributor" `
     --assignee $workerPrincipalId --scope $searchId
   az role assignment delete --role "Search Service Contributor" `
     --assignee $workerPrincipalId --scope $searchId
   
   Write-Host "✓ Worker Search permissions revoked" -ForegroundColor Green
   ```

   Alternatively, for **Azure OpenAI**:

   ```powershell
   $openAiName = az deployment group show --name $deployment --resource-group $resourceGroup `
     --query properties.outputs.openAiName.value -o tsv
   
   $openAiId = az cognitiveservices account show --name $openAiName `
     --resource-group $resourceGroup --query id -o tsv
   
   az role assignment delete --role "Cognitive Services OpenAI User" `
     --assignee $workerPrincipalId --scope $openAiId
   
   Write-Host "✓ Worker OpenAI permissions revoked" -ForegroundColor Green
   ```

2. **Confirm access is revoked**. Wait 1-2 minutes for RBAC cache to clear, then upload a document and watch Worker logs:

   ```kusto
   traces
   | where timestamp > ago(5m)
   | where severityLevel >= 3
   | where message contains "403" or message contains "Forbidden" or message contains "Access Denied"
   | project timestamp, message, customDimensions
   ```

   Expected: Worker logs show 403 Forbidden when attempting to access the revoked service.

### Phase 3: Upload During Outage

1. **Upload 3-5 test documents** (without waiting for processing):

   ```powershell
   $docIds = @()
   for ($i = 1; $i -le 3; $i++) {
       $response = curl.exe -s -H "Authorization: Bearer $token" `
         -F "file=@test-$i.pdf" "https://<api-fqdn>/documents" | ConvertFrom-Json
       $docIds += $response.documentId
       Write-Host "Uploaded document $i: $($response.documentId)"
       Start-Sleep -Seconds 2  # Small delay between uploads
   }
   ```

2. **Verify messages are queued** (but not processed):

   ```powershell
   # Check queue depth
   $queueStatus = az servicebus queue show --resource-group $resourceGroup `
     --namespace-name $ns --name extracted-documents `
     --query "{ActiveMessages:messageCount, DLQ:deadLetterMessageCount}"
   
   Write-Host "Queue status during outage:"
   $queueStatus | ConvertFrom-Json | ForEach-Object {
       Write-Host "  Active messages: $($_.ActiveMessages) (expect 3)"
       Write-Host "  Dead-letter messages: $($_.DLQ) (expect 0)"
   }
   ```

3. **Watch Worker logs for retries**:

   ```kusto
   traces
   | where timestamp > ago(30m)
   | where severityLevel >= 3
   | where message contains "Failed to" or message contains "Abandoned"
   | project timestamp, message, customDimensions.DeliveryCount, operation_Id
   | order by timestamp desc
   ```

   Expected: Multiple log entries for each document, with DeliveryCount incrementing (1, 2, 3, 4, 5).

### Phase 4: Restore Access

1. **Restore the role assignments**:

   ```powershell
   # For AI Search
   az role assignment create --role "Search Index Data Contributor" `
     --assignee-object-id $workerPrincipalId --scope $searchId `
     --assignee-principal-type ServicePrincipal
   
   az role assignment create --role "Search Service Contributor" `
     --assignee-object-id $workerPrincipalId --scope $searchId `
     --assignee-principal-type ServicePrincipal
   
   Write-Host "✓ Worker Search permissions restored" -ForegroundColor Green
   ```

   Or for **Azure OpenAI**:

   ```powershell
   az role assignment create --role "Cognitive Services OpenAI User" `
     --assignee-object-id $workerPrincipalId --scope $openAiId `
     --assignee-principal-type ServicePrincipal
   
   Write-Host "✓ Worker OpenAI permissions restored" -ForegroundColor Green
   ```

2. **Wait for RBAC propagation** (1-2 minutes). Worker will automatically retry the queued messages.

### Phase 5: Verify Recovery

1. **Monitor Worker logs for successful processing**:

   ```kusto
   traces
   | where timestamp > ago(30m)
   | where message startswith "Indexed"
   | project timestamp, message, customDimensions.DocumentId
   | order by timestamp desc
   ```

   Expected: For each of the 3 uploaded documents, a log entry "Indexed {docId} with {n} chunks".

2. **Confirm all documents are in SQL**:

   ```powershell
   $docIds | ForEach-Object {
       $result = sqlcmd -S $sqlServer -d docintake --authentication-method ActiveDirectoryDefault `
         -Q "SELECT Id, ChunkCount, IndexedAt FROM dbo.Documents WHERE Id = '$_'" -s ","
       Write-Host "  $_: ChunkCount=$($result -split ',' | Select-Object -Last 1)"
   }
   ```

   Expected: All 3 documents have non-zero ChunkCount and recent IndexedAt timestamp.

3. **Confirm all documents are in AI Search**:

   ```powershell
   $searchToken = az account get-access-token --resource https://search.azure.com --query accessToken -o tsv
   
   $docIds | ForEach-Object {
       $sourceUrl = # construct or lookup from SQL
       $body = @{ search = '*'; filter = "source eq '$sourceUrl'"; count = $true; top = 0 } | ConvertTo-Json
       $result = Invoke-RestMethod -Method Post -ContentType 'application/json' `
         -Headers @{ Authorization = "Bearer $searchToken" } -Body $body `
         -Uri "$(Get-Output searchEndpoint)/indexes/document-chunks/docs/search?api-version=2024-07-01"
       Write-Host "  $_: $($result.'@odata.count') chunks indexed"
   }
   ```

   Expected: Each document has a chunk count matching the SQL ChunkCount.

4. **Check queue is now empty**:

   ```powershell
   az servicebus queue show --resource-group $resourceGroup --namespace-name $ns `
     --name extracted-documents --query "{ActiveMessages:messageCount, DLQ:deadLetterMessageCount}"
   ```

   Expected: ActiveMessages: 0, DLQ: 0

5. **Confirm no documents are lost**:

   - Verify baseline document (from Phase 1) is still accessible
   - Verify all 3 test documents from Phase 3 are now fully indexed
   - Confirm no documents are in dead-letter queue (DLQ: 0)

## Expected Results

| Metric | During Outage | After Recovery |
| --- | --- | --- |
| Queue depth | 3+ messages (backlog accumulating) | 0 (all processed) |
| DeliveryCount in logs | 1, 2, 3, 4, 5 (retries) | Final success (processed, not retried) |
| Worker errors | 403 Forbidden or similar access error | No errors; successful indexing logs |
| SQL documents | 0 new (Worker can't process) | 3 (all documents inserted) |
| AI Search chunks | 0 new (Worker can't index) | Documents + chunks (all indexed) |
| Dead-letter queue | 0 (still retrying) | 0 (no permanent failures) |

## Recovery Time Measurement

**Time from restoring role assignment to all documents processed**:

Expected: **2-5 minutes**

- 1-2 min for RBAC role assignment to propagate
- 30-90 sec for Worker to retry queued messages
- 30-60 sec for indexing to complete

Measure by:
```powershell
$restoreTime = Get-Date
# Restore role assignment...
Start-Sleep -Seconds 120  # Wait for propagation

# Poll until all documents are indexed
while ($true) {
    $indexed = sqlcmd -S $sqlServer -d docintake --authentication-method ActiveDirectoryDefault `
      -Q "SELECT COUNT(*) FROM dbo.Documents WHERE Id IN ('$($docIds -join "','")')" | [int]::Parse
    
    if ($indexed -eq $docIds.Count) {
        $recoveryTime = (Get-Date) - $restoreTime
        Write-Host "✓ Recovery complete in $($recoveryTime.TotalSeconds) seconds" -ForegroundColor Green
        break
    }
    Start-Sleep -Seconds 10
}
```

## Test Failure Scenarios

| Failure | Cause | Resolution |
| --- | --- | --- |
| Queue still has messages after 10 min recovery | Worker can't restart processing, check Worker logs for different error | Restart Worker Container App or check role assignments again |
| Documents in dead-letter queue | Worker exhausted retries but service still unreachable | Restore role assignment, wait longer for propagation, or manually resubmit using dead-letter recovery script |
| SQL ChunkCount=0 for some documents | Worker completed but failed at SQL insert step | Check SQL connectivity and Worker's db_datawriter role on dbo.Documents |
| AI Search has no chunks for a document | Worker completed but Search upload failed | Verify Search replica and partition count; manually re-trigger Worker for that document |

## Cleanup

```powershell
# Remove test documents from SQL if testing repeatedly
sqlcmd -S $sqlServer -d docintake --authentication-method ActiveDirectoryDefault `
  -Q "DELETE FROM dbo.Documents WHERE Id IN ('$($docIds -join "','")')"

# Remove test blobs from Storage
$docIds | ForEach-Object {
    az storage blob delete-batch --account-name $storageName `
      --source documents --pattern "${_}/*" --auth-mode login
}

# Confirm queues are empty
az servicebus queue show --resource-group $resourceGroup --namespace-name $ns `
  --name extracted-documents --query "{ActiveMessages:messageCount, DLQ:deadLetterMessageCount}"
```

## Automation Script

To automate this test, create [scripts/test-outage-recovery.ps1](../../scripts/test-outage-recovery.ps1):

```powershell
param(
    [string]$ResourceGroup = "rg-docintake-dev",
    [string]$DeploymentName = "docintake-api",
    [ValidateSet("Search", "OpenAI")]
    [string]$ServiceToTest = "Search",
    [int]$OutageDurationSeconds = 300,
    [int]$RecoveryTimeoutSeconds = 600
)

Write-Host "Starting outage recovery test..." -ForegroundColor Cyan

# Phase 1: Baseline
# Upload document, verify success

# Phase 2: Revoke access
Write-Host "Revoking access to $ServiceToTest..." -ForegroundColor Yellow
# ... revoke role assignments

# Phase 3: Upload during outage
Write-Host "Uploading documents during outage..." -ForegroundColor Yellow
# ... upload 3 documents

# Phase 4: Wait for outage
Write-Host "Waiting $OutageDurationSeconds seconds..." -ForegroundColor Yellow
Start-Sleep -Seconds $OutageDurationSeconds

# Phase 5: Restore access
Write-Host "Restoring access..." -ForegroundColor Green
# ... restore role assignments

# Phase 6: Verify recovery
Write-Host "Waiting up to $RecoveryTimeoutSeconds seconds for recovery..." -ForegroundColor Green
# ... poll and confirm documents are indexed

Write-Host "✓ Outage recovery test complete" -ForegroundColor Green
```

## Test Results (to be filled during execution)

**Date Tested**: [YYYY-MM-DD]
**Service Tested**: [Search / OpenAI]
**Outcome**: [PASS / FAIL]
**Notes**: [Any observations or issues]

### Baseline Phase
- Baseline document uploaded: ✓ / ✗
- Baseline document indexed: ✓ / ✗

### Outage Phase
- Role assignment revoked: ✓ / ✗
- Access confirmed as denied: ✓ / ✗
- Documents uploaded: ✓ / ✗
- Queue depth increased: ✓ / ✗
- Worker retried messages (confirmed in logs): ✓ / ✗

### Recovery Phase
- Role assignment restored: ✓ / ✗
- RBAC propagation time: [X] minutes
- Worker resumed processing: ✓ / ✗
- All documents indexed: ✓ / ✗
- Queue emptied: ✓ / ✗
- No documents lost: ✓ / ✗
- Recovery time: [X] minutes [Y] seconds

