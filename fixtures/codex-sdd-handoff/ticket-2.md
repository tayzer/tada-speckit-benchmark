# Support change request — resize an order after dispatch

Support ticket 8421. A customer reserved 6 units. Two went out in the first shipment, and the warehouse then short-picked two remaining units. Support tried to reduce the reserved total to 4 and received 409. Support also needs to add replacements when stock is available.

- Update `PATCH /reservations/{id}` to set the new total for any active reservation, including a partially or fully fulfilled reservation. Keep the existing response shape. The new total must be positive and at least the already fulfilled quantity. A total equal to the fulfilled quantity is allowed. Insufficient stock or a positive total below the fulfilled quantity returns 409. The existing 400 and 404 rules continue to apply.
- Apply the change in the reserved total to available stock. Persist the new total and stock atomically. Preserve fulfilled quantity and existing confirmation receipts. Rejected requests leave reservation fields, stock, and receipts unchanged.
- Open and retain a database created by the split-shipment release. The same successful confirmation must replay its original response after restart and after a resize, even when the current reservation total differs from the stored receipt. The first release's other acceptance rules continue to apply. This ticket replaces its restriction on resizing a fulfilled reservation.

Acceptance example. Reserve 6 from 10, then confirm 2. Resizing to 4 returns 200 and leaves 6 available, 4 reserved total, and 2 fulfilled. Resizing to 1 or 11 returns 409 and preserves those values. Replaying the original confirmation still returns the original receipt. Cancelling twice leaves 8 available.
