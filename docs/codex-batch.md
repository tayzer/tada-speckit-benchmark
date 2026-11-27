# Codex batch comparison

Run from the benchmark checkout on Windows x64. The runner uses PowerShell 7 for the worker.

```powershell
.\bench.ps1 batch --suite codex-sdd --dry-run
.\bench.ps1 batch --suite codex-sdd --preflight-only
.\bench.ps1 batch --suite codex-sdd
.\bench.ps1 batch --suite codex-sdd --repeats 5
.\bench.ps1 batch-report --batch '<batch directory printed by the runner>'
.\bench.ps1 batch-reevaluate --batch '<completed live batch directory>'
```

Each batch writes its manifest, sealed attempt evidence, and `report/comparison.md` and `report/comparison.json`. The default evidence root is `.local/codex-sdd/batches`. Use `--out` for a different controller-only directory. Use `--seed` to reproduce the order. The default is 1729. Dry runs use fabricated results, including a timed-out attempt. The first live batch is a pilot with one attempt per workflow and task. A blocked preflight exits 2 with a report, and cancellation exits 130 with retained attempts. Individual attempt failures appear in the report. A live batch with no coding output exits 2.

The default model pin is `gpt-6.2-sol` at medium effort. Preflight checks the signed-in worker's model catalog and lists its available medium-effort models if the selected model is missing. Use `--model ID` to run a pilot with a different explicit pin. The batch records that pin and uses it for every workflow and blinded review.

Use `batch-reevaluate` to recover scoring when a Windows sandbox setup failure interrupted the candidate build. The command checks the original pins and seals, reruns the affected evaluations, and writes a separate comparison with the original batch ID. It reuses all sealed coding and review evidence with zero model calls.

Add `--retry-openspec` if the original OpenSpec transcript shows that the worker lacked the CLI. That option retries only the affected OpenSpec slots in fresh workspaces with the recorded model and reasoning effort.

## Pin the inputs

Prepare an offline NuGet feed with the exact locked packages needed by the inventory projects and the TADA acceptance runner. Use a flat directory of `.nupkg` archives or a NuGet-generated hierarchical feed readable by both Windows accounts. Use the same absolute feed and plugin paths for approval and for the batch. Download the checksum-verified TADA `0.1.0-alpha.13.107` plugin using the existing release script. Build the Spec Kit and OpenSpec templates once with `uvx` and Node 22.12 or newer. Keep the generated `openspec-package` beside the OpenSpec template. The batch stages it offline and adds its executable to the OpenSpec arm's PATH. Use absolute `openSpecPackage` and `node` paths in `worker.json` when the package or executable lives elsewhere.

```powershell
.\scripts\New-CodexBenchmarkTemplates.ps1 -Out '<new template directory>' -TadaPlugin '<extracted TADA plugin directory>'
.\bench.ps1 batch-controls --out .local/codex-sdd/validation --feed '<offline NuGet feed>'
.\bench.ps1 batch-fixtures --out .local/codex-sdd/owner-review --feed '<offline NuGet feed>' --tada-plugin '<extracted TADA plugin directory>'
```

The fixture command prints each review ID and its approval command. For each task, the fixture owner reviews `<task>-review.json`, the captured files under `<task>/.tada/snapshots`, and the bindings and runner policy. The owner records approval with the printed `tada baseline approve` command from that task workspace. Then freeze the approvals:

```powershell
.\bench.ps1 batch-fixtures --out .local/codex-sdd/owner-review --register
```

Repeating registration verifies the existing receipt and its approved bytes. The fixture reuses the MIT-licensed TADA native launch and checkpoint helpers. The registered fixture is tied to the complete oracle and exact approval history. Prepare a new directory and repeat owner review if those inputs change. The measured task starts after this one-time agreement. The TADA release contains the main `$tada` skill, which the adapter invokes with the approved implementation scope.

## Configure the worker

### Guided setup on a staged checkout

If `.local/codex-sdd/worker.example.json` and the pinned inputs are already staged, open PowerShell as administrator using your normal benchmark account. Run this from the checkout:

```powershell
.\scripts\Initialize-CodexBenchmarkWorker.ps1
```

