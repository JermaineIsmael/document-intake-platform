# Sprint history

This is a reconstruction from the repository's Sprint 1-5 labels and deployment
results shared through 22 October 2026. It is not an audited sprint ledger:
original dates, durations, owners, estimates, and completion approvals are not
available. Code labeled for a sprint is not automatically completed scope.

| Historical label | Scope represented in repository | Current outcome |
|---|---|---|
| Sprint 1 | API foundation, upload validation/authentication, original blobs, health/telemetry | Implemented; deployed upload verified |
| Sprint 2 | Event Grid-triggered Extractor, Document Intelligence, Service Bus contract | Implemented; small-document path verified through downstream SQL; large-text limitation remains |
| Sprint 3 | Chunking, embeddings, Search index, SQL metadata, local Worker roles | Implemented; SQL row observed; some direct verification outstanding |
| Sprint 4 | Containerized deployed Worker, API RAG route and optional chat provisioning | Hosting works; full RAG is still placeholder/incomplete |
| Sprint 5 | Telemetry, retry/idempotency tests, budget/cost guardrails | Controls/tests present; alert delivery, recovery, load, and production readiness not fully verified |

## Deployment stabilization sequence

The troubleshooting sequence exposed service permissions, provider registration,
quota restrictions, private-image authorization ordering, Functions hosting
configuration, telemetry package compatibility, and application configuration.

Significant decisions:

1. Deploy Extractor on Flex Consumption because Linux Consumption quota was
   unavailable; use real Flex runtime/package-storage configuration.
2. Make chat optional and disable it under zero quota; keep embeddings active.
3. Build images with Docker on the hosted pipeline agent instead of ACR Tasks.
4. Correct Service Bus naming and use separate SQL region/name overrides.
5. Separate Worker pull identity from runtime identity to order AcrPull first.
6. Fix Functions telemetry dependency mismatch and wait for indexing before
   creating the Event Grid subscription.
7. Correct Worker Service Bus/SQL settings and inspect dead-letter errors.
8. Opt into the development SQL Azure-services rule after changing egress IPs
   repeatedly caused firewall rejection.

## Latest observed milestone

On 22 October 2026, supplied SQL evidence showed a document persisted with one
chunk. This followed earlier message-size and SQL firewall failures.
It does not retroactively mark all historical dead letters as recovered.

Future work is intentionally described as proposed work in the
[sprint roadmap](../product/sprint-roadmap.md), not assigned fabricated dates.
The [deployment report](deployment-verification.md) retains detailed evidence.
