# Running the platform locally

Everything needed to run the whole platform on a laptop: the database, the four APIs, and the
shell, with a real operator, a real tenant, and its admin signed in to Tickets and Ledger.

Verified end to end on 2026-09-29 (macOS, Docker Desktop, .NET 10, Node 20), with every service
connecting as its own runtime database role, exactly as a deployment does.

**The first time takes about 20 minutes.** Parts 1–6 are one-time setup. Afterwards, starting
again is Part 7.

---

## Quick start: `bin/dev`

`bin/dev` does everything in Parts 2–7 for you. The parts below remain the explanation of what it
does, and the way to do any step by hand. After the prerequisites in Part 1:

```bash
bin/dev setup                                   # build, settings, database (safe to re-run)
bin/dev operator create you@example.com         # asks for a password; prints the MFA secret ONCE
bin/dev up                                      # database + four APIs + shell, in the background
bin/dev tenant create "Acme Fire" admin@acme.test
                                                # asks for the operator password and a current code,
                                                # creates the tenant, licenses Tickets and Ledger,
                                                # pins core.api to it, prints the invitation link
```

Open the printed link, set the admin's password, then sign in at http://localhost:5173.

Day to day:

| Command | Does |
|---|---|
| `bin/dev up` / `bin/dev down` | start / stop everything (data is kept) |
| `bin/dev status` | what is running, the operator, the pinned tenant |
| `bin/dev logs core` | follow a service's log (`core`, `platform`, `tickets`, `ledger`, `shell`); logs are in `.local/logs/` |
| `bin/dev invite-link [email]` | the newest invitation link, for that address if given |
| `bin/dev operator reset` | a new authenticator secret for the operator (lost phone) |
| `bin/dev reset` | delete the local database and state, after you type `reset`; keeps `.local/dev.env` |

It runs the services in the background with `dotnet run --no-build` after one build, and stops them
by the process listening on each port.

---

## What runs where

| Piece | Where | Notes |
|---|---|---|
| PostgreSQL 16 | Docker container `app-platform-dev-db`, port **5433** | Data kept in the Docker volume `app-platform-dev-data` |
| core.api | http://localhost:5100 | Organisation, people, sign-in, invitations |
| platform.api | http://localhost:5101 | Operator API. There is no operator console yet (D14), so you use it with `curl` |
| tickets.api | http://localhost:5102 | |
| ledger.api | http://localhost:5104 | |
| shell | **http://localhost:5173** | The app you open in the browser. It forwards `/api/core`, `/api/tickets` and `/api/ledger` to the APIs above |

Port 5433, not 5432, so it cannot collide with a PostgreSQL you may already run.

The API ports are fixed: the shell's proxy (`shell.ui/vite.config.ts`) expects them. The browser
test suite (`e2e/`) uses the **same** API ports with its own database, so stop your local services
before running it.

Everything local that must not be committed lives in `.local/` at the repository root, which git
ignores: your settings file, the key ring that signs cookies, and captured email.

---

## Part 1 — Prerequisites

Install, then check each:

| Tool | Check | Get it |
|---|---|---|
| Docker Desktop, running | `docker info` | docker.com |
| .NET 10 SDK | `dotnet --version` → `10.x` | dot.net |
| Node.js 20.19 or later | `node --version` | nodejs.org, or `brew install node` |
| An authenticator app on your phone | | Google Authenticator, 1Password, Authy… Operators must use MFA |

`openssl`, `curl` and `python3` come with macOS and are used below.

**Use Chrome or Firefox.** The sign-in cookie is marked `Secure`, and both browsers accept that on
`http://localhost`. Safari has not been tried.

## Part 2 — Build once

From the repository root (every command in this guide runs from there):

```bash
npm ci
dotnet build app-platform.slnx
```

## Part 3 — Your local settings

This creates `.local/dev.env`, which every later step loads. Run it **once**. It generates the
platform's key-encryption key, which encrypts operator authenticator secrets. Keep this file: with
a different key the platform refuses to start (it names both key fingerprints), and you would have
to re-enrol the operator (see Troubleshooting).

