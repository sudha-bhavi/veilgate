# VeilGate

A multi-tenant gateway that finds protected health information (PHI) in text, replaces it according to a policy,
sends only the sanitized text to an LLM, and restores the values in the answer.

> **All data in this project is synthetic. VeilGate is not certified for real PHI.**

```
your app ──► VeilGate ──► LLM provider
              │
              ├─ finds PHI: names, dates, ID numbers, phone numbers ...
              ├─ replaces it using a policy: redact, mask, tokenize, or shift dates
              ├─ checks the model's answer for anything that leaked
              └─ puts the original values back in the answer
```

Design documents (PRS, UI, Backend, Database, Security, Pen test plan, Test cases, Deployment) describe everything below.

## Highlights

- **Policies as YAML**, with a real approval workflow: draft, validate, submit, approve by a _different_ person, publish.
  Every version is kept, can be compared (diff), and can be rolled back.
- **OpenAI-compatible endpoint.** Existing clients only change their base URL. API clients have scoped credentials,
  rate limits, network allowlists, and secret rotation with a grace period.
- **Tenant isolation in the database.** PostgreSQL row-level security on every tenant table.
- **Reversible protection.** Values are encrypted with AES-256-GCM under a per-tenant data key and swapped for tokens,
  so the answer can be restored.
- **Careful sign-in.** Argon2id passwords, ES256 tokens with a public key set, authenticator-app MFA with recovery codes,
  and a fresh second-factor check ("step-up") before sensitive actions such as publishing a policy or creating a client.
- **Tamper-evident audit.** An append-only, hash-chained log of counts and ids, never text.
- **Measured quality.** Evaluations run a policy over synthetic notes with known answers and report recall, precision,
  F1 and leaks per type, and can compare two runs.
- **People and invitations.** Tenant Admins invite colleagues by email (a single-use link that expires after 48 hours),
  choose their role, disable accounts, and reset someone's two-step sign-in.
- **My sessions.** Every signed-in person, whatever their role, can open **Security** in the console to see where they
  are signed in (device, IP address, when, last active), end a session they do not recognise, or sign out all other
  sessions. The IP address and browser are recorded when a session starts.
- **Your own account.** The **MFA** tab shows your authenticator app and how many recovery codes are left, and replaces
  them (after a fresh second-factor check). The **Password** tab changes your password and signs out your other devices.
- **Security events.** The **Security events** tab lists failed sign-in lockouts, two-step resets, blocked leaks and API
  client problems. Tenant Admins and Security Officers see the whole organisation and can filter by person; everyone
  else sees only events about their own account.
- **A read-only demo.** An optional "Try the demo" button on the sign-in page lets a visitor look around with sample
  data, without an account. It is off by default and cannot change anything (see "Try the demo" below).
- **A React console** for all of it: playground, policies with a YAML editor, API clients, providers, evaluations,
  the audit log and users.

## Tech stack

| Area     | What                                                                                          |
| -------- | --------------------------------------------------------------------------------------------- |
| Backend  | .NET 10, ASP.NET Core, FluentValidation, Polly (retries and circuit breaker for LLM calls)    |
| Data     | PostgreSQL 16 with row-level security, EF Core migrations                                     |
| Frontend | React 19, TypeScript, Vite, Tailwind CSS 4, TanStack Query, React Router, Monaco editor       |
| Testing  | xUnit (backend), Vitest and Testing Library (frontend), throwaway Postgres for database tests |
| Local    | Docker Compose: Postgres, Redis, Redpanda (Kafka), MailHog, WireMock                          |

## Status

| Milestone | Scope                                                                                               | State   |
| --------- | --------------------------------------------------------------------------------------------------- | ------- |
| M0        | Solution skeleton, Docker Compose, Postgres roles, health endpoint, first tests                     | ✅ Done |
| M1        | Tenants, users, EF migrations with RLS, login + MFA + JWT + refresh                                 | ✅ Done |
| M2        | Detection engine, policy engine, tokenizer + vault                                                  | ✅ Done |
| M3        | Gateway endpoints, LLM provider with Polly, audit hash chain                                        | ✅ Done |
| M4        | Policy workflow, API clients, LLM providers, evaluations, step-up check                             | ✅ Done |
| M5        | React console: sign-in, playground, policies, API clients, providers, evaluations, audit log, users | ✅ Done |
| M5+       | Security page, read-only demo, invitations                                                          | ✅ Done |
| M6        | Kafka outbox, workers, dashboards                                                                   | 🚧 In progress |
| M7        | Hardening, pen test, deployment                                                                     |         |

