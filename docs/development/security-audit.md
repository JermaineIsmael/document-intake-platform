# Historical secrets and configuration review

## Purpose and status

This is the retained summary of the repository's Sprint 5 review, originally
dated **14 October 2026**. It records what the earlier report claimed; it is
not a new scan, current security assessment, or approval for production.

Documentation consolidation date: **22 October 2026**.

The [security architecture](../architecture/security.md) owns the current
control design, identity boundaries, development exceptions, and known gaps.
This document owns only the historical review record and follow-up evidence.

## Original scope and method

The earlier report described source, Bicep, configuration, Dockerfiles, and
repository exclusions as its scope. It reported manual inspection and this
keyword search:

```bash
git grep -iE "(api-?key|password|connectionstring|accountkey|secret|token|credential|apikey)"
```

Keyword matches were reportedly reviewed to distinguish credentials from
configuration names, comments, and test placeholders.

No reviewed commit identifier, raw scan output, artifact digest, or reproducible
finding log was attached to that report. Its stated date and results are
historical assertions, not independently revalidated findings.

## Results reported at the time

| Area | Earlier reported result | Evidence limitation |
|---|---|---|
| Source/configuration search | No hardcoded secrets identified among reviewed matches | Keyword search cannot establish absence of all secrets or Git-history exposure |
| Repository exclusions | Local settings, environment files, generated outputs excluded | Ignore rules do not prove matching files were never tracked |
| Azure access design | Managed identity/RBAC and disabled local credentials reviewed | Template inspection does not establish live service state or effective permissions |
| Parameter values | Identifiers/endpoints distinguished from credential values | Current environment values and privacy requirements require separate review |
| Dockerfiles | No embedded credentials observed; non-root runtime configured | Dockerfile inspection is not a scan of built image layers or registry artifacts |

The original blanket "passed with no findings" and production-ready conclusion
are not carried forward as current assurances.

## Superseded or unsupported statements

The earlier report repeated architectural details that became stale or were
too broad to support:

- It described CI/CD as absent; an Azure DevOps pipeline now exists.
- It described separate Function host storage; the implemented configuration
  shares the document storage account for host/package responsibilities.
- It described Worker SQL authentication through an Authentication connection
  string keyword; the Worker now uses an Entra access-token callback.
- It treated all telemetry as managed-identity authenticated. Telemetry setup
  differs between API, Extractor, and Worker; connection strings and ingestion
  authentication must be evaluated per component.
- It asserted live Entra-only SQL authentication from template intent. Later
  troubleshooting observed a different administrator and authentication state.
- It inferred credential-free images from Dockerfile inspection and proposed
  rotating RBAC assignments as if they were keys. Role assignments are access
  grants, not rotating credentials.

The current architecture and the
[deployment verification record](deployment-verification.md) replace those
descriptions. Development's broad SQL Azure-services firewall rule and other
known boundaries are documented there; this historical report does not override
them.

## Follow-up review evidence needed

Before treating a future review as a release gate:

1. Record the reviewed commit, date, tool versions, scan scope, and redacted
   results, including how false positives and test/emulator values were handled.
2. Evaluate source, tracked files/history, pipeline output, published packages,
   and built images as distinct evidence sources.
3. Verify actual Azure authentication settings, effective identity permissions,
   SQL grants, firewall configuration, and configuration drift.
4. Record unresolved findings, exceptions, remediation owners, and retest results.
5. Obtain an explicit release decision rather than inferring security approval
   from passing functional tests or successful deployment.

No new scan or approval was performed during this documentation consolidation.