```bash
mkdir -p .local/keys .local/mail && chmod -R 700 .local   # it will hold keys: readable by you only
umask 077                                                # and so will everything written into it
cat > .local/dev.env <<EOF
# Every terminal that sources this writes owner-only files: keys, captured mail, logs.
umask 077
export ASPNETCORE_ENVIRONMENT=Development

# One key ring for every service, or a cookie issued by core is unreadable by tickets and every
# app call answers 401.
export DataProtection__KeyPath="$(pwd)/.local/keys"

# Email is captured to files here instead of sent. Invitations arrive here.
export Email__CapturePath="$(pwd)/.local/mail"

# Shared between platform.api and core.api for provisioning tenants.
export Internal__ApiKey="$(openssl rand -hex 24)"
export Core__BaseUrl="http://localhost:5100"

# The platform's key-encryption key. NOT exported, so no other service inherits it: platform
# commands below pass it to platform.api explicitly.
PLATFORM_KEK="$(openssl rand -base64 32)"

# The database, without a user: each service adds its own role.
export DB="Host=localhost;Port=5433;Database=appplatform;Password=dev"
EOF
```

## Part 4 — The database

Start PostgreSQL:

```bash
docker run -d --name app-platform-dev-db \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=appplatform \
  -p 5433:5432 -v app-platform-dev-data:/var/lib/postgresql/data \
  postgres:16-alpine
```

Wait until it answers (a few seconds), then create the roles, schemas and tables with the same
script a real deployment uses. It runs **inside** the container, so you need no PostgreSQL tools on
your Mac:

```bash
until docker exec app-platform-dev-db psql -U postgres -d appplatform -c 'SELECT 1' >/dev/null 2>&1; do sleep 1; done

docker cp database app-platform-dev-db:/database
docker exec -e PGUSER=postgres -e PGDATABASE=appplatform app-platform-dev-db bash /database/init-local.sh
```

It ends by running `99-verify.sql`; **`(0 rows)`** there means the security boundary holds.

Each service connects as its own role, and roles are created without passwords. Give them a local
one:

```bash
docker exec app-platform-dev-db psql -U postgres -d appplatform -c "
  ALTER ROLE ap_core_rt     PASSWORD 'dev';
  ALTER ROLE ap_tickets_rt  PASSWORD 'dev';
  ALTER ROLE ap_ledger_rt   PASSWORD 'dev';
  ALTER ROLE ap_platform_rt PASSWORD 'dev';"
```

Finally, mark the database as set up. `bin/dev` checks for this mark, so that it treats a setup
which stopped half way as unfinished; without it, `bin/dev` would refuse this database:

```bash
docker exec app-platform-dev-db psql -U postgres -d appplatform -c "COMMENT ON DATABASE appplatform IS 'bin/dev: initialised'"
```

## Part 5 — Create your operator

Operators are created on the machine, never over the API. The password is read from stdin so it
stays out of your shell history. It must be at least 12 characters.

```bash
source .local/dev.env
read -rs "OPERATOR_PASSWORD?Operator password: " && echo   # zsh; in bash: read -rsp "Operator password: " OPERATOR_PASSWORD
printf '%s\n' "$OPERATOR_PASSWORD" | \
  ConnectionStrings__Platform="$DB;Username=ap_platform_rt" \
  Encryption__PlatformKeyEncryptionKey="$PLATFORM_KEK" \
  dotnet run --project platform.api --no-launch-profile -- create-platform-user you@example.com
```

It prints an **MFA secret** and an `otpauth://` link, **once**. Add it to your authenticator app now,
using the app's "enter a setup key" option with the secret, account `you@example.com`, time-based.
The platform stores it only encrypted; if you lose it, see Troubleshooting.

## Part 6 — First run: start everything, then create a tenant

### 6.1 Start the services

Open **five terminal tabs** at the repository root. In each, run `source .local/dev.env` first, then:

```bash
# Tab 1: core.api
ConnectionStrings__Core="$DB;Username=ap_core_rt" \
  dotnet run --project core.api --no-launch-profile --urls http://localhost:5100

# Tab 2: platform.api (the only service given the platform's key)
ConnectionStrings__Platform="$DB;Username=ap_platform_rt" \
  Encryption__PlatformKeyEncryptionKey="$PLATFORM_KEK" \
  dotnet run --project platform.api --no-launch-profile --urls http://localhost:5101

# Tab 3: tickets.api
ConnectionStrings__Tickets="$DB;Username=ap_tickets_rt" \
  dotnet run --project apps/tickets/tickets.api --no-launch-profile --urls http://localhost:5102

# Tab 4: ledger.api
ConnectionStrings__Ledger="$DB;Username=ap_ledger_rt" \
  dotnet run --project apps/ledger/ledger.api --no-launch-profile --urls http://localhost:5104

# Tab 5: the shell
npm run dev --workspace @app-platform/shell
```

Each API logs `Now listening on: http://localhost:51xx` when it is ready. The shell prints
`Local: http://localhost:5173/`. Leave the tabs open.

