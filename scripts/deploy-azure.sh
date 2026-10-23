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
az config set extension.use_dynamic_install=yes_without_prompt > /dev/null

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

resolved_parameters="$(az bicep build-params --file "$PARAMETERS_FILE" --stdout)"

# Reads a value from the resolved parameters file (empty when the parameter is not set there).
parameter_value() {
  python3 -c '
import json, sys
parameters = json.loads(json.load(sys.stdin)["parametersJson"])["parameters"]
print(parameters.get(sys.argv[1], {}).get("value", ""))
' "$1" <<< "$resolved_parameters"
}

sql_admin_group_object_id="$(parameter_value sqlAdminGroupObjectId)"
sql_server_name="$(parameter_value sqlServerNameOverride)"
if [[ -z "$sql_server_name" ]]; then
  # Without an override, main.bicep generates di-<env>-<suffix>-sql.
  mapfile -t generated_sql_servers < <(az sql server list --resource-group "$RESOURCE_GROUP" \
    --query "[?starts_with(name, 'di-') && ends_with(name, '-sql')].name" -o tsv)
  if [[ "${#generated_sql_servers[@]}" -gt 1 ]]; then
    echo "Found several generated SQL servers in '$RESOURCE_GROUP'; set sqlServerNameOverride to choose one." >&2
    exit 1
  fi
  sql_server_name="${generated_sql_servers[0]:-}"
fi

sql_server_exists=false
if [[ -n "$sql_server_name" ]] \
  && az sql server show --resource-group "$RESOURCE_GROUP" --name "$sql_server_name" --query id -o tsv > /dev/null 2>&1; then
  sql_server_exists=true
fi
echo "SQL server '${sql_server_name:-<generated>}' exists: $sql_server_exists"

# Records the current SQL authentication state so drift is visible in the pipeline log. The deployment
# then converges it: the Entra admin group becomes the only administrator and Entra-only auth is on.
report_sql_authentication() {
  local stage="$1" admin_sid entra_only
  admin_sid="$(az sql server ad-admin list --resource-group "$RESOURCE_GROUP" --server-name "$sql_server_name" \
    --query '[0].sid' -o tsv 2>/dev/null || true)"
  entra_only="$(az sql server ad-only-auth get --resource-group "$RESOURCE_GROUP" --name "$sql_server_name" \
    --query azureAdOnlyAuthentication -o tsv 2>/dev/null || true)"
  echo "SQL authentication ($stage): Entra admin SID='${admin_sid:-<none>}', Entra-only='${entra_only:-<unknown>}'."
  if [[ "${admin_sid,,}" != "${sql_admin_group_object_id,,}" || "${entra_only,,}" != "true" ]]; then
    return 1
  fi
}

if [[ "$sql_server_exists" == "true" ]]; then
  report_sql_authentication "before deployment" \
    || echo "SQL authentication drift detected; this deployment restores the configured Entra admin group and Entra-only authentication."
fi

common_parameters=(
  --parameters "$PARAMETERS_FILE"
  sqlServerExists="$sql_server_exists"
)

if [[ "${provision_infrastructure,,}" == "true" ]]; then
  echo "Previewing initial infrastructure bootstrap..."
  az deployment group what-if \
    --resource-group "$RESOURCE_GROUP" \
    "${common_parameters[@]}" bootstrap=true bootstrapExtractor=true workerImage=''

  echo "Deploying infrastructure bootstrap..."
  deploy_infrastructure "$BOOTSTRAP_DEPLOYMENT" \
    "${common_parameters[@]}" bootstrap=true bootstrapExtractor=true workerImage=''

  # The bootstrap created the server (with its administrator) if it did not exist.
  common_parameters=(
    --parameters "$PARAMETERS_FILE"
    sqlServerExists=true
  )
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
migration_image="${registry_server}/docintake-migrations:${BUILD_ID}"

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

echo "Building database migration image $migration_image..."
docker build \
  --tag "$migration_image" \
  --file src/DocIntake.Migrations/Dockerfile .

echo "Pushing images to $registry_server..."
docker push "$api_image"
docker push "$worker_image"
docker push "$migration_image"

# Phase 1 keeps the currently running application images, so new code never runs against an
# unmigrated schema. A first deployment has no running image and uses the new one.
current_image() {
  local suffix="$1" app
  app="$(az containerapp list --resource-group "$RESOURCE_GROUP" \
    --query "[?ends_with(name, '-${suffix}')].name | [0]" -o tsv 2>/dev/null || true)"
  [[ -n "$app" ]] || return 0
  az containerapp show --resource-group "$RESOURCE_GROUP" --name "$app" \
    --query 'properties.template.containers[0].image' -o tsv 2>/dev/null || true
}

phase1_api_image="$(current_image api)"
phase1_api_image="${phase1_api_image:-$api_image}"
phase1_worker_image="$(current_image worker)"

infrastructure_deployment="docintake-data-${BUILD_ID}"
infrastructure_parameters=(
  "${common_parameters[@]}"
  bootstrap=false
  bootstrapExtractor=true
  apiImage="$phase1_api_image"
  workerImage="$phase1_worker_image"
  migrationImage="$migration_image"
)

