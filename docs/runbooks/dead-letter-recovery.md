# Dead-Letter Recovery Runbook

## Overview

This runbook describes how to identify, diagnose, and recover from dead-lettered messages in the DocIntake pipeline. Messages are dead-lettered when they exhaust the maximum delivery count (5 attempts) or fail validation.

## Dead-Letter Locations

DocIntake has **two distinct dead-letter paths**:

### 1. Service Bus Dead-Letter Queue (Worker Processing Failures)

**Queue**: `extracted-documents-deadletter` (automatic Service Bus dead-letter queue)

**When messages are dead-lettered here**:
- Worker fails to process a valid document message (AI Search down, SQL connection lost, embedding failure, etc.)
- After 5 delivery attempts (maxDeliveryCount=5), the message is moved to the dead-letter queue
- The Worker received and failed to complete the message; it's a processing issue, not an invalid message

**Reasons to dead-letter**:
- Document Intelligence returned an unexpected format
- AI Search indexing failed (quota exceeded, network error, permission issue)
- Azure OpenAI embedding request failed (throttled, endpoint unreachable)
- SQL database insert/update failed (deadlock, timeout, permission issue)
- Message format is valid but processing logic encounters an error

### 2. Event Grid Dead-Letter Blob Container (Extraction Failures)

**Container**: `eventgrid-deadletter` in the document storage account

**When blobs are written here**:
- Event Grid cannot deliver the extraction event to the Function App after 30 retries
- The blob contains the original Event Grid event (BlobCreated) that the Function failed to process
- Event Grid has stopped trying; the blob upload to Function is dead

**Reasons for Event Grid dead-letter**:
- Function App is down or unreachable for >30 retries
- Function App is misconfigured or missing the ExtractDocument endpoint
- Event Grid Topic subscription is misconfigured
- Function endpoint returns persistent errors (not transient)

## Comparison: Service Bus vs. Event Grid Dead-Letter

| Aspect | Service Bus | Event Grid |
| --- | --- | --- |
| **Source** | Worker processing failures | Extraction delivery failures |
| **Location** | Queue: `extracted-documents-deadletter` | Blob container: `eventgrid-deadletter` |
| **Why message is there** | Worker received but couldn't process document | Function never received the event |
| **Diagnosis** | Check message body and DeadLetterReason | Check event blob content and Event Grid logs |
| **Resolution** | Fix root cause, reprocess message | Fix Function, replay Event Grid event |

## Inspecting Service Bus Dead-Letter Messages

### Option 1: Azure Portal Service Bus Explorer

