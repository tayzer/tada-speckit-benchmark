# Operator runbook

## The whole procedure

```powershell
.\bench.ps1 run speckit      # do the exercise in Claude Code, /exit, press f
.\bench.ps1 run tada         # same exercise, same answers, /exit, press f
# review each run: read chat.md + final.patch, fill in reviews\<run-id>.json
.\bench.ps1 compare          # refresh the report after reviews are filled in
```

You never log anything by hand. When you exit Claude Code and press **f**, the harness:

1. snapshots the code the agent wrote (`changes/final.patch`)
2. copies the session transcript (secrets redacted) and writes a readable **`chat.md`**
3. counts tokens and cost (`telemetry.json`) without double-counting caching, resumes or subagents
4. counts your inputs from the transcript: typed messages, slash commands, answers to the agent's questions,
   rejected tool calls, interruptions, and how long the agent sat waiting on you
5. seals the run (read-only, hashed), then runs the hidden tests against a fresh copy
6. creates an empty review file and refreshes `reports\latest\comparison.md`

Press **l** instead of **f** to stop for now; `.\bench.ps1 resume <run-id>` picks the same session back up.
`.\bench.ps1 finish <run-id>` finishes a run without resuming it.

## During the session

Work exactly as you would normally with that workflow.

- **Spec Kit:** the session opens with `/speckit-specify <task>` already sent. When it finishes, continue with
  the documented commands yourself (`/speckit-clarify` if it asks, then `/speckit-plan`, `/speckit-tasks`,
  `/speckit-implement`). They are counted as slash commands automatically. Do not just say "go ahead": if the
  workflow's commands are never run, the arm is plain Claude Code and the run is not a Spec Kit run.
- **TADA:** let the skill drive. Approve protected baselines yourself when you agree. Run the approval inside the
  session as `! dotnet tool run tada baseline approve ...` so it appears in `chat.md`; a separate terminal works
  but leaves no trace.
- **Business questions:** answer from `experiments/checkout-retry/decisions.json`, ideally by pasting the answer
  text. Pasted answers are matched to their decision ID automatically, so the report can show both workflows got
  the same answers. If a question is not covered, decide it once, add it to the file, and give the same answer in
  both runs.
- Don't change model, effort or permissions mid-run (`/model`, `/effort`, `/permissions`). If you do, the report
  flags it.

## Reviewing a run

Open the run's `chat.md` and `changes\final.patch` (paths are printed at the end of `run`). Human inputs in
`chat.md` are numbered H1, H2 .... In `reviews\<run-id>.json`:

- `reviewer`: your name or initials
- `scores`: 1-5 per dimension, see [engineering-quality-rubric.md](engineering-quality-rubric.md)
- `correctivePrompts`: the H numbers where you had to correct the agent's mistake, e.g. `[4, 7]`
- `toolingInterventions`: the H numbers where you fixed tooling or the environment
- the risk and assumption lists are optional

Then `.\bench.ps1 compare`. Reviewing blind is easier if someone else renames the folders first.

## What the human-effort numbers mean

| Number | How it is measured |
|---|---|
| Human inputs | Counted from the transcript, by type. |
| Time agent waited on human | For each input, the time since the agent's last activity: reading, deciding, typing. Gaps over 30 min are capped and flagged (set `evidence.humanIdleCapMinutes`). |
| Corrective prompts / tooling interventions | Your judgement in the review file. Shown as "not reviewed" until filled in. |

Not captured: permission prompts you **approve** (Claude Code doesn't record them; rejections are recorded), and
reading you do while the agent is still working.

## Before the official runs (one-time)

1. Approve or edit `experiments/checkout-retry/decisions.json` (D7 is undecided), the agent settings in
   `experiment.json`, and the hidden suite in `evaluator/` (set `evaluation.status`, `approvedBy`,
   `approvedSuiteSha256`; `doctor` prints the hash). Until then results are labelled DRAFT.
2. Install PowerShell 7 (`winget install Microsoft.PowerShell`). TADA's skill calls `pwsh`.
3. Fetch the TADA plugin: `pwsh -File scripts\Get-TadaRelease.ps1` (downloads the pinned release zip and checks
   its SHA-256). `doctor` must report the extracted directory hash equal to `workflows[tada].pluginSha256`.
4. Commit this repository so each run records the exact harness version.
5. `.\bench.ps1 doctor` must report no blocking problems.

Alternate which workflow goes first across repetitions. One run each is a demonstration, not evidence.

## Advanced (optional)

Everything below still works but is not needed for the procedure above.

- `run` also does a ~$0.01 Haiku check that the workflow's skills/plugin actually loaded; skip with
  `--skip-activation-check`.
- Managed mode (`start --run <id> --mode managed`, `continue --run <id> --message ...`) drives `claude -p` with
  stream-json. Approval-gated steps (TADA baseline approval, Spec Kit checklist gates) can't be approved from inside
  it, so interactive is the faithful mode for these workflows.
- Manual logging (`timer`, `interaction`, `answer`) adds operator-logged events and timers to the report if you
  want them.
- `evaluate --run <id> --mutation-control` reruns the hidden tests plus a control that reverts the production change.
- `verify --run <id>` re-checks a sealed run's hashes.
