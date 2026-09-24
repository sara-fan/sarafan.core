# Local development and cloud production environment

This guide describes the configuration shipped in this repository. Run commands from the `sarafan.core` checkout. Local native examples use PowerShell; cloud examples use Bash on Linux.

## Prerequisites

- Git and Docker with Linux containers and Docker Compose v2 supporting `up --wait` and `--wait-timeout`.
- For native development, the .NET 10 SDK selected by [global.json](../global.json): version `10.0.303` with `latestFeature` roll-forward. Container builds include their own SDK.
- PostgreSQL 17, supplied by the Docker commands below.
- For Back Office source development, a sibling `sarafan.back.office` checkout and Node matching its `package.json` (`^22.23.0 || ^24.15.0`). The Core-only build does not require frontend repositories.
- For cloud deployment, a Linux host with Bash, the `C.UTF-8` locale, OpenSSL, registry access, durable storage and DNS pointing at the VPS. The deployment account needs Docker access and read access to its env file. Bind-mount permissions must suit the Docker daemon and container users; the deployment account does not need read/write access to container storage.

## Local development

### Storage and configuration boundaries

The examples below use separate container/project names and storage. Explicit `-f docker-compose.yml` selects the tracked configuration without loading the machine-specific `docker-compose.override.yml`. Use the same project name and file list for every command on a given stack.

In the maintainer's workspace, `docker-compose.override.yml` maps persistent PostgreSQL data from `R:/Projects/30.Projects/sarafan/.pgdata` to `/var/lib/postgresql/data` and requires the host directory to exist. The override, directory and mapping are user-owned. Agents need explicit authorization to change them or write to that database, including migrations and tests. Starting the existing stack is not a configuration check; use `docker compose config --quiet` for read-only validation.

### Option A: API and database in Docker

Run these commands in a shell without cloud `SARAFAN_*` settings or a cloud `.env` file. Development defaults are supplied by [docker-compose.yml](../docker-compose.yml); no environment file is needed.

```powershell
docker compose -p sarafan-core-dev -f docker-compose.yml config --quiet
docker compose -p sarafan-core-dev -f docker-compose.yml up -d --build --wait --wait-timeout 180 db api adminer
Invoke-RestMethod http://localhost:8080/api/v1/status/status
```

The API runs in `Development` and applies migrations on startup. PostgreSQL uses the named volume `sarafan-core-dev_pgdata`. This explicit base configuration does not publish the database port to the host.

| Service | Local address / connection |
| --- | --- |
| Core health | `http://localhost:8080/api/v1/status/status` |
| Swagger UI | `http://localhost:8080/swagger` |
| Adminer | `http://localhost:8088`; server `db`, database `sarafan`, user/password `postgres` with development defaults |
| PostgreSQL inside Compose | `db:5432` |

The API port is published on all host interfaces by the base file; use this development configuration only on a trusted development machine. Adminer binds to loopback. To include Back Office, clone its repository beside Core and use the overlay:

```powershell
docker compose -p sarafan-core-dev -f docker-compose.yml -f docker-compose.backoffice.yml up -d --build --wait --wait-timeout 180
```

Back Office is available at `http://localhost:8083`; `SARAFAN_BACKOFFICE_PORT` changes its loopback port. Its Nginx proxy forwards same-origin API requests to Core. The shared UI package is fetched from a pinned release tarball, so a sibling `sarafan.ui.shared` checkout is unnecessary. Provision the first staff account as described below.

To stop this stack while retaining its named database volume:

```powershell
docker compose -p sarafan-core-dev -f docker-compose.yml -f docker-compose.backoffice.yml down
```

Omit the second `-f` if Back Office was not started. The solution also includes a Visual Studio `docker-compose` startup project; review its effective configuration before use because it can load the protected machine override.

### Option B: run the API with the .NET SDK

Use a separate PostgreSQL container for native development. These credentials are only for the local development database. The volume retains data between container restarts.