echo "Previewing infrastructure and database migration job..."
az deployment group what-if \
  --resource-group "$RESOURCE_GROUP" \
  "${infrastructure_parameters[@]}"

echo "Deploying infrastructure and database migration job (applications keep their current images)..."
deploy_infrastructure "$infrastructure_deployment" "${infrastructure_parameters[@]}"

deployment_output() {
  az deployment group show \
    --name "$1" \
    --resource-group "$RESOURCE_GROUP" \
    --query "properties.outputs.$2.value" \
    -o tsv
}

migration_job_name="$(deployment_output "$infrastructure_deployment" migrationJobName)"
migrator_principal_id="$(deployment_output "$infrastructure_deployment" sqlMigratorPrincipalId)"
sql_server_name="$(deployment_output "$infrastructure_deployment" sqlServerName)"
if [[ -z "$migration_job_name" || -z "$migrator_principal_id" || -z "$sql_server_name" ]]; then
  echo "Deployment did not return the migration job, migrator identity, or SQL server name." >&2
  exit 1
fi

report_sql_authentication "after deployment" || {
  echo "SQL authentication did not converge to the configured Entra admin group with Entra-only authentication." >&2
  exit 1
}

# The migrator authenticates as a member of the SQL Entra admin group. Adding it requires the group's
# owner or a Groups Administrator; this is a one-time step per environment.
ensure_migrator_admin_membership() {
  local is_member
  if ! is_member="$(az ad group member check --group "$sql_admin_group_object_id" \
    --member-id "$migrator_principal_id" --query value -o tsv 2>/dev/null)"; then
    echo "Warning: could not read membership of SQL admin group $sql_admin_group_object_id (needs Microsoft Graph read access); continuing." >&2
    return 0
  fi
  if [[ "$is_member" == "true" ]]; then
    echo "Migration identity $migrator_principal_id is a member of the SQL admin group."
    return 0
  fi

  echo "Adding migration identity $migrator_principal_id to SQL admin group $sql_admin_group_object_id..."
  if ! az ad group member add --group "$sql_admin_group_object_id" --member-id "$migrator_principal_id"; then
    echo "The pipeline identity cannot add members to the SQL admin group. A group owner must run once:" >&2
    echo "  az ad group member add --group $sql_admin_group_object_id --member-id $migrator_principal_id" >&2
    exit 1
  fi
  echo "Added. A new membership can take several minutes to reach Azure SQL; rerun the pipeline if the first migration login fails."
}

ensure_migrator_admin_membership

run_database_migrations() {
  local execution status deadline
  echo "Starting database migration job '$migration_job_name'..."
  execution="$(az containerapp job start --resource-group "$RESOURCE_GROUP" --name "$migration_job_name" \
    --query name -o tsv)"
  if [[ -z "$execution" ]]; then
    echo "Could not start the database migration job." >&2
    exit 1
  fi

  deadline=$((SECONDS + 2100))
  while true; do
    status="$(az containerapp job execution show --resource-group "$RESOURCE_GROUP" --name "$migration_job_name" \
      --job-execution-name "$execution" --query properties.status -o tsv 2>/dev/null || echo Unknown)"
    case "$status" in
      Succeeded)
        echo "Database migration execution '$execution' succeeded."
        return 0
        ;;
      Failed | Stopped | Degraded)
        break
        ;;
    esac
    if (( SECONDS >= deadline )); then
      echo "Timed out waiting for database migration execution '$execution' (last status: $status)." >&2
      break
    fi
    echo "Database migration execution '$execution' is $status; waiting..."
    sleep 15
  done

  echo "Database migration execution '$execution' did not succeed (status: $status). Applications were not updated." >&2
  timeout 120s az containerapp job logs show --resource-group "$RESOURCE_GROUP" --name "$migration_job_name" \
    --execution "$execution" --container migrations --tail 200 --follow false \
    || echo "Warning: could not retrieve migration logs; query ContainerAppConsoleLogs_CL for job '$migration_job_name' in Log Analytics." >&2
  exit 1
}

run_database_migrations

# The server exists now, so its authentication is managed through the child resources.
common_parameters=(
  --parameters "$PARAMETERS_FILE"
  sqlServerExists=true
)

deployment_name="docintake-apps-${BUILD_ID}"
echo "Deploying API and Worker images after a successful migration..."
deploy_infrastructure "$deployment_name" \
  "${common_parameters[@]}" \
  bootstrap=false \
  bootstrapExtractor=true \
  apiImage="$api_image" \
  workerImage="$worker_image" \
  migrationImage="$migration_image"

extractor_name="$(deployment_output "$deployment_name" extractorName)"

if [[ -z "$extractor_name" ]]; then
  echo "Deployment did not return the Extractor Function App name." >&2
  exit 1
fi

echo "##vso[task.setvariable variable=extractorName]$extractor_name"
echo "##vso[task.setvariable variable=apiImage]$api_image"
echo "##vso[task.setvariable variable=workerImage]$worker_image"
echo "##vso[task.setvariable variable=migrationImage]$migration_image"
echo "Infrastructure is ready. Extractor Function App: $extractor_name"
