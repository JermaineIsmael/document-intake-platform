#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Resubmit a dead-lettered message from the Service Bus dead-letter queue back to the main queue.

.DESCRIPTION
    This script receives a message from the Service Bus dead-letter queue, shows its details,
    and optionally resubmits it back to the main queue. Requires Azure.Messaging.ServiceBus
    SDK and DefaultAzureCredential (az login or MSI in Container App).

.PARAMETER ResourceGroup
    Azure resource group containing the Service Bus namespace.

.PARAMETER ServiceBusNamespace
    Service Bus namespace name (without .servicebus.windows.net).

.PARAMETER QueueName
    Queue name (default: extracted-documents).

.PARAMETER All
    If true, resubmit all dead-lettered messages without confirmation.

.EXAMPLE
    ./resubmit-dead-letter.ps1 -ResourceGroup "rg-docintake-dev" -ServiceBusNamespace "di-dev-abc123-sb"
    
    Interactive: shows first dead-letter message, prompts for resubmit confirmation.

.EXAMPLE
    ./resubmit-dead-letter.ps1 -ResourceGroup "rg-docintake-dev" -ServiceBusNamespace "di-dev-abc123-sb" -All
    
    Resubmit all dead-lettered messages without confirmation.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$ServiceBusNamespace,

    [Parameter(Mandatory = $false)]
    [string]$QueueName = "extracted-documents",

    [Parameter(Mandatory = $false)]
    [switch]$All
)

$ErrorActionPreference = "Stop"

# Check for required module
try {
    [void][System.Reflection.Assembly]::LoadFrom("C:\Program Files\PowerShell\Modules\Azure.Messaging.ServiceBus\1.0.0\lib\net6.0\Azure.Messaging.ServiceBus.dll")
}
catch {
    Write-Error "Azure.Messaging.ServiceBus SDK not found. Install with: dotnet add package Azure.Messaging.ServiceBus"
    exit 1
}

$namespaceFqdn = "${ServiceBusNamespace}.servicebus.windows.net"

Write-Host "Connecting to Service Bus namespace: $namespaceFqdn" -ForegroundColor Cyan
Write-Host "Queue: $QueueName" -ForegroundColor Cyan
Write-Host ""

try {
    # Use DefaultAzureCredential (az login, env vars, MSI, etc.)
    $credential = [Azure.Identity.DefaultAzureCredential]::new()
    $client = [Azure.Messaging.ServiceBus.ServiceBusClient]::new($namespaceFqdn, $credential)
    $receiver = $client.CreateDeadLetterReceiver($QueueName)

    $resubmittedCount = 0
    $skippedCount = 0

    while ($true) {
        # Peek first to show user what's there
        $peekMessage = $receiver.PeekMessageAsync().GetAwaiter().GetResult()
        
        if ($null -eq $peekMessage) {
            Write-Host "No more messages in dead-letter queue." -ForegroundColor Green
            break
        }

        Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
        Write-Host "Dead-lettered message found:" -ForegroundColor Cyan
        Write-Host "  MessageId:              $($peekMessage.MessageId)" -ForegroundColor Yellow
        Write-Host "  DeliveryCount:          $($peekMessage.DeliveryCount)" -ForegroundColor Yellow
        Write-Host "  DeadLetterReason:       $($peekMessage.DeadLetterReason)" -ForegroundColor Red
        Write-Host "  DeadLetterDescription:  $($peekMessage.DeadLetterDescription)" -ForegroundColor Red
        
        if ($peekMessage.Body.Length -gt 500) {
            Write-Host "  Body (first 500 chars):"
            Write-Host "    $([System.Text.Encoding]::UTF8.GetString($peekMessage.Body).Substring(0, 500))..." -ForegroundColor Gray
        } else {
            Write-Host "  Body:"
            Write-Host "    $([System.Text.Encoding]::UTF8.GetString($peekMessage.Body))" -ForegroundColor Gray
        }
        
        Write-Host ""

        if ($All) {
            $confirm = 'y'
        } else {
            Write-Host "Resubmit this message to the main queue? (y/n/q for quit)" -ForegroundColor Cyan
            $confirm = Read-Host "Choice"
        }

        if ($confirm -eq 'q') {
            Write-Host "Stopping." -ForegroundColor Yellow
            break
        }

        if ($confirm -eq 'y') {
            # Receive (not just peek) - removes from dead-letter queue
            $message = $receiver.ReceiveMessageAsync([System.TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()

            if ($null -ne $message) {
                try {
                    # Create sender and resubmit
                    $sender = $client.CreateSender($QueueName)
                    $newMessage = [Azure.Messaging.ServiceBus.ServiceBusMessage]::new($message.Body)
                    $newMessage.MessageId = $message.MessageId
                    $newMessage.ContentType = $message.ContentType

                    # Copy application properties
                    foreach ($kvp in $message.ApplicationProperties.GetEnumerator()) {
                        $newMessage.ApplicationProperties[$kvp.Key] = $kvp.Value
                    }

                    $sender.SendMessageAsync($newMessage).GetAwaiter().GetResult()
                    Write-Host "✓ Message resubmitted to main queue" -ForegroundColor Green

                    # Complete (acknowledge) the dead-letter message
                    $receiver.CompleteMessageAsync($message).GetAwaiter().GetResult()
                    Write-Host "✓ Dead-letter message removed from dead-letter queue" -ForegroundColor Green
                    $resubmittedCount++

                    $sender.CloseAsync().GetAwaiter().GetResult()
                }
                catch {
                    Write-Host "✗ Failed to resubmit: $_" -ForegroundColor Red
                }
            }
        }
        else {
            Write-Host "⊘ Skipped" -ForegroundColor Yellow
            $skippedCount++

            # Must still receive to move past this message (even if not resubmitting)
            # Actually, we just peek and loop; if not resubmitting, we stop to avoid infinite loop
            # on a message the user doesn't want to process
            if (-not $All) {
                Write-Host "Continuing to next message..." -ForegroundColor Yellow
                # Keep the message in dead-letter; move on
                break
            }
        }

        Write-Host ""
    }

    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "Summary:" -ForegroundColor Green
    Write-Host "  Resubmitted: $resubmittedCount" -ForegroundColor Green
    Write-Host "  Skipped: $skippedCount" -ForegroundColor Yellow
}
finally {
    $receiver.CloseAsync().GetAwaiter().GetResult()
    $client.DisposeAsync().GetAwaiter().GetResult()
}
