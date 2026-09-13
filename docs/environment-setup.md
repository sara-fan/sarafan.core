# Local development and cloud production environment

[Repository overview](../README.md) · [API and operations reference](technical-reference.md)

This guide describes the configuration shipped in this repository. Run commands from the `sarafan.core` checkout. Local native examples use PowerShell; cloud examples use Bash on Linux.

## Prerequisites

- Git and Docker with Linux containers and Docker Compose v2 supporting `up --wait` and `--wait-timeout`.
- For native development, the .NET 10 SDK selected by [global.json](../global.json): version `10.0.303` with `latestFeature` roll-forward. Container builds include their own SDK.
- PostgreSQL 17, supplied by the Docker commands below.
- For Back Office source development, a sibling `sarafan.back.office` checkout and Node matching its `package.json` (`^22.23.0 || ^24.15.0`). The Core-only build does not require frontend repositories.
- For cloud deployment, a Linux host with Bash, the `C.UTF-8` locale, OpenSSL, registry access, durable storage, DNS and TLS certificates. The deployment account needs Docker access and permission to create/write the configured storage directories.

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

The `http` launch profile selects `Development`, serves the API at `http://localhost:5080`, and enables startup migrations through `appsettings.Development.json`. Swagger is at `/swagger`; health is at `/api/v1/status/status`. Keep the explicit connection override: the tracked fallback connection uses host port `5433`, which may belong to the user's existing database. Other configuration uses normal ASP.NET Core environment variables, with `__` separating configuration sections.

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
| `BackofficeBootstrap__RealOrdersEnabled` | `false` |
| `BackofficeBootstrap__RealPaymentIntegrationEnabled` | `false` |

With the native database connection from Option B still selected, run:

```powershell
dotnet run --project src/Sarafan.Core/Sarafan.Core.csproj --launch-profile http -- --migrate-only
```

For Option A, provide the same settings through the shell environment and pass their names to a one-off container. Ensure `db` is healthy first:

```powershell
docker compose -p sarafan-core-dev -f docker-compose.yml run --rm --no-deps -e BackofficeBootstrap__Enabled -e BackofficeBootstrap__Email -e BackofficeBootstrap__Password -e BackofficeBootstrap__RealOrdersEnabled -e BackofficeBootstrap__RealPaymentIntegrationEnabled api --migrate-only
```

Disable bootstrap and remove its email/password variables after provisioning, then start the native API if applicable. Verify staff login and change the initial password; the account remains marked as demo until its password changes. The normal Docker API service keeps bootstrap disabled. Cloud equivalents are listed below.

Before trying customer registration, an Administrator must publish the required legal-document versions in Back Office, with effective dates that make them current. A fresh database contains no published legal text. See [customer consent configuration and rollout](customer-consents.md). Ordinary customer APIs also require current mandatory куки consent; staff APIs remain independent.

### Build and tests on disposable storage

Integration tests create and drop databases and require explicit `SARAFAN_TEST_POSTGRES`. Even a separate database on the protected PostgreSQL instance would write to protected storage; use a separate instance. The following test container stores its data in memory and is disposable:

```powershell
docker run --name sarafan-core-test-db --rm -d -p 127.0.0.1:55434:5432 -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres --tmpfs /var/lib/postgresql/data postgres:17
docker exec sarafan-core-test-db pg_isready -U postgres -d postgres
```

Once PostgreSQL accepts connections:

```powershell
$env:SARAFAN_TEST_POSTGRES = 'Host=127.0.0.1;Port=55434;Database=postgres;Username=postgres;Password=postgres'
dotnet restore Sarafan.sln
dotnet format Sarafan.sln --no-restore --verify-no-changes
dotnet build Sarafan.sln --configuration Release --no-restore
dotnet test Sarafan.sln --configuration Release --no-build --no-restore --collect:"XPlat Code Coverage"
docker stop sarafan-core-test-db
Remove-Item Env:SARAFAN_TEST_POSTGRES
```

The integration fixture disables the exchange-rate and consent-retention workers. New or modified code must meet the repository's 95% patch-coverage requirement. A container-only alternative uses its own Compose project and database volume; never run it against the user's default stack:

```powershell
docker compose -p sarafan-core-tests -f docker-compose.yml --profile test run --rm --build tests
docker compose -p sarafan-core-tests -f docker-compose.yml --profile test down
```

This alternative retains its test-only named volume and writes reports to `TestResults/`. Direct local `--wait` commands do not inherit the cloud bootstrap timeout; pass `--wait-timeout` explicitly as shown above.

## Cloud production environment

The cloud topology runs PostgreSQL, backup, a one-shot migration service, Core, the customer UI and Back Office. Core and PostgreSQL stay on the private application network; each frontend proxies API requests on its own origin. Adminer is absent.

