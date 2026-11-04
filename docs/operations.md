# Operations runbook

How to run VeilGate's production stack (`deploy/docker-compose.prod.yml`): first deployment, updates, rollback, backups, restore,
secret rotation, what to watch, and what to do when something breaks.

**Read this first.** All data in this project is synthetic and VeilGate is not certified for real PHI. **The penetration test is only
partly run** (see `docs/pentest/`), and everything runs on **one machine**, so there is no high availability: if the machine is down, the service is down. This runbook is for a demo or sandbox
deployment with synthetic data, not for a regulated production system.

## What runs

| Service | What it is | Reachable from outside |
|---|---|---|
| `caddy` | HTTPS front door, Let's Encrypt certificate | ports 80 and 443 |
| `api` | the API and the console | no (through Caddy only) |
| `workers` | outbox publisher, metrics, evaluations, housekeeping | no, and no internet access |
| `postgres` | the database | no |
| `kafka` | Redpanda, the event stream | no |
| `migrate`, `kafka-init` | one-shot jobs: apply migrations, create topics | no |

Every command below runs from the repository root. To keep them short, this is the compose command:

```bash
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env.prod <command>
```

## First deployment

1. **A machine** with Docker, a public IP address, and ports 80 and 443 open to the internet (nothing else needs to be).
2. **DNS:** point your domain's A record at the machine. Let's Encrypt needs this to issue the certificate.
3. **Secrets:** `deploy/scripts/generate-secrets.sh <domain> <email>` writes `deploy/.env.prod`.
4. **Mail:** open `deploy/.env.prod` and set `SMTP_HOST` (and `SMTP_USER`, `SMTP_PASSWORD` if your provider needs them). The API refuses to
   start until you do, because invitation links must reach people.
5. **Back up the secrets file** somewhere safe and separate from the machine. Losing `VAULT_KEK` or `MFA_ENCRYPTION_KEY` means data
   protected with them can never be read again.
6. **Start:** `up -d --build`. The first build takes several minutes.
7. **Check:** `ps -a` should show `migrate` and `kafka-init` as `Exited (0)` and the rest `running` or `healthy`. Then open
   `https://<domain>`.
8. **Create the first administrator.** A fresh database has no organisation and no users. This one-time command creates both and prints a
   single-use link for the person who will administer it (they choose a password and set up their authenticator when they open it):

```bash
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env.prod run --rm bootstrap --organisation "Your Organisation" --email you@example.com --name "Your Name"
```

   Send them the link it prints. It works once and expires after 48 hours, like every invitation. VeilGate keeps only a fingerprint of it, so it
   cannot be shown again. **If it is lost before it was used**, make a new one (the old one stops working):
   `... run --rm bootstrap --reissue`. The command refuses to run if any organisation already exists, so it can never be used later to add an
   administrator by the back door. Everyone else is invited from the console.
9. **Try it before trusting it:** run a backup and a restore drill (below), and run the penetration test (`docs/pentest/`).

To rehearse on your own computer first, use `CADDYFILE=./Caddyfile.local` and a made-up name such as `veilgate.test` with
`127.0.0.1 veilgate.test` in `/etc/hosts`. The browser will warn about the certificate, which is expected.

## Updating to a new version

1. **Back up first** (below). Migrations only go forward, so a backup is your way back.
2. Get the new code (`git pull`) and rebuild: `up -d --build`. Migrations run first, as a one-shot job, before the new API and workers start.
3. Check `ps -a` and `logs api --tail 40`. If the API refuses to start, its log lists every setting it objects to, by name.

## Rolling back

- **If the new version never became healthy:** `git checkout <the previous version>` and `up -d --build`. Nothing was lost.
- **If it ran and changed the database (a migration was applied):** the previous version may not understand the new schema. Restore the
  backup you took before updating, using the restore steps below, then start the previous version. Anything written since that backup is
  lost, which is why you take one first.

## Backups

```bash
deploy/scripts/backup.sh
```

It dumps the whole database (every tenant, the audit log, the vault) into `deploy/backups/veilgate-<time>.dump`, reads the file back to
prove it is usable, writes a checksum next to it, and deletes backups older than 14 days (`BACKUP_KEEP_DAYS=30` changes that).

- **Schedule it.** Example for a daily backup at 02:30 (`crontab -e`):
  `30 2 * * * cd /path/to/veilgate && deploy/scripts/backup.sh >> /var/log/veilgate-backup.log 2>&1`
- **Copy the files off the machine** (another server, object storage). A backup that lives only on the machine it protects is not a backup.
- **Back up `deploy/.env.prod` separately.** It is not in the dump, and the vault and MFA data are unreadable without it.
- **Check the age of the newest backup** as part of your daily look (see "What to watch").

## Restore drill

A restore you have never tried is a hope, not a plan. The drill restores the newest backup into a scratch database next to the live one,
checks it, and drops it again. It never touches the live database.

```bash
deploy/scripts/restore-drill.sh
```

A good result ends with `RESTORE DRILL PASSED`. It checks that the checksum matches, that the restore had no errors, that the same tables
exist, that **row-level security is enabled, forced and has its policies on every tenant table after the restore**, and that as the app
role with no tenant chosen the data is invisible. It also prints how long the restore took, which is your real recovery time at this size.
Run it before go-live and every quarter. Row counts that differ from the live database are reported as a note, not a failure: that is normal if
the system changed since the backup.

## Restoring for real

Use this if the database is lost or damaged. It replaces the live data with the backup, so everything written since is gone.

1. Stop the services that use the database: `stop api workers`.
2. Replace the database (this DELETES the live data; be sure):

