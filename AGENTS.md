# Agent Guidelines for Sarafan Project

## Specification and repository guidance

- Follow the current specification identified in the [specification README](https://github.com/sara-fan/sarafan.spec#source-of-truth). If an implementation issue conflicts with it, flag the discrepancy before implementing the affected behavior.
- In implementation PR descriptions, cite the governing specification version and section, and link the planning issue. Use any suitable format; the PR template is optional.
- When a change introduces or changes a lasting convention, API contract, domain invariant, security/privacy rule, workflow, or test pattern, update the nearest relevant `AGENTS.md` in the same PR. Keep entries concise and reusable.
- Otherwise, include `AGENTS.md: no durable change` in the PR description.
- Before editing documentation, read its current revision and preserve user-authored changes. Keep product requirements in the specification and task-specific discussion in the issue.
- Before enabling real orders or a real payment-system integration, replace and disable the predictable phone-suffix demo verification mechanism. A build/runtime environment named Production does not satisfy this requirement. Keep this release gate tracked in the [MVP delivery issue](https://github.com/sara-fan/sarafan.spec/issues/26).

## Code Standards and Requirements

### Entity Framework model configuration

- Keep `AppDbContext.OnModelCreating` as the single `ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly)` registration point. Do not add inline entity mappings, feature-specific registration helpers, partial context mapping methods, or a growing manual configuration list.
- Put each mapped entity's complete persistence configuration in one internal sealed `<Entity>Configuration : IEntityTypeConfiguration<Entity>` class, in its own file under `Data/Configurations/<Feature>/`. New entities follow this policy immediately; entity model classes remain free of persistence-mapping attributes.
- Use explicit table and column names, keys, lengths/types/conversions, indexes, concurrency tokens and seed data in the owning entity's configuration. Declare each relationship once, on its dependent/foreign-key entity. Avoid configuration-order dependencies and passes that silently rewrite another entity's metadata; there is no feature-specific naming convention.
- `AppDbContextModelTests` enforce one discoverable configuration per mapped entity, order independence and equivalence with the committed migration snapshot. Their `Sarafan.Core.ModelTests` namespace keeps them outside the integration fixture's database setup. Configuration-only refactors must not create schema migrations or edit snapshots; intentional schema changes require their normal migration and verification on disposable storage.

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
- Mark the bootstrap Administrator as demo until its password is changed. Provision it only through the explicitly enabled, idempotent migration bootstrap; supply credentials through secure runtime configuration, never tracked files or logs. Reject bootstrap and reject startup with any active demo staff account while real orders or real payment integration are enabled, then disable and remove bootstrap credentials after the first successful run.
- Require back-office passwords to contain 8 to 18 characters in API models, service validation, bootstrap validation, UI validation, and user-facing guidance. Do not expose byte-count rules to users; the conservative character maximum keeps passwords within BCrypt's input boundary.

### Official exchange rates

- Synchronize only the official CBR USD/RUB reference rate at startup and daily at 00:10 Europe/Moscow. Keep source-effective date separate from UTC retrieval time; use the provider/pair/date unique constraint to preserve the first observation, including across concurrent instances. Provider failures must not block startup or erase history.
- `/api/v1/backoffice/status` is staff-authorized supplementary data, returns the latest persisted rate with its original nominal and source date, and uses `Cache-Control: no-store`. Keep public health free of FX data. Official rates are not the commercial pricing rate; do not apply spreads or alter pricing here.
- Disable the hosted FX worker in deterministic integration tests (`ExchangeRates__Enabled=false`); never call CBR or use protected local database storage in tests.

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

**Version:** 1.14

**Last Updated:** 2026-09-11

**Maintained by:** Development Team

## Versioned customer consent

- Spec v1.17 §4.3 and Core #19 govern combined authentication. Core resolves `Code`/`Agreement`/`Registration` from the canonical Russian phone, account state and current User Agreement evidence; clients never choose a purpose. Keep `AuthenticationFlowStep` and `CustomerState` as stable numeric enums with Core-owned Russian names/aliases exposed by anonymous `ops` endpoints, and never create lookup tables for them.
- Reactivation updates the existing disabled customer, preserves its ID/profile/history, recomputes profile state with the shared completeness rule, increments `token_version`, revokes every prior refresh session and issues a new family. Customer authorization must compare non-disabled state and token version with the database; refresh must reject disabled customers. Treat pre-migration customer JWTs without the version claim as version zero.
- Authentication receipts bind the hashed canonical phone, resolved flow, optional target customer, exact current legal-document versions and idempotency keys. Verify the code before reading account-sensitive receipt requirements; then re-resolve state and complete evidence/account/session changes atomically under the consent transaction lock. A changed account/document boundary must not issue a session.
- Accept only `+7XXXXXXXXXX`, exact `8XXXXXXXXXX`, or `+7` input formatted with ASCII spaces, parentheses and hyphens, and store only `+7XXXXXXXXXX`. The `0_0_9_Login` migration enforces this invariant but must not normalize, deduplicate, scan or report existing phone data.

- Keep `LegalDocumentKind` a stable, append-only numeric enum persisted as PostgreSQL integers and serialized as JSON numbers. Core owns each Russian display name and readable route alias and exposes the identical catalogue from public and staff `ops` endpoints. Do not add string-enum converters, accept legacy string kind codes, reorder/reuse values, or duplicate kind metadata in clients.
- Spec v1.16 §4.18 / CONS-01–07 and Core #20 govern consent. Legal documents live in the database; the obsolete `CustomerConsent`/`ConsentType` models and `customer_consents` rows are removed by migration `20260908181115_0_0_7_CustomerConsents`. Do not retain, import or fabricate legacy evidence; customers without versioned events have missing consent. Every history event identifies its document and content digest. Preserve exact source bytes and frozen canonical HTML/digests. Only Administrator may manage legal documents. Do not expose a general-purpose staff endpoint for browsing a customer's consent history or let staff accept/edit consent evidence.
- A legal document is created once with a required Moscow effective date. Preview rendering must work before a display version is assigned and returns only canonical HTML; assigning or changing the non-rendered display version does not require rerendering, while final creation still requires a non-empty version. Hashes and renderer metadata are generated and retained with the created immutable document. Reject past dates and duplicate `(kind, locale, effective date)` or `(kind, locale, display version)` values. The current document is the greatest effective instant not later than server time and the next change is the earliest future instant. Record successful creation and permitted deletion as append-only audit events containing a complete metadata snapshot; retain those events after deletion. Each document-audit actor is a required `backoffice_users` reference with restricted deletion, while the document ID remains a non-FK logical reference so deletion preserves its audit. Never audit previews or rejected operations.
- Legal-document validation signals a stable problem code for the specific rejected field, file property, or unsupported Markdown construct. Keep the corresponding Russian title and corrective detail in the centralized Problem Details catalogue; do not return a combined list of unrelated upload rules. User-facing Russian sizes use `Кб`.
- Use the consent transaction lock for legal-document creation/deletion, consent changes, withdrawal-request creation/processing and protected writes. Check the current personal-data version before and immediately after a protected action, within the same transaction. Profile and photo writes use `PersonalDataConsentFilter` before model binding/multipart buffering and `WithPersonalDataAsync` in the write transaction; future quote/contact and checkout writes must do the same. Document/request reads, limited authentication, logout and photo deletion stay available.
- Customer consent and browser permission are independent. Browser association is evidence of observation, never proof that the authenticated customer performed the original anonymous action. Never log text, subject keys, onboarding receipts or raw consent payloads.
- Keep `CookieCategory` as a stable append-only numeric enum and persist category snapshots as PostgreSQL `integer[]`. Core owns Russian category names and required flags and exposes the identical catalogue from public and staff legal-document `ops` endpoints; do not add a lookup table or accept string category codes. Core assigns all required categories to a куки document and none to other kinds; clients never submit legal-document categories.
- Current consent to every required куки category is a prerequisite for ordinary customer-service APIs. Enforce it centrally in Core and fail closed with `cookie-consent-required`; keep only public legal reads, куки status/decision, health, refresh/logout, the customer's own consent/history and withdrawal-request submission available through the gate. Back-office authentication and APIs remain independent. Use `куки` in Russian user-facing text while retaining English technical identifiers.
- Retention runs against configured purpose-specific consent periods; never let removal of a denial revive an older permission. Withdrawal-request records are retained and do not hold or alter consent evidence. See `docs/customer-consents.md` for defaults and rollout.
- `CustomerConsentWithdrawalRequest` contains exactly customer ID, request time and processed flag. Enforce one pending record per customer; retries return it and a new record is allowed after processing. Administrator, Shift manager and Senior operator use the dedicated withdrawal-request policy. Creating or processing a record never writes a personal-data withdrawal event, changes consent/access/account/data, or starts automation. Do not add kind/status enums, ops metadata, assignment, deadlines, notes, evidence, completion metadata, actor or request audit until a later legal/product design explicitly requires them.
- Return the withdrawal queue and legal-document audit through the shared bounded `PagedResult<T>` envelope. Validate page/page size, filter and single-sort allowlists in Core; compute filtered totals and keep filtering, deterministic ordering, `Skip`/`Take` and field projection in `IQueryable`. Never log list searches or customer/document identifiers.
- Integration tests require explicit `SARAFAN_TEST_POSTGRES` pointing to disposable storage and disable exchange-rate and consent-retention workers. Migration round trips use separately created test databases, so they cannot destroy another fixture's data.

- Consent decisions must be explicit; missing `decision` never defaults to grant. For code requests, consume the IP quota, normalize the phone, consume the hashed-phone quota, re-resolve the flow and validate exactly the required documents before provider dispatch. For verification, validate the code before receipt/account requirement disclosure, then revalidate affected versions after persistence before commit. Agreement mismatches identify the agreement artifact; consent persistence failures retain server-error semantics.
- Browser association provenance is the validated customer JWT `jti` (`AuthenticationTokenId`), identifying the exact authenticated access session that first observed a receipt. It is not an authentication credential, never comes from a request body, is not logged or exposed by customer history, and is retained with the association independently of token expiry. Preserve the first observation on retries.
- Require `EvidenceDays >= CookieDays`. Verify duplicate decisions, single-use onboarding and competing document creations with concurrent operations in separate DbContexts on disposable databases; sequential retry tests alone do not prove the locking policy.

- Return `effectiveLocalDate` and `effectiveTimeZone` (`Europe/Moscow`) alongside legal-document UTC effective instants. Registration request quotas must pass before persisting onboarding evidence. Retention evaluates latest decisions set-wise in bounded pages, never with per-event database round trips. Legal documents, their creation/deletion audit events, and processed withdrawal-request records are retained indefinitely. Worker failures include only safe `error.type`; tests freeze the numeric ID, dotted name, severity and message. Apply the 200-record history limit after merging customer and observed-browser evidence. Preserve UTF-8 text when editing through Windows shell pipelines.

- Retention must preserve the latest decision for every consent kind while older same-kind evidence remains retained, including after changing CookieDays/EvidenceDays; cookie denial or expiry must never revive an older grant. Service-level access-token failures must retain the Bearer challenge contract (customer and staff), without adding it to refresh-token errors.

- Disposing consent evidence must atomically retain a compact replay-key digest for documents that are still current; remove these tombstones after supersession, when stale-version validation rejects old grants. Tombstones contain no decision, categories, customer ID or browser receipt. Cookie idempotency keys are unique across browser subjects to prevent replay after storage loss; customer keys remain subject-scoped.

- Consume every public authentication IP quota before receipt/account database lookups or phone normalization, and consume hashed canonical-phone quotas before receipt persistence or verification. Verify through the API that withdrawal-request creation and processing leave consent history/status and protected-write access unchanged.
