#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Load test: concurrently upload N documents to the DocIntake API.

.DESCRIPTION
    Uploads N documents concurrently to test API scalability, Worker throughput, and identify
    throttling limits. Reports success/failure counts and timing. Use with `test-load-completion.sql`
    to verify all documents eventually complete indexing.

.PARAMETER ApiBaseUrl
    Base URL of the DocIntake API (e.g., https://api-fqdn).

.PARAMETER Token
    Bearer token for authentication (Entra ID access token).

.PARAMETER SamplePdf
    Path to a sample PDF file to upload (copied N times).

.PARAMETER DocumentCount
    Number of documents to upload concurrently (default 20).

.PARAMETER Verbose
    Print detailed progress for each upload.

.EXAMPLE
    $env:DOCINTAKE_API_TOKEN = (az account get-access-token --resource api://xxxx --query accessToken -o tsv)
    ./load-test-upload.ps1 `
      -ApiBaseUrl "https://my-api.azurecontainers.io" `
      -Token $env:DOCINTAKE_API_TOKEN `
      -SamplePdf "C:\documents\sample.pdf" `
      -DocumentCount 20

.EXAMPLE
    # Run with verbose output
    ./load-test-upload.ps1 `
      -ApiBaseUrl "https://my-api.azurecontainers.io" `
      -Token $token `
      -SamplePdf "sample.pdf" `
      -DocumentCount 5 `
      -Verbose

.NOTES
    After the load test completes:
    1. Note the returned documentIds
    2. Wait 30-60 seconds for processing
    3. Run test-load-completion.sql to verify all documents are indexed in SQL and Search
    4. Check Worker and Function logs for HTTP 429 (throttling) or other errors
    5. Confirm Worker Container App scaled during the burst and back afterward
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$Token,

    [Parameter(Mandatory = $true)]
    [string]$SamplePdf,

    [Parameter(Mandatory = $false)]
    [int]$DocumentCount = 20,

    [Parameter(Mandatory = $false)]
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SamplePdf)) {
    Write-Error "Sample PDF not found: $SamplePdf"
    exit 1
}

if (-not $ApiBaseUrl.StartsWith("https://")) {
    Write-Error "ApiBaseUrl must use HTTPS"
    exit 1
}

$sampleFileSize = (Get-Item $SamplePdf).Length
Write-Host "Load Test Configuration:" -ForegroundColor Cyan
Write-Host "  API Base URL:      $ApiBaseUrl"
Write-Host "  Document Count:    $DocumentCount"
Write-Host "  Sample PDF Size:   $([Math]::Round($sampleFileSize / 1MB, 2)) MB"
Write-Host "  Total Upload Size: $([Math]::Round(($sampleFileSize * $DocumentCount) / 1MB, 2)) MB"
Write-Host ""

# Prepare temporary directory
$tempDir = Join-Path $env:TEMP "docintake-load-test"
if (Test-Path $tempDir) {
    Remove-Item $tempDir -Recurse -Force
}
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

Write-Host "Preparing test documents..." -ForegroundColor Yellow
for ($i = 1; $i -le $DocumentCount; $i++) {
    $destPath = Join-Path $tempDir "doc-$i.pdf"
    Copy-Item $SamplePdf -Destination $destPath
}
Write-Host "✓ Prepared $DocumentCount test documents in $tempDir" -ForegroundColor Green
Write-Host ""

# Start concurrent uploads
Write-Host "Starting concurrent uploads..." -ForegroundColor Cyan
$startTime = Get-Date
$uploadJobs = @()
$documentIds = @()
$uploadResults = @{}

for ($i = 1; $i -le $DocumentCount; $i++) {
    $docPath = Join-Path $tempDir "doc-$i.pdf"
    
    $job = Start-Job -ScriptBlock {
        param($url, $auth, $file, $docNum)
        
        $headers = @{ Authorization = "Bearer $auth" }
        $form = @{ file = Get-Item $file }
        
        $jobStartTime = Get-Date
        try {
            $response = Invoke-RestMethod -Method Post `
                -Uri "$url/documents" `
                -Headers $headers `
                -Form $form `
                -TimeoutSec 30
            
            $jobDuration = (Get-Date) - $jobStartTime
            
            return @{
                DocNum = $docNum
                Status = "Success"
                DocumentId = $response.documentId
                StatusCode = 202
                Duration = $jobDuration
                Error = $null
            }
        }
        catch [Microsoft.PowerShell.Commands.HttpResponseException] {
            $jobDuration = (Get-Date) - $jobStartTime
            return @{
                DocNum = $docNum
                Status = "Failed"
                DocumentId = $null
                StatusCode = $_.Exception.Response.StatusCode.Value__
                Duration = $jobDuration
                Error = $_.Exception.Message
            }
        }
        catch {
            $jobDuration = (Get-Date) - $jobStartTime
            return @{
                DocNum = $docNum
                Status = "Failed"
                DocumentId = $null
                StatusCode = 0
                Duration = $jobDuration
                Error = $_.Exception.Message
            }
        }
    } -ArgumentList $ApiBaseUrl, $Token, $docPath, $i
    
    $uploadJobs += $job
}

# Wait for all uploads to complete
Write-Host "Waiting for all uploads to complete..." -ForegroundColor Yellow
$completedCount = 0
while ($uploadJobs.Count -gt 0) {
    $finished = $uploadJobs | Where-Object { $_.State -ne 'Running' }
    if ($finished.Count -gt 0) {
        foreach ($job in $finished) {
            $result = Receive-Job -Job $job
            $uploadResults[$result.DocNum] = $result
            
            if ($Verbose -or $result.Status -ne "Success") {
                $status = if ($result.Status -eq "Success") { "✓" } else { "✗" }
                Write-Host "$status  Doc-$($result.DocNum): $($result.Status) (HTTP $($result.StatusCode)) - $($result.Duration.TotalMilliseconds)ms"
                if ($result.Error) {
                    Write-Host "   Error: $($result.Error)" -ForegroundColor Red
                }
            }
            
            Remove-Job -Job $job
            $uploadJobs = $uploadJobs | Where-Object { $_.Id -ne $job.Id }
            $completedCount++
        }
        
        if ($completedCount % 5 -eq 0) {
            Write-Host "  Progress: $completedCount / $DocumentCount" -ForegroundColor Cyan
        }
    }
    
    Start-Sleep -Milliseconds 100
}

$uploadEndTime = Get-Date
$uploadDuration = $uploadEndTime - $startTime

# Analyze results
$successful = $uploadResults.Values | Where-Object { $_.Status -eq "Success" }
$failed = $uploadResults.Values | Where-Object { $_.Status -ne "Success" }
$documentIds = $successful | ForEach-Object { $_.DocumentId }

Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "Load Test Results:" -ForegroundColor Green
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""

Write-Host "  Total Documents:      $DocumentCount" -ForegroundColor Cyan
Write-Host "  Successful (202):     $($successful.Count)" -ForegroundColor $(if ($successful.Count -eq $DocumentCount) { "Green" } else { "Yellow" })
Write-Host "  Failed:               $($failed.Count)" -ForegroundColor $(if ($failed.Count -eq 0) { "Green" } else { "Red" })
Write-Host "  Total Upload Time:    $([Math]::Round($uploadDuration.TotalSeconds, 2))s" -ForegroundColor Cyan
Write-Host "  Avg Time per Upload:  $([Math]::Round(($uploadDuration.TotalMilliseconds / $DocumentCount), 0))ms" -ForegroundColor Cyan
Write-Host ""

if ($failed.Count -gt 0) {
    Write-Host "  Failed uploads by status code:" -ForegroundColor Yellow
    $failed | Group-Object StatusCode | ForEach-Object {
        Write-Host "    HTTP $($_.Name): $($_.Count)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Document IDs (for verification):" -ForegroundColor Cyan
$documentIds | ForEach-Object { Write-Host "  $_" }

# Cleanup
Remove-Item $tempDir -Recurse -Force

Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "Next Steps:" -ForegroundColor Green
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""
Write-Host "1. Wait 60-90 seconds for document extraction and indexing"
Write-Host ""
Write-Host "2. Verify all documents are indexed:"
Write-Host ""
Write-Host "   SELECT COUNT(*) as IndexedCount FROM dbo.Documents" -ForegroundColor Yellow
Write-Host "   WHERE Id IN ($(($documentIds | ForEach-Object { \"'$_'\" }) -join ','))"
Write-Host ""
Write-Host "3. Check for throttling (HTTP 429) in logs:"
Write-Host ""
Write-Host "   traces" -ForegroundColor Yellow
Write-Host "   | where timestamp > ago(30m)" -ForegroundColor Yellow
Write-Host "   | where message contains '429' or message contains 'throttled'" -ForegroundColor Yellow
Write-Host "   | summarize Count=count() by message" -ForegroundColor Yellow
Write-Host ""
Write-Host "4. Monitor Worker Container App scaling:"
Write-Host ""
Write-Host "   az containerapp revision list --name di-dev-<suffix>-worker" -ForegroundColor Yellow
Write-Host "   --resource-group <rg> --query '[].{active:properties.active,replicas:properties.replicas}'" -ForegroundColor Yellow
Write-Host ""
Write-Host "5. Review Worker logs for errors:"
Write-Host ""
Write-Host "   az containerapp logs show --name di-dev-<suffix>-worker" -ForegroundColor Yellow
Write-Host "   --resource-group <rg> --type application --tail 50" -ForegroundColor Yellow
Write-Host ""

# Save results to file
$resultsFile = "load-test-results.json"
@{
    timestamp = (Get-Date -Format 'o')
    documentCount = $DocumentCount
    successfulUploads = $successful.Count
    failedUploads = $failed.Count
    totalDurationSeconds = [Math]::Round($uploadDuration.TotalSeconds, 2)
    avgTimePerUploadMs = [Math]::Round(($uploadDuration.TotalMilliseconds / $DocumentCount), 0)
    documentIds = $documentIds
    failuresByStatusCode = ($failed | Group-Object StatusCode | ForEach-Object { @{ code = [int]$_.Name; count = $_.Count } })
} | ConvertTo-Json | Out-File $resultsFile

Write-Host "Results saved to: $resultsFile" -ForegroundColor Green
