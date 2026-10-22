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
- Large extracted text beyond the inline message constraint.
- Dedicated user web UI, per-document status/read endpoint, multi-tenant isolation.
- Production SLA, private networking, load guarantees, compliance certification.

## Acceptance criteria

| ID | Required outcome | Current evidence |
|---|---|---|
| MVP-01 | Authenticated valid upload gives 202 and stored original | Confirmed uploads |
| MVP-02 | Unauthenticated protected request gives 401 | Confirmed invalid-token case |
| MVP-03 | Extractor processes event and publishes small content | Confirmed by downstream processing |
| MVP-04 | Worker creates matching Search chunks and SQL metadata | SQL one-chunk result observed; direct Search check pending |
| MVP-05 | Successful message is completed without new dead letter | Final queue settlement evidence pending |
| MVP-06 | Failed work remains diagnosable and replayable | Dead-letter inspection demonstrated; successful recovery pending |
| MVP-07 | Pipeline deploys real app without manual secret injection | Successful run observed; manual identity/SQL steps documented |
| MVP-08 | Limits and development access trade-offs disclosed | Documented; not resolved production constraints |

## Exit decision

The milestone is **partially verified**, not fully signed off. Close Search and
settlement checks and demonstrate one recovery before declaring the ingestion
MVP accepted. Automated tests alone cannot establish Azure data-plane access.

Production or RAG acceptance needs additional explicit requirements and evidence.
See [requirements](product-requirement.md),
[testing strategy](../development/testing-strategy.md), and
[roadmap](feature-roadmap.md).