```bash
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env.prod exec -T postgres psql -U postgres -d postgres -c "DROP DATABASE veilgate" -c "CREATE DATABASE veilgate"
```
```bash
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env.prod exec -T postgres pg_restore -U postgres -d veilgate --exit-on-error < deploy/backups/<the backup file>
```

3. Start again: `up -d`. Then check the Dashboard and the audit log's "Check for tampering" button (it recomputes the hash chain).

## Rotating secrets

| Secret | How |
|---|---|
| Access-token signing key | Add a second key (`Auth__Keys__1__...`), deploy, and remove the old key after 10 minutes, so tokens issued with it have expired. The public keys are published at `/.well-known/jwks.json`. |
| Database passwords | Change them in `deploy/.env.prod` **and** in the database (`ALTER ROLE vg_app PASSWORD '...'`), then `up -d`. Changing only the file locks the app out. |
| SMTP password | Change it in `deploy/.env.prod`, then `up -d`. |
| `VAULT_KEK`, `MFA_ENCRYPTION_KEY` | **No rotation is built.** Changing either makes existing protected data and enrolled authenticators unreadable. Do not change them on a running system. |

## What to watch

Daily, a few minutes (Document 08, section 6.6). All commands use the compose prefix above.

- **Are all services healthy?** `ps -a`: `api`, `postgres` and `kafka` should say `healthy`.
- **Is the newest backup recent?** `ls -lt deploy/backups | head -3`.
- **Disk space.** `df -h` on the machine, and `docker system df`. A full disk stops PostgreSQL.
- **Dead letters.** Messages that could not be handled land on `vg.gateway.requests.v1.dlq`. It should be empty:
  `exec kafka rpk topic describe vg.gateway.requests.v1.dlq -p` (look at the high watermark).
- **Outbox backlog.** Events waiting to be sent should be near zero, and certainly not growing:
  `exec postgres psql -U postgres -d veilgate -c "select count(*), min(created_at) from outbox_messages where sent_at is null"`.
- **Security events.** In the console: Security, then Security events. Look for repeated failed sign-ins, reused refresh tokens and blocked leaks.

## Operations calendar (Document 08, section 6.6)

| When | Task |
|---|---|
| Daily | Review the health, backup age, dead letters and security events above |
| Weekly | Merge the dependency updates Dependabot opens; review failed sign-ins; review access (users, roles, API clients) |
| Monthly | Restore drill; re-run the penetration test scenarios PT-AUTH, PT-AZ and PT-BL; review index usage |
| Quarterly | Rotate the mail credentials; review retention settings; update the threat model |

## When something is wrong

| Symptom | What to do |
|---|---|
| The API keeps restarting | `logs api --tail 40`. A line "refuses to start ... with unsafe settings" lists each setting and why (never the value). Fix `deploy/.env.prod`. |
| `migrate` exited with an error | `logs migrate`. The API and workers do not start until it succeeds. Common cause: the database is not reachable or the migrator password changed. |
| The site shows a certificate error | Check that DNS points at this machine and that ports 80 and 443 are reachable from the internet: `logs caddy`. Keep the `caddy_data` volume: deleting it forces new certificates and can hit Let's Encrypt's limits. |
| Requests appear on neither the Requests page nor the Dashboard | The event stream is stuck. Check `ps -a` for `kafka` and `workers`, then the outbox backlog above, then `logs workers --tail 40`. |
| An account is locked | Locking after repeated failures is the defence working. It clears when the lock time passes. A Tenant Admin can reset another person's two-step sign-in from the Users page. |
| Disk full | Free space first (old backups, `docker system prune` for unused images). Do not delete volumes: they hold the database. |

## Known gaps

- **No demo data in a production-shaped stack.** The first administrator is created by the bootstrap command, but the seeder (sample
  policies, an API client, a dataset) refuses any database that is not on the same machine by design, so a public read-only demo would need
  its sample data loaded another way. There is also no "platform admin" who can create further organisations: the platform endpoints are not built.
- **No monitoring or alerting stack.** The "What to watch" list is manual. Document 08 section 6.1 describes alerts (availability, error
  rate, latency, sign-in failures); none of those are wired up.
- **One machine, no failover**, and rate limits are per process.
- **The penetration test is only partly run** (see `docs/pentest/results.md`; the runs so far found five flaws, all fixed in code and retested), and no external security review has been done.

## Go-live checklist (Document 08, section 6.5) and where this stands

| Item | State |
|---|---|
| Penetration test plan executed, no open Critical or High findings | **Not done**: started; 5 flaws found (one High), all fixed and retested; no open Critical or High finding, but the 62 scenarios are not all run (tenant isolation needs a run with data, and many scenarios are not scripted) |
| ZAP baseline and image scans clean | Not done (dependency audit runs in CI; no ZAP) |
| MFA enforced for all users; seed accounts removed; bootstrap invitation used | MFA is required at first sign-in; the production stack has no seed accounts; the bootstrap link is single-use and expires in 48 hours. **You must confirm it was used.** |
| WAF in prevention mode; rate limits; health endpoints internal only | Rate limits and internal-only health: done. WAF: none |
| CORS, CSP and HSTS set for the production domain | Done, and the API refuses to start without them |
| Migrations applied; RLS enabled and forced on all tenant tables (automated check) | Done, checked on every test run |
| Backups verified by a restore drill | Scripts done; **you must run the drill** |
| Signing key and KEK in a key vault; rotation scheduled | Keys are generated; no key vault; no vault-key rotation |
| All test levels green | Backend, console and CI green; no end-to-end suite |
| Alerts routed to a monitored address | **Not done** |
| Rollback rehearsed | **Not done** |
| README states synthetic data only, not certified | Done |
| Budget alert and teardown instructions | Teardown: `down -v` (deletes all data). No cloud budget to alert on. |