1. Open the resource group in [Azure Portal](https://portal.azure.com)
2. Select the Service Bus namespace resource (named `di-dev-<suffix>-sb` or similar)
3. Click **Service Bus Explorer** (or navigate to **Queues**)
4. Click the queue: `extracted-documents`
5. Click **Dead Letter** on the left panel
6. You'll see any dead-lettered messages with:
   - **Message ID**: The document GUID
   - **DeliveryCount**: Number of attempts (should be 5)
   - **DeadLetterReason**: Why it was dead-lettered (e.g., `IndexingFailed`)
   - **DeadLetterDescription**: Exception details from the Worker
7. Click a message to see its full body (should be JSON with `documentId`, `sourceUrl`, `content`)

### Option 2: Azure CLI Service Bus Extension

```bash
# Install the Service Bus extension (one-time)
az extension add --name servicebus

# List dead-lettered messages
az servicebus queue dead-letter list \
  --resource-group <resource-group> \
  --namespace-name <service-bus-namespace> \
  --queue-name extracted-documents \
  --query "[].{MessageId:id, DeliveryCount:deliveryCount, Reason:deadLetterReason, Description:deadLetterDescription}" \
  -o table

# Peek at a specific dead-letter message (does not remove it)
az servicebus queue dead-letter peek \
  --resource-group <resource-group> \
  --namespace-name <service-bus-namespace> \
  --queue-name extracted-documents \
  --skip 0 \
  --top 1
```

### Option 3: PowerShell

```powershell
# Prerequisites: Azure.Messaging.ServiceBus NuGet package
# Install-Module -Name Az.ServiceBus

$ns = "<service-bus-namespace>.servicebus.windows.net"
$queueName = "extracted-documents"
$credential = (Get-AzAccessToken -ResourceUrl "https://servicebus.azure.net").Token

$auth = New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $credential)
$headers = @{ Authorization = $auth }

$dlqUri = "https://$ns/$queueName`/`$DeadLetterQueue/messages?api-version=2015-08"
$response = Invoke-RestMethod -Uri $dlqUri -Headers $headers -Method Get
$response | ConvertTo-Json
```

## Diagnosing Dead-Letter Root Causes

### 1. Check Worker Logs in Application Insights

```kusto
traces
| where timestamp > ago(1h)
| where severityLevel >= 3
| where message contains "Failed to process" or message contains "IndexingFailed"
| project timestamp, message, customDimensions.DocumentId, customDimensions.Error
| order by timestamp desc
```

### 2. Extract Details from Dead-Letter Reason and Description

- **Reason: `InvalidDocumentMessage`** → Message body is invalid JSON, missing documentId, or malformed sourceUrl
  - **Action**: Inspect the raw message body; fix and resubmit with corrected format
  
- **Reason: `IndexingFailed`** → Worker encountered an error during indexing
  - **Check**: AI Search logs, OpenAI throttling (429s), SQL deadlocks, network timeouts
  - **Action**: Investigate and fix the underlying service (Search, OpenAI, SQL), then resubmit
  
- **Reason: `LockLost`** → Message lock expired before processing completed
  - **Likely cause**: Worker processing took longer than 1-minute lock duration
  - **Action**: Check if Worker resource ran out of memory/CPU, or a single document's content was extremely large (>100K chunks); scale up Worker or split document

### 3. Common Root Causes

| Symptom | Root Cause | Fix |
| --- | --- | --- |
| AI Search returns 403 Forbidden | Worker identity lacks Search permissions | Verify Worker Container App has "Search Index Data Contributor" and "Search Service Contributor" roles |
| OpenAI returns 429 Too Many Requests | Throttling due to TPM capacity exceeded | Increase `embeddingCapacity` or `chatCapacity` in Bicep; reduce Worker MaxConcurrentCalls |
| SQL connection timeout | Database firewall or connection pool exhausted | Check SQL Server firewall allows Container App IP; reduce Worker MaxConcurrentCalls |
| Message size exceeds 256 KiB | Document extraction produced too much text | Note: Extractor rejects at send time; Worker shouldn't receive oversized messages |
| DeliveryCount < 5 but message is dead-lettered | Invalid message format | Check DeadLetterReason; likely InvalidDocumentMessage |

## Resubmitting Dead-Letter Messages

### Option 1: Azure Portal (Manual, One Message at a Time)

1. Open the dead-letter queue in Service Bus Explorer (see above)
2. Click **Receive** > **PeekLock** mode on a message
3. **Read the message**: Check the body and DeadLetterReason
4. Once the root cause is fixed:
   - Click **Requeue** to move it back to the main queue
   - (Or manually reconstruct and send a new message with corrected data)

### Option 2: PowerShell + Service Bus SDK

Create a script [scripts/resubmit-dead-letter.ps1](../../scripts/resubmit-dead-letter.ps1):

```powershell
# Resubmit a single dead-lettered message back to the main queue

param(
    [string]$ResourceGroup = $(throw "ResourceGroup is required"),
    [string]$ServiceBusNamespace = $(throw "ServiceBusNamespace is required"),
    [string]$DocumentId = $(throw "DocumentId is required (message ID to resubmit)")
)

# Ensure Azure.Messaging.ServiceBus package is installed
# dotnet add package Azure.Messaging.ServiceBus

$credential = New-Object Azure.Identity.DefaultAzureCredential
$namespace = "${ServiceBusNamespace}.servicebus.windows.net"
$client = New-Object Azure.Messaging.ServiceBus.ServiceBusClient($namespace, $credential)

$receiver = $client.CreateDeadLetterReceiver("extracted-documents")

try {
    # Receive one message from the dead-letter queue
    $message = $receiver.PeekMessage()
    
    if ($null -eq $message) {
        Write-Host "No messages in dead-letter queue."
        return
    }

    Write-Host "Found dead-lettered message:"
    Write-Host "  MessageId: $($message.MessageId)"
    Write-Host "  DeadLetterReason: $($message.DeadLetterReason)"
    Write-Host "  DeadLetterDescription: $($message.DeadLetterDescription)"
    Write-Host "  Body: $([System.Text.Encoding]::UTF8.GetString($message.Body))"
    Write-Host ""

    # Confirm before resubmitting
    $confirm = Read-Host "Resubmit this message to the main queue? (y/n)"
    if ($confirm -ne 'y') {
        Write-Host "Cancelled."
        return
    }

    # Receive and complete (removes from dead-letter)
    $receivedMessage = $receiver.ReceiveMessage([System.TimeSpan]::FromSeconds(30))
    
    if ($null -ne $receivedMessage) {
        # Resubmit to main queue
        $sender = $client.CreateSender("extracted-documents")
        $newMessage = New-Object Azure.Messaging.ServiceBus.ServiceBusMessage($receivedMessage.Body)
        $newMessage.MessageId = $receivedMessage.MessageId
        $newMessage.ContentType = $receivedMessage.ContentType
        
        # Copy any application properties
        foreach ($key in $receivedMessage.ApplicationProperties.Keys) {
            $newMessage.ApplicationProperties[$key] = $receivedMessage.ApplicationProperties[$key]
        }
        
        $sender.SendMessageAsync($newMessage).Wait()
        Write-Host "Message resubmitted to main queue: $($receivedMessage.MessageId)"
        
        # Complete (acknowledge) the dead-letter message removal
        $receiver.CompleteMessageAsync($receivedMessage).Wait()
        Write-Host "Dead-letter message removed from dead-letter queue."
        
        $sender.CloseAsync().Wait()
    }
}
finally {
    $receiver.CloseAsync().Wait()
    $client.DisposeAsync().Wait()
}
```

**Usage**:
```powershell
./scripts/resubmit-dead-letter.ps1 `
  -ResourceGroup "rg-docintake-dev" `
  -ServiceBusNamespace "di-dev-abc123-sb" `
  -DocumentId "<message-id-guid>"
```

### Option 3: C# Console Utility

Create [scripts/ResubmitDeadLetter.cs](../../scripts/ResubmitDeadLetter.cs):

```csharp
using Azure.Identity;
using Azure.Messaging.ServiceBus;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: ResubmitDeadLetter <namespace> <queue>");
            Console.WriteLine("  Example: ResubmitDeadLetter my-ns.servicebus.windows.net extracted-documents");
            return;
        }

        string namespaceFqdn = args[0];
        string queueName = args[1];

        var credential = new DefaultAzureCredential();
        var client = new ServiceBusClient(namespaceFqdn, credential);

        var receiver = client.CreateDeadLetterReceiver(queueName);
        
        try
        {
            // Peek first to show details
            var peekMessage = await receiver.PeekMessageAsync();
            if (peekMessage == null)
            {
                Console.WriteLine("No messages in dead-letter queue.");
                return;
            }

            Console.WriteLine($"Found dead-lettered message:");
            Console.WriteLine($"  MessageId: {peekMessage.MessageId}");
            Console.WriteLine($"  DeadLetterReason: {peekMessage.DeadLetterReason}");
            Console.WriteLine($"  DeadLetterDescription: {peekMessage.DeadLetterDescription}");
            Console.WriteLine($"  Body: {System.Text.Encoding.UTF8.GetString(peekMessage.Body)}");
            Console.WriteLine();

            Console.WriteLine("Resubmit to main queue? (y/n)");
            var confirm = Console.ReadLine();
            if (confirm?.ToLower() != "y")
            {
                Console.WriteLine("Cancelled.");
                return;
            }

            // Receive (not just peek)
            var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30));
            if (message != null)
            {
                var sender = client.CreateSender(queueName);
                var newMessage = new ServiceBusMessage(message.Body)
                {
                    MessageId = message.MessageId,
                    ContentType = message.ContentType
                };

                // Copy application properties
                foreach (var kvp in message.ApplicationProperties)
                {
                    newMessage.ApplicationProperties[kvp.Key] = kvp.Value;
                }

                await sender.SendMessageAsync(newMessage);
                Console.WriteLine($"Message resubmitted to main queue: {message.MessageId}");

                await receiver.CompleteMessageAsync(message);
                Console.WriteLine("Dead-letter message removed from dead-letter queue.");

                await sender.CloseAsync();
            }
        }
        finally
        {
            await receiver.CloseAsync();
            await client.DisposeAsync();
        }
    }
}
```

**Compile and run**:
```bash
csc ResubmitDeadLetter.cs -r:"C:\path\to\Azure.Identity.dll" -r:"C:\path\to\Azure.Messaging.ServiceBus.dll"
./ResubmitDeadLetter.exe "my-ns.servicebus.windows.net" "extracted-documents"
```

## Recovering Event Grid Dead-Letter Blobs

Event Grid dead-lettered blobs are stored in the `eventgrid-deadletter` container. Each blob contains the original Event Grid event that failed to be delivered to the Function.

### 1. Inspect the Dead-Letter Blob

```powershell
$storageName = "<storage-account-name>"  # From deployment output
az storage blob list --account-name $storageName --container-name eventgrid-deadletter `
  --auth-mode login

$blobName = "<blob-name-from-above>"
az storage blob download --account-name $storageName --container-name eventgrid-deadletter `
  --name $blobName --file deadletter-event.json --auth-mode login

