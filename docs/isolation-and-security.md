# Isolation, fairness and data handling

## Layout

```
D:\Dev\Benchmark\                harness repo (holds the hidden evaluator)     <- agents denied
D:\Dev\BenchmarkData\             paths.dataRoot (outside the repo)
  base\<sha12>\repo               frozen clone of the pinned commit              <- agents denied
  workspaces\<runId>\app          one agent's workspace (its cwd)
  workspaces\<runId>\nuget        that run's private NUGET_PACKAGES
  runs\<runId>\                   evidence (sealed after finish)                 <- agents denied
  evaluations\<evalId>\           fresh evaluation clones + results              <- agents denied
  plugins\tada-<hash>\tada        read-only pinned TADA plugin (--plugin-dir)
  reports\, reviews\
```

## How agents are kept away from hidden tests and each other

Several layers apply. None of them relies only on instructions to the agent.

1. **Separate trees.** Hidden tests live only in the harness repository. They are copied into a *fresh evaluation clone* after a run has finished and been sealed, and never into a workspace. Workspaces are clones with the `origin` remote removed.
2. **Permission deny rules.** Each run gets a generated `--settings` file. It denies `Read`, `Edit`, `Write`, `Glob` and `Grep` on:
   - the harness repo;
   - `runs`, `evaluations`, `base` and `reports`;
   - every other workspace that exists at preparation time.

   The rule form `Read(//d/...)` was verified to block access on this CLI version. Reads outside the working directory are not allow-listed, so they prompt in interactive mode and are denied in managed mode.
3. **Audit.** At `finish`, every tool input (including Bash and PowerShell commands) is scanned for forbidden roots and hidden-suite tokens. Any hit becomes an `ISOLATION` deviation and a failed integrity check.
4. **Fairness of permissions.** Both arms share the same mode, allow list and deny list. The permission-policy hash replaces run-specific paths with placeholders, so the integrity check can compare the arms.

**Limitation.** Windows has no OS sandbox for Claude Code. A Bash command could still read a denied path without naming it literally, for example by computing it. The audit catches literal references only. For stronger guarantees, run the agents under a separate Windows account that has no ACL access to the harness repository. This is supported by moving `paths.dataRoot` and launching the harness as that user, but it is not automated.

## Same configuration in both arms

The following settings are identical in both arms:

- **Claude Code settings.**
  - `--setting-sources project,local`: user-level plugins, hooks, skills, output styles and model overrides from `~/.claude/settings.json` are excluded equally.
  - `--strict-mcp-config`: user and account MCP connectors are excluded.
  - The global Claude Code configuration is **never modified**.
- **Model and effort.** `--model <full id>` and `--effort <level>`. `CLAUDE_CODE_EFFORT_LEVEL` and `ANTHROPIC_MODEL` are cleared for agent processes because they would override the flags.
- **Forbidden flags.** `--bare`, `--dangerously-skip-permissions` and `bypassPermissions` are refused by construction, and a test covers this.
- **Workflow-specific differences** are intrinsic to each workflow and are recorded rather than removed:
  - Spec Kit's files are committed in a "workflow setup" commit.
  - TADA's plugin is passed with `--plugin-dir`.

  The cross-contamination checks are:
  - The Spec Kit workspace must contain no `tada` components.
  - The TADA workspace must contain no `speckit` or `.specify` components.
  - Both are checked on disk and in the init event or probe.

## Secrets and retention

- Transcripts, stream captures and probe output are **redacted before they are stored**. This covers:
  - Anthropic keys and bearer tokens;
  - GitHub and AWS keys;
  - private keys;
  - `password=` and `secret=` values;
  - account and organisation UUIDs;
  - the literal values of secret-named environment variables.

  The JSON structure is preserved. Redaction is pattern-based and best-effort. It can be disabled with `evidence.redactSecrets: false`.
- The run record stores only the environment variables the harness sets, never the full environment.
- The harness never uploads anything.
  - Claude Code itself talks to Anthropic as usual.
  - The Spec Kit setup fetches the pinned Spec Kit source through `uvx`.
- Evidence is kept under the data root until you delete it. Sealed runs are read-only and covered by a hash manifest (`benchmark verify --run`).
  - The harness never deletes runs, workspaces or repositories.
  - Claude Code deletes its own transcripts after `cleanupPeriodDays` (30 by default), so `finish` imports them straight away.
