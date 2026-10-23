# Security architecture

Baseline: 22 October 2026. This documents controls and known boundaries; it is
not a new vulnerability assessment or an assurance that production is secure.

## Document ownership

Document lifecycle APIs use owner-level access checks. This is the current
security design and control-boundary reference, not live production assurance.
The [historical secrets review](../development/security-audit.md) records the
earlier review's scope, reported results, and evidence limitations; it does not
duplicate this design or certify its implementation.

## Authentication and authorization

- API JWT bearer validation uses configured Entra authority and audience.
  Upload ownership is derived from the token's Entra `oid` claim, never from a
  caller-supplied owner field. Status, delete, and restore operations query
  metadata by both document ID and owner ID; non-owners receive no document
  existence disclosure. Missing-owner legacy rows remain inaccessible.
- Development auth bypass is restricted to Development and uses a fixed
  development-only owner GUID. It is not a production identity.
- Owner ID is carried as Blob metadata, the Service Bus contract, SQL metadata,
  and a filterable Search field. Extracted-text reads verify both document and
  owner metadata against the queue message.
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

The Bicep target configuration creates a VNet-integrated Container Apps
environment, SQL private endpoint/private DNS, and disables SQL public network
access. The address ranges are parameters and must be checked for overlap.
Existing Container Apps environments may require migration/recreation; do not
assume incremental deployment can move an existing environment into the VNet.
The intended private-network configuration has not been deployed or verified.
SQL database users and grants still require administrator execution of the
schema script from a network path that can resolve and reach the private
endpoint.

## Untrusted input and data exposure

- Uploads are validated for allowed PDF/DOCX types and configured size limits.
- Extractor validates source URLs/container paths before reading blobs.
- Document text is untrusted and can include confidential information.
  Queue bodies, dead letters, Search content, source/extracted blobs, and logs
  require controlled operator access. The queue now contains a Blob reference,
  not extracted text.
- The RAG prompt includes untrusted-document guidance, but RAG is incomplete.
  Prompt text alone is not an isolation or authorization mechanism.
- Search chunks carry owner IDs and deletion markers. No user-facing retrieval
  path is implemented yet; any future RAG query must filter by the authenticated
  owner before it is enabled.
- User deletion is recoverable through the API for 30 days. At 90-day retention
  expiry, the Worker soft-deletes blobs and purges Search/SQL metadata; native
  Blob soft-delete remains for up to 30 days, but the application restore
  window has expired. These controls are implemented in code/configuration but
  not live-tested.
- Blob soft delete is not a full backup strategy. Search backup, cross-region
  recovery, malware scanning, and sensitivity policy remain unimplemented.

## Operational rules

Never commit or share bearer tokens, keys, credentials, or sensitive document
bodies. Use non-sensitive test files. Review role assignments and guest-group
membership before replacing SQL administrators. Budget alerts do not stop spend.
Inspect dead-letter metadata before replay, preserving evidence until recovery
is verified. Do not treat the code/configuration changes as deployed controls
until private network access, ownership checks, claim-check reads, and deletion
recovery have been exercised in Azure.

See [system context](system-context.md),
[remaining work](../product/feature-roadmap.md), and
[deployment boundaries](../development/deployment-verification.md).
