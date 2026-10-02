# Checkout delivery addresses

Implementation of the owner-approved proposal in [specification issue #40](https://github.com/kuznetsovrm/sarafan/issues/40). The issue proposes amendment of FR-DELIVERY-003; it is not evidence that the specification amendment has merged.

## Options and saved destinations

Anonymous order Ops returns checkoutDeliveries with routeAlias, name, destinationSource and nullable destination. courier uses customer-profile and null destination. pickup uses test-pickup and the fixed test PVZ destination. No private address appears in Ops.

Saved checkout delivery retains the existing routeAlias/name/destination shape. Its destination is a saved snapshot and must not be compared with today's options. Unpaid orders with a valid quote may explicitly replace it; profile updates alone never change it. Historical test-courier snapshots remain readable without migration.

## Address update

PUT /api/v1/customers/me/delivery-address accepts postalCode, city and address and returns the complete CustomerDto. All fields are trimmed and required, with existing maximum lengths 20/150/500. Validation uses validation_failed and field-keyed errors. Authentication and current personal-data consent are required. Only address fields, customer state and UpdatedAt change; recipient, passport and account data are preserved.

## Checkout

POST /api/v1/orders/{orderNumber}/checkout retains its existing request and adds expectedDeliveryAddress (postalCode/city/address) for a new courier selection or explicit replacement of a saved courier address. The UI embeds DeliveryAddressFields beneath the courier choice, reusing profile controls and keeping an in-memory draft alongside recipient/passport fields. Optional deliveryAddress carries explicit edits; a successful checkout saves them to the profile in the same transaction as the recipient, order snapshot and history. Pickup ignores a courier draft and never overwrites the profile address.

Core compares expectedDeliveryAddress with the original current profile under the existing consent/customer lock, treating blank and null fields equivalently. An incomplete explicit draft produces validation_failed with deliveryAddress.postalCode/city/address errors; incomplete profile-only selection or a missing/mismatched expectation produces an error on delivery. Rejected writes save no profile/order/history changes. The client maps errors beneath the shared fields and offers explicit profile-address refresh after stale-address errors before another submit.

Pickup does not require profile address fields. Subsequent checkout saves may change the delivery alias before payment, while QuoteReady with an unexpired confirmed quote. A new courier choice or explicit courier address replacement requires expectedDeliveryAddress and the same validation as first checkout. Keeping courier with no expectedDeliveryAddress retains the original destination and address fields, including historical test-courier data. Every successful change records immutable before/after history; payment, cancellation, and quote expiry block checkout writes. Existing quote/version/consent gates and pricing/history rules remain in force.

Core and customer UI deploy together. No migration is needed. Verification uses disposable EF Core InMemory storage and mocked browser APIs only, with no PostgreSQL tests.

## Staff order details

GET /api/v1/backoffice/orders/{orderNumber} returns nullable delivery with the saved routeAlias/name/destination from checkout. Null means no delivery has been selected, regardless of the profile address. Customer identity and passport data remain in customer; profile postalCode/city/address are omitted from this staff DTO. The order card displays customer/passport information and delivery in two independent CollapsibleSection blocks, using the retained pickup or courier destination. Subsequent profile edits never change staff delivery display; historical test courier snapshots remain readable. Core and back office deploy together for this detail-contract change.
