# TADA vs GitHub Spec Kit — independent benchmark harness

A small, transparent harness for comparing two AI-assisted development workflows. Both run on the
**same Claude Code CLI host, model, effort and permissions** against the same pinned eShopOnWeb revision, and both
are scored by an **independent hidden evaluator** that neither workflow can see.

- Workflow A: [GitHub Spec Kit](https://github.com/github/spec-kit) `v1.4.0` (`0019fa14`)
- Workflow B: [TADA](https://github.com/tayzer/TADA) `v0.1.0-alpha.13.105` (`bd11c12a`, published prerelease plugin zip, checksum-pinned)
- App: [NimblePros/eShopOnWeb](https://github.com/NimblePros/eShopOnWeb) `f21d6233b851897371c25c5e7ffa67bb8552dba8` (verified)
- Task (identical for both, SHA-256 pinned): *“Customers have reported duplicate orders when checkout is retried after a timeout. Please fix this while ensuring customers can still make legitimate subsequent purchases.”*

> No real TADA vs Spec Kit run has been executed. The evaluator's acceptance criteria and the business
> decisions register are **DRAFT proposals awaiting the benchmark owner**.

## Layout

```
src/Benchmark/                 .NET 10 console app (`benchmark`), no third-party dependencies
  Config/ Workspace/ Claude/ Telemetry/ Human/ Evaluation/ Reporting/ Runs/ DryRun/ Cli/
tests/Benchmark.Tests/         62 xUnit tests + fixtures captured from real CLI 2.1.323 output (sanitised)
experiments/checkout-retry/    experiment.json, task.md (the prompt), decisions.json (DRAFT business answers)
evaluator/                     hidden behavioural suite (DRAFT) + control patches — never copied to workspaces
pricing/models.json            model-equivalent pricing (estimates only)
docs/                          compatibility, telemetry, isolation, rubric, operator runbook
scripts/Get-TadaRelease.ps1    downloads and checksum-verifies the pinned TADA release plugin
experiments/*/ai-review/       pinned prompts and rubric for the independent AI review (docs/ai-review.md)
experiments/checkout-reload/   task 2: continuation of each arm's checkout-retry run (docs/continuation.md)
bench.ps1                      wrapper: builds the harness when sources change, then runs it
```

Evidence and workspaces live **outside** the repo in `paths.dataRoot` (default `D:\Dev\BenchmarkData`).

## Usage

```powershell
.ench.ps1 run speckit      # Claude Code opens with the task; work normally; /exit; press f
.ench.ps1 run tada         # same exercise
.ench.ps1 compare          # rebuild reports\latest\comparison.md (after filling in reviews)
```

After each run you get, under `<dataRoot>uns\<run-id>\`:

- `chat.md`: the readable conversation, human inputs numbered H1, H2 ...
- `changesinal.patch`: the code the agent wrote
- `telemetry.json`: tokens, cost, models, effort, tool calls, and human-input counts derived from the transcript

plus hidden-test results, an empty review file (`<dataRoot>eviews\<run-id>.json`) and an updated comparison
report. No manual logging is needed. Other commands: `resume <run-id>`, `finish <run-id>`, `status`, `doctor`,
`dry-run`; `.ench.ps1 --help` lists the advanced ones.

Build and test the harness: `dotnet test Benchmark.slnx`.

See [docs/operator-runbook.md](docs/operator-runbook.md) for the procedure and how each number is measured, and
[docs/compatibility.md](docs/compatibility.md), [docs/telemetry.md](docs/telemetry.md),
[docs/isolation-and-security.md](docs/isolation-and-security.md),
[docs/engineering-quality-rubric.md](docs/engineering-quality-rubric.md), [evaluator/README.md](evaluator/README.md).

## Principles

- **No winner is computed.** The report shows raw results, denominators, medians and ranges, and every integrity deviation.
- **Every intrinsic workflow difference is recorded**: setup files, `--plugin-dir`, commands issued, approvals.
- Neither workflow grades itself. Engineering quality is a human review against a fixed rubric.
- One run per workflow is a **demonstration**. Repeated runs are aggregated descriptively.
