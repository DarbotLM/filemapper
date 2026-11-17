# Bounded GitHub blob materialization

Materialization is opt-in. `policy.materializeBlobs: true` without `boundedMaterialization` preserves the existing filtered Git fetch. That compatibility mode does not enforce a transfer or scratch-byte cap and may acquire much more than the retained-byte limit. For the separately bounded mode, supply for example:

```json
{
  "materializeBlobs": true,
  "maximumBlobBytes": 8388608,
  "maximumCapturedBytes": 4294967296,
  "fetchTimeoutSeconds": 1800,
  "boundedMaterialization": {
    "mode": "github-response-body",
    "maximumResponseBodyBytes": 1073741824
  }
}
```

The bounded mode enumerates the pinned local tree and reads missing blobs individually from GitHub's Git blob endpoint. It does not invoke `git fetch` or enable lazy fetch. Objects already local are read through Git. `maximumCapturedBytes` limits unique retained bytes; `maximumResponseBodyBytes` independently limits response body bytes read by the application across the attempt. The latter is not a wire-byte limit: HTTP headers, TLS, protocol framing and bytes buffered below the application's reads are not counted. Failed or oversized bodies consume the body budget. No HTTP body is staged on disk; one body of at most `maximumBlobBytes` plus a probe byte is buffered, with additional managed copies. Existing tree, entries, object store and Git cache disk use are not covered by a total scratch quota.

`fetchTimeoutSeconds` is an absolute deadline for the bounded materialization phase, including time spent processing local entries, rather than a per-request timeout or whole-capture deadline. No automatic redirects or decompression are allowed. Provider size hints are not used. Every downloaded body is hashed using Git's object framing and compared to its pinned tree object ID before retention; local blobs are checked as before. If a body exactly exhausts the remaining response-body budget, EOF cannot be established within that budget, so it is unavailable. HTTP failures, rate limits, mismatches, exhausted budgets and deadlines produce unavailable entries and a partial capture; no coverage is inferred from a provider hint. Entries excluded by the retained-byte policy are marked separately. Repeated unavailable objects are not retried in the attempt. GitHub API rate limits and the absence of authentication can severely restrict coverage; this mode does not promise full acquisition.

An HTTP 401, 403 or 429 closes remote materialization for the rest of the attempt; subsequent missing objects are explicitly unavailable and are not requested. Other HTTP errors leave later objects eligible for an attempt.

Each attempt has create-new `request.json`, `materialization.json`, and `sealed.json` or `failed.json`. For sealed bounded attempts, the manifest includes a content-addressed descriptor of the retained materialization receipt; `harvest verify` verifies its bytes along with the other referenced evidence. Failed attempts preserve their receipt in the attempt directory without a sealed manifest. The receipt is not a cryptographic proof of all network traffic. Cancellation propagates after an attempt record is written. This mechanism does not guarantee a total network or disk quota, a whole-capture time limit, or complete acquisition of any repository.