Check them from a sixth tab. **401 is the healthy answer** here: the endpoint needs a session and
you have none yet.

```bash
for url in 5100/api/core/v1/auth/session 5101/api/platform/v1/errors \
           5102/api/tickets/v1/tickets 5104/api/ledger/v1/accounts; do
  echo "$url $(curl -s -o /dev/null -w '%{http_code}' http://localhost:$url)"
done
```

### 6.2 Sign in as the operator

In the sixth tab, with the 6-digit code currently showing in your authenticator app:

```bash
source .local/dev.env
read -rs "OPERATOR_PASSWORD?Operator password: " && echo
read -r "CODE?Authenticator code: "

# The body is built by node from environment variables and piped to curl, so the password never
# appears in a command's arguments (which any process can read with `ps`), and a quote or backslash
# in it cannot break the JSON.
E=you@example.com P="$OPERATOR_PASSWORD" C="$CODE" \
  node -e 'process.stdout.write(JSON.stringify({ email: process.env.E, password: process.env.P, code: process.env.C }))' \
  | curl -s -c .local/operator.jar -H 'Content-Type: application/json' --data-binary @- \
      http://localhost:5101/api/platform/v1/auth/sign-in
# → {"signedIn":true}

# Every change needs the CSRF token the sign-in issued, sent back as a header.
CSRF=$(awk '$6=="ap_csrf"{print $7}' .local/operator.jar)
```

A code works **once**. If you sign in again within the same 30 seconds, wait for the next code.

### 6.3 Create a tenant and license its apps

Use an email address of your own for the tenant's first admin. Nothing is really sent: the
invitation is written to `.local/mail/`. The `idempotencyKey` makes a retry safe: sending the same
request again returns the same tenant rather than creating a second.

```bash
curl -s -b .local/operator.jar -H 'Content-Type: application/json' -H "X-CSRF-Token: $CSRF" \
  -d '{"name":"Acme Fire","adminEmail":"admin@acme.test","idempotencyKey":"first-local-tenant"}' \
  http://localhost:5101/api/platform/v1/tenants
# → {"tenantId":"ten_…"}

TENANT=ten_…   # paste the id from the answer

for app in tickets ledger; do
  curl -s -b .local/operator.jar -H 'Content-Type: application/json' -H "X-CSRF-Token: $CSRF" \
    -d "{\"app\":\"$app\",\"licensed\":true}" \
    http://localhost:5101/api/platform/v1/tenants/$TENANT/entitlements; echo
done

echo "export TENANT=$TENANT" >> .local/dev.env   # so later starts can find it
```

### 6.4 Restart core.api pinned to that tenant

Sign-in has to know which tenant you are signing in to. In production that will come from the
hostname; that is not built yet (D5), so locally core.api is **pinned** to one tenant by
configuration. In tab 1, stop core.api (`Ctrl+C`) and start it again with the tenant:

```bash
source .local/dev.env
ConnectionStrings__Core="$DB;Username=ap_core_rt" Tenant__PublicId="$TENANT" \
  dotnet run --project core.api --no-launch-profile --urls http://localhost:5100
```

If it says the address is already in use, see "Port already in use" below.

### 6.5 Accept the invitation and sign in

Get the link from the captured invitation:

```bash
TOKEN=$(node -e "const fs=require('fs');const f=fs.readdirSync('.local/mail').filter(n=>n.endsWith('.json')).map(n=>'.local/mail/'+n).sort((a,b)=>fs.statSync(b).mtimeMs-fs.statSync(a).mtimeMs)[0];console.log(JSON.parse(fs.readFileSync(f)).payload.token)")
echo "http://localhost:5173/accept-invite?token=$TOKEN"
```

Open that link in Chrome or Firefox, choose the admin's password, and accept. Then go to
**http://localhost:5173**, sign in as `admin@acme.test`, and you should see **Tickets** and
**Ledger** in the navigation.

That is the whole journey: an operator created a tenant and licensed it, and its admin, invited by
email, is working in both apps.

---

## Part 7 — Every time after that

```bash
docker start app-platform-dev-db
```

Then the five tabs from 6.1, each starting with `source .local/dev.env`, with **one change**: tab 1
starts core.api already pinned:

```bash
ConnectionStrings__Core="$DB;Username=ap_core_rt" Tenant__PublicId="$TENANT" \
  dotnet run --project core.api --no-launch-profile --urls http://localhost:5100
```

To stop: `Ctrl+C` in each tab, then `docker stop app-platform-dev-db`. Your data stays in the Docker
volume.

