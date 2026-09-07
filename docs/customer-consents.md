<!--
Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
All rights reserved.
This file is a part of the Sarafan application
-->

# Customer consent — Core #20 / specification v1.16

The coordinated change covers [Core #20](https://github.com/sara-fan/sarafan.core/issues/20), [UI #16](https://github.com/sara-fan/sarafan.ui/issues/16), [back.office #6](https://github.com/sara-fan/sarafan.back.office/issues/6), and [spec #30](https://github.com/sara-fan/sarafan.spec/issues/30). This implements consent infrastructure and best-effort rights handling; operator-specific wording, processors, lawful bases and retention still require review under spec #3. No production documents or acceptance records are seeded.

## Rollout

1. Back up the deployment database using its normal operational procedure. Apply migration `20260907125724_VersionedCustomerConsents` through the established deployment migration process. The migration creates the versioned consent schema and drops the obsolete `customer_consents` table and all its rows. No old receipt is copied or linked to a new document. Never use an agent verification run against the workspace `.pgdata` or modify the protected Compose override.
2. Deploy Core and both clients together. Old registration booleans are deliberately insufficient: the client now sends the version/hash before requesting a code, and a short-lived receipt when verifying it. Login and refresh continue to work for existing customers with limited access.
3. Configure retention and the working calendar. Publish the operator's reviewed `user-agreement` and separate `personal-data-consent` as Administrator. Publish cookie consent, privacy policy and order rules as applicable. Until the required texts are effective, registration and consent-dependent writes fail closed, with recoverable errors. Public legal reading, limited authentication and rights requests remain available.
4. Check the saved preview and source download, explicit date/time, one existing account's missing-consent state, and grant/refusal/withdrawal in a fresh browser. A cookie document's category metadata must match its text. No optional integrations currently run in the customer client.

The consent migration is amended in place; no follow-up migration is added. A database that already applied its earlier revision will not rerun it automatically. Such an environment needs an explicitly authorized rollback/reapply or rebuild through the deployment procedure; no live database is changed by this source update. Down recreates only the empty former table and cannot recover deleted receipts.

Deploying an older binary after new evidence exists is not a supported rollback: it could bypass consent guards. Prefer a forward fix or restrict affected writes while retaining legal and rights access. The migration's Down path is for disposable round-trip verification, not production evidence disposal.

## API

All JSON requests use `Accept: application/json, application/problem+json`; mutations use JSON content type. Responses use server UTC instants. All legal/consent endpoints are `no-store`. Source downloads use `text/markdown; charset=utf-8`, attachment disposition and `nosniff`. Identifiers are UUIDs; a display version is a human label, not the authorization key.

| Principal | Endpoint | Result |
|---|---|---|
| Public | `GET /api/v1/legal/current/{kind}` | `{ document, serverNow, nextChangeAt }`; document may be null |
| Public | `GET /api/v1/legal/documents/{id}` and `/source` | Exact effective/superseded artifact; draft/future 404; disposed content 410 |
| Browser | `GET /api/v1/consents/cookies` | Current browser decision, enabled categories, version, expiry and next activation |
| Browser | `POST /api/v1/consents/cookies` | Append grant/refuse/withdraw; essential HttpOnly receipt cookie |
| Customer | `GET /api/v1/consents/me` | Customer ID, server time, status, evidence history and rights cases |
| Customer | `POST /api/v1/consents/me/personal-data` | Append current-version grant/refuse |
| Customer | `POST /api/v1/consents/me/browser` | Associate only the currently observed browser receipt, 204 |
| Customer | `POST /api/v1/consents/me/rights` | Idempotent withdrawal/stop-processing case and immediate denial of consent-based processing |
| Administrator | `GET/POST /api/v1/backoffice/legal-documents` | List/create draft (list optional `kind`) |
| Administrator | `GET/PUT /api/v1/backoffice/legal-documents/{id}` | Read/edit draft with optimistic revision |
| Administrator | `POST .../{id}/publish`, `POST .../{id}/cancel` | Publish now/schedule or cancel future publication |
| Administrator | `GET .../{id}/source`, `GET .../{id}/audit` | Exact bytes and publication audit |
| Administrator | `GET /api/v1/backoffice/consents/customers/{id}` | Read-only customer evidence |
| Administrator | `GET /api/v1/backoffice/consents/rights` | Earliest open deadlines first; optional `customerId` |
| Administrator | `PUT /api/v1/backoffice/consents/rights/{id}` | Assign, record lawful retention/result, extend eligible demand, complete |

Kinds: `cookie-consent`, `personal-data-consent`, `user-agreement`, `order-rules`, `privacy-policy`; locale `ru`. Cookie optional categories are `analytics` and `marketing`. No category is granted by authentication or by publishing a document.

Draft request:

```json
{"kind":"personal-data-consent","locale":"ru","title":"Согласие на обработку персональных данных","displayVersion":"2026-09-07.1","fileName":"consent.md","source":"BASE64_OF_EXACT_UTF8_BYTES","cookieCategories":[],"revision":0}
```

Create returns revision 1. Draft edits submit the latest revision. Publication is either `{"revision":1,"now":true}` or `{"revision":1,"effectiveDate":"2026-09-10"}`. Date-only means 00:00 Moscow, `2026-09-09T21:00:00Z` in this example. Every document DTO returns `effectiveLocalDate` (`2026-09-10` here) and `effectiveTimeZone` (`Europe/Moscow`) alongside `effectiveAt`. Drafts have null instant/date; immediate publication reports its Moscow calendar date. Activation is evaluated from database state and server time, not a job. Cancellation requires `{"revision":2}` and is only possible before activation. Text cannot be edited after publication; create a new draft for corrections. At most one next scheduled version exists per kind/locale, and simultaneous effective instants are rejected. Decision and onboarding transactions revalidate versions after saving, before commit, so crossing a scheduled activation rolls back stale evidence. Agreement-only changes return the agreement as the required artifact.

Decision request (IDs/hashes must come from the displayed server artifact):

```json
{"documentId":"11111111-1111-1111-1111-111111111111","contentHash":"64_LOWERCASE_HEX_CHARACTERS","decision":"grant","categories":["analytics"],"idempotencyKey":"22222222-2222-2222-2222-222222222222"}
```

The `decision` field is mandatory and has no implicit grant default. Use no categories for personal-data decisions, refusal or withdrawal. Reusing a key with different content is 409. Exact retries do not append evidence. Cookie keys are unique across browsers: reuse from a different receipt is a conflict, never a new grant. After evidence disposal, replay of a key for a still-current document returns 409 `consent-conflict`; a new explicit decision must use a fresh key. Withdrawal of a browser's previous grant remains possible after a newer document activates. An unknown browser has no permission. Cookie subject keys are opaque digests; a lost first response uses a stable keyed derivation from its random idempotency key, so retry does not create a second anonymous subject. Raw IP/User-Agent are not collected in consent evidence.

Registration requests `POST /api/v1/auth/code/request` with `{ phone, purpose:"register", termsAccepted:true, termsDocumentId, personalDataConsent:<decision> }`. The exact current PD document and agreement are validated before phone normalization or requesting a code. Verification validates receipt freshness and document versions before phone normalization or code verification. Both IP and phone request quotas must pass before onboarding evidence is persisted. The 202 response contains `{ onboardingToken }`. Keep this receipt in memory and pass it to `/auth/code/verify` with phone, purpose, code and `termsAccepted:true`. Core binds the receipt to the normalized phone via HMAC, checks expiry/single use and checks both versions again in the customer-creation transaction. The agreement and separate PD acceptance retain their own exact artifacts. Login requests do not grant consent.

Typical problems are `.../consent-version-changed` (409, extensions `requiredDocumentId` and `consentKind`), `.../personal-data-consent-required` (409), `.../consent-document-unavailable` (409), `.../consent-conflict` (409), `.../invalid-legal-document` (400), and `.../onboarding-consent-expired` (400), under `https://sarafan.sw.consulting/problems/`. Use canonical `type`, not Russian text. These recoverable failures must retain entered forms and limited legal/rights access.

Lists expose at most 200 newest records (rights queues prioritize earliest unresolved deadlines); the database retains all evidence under the retention policy. Direct immutable document lookup remains available. Large-scale pagination/export and separate law-enforcement holds are later extensions, not a staff evidence-editing API.

## Operation and basis matrix

| Operation | Implemented requirement / separate basis |
|---|---|
| Public documents, necessary receipt/auth storage | Available without optional cookie permission; essential storage inventory below |
| Optional analytics/marketing | Current browser category grant, before resource start; cleanup on denial/expiry/foreground revalidation |
| New registration and phone-code delivery | Exact separate PD consent and agreement before sending the code; short-lived onboarding evidence |
| Existing login, refresh, own profile read, legal/consent history, logout | Limited session supports existing relationship and exercise of rights; does not enable new consent-based writes |
| Profile save and photo upload | Current customer PD grant checked by a resource filter before model binding/multipart buffering, then again inside the transaction before commit |
| Photo deletion and rights request | Remain available when missing, outdated, refused or withdrawn |
| Quote/contact/checkout persistence | These branches have no operational quote/checkout write API yet. Future endpoints must call `ConsentService.WithPersonalDataAsync` or document a separate applicable legal basis; existing placeholder UI does not submit orders |
| Existing contractual/tax/claims obligations | Consent withdrawal does not erase independent obligations. Administrator records specific data, basis, duration and completion evidence in the rights case; no blanket “contract” exemption for all processing |

## Storage and retention

`legal_documents` keeps exact Markdown bytes, frozen canonical HTML, both SHA-256 digests, renderer version, category catalog, effective/publication dates and revisions. `consent_events` is append-only for business APIs. `consent_associations` records who observed an anonymous browser receipt, when, and the UUID `jti` of the validated customer access token (`authentication_token_id`). This preserves the exact authenticated access-session provenance without storing a bearer token or retroactively attributing the original anonymous action. Retries retain the first observation; provenance is not returned in normal history DTOs or diagnostic logs. `consent_replay_tombstones` contains only a replay-key digest and document reference after evidence disposal, with no decision, categories, customer ID or browser receipt. These minimal integrity markers remain only while the document is current, and the next sweep deletes them after supersession; stale-version checks then reject old grants. They never authorize processing or appear in consent history. `consent_onboarding` contains only a keyed phone digest and hashed receipt. `consent_rights_cases` and `legal_audit_events` track execution. The former `customer_consents` table, data and runtime models are removed. Customer histories contain only versioned events with a non-null document ID and content digest. Existing customers start with missing consent and an empty history until they make a new decision; limited authentication and rights access remain available.

| Configuration (`Consents`) | Default | Purpose |
|---|---|---|
| `CookieDays` | 180 days | Browser decision renewal; changed version may require earlier renewal |
| `OnboardingMinutes` | 15 minutes | Temporary evidence before verified account creation |
| `EvidenceDays` | 1095 days | Provisional evidence/dispute retention, not a statutory universal period |
| `DraftDays` | 90 days | Unreferenced abandoned draft content |
| `RetentionWorkerEnabled` | true | Startup and daily retention sweep |
| `NonWorkingDates`, `WorkingDates` | 2026 extra days / empty | ISO date overrides for Moscow deadline calculation |

Configuration requires `EvidenceDays >= CookieDays` so retention cannot shorten an advertised cookie lifetime. The sweep deletes expired onboarding records, aged non-operational decisions, old completed rights cases and audit events. Open rights cases hold related customer and observed-browser evidence. A latest current PD grant stays while it authorizes an enabled account. For every consent kind, the sweep preserves the latest decision while older same-kind evidence remains retained. Cookie refusal, withdrawal or expiry cannot revive an older grant after retention settings change. Unreferenced obsolete/draft document contents are disposed with an audited tombstone; currently effective and future documents and legally held evidence are protected. Staff author identity is removed from disposed tombstones. Evidence and artifact candidates are processed in pages of at most 1,000 IDs; holds and references are evaluated set-wise in SQL. Artifact content is cleared by bulk update and disposal audit is saved in the same transaction. Retention does not claim to dispose independent order/payment data or backups; their periods and processor procedures belong to the operator's inventory.

Withdrawal defaults to 30 calendar days for case handling; consent-based operations stop immediately. A distinct stop-processing demand is due after ten working days, with one reasoned extension by five working days before the original deadline. Extension and completion must be separate requests so each transition has its own audit entry. Completion requires evidence; it records performed work and does not itself delete a customer. The customer sees the result, retention basis and extension reason in the consent center.

The calendar uses Moscow dates, weekends, federal recurring holidays and validated explicit overrides. Defaults include the 2026 extra non-working dates January 9, March 9, May 11 and December 31. Annual government transfers must be maintained for subsequent years; incomplete future overrides produce an earlier operational deadline, not an entitlement to a longer period. The January/December transfers follow [Government Resolution 1466](https://government.ru/docs/all/161028/); see the specification's legal baseline for rights requirements.

Essential browser storage: `sarafan.consent-browser` is HttpOnly, SameSite Strict, Secure when deployed over HTTPS, scoped to `/api/v1/consents`, and lives for `CookieDays`; it stores an opaque reference to the user's choice. Existing refresh cookies are for authentication; access tokens stay in memory. No analytics, marketing SDK, external font/pixel or consent payload in localStorage is introduced.

Customer status returns `serverNow` and `nextChangeAt` for the next personal-data activation. The customer UI refreshes at that boundary and on foreground recovery, and preflights profile/photo writes against current server consent. A stale acceptance reloads the document and resets the affected checkbox without submitting the new version automatically.

## Verification

Use an explicitly disposable PostgreSQL server in `SARAFAN_TEST_POSTGRES`; tests create and drop UUID-named databases and disable periodic workers. Run `dotnet test Sarafan.sln --collect:"XPlat Code Coverage"`, `dotnet format Sarafan.sln --no-restore --verify-no-changes`, and both clients' lint, coverage and production build scripts. Consent tests cover version boundaries, retries, deletion of obsolete consent data on migration, safe Markdown, rights deadlines, isolation and role denial. No test needs the user's local application stack.
