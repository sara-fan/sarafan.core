# Customer order cancellation API

The authenticated customer may call `POST /api/v1/orders/{orderNumber}/cancel` with JSON `{ "expectedUpdatedAt": "2026-09-24T10:00:00Z", "reason": null }`. The reason is optional; Core trims it, converts blank text to null, and limits it to 2,000 characters. The response is the updated customer order details with `Cache-Control: no-store`.

Customer order detail and creation responses include `updatedAt` (the order version), `canCancel` (server eligibility), and nullable `cancelledAt`. Cancellation is available only in UnderReview, QuoteReady, and QuoteExpired. Cancelled orders return `canCancel=false`; their cancellation time comes from the immutable cancellation history event. Older cancelled orders without an event may return `cancelledAt=null`.

The route uses the public order number and caller ownership. Invalid, missing, and foreign numbers return `resource_not_found`. Stale versions return `order_update_conflict` (409), and other non-cancelled statuses return `order_not_cancellable` (409). Missing version or overlong reason returns `validation_failed` with field errors. Repeating a cancellation returns the current cancelled order without changing its original time or reason.

Cancellation retains the latest pricing snapshot and does not create or change a payment. Client displays of cancelled amounts must treat them as historical values, not amounts payable. The optional reason is available only through staff history detail.