**Release gate:** the current application verifies customer phone numbers with their last four digits in every runtime environment, including `Production`. It is a demonstration mechanism and must be replaced and disabled before real orders or payment integration. Keep both real-operation flags false until that work is complete. Active demo staff accounts also block real-operation startup. Track the release gate in the [MVP delivery plan](https://github.com/sara-fan/sarafan.spec/issues/26).

### Choose one edge mode

| Mode | Compose overlay | Requirements |
| --- | --- | --- |
| `edge` | `docker-compose.edge.yml` | Existing shared edge on external `sw-consulting-edge` network, routing the two hostnames to `sarafan-ui:8080` and `sarafan-backoffice:8080` |
| `production` | `docker-compose.production.yml` | Dedicated Nginx edge publishing ports 80/443; certificate files `s.crt` and `s.key` |

Configure DNS for both `sarafan.sw.consulting` and `sb.sw.consulting` to point at the selected edge. Allow inbound HTTP/HTTPS to the edge, and keep database/API ports private. For the dedicated mode, place the certificate chain and private key in `/srv/sarafan/certificate` or the chosen `SARAFAN_CERTIFICATE_DIR`. The certificate must cover both names; `*.sw.consulting` covers both. For shared mode, start and configure the shared edge before bootstrapping Sarafan.

### Prepare the checkout and environment file

Clone or update the Core checkout to the reviewed deployment revision. On a new installation, create the private configuration file without overwriting an existing one:

```bash
umask 077
test -e sarafan.env || cp sarafan.env.example sarafan.env
chmod 600 sarafan.env
chmod +x scripts/bootstrap-cloud.sh scripts/update-cloud.sh
```

Edit `sarafan.env` before deployment. It is ignored by Git. The scripts **source it as Bash** as well as passing it to Compose: keep it trusted, use shell-safe assignments and quote values containing spaces or shell metacharacters. Hexadecimal random values are convenient for database/JWT secrets; for example, `openssl rand -hex 32` generates one value. Generate distinct values and store them privately.

| Setting | Required configuration |
| --- | --- |
| `COMPOSE_PROJECT_NAME` | Stable project name, default `sarafan` |
| `SARAFAN_POSTGRES_DB`, `SARAFAN_POSTGRES_USER` | Database/user, default `sarafan` / `postgres` |
| `SARAFAN_POSTGRES_PASSWORD` | Replace the example placeholder with a strong database password |
| `SARAFAN_JWT_SECRET`, `SARAFAN_BACKOFFICE_JWT_SECRET` | Different random secrets, each at least 32 characters; replace both placeholders |
| `SARAFAN_POSTGRES_DATA_DIR` | Durable absolute non-root directory for PostgreSQL |
| `SARAFAN_BACKUP_DATA_DIR`, `SARAFAN_BACKUP_LOG_DIR` | Separate durable absolute non-root directories for backup files and logs |
| `SARAFAN_BACKUP_RETENTION_DAYS` | Backup retention, default 7 days |
| `SARAFAN_CORE_IMAGE_TAG`, `SARAFAN_UI_IMAGE_TAG` | Available, reviewed image versions; release numbers are independent |
| `SARAFAN_BACKOFFICE_IMAGE`, `SARAFAN_BACKOFFICE_IMAGE_TAG` | Repository without a tag and its independent version; default repository `ghcr.io/sara-fan/sarafan.back.office` |
| `SW_CONSULTING_EDGE_NETWORK` | Existing external network for `edge`, default `sw-consulting-edge` |
| `SARAFAN_CERTIFICATE_DIR` | Certificate directory for `production` |
| `SARAFAN_DEPLOYMENT_WAIT_TIMEOUT` | Positive health-wait timeout in seconds, default 180 |
| `SARAFAN_BACKOFFICE_BOOTSTRAP_ENABLED` | `true` only when creating the first Administrator |
| `SARAFAN_BACKOFFICE_BOOTSTRAP_EMAIL`, `SARAFAN_BACKOFFICE_BOOTSTRAP_PASSWORD` | Supply securely only for bootstrap; password must contain 8 to 18 characters |
| `SARAFAN_REAL_ORDERS_ENABLED`, `SARAFAN_REAL_PAYMENT_INTEGRATION_ENABLED` | `false` while demonstration authentication is in use |
| `SARAFAN_UI_LOGGING_ENABLED`, `SARAFAN_BACKOFFICE_LOGGING_ENABLED` | Independent frontend logging switches, default `false` |
| `OTEL_LOGS_EXPORTER`, `OTEL_TRACES_EXPORTER` | Default `none`; use `otlp` only when sending telemetry to a configured collector |

Bootstrap creates and checks the configured storage directories. Choose distinct locations and ensure the container processes can write their mounts. On an existing database, changing `SARAFAN_POSTGRES_PASSWORD` does not change the stored PostgreSQL role password; credential rotation requires a coordinated database operation.

**Image namespace:** [docker-compose-ghrc.yml](../docker-compose-ghrc.yml) currently pins the Core and customer UI repositories to `ghcr.io/maxirmx/sarafan.core` and `ghcr.io/maxirmx/sarafan.ui`. Their tag variables do not change those repositories. Core's publish workflow uses the GitHub repository owner, currently `sara-fan`, so a newly published tag is not automatically available at the older deployment path. Verify availability at the exact configured paths before deployment; deploying images that exist only under another owner requires a reviewed cloud Compose change. Back Office already supports its separate image-repository variable.

Only settings wired into the Compose files reach containers. For example, putting `Logging__LogLevel__Sarafan` or `ExchangeRates__Enabled` in `sarafan.env` alone does not pass it into Core; additional runtime options require explicit Compose environment entries. The local port settings in the example file do not publish cloud database or frontend ports.

### Validate and deploy

Validate without starting services or printing resolved secrets. Choose one value for `target` and retain it for updates:

```bash
target=production # Use edge for the shared server.
docker compose --env-file sarafan.env -f docker-compose-ghrc.yml -f "docker-compose.${target}.yml" config --quiet
```

If a registry package is private, authenticate the deployment account to GHCR with pull permission before proceeding. On an existing installation, take and verify a database backup before applying new migrations.

```bash
scripts/bootstrap-cloud.sh "$target"
```

The script validates Compose wait support, environment values, storage paths, and the selected edge prerequisite; dedicated mode also checks certificate hostname coverage. It then validates Compose, pulls images, starts backup/API dependencies, and waits for the frontends and dedicated edge when selected. Each scripted `--wait` uses `SARAFAN_DEPLOYMENT_WAIT_TIMEOUT`; this is not a total script, image-pull or migration deadline.

The `migrate` service runs `dotnet Sarafan.Core.dll --migrate-only` and must exit successfully before the API starts. The long-running API has `Database__ApplyMigrations=false`. Bootstrap does not create legal documents: publish them through Back Office before customer registration can succeed.

### Verify and finish bootstrap

```bash
docker compose --env-file sarafan.env -f docker-compose-ghrc.yml -f "docker-compose.${target}.yml" ps -a
docker compose --env-file sarafan.env -f docker-compose-ghrc.yml -f "docker-compose.${target}.yml" logs --tail=100 migrate api backup
curl --fail --silent --show-error https://sarafan.sw.consulting/api/v1/status/status
curl --fail --silent --show-error https://sb.sw.consulting/health
```

Confirm the migration container exited with code 0, the running services are healthy, and both frontends load through HTTPS. Check a staff login/session refresh, change the bootstrap Administrator password, then set `SARAFAN_BACKOFFICE_BOOTSTRAP_ENABLED=false` and remove both bootstrap credential values from `sarafan.env`. Reapply the deployment using the update command below so the migration container no longer retains them in its configuration. Provisioning is idempotent, but credentials should not remain configured.

Both cloud modes use two proxy hops (edge and frontend). Preserve the original HTTPS scheme at each hop so secure customer/staff refresh cookies work. Cloud Compose configures a forwarded-header limit of 2 and trusts private Docker network ranges; Core's standalone default trusts only loopback proxies. Other network layouts must set `ForwardedHeaders__KnownNetworks__N` or `ForwardedHeaders__KnownProxies__N` for their actual trusted boundary. Docker assigns container addresses dynamically; no custom IPAM subnet is required.

### Updates, backups and diagnosis

Update the checkout/configuration and select the intended Core/UI/Back Office tags, then run the same mode:

```bash
scripts/update-cloud.sh "$target"
```

The update script delegates to bootstrap, including image pulls, migrations and health checks. For a non-default environment-file path, set `SARAFAN_ENV_FILE` for either script and use the same path in manual Compose commands.

The backup service uses `ghcr.io/sw-consulting/db-backup:latest`, with data and logs in the configured durable directories. Verify successful backup creation and practice restoration into separate disposable storage. Restoring a database or rolling back a migration is a separate operation: reverting an image tag does not revert the schema. Review migrations and preserve a verified backup before attempting recovery.

For startup failure, inspect `migrate` logs first, then API and edge health. A missing edge network or invalid certificate must be corrected before retrying. An unhealthy backup service requires checking its logs, mounts and generated files; a successful API health response alone does not prove backups work. For login/session problems, verify DNS/TLS, forwarded HTTPS headers and the distinct JWT keys.

Core writes privacy-filtered logs to stdout and supports optional OTLP export. Choose stdout collection or OTLP delivery to a given backend to avoid duplicate ingestion. See [observability](technical-reference.md#observability) for correlation IDs, log levels and exporter settings. Frontend logging changes take effect when containers are recreated with updated environment settings; no image rebuild is required.

The exchange-rate worker needs outbound HTTPS to the CBR provider and OS time-zone data for `Europe/Moscow`. Its failures do not block API startup. See [official rates](technical-reference.md#official-usdrub-reference-rates) and [consent retention](customer-consents.md#storage-and-retention) for background-service behavior and configuration.
