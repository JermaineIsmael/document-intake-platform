#!/usr/bin/env bash
set -euo pipefail

: "${RESOURCE_GROUP:?Set the Azure DevOps pipeline variable resourceGroup}"
: "${BUILD_ID:?Build.BuildId was not provided}"
: "${EXTRACTOR_PACKAGE:?Extractor ZIP package path was not provided}"
: "${API_IMAGE:?API image was not passed from the infrastructure deployment}"
: "${WORKER_IMAGE:?Worker image was not passed from the infrastructure deployment}"

if [[ -z "${EXTRACTOR_NAME:-}" ]]; then
  echo "Extractor Function App name was not set by the infrastructure deployment." >&2
  exit 1
fi
if [[ ! -f "$EXTRACTOR_PACKAGE" ]]; then
  echo "Extractor package not found: $EXTRACTOR_PACKAGE" >&2
  exit 1
fi

az functionapp deployment source config-zip \
  --resource-group "$RESOURCE_GROUP" \
  --name "$EXTRACTOR_NAME" \
  --src "$EXTRACTOR_PACKAGE" \
  --build-remote false \
  --timeout 1800

state="$(az functionapp show \
  --resource-group "$RESOURCE_GROUP" \
  --name "$EXTRACTOR_NAME" \
  --query state \
  -o tsv)"
if [[ "$state" != "Running" ]]; then
  echo "Extractor Function App is not running (state: $state)." >&2
  exit 1
fi

function_found=false
for attempt in $(seq 1 30); do
  functions="$(az functionapp function list \
    --resource-group "$RESOURCE_GROUP" \
    --name "$EXTRACTOR_NAME" \
    --query '[].name' \
    -o tsv)"
  if grep -Eq '(^|/)ExtractDocument$' <<< "$functions"; then
    function_found=true
    break
  fi
  echo "Waiting for ExtractDocument to be indexed (attempt $attempt/30)..."
  sleep 10
done

if [[ "$function_found" != "true" ]]; then
  echo "ExtractDocument was not indexed; Event Grid will remain disabled." >&2
  exit 1
fi

PARAMETERS_FILE="infra/main.bicepparam"
deployment_name="docintake-enable-events-${BUILD_ID}"
echo "Previewing Event Grid subscription deployment..."
az deployment group what-if \
  --resource-group "$RESOURCE_GROUP" \
  --parameters "$PARAMETERS_FILE" \
    bootstrap=false \
    bootstrapExtractor=false \
    apiImage="$API_IMAGE" \
    workerImage="$WORKER_IMAGE"

echo "Enabling Event Grid delivery to ExtractDocument..."
az deployment group create \
  --name "$deployment_name" \
  --resource-group "$RESOURCE_GROUP" \
  --parameters "$PARAMETERS_FILE" \
    bootstrap=false \
    bootstrapExtractor=false \
    apiImage="$API_IMAGE" \
    workerImage="$WORKER_IMAGE"
