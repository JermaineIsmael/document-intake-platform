#!/usr/bin/env bash
set -euo pipefail

: "${RESOURCE_GROUP:?Set the Azure DevOps pipeline variable resourceGroup}"
: "${BUILD_ID:?Build.BuildId was not provided}"

PARAMETERS_FILE="infra/main.bicepparam"
BOOTSTRAP_DEPLOYMENT="docintake-bootstrap"
provision_infrastructure="${PROVISION_INFRASTRUCTURE:-false}"

if [[ ! -f "$PARAMETERS_FILE" ]]; then
  echo "Bicep parameters file not found: $PARAMETERS_FILE" >&2
  exit 1
fi

az bicep install

if [[ "${provision_infrastructure,,}" == "true" ]]; then
  echo "Previewing initial infrastructure bootstrap..."
  az deployment group what-if \
    --resource-group "$RESOURCE_GROUP" \
    --parameters "$PARAMETERS_FILE" bootstrap=true bootstrapExtractor=true workerImage=''

  echo "Deploying infrastructure bootstrap..."
  az deployment group create \
    --name "$BOOTSTRAP_DEPLOYMENT" \
    --resource-group "$RESOURCE_GROUP" \
    --parameters "$PARAMETERS_FILE" bootstrap=true bootstrapExtractor=true workerImage=''
fi

registry_names="$(az acr list --resource-group "$RESOURCE_GROUP" --query '[].name' -o tsv)"
if [[ -z "$registry_names" ]]; then
  registry_count=0
else
  mapfile -t registry_names <<< "$registry_names"
  registry_count="${#registry_names[@]}"
fi
if [[ "$registry_count" -ne 1 ]]; then
  echo "Expected exactly one ACR in resource group '$RESOURCE_GROUP'; found $registry_count." >&2
  echo "Run this pipeline once with provisionInfrastructure=true to create the registry." >&2
  exit 1
fi

registry_name="${registry_names[0]}"
registry_server="$(az acr show --name "$registry_name" --query loginServer -o tsv)"
api_image="${registry_server}/docintake-api:${BUILD_ID}"
worker_image="${registry_server}/docintake-worker:${BUILD_ID}"

docker info > /dev/null
az acr login --name "$registry_name"

echo "Building API image $api_image..."
docker build \
  --tag "$api_image" \
  --file src/DocIntake.Api/Dockerfile .

echo "Building Worker image $worker_image..."
docker build \
  --tag "$worker_image" \
  --file src/DocIntake.Worker/Dockerfile .

echo "Pushing images to $registry_server..."
docker push "$api_image"
docker push "$worker_image"

deployment_name="docintake-apps-${BUILD_ID}"
echo "Previewing API, Worker, and Extractor infrastructure..."
az deployment group what-if \
  --resource-group "$RESOURCE_GROUP" \
  --parameters "$PARAMETERS_FILE" \
    bootstrap=false \
    bootstrapExtractor=true \
    apiImage="$api_image" \
    workerImage="$worker_image"

echo "Deploying API, Worker, and Extractor infrastructure..."
az deployment group create \
  --name "$deployment_name" \
  --resource-group "$RESOURCE_GROUP" \
  --parameters "$PARAMETERS_FILE" \
    bootstrap=false \
    bootstrapExtractor=true \
    apiImage="$api_image" \
    workerImage="$worker_image"

extractor_name="$(az deployment group show \
  --name "$deployment_name" \
  --resource-group "$RESOURCE_GROUP" \
  --query properties.outputs.extractorName.value \
  -o tsv)"

if [[ -z "$extractor_name" ]]; then
  echo "Deployment did not return the Extractor Function App name." >&2
  exit 1
fi

echo "##vso[task.setvariable variable=extractorName]$extractor_name"
echo "##vso[task.setvariable variable=apiImage]$api_image"
echo "##vso[task.setvariable variable=workerImage]$worker_image"
echo "Infrastructure is ready. Extractor Function App: $extractor_name"
