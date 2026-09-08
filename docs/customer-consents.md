# Customer consent — Core #20 / specification v1.16

The coordinated implementation is tracked by [spec #30](https://github.com/sara-fan/sarafan.spec/issues/30), [UI #16](https://github.com/sara-fan/sarafan.ui/issues/16), and [back.office #6](https://github.com/sara-fan/sarafan.back.office/issues/6). It supplies versioned consent infrastructure and an MVP queue for a customer's request to stop using the system and withdraw personal-data consent. The queue is an acknowledgement mechanism for manual work outside the application; it is not a claim of complete legal compliance.

## Rollout

1. Apply migration `20260908181115_0_0_7_CustomerConsents` through the established migration process. It creates the versioned consent schema and drops the obsolete `customer_consents` table and rows without importing them. It also creates `customer_consent_withdrawal_requests` directly in its final three-column form. No rights-case or rights-audit schema exists.
2. Deploy Core and both clients together. Existing browsers without a current mandatory куки receipt may use only the legal/consent recovery surface. Existing customers without a versioned personal-data receipt also have missing personal-data consent.
3. As Administrator, create the reviewed User Agreement, personal-data consent, куки consent, privacy policy and order rules as applicable. Until required documents are effective, registration and consent-dependent operations fail closed.
4. Check a fresh browser's куки choice, refusal and withdrawal gates, registration receipt, customer renewal, legal-document audit, and the staff withdrawal queue. Shift leads and Senior operators must see the queue but must not gain legal-document management.

The consent migration is amended in place. An environment that applied an earlier revision requires an explicitly authorized rollback and reapplication. Its Down method recreates only the empty former `customer_consents` table and cannot recover deleted receipts.

## Legal-document contract

`LegalDocumentKind` is a stable numeric enum stored as PostgreSQL `integer` and serialized as JSON numbers. Values are append-only. Core owns Russian names and readable route aliases and returns the same catalogue from public and staff `ops` endpoints.

| Value | Enum | Name | `routeAlias` |
|---:|---|---|---|
| 0 | `CookieConsent` | Согласие на куки | `cookie-consent` |
| 1 | `PersonalDataConsent` | Согласие на обработку персональных данных | `personal-data-consent` |
| 2 | `UserAgreement` | Пользовательское соглашение | `user-agreement` |
| 3 | `OrderRules` | Правила заказа товаров | `order-rules` |
| 4 | `PrivacyPolicy` | Политика обработки персональных данных | `privacy-policy` |

| Principal | Endpoint | Result |
|---|---|---|
| Public | `GET /api/v1/legal/ops` | Kind catalogue and куки category catalogue |
| Public | `GET /api/v1/legal/current/{kind:int}` | `{ document, serverNow, nextChangeAt }` |
| Public | `GET /api/v1/legal/documents/{id}` and `/source` | Effective or superseded immutable document |
| Administrator | `GET /api/v1/backoffice/legal-documents/ops` | The same kind/category catalogue |
| Administrator | `GET/POST /api/v1/backoffice/legal-documents` | List or create immutable documents |
| Administrator | `POST /api/v1/backoffice/legal-documents/preview` | Return canonical HTML without persistence; version may be blank |
| Administrator | `GET/DELETE /api/v1/backoffice/legal-documents/{id}` | Read or conditionally delete a future document |
| Administrator | `GET .../{id}/source`, `GET .../audit` | Exact source and paginated creation/deletion audit |

Creation requires a today-or-future Moscow calendar date. It is stored as the corresponding UTC instant at 00:00 Europe/Moscow. `(kind, locale, effectiveAt)` and `(kind, locale, displayVersion)` are unique. The current document is the greatest effective instant not later than server time; `nextChangeAt` is the earliest future effective instant. Source Markdown is UTF-8 `.md`, at most 256 Кб, and supports headings, paragraphs, lists, emphasis, safe links and tables. Raw HTML, images, embedded resources, code, quotes and unsafe links are rejected with a specific centralized Problem Details response.

Preview returns only canonical HTML. A display version is not rendered, so it may be entered or changed after preview without invalidating it. Kind, locale, title, effective date and source changes require a fresh preview. Final creation requires a non-empty display version and saves the source, canonical HTML, hashes and renderer version once.

`CookieCategory` is a stable append-only numeric enum. The current catalogue contains only `{ value: 0, name: "Обязательные", required: true }`. Category arrays are JSON numbers and PostgreSQL `integer[]`. Core assigns `[0]` to every `CookieConsent` document and `[]` to other document kinds; preview/create requests do not accept category input. A куки grant must include every required category, while refusal and withdrawal contain `[]`.

Successful creation and permitted deletion append a `legal_document_audit_events` record in the same transaction. `actor_id` is a required restricted foreign key to `backoffice_users`; the logical document ID is deliberately not a document foreign key so deleted-document metadata remains available indefinitely. Previews and rejected operations are not audited.

## Consent and withdrawal APIs

| Principal | Endpoint | Result |
|---|---|---|
| Browser | `GET /api/v1/consents/cookies` | Current browser decision, numeric categories, expiry and next change |
| Browser | `POST /api/v1/consents/cookies` | Append a grant, refusal or куки withdrawal |
| Customer | `GET /api/v1/consents/me` | Own status, versioned evidence history, nullable latest `withdrawalRequest`, and next change |
| Customer | `POST /api/v1/consents/me/personal-data` | Append a current-version grant or refusal |
| Customer | `POST /api/v1/consents/me/browser` | Associate the currently observed browser receipt |
| Customer | `POST /api/v1/consents/me/withdrawal-request` | Bodyless creation or return of the existing pending request |
| Administrator, Shift lead, Senior operator | `GET /api/v1/backoffice/consents/withdrawal-requests` | All requests, pending first; processed history remains |
| Administrator, Shift lead, Senior operator | `PUT /api/v1/backoffice/consents/withdrawal-requests/processed` | Idempotently mark exact `{ customerId, requestedAt }` as processed |

The withdrawal DTO is exactly:

```json
{"customerId":42,"requestedAt":"2026-09-08T12:00:00Z","processed":false}
```

Only one pending request may exist per customer. Repeated submission while pending returns it. After staff mark it processed, the customer may create another record. `processed` means staff report that the necessary manual work outside the application is complete. The application stores no request ID, kind, assignee, state code, deadline, notes, evidence, revision, completion time, actor, or request audit event.

Submitting or processing a request does not append a personal-data withdrawal consent event, change the customer's consent status, disable access, block protected writes, delete data, or start automatic processing. Staff must not interpret the queue state as proof that those external actions occurred. An unknown exact request returns `consent-withdrawal-request-not-found` as centralized RFC 9457 Problem Details.

## Mandatory куки service gate

Core checks the current browser receipt for every ordinary customer-service API. A missing, refused, withdrawn, expired or stale receipt, or a grant missing any required category, returns centralized RFC 9457 `cookie-consent-required`. Public legal reads, куки status/decision, health, refresh/logout, the customer's own consent/history and withdrawal-request submission remain available so the visitor can recover. Back-office authentication and APIs are independent of this gate. The customer UI loads ops and куки status before restoring a session or calling ordinary application APIs and has no compiled category fallback.

## Storage and retention

`customer_consent_withdrawal_requests` has exactly `customer_id`, `requested_at`, and `processed`. Its key is `(customer_id, requested_at)`, its customer foreign key is restricted, and a partial unique index on `customer_id WHERE processed = false` enforces one pending request. Processed request records remain indefinitely for this MVP; the consent retention worker does not remove them.

`legal_documents` preserves source bytes and canonical presentation. `consent_events` contains versioned decisions. `consent_associations` records the verified customer session that first observed a browser receipt without turning the browser decision into customer consent. `consent_replay_tombstones` prevents replay after evidence disposal. `consent_onboarding` contains only a keyed phone digest and a short-lived receipt. Legal documents and legal-document audit events remain indefinitely.

| Configuration (`Consents`) | Default | Purpose |
|---|---|---|
| `CookieDays` | 180 days | Browser decision renewal |
| `OnboardingMinutes` | 15 minutes | Temporary registration evidence |
| `EvidenceDays` | 1095 days | Provisional consent evidence retention |
| `RetentionWorkerEnabled` | true | Startup and daily evidence sweep |

Configuration requires `EvidenceDays >= CookieDays`. The worker deletes expired onboarding and eligible consent evidence in bounded pages while preventing removal from reviving an older permission. It does not implement request deadlines, working-day calendars, request holds, account deletion, or external manual work.

## Verification

Run integration tests only with `SARAFAN_TEST_POSTGRES` pointing to explicitly disposable PostgreSQL storage. Verify migration apply/rollback/apply, the three request columns, composite key, restricted customer foreign key, pending-only unique index, and absence of rights-case tables. Run `dotnet test Sarafan.sln --collect:"XPlat Code Coverage"`, `dotnet format Sarafan.sln --no-restore --verify-no-changes`, and both clients' lint, coverage and production builds.