In the console, Dashboard, Requests and Settings are marked "Not built yet": their server side is not written.
Everything else works end to end, including the Security page (sessions, two-step sign-in, password and security
events). Passkeys are not built yet.

## Run it locally

You need the .NET 10 SDK, Docker Desktop, Git and Node 22 or newer.

**1. Set up the database and tools**

```bash
./setup.sh                      # Windows: .\setup.ps1
cp .env.example .env            # Windows: copy .env.example .env  (then edit the passwords inside)
docker compose -f deploy/docker-compose.yml --env-file .env up -d
dotnet tool restore
dotnet ef database update --project src/VeilGate.Infrastructure --startup-project src/VeilGate.Api
```

**2. Start the API** (on port 5080, because macOS uses port 5000 for AirPlay)

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 dotnet run --project src/VeilGate.Api
curl http://localhost:5080/health/live          # in another terminal
```

**3. Start the worker** (it runs evaluations)

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project src/VeilGate.Workers
```

**4. Create the demo organisation and people.** Use the `vg_migrator` password from your `.env`, and choose your own
password of at least 12 characters for the demo people:

```bash
dotnet run --project tools/VeilGate.Seeder -- --connection "Host=localhost;Database=veilgate;Username=vg_migrator;Password=<PG_MIGRATOR_PASSWORD from .env>" --password "<a password you choose>"
```

This creates the tenant "Acme Health" and one person per role: `admin@acme.test` (Tenant Admin),
`security@acme.test` (Security Officer), `dev@acme.test` (Developer) and `auditor@acme.test` (Auditor).
The seeder only works on a database on your own machine.

It also creates some sample content in that organisation, so the console has something to show: a published policy
("HIPAA Safe Harbor") and one waiting for approval ("Names only"), an API client (`ehr-bridge`, whose secret is printed
once in the terminal), a dataset of 200 invented notes, and a queued evaluation run that the worker finishes. Running
the seeder again changes nothing. It also creates `demo@acme.test`, a Security Officer with a random, discarded
password and no authenticator, who is only reachable through the optional demo button below.

**5. Start the console**

```bash
cd src/VeilGate.Web
npm install
npm run dev
```

Open <http://localhost:5173> in Chrome. The first sign-in asks you to set up an authenticator app and shows
one-time recovery codes. Keep them: they are shown only once.

When you add a model provider, enter its address without `/v1` (for the WireMock container: `http://localhost:8089`).
VeilGate adds `/v1/chat/completions` itself.

**My sessions**

Apply the migration that adds the `ip` and `user_agent` columns to sessions (`AddSessionIpUserAgent`), the same way as
the others:

```bash
dotnet ef database update --project src/VeilGate.Infrastructure --startup-project src/VeilGate.Api
```

Sessions that were already open before the migration have no IP address or browser, so they show as "Unknown device"
until the person signs in again. The API offers three calls to every signed-in person, and only ever for their own
sessions:

- `GET /api/v1/auth/sessions` lists the active ones, with the one the request is using marked `current`.
- `DELETE /api/v1/auth/sessions/{id}` ends another one. Ending the session in use is refused (`409 session.current`);
  that is a sign-out. Someone else's session is "not found".
- `POST /api/v1/auth/logout-all` with `{"keepCurrent": true}` (the default) signs out every other session. It needs a
  recent second-factor check, like the other sensitive actions.

An ended session stops working at once. The API looks up the session of every signed-in request, so after a sign-out, a
revoke, "sign out all other sessions", a password change, or disabling the person, that device's very next request is
refused with `401 auth.token_invalid`, and the console signs it out. A token whose session has no record is not refused
by this check, in the same way an unknown API client is not (a forged token already fails its signature). The idle
timeout is not checked per request: it only moves when a session is renewed, and an access token lasts
`Auth__AccessTokenMinutes` (10 by default) at most. A demo session can look at the list but cannot end anything.

**Two-step sign-in and password**

Three calls for every signed-in person, always about themselves. No migration is needed.

- `GET /api/v1/auth/mfa/factors` returns when the authenticator app was added (or `null`) and how many recovery codes are
  left of how many.