### After pulling new code

A pull may bring new migrations. Applying them by hand as the right role is covered in
`database/README.md`. Locally, the simplest route is to start again: see "Start completely fresh"
below, then Parts 4–6. Nothing local is worth keeping yet.

### Start completely fresh

```bash
docker rm -f app-platform-dev-db
docker volume rm app-platform-dev-data
rm -rf .local/keys .local/mail .local/operator.jar
sed -i '' '/^export TENANT=/d' .local/dev.env
```

Then Parts 4 to 6. Keep `.local/dev.env` (its keys still work for a new database). Delete it too
if you want new keys, and redo Part 3 first.

---

## Running the tests

```bash
dotnet test app-platform.slnx                       # all .NET tests; needs Docker running
npm run test --workspace @app-platform/shell        # UI unit tests (also tickets-ui, ledger-ui, web-api)
npm run test --workspace @app-platform/e2e          # the browser journey
```

The browser journey starts its own database (port 55433) and its own copies of every service, on
the **same ports** as your local ones. Stop your five tabs first.

---

## Troubleshooting

**Port already in use** (`address already in use`, or a service that will not start). `dotnet run`
starts your app as a separate process, and stopping one does not always stop the other. Free the
port, then start again:

```bash
kill $(lsof -ti tcp:5100 -sTCP:LISTEN)     # or 5101, 5102, 5104, 5173
```

**Signed in, but every Tickets or Ledger call answers 401.** The services are not sharing one key
ring. Every API tab must have run `source .local/dev.env` (it sets `DataProtection__KeyPath`).
Restart any tab that did not.

**The admin's sign-in says the email or password is wrong, and it is right.** core.api is not
pinned to the tenant, or is pinned to a different one. Check tab 1 was started with
`Tenant__PublicId="$TENANT"` and that `echo $TENANT` shows the tenant's id.

**platform.api or `create-platform-user` refuses to start, naming key fingerprints.** It was started
with a different `PLATFORM_KEK` from the one that created its keys, almost always because
`.local/dev.env` was recreated. Use the original file, or start completely fresh.

**platform.api refuses to start: "Encryption:PlatformKeyEncryptionKey is required".** The command
was missing `Encryption__PlatformKeyEncryptionKey="$PLATFORM_KEK"`, which is passed to platform.api
on its own command line rather than exported to every service.

**Lost the operator's authenticator, or its secret was never saved.** Re-enrol. This also signs the
operator out everywhere:

```bash
source .local/dev.env
ConnectionStrings__Platform="$DB;Username=ap_platform_rt" \
  Encryption__PlatformKeyEncryptionKey="$PLATFORM_KEK" \
  dotnet run --project platform.api --no-launch-profile -- reset-platform-user-mfa you@example.com
```

**Operator sign-in answers `mfa_code_invalid`.** The code was already used (wait for the next one),
or the phone's clock is off by more than 30 seconds.

**Operator sign-in answers `mfa_required`.** The request had no `code`.

**`rate_limited` (429).** More than 10 sign-in attempts a minute from your machine. Wait a minute,
or add `export RateLimits__AnonymousPerMinute=100` to `.local/dev.env` and restart the APIs.

**core.api stops at startup: "No email transport is configured".** `Email__CapturePath` is not set:
run `source .local/dev.env` in that tab.

**No invitation in `.local/mail/`.** core.api delivers it within a few seconds of the tenant being
created, but only while it is running. Check tab 1 is up and has no errors.

**A page shows an error with a reference like `err_…`.** Search that service's tab for the
reference: the full error is logged there under it. Operators also see it in the error feed:

```bash
curl -s -b .local/operator.jar 'http://localhost:5101/api/platform/v1/errors?limit=20'
```

---

## The operator API, briefly

With a signed-in `.local/operator.jar` and `$CSRF` from 6.2:

| What | Request |
|---|---|
| Error feed | `GET /api/platform/v1/errors?limit=50` |
| Create a tenant | `POST /api/platform/v1/tenants` `{"name","adminEmail","idempotencyKey"}` |
| License or unlicense an app | `POST /api/platform/v1/tenants/{tenantId}/entitlements` `{"app":"tickets","licensed":true}` |
| Suspend, resume or retire a tenant | `POST /api/platform/v1/tenants/{tenantId}/status` (see `SetTenantStatusFeature.cs` for the body) |

Every `POST` needs `-H "X-CSRF-Token: $CSRF"`. Operator sessions end after 30 minutes idle or 8 hours
in total; sign in again as in 6.2.
