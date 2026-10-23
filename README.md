# VeilGate

A multi-tenant gateway that finds protected health information (PHI) in text, replaces it according to a policy,
sends only the sanitized text to an LLM, and restores values in the answer. All data in this project is synthetic.
Not certified for real PHI.

Design documents (PRS, UI, Backend, Database, Security, Pen test plan, Test cases, Deployment) describe everything below.

## Status

| Milestone | Scope | State |
| --- | --- | --- |
| M0 | Solution skeleton, Docker Compose, Postgres roles, health endpoint, first tests | **This commit** |
| M1 | Tenants, users, EF migrations with RLS, login + MFA + JWT + refresh | next |
| M2 | Detection engine, policy engine, tokenizer + vault | |
| M3 | Gateway endpoints, LLM provider with Polly, audit hash chain | |
| M4 | Policy workflow, API clients, evaluations | |
| M5 | React console | |
| M6 | Kafka outbox, workers, dashboards | |
| M7 | Hardening, pen test, deployment | |

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