- `POST /api/v1/auth/mfa/recovery-codes/regenerate` replaces all recovery codes, used or not, and returns the 10 new ones
  once. It needs a recent second-factor check. The old codes stop working at once.
- `POST /api/v1/auth/password/change` with `{"currentPassword", "newPassword"}` returns `204`. A wrong current password is
  `400 password.current_incorrect` and counts toward the account lockout (5 in a row lock it for 15 minutes). The new
  password follows the same rules as at sign-up (12 to 128 characters) and must differ from the current one. On success
  the person's other sessions end and their token version goes up, so every other device is signed out at once; the
  device that made the change stays signed in and quietly renews its token.

Each of these writes an audit event and a security event (Info), so they appear on the Security events tab. The
authenticator app cannot be removed here, because it is the only second factor, and passkeys are not built yet. A demo
session can look but not change anything.

**Security events**

`GET /api/v1/security-events` is open to every person of the tenant, and what comes back depends on the role. Tenant
Admins and Security Officers get the whole organisation and may narrow it with `userId`, `severity`, `from` and `to`.
Everyone else always gets only events about their own account; asking for another person's is refused with
`403 security_events.own_only`. `GET /api/v1/security-events/people` lists the people for the console's User filter and
is for Tenant Admins and Security Officers only. Another organisation's events are never visible. No migration is needed.

Events about an API client (a wrong secret, a blocked address) have no person, so only the two oversight roles see them.
The table keeps no browser information for security events, so a row shows time, type, IP address and details.

**Try the demo (optional)**

The sign-in page can offer a "Try the demo" button that signs a visitor in as the seeded `demo@acme.test` (a Security
Officer), with no password and no authenticator. It is **off by default**. First apply the migration that adds the
`is_demo` column to sessions (`AddSessionDemoFlag`), then start the API with `Demo__Enabled=true`:

```bash
dotnet ef database update --project src/VeilGate.Infrastructure --startup-project src/VeilGate.Api
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 Demo__Enabled=true dotnet run --project src/VeilGate.Api
```

- **Read-only.** A demo session can look at every page a Security Officer can see, try the Playground, test a policy
  on sample text, and sign out. Every other change is refused with `403 demo.read_only`, before it reaches a controller.
  That includes the step-up check, so a demo session can never publish or roll back a policy.
- **Marked and short.** The session is flagged as a demo in the database and in its token (sign-in method `demo`), it
  lasts one hour, and refreshing it keeps it a demo session. Every demo sign-in is recorded in the audit log
  (`auth.demo_login`).
- **Never an administrator.** The API refuses to sign in a demo user who is a Tenant Admin or Platform Admin. The
  settings are `Demo__Enabled` (default `false`) and `Demo__UserEmail` (default `demo@acme.test`).

> **Warning.** This button skips the passwords and two-step sign-in that the rest of VeilGate insists on. Switch it on
> only for a sandbox organisation that holds synthetic data and nothing else. The go-live checklist in the deployment
> document (seed accounts removed, MFA enforced for everyone) still applies to a real deployment.

**Check the console under its production security policy**

In production the API serves the built console itself (same origin, which the `SameSite=Strict` refresh cookie
needs), with a Content-Security-Policy that allows only its own files: no inline scripts, no `eval`, and Monaco's
worker as a plain file. `npm run dev` does not apply that policy, because hot reload needs inline scripts. To see the
built console under it, with the API running:

```bash
cd src/VeilGate.Web
npm run build && npm run preview     # then open http://localhost:4173 and watch the browser console for CSP errors
```

The policy lives in `SecurityHeadersMiddleware.ConsolePolicy` and is copied into `vite.config.ts` for the preview.
The Docker image (`deploy/Dockerfile.api`) builds the console and puts it in the API's `wwwroot`.

**Run the tests**

```bash
dotnet test                                  # backend
cd src/VeilGate.Web && npm test              # frontend
```

If you change the passwords in `.env`, put the same `vg_app` password in
`src/VeilGate.Api/appsettings.Development.json` (or use user-secrets:
`dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Database=veilgate;Username=vg_app;Password=..." --project src/VeilGate.Api`).

If the database volume already existed before you changed passwords, remove it so the roles are recreated:
`docker compose -f deploy/docker-compose.yml down -v`.

## Layout