```powershell
docker run --name sarafan-core-native-db -d -p 127.0.0.1:55433:5432 -e POSTGRES_DB=sarafan -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres --mount type=volume,source=sarafan-core-native-pgdata,target=/var/lib/postgresql/data postgres:17
docker exec sarafan-core-native-db pg_isready -U postgres -d sarafan
```

Wait until `pg_isready` reports that PostgreSQL accepts connections. On later runs, use `docker start sarafan-core-native-db` instead of creating the container again.

```powershell
$env:ConnectionStrings__DefaultConnection = 'Host=127.0.0.1;Port=55433;Database=sarafan;Username=postgres;Password=postgres'
dotnet restore Sarafan.sln
dotnet run --project src/Sarafan.Core/Sarafan.Core.csproj --launch-profile http
```

The `http` launch profile selects `Development`, serves the API at `http://localhost:5080`, and enables startup migrations through `appsettings.Development.json`. Swagger is at `/swagger`; health is at `/api/v1/status/status`. Keep the explicit connection override: the tracked fallback connection uses host port `5432`, which may belong to the user's existing database. Other configuration uses normal ASP.NET Core environment variables, with `__` separating configuration sections.

To run Back Office with Vite, open a second PowerShell terminal in the sibling repository:

```powershell
npm ci
$env:SARAFAN_API_TARGET = 'http://localhost:5080'
npm run dev
```

Open `http://localhost:5174`. When Core runs in Docker, use `http://localhost:8080` as the API target (the Vite default). The Vite proxy keeps browser requests on the frontend origin.

### First staff account and legal documents

Staff accounts use a separate email/password identity realm. Public customer registration cannot create a staff account. The initial Administrator is provisioned only by the explicitly enabled, idempotent migration bootstrap.

Supply these native Core settings through a private process environment; do not put credentials in tracked configuration or command history:

| Native setting | Value |
| --- | --- |
| `BackofficeBootstrap__Enabled` | `true` only for the initial migration run |
| `BackofficeBootstrap__Email` | Initial Administrator email obtained securely |
| `BackofficeBootstrap__Password` | Initial password, 8 to 18 characters |
| `BackofficeBootstrap__RealPaymentIntegrationEnabled` | `false` |

With the native database connection from Option B still selected, run:

```powershell
dotnet run --project src/Sarafan.Core/Sarafan.Core.csproj --launch-profile http -- --migrate-only
```

For Option A, provide the same settings through the shell environment and pass their names to a one-off container. Ensure `db` is healthy first:

```powershell
docker compose -p sarafan-core-dev -f docker-compose.yml run --rm --no-deps -e BackofficeBootstrap__Enabled -e BackofficeBootstrap__Email -e BackofficeBootstrap__Password -e BackofficeBootstrap__RealPaymentIntegrationEnabled api --migrate-only
```

Disable bootstrap and remove its email/password variables after provisioning, then start the native API if applicable. Verify staff login and change the initial password; the account remains marked as demo until its password changes. The normal Docker API service keeps bootstrap disabled. Cloud equivalents are listed below.

Before trying customer registration, an Administrator must publish the required legal-document versions in Back Office, with effective dates that make them current. A fresh database contains no published legal text. Registration retains its personal-data consent and agreement requirements, and protected customer writes still require current personal-data consent. Customer APIs no longer require technical-cookie consent or a cookie-consent document; session restoration is independent of cookie-consent evidence and legal-document availability. Authentication cookies and staff authentication remain unchanged.

### Build and zero-setup tests

The automated suite uses uniquely named EF Core InMemory stores. It does not connect to PostgreSQL, start Docker, read `SARAFAN_TEST_POSTGRES`, execute migrations or require Visual Studio test settings to be selected manually. Run it directly from Test Explorer or from a shell:

```powershell
dotnet restore Sarafan.sln
dotnet format Sarafan.sln --no-restore --verify-no-changes
dotnet build Sarafan.sln --configuration Release --no-restore
dotnet test Sarafan.sln --configuration Release --no-build --no-restore --collect:"XPlat Code Coverage"
```

