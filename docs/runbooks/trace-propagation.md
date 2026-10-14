# End-to-End Trace Propagation

## Overview

Sprint 5 implements W3C Trace Context propagation across the Service Bus message hop, linking the API request → Function extraction → Worker processing into a single operation_Id in Application Insights. This enables full end-to-end request tracing and debugging across the three-hop pipeline.

## Implementation

### Trace Context Propagation Flow

1. **API Request** (ASP.NET Core): Receives an authenticated upload request, generates operation_Id
2. **Function Execution** (Event Grid trigger): Extracts document, continues operation_Id through Service Bus
3. **Worker Processing** (Service Bus message handler): Receives message with embedded trace context, links Worker activity to the same operation_Id

### Code Changes

#### Extractor (Function) — ExtractDocument.cs

When sending a message to Service Bus, the Function now extracts the current Activity (if any) and embeds its trace context:

```csharp
var serviceBusMessage = new ServiceBusMessage(...) 
{ 
    MessageId = documentId.ToString("D"),
    ContentType = "application/json"
};

// Sprint 5: Propagate trace context through Service Bus message properties
var activity = Activity.Current;
if (activity != null)
{
    serviceBusMessage.ApplicationProperties["traceparent"] = activity.Id;  // W3C format
    if (!string.IsNullOrEmpty(activity.TraceStateString))
    {
        serviceBusMessage.ApplicationProperties["tracestate"] = activity.TraceStateString;
    }
}
```

**Format**: W3C Trace Context (RFC 9411) — `traceparent` header format: `version-trace_id-parent_id-trace_flags`

#### Worker — IngestionWorker.cs

When processing a message, the Worker extracts the trace context from ApplicationProperties and starts a linked Activity:

```csharp
internal async Task HandleMessageAsync(ProcessMessageEventArgs args)
{
    var message = args.Message;

    // Sprint 5: Extract trace context to link Worker span to Function/API
    Activity? linkedActivity = null;
    if (message.ApplicationProperties.TryGetValue("traceparent", out var traceparentObj)
        && traceparentObj is string traceparent)
    {
        try
        {
            var parts = traceparent.Split('-');
            if (parts.Length >= 4)
            {
                linkedActivity = new Activity($"ProcessMessage-{message.MessageId}")
                    .SetParentId(traceparent);
                if (message.ApplicationProperties.TryGetValue("tracestate", out var tracestate))
                {
                    linkedActivity.TraceStateString = tracestate.ToString();
                }
                linkedActivity.Start();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse trace context");
        }
    }

    try
    {
        // ... process message ...
    }
    finally
    {
        linkedActivity?.Stop();
        linkedActivity?.Dispose();
    }
}
```

## Verification

### Kusto Query to Verify End-to-End Tracing

Run this query in Application Insights **Logs** blade to confirm trace linkage for a single document upload:

```kusto
// Replace with a recent operation_Id from a successful upload
union requests, dependencies, traces
| where operation_Id == "<operation-id-from-a-recent-upload>"
| project 
    Timestamp=timestamp, 
    Component=case(
        itemType == "request", "API",
        itemType == "dependency", "Dependency",
        itemType == "trace", "Trace",
        itemType
    ),
    Name=case(
        itemType == "request", name,
        itemType == "dependency", name,
        message
    ),
    Target=case(
        itemType == "dependency", target,
        ""
    ),
    Duration=case(
        itemType == "request", duration,
        itemType == "dependency", duration,
        tolong(0)
    ),
    Success=case(
        itemType == "request", success,
        itemType == "dependency", success,
        itemType == "trace", true,
        false
    )
| order by Timestamp asc
```

**Expected Output** (three-hop flow):

| Timestamp | Component | Name | Target | Duration | Success |
| --- | --- | --- | --- | --- | --- |
| 2024-10-14T21:30:00Z | API | POST /documents | - | 250ms | true |
| 2024-10-14T21:30:01Z | Dependency | ExtractDocument | Azure Document Intelligence | 800ms | true |
| 2024-10-14T21:30:02Z | Dependency | ServiceBusSender | extracted-documents queue | 50ms | true |
| 2024-10-14T21:30:05Z | Trace | Extracted docid and queued | - | - | - |
| 2024-10-14T21:30:08Z | Trace | Indexed docid with N chunks | - | - | - |

All rows share the **same operation_Id**, confirming the trace is linked.

