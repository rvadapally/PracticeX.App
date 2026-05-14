# PracticeX E2E Auth

Verifying behavior on `https://app.practicex.ai` requires two layers of auth:

1. **Cloudflare Access** — gates the entire staging environment
2. **App auth** — the actual user session inside the app

Two operational modes for handling this in tests.

## Mode A — Test-auth endpoint (recommended)

**Status:** ⚠️ Not yet implemented in the PracticeX backend.

Adds a backend endpoint `POST /api/auth/test-agent-login` (gated by `ENABLE_TEST_AGENTS=1`) that mints a session cookie for a named fixture user. Tests call `loginAs(page, 'demo-doctor')` and get a working session in one request.

**To enable:**
1. Backend PR: add `apps/api/src/auth/test-agent-login.ts` (or equivalent) wrapped in `process.env.ENABLE_TEST_AGENTS === '1'`.
2. Seed fixture users (`demo-doctor@practicex.ai`, `demo-admin@practicex.ai`, `demo-staff@practicex.ai`) in the staging Postgres.
3. Set `ENABLE_TEST_AGENTS=1` on the staging App Service slot only — never production.
4. Verifier sub-agent uses `loginAs()` from `helpers/login.ts` and we're done.

This is the cross-repo pattern: Pulse already uses this (`samantha`, `stephen`, `drbrittany` via `/api/auth/test-agent-login`).

## Mode B — Saved storageState (fallback, no backend changes)

**Status:** ✅ Works today; requires manual refresh.

Playwright's `storageState` captures a serialized browser session (cookies, localStorage) to a JSON file. Tests load it and start "already logged in."

**To use:**

1. Run codegen once a week to capture a fresh session:
   ```bash
   npx playwright codegen \
     --save-storage=tests/e2e/auth-storage.json \
     https://app.practicex.ai
   ```
   Walk through the Cloudflare Access login, then the app login. Close the browser. `auth-storage.json` now has both cookies.

2. Run verification specs with the saved state:
   ```bash
   PW_STORAGE_STATE=tests/e2e/auth-storage.json \
     BASE_URL=https://app.practicex.ai \
     npx playwright test \
     --config=tests/e2e/playwright.config.ts
   ```

3. **Gitignore `auth-storage.json`.** It contains a live session cookie; committing it is a security incident. Add to `.gitignore` if not already.

**Refresh cadence:** Cloudflare Access tokens expire every 24h by default; the app session may last longer. If specs start failing with redirect-to-login, refresh.

## Mode C — Unauthenticated routes only (very limited)

Some behavior is observable without auth (landing page, public marketing pages, health endpoints). For Pacts that only need this scope, the verifier can skip auth entirely. Document in `.pact/<task-id>/pact.md` under `## Assumptions`: "Verification scope is limited to unauthenticated routes."

## What the verifier sub-agent does

`.claude/agents/delivery-verifier.md` checks for `PW_STORAGE_STATE` env var:
- Set → Mode B (storageState already loaded by config)
- Not set + test-auth endpoint responds → Mode A (`loginAs(...)`)
- Neither works → fail with `claude-blocked` and a comment explaining auth setup

## Setup checklist (one-time)

Before the verifier can do anything useful in PracticeX:

- [ ] Run `npm i -D @playwright/test` at the workspace root
- [ ] Run `npx playwright install --with-deps chromium`
- [ ] Choose Mode A or B (or commit to "Mode C only" for now)
- [ ] If Mode B: run codegen to capture `auth-storage.json`, gitignore it
- [ ] If Mode A: open backend PR for the test-auth endpoint
- [ ] Smoke test: `BASE_URL=https://app.practicex.ai npx playwright test tests/e2e/`