The in-memory host disables migrations, the exchange-rate worker and the consent-retention worker, then creates and deterministically seeds a fresh store for stateful tests. Disconnected Npgsql contexts are used only for model metadata or generated-SQL inspection and never open a connection. Migration execution and PostgreSQL lock, transaction, concurrency and constraint semantics are intentionally outside the automated-test policy.

The checked-in test runsettings collect coverage without Visual Studio configuration. Migration files and the thin PostgreSQL operations adapter are excluded; new or modified application code must still meet the repository's 95% patch-coverage requirement. A container-only alternative is also independent of the database service:

```powershell
docker compose -p sarafan-core-tests -f docker-compose.yml --profile test run --rm --build tests
```

This alternative writes reports to `TestResults/`. Direct local `--wait` commands do not inherit the cloud bootstrap timeout; pass `--wait-timeout` explicitly as shown above.

## Cloud production environment

Deploy to a dedicated Linux VPS using [docker-compose.production.yml](../docker-compose.production.yml), which includes the application and its Traefik wrapper. This follows the sibling Logibooks deployment: Traefik discovers frontend Docker labels and manages Let's Encrypt certificates. All services use the Compose default network; no external edge network is needed. Only Traefik publishes host ports (80 and 443). Core, PostgreSQL and frontend ports stay private.

| Public hostname | Service |
| --- | --- |
| `sarafanof.com`, `www.sarafanof.com` | Customer UI |
| `gtc.sarafanof.com` | Back Office |

Each frontend proxies its same-origin API requests to Core and preserves the forwarded HTTPS scheme for secure cookies. HTTP redirects to HTTPS. Create DNS A records for all three names pointing to the VPS; publish AAAA records only if IPv6 reaches that VPS. Allow inbound ports 80 and 443 and outbound access to Let's Encrypt. Traefik uses HTTP-01 validation and automatic renewal; no manually supplied certificate/key files are needed. See the [Traefik ACME reference](https://doc.traefik.io/traefik/v3.6/reference/install-configuration/tls/certificate-resolvers/acme/).

### Persistent files

| Host path (default) | Container path | Purpose |
| --- | --- | --- |
| `/srv/sarafan/pgdata` | `/var/lib/postgresql/data` | PostgreSQL 17 data |
| `/srv/sarafan/backup` | `/backups` | Database backups |
| `/srv/sarafan/backup/logs` | `/var/log` | Backup service logs |
| `/srv/sarafan/certificate` | `/letsencrypt` | Traefik ACME account and certificates |
| `/srv/sarafan/settings/appsettings.json` | `/app/appsettings.json` (read-only) | Core configuration, mounted in API and migration containers |

Provision the host directories separately with administrative privileges and ownership appropriate to the containers. Bootstrap checks only absolute, non-root path syntax; it does not test the shell user's access or create/chmod storage files. Docker validates mounts and services enforce their actual access permissions at startup. Traefik creates `acme.json` with mode 600 under its container credentials; an existing file must already have that mode and be accessible to Traefik. Preserve it across updates. Prepare the settings file yourself; a missing file fails the bind mount instead of becoming a directory. Ensure the API container user (UID 1654 in the .NET image) can read it.

### Prepare configuration

Run from the Core checkout on the VPS:

```bash
umask 077
test -e sarafan.env || cp sarafan.env.example sarafan.env
chmod 600 sarafan.env
chmod +x scripts/bootstrap-cloud.sh scripts/update-cloud.sh
```

Separately, have the VPS administrator provision the directories in the table and install `src/Sarafan.Core/appsettings.json` at `/srv/sarafan/settings/appsettings.json` if it does not already exist. Set ownership and permissions for the actual container users, including any rootless Docker or user-namespace mapping. Do not recursively change ownership on an existing PostgreSQL data directory or replace existing settings/certificates during an update.

Edit `sarafan.env` and the mounted settings before deployment. Arrange read permissions for the settings file without making secrets publicly readable (for example a group readable by UID 1654). The scripts source the env file as trusted Bash: use shell-safe assignments and quote special characters. It is ignored by Git.

