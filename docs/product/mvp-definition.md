# MVP definition

Working definition based on the implemented ingestion system, 22 October 2026.
This is an engineering baseline for agreement, not a previously approved product
contract. The ingestion MVP and a complete RAG product are distinct milestones.

## Ingestion MVP objective

An authorized user can submit a supported small document and an operator can
verify that its text was extracted, embedded, indexed, and recorded without
losing failed work.

## Included

- Authenticated API and validated PDF/DOCX upload to private storage.
- Asynchronous Event Grid extraction and Service Bus delivery.
- Worker chunking, embeddings, Search upload, and SQL metadata.
- Explicit errors, retries, dead-letter evidence, and operator diagnostics.
- Reproducible build/deploy pipeline with a Function readiness gate.
- Documented manual Entra/SQL prerequisites and development network trade-offs.

## Excluded from this milestone

- Complete natural-language question answering or chat availability.
- Dedicated user web UI and detailed processing/failure statuses.
- Production SLA, load guarantees, compliance certification, backup, and
  disaster recovery.

Since the prior small-document deployment, owner-scoped lifecycle APIs,
claim-check messaging, SQL private-networking infrastructure, and retention
automation have been added in code. They are not evidence for this original
deployed MVP and still require deployment/live verification; claim-check removes
the old inline Service Bus payload limit from the new implementation.

## Acceptance criteria

| ID | Required outcome | Current evidence |
|---|---|---|
| MVP-01 | Authenticated valid upload gives 202 and stored original | Confirmed uploads |
| MVP-02 | Unauthenticated protected request gives 401 | Confirmed invalid-token case |
| MVP-03 | Extractor processes event and publishes small content | Confirmed by downstream processing |
| MVP-04 | Worker creates matching Search chunks and SQL metadata | Seven accepted SQL rows each match one Search chunk in supplied evidence |
| MVP-05 | Successful message is completed without new dead letter | Latest supplied queue and dead-letter counts are zero; not an audit of every settlement |
| MVP-06 | Failed work remains diagnosable and replayable | Dead-letter inspection performed; six historical items have matching evidence and two were intentionally deleted/excluded |
| MVP-07 | Pipeline deploys real app without manual secret injection | Successful run observed; manual identity/SQL steps documented |
| MVP-08 | Limits and development access trade-offs disclosed | Documented; not resolved production constraints |

## Exit decision

The small-document end-to-end verification is complete for the seven accepted
samples; historical dead-letter follow-up is closed by disposition. This does
not constitute a complete failure-recovery exercise or production sign-off.
Automated tests alone cannot establish Azure data-plane access.

Production or RAG acceptance needs additional explicit requirements and evidence.
See [requirements](product-requirement.md),
[testing strategy](../development/testing-strategy.md), and
[roadmap](feature-roadmap.md).
