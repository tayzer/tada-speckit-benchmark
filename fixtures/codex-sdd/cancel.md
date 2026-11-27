# Fix repeat cancellation

A retry of `DELETE /reservations/{id}` currently restores the same units again. A known reservation always returns 204 when cancelled, including retries. Its units become available exactly once. Unknown IDs still return 404. Keep creation, lookup and stock behaviour intact. Resizing is outside this task.

Visible acceptance example: start with 10 units, reserve 3, cancel twice, and observe 204, 204, and 10 available units at the end.
