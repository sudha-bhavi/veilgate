# VeilGate

A multi-tenant gateway that finds protected health information (PHI) in text, replaces it according to a policy,
sends only the sanitized text to an LLM, and restores values in the answer. All data in this project is synthetic.
Not certified for real PHI.

Design documents (PRS, UI, Backend, Database, Security, Pen test plan, Test cases, Deployment) describe everything below.

## Status

| Milestone | Scope                                                                           | State   |
| --------- | ------------------------------------------------------------------------------- | ------- |
| M0        | Solution skeleton, Docker Compose, Postgres roles, health endpoint, first tests | ✅ Done |
| M1        | Tenants, users, EF migrations with RLS, login + MFA + JWT + refresh             | ✅ Done |
| M2        | Detection engine, policy engine, tokenizer + vault                              | ✅ Done |
| M3        | Gateway endpoints, LLM provider with Polly, audit hash chain                    | ✅ Done |
| M4        | Policy workflow, API clients, LLM providers, evaluations                        | in progress: all but evaluations done |
| M5        | React console                                                                   |         |
| M6        | Kafka outbox, workers, dashboards                                               |         |
| M7        | Hardening, pen test, deployment                                                 |         |

## Prerequisites

.NET 10 SDK, Docker Desktop, Git, Node 22 (later, for the frontend).

## First run

```bash
./setup.sh                      # Windows: .\setup.ps1
cp .env.example .env            # Windows: copy .env.example .env  (then edit the passwords)
docker compose -f deploy/docker-compose.yml --env-file .env up -d
dotnet build
dotnet test
dotnet run --project src/VeilGate.Api
# in another terminal:
curl http://localhost:5000/health/live      # port is printed in the console
curl http://localhost:5000/api/v1/system/info
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
tests/                       xUnit tests
deploy/                      Docker Compose, Dockerfiles, Postgres init, WireMock
```

## M1a: database schema and row-level security

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

## M1b: login, Argon2id, JWT (ES256), JWKS

New project `tests/VeilGate.TestSupport` (shared throwaway-Postgres helper). Register it, restore and test:

```bash
./setup.sh                      # recreates VeilGate.slnx with the new project
dotnet build
dotnet test                     # first run downloads a NuGet package or two
```

No new migration is needed. To try it by hand, create a user in the dev database (see `docs/` for the seed script when it lands), then:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/VeilGate.Api
curl -s http://localhost:5000/.well-known/jwks.json
curl -s -X POST http://localhost:5000/api/v1/auth/login -H 'Content-Type: application/json' -d '{"email":"a@b.test","password":"x"}'
```

In Development the API creates a temporary signing key at startup. In any other environment you must set `Auth__Keys__0__Kid` and `Auth__Keys__0__PrivateKeyPem`
(generate with `openssl ecparam -name prime256v1 -genkey -noout`).

## M2: detection, policy, transformation, vault

Pure logic first, then the encrypted vault. No HTTP endpoints yet (those are M3).

```
text -> TextNormalizer -> DetectionPipeline (RegexDetector, DictionaryDetector) -> PolicyEngine (PolicyParser reads the YAML)
     -> Transformer (MASK / REDACT / TOKENIZE / SHIFT) -> OutboundLeakGate -> [LLM] -> Rehydrator (tokens back to originals)
```

- Vault: values are encrypted with AES-256-GCM under a per-tenant data key; the data key is stored wrapped by a master key.
- New migration `AddVault` (tables `tenant_keys`, `vault_sessions`, `vault_tokens`, all with row-level security). Apply it with `dotnet ef database update --project src/VeilGate.Infrastructure --startup-project src/VeilGate.Api`.
- New setting `Security:VaultKek` (32 random bytes, base64). `appsettings.Development.json` has a development-only value. In any other environment set it yourself: `openssl rand -base64 32` (production will use Key Vault).
- Token format is `[[TYPE_XXXX]]` (underscore), as in Backend doc 6.2 and Database doc 3.4.

## M3: the gateway

```
POST /api/v1/gateway/detect            scope gateway:detect       find entities, positions only
POST /api/v1/gateway/deidentify        scope gateway:deidentify   safe text + vault session
POST /api/v1/gateway/reidentify        scope gateway:reidentify   put the originals back
POST /api/v1/gateway/chat/completions  scope gateway:invoke       OpenAI-compatible: protect, forward, check, restore
POST /api/v1/playground/run            signed-in Developer / Security Officer / Tenant Admin
```

- **Tokens.** Gateway endpoints take a *client* token (claims `client_id`, `tid`, `scope`); a person's token is refused there, and a client token is refused on every console endpoint.
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

## M4: stored policies, API clients, LLM providers

```
/api/v1/policies ...             draft (If-Match), validate, submit, publish, reject, rollback, archive, versions, diff, test
/oauth/token                     client credentials: client id + secret -> 15-minute gateway token
/api/v1/api-clients ...          create (secret shown once), update, rotate-secret (grace period), revoke
/api/v1/llm-providers ...        connect, update, order (failover), test, delete        Tenant Admin only
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

Not yet in M4: evaluations (datasets, runs, reports), and `/auth/step-up` (so the 5-minute second-factor check cannot yet be renewed without signing in again).
