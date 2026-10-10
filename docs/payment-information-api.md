# Payment information bundles

Pilot PAY FR-026/027/029; reconciliation item 1 of [issue #17](https://github.com/sara-fan/sarafan/issues/17).
The customer payment screen and the remaining payment reconciliation items are separate.

## Lifecycle and authorization

Only administrators may access /api/v1/backoffice/payment-information-bundles, including reads and QR resources.
An automatically assigned numeric ID is the bundle number. Contents are editable until first enablement.
Drafts can omit any field; supplied values are validated. Enabling requires complete recipient/bank details, a bank payment link and a static QR image.
Legal entities require 10-digit INN and 9-digit KPP; individual entrepreneurs require 12-digit INN and no KPP.
Accounts contain 20 digits and BIK contains 9 digits. Preserve leading zeroes.

Visible states are draft (Черновик), enabled (Включён), and disabled (Отключён).
First enablement permanently freezes the contents through the persisted published flag. No first-enablement time or staff actor is stored.
Previously enabled bundles can be enabled again. Copying a published bundle creates an editable disabled draft with its QR.
Any inactive bundle can be deleted, including previously published bundles. Enabled bundles cannot be deleted.

## Management API

All responses use no-store. Base path: /api/v1/backoffice/payment-information-bundles.

| Method | Suffix | Contract |
|---|---|---|
| GET | / | Server-paged metadata, capabilities and globally enabled ID/token |
| GET | /ops | Recipient/state catalogues, upload limits and canManage |
| GET | /{id} | Full staff metadata, fields and capabilities |
| GET | /{id}/qr?v={sha256} | Staff-authenticated original image, exact digest required |
| POST | / | Multipart draft creation, returns 201 |
| PUT | /{id} | Multipart draft update, requires version |
| POST | /{id}/copy | JSON {version}, published bundles only, returns 201 |
| POST | /{id}/enable | JSON {version, expectedEnabled} |
| POST | /{id}/disable | JSON {version} |
| DELETE | /{id} | JSON {version}, inactive bundles only, returns 204 |

Multipart fields: recipientType (0=legal entity, 1=individual entrepreneur), recipientName, inn, kpp,
settlementAccount, bankName, bik, correspondentAccount, paymentLink, optional qr file and update version.
Blank fields become null. Omitting qr on update retains the saved image; providing it replaces the image.
Never save payment identifiers as numbers.

Enable requests must explicitly supply expectedEnabled, either null (observed no selection) or {id, version}.
Within the catalogue transaction and advisory lock, the service validates both the target and current selection.
Replacement disables the prior selection before enabling the target; a filtered unique index also enforces at most one enabled row.
Every mutation advances its opaque UUID and updated timestamp. Saving does not enable.
Conflicts (409 payment_bundle_update_conflict) require an authoritative refresh.
Published update attempts return payment_bundle_frozen; enabled deletion returns payment_bundle_enabled.
Missing/empty concurrency tokens return invalid_payment_bundle_version (400).
Field validation uses the standard validation_failed Problem Details with canonical field keys.
QR content never appears in list/detail JSON.

List parameters: page (1–1000000), pageSize (10/25/50/100), sortBy, sortOrder (asc/desc),
search (trimmed, at most 200 characters), state (draft/enabled/disabled or omitted).
Sort keys: id, recipientName, inn, bankName, state, createdAt; default id desc.
The response includes items, pagination, sorting, search, state and enabledBundle.
enabledBundle is global even when search/state filters hide that row.
Search matches recipient, INN, bank, Russian state and Moscow creation date/time as displayed in collapsed rows; the internal bundle number is not displayed or searched.

## Images and links

Accept static PNG, JPEG and WebP, at most 2097152 upload bytes. Ops publishes shared image bounds:
4096 pixels per side, 4194304 pixels per image, and 1048576 decoded PNG metadata bytes.
Shared validation preserves original bytes; PNG is decoded within bounded scanline budgets,
while JPEG/WebP use bounded container/header validation. Payment QR WebP animation is rejected.
Store image behavior, including its existing WebP animation support, is unchanged.

Payment links must be absolute HTTPS without embedded credentials, controls or backslashes, up to 2048 characters.
Preserve the bank-provided path and query. Core never follows the URL or contacts a bank.
Before enablement, the administrator checks that the recipient, link and QR correspond.

## Customer read contract

Authenticated customers use GET /api/v1/payment-information/current.
It returns {paymentInformation:null} when disabled or
{paymentInformation:{bundleId, information, qrUrl}} for the enabled bundle.
The nested information contains recipient and bank fields, with no staff identity or lifecycle metadata.
GET /api/v1/payment-information/current/qr?v={sha256} serves only the currently enabled image with its matching digest.
Drafts and disabled bundles cannot be fetched through customer endpoints.
Both resources use no-store; image responses also use nosniff.

## Verification and migration

The catalogue starts empty without seeded bank details.
Generate migration 0_3_7_Payments through dotnet ef migrations add. Rollback or application against protected local storage requires explicit user authorization.
For this implementation, verification uses EF InMemory and mocked UI tests.
Verification does not run live PostgreSQL tests, migration execution or migration checks.
These tests prove application rules and HTTP/UI contracts, without claiming PostgreSQL locking or rollback verification.
