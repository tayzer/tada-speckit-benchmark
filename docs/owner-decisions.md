# Owner decisions

**Status 2026-11-14:** the benchmark owner approved business decisions D1-D10 and the hidden acceptance suite
(sha256 `380e5459...540f`, recorded in `experiment.json`). D7 was left to Claude at the owner's request: a
submission from a reloaded checkout page while the first is still in flight is a **retry** (exactly one order),
because that basket has not been ordered yet (consistent with D1). HT08 now asserts this; it fails on the
original code and passes on the reference fix. HT08 remains non-critical.

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

   The current configuration uses (a).
2. **TADA version.** TADA is evaluated from the **unreleased** commit `42f1429` (plugin 0.1.0-alpha.13). The published alpha.12 has no Claude Code packaging. Accept, or wait for a release?
3. **TADA plugin build.** It requires installing **PowerShell 7** and running TADA's build scripts, which download Zig. Approve both.
4. **Execution mode.** Choose the official mode. Interactive (Mode A) is recommended because both workflows have human approval gates.

## E. Experiment design

1. Number of repetitions per workflow. One per workflow is a demonstration only.
2. The order of arms (alternate or randomise), and whether the same operator runs both arms.
3. Engineering-quality reviewer(s), and whether to blind the reviews.