cat deadletter-event.json | jq .
```

### 2. Understand the Event Content

The blob contains the Event Grid event in this format:

```json
{
  "eventType": "Microsoft.Storage.BlobCreated",
  "subject": "/blobServices/default/containers/documents/blobs/document-id/filename.pdf",
  "dataVersion": "2.0",
  "metadataVersion": "1",
  "eventTime": "2024-10-14T21:30:00Z",
  "data": {
    "api": "PutBlob",
    "clientRequestId": "...",
    "requestId": "...",
    "contentType": "application/pdf",
    "contentLength": 123456,
    "blobType": "BlockBlob",
    "url": "https://storage.blob.core.windows.net/documents/..."
  }
}
```

### 3. Replay the Event (Trigger Extraction Manually)

Once the Function App is healthy again:

**Option A**: Use Azure CLI to manually trigger the function with the blob:

```powershell
$functionName = "ExtractDocument"
$functionApp = "<function-app-name>"  # From deployment output
$resourceGroup = "<resource-group>"

# Send a direct HTTP call to the function (requires Entra permissions)
$functionUrl = "https://$functionApp.azurewebsites.net/api/$functionName"
$eventGridEvent = @{
  eventType = "Microsoft.Storage.BlobCreated"
  subject = "/blobServices/default/containers/documents/blobs/<document-id>/filename.pdf"
  eventTime = (Get-Date -AsUTC).ToString("o")
  data = @{
    url = "https://<storage>.blob.core.windows.net/documents/<document-id>/filename.pdf"
  }
} | ConvertTo-Json

