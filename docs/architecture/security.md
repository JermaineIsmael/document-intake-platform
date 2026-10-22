# Security architecture

Baseline: 22 October 2026. This documents controls and known boundaries; it is
not a new vulnerability assessment or an assurance that production is secure.

## Document ownership

This document is the current security design and control-boundary reference.
The [historical secrets review](../development/security-audit.md) records the
earlier review's scope, reported results, and evidence limitations; it does not
duplicate this design or certify its implementation.

## Authentication and authorization

- API JWT bearer validation uses configured Entra authority and audience.
  Protected routes require an authenticated identity; no application-specific
  document ownership model is implemented.
- A local authentication bypass is allowed only in Development configuration;
  deployed production API configuration disables it.
- Azure SDK clients use DefaultAzureCredential. Deployed components use managed
  identities rather than source-controlled service passwords.
- Resource-management rights, service data-plane RBAC, and SQL database grants
  are separate. Subscription Contributor is not SQL login or message-read access.
- SQL's intended template state is Entra-only authentication and a group admin.
  Existing-server drift/transitions must be checked explicitly.

## Identities and scope

| Identity | Intended responsibility |
|---|---|
| API system identity | Original blob uploads and configured API service access |
| Extractor system identity | Host/package storage, source reads, extraction, queue sends |
| Worker system identity | Queue receive/scaling, embeddings, Search administration/data, SQL token |
| Worker user-assigned pull identity | ACR image pull, granted before Worker provisioning |
| Event Grid system-topic identity | Dead-letter writes; Blob Data Contributor at account scope |
| Deployment service connection | Resource deployment, role assignments, identity assignment, image push |
| Operator user/group | Entra-authorized SQL administration and diagnostic data access |

SQL requires a contained Worker user and reader/writer role membership in
addition to successful Entra authentication and network access.

## Network and storage controls

HTTPS/TLS is configured for ingress and service clients. Worker has no public
ingress. Blob containers are private; shared-key storage access and local
authentication on Service Bus, Search, Document Intelligence, and OpenAI are
disabled by the template. ACR admin credentials are disabled.

Development opts into SQL `AllowAzureServices` using a `0.0.0.0` firewall rule.
This permits attempts from Azure resources outside the subscription; it does
not grant database credentials. Turning the template parameter off does not
delete the existing rule under incremental deployment: removal is explicit.
Production needs a separately reviewed private-connectivity or egress design.

## Untrusted input and data exposure

- Uploads are validated for allowed PDF/DOCX types and configured size limits.
- Extractor validates source URLs/container paths before reading blobs.
- Document text is untrusted and can include confidential information.
  Queue bodies, dead letters, Search content, blobs, and logs require controlled
  operator access.
- The RAG prompt includes untrusted-document guidance, but RAG is incomplete.
  Prompt text alone is not an isolation or authorization mechanism.
- There is no implemented per-document owner/tenant search filtering; do not
  use the system as a multi-tenant confidential-document product.
- Malware scanning, document retention, deletion, and sensitivity policies need
  explicit design; upload type checking is not a malware guarantee.

## Operational rules

Never commit or share bearer tokens, keys, credentials, or sensitive document
bodies. Use non-sensitive test files. Review role assignments and guest-group
membership before replacing SQL administrators. Budget alerts do not stop spend.
Inspect dead-letter metadata before replay, preserving evidence until recovery
is verified.

See [system context](system-context.md),
[remaining work](../product/feature-roadmap.md), and
[deployment boundaries](../development/deployment-verification.md).
