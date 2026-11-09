# Recording the VeilGate demo video

A 3-minute screen recording with captions only (no voice), uploaded to YouTube. VeilGate is not deployed, so the video runs the real
stack on your own machine. Everything on screen is synthetic data.

Why this works for recruiters: they will not clone and run a repository. A short video that shows the product doing its job, plus
the README and a green CI badge, is what they actually look at.

## 1. Before you record (about 20 minutes, once)

Open one terminal tab per line. Every command runs from the repository root.

1. **Infrastructure** (Postgres, Redis, Kafka, MailHog, the fake model):

   ```bash
   docker compose -f deploy/docker-compose.yml --env-file .env up -d
   ```

2. **The API with the demo button switched on** (port 5080):

   ```bash
   ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 Demo__Enabled=true dotnet run --project src/VeilGate.Api
   ```

3. **The worker** (it turns gateway calls into the Requests page and the Dashboard):

   ```bash
   DOTNET_ENVIRONMENT=Development dotnet run --project src/VeilGate.Workers
   ```

4. **The console:**

   ```bash
   cd src/VeilGate.Web && npm run dev
   ```

5. **Make sure the demo organisation exists** (skip if you already ran it; running it again changes nothing). It creates the people, a published policy "HIPAA Safe Harbor", a second policy "Names only" left waiting for approval, an API client and a dataset. Use the migrator password
   from `.env` and a password of your choice:

   ```bash
   dotnet run --project tools/VeilGate.Seeder -- --connection "Host=localhost;Database=veilgate;Username=vg_migrator;Password=<PG_MIGRATOR_PASSWORD from .env>" --password "<a password you choose>"
   ```

6. **Optional: give Chat mode a model to call.** The seeded Acme Health organisation has no provider, so skip the Chat shot unless you add one. Sign in as `admin@acme.test`, open **Providers**. You need one provider with the address
   `http://localhost:8089` (no `/v1`; the fake model answers "Mock reply"). If the list is empty, add one with that address.
   Sign out afterwards: the video uses the demo button, not the admin.
7. **Put data on the Dashboard.** Sign in with "Try the demo", open the Playground, and run each text below once in **De-identify**
   mode (use "Use sample text" for the first). Eight runs is plenty. Wait about a minute, then open **Requests** and **Dashboard**:
   the rows and bars must be there. If not, the worker is not running.

   - `Patient Jane Roe, born 03/14/1962, MRN 4481920, can be reached at 555-201-8890. She was seen at Riverside Clinic on 2026-09-02 for a follow-up.`
   - `Maria Gomez (DOB 11/02/1975) phoned from 555-014-2233 about her prescription. MRN 7731045.`
   - `Follow-up for Samuel Ortiz, born 1988-07-19, discharged 2026-08-30 from Lakeside Hospital. Contact 555-330-1198.`
   - `Chen Wei, MRN 5520917, called 555-908-7766 on 2026-09-12 to reschedule.`
   - `Aisha Khan, 04/22/1991, reports the new dose is working. Reach her at 555-471-0925.`
   - `Dr. Patel saw Robert Hale (MRN 6642018, born 12/05/1950) on 2026-09-20.`
   - `Lena Fischer's number is 555-182-6640 and her date of birth is 01/30/1983.`
   - `Tomas Rivera, MRN 3390284, was admitted 2026-09-25 and discharged 2026-09-28.`

   These names and numbers are made up. Do not paste anything real into the Playground.
8. **Tidy the screen:** Chrome in a clean window, dark theme (it looks better on video), window about 1280 by 800, bookmarks bar
   and other tabs hidden, notifications off (Do Not Disturb), battery full, desktop clean. Zoom the page to 110% so text is readable
   on a phone.

## 2. Record

On a Mac: press **Cmd+Shift+5**, choose **Record Selected Portion**, drag a box around the browser window, then **Options** and set
the microphone to **None**. Click Record, do the shots below slowly (pause about 2 seconds on each result so a caption can sit on it),
and stop from the menu bar. Record each shot as a separate clip if that is easier; trimming is simpler than re-doing a long take.

Move the mouse deliberately and click only what the shot says. A slow, calm recording looks more professional than a fast one.

## 3. Shot list (about 3 minutes)

