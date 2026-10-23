# Feature roadmap

Proposed priorities as of 22 October 2026, derived from observed limitations.
These are recommendations, not committed dates or approved delivery estimates.

| Priority / stage | Feature | Dependencies | Definition of done |
|---|---|---|---|
| P0: close current milestone | Small-document verification | Existing deployment | Direct Search/SQL agreement and message completion captured |
| P0: stabilize operations | Recover historical dead letters | Diagnose each reason; working dependencies | Controlled replay succeeds and originals are reconciled |
| P1: large-document reliability | Claim-check implemented in code | Deploy compatible Extractor/Worker and drain old messages | Live large extracted text processed without truncation; retries, cleanup, and owner metadata verified |
| P1: lifecycle visibility | Owner-scoped status (queued/extracting/processing/indexed/noText/failed/deleted), delete, and restore implemented in code | Live-test transitions, failures, timeouts, and ownership | Authorized status endpoint with queued/processing/completed/failed semantics |
| P1: reproducible deployment | Automated migrations and SQL auth convergence implemented; bootstrap safeguards remain | Live-run migration job and auth convergence; add bootstrap safeguards | Repeat deploy preserves admin/auth settings and cannot silently leave sample API |
| P1 before production | Restricted network access | Bicep target implemented; CIDR review and environment migration | Deploy private SQL endpoint/DNS, validate application connectivity, and confirm public access is denied |
| P2: question answering | Complete retrieval and answer generation | Replace RAG placeholders, chat quota | Relevant vector results, grounded answer/citations, no-context/error tests |
| P2: data isolation | Owner-scoped lifecycle access implemented in code | Live authorization checks; owner-filtered RAG is still required | Upload/status/delete/restore and any retrieval enforce the same owner boundary |
| P2: operational maturity | Retention/delete/restore implemented in code | Deploy and live-test; define backup/recovery and alerts | Cleanup, 30-day restore, 90-day expiry, and a separate recovery strategy verified |
| P2: performance | Load and quota validation | Stable functional path | Measured throughput/latency/cost and tested throttling behavior |

## Scope constraints

Large PDFs are not the only large-message case: compressed or multilingual
content may expand during extraction/JSON serialization. Size policy must
operate on actual payloads and must not silently discard text.

Model deployment alone does not implement RAG. Retrieval and embedding result
handling are placeholders in the current service. Chat is off in development.

## Prioritization principles

Close evidence gaps before claiming completion. Prefer diagnosed fixes over
repeated uploads/redeployments. Add recoverability and visibility before
raising scale. Production data isolation/networking are release prerequisites,
not optional polish.

See [current status](../development/implementation-status.md),
[MVP definition](mvp-definition.md), and [proposed sprint plan](sprint-roadmap.md).
