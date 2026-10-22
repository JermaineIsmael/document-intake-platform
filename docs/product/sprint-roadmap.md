# Sprint roadmap

Proposed work packages following the current ingestion milestone, dated
22 October 2026. Labels A-E are sequencing aids, not historical Sprint 6-10
commitments. Dates, ownership, capacity, and estimates require agreement.

## A. Close ingestion verification

Dependencies: working development deployment and data-plane operator access.

- Confirm direct Search content/count against the successful SQL document.
- Confirm message settlement and classify all historical dead letters.
- Demonstrate one controlled replay after fixing its dependency.
- Record build/tag/document/queue evidence.

Exit: [MVP](mvp-definition.md) verification gaps closed or explicitly accepted.

## B. Make large-document processing reliable

Depends on a contract/storage/security decision; functional evidence from A
provides the baseline.

- Implement private extracted-text claim-check storage and reference validation.
- Coordinate Extractor/Worker contracts and existing-message compatibility.
- Define text/package retention, cleanup, oversize behavior, and replay.
- Add large payload, Unicode, failure, and reference-access tests.

Exit: representative large extracted content reaches Search/SQL without data
truncation or Service Bus message-size failure.

## C. Improve lifecycle and deployment safety

Status design depends on product decisions; deployment safeguards can proceed
independently of B.

- Persist lifecycle/failure states and implement authorized status retrieval.
- Define no-text behavior.
- Support new/existing SQL authentication paths without admin lockout.
- Protect populated environments from accidental sample-image bootstrap.
- Remove obsolete test firewall rules after confirming stable access.

Exit: users/operators can distinguish queued, failed, skipped, and completed
work; repeated deployment does not undermine the running service.

## D. Complete RAG as a separate capability

Depends on retrieval implementation, chat quota, data authorization, and a
validated indexed corpus; independent from merely enabling the model.

- Replace null embedding/retrieval placeholders.
- Implement retrieval, grounded generation, source output, and explicit errors.
- Test no-context, malicious-document, access-control, throttling, and citation cases.
- Enable chat only after quality/cost acceptance.

Exit: real deployed retrieval and answer generation pass agreed product tests.

## E. Production readiness

Production planning can run in parallel, but release depends on resolved
ownership, network, security, and reliability requirements.

- Implement chosen private SQL/controlled-egress design and revoke broad access.
- Add alerts, retention/deletion, recovery exercises, and operator runbooks.
- Verify ownership/tenant boundaries for document data and retrieval.
- Measure load, quota behavior, cost, and service recovery; define SLAs/SLOs.

Exit: explicit readiness approval backed by evidence, not a green deployment
or one successful ingestion.

See [historical sprints](../development/sprint-history.md),
[prioritized features](feature-roadmap.md), and
[test strategy](../development/testing-strategy.md).
