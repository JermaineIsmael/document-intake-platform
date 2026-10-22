# Feature roadmap

Proposed priorities as of 22 October 2026, derived from observed limitations.
These are recommendations, not committed dates or approved delivery estimates.

| Priority / stage | Feature | Dependencies | Definition of done |
|---|---|---|---|
| P0: close current milestone | Small-document verification | Existing deployment | Direct Search/SQL agreement and message completion captured |
| P0: stabilize operations | Recover historical dead letters | Diagnose each reason; working dependencies | Controlled replay succeeds and originals are reconciled |
| P1: large-document reliability | Blob claim-check messaging | Contract/versioning and identity design | Large extracted text processed without truncation, with tests and cleanup policy |
| P1: lifecycle visibility | Document status API | Persist lifecycle/error states, define ownership | Authorized status endpoint with queued/processing/completed/failed semantics |
| P1: reproducible deployment | SQL auth transition and bootstrap safeguards | Existing/new-server requirements | Repeat deploy preserves admin/auth settings and cannot silently leave sample API |
| P1 before production | Restricted network access | Architecture/cost decision | Validated private SQL or controlled egress; broad rule removed |
| P2: question answering | Complete retrieval and answer generation | Replace RAG placeholders, chat quota | Relevant vector results, grounded answer/citations, no-context/error tests |
| P2: data isolation | Document ownership / tenant boundaries | Product authorization model | Upload/status/retrieval enforce the same document access rules |
| P2: operational maturity | Alerts, retention, backup/recovery | Ownership/SLO decisions | Failure alerts exercised, cleanup and restore verified |
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
