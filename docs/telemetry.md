# Claude Code telemetry: sources, accounting and limitations

## Sources by mode

| | Mode A: interactive (`benchmark start --mode interactive`) | Mode B: managed (`--mode managed` / `continue`) |
|---|---|---|
| Launch | The harness starts `claude` attached to your console with `--session-id <uuid>`, so the transcript path is known in advance. | The harness runs `claude -p --output-format stream-json --verbose`. The message is sent on stdin and the session is continued with `--resume <uuid>`. |
| Primary usage source | Transcript `~/.claude/projects/<encoded cwd>/<session>.jsonl`. Its `cost-state` lines are cumulative per process; `assistant` lines are de-duplicated by `message.id`. | `result` events: `model_usage_since_last_result` gives the per-invocation delta, and `modelUsage` / `total_cost_usd` are cumulative. |
| Subagents | `<session>/subagents/agent-*.jsonl` (`isSidechain: true`) | `assistant` events with `parent_tool_use_id` set |
| Effort | The `effort` field on transcript assistant entries | Not in stream-json on 2.1.323. Taken from the transcript, which `-p` also writes. |
| Loaded plugins/skills | **Not in transcripts.** Use the static check plus `benchmark verify-activation`. | The `system`/`init` event, checked automatically |
| Permission prompts | Approvals are **not recorded** by Claude Code; rejections are (counted as "tool calls rejected"). | `--permission-prompts none`: anything not allowed is **denied and recorded** in `permission_denials`. Nothing is bypassed. |
| Raw evidence | A redacted copy of the transcript and subagent transcripts | stdout (stream-json) and stderr, captured separately, plus the exit code, timeout and cancellation flags |

## Accounting rules (implemented in `UsageAggregator`, tested in `TelemetryTests.cs`)

1. **One response, one count.** Claude Code writes one line per content block, and every line repeats the same `message.id` and `usage`. Usage is counted once per id.
2. **Disjoint categories.** `input_tokens` excludes cache reads and cache writes. Prompt size is therefore `input + cache_read + cache_creation`, computed once; cache tokens are never added twice.
3. **Cumulative results are never summed.**
   - `result.modelUsage` and `total_cost_usd` already include earlier `--resume` invocations of the same session, and they include subagents.
   - Per-invocation figures come from `model_usage_since_last_result` when it exists, otherwise from the difference between consecutive cumulative snapshots. The deltas are reconciled against the final snapshot.
   - Verified on CLI 2.1.323: 0.00157153 followed by 0.00292885 means 0.00292885 in total, not 0.0045.
4. **Separate sessions are summed.**
5. **Subagent usage** is reported separately as *already included*. It is never added again.
6. **Incomplete telemetry.** The run is marked incomplete when there is no `result` (crash, Ctrl+C, timeout) or no `cost-state`. Tokens then fall back to de-duplicated message sums, which can understate output tokens, and the CLI cost becomes "unavailable" rather than a guess.
7. **Models.** Every main-thread `message.model` is compared with the requested model. Any difference (fallback or switch) produces `MODEL_MISMATCH`, and the run is flagged as potentially incomparable. Models used by subagents are listed separately.
8. **Cost has two separately labelled sources:**
   - *Claude Code reported*: `total_cost_usd` / `cost-state.totalCostUSD`, which is the CLI's own list-price estimate.
   - *Model-equivalent estimate*: computed from `pricing/models.json`.

   Neither is subscription spend. The pricing table reproduced the CLI's Haiku 5.5 figure exactly (input, output, cache read, 1-hour cache write).

## Known limitations

- The transcript JSONL format is internal and undocumented, and may change between CLI versions. The parser ignores unknown lines and reports malformed ones.
- For interactive sessions the effective plugin and skill set is not observable after the fact. The optional probe (`verify-activation`) uses the run's exact flags. It overrides the model to Haiku and turns tools off, so it costs about $0.01.
- Whether transcript `cost-state` includes subagent usage was not independently confirmed. The importer compares it with main-only and main-plus-subagent message sums and records a warning when they disagree.
- Under `-p`, a SIGTERM or kill leaves no `result` for the in-progress turn, so that turn is reported as incomplete.
- OpenTelemetry (`CLAUDE_CODE_ENABLE_TELEMETRY`) can also emit per-request `api_request` events, but it needs a collector to write them to a file. It is not used by the MVP.
- Agent execution time is defined as follows:
  - Managed mode: the sum of `claude -p` process durations.
  - Interactive mode: the transcript's API time plus tool time, which excludes time spent waiting for the human.

  Wall-clock time spans the first launch to the last exit. Human time is the sum of agent-idle gaps before each human input in the transcript (capped; see operator-runbook.md). Optional manual timers are reported separately.
