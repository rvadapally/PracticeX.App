# Cloud-host the API for the demo (no laptop required)

End state after this runbook: `https://api.practicex.ai` is served by a
container running on a managed cloud host (Render free tier by default,
Fly.io as an alternative), backed by managed Postgres. The Cloudflare
Pages frontend at `app.practicex.ai` keeps working unchanged because the
Pages Function proxies same-origin `/api/*` traffic to whatever
`api.practicex.ai` resolves to.

The API self-bootstraps schema on first boot, so you do **not** need to
run psql against the cloud DB. The demo tenant is seeded automatically on
fresh databases.

---

## What is missing vs. the laptop setup

The laptop database holds the 18 Eagle GI documents and their analysis
artifacts. A fresh cloud database is empty — it will show the demo
tenant, an empty portfolio, and zero documents. Two paths:

- **Demo with empty data**: skip the data-restore section. The portfolio
  will say "No documents yet". You can show the connector UI, source
  configuration, and the brief shell. Better than a 502.
- **Demo with real data**: pg_dump the laptop DB once you are home, then
  pg_restore into the cloud Postgres. See "Restore the Eagle GI data"
  below.

If you are on the road and cannot reach the laptop, take path A and
acknowledge the empty state up front during the demo.

---

## Path 1 — Render.com (recommended, ~10 minutes from a phone)

Render has a phone-friendly dashboard, free Postgres for 90 days, and
Blueprint deploys that read `render.yaml` from the repo.

1. Open https://dashboard.render.com on your phone.
2. **New +** -> **Blueprint**.
3. Connect the GitHub repo `rvadapally-synexar/PracticeX.App`.
4. Pick the branch this PR landed on (or `main` once merged).
5. Render reads `render.yaml`, shows a preview of one Postgres + one Web
   Service. Click **Apply**.
6. Wait ~5 minutes for the first build. The build runs `Dockerfile` from
   the repo root; the API image embeds all SQL migrations.
7. When the web service goes green, copy its public URL. It looks like
   `https://practicex-api-xxxx.onrender.com`.
8. Verify schema and seed:
   ```
   curl https://practicex-api-xxxx.onrender.com/api/system/info
   ```
   You should see `{"product":"PracticeX Command Center", ...}`.

### Repoint `api.practicex.ai` at the Render host

Cloudflare DNS, on the phone:

1. Cloudflare dashboard -> `practicex.ai` zone -> **DNS** -> **Records**.
2. Find the existing `api` CNAME (currently points at the
   `<tunnel-uuid>.cfargotunnel.com` host) and **edit** it:
   - Type: `CNAME`
   - Name: `api`
   - Target: `practicex-api-xxxx.onrender.com`
   - Proxy status: **Proxied** (orange cloud).
3. Save. Cloudflare propagates within ~1 minute.
4. Same for `practicex.net` if you also use that hostname.

### Re-bless Cloudflare Access for the API

The existing Access app on `api.practicex.ai` already trusts the service
token used by the Pages Function. **Do not change the policy** — the
service-token credentials in the Pages Function (`CF_ACCESS_CLIENT_ID` /
`CF_ACCESS_CLIENT_SECRET`) keep working with whatever origin lives
behind the hostname.

If you ever rotated those secrets, set them on the Render service too
under **Environment** so the API can identify the proxy if you ever add
mTLS later. They are not required for the current path.

### Smoke test from the phone

```
curl -I https://app.practicex.ai/api/system/info
```

You should see `HTTP/2 302` (Cloudflare Access redirect for an
unauthenticated curl). In a browser, log in via the email-OTP gate and
the portfolio should load.

### Stop the laptop tunnel

Once Cloudflare DNS is repointed and the smoke test passes, you can stop
the laptop `cloudflared` and Windows API service. The cloud host now
owns `api.practicex.ai`.

---

## Path 2 — Fly.io fallback

Use this only if Render is unavailable. Requires `flyctl` on a laptop;
not phone-friendly.

```
flyctl auth login
flyctl launch --no-deploy --copy-config --name practicex-api
flyctl postgres create --name practicex-db --region iad
flyctl postgres attach --app practicex-api practicex-db
flyctl deploy
```

Then repoint `api.practicex.ai` at `practicex-api.fly.dev` in Cloudflare
DNS exactly as above.

---

## Path 3 — Pre-built image from GHCR

The `.github/workflows/publish-api-image.yml` workflow publishes
`ghcr.io/rvadapally-synexar/practicex-api:latest` on every push to
`main`. Any container host (Azure Container Apps, AWS App Runner,
DigitalOcean App Platform, GCP Cloud Run) can pull this image. Required
runtime config:

- `ConnectionStrings__PracticeX` — Npgsql keyword string. If your host
  injects `DATABASE_URL` instead, the entrypoint translates it.
- `PORT` — host injects this; the API binds to `0.0.0.0:$PORT`.
- `Seeding__DemoTenant=true` — keeps the demo tenant resolvable on a
  fresh DB.

---

## Restore the Eagle GI data when you get home

```
# On the laptop:
pg_dump --format=custom --no-owner --no-privileges \
  -h localhost -p 5436 -U postgres -d practicex \
  -f practicex-eagle-gi.dump

# Upload to the cloud host:
psql "<render-or-fly-postgres-url>" -c "DROP SCHEMA IF EXISTS doc CASCADE;"
# repeat for: contract, evidence, rate, workflow, audit, org
pg_restore --no-owner --no-privileges --clean --if-exists \
  -d "<render-or-fly-postgres-url>" practicex-eagle-gi.dump
```

The schema layout is preserved exactly because every migration script is
idempotent and re-applied on next API restart.

---

## Roll back to the laptop tunnel

If the cloud host misbehaves during the demo:

1. Cloudflare DNS -> edit `api` CNAME back to the
   `<tunnel-uuid>.cfargotunnel.com` target you replaced.
2. Restart `cloudflared` and the Windows API service on the laptop.
3. Pause the Render service (or `flyctl scale count 0 -a practicex-api`)
   to avoid billing surprises.

DNS propagates in ~1 minute. The Pages frontend never changes.

---

## What this PR ships

- `Dockerfile` + `.dockerignore` — multi-stage build of the API.
- `scripts/docker-entrypoint.sh` — translates `DATABASE_URL` to Npgsql
  form so any managed Postgres host works without per-host config.
- `render.yaml` — Render Blueprint (web service + free Postgres).
- `fly.toml` — Fly.io alternative.
- `.github/workflows/publish-api-image.yml` — auto-publishes the image
  to GHCR on every push.
- `StartupMigrationRunner` + embedded SQL migrations — fresh cloud DB
  self-bootstraps schema on first boot, no manual psql step.
- `Program.cs` honours `$PORT`, skips HTTPS redirect behind a
  TLS-terminating proxy, and seeds the demo tenant in non-Development
  environments when `Seeding:DemoTenant=true`.
