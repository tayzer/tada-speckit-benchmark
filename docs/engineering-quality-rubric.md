# Engineering-quality rubric (human review)

> An independent, blinded AI review with the same dimensions is available: see `docs/ai-review.md`.

These scores are **human judgements**, recorded per run with `benchmark review-template --run <id>` and
`<dataRoot>/reviews/<runId>.json`. They are never produced by either competing workflow, and no AI score is
treated as authoritative. Review the diff in `<dataRoot>/runs/<runId>/changes/final.patch`.

**Blinding (recommended).** Give the reviewer the production and test diff only (exclude `.specify/`, `specs/`,
`.tada/`, `features/`, `tests/Acceptance/`) and an opaque label. Set `"blinded": true` when you do this. Workflow
artifacts usually reveal the workflow, so record when blinding was not possible.

Score each dimension 1–5. Write one sentence of evidence per score in `notes`.

| Dimension | 1 (poor) | 3 (acceptable) | 5 (excellent) |
|---|---|---|---|
| `maintainability` | The change is hard to follow, logic is duplicated, names mislead, or idempotency handling is scattered. | The change is readable and the logic is in the expected places. There are minor naming or structure issues. | The change is small and cohesive. A newcomer could find, understand and modify the retry handling quickly. |
| `unnecessary-complexity` | The change adds frameworks, queues, caches, abstractions or configuration the problem does not need. | There is some speculative generality. | The change is the minimum that meets the agreed behaviour, and every piece earns its place. |
| `architectural-consistency` | The change bypasses the existing layers, for example data access from Razor pages when services exist, and ignores the existing patterns (specifications, repositories, guard clauses). | The change mostly follows the existing structure, with some leakage. | The change fits the existing ApplicationCore, Infrastructure and Web layering and conventions, and adds a migration when the schema changes. |
| `error-handling` | Failures are swallowed, generic catch-alls hide real errors, or the customer sees raw exceptions. | The main failure paths are handled, but some edge cases leak. | Failures are explicit. Duplicates and concurrency conflicts map to clear outcomes, and genuine errors still surface. |
| `test-quality` | There are no tests, the tests only re-state the implementation, or the change weakens or deletes existing tests. | The tests cover the happy path and one duplicate scenario. | Tests cover concurrency, retries and legitimate repeat purchases. They fail without the fix (see the mutation control) and are deterministic. |
| `security-and-reliability` | The change introduces race windows, client-trusted values (such as prices or IDs) without validation, unbounded growth, or secrets in code. | There are no obvious new risks, but some assumptions are unverified. | The change is atomic under concurrency, has no new trust of client input, cleans up or bounds stored keys, and keeps transaction scope tight. |

Also list, per run:

- `risksDiscovered` / `risksMissed`. Examples:
  - concurrency between two requests
  - failure after the order is saved
  - the time window for the idempotency key
  - multiple browser tabs
  - basket transfer on login
  - the InMemory provider not enforcing unique indexes
- `incorrectAssumptions`: assumptions the workflow made that contradict the decisions register or the code.
- `necessaryQuestionsIdentified`: business questions the workflow raised that the decisions register shows were genuinely needed.
- `verificationOmissions`: checks it should have run but did not, for example the existing test suite or a concurrency test.
- `reviewMinutes`: the reviewer's own time. This is not included in the workflow's human effort.
