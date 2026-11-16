# Independent AI engineering review

The engineering-quality and process judgements can be made by AI reviewers instead of the benchmark
owner, who has a conflict of interest (TADA's author). The review is blinded where blinding is possible,
repeated with the presentation order swapped, and run by more than one reviewer model. Disagreement is
reported; it is never averaged away.

## Commands

```powershell
.\bench.ps1 review-bundle                  # build blinded bundles for the current pair; no model calls
.\bench.ps1 ai-review --set <setId>        # run the reviewers on those bundles (protocol must be approved)
.\bench.ps1 ai-review                      # build bundles and review in one step
```

Options: `--runs 'a,b'` picks the pair explicitly (default: the latest current-pin run of each workflow);
`--code-only` / `--process-only`; `--allow-draft` runs with an unapproved protocol and labels every result
DRAFT. `compare` shows the latest review covering the compared runs.

## What the reviewers do

**Blinded code review.** Each reviewer gets both changes as `change-A/` and `change-B/` in one session and
scores each against `experiments/checkout-retry/ai-review/rubric.md` (the same six dimensions as
`docs/engineering-quality-rubric.md`), with `path:line` evidence. Each reviewer runs `aiReview.passes`
times (default 2); passes alternate which change is shown first as A. A run's scores are therefore listed
per pass, for example `opus-5-5 4/3`.

Each bundle contains:

- `changes.patch`: the run's final diff, minus `aiReview.excludeGlobs`;
- `after/`: the full post-change text of every shown file;
- `files.md`: every non-excluded file with line counts; `aiReview.collapseGlobs` files (lock files, EF
  migration designers) are listed but not shown;
- `existing-test-failures.md`: original tests that passed at baseline and fail after the change (no hidden
  test results are shown, to avoid anchoring).

The reviewer also gets `task.md`, `decisions.md` (approved answers only, without hidden-test mappings) and
read-only access to the unchanged application source.

**Process review (not blinded).** Per run and reviewer, `chat.md` is classified: every human input `H<n>`
goes into exactly one of workflow step, business answer, corrective prompt, tooling intervention or other.
Required workflow steps (documented commands, baseline approvals) are not corrections. The chat reveals the
workflow, so this part is unblinded; it is close to a factual classification.

## Blinding: what is removed, and what leaks

Removed: workflow process records and tool scaffolding that are not part of the product, namely
`.specify/**`, `specs/**`, `.claude/**`, `.tada/**`, `tada.json`, `features/README.md` and TADA's generated
acceptance plumbing (`TadaAcceptance.cs`, `TadaRuntimeCheckpoint.cs`, `Tada.Acceptance.targets`).

Kept on purpose: every change that affects building, running or testing the product, even when it reveals
the workflow. That includes `Directory.Build.props`, `NuGet.Config`, `dotnet-tools.json`, `.gitignore`,
`Web.csproj`, the acceptance test project, its bindings and `.feature` files. Hiding them would hide real
cost and complexity from the reviewer, which would favour the workflow that made them.

Consequence: the TADA bundle names TADA about 30 times (its bindings call `TadaAcceptance`, and
`Directory.Build.props` explains a TADA-specific build switch). The Spec Kit bundle has no workflow names.
Blinding is therefore **partial**. The harness counts `aiReview.leakTerms` per bundle (never redacting) and
reports them, and the reviewer is asked to list any process clues it noticed and to ignore them when
scoring.

Absolute workspace paths and run IDs are replaced with `<app>` / `<run>`.

## Isolation

Reviewers run `claude -p` with:

- the working directory `<dataRoot>/ai-review-work/<setId>/<call>/`, holding only that call's inputs;
- `--add-dir` for the base source tree;
- tools: Read, Glob and Grep only. Bash, PowerShell, Edit, Write, web tools and subagents are disallowed;
- deny rules on the harness and on every data-root folder except the base tree and the call's own folder.
  This covers the run-to-bundle key, run evidence, evaluations, other passes and earlier results;
- `--setting-sources project,local --strict-mcp-config --no-session-persistence`, the effort and model
  environment overrides cleared, and `--max-budget-usd aiReview.maxBudgetUsdPerCall`.

The key `<dataRoot>/ai-reviews/<setId>/key.json` maps bundles to runs. A seeded shuffle, recorded in the
key, picks which run is bundle-1.

## Protocol approval

The protocol hash covers the prompt folder (`code-review.md`, `process-review.md`, `rubric.md`), reviewers,
passes, path filters, leak terms, the tool policy and the rubric dimensions. `doctor` prints it. To approve,
set `aiReview.status` to `approved` and add `approvedBy`, `approvedAt` and `approvedProtocolSha256` in
`experiment.json`. Any later change to the protocol invalidates the approval, and existing bundle sets
refuse to run.

## Reliability safeguards

- An unusable reply (no JSON object, or a missing or out-of-range score) is retried once. Both attempts
  are kept and the retry is flagged. A reply is never retried because of its scores.
- Flags in `summary.md` and the comparison:
  - **ORDER-SENSITIVE**: the same reviewer's scores for a dimension differ by 2 or more between passes.
  - **REVIEWERS DISAGREE**: reviewers' mean scores for a dimension differ by 1.5 or more, or their
    corrective-prompt classifications differ.
  - **Blinding leak**: leak terms were found, or the reviewer reported clues.
  - **DRAFT**: the protocol is not approved.
- Raw output (`calls/*.stdout.jsonl`), the exact arguments and the settings of every call are kept under
  `<dataRoot>/ai-reviews/<setId>/calls/`.

## Limitations

- All reviewers are Claude models, the same family that wrote both implementations. That bias affects
  both arms, but it is not removed.
- Blinding is partial (see above), and the process review is not blinded at all.
- One pair is a demonstration only. Review conclusions need repeated runs as much as the other metrics do.
- AI scores are judgements, not measurements. The owner, or better a neutral person, should spot-check at
  least one `summary.md` against the code.
