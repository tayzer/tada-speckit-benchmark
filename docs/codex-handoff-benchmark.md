# Codex brownfield handoff benchmark

The suite uses the existing configured Windows worker. Each fixture revision needs both recorded owner approvals before a live batch can start.

Compare TADA, GitHub Spec Kit, OpenSpec, and plain Codex on one warehouse change followed by a production support ticket. Measure whether an autonomous agent can ship both changes and preserve earlier behaviour across a clean session handoff. The first pilot provides a separate reference point for coding time and token use. Use the same Windows worker, .NET SDK, HTTP test scaffold, and sealed reporting.

## Starting application

Start from a new pinned inventory commit that has both fixes from the pilot. Repeat cancellation releases stock once, and resizing an unfulfilled reservation updates the reservation and available stock atomically. Prove the combined baseline with the original regression suites. Provide a small seeded SQLite database in the pre-change schema containing open and cancelled reservations. New and existing databases must both work. Keep the original HTTP routes, status codes, and response fields covered by an unchanged client regression suite.

Each ticket contains a short business request, the affected HTTP contract, reproducible customer examples, and operational constraints. Include a sanitized warehouse log showing a retried confirmation in the first ticket and a support report showing a back-order after split shipment in the second. Make every judged rule visible in the ticket or prior contract. Use different inputs and request sequences in hidden checks.

## Ticket 1 — confirm a split shipment

Warehouse staff need to confirm shipments in increments. The confirmation sender retries when it loses a response.

- Add `POST /reservations/{id}/fulfill` using the existing quantity shape and a required `Idempotency-Key`. On success return 200 with the reservation ID, total quantity, and cumulative fulfilled quantity. Add `fulfilledQuantity` to the existing reservation read response. Retain existing fields and types.
- A confirmation consumes reserved units and leaves available stock unchanged. The requested increment must be positive and fit within the remaining quantity. Invalid input returns 400, an unknown reservation returns 404, and over-fulfillment returns 409 with state unchanged.
- Scope keys to a reservation. A successful key replay with the same increment returns its stored 200 response without a second fulfillment, including after a restart or cancellation. Reusing a successful key with a different increment returns 409. A fresh key on a cancelled reservation returns 404. Rejected confirmations do not create a successful receipt.
- Cancellation releases only the unfulfilled units, including zero for a fully fulfilled reservation. Repeat cancellations retain the existing 204 response without another release. A partially or fully fulfilled reservation cannot be resized in this release and returns 409 on a valid resize request. Unfulfilled reservations retain the existing resize rules.
- Upgrade existing databases in place. Old rows start with zero fulfilled units. Preserve reservation identities, stock, and cancellation state.

## Ticket 2 — revise an order after dispatch

Support staff need to remove back-ordered units or add replacements after the first shipment has gone out.

- Allow the existing `PATCH /reservations/{id}` to set a new positive total on an active reservation, including one with fulfillment history. The new total may equal the fulfilled quantity. A smaller total or an increase beyond available stock returns 409.
- Adjust available stock by the change in the total quantity. Keep fulfilled quantity and confirmation receipts intact. Rejected resizes leave reservation fields, stock, and receipts unchanged. Preserve the existing response shapes, 400 validation, and 404 behaviour.
- Use a database produced by the first release, including a partial shipment and a successful confirmation receipt. Reopen it with the second release and verify the full life cycle after a restart.

The shared worked example starts with 10 available units. Reserve 6, leaving 4 available. Confirm 2 and replay the request, leaving 4 available and 2 fulfilled. Ticket 1 rejects a resize to 4 with state unchanged. Ticket 2 accepts that resize and leaves 6 available. Resizing to 1 or 11 returns 409 and leaves the prior state intact. Cancelling twice ends with 8 available.

## Handoff and workflow evidence

Each campaign starts in a fresh workspace and a fresh Codex session. Disclose Ticket 1 only. Seal and independently evaluate the submission. Promote the sealed tree to Ticket 2 when it passes the common Ticket 1 evaluator and shared integrity checks. Preserve a blocked Ticket 2 slot when its predecessor fails. The promotion step runs the same public HTTP seed sequence against each arm’s first release to produce its persisted Ticket 2 data, regardless of the schema chosen by the agent.

