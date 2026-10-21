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
