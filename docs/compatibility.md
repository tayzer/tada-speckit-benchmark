# Compatibility findings (investigated 2026-11-13)

## Environment observed

| Item | Value |
|---|---|
| OS | Windows 11 Enterprise 10.0.26300 |
| Claude Code CLI | 2.1.323 (pinned in `experiment.json`) |
| .NET SDKs | 8.0.415, 10.0.103, 10.0.403. eShop's `global.json` (10.0.0, `latestFeature`) resolves to 10.0.403. |
| PowerShell | Windows PowerShell 5.1 only. **PowerShell 7 (`pwsh`) is not installed.** |
| uv / uvx | Installed. Used to run Spec Kit's `specify` CLI. |
| SQL Server LocalDB | `MSSQLLocalDB` (2019 and 2025 engines). Used by the hidden suite. |
| Docker | Not installed. Not required. |

## eShopOnWeb at `f21d6233b851897371c25c5e7ffa67bb8552dba8`

- **Pinned commit.** The SHA was verified: it is `origin/main` HEAD, "Merge pull request #280 … jwt-signing-key-278", dated 2026-10-13.
- **Build.** Restore and build succeed with 57 warnings (NuGet vulnerability advisories plus analyzers).
- **Baseline tests.** Each run of `prepare-base` writes the exact figures to `base.json`.

  | Project | Result |
  |---|---|
  | UnitTests | 44/44 pass |
  | IntegrationTests | 3/3 pass |
  | FunctionalTests | 12/12 pass |
  | PublicApiIntegrationTests | **No tests discovered** under `dotnet test`. This MSTest project is not picked up by the VSTest adapter. It is recorded as a known baseline issue, so it cannot produce regressions. |
  | EndToEndTests | Excluded. Playwright needs installed browsers and a separately running app. |

- **Checkout.** `Pages/Basket/Checkout.cshtml.cs` runs three steps in order:
  1. `SetQuantities`
  2. `OrderService.CreateOrderAsync`, which saves the order with its own `SaveChanges`
  3. `DeleteBasketAsync`

  There is no transaction and no idempotency key, so duplicates arise when:
  - two submissions overlap, because both read the basket before either deletes it;
  - a failure happens after the order commits but before the basket is deleted, and the customer then retries.

  A purely sequential resubmission is already safe: the basket is gone, so `EmptyBasketOnCheckoutException` redirects to the basket page. The draft hidden suite reproduces this baseline behaviour (HT02/HT03/HT05 fail, HT01 passes).
- **Database providers.** Development uses SQL Server LocalDB, and production uses Azure SQL. The existing functional tests use EF InMemory, which enforces neither unique indexes nor transactions. A correct fix based on a unique constraint or a transaction would therefore look broken under InMemory. **The hidden suite runs against LocalDB** for this reason; it needs an owner decision.
- **Table names.** Catalog items live in the `Catalog` table, not `CatalogItems`. The hidden suite queries only `Orders`, `OrderItems`, `Baskets`, `BasketItems` and `Catalog`.
- **Repository contents.** The repository contains its own `.claude/skills` (Playwright helpers). They load identically in both workspaces and are recorded rather than removed.
- **Compatibility patches.** No compatibility patches are needed, so `compatibilityPatches` is empty.

## GitHub Spec Kit

- **Version.** Pinned to `v1.4.0` = `0019fa14d12f45edabc2cc3176c9168a68bd0f97`. The setup uses that SHA through `uvx --from git+…@SHA`.
- **Setup command.** This is the documented existing-project, non-interactive setup:
  `specify init --here --force --non-interactive --integration claude --script ps --ignore-agent-tools`
  - `--ai` was removed in 0.10.0.
  - Templates are bundled in the wheel, so init downloads no release ZIP and needs no GitHub token.
- **Files it writes.**
  - `.claude/skills/speckit-*/SKILL.md`: nine skills, invoked as `/speckit-specify`, and so on.
  - `.specify/` (constitution, PowerShell scripts, templates, `init-options.json`, `integration.json`).
  - No `CLAUDE.md` and no `~/.claude` changes.
- **Scripts.** The scripts target Windows PowerShell 5.1, and the skills do not call `pwsh`. Execution policy is the remaining risk.
- **Human interaction.** The documented order is constitution → specify → clarify → plan → tasks → analyze → implement → converge. Human interaction happens at:
  - `specify` (up to 3 `[NEEDS CLARIFICATION]` questions);
  - `clarify` (up to 5);
  - `checklist`;
  - the "proceed anyway?" gate in `implement`.
- **Git.** The core flow needs no git and creates no branches; feature folders go under `specs/NNN-*`. The harness commits setup files before the run, so the reported diff contains only the agent's work.

## TADA

