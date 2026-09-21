# Service catalogue back-office API

`/api/v1/backoffice/service-catalogue` is the Core-owned staff contract for service pricing metadata. All authenticated staff may read entries, Ops and audit history. Only Administrators may create, update or delete entries. Both the endpoint policies and the service layer enforce that matrix.

`GET /ops` is the only client source for service, price-method and audit-action numeric values, Russian labels and route aliases. Its currency catalogue intentionally contains only RUB (`643`) and USD (`840`); the global `Currency` enum still contains EUR for unrelated exchange-rate and order-limit features. Ops also publishes numeric limits, the `Europe/Moscow` availability timezone and caller capabilities.

Entries support three mutually exclusive shapes:

- Percent: `percentage` is greater than zero and at most 100 with four fractional digits; optional nonnegative `minimumAmount` and `maximumAmount` are USD store-price caps, and `currency`/`amount` are absent.
- Fixed: nonnegative `amount` with two fractional digits and a required RUB or USD `currency`; percent fields are absent.
- Manual: required RUB or USD `currency`; all catalogue amount and percent fields are absent.

`availableFrom` and optional `availableBy` are inclusive Moscow calendar dates. A missing end date is unbounded. Periods for the same service cannot overlap, including at a shared boundary date. PostgreSQL enforces this with the `ex_service_catalogue_entries_service_period` exclusion constraint; Core performs the same check first for useful field errors and non-relational tests.

`POST /`, `PUT /{id}` and `DELETE /{id}` serialize through one catalogue advisory transaction lock. Update and delete require the opaque current UUID `version`. Every successful mutation stores the entry and an immutable audit event in one transaction using one server-clock snapshot. Create events expose only `after`, updates expose typed `before` and `after`, and deletes expose only `before`; audit remains after the entry is deleted.

`GET /audit` uses the shared paged envelope. Page sizes are 1–100. Filters are numeric `service`, numeric `action`, positive `entryId`, and an actor-name or entry-ID `search` of at most 200 characters. Sorts are `timestamp`, `service`, `action`, or `actor`, with `asc`/`desc`. Invalid filters and mutations use Russian RFC 9457 Problem Details with stable catalogue codes and field mappings.