```
src/VeilGate.Domain          entities, Result<T>, no dependencies
src/VeilGate.Application     use cases, interfaces, validators
src/VeilGate.Infrastructure  EF Core, tenant interceptor, external services
src/VeilGate.Api             controllers, middleware, composition root
src/VeilGate.Workers         background services (outbox, purger, evaluations)
src/VeilGate.Web             the React console
tools/VeilGate.Seeder        creates the demo organisation and people (local databases only)
tests/                       xUnit tests
deploy/                      Docker Compose, Dockerfiles, Postgres init, WireMock
```

## Development notes by milestone

These are the build notes, kept as written. Their `curl` examples use port 5000, the .NET default; on macOS use 5080
as above.

### M1a: database schema and row-level security

```bash
dotnet tool restore                      # installs dotnet-ef from .config/dotnet-tools.json
dotnet sln VeilGate.slnx add tests/VeilGate.Infrastructure.Tests
docker compose -f deploy/docker-compose.yml --env-file .env down -v   # re-runs init.sh (adds vg_platform, vg_audit_writer)
docker compose -f deploy/docker-compose.yml --env-file .env up -d

dotnet ef migrations add InitialSchema --project src/VeilGate.Infrastructure --startup-project src/VeilGate.Api --output-dir Persistence/Migrations
```

Open the generated `Persistence/Migrations/<timestamp>_InitialSchema.cs` and add these lines at the **end** of `Up(...)`, before its closing brace:

```csharp
migrationBuilder.CreateTenantFunction();
migrationBuilder.ProtectTenantsTable();
migrationBuilder.ProtectTenantTable("users");
migrationBuilder.CreateLoginLookupFunction();
```

and `using VeilGate.Infrastructure.Persistence.Sql;` at the top. Then:

```bash
dotnet ef database update --project src/VeilGate.Infrastructure --startup-project src/VeilGate.Api
dotnet test
```

### M1b: login, Argon2id, JWT (ES256), JWKS

New project `tests/VeilGate.TestSupport` (shared throwaway-Postgres helper). Register it, restore and test:

```bash
./setup.sh                      # recreates VeilGate.slnx with the new project
dotnet build
dotnet test                     # first run downloads a NuGet package or two
```

No new migration is needed. To try it by hand, create a user in the dev database (the seeder above does this), then:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/VeilGate.Api
curl -s http://localhost:5000/.well-known/jwks.json
curl -s -X POST http://localhost:5000/api/v1/auth/login -H 'Content-Type: application/json' -d '{"email":"a@b.test","password":"x"}'
```

In Development the API creates a temporary signing key at startup. In any other environment you must set `Auth__Keys__0__Kid` and `Auth__Keys__0__PrivateKeyPem`
(generate with `openssl ecparam -name prime256v1 -genkey -noout`).

### M2: detection, policy, transformation, vault

Pure logic first, then the encrypted vault. No HTTP endpoints yet (those are M3).

```
text -> TextNormalizer -> DetectionPipeline (RegexDetector, DictionaryDetector) -> PolicyEngine (PolicyParser reads the YAML)
     -> Transformer (MASK / REDACT / TOKENIZE / SHIFT) -> OutboundLeakGate -> [LLM] -> Rehydrator (tokens back to originals)
