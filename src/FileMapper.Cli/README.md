# FileMapper CLI: Robocopy actions

`filemapper robocopy --help` lists the local action interface. It never accepts a claimed role or caller identity from JSON. Authority comes from the current Windows account, and execution is disabled unless that invocation includes `--enable-execution`. The HTTP service remains planning-only.

Start with a typed JSON draft, for example:

```json
{"source":"C:\\ExampleSource","destination":"C:\\ExampleDestination"}
```

Both endpoints must already exist. Omitting options selects bounded defaults; include a typed `options` array to choose explicit catalog switches. `catalog` describes all 97 recorded installed-help options. Validation, preview support, and verified execution support are different: unsupported execution options remain blocked even when listed in the catalog.

```powershell
filemapper robocopy validate --draft draft.json
filemapper robocopy plan --draft draft.json
filemapper robocopy get-plan --plan PLAN_ID
filemapper robocopy preview --plan PLAN_ID
filemapper robocopy job --job PREVIEW_JOB_ID
filemapper robocopy output --job PREVIEW_JOB_ID
filemapper robocopy events --job PREVIEW_JOB_ID
```

The preview forces native `/L`, waits for a terminal journal, and records tree fingerprints, command arguments, binary identity, native exit bits, and bounded output. The default `native` output channel is the managed Unicode log; `stdout` and `stderr` retain raw console evidence with their recorded encoding limitations. Ctrl+C requests cancellation and waits for containment cleanup. A second CLI can request durable cancellation with `cancel --job ID`.

Review the exact plan, successful preview, mutation scopes and rollback statement before explicitly approving:

```powershell
filemapper robocopy approve --enable-execution --plan PLAN_ID --preview PREVIEW_JOB_ID `
  --plan-hash REVIEWED_PLAN_SHA256 --preview-hash REVIEWED_PREVIEW_SHA256 `
  --rollback-statement 'EXACT REVIEWED STATEMENT' `
  --accept-overwrites --accept-irreversible-changes --confirm-trees-quiescent
filemapper robocopy execute --enable-execution --approval APPROVAL_ID
```

There is no automatic approval or backup. Approval expires and is consumed once; changed trees or policy fail closed. The quiescence assertion is the operator's reviewed statement, not an atomic filesystem guarantee. Native exit success alone is not content verification. `--allow-destructive` is an additional explicit capability and does not bypass unsupported-policy blocks. This development implementation does not claim all 97 options can execute safely.

`--database PATH` chooses the maintenance admission barrier and defaults the action state to `actions/robocopy` beside the database. The same path as the service coordinates installation and action jobs. A new database data directory is provisioned with a protected user-only ACL; an existing directory granting other identities access is rejected without rewriting its ACL. `--state-directory PATH` selects a different journal, but does not bypass the database maintenance barrier. Keep managed state outside both endpoints.

Published packages include a self-contained sibling `worker` directory. Source development must explicitly supply `--dotnet-host` and, when necessary, `--runtime-config`; no command searches an untrusted repository for a host executable. `import-job --path file.rcj` parses bounded RCJ data; `export-job --draft draft.json` writes RCJ text to stdout. Neither invokes native `/JOB` or `/SAVE`.
