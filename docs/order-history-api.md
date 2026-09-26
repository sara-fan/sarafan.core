# Staff order history API

All endpoints are under /api/v1/backoffice/orders/{orderNumber}/history, require the existing ManualQuotes policy, and return Cache-Control: no-store. Core service authorization repeats the role check. Unknown/foreign event keys return resource_not_found. No history mutation endpoint exists.

- GET /ops returns numeric kinds, bit-valued affected areas, and actor types with Russian names and stable aliases.
- GET / accepts page (1), pageSize (25; 10/25/50/100), sortBy (timestamp/event/actor), sortOrder (desc/asc), search (actor name, trimmed, up to 200 characters), area (1 creation, 2 product, 4 pricing, 8 status), actorType (0 customer, 100 staff, 200 system), from/to (inclusive Moscow YYYY-MM-DD). Invalid queries use invalid_order_list_filter. The shared page envelope echoes normalized filters/sorting and exposes the complete filtered count.
- GET /{eventKey} returns version=1, the matching event summary, missingCreationDetails, nullable productBefore/productAfter, statusBefore/statusAfter, sourceUrl, pricingBefore/pricingAfter, and validUntilBefore/validUntilAfter. Products and calculations use their existing typed contracts. No internal order/customer identifier is returned.

Each summary contains eventKey, at, kind, areas, actorType, actorName, actorNameHistorical. Event keys are opaque to clients (currently source-number pairs). Names from old product-audit lookups have actorNameHistorical=false; saved names and generic customer/system attribution are true. New events link their immutable evidence and suppress duplicate legacy projections. Legacy product/pricing association requires a one-to-one match on order, time, and actor; uncertain records remain independent.

Pricing GET/PUT/confirm responses no longer contain history. Deploy Core and back-office contract changes together. Financial evidence is read from saved snapshots, independent of live tariff/rate availability. No data rewrite or backfill is needed; the new table is included in the redefined 20260926084356_0_3_0_ServiceCatalogue_2 migration, which is not automatically reapplied to databases where that identity is already recorded.
