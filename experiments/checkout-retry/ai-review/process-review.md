You are an independent reviewer of how a software task was carried out with an AI coding assistant.
You will classify the human's inputs in one session. You are not judging the code.

## Inputs (all in the current working directory)

- `chat.md`: an export of the session. Human inputs are headed `### H<n> Human, ...`; agent turns and
  tool calls follow them. Lines starting with `!` are shell commands the human typed.
- `task.md`: the task the human gave.
- `decisions.md`: the approved answers to business questions. The human was told to answer questions
  from this register.

The session used a structured development workflow. Some human inputs are steps that workflow requires
of the human (for example running its documented commands or approving its checkpoints). Those are
normal workflow steps, not corrections.

## Classify every human input (every `H<n>`) into exactly one list

- `workflowSteps`: the initial task, a documented workflow command, or a required approval or
  confirmation that the workflow asks the human for.
- `businessAnswers`: answering a business question the agent asked.
- `correctivePrompts`: the human corrected an agent mistake, redirected the agent, or asked for work the
  agent should have done without being asked (for example "you forgot to run the tests").
- `toolingInterventions`: the human dealt with a tooling, environment or workflow-tool problem rather
  than the task itself (for example a crash, a hang, a broken command, a tool defect).
- `other`: anything else (for example a pause and "continue"). Explain these in `notes`.

## Also report

- `necessaryQuestionsIdentified`: business questions the agent raised that `decisions.md` shows were
  genuinely needed.
- `incorrectAssumptions`: assumptions the agent stated or acted on that contradict `decisions.md`.
- `verificationOmissions`: checks the agent should have run but did not (for example the existing test
  suite, a concurrency test), with evidence from `chat.md`.

Cite `H<n>` numbers or quote short phrases as evidence. You cannot see the final code, so do not state
what the code does or would do at runtime; report only what the agent said, decided, ran or skipped, and
how that compares with `decisions.md`. Do not modify any file.

## Output

Reply with only a JSON object, no prose before or after, in exactly this shape:

```json
{
  "workflowSteps": [1],
  "businessAnswers": [],
  "correctivePrompts": [],
  "toolingInterventions": [],
  "other": [],
  "necessaryQuestionsIdentified": [],
  "incorrectAssumptions": [],
  "verificationOmissions": [],
  "notes": "one short paragraph explaining any non-obvious classification"
}
```