```

- Vault: values are encrypted with AES-256-GCM under a per-tenant data key; the data key is stored wrapped by a master key.
- New migration `AddVault` (tables `tenant_keys`, `vault_sessions`, `vault_tokens`, all with row-level security). Apply it with `dotnet ef database update --project src/VeilGate.Infrastructure --startup-project src/VeilGate.Api`.
- New setting `Security:VaultKek` (32 random bytes, base64). `appsettings.Development.json` has a development-only value. In any other environment set it yourself: `openssl rand -base64 32` (production will use Key Vault).
- Token format is `[[TYPE_XXXX]]` (underscore), as in Backend doc 6.2 and Database doc 3.4.

### M3: the gateway

```
POST /api/v1/gateway/detect            scope gateway:detect       find entities, positions only
POST /api/v1/gateway/deidentify        scope gateway:deidentify   safe text + vault session
POST /api/v1/gateway/reidentify        scope gateway:reidentify   put the originals back
POST /api/v1/gateway/chat/completions  scope gateway:invoke       OpenAI-compatible: protect, forward, check, restore
POST /api/v1/playground/run            signed-in Developer / Security Officer / Tenant Admin
```

- **Tokens.** Gateway endpoints take a _client_ token (claims `client_id`, `tid`, `scope`); a person's token is refused there, and a client token is refused on every console endpoint.
  Client tokens are issued by `POST /oauth/token` for a stored API client (M4d). The playground works for a signed-in user.
- **Policy.** The policy named in the request, else the API client's default policy, else the built-in `hipaa-safe-harbor` template (`PolicyTemplates`). Stored policies arrived in M4a to M4c.
- **Model providers** are per tenant and live in the database since M4e (see below). Each provider gets its own timeouts, retries (2, with backoff and jitter), circuit breaker and concurrency limit, and the destination address is checked against private and metadata ranges on every connection (set `Llm:AllowPrivateNetworks` only in Development).
- **Audit.** Every gateway call appends a row to `audit_events`, a per-tenant hash chain (`HashChainAuditWriter`, verified by `AuditChainVerifier`). The table is append-only: the app role cannot UPDATE or DELETE, and a trigger refuses it for everyone.
  Rows hold counts and ids, never text. Blocked leaks are written to `security_events`.
- **Apply the new migrations** to your dev database (`AddAuditEvents`, `AddSecurityEvents`, plus `AddVault` from M2):

```bash
dotnet ef database update --project src/VeilGate.Infrastructure --startup-project src/VeilGate.Api
```

Not in M3: streaming responses, FHIR, idempotency keys, the `gateway_requests` / `detections` tables and their partitions, signed audit checkpoints, and the ONNX name model (a rule-based name detector stands in).

### M4: stored policies, API clients, LLM providers

```
/api/v1/policies ...             draft (If-Match), validate, submit, publish, reject, rollback, archive, versions, diff, test
/oauth/token                     client credentials: client id + secret -> 15-minute gateway token
/api/v1/api-clients ...          create (secret shown once), update, rotate-secret (grace period), revoke
/api/v1/llm-providers ...        connect, update, order (failover), test, delete        Tenant Admin only
/api/v1/auth/step-up             renew the 5-minute second-factor check without signing in again
```

- **Policy workflow.** The person who submits a policy cannot publish it (`policy.self_approval`). Publishing and rolling back need a second-factor check in the last 5 minutes; a stored policy that no longer matches its hash, or no longer parses, is refused rather than replaced.
- **API clients.** A secret is shown once and stored as an Argon2id hash; a rotation keeps the old secret working for 1, 24 or 168 hours. Revoking a client stops its tokens on the very next call. A client has its own rate limit, an optional network allowlist and an optional default policy.
- **LLM providers.** Each tenant connects its own providers (address, API key, models, timeout, failover order). The key is encrypted with the tenant's data key, bound to its provider, and never returned (only its last four characters). Changing a provider's address requires entering the key again. In Development, to use the WireMock container (a Tenant Admin's token in `$TOKEN`):

```bash
curl -s -X POST http://localhost:5000/api/v1/llm-providers -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"mock-llm","baseUrl":"http://localhost:8089","apiKey":"dev-not-a-secret","models":["gpt-4o-mini"]}'
```

- **The `Llm:Providers` configuration list is gone.** Remove any `Llm__Providers__*` environment variables; they are ignored. Only the shared settings (`AllowPrivateNetworks`, retry and breaker tuning) remain in `Llm`.
- **Apply the new migrations** to your dev database (`AddPolicies`, `AddApiClients`, `AddLlmProviders`):

```bash
dotnet ef database update --project src/VeilGate.Infrastructure --startup-project src/VeilGate.Api
```

### M4f: evaluations

```
/api/v1/datasets ...             create a synthetic dataset (invented notes with known answers), list, read
/api/v1/evaluations ...          start a run, list, read progress, leaks, report (json or md), compare two runs
```

- **A run measures a policy version on a dataset.** Every note goes through the same path as the gateway (detect, then the policy decides) and is scored against the labels: recall, precision and F1 per type, and **leaks** (a value the policy protects that was not fully covered). The gate passes only if recall reaches the threshold (default 98%) **and** nothing leaked.
- **Runs are queued.** `POST /evaluations` returns 202 and the worker does the work: `DOTNET_ENVIRONMENT=Development dotnet run --project src/VeilGate.Workers`. Until Kafka arrives in M6 the `eval_runs` table is the queue; a run abandoned by a crashed worker is taken again, up to 3 attempts.
- **Datasets are synthetic only.** The same seed always gives the same notes, so two policies can be compared on identical data. Compare only works for runs of the same dataset.
- **Modes.** `rules` uses the pattern and dictionary detectors; `rules+ner` adds the name detector (a rule-based stand-in for the ONNX model).
- **Apply the new migration** (`AddEvaluation`) to your dev database:

```bash
dotnet ef database update --project src/VeilGate.Infrastructure --startup-project src/VeilGate.Api
```

### M5: the console

The React console lives in `src/VeilGate.Web`. Its dev server proxies `/api` and `/oauth` to the API on port 5080
(`vite.config.ts`), so the browser only ever talks to one address and the HttpOnly refresh cookie behaves as it will in production.

- The access token is kept in memory only; a reload brings the session back through the refresh cookie.
- Menu items and pages follow the server's role rules, but the server checks every request itself.
- Idle sign-out after 15 minutes, with a warning one minute before.
- Dialogs trap keyboard focus, tabs work with the arrow keys, and there is a "skip to main content" link.

### M5: users and invitations

```
GET   /api/v1/users ...                    list, search, filter by role and status           Tenant Admin only
POST  /api/v1/users                        invite by email and role
PATCH /api/v1/users/{id}                   change role                                        needs the second-factor check
POST  /api/v1/users/{id}/disable           and /enable
POST  /api/v1/users/{id}/invite/resend     a fresh link; every older link stops working
POST  /api/v1/users/{id}/mfa/reset         remove their authenticator and recovery codes      needs the second-factor check
GET   /api/v1/auth/invitations?token=...   show an invitation                                 no sign-in
POST  /api/v1/users/accept-invite          choose a name and password                         no sign-in
```

- **The link.** An invitation works once and lasts 48 hours. Only a hash of its token is stored. A link that is unknown, used, expired or replaced always gets the same answer, so it tells a stranger nothing. The token travels in the query string, not the path, because the request log records the path and a token must never be logged.
- **After accepting,** the person chooses a name and password and goes straight on to set up two-step sign-in, exactly as on a first sign-in.
- **Rules.** Nobody can change, disable or reset their own account, the last active Tenant Admin is protected, and `PlatformAdmin` cannot be handed out. An email address can belong to only one account in the whole system, because sign-in finds a person by email alone.
- **Signing people out.** Disabling someone or resetting their two-step sign-in ends all their sessions. Changing a role makes their old tokens stop working. A reset also removes their recovery codes and raises a security event.
- **Email.** In Development the API sends to MailHog, which keeps every message in a local inbox at <http://localhost:8025> and sends nothing to the internet. The settings are `Smtp` and `Console:BaseUrl` in `appsettings.Development.json`; they have no defaults elsewhere, so a deployment that forgets them fails loudly instead of emailing links to `localhost`. If an email cannot be sent, the person is still invited and the admin can use "Resend invite".
- **Apply the new migration** (`AddInvitations`) to your dev database:

```bash
dotnet ef database update --project src/VeilGate.Infrastructure --startup-project src/VeilGate.Api
```

### M6: the outbox (in progress)

Gateway events will travel through Kafka (Redpanda locally) to a worker that fills the Dashboard and Requests pages. The first
step is the outbox: a table that holds each message until a worker has sent it.

- **Why an outbox.** A request's work and its message must not drift apart. `IOutbox.Enqueue` adds the message to the same
  database transaction as the work, so both are saved or neither is. A separate worker sends the message afterwards.
- **What a message holds.** A topic, a key (the tenant id, so one tenant's messages stay in order), a JSON payload and the headers
  `messageId`, `tenantId`, `schemaVersion` and, when there is one, `traceparent`. Like the audit log it never holds content.
- **Access.** The app role can read and add messages of its own tenant only. It cannot update or delete them; the publisher
  will do that through a narrow database function.

### Decisions

Where this project departs from the design documents (which were drafted with AI help and are not a fixed contract):

- **Transport.** The documents assume Kafka throughout. Here the code talks to an event transport interface, so Kafka can be
  swapped out without touching the gateway.
- **One topic first.** Only `vg.gateway.requests.v1` is allowed in the outbox for now. Audit and security events are not sent
  through Kafka yet; more topics get their own migration when they are used.
- **`last_error_code`.** `outbox_messages` has one extra column that the documents do not list. It holds only the kind of error
  from the last failed send, never message content.
- **Evaluations** keep using the `eval_runs` table as their queue. Moving them onto Kafka is optional and comes last.