$token = (az account get-access-token --resource "https://$functionApp.azurewebsites.net" --query accessToken -o tsv)
$headers = @{ Authorization = "Bearer $token"; "Content-Type" = "application/json" }

Invoke-RestMethod -Method Post -Uri $functionUrl -Headers $headers -Body $eventGridEvent
```

**Option B**: Simply re-upload the original document through the API `/documents` endpoint; a new event will be triggered.

### 4. Verify Recovery

Check Function logs and Service Bus queue:

```kusto
traces
| where timestamp > ago(30m)
| where message startswith "Extracted" or message startswith "Failed to extract"
| project timestamp, message, operation_Id
```

Confirm the document appears in AI Search and SQL:

```powershell
sqlcmd -S $sqlServer -d docintake --authentication-method ActiveDirectoryDefault `
  -Q "SELECT Id, SourceUrl, ChunkCount FROM dbo.Documents WHERE Id = '<document-id>'"
```

## Prevention

To prevent messages from being dead-lettered:

1. **Monitor Worker capacity**: Watch for timeouts (LockLost); scale up maxReplicas if needed
2. **Check role assignments**: Ensure Worker identity has all required Azure roles
3. **Verify service availability**: Monitor AI Search, OpenAI, and SQL before uploading bulk documents
4. **Set appropriate retry policies**: Current maxDeliveryCount=5 is reasonable; increase only if transient failures are common
5. **Implement dead-letter monitoring**: Alert on any messages entering the dead-letter queue (see Monitoring section below)

## Monitoring Dead-Letter Events

### Azure Monitor Alert

Create an alert rule to notify when messages are dead-lettered:

```powershell
$metricName = "DeadletterMessagesCount"
$namespace = "Microsoft.ServiceBus/namespaces"
$queueName = "extracted-documents"
$alertName = "DLQ-Alert-${queueName}"

az monitor metrics alert create \
  --resource-group <resource-group> \
  --name $alertName \
  --scopes "/subscriptions/<subscription>/resourcegroups/<rg>/providers/microsoft.servicebus/namespaces/<ns>" \
  --description "Alert when messages are dead-lettered" \
  --condition "avg $metricName > 0" \
  --window-size 5m \
  --evaluation-frequency 1m \
  --action email <admin-email>
```

### Application Insights Query

```kusto
customMetrics
| where name == "DLQCount"
| summarize Count=sum(value) by bin(timestamp, 1h)
| where Count > 0
```

## Testing the Runbook

To test this runbook without waiting for real failures:

```bash
# 1. Inject a bad message directly into the queue
az servicebus queue send \
  --resource-group <rg> \
  --namespace <ns> \
  --queue-name extracted-documents \
  --message "INVALID_JSON{not-valid"

# 2. Wait for Worker to process and dead-letter it (should be immediate since it's malformed)

# 3. Verify it appears in dead-letter queue
az servicebus queue dead-letter list --resource-group <rg> --namespace <ns> --queue-name extracted-documents

# 4. Follow the resubmit steps above to confirm the utility script works
```

## Summary

| Task | Steps |
| --- | --- |
| **Inspect dead-letter** | Portal SB Explorer, or Azure CLI `az servicebus queue dead-letter peek` |
| **Diagnose root cause** | Check DeadLetterReason/Description, Application Insights logs, service availability |
| **Resubmit one message** | Use Portal requeue or PowerShell script [scripts/resubmit-dead-letter.ps1](../../scripts/resubmit-dead-letter.ps1) |
| **Batch resubmit** | Write a loop over all dead-letter messages using the same script |
| **Fix Event Grid dead-letter** | Diagnose Function App health, inspect event blob, re-upload or manually trigger |