Start Ticket 2 in a fresh Codex session with the promoted code, tests, Git history, and workflow artifacts. Deliver the same follow-up packet to each arm. Keep earlier chat transcripts, other workspaces, private fixture source, and scorer output outside the worker’s read boundary. Provide the stage-one promotion outcome as the only communication from the evaluator.

| Workflow | Native path | Evidence to retain |
|---|---|---|
| TADA | Use the reviewed executable expectations for each ticket, carry forward prior obligations, and run and verify both checkpoints. | Exact approval and baseline lineage, accepted obligations, run IDs, advisory L1 verdicts, and any agent-authored handoff. |
| GitHub Spec Kit | Use specify, plan, tasks, and implement for each ticket. Preserve the first ticket’s artifacts for the follow-up. | Requirements, design decisions, dependency-ordered tasks, and any references the follow-up makes to them. |
| OpenSpec | Propose and apply a change per ticket. At promotion, validate and archive the named first change with the pinned noninteractive CLI so the second ticket sees the updated local specs. | Both changes, archive outcome, updated canonical specs, and the follow-up delta. |
| Plain Codex | Implement and test both tickets with the shared brief and ordinary repository tools. The agent may leave its own project notes. | Resulting code and tests, any notes the agent chose to leave, and the follow-up transcript. |

Validate `openspec archive <change> --yes --json` before live work. Preserve the sealed first submission and produce a separate handoff snapshot. A successful archival step may change workflow artifacts only. If archival fails, preserve the pending change and record the failure. The same common promotion gate decides whether Ticket 2 starts.

Use the same signed-off examples, API rules, initial data, allowed tools, coding time limit, and engineering test scaffold for all arms. Native artifacts carry the shared semantics in each kit’s format. For TADA, the owner reviews and approves the exact Gherkin, bindings, setup, and runner policy for Ticket 1 and the Ticket 2 amendment before any live campaign. Seal the amendment separately and release it at the handoff. Carry forward immutable approval history. A change to those bytes requires a new review.

Record which retained artifacts agents actually consult during the handoff. Artifact count and required workflow stages are descriptive. All four arms use the same behavioural evaluator.

## Predeclared controls

Run the original regressions and a private HTTP evaluator at both checkpoints. Check public responses and persisted outcomes. Run old-database startup, restart, and replay checks against throwaway database copies. The Ticket 2 evaluator carries forward Ticket 1 assertions except for the explicitly amended resize rule. Treat an agent test that changes that rule as a reviewed test revision and retain its prior evidence.

| Control | Required observation |
|---|---|
| Starting release | Original suite passes. The new confirmation case fails on the missing HTTP behaviour. |
| Ticket 1 reference | Original and Ticket 1 suites pass on fresh and legacy databases. Ticket 2’s partial-resize case fails at the expected 409. |
| Ticket 2 reference | Both sets of accepted obligations pass. The Ticket 1 restriction on resizing a fulfilled reservation is superseded by Ticket 2’s specific rules. |
| Duplicate confirmation | Replaying the same key, including concurrent delivery or a restart, produces one durable fulfillment. A seeded duplicate-apply variant fails at cumulative quantity. |
| Wrong stock accounting | Confirming units leaves available stock unchanged. Cancelling leaves shipped units consumed. Independent variants charge stock twice at confirmation or release shipped units at cancellation. |
| Legacy migration | An old database preserves existing stock and reservation states. A seeded reset or dropped-row variant fails at the legacy state assertion. |
| Fulfilled floor | A valid positive total below the fulfilled quantity is rejected. A seeded floor bypass fails at status and state. |
| Incorrect resize delta | A valid increase and decrease adjust only the difference. A seeded full-total charge fails at the stock assertion. |
| Partial write on rejection | Insufficient stock leaves total, fulfilled quantity, and available stock unchanged. A seeded partial-write variant fails at the preserved-state assertion. |
| Handoff regression | Both releases preserve confirmation replay and repeat cancellation. A seeded second-release regression fails at the preserved obligation or receipt assertion. |

Keep variants single-fault where practical. Freeze defect IDs, expected failing assertions, and excluded cases before the repeated run. Use synchronized real HTTP clients for duplicate delivery. Include a race-sensitive variant in scored coverage only after repeated fixture runs establish that the chosen check detects it reliably. Report fixture errors and flaky outcomes separately.

