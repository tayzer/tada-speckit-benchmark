# Change reservation quantity

Implement `PATCH /reservations/{id}` according to the inventory HTTP contract. The new total may increase or decrease. An insufficient-stock response preserves both prior values. Return 404 for missing or cancelled reservations and 400 for nonpositive quantities. Keep creation, lookup and cancellation behaviour intact.

Visible acceptance examples: reserve 3 from 10 and resize to 5, leaving 5 units available. A request to resize to 12 returns 409 with quantity 5 and stock 5 unchanged.
