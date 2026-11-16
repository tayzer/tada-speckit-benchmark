# Engineering-quality rubric

Score each dimension from 1 to 5 using the anchors below. Use 2 and 4 for work between two anchors.
Judge the change as a pull request to this application: production code, tests and any build or
configuration changes are all part of it.

| Dimension | 1 (poor) | 3 (acceptable) | 5 (excellent) |
|---|---|---|---|
| `maintainability` | Hard to follow, duplicated logic, misleading names, or idempotency handling scattered across layers. | Readable, logic in the expected places, minor naming or structure issues. | Small and cohesive. A newcomer could find, understand and modify the retry handling quickly. |
| `unnecessary-complexity` | Adds frameworks, queues, caches, abstractions, build or configuration changes the problem does not need. | Some speculative generality or incidental changes. | The minimum that meets the agreed behaviour; every piece, including build and configuration changes, earns its place. |
| `architectural-consistency` | Bypasses the existing layers (for example data access from Razor pages when services exist) and ignores existing patterns (specifications, repositories, guard clauses). | Mostly follows the existing structure, with some leakage. | Fits the existing ApplicationCore / Infrastructure / Web layering and conventions, and adds a migration when the schema changes. |
| `error-handling` | Failures swallowed, generic catch-alls hide real errors, or the customer sees raw exceptions. | Main failure paths handled; some edge cases leak. | Failures are explicit. Duplicates and concurrency conflicts map to clear outcomes, and genuine errors still surface. |
| `test-quality` | No tests, tests only restate the implementation, or existing tests are weakened or deleted without justification. | Tests cover the happy path and one duplicate scenario. | Tests cover concurrency, retries and legitimate repeat purchases, would fail without the fix, and are deterministic. Judge relevance and strength, not volume. |
| `security-and-reliability` | Introduces race windows, trusts client-supplied values (prices, IDs) without validation, unbounded growth, or secrets in code. | No obvious new risks, but some assumptions unverified. | Atomic under concurrency, no new trust of client input, bounded stored keys, tight transaction scope. |

Risks worth checking for (not exhaustive): concurrency between two requests; failure after the order
is saved; the lifetime or time window of any idempotency key; multiple browser tabs; basket transfer on
login; the EF Core InMemory provider not enforcing unique indexes; behaviour of existing callers and
existing tests.
