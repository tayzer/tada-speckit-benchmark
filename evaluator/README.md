# Independent evaluator: hidden acceptance suite (DRAFT)

> **Status: DRAFT. Not approved by the benchmark owner.** `experiment.json → evaluation.status = "draft"`.
> Official evaluations refuse to run until the owner approves. Approving means setting `status: "approved"`,
> `approvedBy`, `approvedAt` and `approvedSuiteSha256` (shown by `benchmark doctor`). Any later byte change to
> the suite invalidates the approval.

This directory is **never copied into an agent workspace**. `benchmark evaluate` copies `CheckoutRetry.HiddenTests/`
into `<fresh evaluation clone>/tests/__HiddenEvaluation/` only after the run is finished and sealed.

## What it checks

The suite works **black-box**:

- **Requests.** It sends real HTTP requests to the application through `WebApplicationFactory` and posts the checkout form exactly as rendered. Any hidden idempotency fields the implementation adds are therefore submitted too.
- **Assertions.** It checks only persisted rows (`Orders`, `OrderItems`, `Baskets`, `BasketItems`) and page outcomes.
- **Database.** The application runs against fresh SQL Server LocalDB databases and migrates and seeds them itself. This uses real transactions and unique indexes, whereas EF InMemory has neither.
- **Fault injection.** Faults are injected only at the database boundary (an EF Core command and transaction interceptor) and the HTTP boundary (an outer middleware):

  | Fault | Simulates |
  |---|---|
  | Delay the first order insert | A slow request that outlives the customer's timeout |
  | Fail the first order insert | A failure before persistence |
  | Fail the next DB command, or the response, after the order insert **commits** | A failure after persistence but before acknowledgement |

  If an implementation saves orders in a way the seam cannot observe, the test reports **INDETERMINATE**, not FAIL.

| Test | Category | Critical | Decision |
|---|---|---|---|
| HT01 | Resubmitting the same form after completion makes no second order | | D1 |
| HT02 | Two overlapping identical submissions → exactly one order | ✔ | D3 |
| HT03 | Client times out while the server is still processing; retry → exactly one order, no 5xx | ✔ | D3 |
| HT04 | Failure before persistence; retry → exactly one order | | D4 |
| HT05 | Failure after the order commits, before the response; retry → exactly one order | ✔ | D5 |
| HT06 | New checkout with the **same items** after a completed order → second order | ✔ | D1, D6 |
| HT07 | New checkout with different items → second order | | D6 |
| HT08 | Reloaded checkout page while the first submission is in flight → exactly one order (retry), and neither submission ends in a 5xx (added 2026-11-16, draft) | | D7, D3 |
| HT09 | Happy path still works: confirmation page, 1 order, basket emptied | ✔ | — |
| HT10 | Empty-basket checkout: no order, no 5xx | | — |
| HT11 | Two different customers checking out concurrently each get their order | | — |
| HT12 | Retry of a completed checkout shows the confirmation | | D2 |
| HT13 | Overlapping duplicate submissions produce no server error | | D3 |

## Validation of the suite (controls, no AI involved)

Run with `benchmark evaluate --control <patch|baseline> --allow-draft`:

| Control | Expected | Observed (2026-11-13) |
|---|---|---|
| `baseline` (pinned eShop, no change) | Duplicate scenarios fail; legitimate purchases and compatibility pass | HT02, HT03, HT05, HT12, HT13 FAIL. All others PASS. HT08 pending. |
| `controls/reference-transactional-checkout.patch` | Everything passes | 12 PASS, HT08 pending |
| `controls/seeded-overblocking.patch` (blocks any order within 10 minutes) | Legitimate-purchase tests catch it | HT06 and HT07 FAIL (plus the race tests) |

Re-validation after the draft HT08 change (2026-11-16): `baseline` 7 PASS / 6 FAIL including HT08;
reference patch 13/13 PASS. Both benchmark runs (Spec Kit `d26ba9`, TADA `485b83`) now fail HT08. In both,
the first, still-in-flight submission ends in HTTP 500 after the reloaded resubmission has placed the
order. Exactly one order is created in both.

The reference patch is an evaluator-side control only. It is one acceptable design, not the expected one.

## Outcome semantics

- **PASS / FAIL**: the behaviour was exercised, and the assertion held or did not.
- **INDETERMINATE** (cause recorded): the evaluator could not exercise the behaviour. Causes are:
  - LocalDB unavailable;
  - seam not reachable;
  - the hidden suite fails to compile against a changed public surface;
  - a pending owner decision.

  INDETERMINATE is never counted as a business-rule failure.
- **FAIL (implementation-build)**: the implementation does not compile, so no behaviour can be shown.

## Coupling to the implementation (kept minimal)

The suite compiles against the following:

- `Microsoft.eShopWeb.Web.Interfaces.IBasketViewModelService`. This is the `WebApplicationFactory` entry point, the same one the existing functional tests use.
- `Microsoft.eShopWeb.Infrastructure.Data.CatalogContext`. This is used only to attach the interceptors.
- Table names from the original schema.
- The seeded demo users.

Renaming any of these makes the suite INDETERMINATE (infrastructure) for that run. It is not counted as FAIL.
