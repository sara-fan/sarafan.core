<!-- Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting) -->
<!-- All rights reserved. -->
<!-- This file is a part of Sarafan application -->

# Sarafan Core

Product requirements are defined in the [current specification](https://github.com/sara-fan/sarafan.spec); implementation scope and delivery are tracked in the [MVP issue plan](https://github.com/sara-fan/sarafan.spec/issues/26). This README documents the technical implementation and operation of Core.

## Core service

[![ci](https://github.com/maxirmx/sarafan.core/actions/workflows/ci.yml/badge.svg)](https://github.com/maxirmx/sarafan.core/actions/workflows/ci.yml)
[![publish](https://github.com/maxirmx/sarafan.core/actions/workflows/publish.yml/badge.svg)](https://github.com/maxirmx/sarafan.core/actions/workflows/publish.yml)

ASP.NET Core identity and customer-profile service for Sarafan. It targets .NET 10 LTS, uses PostgreSQL from the first migration, and is packaged as a Linux container.

## Prerequisites

- .NET 10 SDK
- Docker with Docker Compose

## Local development

```bash
docker compose up -d --wait db adminer
dotnet restore Sarafan.sln
dotnet run --project src/Sarafan.Core/Sarafan.Core.csproj
```

The development connection uses PostgreSQL on host port `5433`. The machine-specific Compose override stores database data in `R:/Projects/30.Projects/sarafan/.runtime/postgres`. Adminer is available only in the development compose stack at <http://localhost:8088>; use server `db` from inside Compose or `host.docker.internal:5433` when connecting through the browser-hosted Adminer container.

The v1 API status endpoint is <http://localhost:5080/api/v1/status/status> when the development launch profile is used. Registration and login use the normalized phone number's last four digits as the verification code in Development, Testing, and Production. This is a demonstration mechanism, not phone-possession verification. It must be replaced and disabled before accepting real orders or integrating a real payment system, regardless of the runtime environment name; the implementation/release gate is tracked in [#5](https://github.com/sara-fan/sarafan.spec/issues/5) and [#14](https://github.com/sara-fan/sarafan.spec/issues/14).

### Identity endpoints

| Method | Endpoint | Purpose |
| --- | --- | --- |
| `POST` | `/api/v1/auth/code/request` | Request a registration or login code |
| `POST` | `/api/v1/auth/code/verify` | Register or log in and issue an access/refresh session |
| `POST` | `/api/v1/auth/refresh` | Rotate the HttpOnly refresh cookie and issue a new access token |
| `POST` | `/api/v1/auth/logout` | Revoke the refresh-token family and clear the cookie |
| `GET`, `PUT` | `/api/v1/customers/me` | Read or update the authenticated customer profile |
| `GET`, `PUT`, `DELETE` | `/api/v1/customers/me/photo` | Manage a JPEG, PNG, or WebP profile photo up to 5 MiB |

### Back-office identity

Back-office users are a separate identity realm, not customer records. They use email/password login with BCrypt hashes, a dedicated JWT issuer, audience, signing key and authentication scheme, a separate refresh cookie/session table, and routes under `/api/v1/backoffice`. Customer tokens cannot authorize back-office routes, and back-office tokens cannot authorize customer routes.

The fixed role catalogue contains `administrator`, `shift-manager`, `senior-operator`, and `operator`. Only Administrators can list, create, update, disable, or assign roles to back-office users. Any authenticated back-office user can read their current identity and update only their own name and password. The service prevents disabling or demoting the last active Administrator.

| Method | Endpoint | Purpose |
| --- | --- | --- |
| `POST` | `/api/v1/backoffice/auth/login` | Log in with back-office email and password |
| `POST` | `/api/v1/backoffice/auth/refresh` | Rotate the dedicated HttpOnly refresh cookie |
| `POST` | `/api/v1/backoffice/auth/logout` | Revoke the back-office refresh-token family |
| `GET` | `/api/v1/backoffice/auth/me` | Check the current back-office identity |
| `GET`, `POST`, `PUT`, `DELETE` | `/api/v1/backoffice/users` | Administrator-only user management; same-user reads are limited |
| `GET` | `/api/v1/backoffice/users/ops` | Logibooks-compatible Administrator role catalogue |
| `PUT` | `/api/v1/backoffice/users/me` | Update the current user's name or password |
| `GET` | `/api/v1/backoffice/roles` | Administrator-only role catalogue |

For the one-time demo bootstrap, obtain the initial Administrator credentials securely from the issue owner. Configure a signing key distinct from the customer key, keep `SARAFAN_REAL_ORDERS_ENABLED` and `SARAFAN_REAL_PAYMENT_INTEGRATION_ENABLED` false, set `SARAFAN_BACKOFFICE_BOOTSTRAP_ENABLED=true`, and provide the email and password only through the protected deployment environment. Run the migration service once, verify login, then set the bootstrap flag to `false` and remove both credential values. The service and deployment bootstrap reject demo provisioning when either real-operation flag is enabled. Tracked configuration contains no credential value.

### API error contract (0.0.4)

Version 0.0.4 replaces the demo's ad-hoc error responses with RFC 9457 Problem Details. API errors use `application/problem+json`; `type` is the canonical machine-readable identifier, while `code` remains an explicit compatibility extension. Clients should branch on `type`, display the safe Russian `detail`, and retain the structured `errors` extension for field-level validation. Clients must not make decisions by parsing localized `title` or `detail` text.

## Docker

```bash
docker compose up --build
```

The containerized API is available at <http://localhost:8080/api/v1/status/status>, PostgreSQL at `127.0.0.1:5433`, and Adminer at `127.0.0.1:8088` by default.

`Sarafan.sln` also contains a `docker-compose` project for Visual Studio, matching the Docker Compose workflow used by Logibooks Core. Select the **docker-compose** startup project to build and start the development stack; no `.env` file is required for development. The `sarafan.env.example` settings, including the required durable host paths, are used by cloud deployment.

## Verification

```bash
dotnet build Sarafan.sln --configuration Release
# Start PostgreSQL first; tests create and remove an isolated database.
SARAFAN_TEST_POSTGRES='Host=127.0.0.1;Port=5433;Database=postgres;Username=postgres;Password=postgres' \
  dotnet test Sarafan.sln --configuration Release --collect:"XPlat Code Coverage"
docker compose --profile test run --rm --build tests
docker compose up -d --build --wait
curl --fail http://localhost:8080/api/v1/status/status
docker compose down
```

## Observability

Sarafan Core writes one human-readable record per line to stdout. Each line uses an RFC 3339 UTC timestamp and includes severity, a stable dotted event name, W3C trace/span identifiers when present, and a fixed English diagnostic message. Application code continues to use `ILogger<T>` through the source-generated Sarafan event catalogue; structured fields follow the OpenTelemetry Logs Data Model and semantic conventions.

ASP.NET Core accepts and propagates the W3C `traceparent` header. RFC 9457 responses expose the same 32-character lowercase hexadecimal trace ID in `traceId` and `instance=urn:sarafan:problem:{traceId}`. To troubleshoot a reported failure, search Core logs or the telemetry backend for that `trace_id` value.

OTLP log and trace export is disabled unless `OTEL_LOGS_EXPORTER=otlp` or `OTEL_TRACES_EXPORTER=otlp` is configured (an `OTEL_EXPORTER_OTLP_ENDPOINT` also enables the corresponding exporters when their selectors are absent). Standard `OTEL_EXPORTER_OTLP_*` variables configure protocol, endpoint, headers, timeout, and batching. Export failure is isolated from request handling.

Do not enable both OTLP export and infrastructure collection of stdout into the same backend: choose one delivery path to avoid duplicate records. Set the signal exporter to `none` when stdout is collected. The default `Logging:LogLevel:Sarafan` is `Debug`; set `Logging__LogLevel__Sarafan=Information` in deployments that should suppress detailed entry/exit events.

Controller entry points and application services log safe input/output summaries at Debug, with redaction markers for customer data and secrets. Unexpected failures are recorded at Warning with the operation name and exception type, while expected rejections and requested cancellation get a Debug exit. See the logging policy in `AGENTS.md` for event identifiers, exception ownership, and allowlisted fields.

Logs intentionally exclude raw URLs and query strings, HTTP headers and bodies, tokens/cookies/codes, personal data, localized Problem Details text, connection strings, SQL values, and client IP addresses. New events must use the stable catalogue and allowlisted low-cardinality attributes described in `AGENTS.md`.

Framework Warning, Error, and Critical records remain visible as the stable `framework.diagnostic` event. Their category is retained only when it is a safe type-like identifier; message state, attributes, and exception data are discarded before console or OTLP output.

## Cloud deployment

Cloud bootstrap requires Docker Compose v2 with `up --wait` and `--wait-timeout`
support and verifies both before deployment. Each bootstrap health wait is bounded
by `SARAFAN_DEPLOYMENT_WAIT_TIMEOUT` seconds (default 180). Direct local Compose
commands using `--wait` require that option but do not inherit the bootstrap timeout;
pass `--wait-timeout` explicitly to bound a local health wait.

The cloud stack contains the customer UI, back office and Sarafan Core without
publishing their containers directly on the host. Choose exactly one deployment overlay:

- `edge` attaches both frontends to the external `sw-consulting-edge` network using
  aliases `sarafan-ui` and `sarafan-backoffice`;
- `production` starts a dedicated TLS edge on ports 80 and 443 for
  `sarafan.sw.consulting` and `sb.sw.consulting`.

```bash
cp sarafan.env.example sarafan.env
chmod 600 sarafan.env
chmod +x scripts/bootstrap-cloud.sh scripts/update-cloud.sh

# Shared server
scripts/bootstrap-cloud.sh edge

# Dedicated production server
scripts/bootstrap-cloud.sh production
```

For a dedicated server, place `s.crt` and `s.key` in
`/srv/sarafan/certificate` (or set `SARAFAN_CERTIFICATE_DIR`). The certificate
must cover both hostnames (the `*.sw.consulting` wildcard covers both). For the shared server, start the
`sw-consulting-edge` project before Sarafan so the external Docker network
exists.

Update the selected deployment with `scripts/update-cloud.sh edge` or
`scripts/update-cloud.sh production`. UI and Core image tags are independent so
the repositories do not need synchronized release numbers. Set `SARAFAN_BACKOFFICE_IMAGE`
to the registry/repository name without a tag and `SARAFAN_BACKOFFICE_IMAGE_TAG`
to its version. `SARAFAN_BACKOFFICE_LOGGING_ENABLED` controls staff UI logging independently.

Create the DNS record for `sb.sw.consulting` pointing to the selected edge.
The shared-edge configuration must route that host to `sarafan-backoffice:8080`;
the API and database stay on the private application network. Forward the original
HTTPS scheme through both proxies so staff refresh cookies remain secure.

For local back-office development with the sibling checkout available:

```bash
docker compose -f docker-compose.yml -f docker-compose.backoffice.yml up -d --build --wait
# Back office: http://localhost:8083 (override SARAFAN_BACKOFFICE_PORT if needed)
```

The explicit overlay builds `../sarafan.back.office`; the ordinary Core Compose
file and CI remain usable without that repository. The staff API and bootstrap
configuration are described above; no additional identity tables or migrations
are required for the UI integration.

The production stack starts `ghcr.io/sw-consulting/db-backup:latest`, matching Logibooks' `tooling.db-backup` setup. Configure durable `SARAFAN_BACKUP_DATA_DIR` and `SARAFAN_BACKUP_LOG_DIR` host paths plus the retention period in `sarafan.env`; bootstrap validates all database and backup paths before deployment.

Production migrations run in a dedicated one-shot `migrate` service before the API starts. The long-running API has startup migration disabled. Core defaults to the framework's restrictive loopback proxy trust; the cloud Compose stack explicitly trusts private Docker network ranges and processes the two proxy hops (`edge` and `ui`) used by both deployment modes. Docker assigns container addresses dynamically, so the stack does not require a custom IPAM subnet. Other deployments must configure `ForwardedHeaders__KnownNetworks__N` or `ForwardedHeaders__KnownProxies__N` for their own trusted proxy boundary.

## Optional pull request template

Use the [traceability template](.github/PULL_REQUEST_TEMPLATE/traceability.md) if helpful, or write your own PR description. It provides prompts for the planning issue, specification version and sections, relevant scenarios and design frames, and verification results.

To select it on GitHub, append `&template=traceability.md` to a PR creation URL that already has query parameters, or `?template=traceability.md` if it has none. You can also copy the template into the description. See [GitHub's query parameter documentation](https://docs.github.com/en/pull-requests/reference/using-query-parameters-to-create-a-pull-request).

The template is opt-in, is not the default PR body, and has no CI enforcement. Applicable issue and repository requirements still apply when using a custom description.
