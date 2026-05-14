import type { Page } from '@playwright/test';

/**
 * Two auth modes are supported. The mode is chosen by which env vars are set.
 * See ../AUTH.md for the operational details and refresh procedures.
 *
 * Mode A — test-auth endpoint (recommended, requires backend support):
 *   ENABLE_TEST_AGENTS=1 on the backend; POST /api/auth/test-agent-login
 *   accepts { username } and mints a session cookie. Used by `loginAs(page, username)`.
 *
 * Mode B — storageState (fallback, no backend changes):
 *   Run `npm run pw:save-state` once to capture a manual login as JSON, then
 *   pass it via the Playwright `storageState` config option. `loginAs` no-ops
 *   in this mode (auth is already in the cookie jar).
 */

export type TestUser = 'demo-doctor' | 'demo-admin' | 'demo-staff';

export async function loginAs(page: Page, user: TestUser): Promise<void> {
  if (process.env.PW_STORAGE_STATE) {
    // Mode B: auth already in cookie jar via storageState
    return;
  }

  // Mode A: test-auth endpoint
  const baseURL = process.env.BASE_URL || 'http://localhost:5173';
  const response = await page.request.post(`${baseURL}/api/auth/test-agent-login`, {
    data: { username: user },
  });

  if (!response.ok()) {
    throw new Error(
      `loginAs(${user}) failed: ${response.status()} ${response.statusText()}. ` +
      `Either set ENABLE_TEST_AGENTS=1 on the backend (Mode A) or set ` +
      `PW_STORAGE_STATE=tests/e2e/auth-storage.json after running pw:save-state (Mode B). ` +
      `See tests/e2e/AUTH.md.`
    );
  }
}

/**
 * One-shot helper to save authenticated state for Mode B usage. Run via:
 *   npx playwright codegen --save-storage=tests/e2e/auth-storage.json https://app.practicex.ai
 * Then commit nothing; auth-storage.json is gitignored (contains session cookie).
 */