The command creates the dedicated standard account and runner folders, configures the benchmark permissions, prepares portable PowerShell under the runner tools folder when needed, verifies the pinned Codex package, and writes `worker.json`. Choose the worker's Windows password at the prompt. Complete Codex device sign-in when prompted. If device sign-in is unavailable, sign in once as the worker, run `codex login` using the printed executable path, and rerun the setup command. Reruns reuse a worker created by the guided setup.

The portable PowerShell ZIP is verified against the published SHA-256 and extracted directly. Rerun the same setup command after an interrupted setup to reuse its folders and verified archives.

Use `-Preview` to list the setup actions without changing the machine. The owner reviews, approves, and registers the two fixture baselines separately. Run the batch in your normal PowerShell after setup.

### Manual setup

Provision a dedicated **standard** local Windows account. Initialize its Windows profile, authenticate Codex CLI `0.162.1` under that account, and provision its elevated Windows sandbox using the Codex setup flow. Reserve the account for the benchmark. Install Codex, .NET SDK `10.0.403`, Git, and PowerShell 7 where both accounts can execute them.

Run the harness under the controller account. Put the worker workspaces in a separate directory. Allow the worker to traverse the parent and read the offline dependencies. Keep the benchmark checkout, controller evidence, and controller credentials inaccessible to the worker. The harness grants access to the active workspace and revokes it after use. Export the Windows worker credential with the built-in Windows user-bound encryption under the controller account:

```powershell
Get-Credential -UserName "$env:COMPUTERNAME\TadaBenchWorker" | Export-Clixml -LiteralPath '<controller-only credential file>'
```

Use `.local/codex-sdd/worker.json` or pass `--config`. Populate every path with an absolute path. Copy the six template path and digest fields from `template-pins.json`.

```json
{
  "workerRoot": "D:/CodexBenchmarkWorkers",
  "workerProfile": "C:/Users/TadaBenchWorker",
  "credentialFile": "D:/ControllerOnly/benchmark-worker.clixml",
  "dotnet": "C:/Program Files/dotnet/dotnet.exe",
  "codex": "D:/PinnedTools/codex.exe",
  "git": "C:/Program Files/Git/cmd/git.exe",
  "powerShell": "C:/Program Files/PowerShell/7/pwsh.exe",
  "nugetFeed": "D:/PinnedTools/nuget",
  "approvals": "D:/Dev/Benchmark/.local/codex-sdd/owner-review",
  "tadaPlugin": "D:/PinnedTools/tada",
  "tadaPluginSha256": "<tree SHA-256>",
  "specKitTemplate": "D:/PinnedTools/templates/speckit",
  "specKitTemplateSha256": "<tree SHA-256>",
  "openSpecTemplate": "D:/PinnedTools/templates/openspec",
  "openSpecTemplateSha256": "<tree SHA-256>"
}
```

Allow the local `CodexSandboxUsers` group to read the portable tools and pinned package feeds. The batch temporarily blocks that group from reading the private benchmark tree and restores those permissions on exit. Run one live batch or preflight per checkout at a time.

Use `--preflight-only` to validate the configured Windows worker, the eight Git-backed workspace restores, and the fixture controls with zero model calls. The preflight checks versions, login, ACL canaries, real loopback traffic, every hidden evaluator control, and TADA RED/GREEN using the registered fixtures. The agent receives project-local skills and ignores personal Codex configuration. Coding and submitted code run under the worker policy. Hidden HTTP expectations stay with the controller. The fixture-only command runs the trusted bundled reference and fault variants under the caller for local validation.

## Read the report

Shared HTTP tests determine behavioural correctness. Original regression checks use the bundled original tests against the sealed API. TADA's L1 advisory result appears separately. For mutation credit, an added test must pass repeatedly on the submitted and reference versions and fail by xUnit assertion repeatedly on the faulty version. The report records invalid executions and flaky results. The review sees anonymized added test source, a fixed rubric, and the shared brief. Framework and test naming can still reveal workflow hints.

Token counts come from completed Codex turns. Cached input forms part of total input. Failed or truncated turns can leave incomplete token totals. Reviewer usage is separate. Test evidence and raw transcripts stay local. Review the Markdown before including it in TADA.
