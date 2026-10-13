param(
    [Parameter(Mandatory)]
    [uri] $ApiBaseUrl,

    [Parameter(Mandatory)]
    [string] $SamplePdf,

    [Parameter(Mandatory)]
    [string] $ResourceGroup,

    [Parameter(Mandatory)]
    [string] $ServiceBusNamespace,

    [string] $QueueName = 'extracted-documents',

    [int] $TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:DOCINTAKE_API_TOKEN)) {
    throw 'Set DOCINTAKE_API_TOKEN to a valid bearer token for the DocIntake API.'
}
if (-not (Test-Path -LiteralPath $SamplePdf -PathType Leaf)) {
    throw "Sample PDF not found: $SamplePdf"
}
if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 120) {
    throw 'TimeoutSeconds must be between 1 and 120.'
}

$sample = Get-Item -LiteralPath $SamplePdf
if ($sample.Extension -ine '.pdf') {
    throw 'SamplePdf must refer to a .pdf file.'
}

$baseline = az servicebus queue show `
    --resource-group $ResourceGroup `
    --namespace-name $ServiceBusNamespace `
    --name $QueueName `
    --query countDetails.activeMessageCount `
    --output tsv
if ($LASTEXITCODE -ne 0 -or $baseline -notmatch '^\d+$') {
    throw 'Unable to read the Service Bus active message count. Check Azure CLI login, RBAC, namespace, and queue.'
}

$response = Invoke-RestMethod `
    -Uri ([uri]::new($ApiBaseUrl, '/documents')) `
    -Method Post `
    -Headers @{ Authorization = "Bearer $env:DOCINTAKE_API_TOKEN" } `
    -Form @{ file = $sample }

if (-not $response.documentId) {
    throw 'The upload endpoint did not return a documentId.'
}
Write-Host "Upload accepted: $($response.documentId). Waiting for a new queue message..."

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
do {
    Start-Sleep -Seconds 5
    $count = az servicebus queue show `
        --resource-group $ResourceGroup `
        --namespace-name $ServiceBusNamespace `
        --name $QueueName `
        --query countDetails.activeMessageCount `
        --output tsv

    if ($LASTEXITCODE -ne 0 -or $count -notmatch '^\d+$') {
        throw 'Unable to read the Service Bus active message count while polling.'
    }
    if ([int64]$count -gt [int64]$baseline) {
        Write-Host "SUCCESS: active queue count increased from $baseline to $count. Uploaded documentId: $($response.documentId)"
        exit 0
    }
} while ([DateTimeOffset]::UtcNow -lt $deadline)

Write-Error "FAILURE: no active queue-count increase was observed within $TimeoutSeconds seconds for documentId $($response.documentId)."
exit 1
