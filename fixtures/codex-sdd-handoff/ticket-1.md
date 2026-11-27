# Warehouse change request — confirm split shipments

The dispatch service confirms a reservation as boxes leave the warehouse. One reservation may be dispatched in several shipments. The sender retries when it loses a response.

```text
09:12:04 dispatch shipment-481 reservation=R-18 quantity=2 confirmation sent
09:12:09 dispatch shipment-481 reservation=R-18 confirmation response timed out
09:12:14 dispatch shipment-481 reservation=R-18 quantity=2 confirmation retried
```

- Add `POST /reservations/{id}/fulfill`. The body uses the existing `{"value": 2}` shape. Require one nonblank `Idempotency-Key` header and a positive increment. Invalid input returns 400. Return 200 with `id`, `quantity` (reserved total), and `fulfilledQuantity` (cumulative). Return `fulfilledQuantity` on a successful reservation lookup alongside its existing fields.
- A confirmation uses units already reserved. Available stock stays unchanged. Return 404 for a missing or cancelled reservation when there is no previous successful receipt for the key. Return 409 when an increment exceeds the unfulfilled quantity. Rejections leave state unchanged and do not create a successful receipt.
- Scope receipt keys to one reservation. Replaying a successful key with the same increment returns that receipt's original 200 body, including after restart or cancellation. The same successful key with a different increment returns 409. Concurrent deliveries of the same key may both succeed, but the increment applies once.
- Cancellation releases just the unfulfilled units, including zero for a fully fulfilled reservation. Preserve the 204 response on repeated cancellation. An active reservation with any fulfilled units returns 409 for a valid resize request. Unfulfilled reservations retain the existing resize rules.
- On startup, upgrade an existing inventory database in place and preserve its rows, identifiers, available stock, and cancellation state. Fulfilled quantity starts at zero for legacy reservations. Keep existing client behaviour and package locks.

Acceptance example. Start with 10 units. Reserve 6 and confirm 2 with key `shipment-481`. Replaying leaves 2 fulfilled and 4 available. Resizing to 4 returns 409 with the state unchanged. Cancelling twice leaves 8 units available.
