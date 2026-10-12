# Customs register integration with Logibooks

[Implementation plan: Core #65](https://github.com/sara-fan/sarafan.core/issues/65).

Staff UI names this feature «Таможенный реестр». Recognized saved operation failures use current catalogue wording on reads without rewriting retained evidence. Administrator and Shift manager may create registers from **Paid (300)** orders. Each order must have an actual positive USD seller **unit** price with at most two decimals, up to 99,999,999.99. Selection is limited to **1000 orders**. Created and unresolved rows reserve their orders; definitive rejected rows may be selected again. Registration advances the order version and records history without changing its status.

## Configuration and rollout

Apply the reviewed **0_4_0_Logibooks** migration through the normal deployment process. It adds only the operation, result-row, immutable receipt and reconciliation tables. It has not been applied by agent verification.

The checked-in Sarafan.Core/appsettings.json includes the complete Logibooks section, disabled with a placeholder BaseUrl, Identifier=Sarafan and an empty password. Supply the deployment URL (without /api) and credentials through secure configuration overrides (for example Logibooks__BaseUrl, Logibooks__Identifier and Logibooks__Password), then set Logibooks__Enabled=true. Configure Core through the deployment's existing secure configuration mechanism:

```json
{
  "Logibooks": {
    "Enabled": true,
    "BaseUrl": "https://logibooks.example",
    "Identifier": "Sarafan",
    "Password": "SUPPLY_SECURELY",
    "DeadlineSeconds": 120,
    "OutboundBytes": 33554432,
    "ResponseBytes": 4194304
  }
}
```

BaseUrl is the deployment base **without /api**, with an optional deployment path. Credentials are runtime secrets and never enter operation snapshots, browser responses or logs. Provision an automated Logibooks account with the AdapterRegister role (1026); Logibooks must already contain GTC company 8 and country 643. That role grants creation, so external investigation uses an appropriately authorized human account.

The non-concurrent Quartz job uses ScheduledJobs:LogibooksRegisters, default cron `0/5 * * * * ?`, Europe/Moscow, RunOnStartup=true. Disabling Logibooks pauses new dispatches and preview/admission; existing reads, identical UUID replay, reconciliation and local receipt application remain available. A queued operation retains its deployment target and waits when the configured target differs.

DeadlineSeconds is bounded to 1–3600; body limits may be lowered to 1024 bytes and cannot exceed the shown defaults. Incoming commands are bounded to 1 MiB; labels to 200 characters. Verify against disposable InMemory stores and disconnected SQL generation. Do not apply migrations or run tests against protected user PostgreSQL storage.

## Frozen demo mapping

- fileName defaults to `Sarafan-<Moscow yyyyMMdd-HHmmss>-<first 8 operation UUID characters>`; the staff editor retains the generated filename automatically without an editing control.
- Import=40, Road=1, CPT=2; no airports, warehouse or counterparty invented.
- One goods row per order; postingNumber is its public order number. Quantity is retained. unitPrice is the saved USD unit price, with no conversion or total-price fallback.
- weightKg=1.000 per unit; realWeightKg is the sum of weight × quantity.
- tnVed=1000000000, countryCode=senderCountryCode=tradingCountryCode=643, sender=Sarafan Demo.
- Name/link come from the order snapshot; a missing name receives a demo description. Phone comes from the customer's canonical account phone. Optional recipient/passport data comes only from saved checkout.
- Saved delivery destination is authoritative. Structured postal code/city/address are sent only for courier checkout whose saved profile fields reproduce that destination exactly. Otherwise the complete saved destination is sent as address, with postal code/city omitted. Current profile addresses never replace checkout evidence.
- Missing optional values are omitted. No customer-facing payment action is triggered.

## Staff API

All paths below are under `/api/v1/backoffice/logibooks-registers`, require ManageLogibooksRegisters at HTTP and service boundaries and return no-store.

| Method/path | Contract |
|---|---|
| GET /ops | Enablement, capability, limits, mapping version, demo defaults, state labels and USD unitPriceCurrency metadata (value/name/routeAlias/symbol) |
| POST /preview | `{operationId, fileName?: null|string, orders:[{orderNumber,expectedUpdatedAt}], register?: {dealNumber?, customerOrderNumber?, invoiceNumber?, invoiceDate?}}` → frozen register/parcels, normalized label and fingerprint |
| POST / | Preview selection + `fileName, mappingVersion, fingerprint` → **202** operation metadata and Location |
| GET / | Server list: page, pageSize (10/25/50/100), search (200 chars), state, sortBy (createdAt/fileName/state), sortOrder |
| GET /{id} | Lightweight operation metadata, counts, version, attemptId and optional retained RFC 9457 failure |
| GET /{id}/details | Frozen register metadata, read separately from polling; includes the four optional register fields |
| GET /{id}/rows | Separate paged rows with frozen goods, outcome, parcelId/statusId/checkStatus and issues; page/pageSize/search |
| POST /{id}/reconcile | Versioned, audited classification of unresolved rows after external investigation |

Order Ops must supply logibooksMaximumOrders when logibooksEnabled is true. Both order and register Ops limits must be safe integers from 1 through 1000. Goods tables use register Ops unitPriceCurrency.symbol immediately after the Russian-formatted unit price (for example, 12,34$), and row search matches that same text. Adapter payload currency remains USD.

Preview and create accept the same optional `register` object with dealNumber, customerOrderNumber, invoiceNumber and invoiceDate. Optional text is trimmed and blank becomes unset; invoiceDate is a calendar date in YYYY-MM-DD format. The top-level fileName is unchanged. Normalized metadata participates in both preview fingerprint and immutable command intent. An omitted, null or entirely empty extension preserves version-one canonical bytes and replay hashes; mappingVersion and Ops are unchanged. Existing frozen JSON remains readable without a migration.

The editor shows only the four optional register fields and calculated total weight in a compact aligned layout. Import=40, Авто=1, CPT=2 and GTC recipient with Russia as destination stay fixed and hidden. The filename is generated automatically and has no editing control; the top-level API field remains supported for compatibility and immutable retries. Register sender company/country, airports and warehouse are unset; lookupByArticle remains false. Edits stay in session memory and retain visible parcel rows. Create automatically rebuilds a stale preview, validates it and then freezes and submits the complete command; no separate Refresh action is required after editing. Failed preview preparation retains edits and permits retry without freezing a command. Once sent, the command stays frozen through uncertain outcomes, and retries resend it unchanged without rebuilding the preview. The eye action is removed; the optional Refresh action still rebuilds the preview or recovers a frozen operation. Saved details use the same panel in read-only mode.

The preview/result parcel table follows the GTC reference input-column order. Missing optional values display an em dash; sender contact/address defaults remain unset. Passport displays series, number, “выдан”, issuer and a Russian-formatted calendar date when available. Currency cells use the existing Core currency symbol. Results retain their outcome, remote parcel ID, issues and reconciliation action after the common data columns.

Admission compares the exact opaque order versions and preview fingerprint while holding deterministic order locks. UUID plus canonical intent is durable idempotency: identical intent replays before checking current eligibility/enablement; changed intent conflicts. No network request occurs inside admission or receipt/application database transactions.

Public states: Submitting, Succeeded, PartiallySucceeded, Rejected, Unknown. Internal phases: Queued, Dispatching, ReceiptRetained, Applied. Metadata contains no frozen recipient payload. Rows use Unresolved, Created and Rejected. Search matches literal displayed values, including every displayed goods, recipient and sender attribute, the compound passport value, formatted USD unit price/currency, numeric country codes, quantity, weight, outcome, parcel ID and joined issue messages, before count/page.

A receipt is retained in its own transaction before local order/history writes. Accepted rows get one linked LogibooksRegistered=1300 / area=64 / version=6 history event with unchanged before/after status, original staff attribution, operation/register/parcel IDs and optional reconciler attribution. Local failures retry only the saved receipt.

## Uncertain outcomes and reconciliation

Logibooks provides no create idempotency. A persisted attempt marker precedes HTTP dispatch. Restart after dispatch, deadline expiry, transport/lost response, malformed results and unproven 5xx produce **Unknown**, keep orders reserved, and never automatically resend. The sole exception is one explicit, validated 401 response: authorization to refresh/retry is durably recorded before a second login/create attempt.

201 responses must cover every input row once, in order, with consistent counts, positive unique created parcel IDs and a positive register ID. 422 must prove no creation. Documented pre-creation rejection statuses with the documented envelope are definitive. Duplicate/customs check flags on Created rows are still successful creation.

Reconcile body:

```json
{
  "version": "<current operation UUID>",
  "attemptId": "<original attempt UUID>",
  "registerId": 101,
  "originalAttemptFinished": true,
  "note": "External evidence and operator's conclusion",
  "rows": [{"rowNumber": 1, "created": true, "parcelId": 1001}]
}
```

Only unresolved rows may be classified. Created requires one consistent positive register ID and unique positive parcel IDs; noncreation requires parcelId=null. Every reconciliation requires a nonblank note (up to 2000 chars) and confirmation that the original attempt finished. A deadline alone never supplies that proof. The browser reconciles one row at a time; the API supports a subset of rows.

Late receipts are retained and compared against both applied results and definitive receipts awaiting local application. Contradictions preserve existing evidence and place an investigation hold on affected orders. Queued exports cannot dispatch through that hold; routine reconciliation cannot clear it. Investigate externally before an explicitly reviewed repair.

Stable problems: invalid_logibooks_register, logibooks_disabled, logibooks_preview_changed, logibooks_update_conflict, logibooks_rejected, logibooks_auth_failed, logibooks_unknown, logibooks_conflicting_evidence. Browser and service failures remain RFC 9457; remote envelopes are private adapter input.
