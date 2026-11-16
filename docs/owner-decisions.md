# Owner decisions

**Status 2026-11-14:** the benchmark owner approved business decisions D1-D10 and the hidden acceptance suite
(sha256 `380e5459...540f`, recorded in `experiment.json`). D7 was left to Claude at the owner's request: a
submission from a reloaded checkout page while the first is still in flight is a **retry** (exactly one order),
because that basket has not been ordered yet (consistent with D1). HT08 now asserts this; it fails on the
original code and passes on the reference fix. HT08 remains non-critical.

**Approved 2026-11-16 by the benchmark owner: hidden-suite change** (sha256 `044aafa9...4896`). Both AI code reviewers found that, in both
implementations, the submission that loses a reload-and-resubmit race (D7) ends in an HTTP 500. HT08 only
counted orders. The draft change also asserts that neither submission ends in a server error, as D3
requires for duplicate in-flight submissions. Validation results are in evaluator/README.md. The owner also approved the AI review protocol
(sha256 `9d9e045a...8fbe`: Opus 5.5 and Sonnet 5.5, two passes each, filters as configured) on the same day.

**Pending 2026-11-16: task 2, `experiments/checkout-reload` (docs/continuation.md).** Owner to approve:
1. the task wording in `experiments/checkout-reload/task.md`;
2. the hidden suite `evaluator/CheckoutReload.HiddenTests` (RL01-RL04). Validation results are in
   docs/continuation.md, and the hash is printed by `doctor --experiment checkout-reload`;
3. the AI review protocol for continuations (`experiments/checkout-reload/ai-review/`: same rubric and
   process prompt, with the code-review prompt adapted to different starting points);
4. the parent runs pinned in `continuation.parentRuns`.

Still open (sections C-E below): agent settings, Spec Kit first-message choice, execution mode and repetitions
use the current configuration unless changed.

---

## Original list of decisions

None of these has been decided on your behalf. The values currently in the configuration are **proposals**.

## A. Business behaviour (`experiments/checkout-retry/decisions.json`)

| Decision | Status | What is needed |
|---|---|---|
| D1–D6, D8–D10 | Proposed | Approve or edit each answer. |
| **D7** | Undecided | Is a submission from a *reloaded* checkout page, sent while the first one is still in flight, a retry or a new purchase? HT08 stays INDETERMINATE until you decide. |
| D2 | Proposed | Is "show the confirmation page on retry" a requirement (HT12) or only preferred? |

## B. Hidden acceptance suite (`evaluator/`)

1. Approve HT01–HT13, the critical set (HT02, HT03, HT05, HT06 and HT09), and the outcome semantics.
2. Approve **SQL Server LocalDB** as the evaluation database. It is the production-like provider with real transactions and unique indexes. The existing functional tests use EF InMemory, which would wrongly fail transaction- or constraint-based fixes.
3. Approve the fault-injection approach. It works at the EF Core command and transaction boundary plus an outer HTTP middleware. When an implementation cannot be observed there, the outcome is INDETERMINATE, not FAIL.
4. Once approved, set `evaluation.status: "approved"`, `approvedBy`, `approvedAt` and `approvedSuiteSha256`. `benchmark doctor` prints the hash.

## C. Agent configuration (`experiment.json → agent`)

| Setting | Proposal | Question for you |
|---|---|---|
| Model | `claude-opus-5-5` | Is this the model to use? |
| Effort | `high` | The Claude Code default for this model differs, so the level is set explicitly. Which level do you want? |
| `permissionMode` | `acceptEdits` | File edits inside the workspace are auto-accepted. Everything else is allow-listed or needs your approval. Acceptable? |
| Allow list | `dotnet restore/build/test` and read-only `git` (Bash and PowerShell tools) | Should workflow-specific scripts also be pre-approved for **both** arms? These are `.specify/scripts/powershell/*.ps1` and `pwsh … Initialize-Tada.ps1` / `dotnet tool run tada …`. Pre-approving reduces approval clicks, but they still appear as approvals in the report. |
| `WebFetch` / `WebSearch` | Denied for both arms | Keep them denied? |
| `--setting-sources project,local` + `--strict-mcp-config` | Exclude your user-level plugins (`caveman`, synced plugins), hooks, skills and MCP connectors from both arms | Confirm. Without this, your personal SessionStart hook would alter both agents' behaviour. |

## D. Workflow delivery

1. **Spec Kit invocation.** Both arms receive the identical natural-language request as their first message (`{prompt}`). Spec Kit's documented flow has the human type `/speckit-specify …`, `/speckit-plan`, and so on. Choose one of:
   - **(a)** Keep `{prompt}` and let the operator issue the documented commands afterwards. Each command is logged as a `workflow-command`.
   - **(b)** Set Spec Kit's `initialMessageTemplate` to `"/speckit-specify {prompt}"`. This is recorded as a deviation.

   Resolved 2026-11-14: **(b)**. The pilot under (a) never ran a Spec Kit command (the agent coded straight from the prompt), so the arm was not exercising Spec Kit. Every Spec Kit run records the template as a deviation.
2. **TADA version.** Resolved 2026-11-14: re-pinned to the published prerelease `v0.1.0-alpha.13.105` after the owner flagged that the source build (`42f1429`, 0.1.0-alpha.13) was not the latest. See docs/compatibility.md for the diff between the two.
3. **TADA plugin build.** Superseded: the release zip is used, so no build. PowerShell 7 is still needed at runtime.
4. **Execution mode.** Choose the official mode. Interactive (Mode A) is recommended because both workflows have human approval gates.

## E. Experiment design

1. Number of repetitions per workflow. One per workflow is a demonstration only.
2. The order of arms (alternate or randomise), and whether the same operator runs both arms.
3. Engineering-quality reviewer(s), and whether to blind the reviews. 2026-11-16: the owner chose an independent AI review (docs/ai-review.md). **Pending approval of the protocol:**
   - the three files in `experiments/checkout-retry/ai-review/`;
   - the reviewers (default `claude-opus-5-5` and `claude-sonnet-5-5`, both high effort; another model family is not available through Claude Code);
   - two passes, with the presentation order swapped;
   - the exclude/collapse filters, in particular that product-affecting build changes stay visible even though they reveal TADA.

   Approved by the owner on 2026-11-16 (protocol sha256 `9d9e045a...8fbe`), keeping Sonnet as the second reviewer.
