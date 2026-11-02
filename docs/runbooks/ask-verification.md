# `/ask` live verification runbook

Use this after enabling chat to verify retrieval, owner isolation, citations and
failure behaviour against real Azure resources. Unit tests do not prove any of these.

## Prerequisites

1. The API, Worker and Extractor are deployed and at least one document is `indexed` for **user A**
   (see [deployment-verification](../development/deployment-verification.md)).
2. The Azure OpenAI chat deployment exists with quota (`deployChat = true` in
   [main.bicepparam](../../infra/main.bicepparam); `OpenAI__ChatEnabled=true` on the API).
3. The API identity can read the `document-chunks` index and call the embedding and chat deployments.
4. You can acquire Entra access tokens for the API audience for user A and, ideally, a different user B.

Startup fails fast if chat is enabled without `Search__Endpoint`, `OpenAI__Endpoint`,
`OpenAI__EmbeddingDeployment` or `OpenAI__ChatDeployment`; check the API revision logs first if it does not start.

## Run the automated checks

```powershell
$env:ASK_IT_BASE_URL          = "https://$apiFqdn"
$env:ASK_IT_TOKEN_A           = az account get-access-token --scope "$authAudience/.default" --query accessToken -o tsv
$env:ASK_IT_QUESTION          = "<a question answered by user A's document>"
$env:ASK_IT_EXPECTED_SOURCE   = "<fragment of that document's blob URL / name>"
$env:ASK_IT_UNRELATED_QUESTION = "What is the capital of Australia?"
# Optional: sign in as a different user and set a token to verify cross-owner isolation.
# $env:ASK_IT_TOKEN_B = "<token for user B>"

dotnet test tests\DocIntake.Tests\DocIntake.Tests.csproj --filter "FullyQualifiedName~AskAzureIntegrationTests"
```

Checks whose settings are missing are reported as skipped, not passed. A run with skips is not full verification.

| Check | Pass criteria |
|---|---|
| Unauthenticated | 401 |
| Blank question | 400 |
| Owner question | 200, non-empty answer, `sources` contains the expected document |
| Other user | 404, or 200 whose `sources` never include user A's document |
| Unrelated question | 404, or 200 containing "I don't have enough information" |

## Manual checks

1. **Delete exclusion** — delete user A's document (`DELETE /documents/{id}`), repeat the owner question, and confirm
   the document is no longer cited. Restore it within the recovery window and confirm it is cited again.
2. **Prompt injection** — index a document built from
   [prompt-injection-notes](prompt-injection-notes.md), ask about it, and confirm the answer does not follow the
   embedded instructions or reveal the system prompt.
3. **Failure behaviour** — temporarily remove the API identity's Search read role or point at a missing chat
   deployment: `/ask` must return 500 (not 404) and the response body must not contain exception details.
4. **Telemetry and cost** — in Application Insights confirm `Retrieved N chunks`, `Chat completion returned ... characters`
   and `Generated answer from N sources` traces appear, no document text or full question is logged, and
   Azure OpenAI token spend matches the [cost guardrails](cost-guardrails.md).

## Record the outcome

Record the date, environment, model deployments, which checks passed or were skipped, and any follow-ups in
[deployment-verification](../development/deployment-verification.md). Only then change the README status from
"implemented (unverified live)" to verified.
