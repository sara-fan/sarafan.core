# Customer checkout API

Pilot implementation of [CHECKOUT](https://github.com/sara-fan/sarafan/blob/b55cffd56b0203f9723766280158231f4f0cea6a/docs/features/006-checkout-and-customer-data/spec.md), reconciliation [#14](https://github.com/sara-fan/sarafan/issues/14).

## Read before saving

`GET /api/v1/orders/{orderNumber}/checkout` requires customer authentication and ownership, returns the existing `OrderDto`, and uses `Cache-Control: no-store`. Missing/foreign orders return `resource_not_found`.

The pricing projection includes the already saved nullable `domesticDeliveryRub`, allowing checkout to show it after the courier address is complete. Null remains unknown; zero is a known zero. This component stays outside `totalRub`. The read neither calculates a new tariff nor saves recipient/address/checkout data or a checkout history event. The existing quote-expiry checks on order reads still apply.

Ordinary list/detail/create responses continue concealing delivery amounts until the order owns a saved checkout. Checkout UI keeps the received amount hidden until delivery and its required address fields are complete.

## Save

`POST /api/v1/orders/{orderNumber}/checkout` retains the existing versioned request and response. Require ownership, an unexpired confirmed QuoteReady order, matching `expectedUpdatedAt`, and current Personal Data Consent and User Agreement acceptance in the shared consent transaction.

Required profile fields are `lastName`, `firstName`, `email`, `passportSeries`, `passportNumber`, `passportIssuedBy`, `passportIssueDate` and `inn`. Blank/whitespace text counts as missing. Keep the existing length, email/INN format and future-date checks. The account phone is server-derived; no second phone, birth date or department code is accepted as a checkout field.

Patronymic remains optional by the owner's instruction, pending specification reconciliation in [kuznetsovrm/sarafan#65](https://github.com/kuznetsovrm/sarafan/issues/65). Partial profile updates outside checkout retain their existing contract.

Current order Ops advertises only `courier`; new saves reject `pickup` with a structured `delivery` field error. Courier requires postal code, city and address. Explicit edits use `expectedDeliveryAddress` and `deliveryAddress` and save profile, snapshot and history atomically. Reusing a saved courier snapshot requires its own complete address; incomplete historical data requires address completion. Profile changes do not silently replace a complete retained order destination.

Historical pickup and incomplete old profile snapshots remain readable. A customer can replace an unpaid, unexpired historical pickup selection with courier, but cannot save another pickup selection. Existing version/consent/expiry conflict and draft-preservation behavior is retained.
