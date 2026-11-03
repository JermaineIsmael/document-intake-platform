#!/usr/bin/env bash
# Builds the React front end (src/DocIntake.Web) for this environment and publishes it to the Static
# Web App created by infra/main.bicep. Runs after scripts/deploy-azure.sh, which passes the API FQDN
# and Static Web App name as pipeline variables.
set -euo pipefail

: "${RESOURCE_GROUP:?Set the Azure DevOps pipeline variable resourceGroup}"

if [[ -z "${WEB_APP_NAME:-}" ]]; then
  echo "WEB_APP_NAME is empty: deployWebApp is false or the infrastructure deployment did not return it; skipping the front end."
  exit 0
fi
if [[ -z "${API_FQDN:-}" ]]; then
  echo "API_FQDN was not passed from the infrastructure deployment." >&2
  exit 1
fi

PARAMETERS_FILE="infra/main.bicepparam"
WEB_DIR="src/DocIntake.Web"
API_SETTINGS="src/DocIntake.Api/appsettings.json"

resolved_parameters="$(az bicep build-params --file "$PARAMETERS_FILE" --stdout)"
parameter_value() {
  python3 -c '
import json, sys
parameters = json.loads(json.load(sys.stdin)["parametersJson"])["parameters"]
print(parameters.get(sys.argv[1], {}).get("value", ""))
' "$1" <<< "$resolved_parameters"
}

auth_authority="$(parameter_value authAuthority)"
auth_audience="$(parameter_value authAudience)"
# The SPA signs in with the API app registration, so its client ID is the API audience (a GUID for v2 tokens).
client_id="${WEB_CLIENT_ID:-$auth_audience}"
if [[ ! "$client_id" =~ ^[0-9a-fA-F-]{36}$ ]]; then
  echo "Front-end client ID '$client_id' is not an application (client) ID; set WEB_CLIENT_ID." >&2
  exit 1
fi
max_upload_mb="${WEB_MAX_UPLOAD_MB:-$(python3 -c '
import json, sys
print(json.load(open(sys.argv[1], encoding="utf-8-sig")).get("Upload", {}).get("MaxFileSizeMb", 25))
' "$API_SETTINGS")}"

# Vite embeds these at build time; none of them is a secret.
export VITE_API_BASE_URL="https://${API_FQDN}"
export VITE_AUTH_ENABLED=true
export VITE_AUTH_CLIENT_ID="$client_id"
export VITE_AUTH_AUTHORITY="$auth_authority"
export VITE_API_SCOPE="${WEB_API_SCOPE:-api://${client_id}/access_as_user}"
export VITE_MAX_UPLOAD_MB="$max_upload_mb"

echo "Building front end for API ${VITE_API_BASE_URL} (scope ${VITE_API_SCOPE})..."
(
  cd "$WEB_DIR"
  npm ci --no-audit --no-fund
  npm run build
)

web_hostname="$(az staticwebapp show --name "$WEB_APP_NAME" --resource-group "$RESOURCE_GROUP" \
  --query defaultHostname -o tsv)"
# Passed through the environment rather than the command line so it does not appear in process listings.
SWA_CLI_DEPLOYMENT_TOKEN="$(az staticwebapp secrets list --name "$WEB_APP_NAME" --resource-group "$RESOURCE_GROUP" \
  --query properties.apiKey -o tsv)"
if [[ -z "$SWA_CLI_DEPLOYMENT_TOKEN" ]]; then
  echo "Could not read the deployment token for Static Web App '$WEB_APP_NAME'." >&2
  exit 1
fi
export SWA_CLI_DEPLOYMENT_TOKEN

echo "Publishing ${WEB_DIR}/dist to Static Web App '$WEB_APP_NAME'..."
npx --yes @azure/static-web-apps-cli@2 deploy "${WEB_DIR}/dist" --env production
unset SWA_CLI_DEPLOYMENT_TOKEN

# Smoke checks: the app shell, a client-side route (navigation fallback), and the API CORS preflight.
site="https://${web_hostname}"
for path in / /documents/00000000-0000-0000-0000-000000000000; do
  for attempt in $(seq 1 12); do
    status="$(curl -s -o /dev/null -w '%{http_code}' "${site}${path}" || echo 000)"
    [[ "$status" == "200" ]] && break
    echo "Waiting for ${site}${path} (HTTP $status; attempt $attempt/12)..."
    sleep 10
  done
  if [[ "$status" != "200" ]]; then
    echo "Front end check failed: ${site}${path} returned HTTP $status." >&2
    exit 1
  fi
done

allowed_origin="$(curl -s -o /dev/null -D - -X OPTIONS "${VITE_API_BASE_URL}/documents" \
  -H "Origin: ${site}" -H 'Access-Control-Request-Method: POST' -H 'Access-Control-Request-Headers: authorization' \
  | tr -d '\r' | awk 'tolower($1) == "access-control-allow-origin:" { print $2 }')"
if [[ "$allowed_origin" != "$site" ]]; then
  echo "The API at ${VITE_API_BASE_URL} does not allow CORS requests from ${site} (got '${allowed_origin:-none}')." >&2
  echo "Check the API's Cors__AllowedOrigins__* settings from infra/main.bicep." >&2
  exit 1
fi

echo "Front end published: ${site}/"