| Time | What is on screen | Caption (on screen text) |
|---|---|---|
| 0:00 | Title card (2 seconds, white text on dark): **VeilGate** | "VeilGate: stop patient data from reaching an AI model" |
| 0:04 | Sign-in page. Click **Try the demo**. | "Runs locally. Synthetic data only. Read-only demo session." |
| 0:12 | Playground. Click **Use sample text**. Mode **Detect**, click **Run**. | "Step 1: find the protected health information" |
| 0:25 | The detected items (names, date of birth, ID number, phone). Pause. | "Names, dates, IDs and phone numbers, found by detectors" |
| 0:32 | Mode **De-identify**, **Run**. Pause on **Masked text**. | "Step 2: replace it using a policy. This is all the model ever sees." |
| (optional) | Mode **Chat**, **Run**. Only if you added a provider (see step 6). | "Step 3: the model answers; VeilGate checks it for leaks and restores values. (A mock model here.)" |
| 1:10 | **Policies**. Open **HIPAA Safe Harbor** (published): the YAML. | "Policies are YAML, versioned, and compared version to version" |
| 1:25 | Its **test** panel: run the sample text. | "Test a policy on sample text before it ever goes live" |
| 1:35 | Go back, open **Names only** (waiting for approval): show the workflow bar. | "Two-person rule: you cannot approve your own policy" |
| 1:50 | **Requests**. Open one row. | "Every call is recorded: how many items of each kind, never the text" |
| 2:05 | **Dashboard**. Switch the range once. | "Counts flow through an outbox and Kafka into idempotent metrics" |
| 2:20 | **Audit log**. Click **Check for tampering**. | "A hash-chained audit log: any edit breaks the chain" |
| 2:35 | **Security** page, Sessions then Security events. | "MFA, session control and security events" |
| 2:50 | End card (4 seconds): the repository address, the portfolio address, your name. | "Code, tests and security write-up on GitHub: sudha-bhavi/veilgate" |

If a shot goes wrong, record only that shot again.

## 4. Edit and add captions

- **iMovie** (free, already on most Macs) or **CapCut** (free): drop the clips on the timeline in order, trim the pauses, and add a title
  for each caption in the table. Keep each caption on screen for at least 3 seconds and under 12 words if you can.
- Add a soft instrumental track only if it is quiet and you have the right to use it (YouTube's Audio Library is free). No track
  at all is fine.
- Export **1080p**, **MP4**, no more than 3 minutes 30 seconds.
- Watch it once with the sound off and once on a phone-sized window. If you cannot follow it muted, add a caption.

## 5. Upload and link it

On YouTube choose **Public** (or **Unlisted** if you only want to share the link).

**Title:** VeilGate demo: masking patient data before it reaches an AI model

**Description:**

```text
VeilGate is a multi-tenant gateway that finds protected health information in text, replaces it according to a policy, sends only
the sanitized text to a language model, and restores the values in the answer.

Built with .NET 10, React, PostgreSQL (row-level security), Kafka, Docker and GitHub Actions. Includes a policy approval workflow,
MFA, a hash-chained audit log, and a penetration test I ran myself (five flaws found, fixed and retested).

Synthetic data only. Not certified for real PHI. Runs locally; not deployed.

Code: https://github.com/sudha-bhavi/veilgate
Portfolio: https://sudhabhavi-portfolio.netlify.app/

Chapters
0:00 What it is
0:12 Find the patient data
0:32 Mask it with a policy
0:50 Ask a model and restore the answer
1:10 Policies and the two-person rule
1:50 Requests and dashboard
2:20 Audit log and security
```

Adjust the chapter times to match your edit. YouTube needs the first chapter at 0:00 and at least three chapters.

## 6. After the upload (send me the link and I will do the file edits)

Once you have the YouTube address, send it to me. I will add it to the top of the VeilGate README (a thumbnail that links to the
video), the portfolio's VeilGate card, the resume, and the GitHub and LinkedIn text. You commit and push as usual.

By hand, because I cannot sign in for you:

1. **GitHub repository page:** in **About**, add a one-line description and topics (`dotnet`, `phi`, `hipaa`, `llm`, `kafka`,
   `postgresql`, `row-level-security`, `react`, `security`) and the portfolio as the website. Pin the repository on your profile.
2. **LinkedIn:** add the video under **Featured**, and the project under **Projects** (text is in `profile-text.md` in the portfolio repository).
3. **Say plainly that it is not deployed.** The video, README and portfolio all say "runs locally". That is a strength if said
   plainly: you built and security-tested it, and you are not pretending it is a hosted product.

## 7. Optional improvements that help recruiters

- **An architecture diagram** at the top of the README (a picture is read faster than the ASCII sketch). I can draw one.
- **A 30-second GIF** of the Playground at the top of the README, for people who do not click through to YouTube.
- **A "Why I built it" paragraph** (three sentences: the problem, the design choice you are proudest of, what you learned). Recruiters
  quote this in interviews.