- Set `SARAFAN_ACME_EMAIL` to the certificate administrator's email.
- Replace the database password and both JWT secrets; the JWT secrets must differ and each contain at least 32 characters. `openssl rand -hex 32` can generate each value independently.
- Select available Core, UI and Back Office image tags. Core and UI retain the existing `ghcr.io/maxirmx` image paths; Back Office defaults to `ghcr.io/sara-fan/sarafan.back.office`. Verify the exact image/tag exists before deployment; Core's publish workflow uses the GitHub repository owner, which may differ from the deployment path.
- Keep `COMPOSE_PROJECT_NAME=sarafan` stable. Storage path and image tag overrides are listed in the example env file.
- Set additional Core options, including Quartz schedules, in the mounted JSON. Compose environment values override the JSON for database connection, identity keys, secure cookies, migration policy and forwarded headers. Keep secrets out of tracked files.

For a new database, set `ScheduledJobs:IanaTldUpdate:RunOnStartup` to `true` in the mounted JSON to populate the TLD catalogue immediately. Restore `false` after the first successful download. Without a catalogue, URL preview, new order creation and anonymous order Ops return `503 tld_catalog_unavailable`.

**Release gate:** predictable phone-suffix verification allows non-payment functionality, including orders, but must be replaced and disabled before real payments. Keep `SARAFAN_REAL_PAYMENT_INTEGRATION_ENABLED=false` until then. Active demo staff accounts also block real-payment startup. See the [MVP delivery plan](https://github.com/sara-fan/sarafan.spec/issues/26).

For the first Administrator only, set `SARAFAN_BACKOFFICE_BOOTSTRAP_ENABLED=true` and supply the bootstrap email and password securely (8 to 18 characters). After successful login, change that password, disable bootstrap and remove the credentials from the env file, then run the update script. Publish current legal documents through Back Office before customer registration.

### Validate and deploy

Read-only validation does not start containers or apply migrations:

```bash
docker compose --env-file sarafan.env -f docker-compose.production.yml config --quiet
```

Authenticate to GHCR if packages are private. Before updating an existing database, take and verify a backup. Deploy:

```bash
scripts/bootstrap-cloud.sh
```

The script checks configuration, storage path syntax and Compose wait support, pulls images, starts the migration/API dependencies, and waits for the frontends, backup and Traefik. Each health wait uses `SARAFAN_DEPLOYMENT_WAIT_TIMEOUT` (default 180 seconds); this is not a total migration or image-pull deadline. Traefik's health check confirms the proxy process, not certificate issuance: verify public HTTPS separately.

The one-shot migration service runs the image entrypoint with `--migrate-only`. API startup requires successful migration completion and keeps automatic migrations disabled. Both use the same mounted settings.

```bash
docker compose --env-file sarafan.env -f docker-compose.production.yml ps -a
docker compose --env-file sarafan.env -f docker-compose.production.yml logs --tail=100 migrate api backup traefik
curl --fail --silent --show-error https://sarafanof.com/api/v1/status/status
curl --fail --silent --show-error https://www.sarafanof.com/health
curl --fail --silent --show-error https://gtc.sarafanof.com/health
```

Verify customer and staff login/session refresh. Core trusts private Docker ranges with a forwarded-header limit of two, matching Traefik and the frontend proxy.

### Updates and backups

Update the checkout and selected image tags, then run:

```bash
scripts/update-cloud.sh
```

The update script repeats bootstrap, including pulls, migrations and health checks. Set `SARAFAN_ENV_FILE` for a non-default env path and use that same file with manual Compose commands. Local Compose port variables in the example env do not publish cloud ports.

The backup container retains backups for seven days by default. Check its logs and generated files; API health alone does not prove backups work. Practice restoration only into separate disposable storage. Changing the env database password does not rotate an existing database role's password, and reverting an image does not roll back schema migrations.

For deployment failure, inspect migration/API logs first. For HTTPS failure, inspect Traefik logs, DNS, ports 80/443 and ACME storage permissions. Choose stdout collection or optional OTLP export (`OTEL_*`) to avoid duplicate log ingestion.
