# Pact Schema (PracticeX)

The Pact is the delivery contract for a `/synexar-task` invocation. It is written by the `pact-writer` sub-agent at the start of every Tier 2/3/H task and read by the `delivery-verifier` sub-agent (or the `claude-verify-staging.yml` CI verifier) after staging deploy.

Format matches the cross-repo standard — same Pact must be readable by both implementer flows (Claude via `claude-claim.yml`, Codex via `synexar-task-codex.sh`).

## Directory layout

```
.pact/<task-id>/
├── input.json         # invocation metadata
├── pact.md            # the acceptance brief (committed, commit #1)
├── state.json         # phase progression
├── build-summary.md   # post-Build
├── verify.md          # post-Verify
├── proof/             # screenshots tracked; videos gitignored
├── agent-runs/        # gitignored
└── context/           # gitignored
```

## pact.md template

```markdown
# Pact: <task-id>

## User intent
<2–4 sentences in the user's voice>

## Grounded context from the current codebase
<file paths, endpoints, services the implementer should know exist — for
PracticeX: typically apps/command-center/src/**, apps/api/src/**, packages/*>

## What good looks like in staging
<observable behaviors on https://app.practicex.ai>

## Acceptance checks
1. <numbered, falsifiable>
2. ...

## Verification driver
<one of: playwright | curl | manual>
<!-- Note: PracticeX has no native/mobile target; iOS-specific drivers don't apply. -->

## Staging target
https://app.practicex.ai

## Proof required
<screenshots / API transcripts. PracticeX-specific note: staging is Cloudflare-
gated, so the verifier may need a one-time Cloudflare Access token in addition
to app auth.>

## Assumptions
<state "None" if none>

## Blockers
<state "None at Pact time." if none>
```

## state.json shape

```json
{
  "taskId": "issue-123",
  "phase": "pact | build | verify",
  "status": "ready | needs_human | running | passed | failed",
  "needsHuman": false,
  "confidence": 0.95,
  "verificationDriver": "playwright | curl | manual",
  "stagingTarget": "https://app.practicex.ai"
}
```

## Confidence rubric

- `confidence >= 0.85` + `status: ready` → HIGH → label `pact-ready`
- `confidence` in `[0.6, 0.85)` + `status: ready` → MEDIUM → label `pact-ready` with inferences in Assumptions
- `confidence < 0.6` OR `needsHuman: true` → LOW → label `pact-needs-human`

## Terminal success

- **Claude flow** → `claude-shipped` (set by claude-verify-staging.yml or local delivery-verifier)
- **Codex flow** → `codex-staging-verified` (set by synexar-task-codex.sh)

## PracticeX-specific notes

- **No test-auth endpoint yet.** Verifier auth options:
  1. Cloudflare Access token (manual, expires every 24h)
  2. Playwright `storageState` (cookie jar from a manual login, refreshed weekly)
  3. Unauthenticated routes only (limited verification scope)

  Document which option the Pact assumes under `## Assumptions`.

- **Single staging environment** at `https://app.practicex.ai`. No per-PR preview deploys (yet). Verifier always hits the shared staging URL.

- **Stack note**: React 19 + Vite + TypeScript frontend (`apps/command-center/`), ASP.NET Core 9 backend (`apps/api/`). Build command: `npm run build` in each workspace.
