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

# Resource providers used by infra/main.bicep. Registration is subscription-scoped and needs
# Microsoft.Resources/subscriptions/providers/register/action (for example, Contributor on the subscription).
REQUIRED_PROVIDERS=(
  Microsoft.App
  Microsoft.CognitiveServices
  Microsoft.Consumption
  Microsoft.ContainerRegistry
  Microsoft.EventGrid
  Microsoft.Insights
  Microsoft.KeyVault
  Microsoft.ManagedIdentity
  Microsoft.Network
  Microsoft.OperationalInsights
  Microsoft.Search
  Microsoft.ServiceBus
  Microsoft.Sql
  Microsoft.Storage
  Microsoft.Web
)

ensure_resource_providers() {
  local provider state subscription_id
  subscription_id="$(az account show --query id -o tsv)"
  for provider in "${REQUIRED_PROVIDERS[@]}"; do
    state="$(az provider show --namespace "$provider" --query registrationState -o tsv 2>/dev/null || echo Unknown)"
    if [[ "$state" == "Registered" ]]; then
      continue
    fi
    echo "Resource provider '$provider' is '$state'; registering..."
    if ! az provider register --namespace "$provider" --wait; then
      echo "Could not register resource provider '$provider' in subscription '$subscription_id'." >&2
      echo "Ask a subscription Owner/Contributor to run: az provider register --namespace $provider --subscription $subscription_id --wait" >&2
      exit 1
    fi
  done
}

ensure_resource_providers

report_failed_deployment() {
  local deployment="$1"
  local apps app nested_deployment

  echo "Failed ARM operations for deployment '$deployment':" >&2
  az deployment operation group list \
    --resource-group "$RESOURCE_GROUP" \
    --name "$deployment" \
    --query "[?properties.provisioningState=='Failed'].{Resource:properties.targetResource.id,Error:properties.statusMessage}" \
    --output json \
    || echo "Warning: could not retrieve failed ARM operations for '$deployment'." >&2

  for nested_deployment in docintake-api docintake-worker; do
    echo "Failed ARM operations for nested deployment '$nested_deployment':" >&2
    az deployment operation group list \
      --resource-group "$RESOURCE_GROUP" \
      --name "$nested_deployment" \
      --query "[?properties.provisioningState=='Failed'].{Resource:properties.targetResource.id,Error:properties.statusMessage}" \
      --output json \
      || echo "Warning: could not retrieve operations for '$nested_deployment'." >&2
  done

  if ! apps="$(az containerapp list \
    --resource-group "$RESOURCE_GROUP" \
    --query "[?ends_with(name, '-api') || ends_with(name, '-worker')].name" --output tsv)"; then
    echo "Warning: could not discover API/Worker Container Apps for diagnostics." >&2
    return 0
  fi

  if [[ -z "$apps" ]]; then
    echo "No API/Worker Container Apps found; revision diagnostics are unavailable." >&2
  fi

  while IFS= read -r app; do
    [[ -n "$app" ]] || continue
    echo "Revision provisioning details for '$app':" >&2
    az containerapp revision list \
      --resource-group "$RESOURCE_GROUP" --name "$app" \
      --query "[].{Name:name,Provisioning:properties.provisioningState,Health:properties.healthState,Details:properties.provisioningError}" \
      --output json \
      || echo "Warning: could not retrieve revisions for '$app'." >&2

    echo "Recent system logs for '$app':" >&2
    timeout 60s az containerapp logs show \
      --resource-group "$RESOURCE_GROUP" --name "$app" \
      --type system --tail 100 --follow false \
      || echo "Warning: system log retrieval failed or timed out for '$app'." >&2
  done <<< "$apps"
}

deploy_infrastructure() {
  local deployment="$1"
  local exit_code
  shift
  if az deployment group create \
    --name "$deployment" \
    --resource-group "$RESOURCE_GROUP" \
    "$@"; then
    return 0
  else
    exit_code=$?
    echo "Infrastructure deployment '$deployment' failed (exit code $exit_code). Collecting diagnostics..." >&2
    report_failed_deployment "$deployment"
    exit "$exit_code"
  fi
}

if [[ "${provision_infrastructure,,}" == "true" ]]; then
  echo "Previewing initial infrastructure bootstrap..."
  az deployment group what-if \
    --resource-group "$RESOURCE_GROUP" \
    --parameters "$PARAMETERS_FILE" bootstrap=true bootstrapExtractor=true workerImage=''

  echo "Deploying infrastructure bootstrap..."
  deploy_infrastructure "$BOOTSTRAP_DEPLOYMENT" \
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
deploy_infrastructure "$deployment_name" \
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
