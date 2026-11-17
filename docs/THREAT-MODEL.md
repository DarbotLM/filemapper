# Initial trust boundary review

## Assets and boundaries

Filesystem and repository content are untrusted data. Collectors observe under the current user's privileges. SQLite contains potentially sensitive paths, owners, code metadata, and findings. The UI calls a loopback service using a bearer token; no token is embedded in served assets. Semantic extractors must not execute repository builds, generators, scripts, hooks, or modules. No destructive action endpoint exists in this development increment.

| Threat | Current control | Remaining verification |
|---|---|---|
| Spoofed local service requests / DNS rebinding | Loopback listener, exact Host allowlist, role bearer tokens, same-origin checks | Socket transport and broader adversarial audit |
| Repository code execution | Compiler/parser-only analysis; Git read operations | Adversarial repository and parser resource isolation |
| Path or HTML injection | UI text nodes, parameterized SQL, CSV formula neutralization | Render malicious filename corpus |
| Reparse traversal / cycles | Four follow policies, stable identity cycle detection, ancestor identity/policy checks on resumed work | Check/open race hardening and sustained mutation stress |
| Hardlink double counting | Physical identity separated from paths; native hardlink/ADS fixtures and scoped aggregations validated | Full golden volume corpus |
| Partial scans imply deletion | Explicit completeness; diff suppresses deletion for incomplete after-snapshot | Crash/cancellation/denial validation |
| Information leakage | Private data directory before token creation, existing token ACL checks, role authentication, no telemetry export | Parent-path replacement hardening, encryption at rest and secret redaction |
| Denial of service | Cross-process OS writer lease, durable jobs, page/request bounds, bounded analysis | Parser process limits, SQL cancellation and large graph benchmark |
| Upgrade loses newly committed data | Maintenance admission held across backup/activation/rollback; narrowly authenticated installer restore; health checks identify process | All writers must participate; legacy binaries/direct DB edits remain outside this protocol |
| Action abuse | No executor implemented or advertised as available | Policy, preview, approval, TOCTOU, journal, verification, rollback |

## Release blockers

This review is a design record, not a passed security assessment. Artifact signing, policy/rule signing, parser isolation, complete privilege policy and full action hardening remain required before production release. Role tokens do not isolate programs running as the same operating-system user, which can read the user's credential files and database. No defense against a malicious local administrator is claimed.