- **Pinned release.** `v0.1.0-alpha.13.107` (source `7e028efdcb28813970117c7cfdd507e037aca653`, published 2026-11-16 11:50 UTC as a prerelease). The harness uses the official `TADA-plugin-0.1.0-alpha.13.107.zip` asset, SHA-256 `50dbe79d…69e5`, which matches the release `SHA256SUMS`. The extracted directory hash is pinned in `experiment.json`.
- **Mid-benchmark fix (fairness note).** TADA run `20261116-090523-tada-f5df92` on alpha.13.105 hit a TADA verifier defect on eShopOnWeb: `Build input changed during build`, caused by Blazor static web assets and SDK transitive project references. The TADA maintainer fixed it in alpha.13.107 (`src/Tada.Core/BuildInputs.cs`, `RunEngine.cs`; nothing task-specific) using that run's workspace, and TADA was re-run. The Spec Kit run on the same harness commit was kept unchanged. This is an asymmetry: TADA received a fix informed by the benchmark application, while Spec Kit had no comparable opportunity. The failed alpha.13.105 run is kept as evidence of the defect. TADA's own investigation note (`docs/validation/2026-11-16-build-input-transforms.md`) reports a remaining INDETERMINATE result on the Web project, caused by eShop's `BundleMinify` target ordering.
- **Earlier pin.** alpha.13.105 (`bd11c12a`, plugin zip SHA-256 `dbae497d…c543`) was used from 2026-11-14 until the fix above. Its skill files are identical to alpha.13.107; only the bundled Tada.Cli differs.
- **History.** The harness first pinned unreleased source `42f1429` (plugin `0.1.0-alpha.13`), because the then-latest release alpha.12 had no Claude Code packaging. alpha.13.105 was published later the same day. Between the two commits there are **no changes under `src/` or `tests/`** (Tada.Cli is the same code). The agent-facing differences are two wording lines in `SKILL.md`, which now name Cursor, and a version comparison in `Initialize-Tada.ps1` that only applies when an older Tada.Cli is already installed in the repository. TADA runs made with the source build record `pluginSha256 b2199946…` and are excluded from the default `compare`.
- **Packaging.**
  - The plugin has **one skill** (`tada:tada`) and no commands, agents, hooks or MCP servers.
  - The skill is auto-selected for ordinary requests.
  - The plugin needs a bundled verifier feed, the `Tada.Cli` nupkg. A raw `plugins/tada` folder from git is therefore incomplete. The release zip includes it (`feed/Tada.Cli.0.1.0-alpha.13.105.nupkg`), so no build is needed.
  - `scripts/Get-TadaRelease.ps1` downloads the zip, verifies it and extracts it. The zip also contains marketplace and Codex/Cursor manifests; `--plugin-dir` ignores them.
- **Activation.** The harness uses the documented per-session form `claude --plugin-dir <extracted plugin>`. It does not use a marketplace install, which would cache the plugin under `~/.claude/plugins`.
- **Runtime requirement: PowerShell 7.** The skill tells the agent to run `pwsh -NoProfile -File Initialize-Tada.ps1 …`.
  - **Without `pwsh` on PATH, TADA cannot initialise.** Install PowerShell 7 before the benchmark.
  - It must be present for both arms, so the environment stays identical.
- **What `tada init` adds to the app.** It writes these files if they are absent:
  - `.tada/`, `tada.json`, `global.json` (eShop already has one, so it is preserved)
  - `NuGet.Config`, `.gitattributes`, `Directory.Build.props` (lock files), `Directory.Build.targets`
  - `tests/Acceptance` (Reqnroll 3.3.4 + MSTest 4.5.2 on Microsoft.Testing.Platform)
- **xUnit compatibility.**
  - TADA's acceptance and verification-plan checks are MSTest/MTP only.
  - eShop's existing **xUnit v3** tests cannot be plan-managed, but the skill can still run them with `dotnet test`.
  - The new `Directory.Build.props` enables `RestorePackagesWithLockFile` for the whole solution. This can change restore behaviour and the diff; the evaluator records it rather than reverting it.
  - The acceptance project is not added to the `.slnx`.
- **Human approval gates.**
  - The agent must get human agreement on Gherkin examples before production code.
  - Protected baselines are approved **only by a human** running `tada baseline approve --review <id> --by <name>`. The skill prepares this command and must not run it.
  - The harness never approves baselines. In managed mode such commands are denied and recorded; in interactive mode the operator runs them in the session, where they show up in `chat.md`.
- **Global state.** No writes to `~/.claude` were found.
  - `dotnet tool install` fills the NuGet cache, and different alpha builds share a version string. The harness therefore gives every run its own `NUGET_PACKAGES` (both arms).
- **Evidence.** TADA writes `.tada/runs`, `.tada/baselines`, `.tada/approvals`, etc. These are captured in the final commit and listed as workflow artifacts.

## Claude Code CLI facts verified locally (2.1.323)

- **`--setting-sources project,local`.**
  - Removes user plugins, user hooks (for example, a SessionStart hook that rewrites the agent's style), user skills and user MCP servers from the init event.
  - Claude.ai account connectors still load unless `--strict-mcp-config` is also given. With it, `mcp_servers` is empty.
- **Deny rule form.** `Read(//c/Users/.../secret/**)` blocks the Read tool on Windows paths ("File is in a directory that is denied by your permission settings").
- **Stream-json output.**
  - Each content block is a separate `assistant` line repeating the same `message.id` and `usage`.
  - `result` carries `total_cost_usd` and `modelUsage`, which are **cumulative for the session** (verified across `--resume`: 0.00157153 → 0.00292885).
  - It also carries `model_usage_since_last_result`, the per-invocation delta.
- **Init event.** It does **not** report the effective effort here, only `per_turn_effort_active`. The transcript's assistant entries do carry `effort`.
- **Transcripts.**
  - Written to `~/.claude/projects/<cwd with non-alphanumerics → '-'>/<session-id>.jsonl` for both interactive and `-p` sessions.
  - Subagents go under `<session-id>/subagents/agent-*.jsonl` with `isSidechain: true`.
  - A `cost-state` line holds cumulative totals per process.
  - The docs call this format internal; the parser is defensive.
- **Pricing check.** Haiku 5.5 list pricing reproduces the CLI's `costUSD` exactly. That covers input, output, cache read and 1h cache write.