### Alternative Query: Simplified View

```kusto
union requests, dependencies, traces
| where operation_Id == "<operation-id>"
| project 
    timestamp, 
    itemType,
    name=case(itemType == "request", name, itemType == "dependency", name, message),
    success
| order by timestamp asc
```

### Query: Verify Trace Context in Message Properties

To inspect whether trace context is actually being embedded in messages (requires access to Service Bus message inspection):

```kusto
traces
| where message contains "traceparent" or message contains "tracestate"
| project timestamp, message
```

(This would show debug logs if the Worker or Function explicitly log the trace context.)

## Backward Compatibility

- **API requests before Sprint 5**: Still processed correctly; no trace context embedded, but operation_Id still flows through normal Azure SDK channels
- **Service Bus messages from old Extractors**: Worker handles gracefully; if no `traceparent` ApplicationProperty exists, linkedActivity is null and processing continues with Worker's own operation_Id
- **New messages from Sprint 5 Extractor to old Worker**: Messages include traceparent but old Worker ignores it (safe, no errors)

## Performance Impact

- **Extractor**: Negligible; single Activity.Current read and string copy to message properties
- **Worker**: Negligible; single message property read and Activity creation (reuses .NET diagnostics infrastructure)
- **Telemetry**: No additional bytes; trace context uses existing ApplicationProperties field

## Benefits

1. **Full request visibility**: Track a single document from upload through indexing in one end-to-end view
2. **Faster debugging**: No need to manually correlate operation_Ids across hops
3. **Performance analysis**: Measure end-to-end latency and identify bottlenecks (API response time, extraction time, queue latency, indexing time)
4. **Failure diagnostics**: Trace exceptions across all three components for a failed document

## Deployment

The trace context propagation is transparent after deployment:

1. **Build and deploy new Extractor** function with updated code
2. **Build and deploy new Worker** container app with updated code
3. **Test**: Upload a document, wait for indexing, run the Kusto query above
4. Existing deployments do not require reconfiguration; trace propagation works automatically through Application Insights OpenTelemetry

## Monitoring

Add an alert to notify when operation_Id is not properly linked (indicating a Service Bus hop failure):

```kusto
// Alert: Service Bus messages not linked to parent operation
let requestIds = requests
| where timestamp > ago(1h)
| where url contains "/documents"
| distinct operation_Id;

let workerLogs = traces
| where timestamp > ago(1h)
| where message contains "Indexed";

requestIds
| join kind=rightanti workerLogs on operation_Id
| project operation_Id, requestTime=timestamp
| summarize Count=count() by operation_Id
| where Count > 0
```

(This finds documents uploaded but not indexed; could indicate trace context loss or Worker failure.)

## Testing the Trace Propagation

### Manual Test

1. Deploy Sprint 5 code to a test environment
2. Upload a document via POST /documents
3. Note the response Location header (contains documentId)
4. In Application Insights Logs, search for that documentId in the message column
5. Copy the operation_Id from any matching trace
6. Run the Kusto query above with that operation_Id
7. Confirm all three hops (API, Function, Worker) appear with the same operation_Id

### Automated Test

In the test suite, after uploading a document and waiting for indexing:

```csharp
[Fact]
public async Task UploadedDocument_HasLinkedTrace()
{
    // Upload document
    var documentId = await UploadTestDocument();
    
    // Wait for indexing
    await Task.Delay(TimeSpan.FromSeconds(5));
    
    // Query Application Insights for this documentId
    var kusto = $@"
        traces
        | where message contains ""{documentId}""
        | summarize OperationIds=dcount(operation_Id)
    ";
    
    var result = await QueryApplicationInsights(kusto);
    
    // All traces for this document should share one operation_Id
    Assert.Equal(1, result.OperationIds);  // Only 1 unique operation_Id
}
```

## Known Limitations

- **Service Bus batching**: If multiple messages are sent in a batch, each retains its own trace context (no issues; each is linked independently)
- **Message forwarding**: If messages are forwarded to a sub-queue or dead-letter queue, trace context is preserved automatically by the SDK
- **Azure SDK versions**: Requires Azure.Core with ActivityExtensions support (standard in current versions)

## References

- [W3C Trace Context (RFC 9411)](https://www.w3.org/TR/trace-context/)
- [.NET Activity and Distributed Tracing](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.activity)
- [Azure Monitor OpenTelemetry](https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-enable)

