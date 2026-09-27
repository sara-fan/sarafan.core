# Customer pricing API

Issue [sarafan.ui #35](https://github.com/sara-fan/sarafan.ui/issues/35) extends the customer order contract. The governing pricing decisions are in PRICE and the pricing reconciliation linked from that issue. Customer-entered valid USD prices may be used for a preliminary forecast; the forecast is indicative and does not reserve a price.

`POST /api/v1/orders/forecast` is anonymous, returns `Cache-Control: no-store`, and accepts `sellerPrice` (`{ amount, currency }` or `null`) and `quantity` (1–4). A missing price yields a successful unavailable forecast. Invalid prices and quantities produce structured field errors. The request does not create an order, number, snapshot, or history event. It uses the same calculator and current catalogue and rate inputs as order creation. Automatic integrations requiring a persisted order are not called.

Customer order list, detail, and creation responses each include `pricing`:

| Field | Meaning |
| --- | --- |
| `state` | `0` Forecast, `100` Confirmed, `200` Expired; aliases and names are published through anonymous order Ops. |
| `totalRub` | Nullable customer total in RUB. Zero is a calculated amount. |
| `calculatedAt` | Nullable timestamp of the saved calculation. |
| `validUntil` | Nullable confirmation deadline. Required with Confirmed or Expired. |
| `asOf` | Server response time for expiry decisions. |
| `domesticDeliveryRub` | Calculated RUB amount when known, otherwise `null`; excluded from `totalRub`. |
| `customsRub` | Calculated RUB amount when known, otherwise `null`; excluded from `totalRub`. |

Saved pricing comes from the latest immutable order snapshot. Reads never recalculate or persist. An order without pricing evidence has an unavailable forecast. Domestic delivery and customs are excluded from the main total by the service catalogue's inclusion rule, whether their amounts are known or unknown. Internal components, tariffs, margins, and exchange-rate evidence are absent from this customer DTO. Ownership filtering and `no-store` apply to all customer order reads.
