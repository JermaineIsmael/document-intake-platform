# Product requirements

Working requirements baseline reconstructed from repository behavior and testing
through 22 October 2026. Proposed requirements are not represented as already
approved or delivered. Business owners, SLAs, and commercial targets have not
been specified. The current implementation uses a 90-day retention period and
a 30-day owner-delete recovery window; business and legal approval of those
defaults remains pending.

## Problem and users

DocIntake reduces manual handling of uploaded documents by extracting text and
preparing it for searchable retrieval. Submitters use an authenticated API;
operators monitor progress, diagnose failures, and recover messages. Database
administrators and developers support configuration and delivery.

## Functional requirements

| ID | Requirement | Current state |
|---|---|---|
| FR-01 | Require a valid Entra token for protected API operations | Implemented |
| FR-02 | Validate one multipart PDF/DOCX file and configurable upload limit | Implemented; default 25 MiB |
| FR-03 | Persist original with unique document ID and acknowledge asynchronously | Implemented, verified 202 |
| FR-04 | Extract text after blob creation without client polling the upload request | Implemented |
| FR-05 | Deliver extracted text for chunking and embedding | Claim-check implemented in code; live large-document verification pending |
| FR-06 | Index text/vectors and upsert metadata keyed by document ID | Implemented; small-document SQL evidence |
| FR-07 | Retry/reconcile failures without silently dropping work | Retry/dead-letter mechanisms implemented; recovery verification pending |
| FR-08 | Expose document lifecycle and actionable failure status to authorized callers | Owner-scoped queued/indexed/deleted API implemented; processing/failed states remain |
| FR-09 | Answer questions using authorized document context with sources | Planned/partial route; RAG placeholders, chat disabled |
| FR-10 | Support larger extracted text without truncation | Private Blob claim-check implemented; live service-limit verification pending |

## Quality and operational requirements

- Preserve explicit failures and identify operations with document/message IDs.
- Treat external events, file contents, and retrieved text as untrusted.
- Keep secrets out of repository/logs; use managed identities where implemented.
- Distinguish application/data permissions from Azure management permissions.
- Support same-document replay without duplicate Search keys or SQL rows.
- Capture deployment/version evidence and repeatable verification procedures.
- Deploy and verify private SQL networking and owner authorization; define
  performance, backup/recovery objectives, and alert ownership before production use.

There is no measured throughput/latency SLA or approved recovery objective.
Do not invent target numbers from service limits or replica settings.

## Acceptance and exclusions

The [MVP definition](mvp-definition.md) establishes the prior small-document
acceptance gate. Full RAG remains excluded; claim-check, private networking,
ownership, lifecycle, and retention changes are implemented in code but require
deployment and live verification before they count as accepted capabilities.

The currently deployed development resources still reflect the earlier broad
Azure-origin SQL access setting. Repository Bicep now targets SQL private
networking, but it has not been deployed or approved as a production network
design.

## Decisions still needed

1. Which document sizes, formats, languages, and sensitivity levels are supported?
2. Who can access each document and its derived chunks?
3. What lifecycle/status and empty-text behavior should users see?
4. Are the implemented 90-day retention and 30-day restore defaults acceptable?
5. What latency, volume, budget, and production network constraints apply?
6. Is complete RAG required for the next release, or a separate milestone?

See [architecture context](../architecture/system-context.md) and
[feature roadmap](feature-roadmap.md).
