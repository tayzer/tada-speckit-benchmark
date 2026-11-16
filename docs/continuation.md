# Continuation experiments (task 2 onwards)

A continuation experiment gives both workflows a **second task on the codebase each of them already changed**.
It measures what the first experiment could not: the cost and quality of a later change, once each workflow's
one-off setup (TADA's verifier, lock files and acceptance project; Spec Kit's constitution and `specs/001`) is
already in place.

## How it differs from a normal experiment

| | Normal experiment | Continuation |
|---|---|---|
| Starting code | The same frozen base for both arms | Each arm starts from the **final commit of its own pinned parent run** |
| Workflow setup | Installed before the run | Not re-run; Spec Kit's files are already in the repo, and TADA is activated with the same `--plugin-dir` |
| Integrity check 1 | Same base commit | Each arm continues its pinned parent (`continuation.parentRuns`) |
| Existing-test regressions | Base commit tests vs base results | The **parent run's own final tests**, including tests the workflow added in task 1, vs the parent's evaluation |
| Hidden suites | The experiment's suite | The experiment's suite **plus** the parent experiment's suite, re-run unchanged, with tests that passed for the parent and fail now listed as regressions |
| AI review | Both changes reviewed against the shared base | Each change reviewed against its own starting version (`change-X/before/`); prompt says the starting points differ |

Because the starting code differs between arms, results describe **each workflow's second change**, not a
controlled comparison from one codebase. The report states this in its limitations.

## checkout-reload (task 2)

- **Task** (`experiments/checkout-reload/task.md`): customers get an error page when they reload checkout or pay
  from two tabs while the first payment is in flight.
- **Parents**: Spec Kit `20261115-124537-speckit-d26ba9`, TADA `20261116-134151-tada-485b83`. Both fail the
  stricter HT08 in the same way: one order is placed, but one of the two submissions returns HTTP 500.
- **Why this task**: both arms share the defect, so neither starts ahead on the behaviour under test. It was
  chosen after the task-1 results were known, and the config records that (`continuation.taskSelection`).
- **Decisions**: the task-1 register (D1-D10). D3 and D7 already define the expected behaviour, so there are
  no new business questions.
- **Hidden suite** (`evaluator/CheckoutReload.HiddenTests`, approved 2026-11-16 at `2b1d070f…d5a8`):

  | Test | Scenario | Decisions | Critical |
  |---|---|---|---|
  | RL01 | Reload while the first submission is in flight, pay again → one order, no 5xx on either | D7, D3 | ✔ |
  | RL02 | Two tabs opened before paying, both submitted while the first is in flight → one order, no 5xx | D3, D7 | |
  | RL03 | Customer stops waiting, reloads, pays again while the first is still processing → one order, no 5xx | D3, D7 | |
  | RL04 | After a reload race, a newly filled basket is a new purchase → second order, confirmation page | D1, D6 | ✔ |

- **Validation (2026-11-16, no model calls)**:

  | Target | RL01 | RL02 | RL03 | RL04 | Task-1 suite |
  |---|---|---|---|---|---|
  | Reference fix (`reference-transactional-checkout.patch`) | Pass | Pass | Pass | Pass | 13/13 |
  | Untouched baseline | Fail | Fail | Fail | Fail | 7/13 |
  | Spec Kit parent `d26ba9` | Fail | Fail | Fail | Pass | 12/13 |
  | TADA parent `485b83` | Fail | Fail | Fail | Pass | 12/13 |

  In both parents, every RL01-RL03 failure is the server-error assertion after the one-order assertion has
  passed. The bug report ("only one order is placed, but an error page") is therefore accurate for both arms.

  A smoke test of `prepare --experiment checkout-reload` for both arms started each from its parent's final
  commit, with workflow state intact, static activation passing and the parent workspace in the deny rules.
- **Regression check**: `CheckoutRetry.HiddenTests` at its approved hash (`044aafa9…4896`), plus each parent's
  own final test projects.

## Running it

```powershell
.\bench.ps1 doctor  --experiment checkout-reload
.\bench.ps1 run speckit --experiment checkout-reload
.\bench.ps1 run tada    --experiment checkout-reload
.\bench.ps1 compare --experiment checkout-reload
.\bench.ps1 ai-review --experiment checkout-reload
```

Operator rules are unchanged (docs/operator-runbook.md). For Spec Kit, run the documented commands after
`/speckit-specify`. For TADA, let the skill drive and approve baselines with `!` commands. In both cases, type
`/exit` and answer `f`.

The parent workspaces under `<dataRoot>/workspaces/` must still exist; a continuation clones from them.
