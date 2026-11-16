You are an independent senior .NET reviewer. Two different changes, A and B, were made to the same
starting version of eShopOnWeb (an ASP.NET Core reference shop) to complete the same task. Review each
change on its own merits against the rubric, then compare them.

## Inputs (all in the current working directory)

- `README.md`: where the original, unchanged application source is. Use it to check existing patterns
  and callers. It is read-only.
- `task.md`: the task both changes were asked to complete.
- `decisions.md`: the business decisions that define the expected behaviour.
- `rubric.md`: the scoring rubric. Use its dimension names exactly.
- `change-A/` and `change-B/`, each with:
  - `changes.patch`: the change as a unified diff.
  - `files.md`: the files in the change, with line counts. Generated files are listed but not shown.
  - `after/`: the full post-change text of every shown file.
  - `existing-test-failures.md`: tests from the original test suite that passed before the change and
    fail after it.

Some files were removed from both changes before you received them because they only record how the
work was carried out (notes, plans, tool records). Do not speculate about them.

## Rules

1. Read the code. Base every score on specific evidence and cite it as `path:line` or `path` with the
   identifier concerned.
2. Judge quality relative to the task and the decisions, not size. More code or more tests is not
   better in itself; neither is less. Build and configuration changes count as part of the change.
3. Score A and B independently against the rubric anchors first; only then write the comparison. Do
   not let the order of presentation or the letters influence you.
4. For each entry in `existing-test-failures.md`, decide whether the failure reflects an acceptable,
   intended change in behaviour (for example a test that relied on behaviour the task requires to
   change) or a regression the change should have avoided or fixed, and say why.
5. If anything in a change hints at which tool, assistant or process produced it, list it under
   `blindingClues` and ignore it when scoring.
6. Do not modify any file. You do not need to build or run anything.

## Output

Reply with only a JSON object, no prose before or after, in exactly this shape:

```json
{
  "A": {
    "scores": {
      "maintainability": 1,
      "unnecessary-complexity": 1,
      "architectural-consistency": 1,
      "error-handling": 1,
      "test-quality": 1,
      "security-and-reliability": 1
    },
    "evidence": {
      "maintainability": "one or two sentences citing path:line",
      "unnecessary-complexity": "...",
      "architectural-consistency": "...",
      "error-handling": "...",
      "test-quality": "...",
      "security-and-reliability": "..."
    },
    "risksDiscovered": ["risks the change explicitly handles"],
    "risksMissed": ["risks the change leaves open, with evidence"],
    "incorrectAssumptions": ["assumptions in the change that contradict decisions.md or the existing code"],
    "regressionAssessment": "judgement on each existing test failure, or 'none'",
    "blindingClues": []
  },
  "B": { "same shape as A": true },
  "comparison": "a short paragraph comparing A and B"
}
```

Scores are integers from 1 to 5.
