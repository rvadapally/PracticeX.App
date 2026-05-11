#!/usr/bin/env bash
#
# Preflight check for the Codex self-hosted runner.
#
# Verifies that this Mac has the local tooling needed for PracticeX issue work
# before spending an agent run.
#
# Run from repo root: `./scripts/codex-preflight.sh`

set -euo pipefail

red()    { printf '\033[31m%s\033[0m\n' "$*"; }
green()  { printf '\033[32m%s\033[0m\n' "$*"; }
yellow() { printf '\033[33m%s\033[0m\n' "$*"; }

fail() {
    red "x $*"
    exit 1
}

ok() {
    green "ok $*"
}

echo "-- Codex agent preflight --"

for bin in codex git gh jq dotnet node npm; do
    command -v "$bin" >/dev/null 2>&1 || fail "missing CLI: $bin"
done
ok "all required CLIs present"

DOTNET_VERSION=$(dotnet --version 2>/dev/null || true)
[ -n "$DOTNET_VERSION" ] || fail "dotnet is not usable"
ok "dotnet $DOTNET_VERSION"

NODE_VERSION=$(node --version 2>/dev/null || true)
[ -n "$NODE_VERSION" ] || fail "node is not usable"
case "$NODE_VERSION" in
    v2[4-9]*|v[3-9][0-9]*) ok "node $NODE_VERSION" ;;
    *) fail "Node.js >=24 is required; found $NODE_VERSION" ;;
esac

NPM_VERSION=$(npm --version 2>/dev/null || true)
[ -n "$NPM_VERSION" ] || fail "npm is not usable"
case "$NPM_VERSION" in
    1[1-9]*|[2-9][0-9]*) ok "npm $NPM_VERSION" ;;
    *) fail "npm >=11 is required; found $NPM_VERSION" ;;
esac

if [ -n "${GH_TOKEN:-}" ] || [ -n "${GITHUB_TOKEN:-}" ]; then
    ok "gh authenticated via env token"
elif gh auth status -h github.com >/dev/null 2>&1; then
    ok "gh CLI authenticated for github.com"
else
    fail "gh CLI not authenticated for github.com"
fi

if command -v psql >/dev/null 2>&1; then
    if pg_isready -h localhost -p 5432 >/dev/null 2>&1; then
        ok "local PostgreSQL is reachable on localhost:5432"
    else
        yellow "local PostgreSQL is not reachable on localhost:5432; DB-backed tasks may need setup"
    fi
else
    yellow "psql not found; DB-backed tasks may need PostgreSQL client tooling"
fi

green "-- preflight passed --"