Preflight also exercises drift in the approved oracle, a leaked second-ticket canary, foreign workflow artifacts, the pinned CLI and archive path, broken loopback, and a timeout that seals its evidence and allows the next campaign to run. Run the same infrastructure checks against every affected workflow. Verify that cancellation produces a partial batch report.

## Scoring and release of results

- **Primary outcome** is the number of campaigns passing both checkpoints out of all scheduled campaigns. Show stage-one results, stage-two results, and handoffs blocked by stage one. Keep build failures, behavioural failures, integrity failures, and indeterminate results distinct. Show TADA’s advisory verdict beside the common result.
- **Test quality** is the distinct seeded faults detected by added black-box tests. Credit a fault only when the same test passes repeatedly on the submitted and reference releases and fails by assertion on the fault. Retain cumulative test coverage and tests added or changed after the handoff as separate measurements. A blinded reviewer cites tests for public response and state assertions, rejected changes, restart and replay, legacy compatibility, and concurrent delivery. Record evidence gaps and brittle assumptions per category. Reviewer usage is separate.
- **Efficiency** includes coding and total elapsed time, agent messages, turns, tool calls, and input, cached input, and output tokens at each stage. Report stage-two measures and whole-campaign totals. Record missing telemetry and show denominators with medians and ranges. Use the same pinned model and medium effort in every arm.
- **Evidence** includes the dated run IDs, package and fixture digests, schedule seed, change packets, stage prompts, oracle approval IDs, and control outcomes. Retain raw data locally. Generate the Markdown and JSON comparison and prove both can regenerate offline from sealed evidence.

Use an offline dry run and model-free fixture validation first. After owner approval of the TADA packets, run one live campaign per workflow as a smoke test. Each smoke must prove both ticket handoffs, all four workflow paths, and every report row. Freeze the suite revision before the comparison. Any change to the scenario, scoring, or orchestration that affects measurement gets a new revision and its own smoke evidence.

Run three fresh campaigns per workflow for the first repository report. Rotate the order so each workflow appears in three different positions across the repeats and record the seed. Publish all 12 campaign outcomes and 24 scheduled stage slots. Keep the pilot and smoke reports as separate evidence, and describe the repeated comparison as evidence for this application and model pin.

## Running the suite

The configured worker supplies the pinned NuGet feed and TADA release. From the benchmark checkout, prepare a fresh approval directory and validate the reference and fault controls.

```powershell
$worker = Get-Content -LiteralPath .local/codex-sdd/worker.json -Raw | ConvertFrom-Json
.\bench.ps1 batch-fixtures --suite codex-sdd-handoff --out .local/codex-sdd-handoff/owner-review --feed $worker.nugetFeed --tada-plugin $worker.tadaPlugin
.\bench.ps1 batch-controls --suite codex-sdd-handoff --out .local/codex-sdd-handoff/validation --feed $worker.nugetFeed
.\bench.ps1 batch --suite codex-sdd-handoff --dry-run --repeats 3
```

Open the generated `OWNER-REVIEW.md` and follow its commands after reviewing the two exact packets. The Ticket 2 approval explicitly replaces Ticket 1. Use `--approvals DIR` to run a separately registered fixture revision.

Run the actual worker checks and the four-campaign live smoke after registration.

```powershell
.\bench.ps1 batch --suite codex-sdd-handoff --preflight-only
.\bench.ps1 batch --suite codex-sdd-handoff --model gpt-6.1-sol
```

Use the verified smoke revision for the repeated comparison.

```powershell
.\bench.ps1 batch --suite codex-sdd-handoff --model gpt-6.1-sol --repeats 3
```

Handoff reports live under `.local/codex-sdd-handoff/batches`. Run `batch-report --batch DIR` to regenerate Markdown and JSON from the retained evidence. Each report counts both ticket slots for every scheduled campaign and records which test cases belong to files added or edited during Ticket 2.

If a handoff batch stops before all tickets are sealed, run `batch-resume --batch DIR` after the old controller has stopped. The command writes a new linked batch, verifies and copies the sealed prefix, and dispatches the unfinished tickets in the original order. An interrupted ticket gets a fresh workspace and session. Its original logs stay in the source batch. The continuation reruns worker and fixture preflight, checks the original pins, and records both runner hashes in its report.
