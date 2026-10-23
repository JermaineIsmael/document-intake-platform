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

## B. Verify large-document processing

Claim-check implementation is in the repository; live Azure verification and
compatibility rollout remain.

- Drain or disposition old inline messages before deploying the new contract.
- Ingest a large non-sensitive PDF/DOCX and verify extracted-text Blob access.
- Validate retries, Unicode content, Worker indexing, and queue settlement.

Exit: representative large extracted content reaches Search/SQL without data
truncation or Service Bus message-size failure.

## C. Verify lifecycle and deployment safety

Owner-scoped queued/indexed/deleted status, delete/restore, and retention are
implemented in code; detail states and live evidence remain.

- Add processing/failed lifecycle states and user-visible extraction failures.
- Live-test cross-owner denial, 30-day restore boundaries, and 90-day cleanup.
- Define no-text behavior.
- Support new/existing SQL authentication paths without admin lockout.
- Protect populated environments from accidental sample-image bootstrap.
- Deploy private SQL networking after CIDR review and any required Container
  Apps environment migration; run SQL grants over private connectivity.

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

- Verify deployed private SQL access and revoke the legacy broad rule.
- Add alerts and backup/recovery exercises; retention/delete automation exists
  in code but has no live evidence.
- Verify ownership boundaries for document data and implement owner-filtered
  retrieval before enabling RAG.
- Measure load, quota behavior, cost, and service recovery; define SLAs/SLOs.

Exit: explicit readiness approval backed by evidence, not a green deployment
or one successful ingestion.

See [historical sprints](../development/sprint-history.md),
[prioritized features](feature-roadmap.md), and
[test strategy](../development/testing-strategy.md).
