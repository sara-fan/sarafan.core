# Agent Guidelines for Sarafan Project

## Specification and repository guidance

- Follow the current specification identified in the [specification README](https://github.com/sara-fan/sarafan.spec#source-of-truth). If an implementation issue conflicts with it, flag the discrepancy before implementing the affected behavior.
- In implementation PR descriptions, cite the governing specification version and section, and link the planning issue. Use any suitable format; the PR template is optional.
- When a change introduces or changes a lasting convention, API contract, domain invariant, security/privacy rule, workflow, or test pattern, update the nearest relevant `AGENTS.md` in the same PR. Keep entries concise and reusable.
- Otherwise, include `AGENTS.md: no durable change` in the PR description.
- Before editing documentation, read its current revision and preserve user-authored changes. Keep product requirements in the specification and task-specific discussion in the issue.
- The predictable phone-suffix demo verification mechanism permits all non-payment functionality, including orders. Replace and disable it before enabling a real payment-system integration; a build/runtime environment named Production does not satisfy this requirement. Keep this payment release gate tracked in the [MVP delivery issue](https://github.com/sara-fan/sarafan.spec/issues/26).

## Code Standards and Requirements

### Entity Framework model configuration

- Keep `AppDbContext.OnModelCreating` as the single `ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly)` registration point. Do not add inline entity mappings, feature-specific registration helpers, partial context mapping methods, or a growing manual configuration list.
- Put each mapped entity's complete persistence configuration in one internal sealed `<Entity>Configuration : IEntityTypeConfiguration<Entity>` class, in its own file under `Data/Configurations/<Feature>/`. New entities follow this policy immediately; entity model classes remain free of persistence-mapping attributes.
- Use explicit table and column names, keys, lengths/types/conversions, indexes, concurrency tokens and seed data in the owning entity's configuration. Declare each relationship once, on its dependent/foreign-key entity. Avoid configuration-order dependencies and passes that silently rewrite another entity's metadata; there is no feature-specific naming convention.
- `AppDbContextModelTests` enforce one discoverable configuration per mapped entity and configuration-order independence through disconnected Npgsql metadata. They must never open a connection or compare the model with a migration snapshot. Configuration-only refactors must not create schema migrations or edit snapshots; intentional schema changes retain their normal production migration and deployment review.

### Protected local database configuration

- Treat `docker-compose.override.yml` as user-owned machine configuration. Do not edit, replace, delete, regenerate, or commit it without explicit user authorization for that action; general implementation, testing, cleanup, or deployment requests do not authorize these changes.
- The local PostgreSQL data directory is `R:/Projects/30.Projects/sarafan/.pgdata`, mounted at `/var/lib/postgresql/data`. Do not change this mapping or modify, delete, move, reset, restore over, or change permissions on that directory without explicit user authorization. This includes database writes through SQL, migrations, bootstrap, tests, and containers. Use a separate disposable database and storage for agent verification.
- Do not start or recreate the user's database/application stack merely to validate configuration. Validate with `docker compose config`; keep the existing services and data untouched unless the user explicitly authorizes the runtime action.

### Back-office identity boundary

- Serve the back-office SPA in its independent `backoffice` container at `sb.sw.consulting`, with shared-edge alias `sarafan-backoffice`. Preserve the same-origin API proxy and forwarded HTTPS scheme so secure staff cookies work. Keep its image/tag/logging settings independent from the customer UI; the local sibling build belongs in the explicit backoffice Compose overlay.
- Cloud bootstrap must check Compose wait support and bound service health waits using the configured deployment timeout; documentation must distinguish bootstrap guarantees from direct local Compose commands.

- Keep back-office users, roles, refresh sessions, credentials, JWT issuer/audience/signing key, cookie, authentication scheme, and `/api/v1/backoffice` routes separate from customer identity and `/api/v1/auth`.
- Keep the fixed back-office role catalogue and deny-by-default action-to-role matrix in `src/Sarafan.Core/Authentication`; unknown roles and actions must never grant access.
- Revoke back-office refresh sessions and increment the user's token version whenever email, password, active state, or roles change. Serialize administrator state/role mutations with the PostgreSQL advisory transaction lock and reject disabling or demoting the last active Administrator.
- Mark the bootstrap Administrator as demo until its password is changed. Provision it only through the explicitly enabled, idempotent migration bootstrap; supply credentials through secure runtime configuration, never tracked files or logs. Reject bootstrap and reject startup with any active demo staff account while real payment integration is enabled, then disable and remove bootstrap credentials after the first successful run.
- Require back-office passwords to contain 8 to 18 characters in API models, service validation, bootstrap validation, UI validation, and user-facing guidance. Do not expose byte-count rules to users; the conservative character maximum keeps passwords within BCrypt's input boundary.

### Scheduled background jobs

- Run periodic Core work through durable Quartz jobs configured under `ScheduledJobs:<JobName>` with a Quartz `Cron`, timezone identifier and independent `RunOnStartup` flag. Blank cron means no recurring trigger; blank cron plus `RunOnStartup=false` disables the job. Validate cron and timezone settings at startup, use non-concurrent job execution, and let failed work retry only at the next configured trigger.
- Disable every scheduled job in deterministic integration tests by setting its cron to blank and `RunOnStartup=false`. Use direct Quartz triggers when exercising scheduling behavior; never let tests call external providers or use protected local database storage.

### Official exchange rates

- Keep `Currency` append-only with ISO 4217 numeric values and publish its Core-owned Russian names and aliases through order operations and the authorized back-office status catalogue. Persist exchange-rate currency pairs as numeric enum values; clients must not infer enum labels.
- Synchronize official CBR USD/RUB and EUR/RUB from one response at startup and daily at 00:10 Europe/Moscow. Preserve each independently valid currency and keep source-effective date separate from UTC retrieval time; the provider/pair/date unique constraint preserves the first observation, including across concurrent instances. Provider failures must not block startup or erase history.
- `/api/v1/backoffice/status` is staff-authorized supplementary data, returns the latest persisted rate with its original nominal and source date, and uses `Cache-Control: no-store`. Keep public health free of FX data. Official rates are not the commercial pricing rate; do not apply spreads or alter pricing here.
- Configure exchange-rate synchronization through `ScheduledJobs:ExchangeRates`; retain startup execution and the daily 00:10 Europe/Moscow cron by default.

### Order product snapshots

- Preview log summaries allow only `manual_review` and `recognized` outcomes; redact unknown outcomes and all source/product data. Normalize top-level JSON quantity conversion errors to `validation_failed` with `errors.quantity` and the Russian integer-validation message; preserve required and positive-number validation messages.

- [Product review amendment v1.22](https://github.com/sara-fan/sarafan.spec/blob/main/spec/Product%20review%20amendment.md), Core #34, governs submitted products and staff corrections. Keep the recognized product, dimensions, characteristics and commercial applied rate server-owned. Store immutable submitted name/USD unit price/color/size separately from original URL/quantity/comment and from nullable complete staff overrides. Effective projections, staff search and sorting use overrides → submission → available legacy snapshot; never infer color/size from arbitrary characteristics.
- New submissions and every staff correction require name (500), positive USD price decimal(10,2), quantity 1–4, optional color/size (200 each) and comment (2000). Trim outer whitespace without changing internal text/case. Resolve equivalent original-payload idempotency before new-field/rate/quantity limits; retain legacy replay and do not compare staff corrections with the replay request.
- Enforce the inclusive 900 EUR merchandise-only limit (1000 EUR × constant reserve coefficient 0.9) using the latest common official CBR USD/RUB and EUR/RUB date no later than Moscow today. Respect nominals and compare integer price cents/rate millionths with BigInteger cross-products, never rounded EUR. Missing pairs block new submissions/corrections but not reads or equivalent replays. Store original and last-correction rate references separately from AppliedExchangeRate.
- Staff product details use public orderNumber and current nullable buyer-profile fields, with no general customer API. EditOrderProduct is deny-by-default, permits the four established roles only in UnderReview, and cannot alter identity, URL, submission, profile or status. UpdatedAt and Status are concurrency tokens; preserve timestamp microseconds in DTOs. Persist correction, advanced UpdatedAt, rate references and append-only before/after actor audit in one transaction. Conflict responses require reload, never automatic overwrite.
- Both order Ops publish identical productLimits/valueLimit contracts; staff does not depend on customer Ops. Public preview remains stateless manual_review with nullable product prefill for future recognition. New clients and Core must ship together because new submissions require submittedProduct. Keep product/profile/request data redacted in boundary logs.
- Normalize product source addresses in Core for anonymous preview, order creation and API projections. Preserve explicit HTTP/HTTPS, assume HTTPS for scheme-less or scheme-relative addresses, reject other explicit schemes and URL userinfo credentials, and return/store the same canonical absolute URL. New inputs must use a DNS hostname with an IANA-listed top-level domain and must reject IP literals. The singleton database row is the sole current IANA catalogue: replace it atomically only with a validated newer feed and publish its version and entries through anonymous order Ops. When the row is absent, TLD-dependent Ops, preview and genuinely new order creation return `503 tld_catalog_unavailable`; unrelated APIs and equivalent idempotent replay remain available. Legacy projections are non-throwing and fall back to the unchanged stored URL when its canonical form cannot fit the response contract. Idempotency replay equivalence uses the pre-v0.1.0 URL rules without rewriting, suffix-validating or canonical-length-validating the immutable legacy row; new orders remain strict. The v0.1.0 preview is stateless and deterministically selects manual review.

### Controller Error Responses

- Represent every in-scope API error as an RFC 9457 Problem Details document and return it as `application/problem+json`.
- Use the single dedicated Sarafan problem-details model in `src/Sarafan.Core/RestModels`; do not introduce alternative error DTOs, anonymous error objects, raw error strings, or bodyless controller errors.
- Keep `type` stable and use it as the primary machine-readable problem identifier. Use `https://sarafan.sw.consulting/problems/{kebab-case-name}` while retaining the matching snake-case `code` extension for client compatibility; changing either identifier is a breaking contract change.
- Keep each problem's HTTP status in the centralized catalogue, keep the payload `status` identical to the actual response status, identify each occurrence with `urn:sarafan:problem:{traceId}`, and return Russian problem responses with `Content-Language: ru`.
- Write every user-facing `title`, `detail`, and validation message in Russian. Keep `title` short and stable for a problem type, and put occurrence-specific corrective information in `detail`.
- Define problem payloads in the centralized Sarafan problem-details factory/service. Services may signal an error condition but must not construct HTTP payloads or supply client-facing exception messages.
- Implement controller-action error paths through specifically named helpers in `src/Sarafan.Core/Controllers/SarafanControllerBase.cs`; those helpers must delegate to the centralized problem-details factory/service.
- Reuse an existing helper when it matches the response. Otherwise add a specifically named helper instead of constructing `StatusCode`, `BadRequest`, `NotFound`, `Conflict`, `Unauthorized`, `Forbid`, `Problem`, or another error result directly in a controller action.
- Route automatic model validation, authentication and authorization failures, empty error statuses, and unhandled exceptions through the same RFC 9457 contract.
- Preserve RFC 6750 semantics alongside RFC 9457: every JWT 401 challenge must include `WWW-Authenticate: Bearer`; add only the safe `error="invalid_token"` parameter for a supplied invalid token, and never expose token-validation exception details in challenge parameters.
- Never expose stack traces, database details, exception messages, credentials, tokens, or other implementation-sensitive information in a problem response.

### Test Coverage

- Maintain at least 95% patch coverage for all new or modified code.
- Add or expand tests until the changed-code coverage target is met before handing off a change.
- Automated tests must require no PostgreSQL instance, Docker service, environment variable or manual Visual Studio setup. Use EF Core InMemory for application-owned stateful behavior; disconnected `UseNpgsql` is allowed only for model metadata or generated-SQL inspection and must never open a connection.
- Do not test migration execution or PostgreSQL lock, transaction, concurrency, constraint or rollback semantics. Keep production migrations and provider behavior intact, but exclude `Data/Migrations` and the thin `Data/PostgreSql` adapter from Coverlet, Visual Studio and Codecov coverage.
- Keep convention tests that reject direct construction of controller error responses outside `SarafanControllerBase.cs` and reject RFC 9457 payload construction outside the centralized problem-details factory/service.

### Observability and Logging

- Emit application logs only through constructor-injected `ILogger<T>` (or a typed `ILogger<T>` resolved at the composition root) and the source-generated stable event catalogue in `src/Sarafan.Core/Observability/SarafanEvents.cs`; do not add vendor-specific loggers, string-based logger categories, ad-hoc event identifiers, interpolated log strings, or direct console output.
- Keep each event's numeric `EventId`, dotted `EventName`, severity, and fixed English human-readable message stable. Treat changes as an operational contract change and cover them with tests.
- Model records according to the OpenTelemetry Logs Data Model and use OpenTelemetry semantic-convention attribute names when defined. Use `sarafan.*` only for Sarafan-specific concepts.
- Keep the text console record human-readable and single-line, with an RFC 3339 UTC timestamp, severity, event name, W3C trace/span identifiers when available, and a meaningful message. Structured fields supplement the message and must never replace it with JSON or a code-only body.
- Propagate W3C Trace Context and correlate RFC 9457 `traceId` with `Activity.TraceId.ToHexString()` (32 lowercase hexadecimal characters). Do not use the complete `Activity.Id` as the public trace identifier.
- Log HTTP operations by low-cardinality route template, method, status, and duration only. Never log raw paths, query strings, full URLs, request/response bodies, headers, SQL parameters, localized Problem Details text, or serialized problem documents.
- Use an allowlist for log attributes. Never record credentials, tokens, cookies, verification codes, personal/customer data, free-form input, client IP addresses, exception messages, database connection strings, or other secrets.
- Every controller entry point must log its inputs on entry and its outputs on exit at **Debug** level, except the public `StatusController` endpoint, which logs at **Trace** to avoid routine probe noise. This includes early returns and automatic action-validation responses. Register `ControllerLoggingFilter` globally so new actions inherit this policy. Requests rejected before the MVC action pipeline (for example, authorization failures) retain centralized HTTP/problem logging.
- Apply the same **Debug** input/output and **Warning** unexpected-exception rules to every public instance entry point of application services, including authentication, token issuance, problem-details creation, and exception handling. Use `OperationLogging` with constructor-injected `ILogger<T>`; private implementation methods and static utility functions do not require separate boundary events.
- Input/output logging means explicit safe summaries through `LogValueSummary`, never raw payload serialization or arbitrary `ToString()`. Include parameter names, allowlisted enum-like values, result kinds, HTTP status codes, and explicit redaction markers. Unknown types and strings are redacted by default. Extend the allowlist and its privacy tests together when adding an input/output shape.
- At each controller/service boundary, log an unexpected exception at **Warning** with the operation's fully qualified method name and safe `error.type`; preserve the original exception and stack by rethrowing it. A propagating failure can have one Warning per boundary to identify the affected operations. The centralized exception handler emits a fallback Warning only when no boundary has reported it. Event 1400 (`sarafan.core.exception.unhandled`) uses Warning starting with version 0.0.6.
- Starting with version 0.0.6, event 1400 describes only the exception reaching the centralized handler, with no problem code or claim that a response was produced. Preserve this warning even if the response has already started or writing fails. For direct problem-response writes, emit event 1300 only after serialization to the response stream succeeds, using the actual normalized problem status and code; keep result/model creation logging for MVC callers separate from direct-write completion.
- Expected `ServiceException`/`BadHttpRequestException` rejections and cancellation accompanied by a cancelled operation/request token are not unexpected-exception Warnings. Log an exit at the selected boundary level describing the failure/cancellation when no value was returned; do not manufacture a successful output. An unsolicited `OperationCanceledException` is unexpected.
- Keep input/output summary construction behind the selected boundary-level check: **Debug** by default and **Trace** for the public `StatusController` endpoint. Use catalogue events 1600/1601/1602 (`sarafan.core.operation.entered`/`exited`/`failed`), `code.function.name`, `sarafan.operation.inputs`/`outputs`, and `error.type`; retain current W3C trace correlation.
- Keep framework Warning, Error, and Critical records visible, but pass them through the centralized privacy policy: emit a stable generic event and safe category only, and discard framework message state, attributes, and exceptions before console or OTLP output.
- Keep OTLP export optional and driven by standard `OTEL_*` configuration. Exporter or collector failure must not affect API behavior, and a deployment must choose either OTLP delivery or stdout collection to avoid duplicate ingestion.
- Add tests for every new event, severity, semantic attribute, trace-correlation path, redaction boundary, and filtering decision. New features must extend the stable catalogue rather than bypass the observability facility.

### Copyright Header Requirement

All source code files in the Sarafan project **MUST** include a copyright header at the top of the file.

#### C# Files

All `.cs` files must include the following copyright header:

```csharp
// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application
```

**Placement:** The copyright header must be the first lines in the file, before any `using` statements or namespace declarations.

**Example:**
```csharp
// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System;
using System.Collections.Generic;

namespace Sarafan.Core
{
	// ... rest of the file
}
```
**Auto-generated files:** Do not add headers to auto-generated files like AppDbContextModelSnapshot.cs

#### Other File Types

For other file types (XML, JSON, YAML, etc.), use the appropriate comment syntax for that language:

- **XML files (.xml, .csproj, .props):** Use `<!-- -->` comment style
- **JSON files (.json):** Cannot have comments in standard JSON; 
- **Markdown files (.md):** Do not add a copyright header.
- **PowerShell scripts (.ps1):** Use `#` comment style
- **Batch files (.bat, .cmd):** Use `REM` comment style

---

**Version:** 1.15

**Last Updated:** 2026-09-13

**Maintained by:** Development Team

## Versioned customer consent

- Spec v1.17 §4.3 and Core #19 govern combined authentication. Core resolves `Code`/`Agreement`/`Registration` from the canonical Russian phone, account state and current User Agreement evidence; clients never choose a purpose. Keep `AuthenticationFlowStep` and `CustomerState` as stable numeric enums with Core-owned Russian names/aliases exposed by anonymous `ops` endpoints, and never create lookup tables for them.
- Reactivation updates the existing disabled customer, preserves its ID/profile/history, recomputes profile state with the shared completeness rule, increments `token_version`, revokes every prior refresh session and issues a new family. Customer authorization must compare non-disabled state and token version with the database; refresh must reject disabled customers. Treat pre-migration customer JWTs without the version claim as version zero.
- Authentication receipts bind the hashed canonical phone, resolved flow, optional target customer, exact current legal-document versions and idempotency keys. Verify the code before reading account-sensitive receipt requirements; then re-resolve state and complete evidence/account/session changes atomically under the consent transaction lock. A changed account/document boundary must not issue a session.
- Authentication consent payloads belong to code requests. Keep verification in an independent DTO with forbidden consent fields captured as unvalidated JSON, so nested validation and consent-field conversion cannot preempt the code provider. After successful code verification and before either login or receipt completion, reject non-null consent values (`TermsAccepted = false` remains empty) with `400 invalid_auth_request`; verification must never replace or supplement receipt-bound confirmations. Match field names case-insensitively and preserve `401 invalid_code` precedence through HTTP tests. Preserve verification's phone/code/receipt validation and code requests' typed consent validation.
- Accept only `+7XXXXXXXXXX`, exact `8XXXXXXXXXX`, or `+7` input formatted with ASCII spaces, parentheses and hyphens, and store only `+7XXXXXXXXXX`. The `0_0_9_Login` migration enforces this invariant but must not normalize, deduplicate, scan or report existing phone data.

- Keep `LegalDocumentKind` a stable, append-only numeric enum persisted as PostgreSQL integers and serialized as JSON numbers. Core owns each Russian display name and readable route alias and exposes the identical catalogue from public and staff `ops` endpoints. Do not add string-enum converters, accept legacy string kind codes, reorder/reuse values, or duplicate kind metadata in clients.
- Spec v1.16 §4.18 / CONS-01–07 and Core #20 govern consent. Legal documents live in the database; the obsolete `CustomerConsent`/`ConsentType` models and `customer_consents` rows are removed by migration `20260908181115_0_0_7_CustomerConsents`. Do not retain, import or fabricate legacy evidence; customers without versioned events have missing consent. Every history event identifies its document and content digest. Preserve exact source bytes and frozen canonical HTML/digests. Only Administrator may manage legal documents. Do not expose a general-purpose staff endpoint for browsing a customer's consent history or let staff accept/edit consent evidence.
- A legal document is created once with a required Moscow effective date. Preview rendering must work before a display version is assigned and returns only canonical HTML; assigning or changing the non-rendered display version does not require rerendering, while final creation still requires a non-empty version. Hashes and renderer metadata are generated and retained with the created immutable document. Reject past dates and duplicate `(kind, locale, effective date)` or `(kind, locale, display version)` values. The current document is the greatest effective instant not later than server time and the next change is the earliest future instant. Record successful creation and permitted deletion as append-only audit events containing a complete metadata snapshot; retain those events after deletion. Each document-audit actor is a required `backoffice_users` reference with restricted deletion, while the document ID remains a non-FK logical reference so deletion preserves its audit. Never audit previews or rejected operations.
- Legal-document validation signals a stable problem code for the specific rejected field, file property, or unsupported Markdown construct. Keep the corresponding Russian title and corrective detail in the centralized Problem Details catalogue; do not return a combined list of unrelated upload rules. User-facing Russian sizes use `Кб`.
- Use the consent transaction lock for legal-document creation/deletion, consent changes, withdrawal-request creation/processing and protected writes. Check the current personal-data version before and immediately after a protected action, within the same transaction. Profile and photo writes use `PersonalDataConsentFilter` before model binding/multipart buffering and `WithPersonalDataAsync` in the write transaction; future quote/contact and checkout writes must do the same. Document/request reads, limited authentication, logout and photo deletion stay available.
- Retention runs against configured purpose-specific consent periods; never let removal of a denial revive an older permission. Withdrawal-request records are retained and do not hold or alter consent evidence. See `docs/customer-consents.md` for defaults and rollout.
- `CustomerConsentWithdrawalRequest` contains exactly customer ID, request time and processed flag. Enforce one pending record per customer; retries return it and a new record is allowed after processing. Administrator, Shift manager and Senior operator use the dedicated withdrawal-request policy. Creating or processing a record never writes a personal-data withdrawal event, changes consent/access/account/data, or starts automation. Do not add kind/status enums, ops metadata, assignment, deadlines, notes, evidence, completion metadata, actor or request audit until a later legal/product design explicitly requires them.
- Return the withdrawal queue and legal-document audit through the shared bounded `PagedResult<T>` envelope. Validate page/page size, filter and single-sort allowlists in Core; compute filtered totals and keep filtering, deterministic ordering, `Skip`/`Take` and field projection in `IQueryable`. Never log list searches or customer/document identifiers.
- Stateful integration tests use deterministically seeded EF Core InMemory stores and disable migrations, exchange-rate synchronization and consent-retention workers. They must not read PostgreSQL connection settings or create/drop databases.

- Consent decisions must be explicit; missing `decision` never defaults to grant. For code requests, consume the IP quota, normalize the phone, consume the hashed-phone quota, re-resolve the flow and validate exactly the required documents before provider dispatch. For verification, validate the code before receipt/account requirement disclosure, then revalidate affected versions after persistence before commit. Agreement mismatches identify the agreement artifact; consent persistence failures retain server-error semantics.

- Consume every public authentication IP quota before receipt/account database lookups or phone normalization, and consume hashed canonical-phone quotas before receipt persistence or verification. Verify through the API that withdrawal-request creation and processing leave consent history/status and protected-write access unchanged.

## Order statuses

- Keep `OrderStatus` as the single stable, sparse numeric order-status enum. Exact execution states occupy the approved values in the `300` range; `InProgress` and `Completed` are presentation meanings, not enum members. Do not reuse unassigned values or add a status absent from the product specification.
- Core owns the exact and upper-level Russian names, route aliases, terminal-state marker, and stable presentation progress percentage exposed by anonymous `GET /api/v1/orders/ops`. Clients consume this catalogue for grouping and presentation instead of maintaining their own mappings or deriving progress from catalogue order, and order statuses do not use a lookup table.
- Back-office aggregate filters are metadata, never `OrderStatus` members: `work` (`В работе`) contains every defined exact status from `0` through `380`, while `in_progress` (`Выполняется`) contains every defined exact status from `300` through `380`. Return exact statuses on rows even when an aggregate filter selected them.
- Public order numbers are normalized pairs of the customer's immutable eight-digit `OrderCode` and the order's immutable positive `CustomerOrderNumber`, rendered as `{OrderCode}-{CustomerOrderNumber}` only at API boundaries. Never persist or query a combined `OrderNumber` column, expose it as authorization, derive the customer code from an internal ID, allocate with `MAX`, or recycle a number.
- Assign `OrderCode` lazily during the first order transaction, allocate from the customer row under a lock, and preserve leading zeroes. Enforce its persistence immutability as a one-way `NULL`-to-code transition in application change tracking and PostgreSQL; unconditional EF after-save rejection is incompatible with lazy assignment. Customer creation idempotency keys are customer-scoped, private infrastructure data and must never be logged or returned.
- Orders store non-null UTC `CreatedAt` and `UpdatedAt` values initialized from the injected `TimeProvider`. `CreatedAt` is immutable after insertion; every domain mutation advances `UpdatedAt` without moving it backwards. `Spec v1.15 §4.17`; planning issue [#26](https://github.com/sara-fan/sarafan.spec/issues/26).
- Keep the staff order list read-only and protected by `ManualQuotes`; it remains available with demo phone-suffix authorization because only payments are release-gated. Validate malformed or out-of-range pagination and its one-sort, search, exact/group status and inclusive Moscow creation-date filters with `invalid_order_list_filter`; normalize sort keys case-insensitively and echo their canonical camelCase names. Keep filtering, count, deterministic ordering with internal-ID tie-breakers, compact projection, `Skip` and `Take` in `IQueryable`. Never expose internal order/customer IDs or log searches, URLs, public order numbers, or filter contents.
- `GET /api/v1/orders` returns only authenticated customer's persisted orders (ownership is `WHERE CustomerId = @caller`). Sort customers' rows by `CreatedAt` descending plus internal `Id` descending for deterministic ties. The public item contract is stable and excludes internal identifiers beyond `OrderNumber`; each item contains `Id`, public `OrderNumber`, `Status`, `SourceUrl`, optional `ProductName`, optional `StoreName`, optional `ImageUrl`, optional `SellerPrice`, `Quantity`, and `CreatedAt`. All authenticated customer order reads use `Cache-Control: no-store` so responses cannot cross account changes through a browser cache.

## Retired technical-cookie consent

- Spec v1.20 removes customer cookie consent, category metadata and browser associations. Authentication restoration must not depend on legal Ops or cookie permission; personal-data consent and agreement gates remain enforced.
- Legal kind 0 is retired and permanently reserved; never renumber kinds 1–4. Migration `0_0_11_RetireCookieConsent` irreversibly deletes cookie-only documents, decisions, associations, replay markers and their staff audit, preserving every other record. This is the specific exception to historical cookie-document audit retention.
- Expire incoming legacy `sarafan.consent-browser` cookies at path `/api/v1` for one transition release only. No new browser receipt, banner or acknowledgement is introduced. Future optional tracking requires separate purpose-specific design and applicable consent.
- Core and both interfaces ship together because the removed endpoints and DTO fields are intentionally incompatible with older clients. Never apply the migration to protected local storage without explicit authorization.

- Retain consent replay-key digests atomically while their documents are current and remove them after supersession. Customer idempotency remains subject-scoped. Preserve the latest decision while older same-kind evidence remains retained, including after EvidenceDays changes.
- Return effectiveLocalDate/effectiveTimeZone alongside UTC document dates; enforce registration quotas before onboarding evidence. Keep retention set-based and paged, safe worker diagnostics, the 200-record customer history limit, and UTF-8 editing. Other legal documents, their audit and processed withdrawal requests remain retained indefinitely.
- Service-level access-token failures retain the Bearer challenge contract, without adding it to refresh-token errors. Verify application state transitions in memory; PostgreSQL locking remains outside automated tests. Test hosts use ephemeral data protection rather than machine-owned keys.
