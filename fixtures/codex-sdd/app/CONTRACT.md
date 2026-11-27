# Inventory HTTP contract

Each fresh SQLite database starts with 10 available units. Requests with a quantity use the JSON body `{"value": 3}`.

- `GET /health` returns 200 when the API is ready.
- `GET /stock` returns 200 with `{"available": 10}`.
- `POST /reservations` returns 201 with an `id` and `quantity`, reserving that quantity. Nonpositive quantities return 400. Insufficient stock returns 409 without changing state.
- `GET /reservations/{id}` returns 200 with `id` and `quantity` for an active reservation. Unknown and cancelled reservations return 404.
- `DELETE /reservations/{id}` returns 204 for a known reservation and restores its stock exactly once. Later cancellations return 204 with no further state changes. Unknown reservations return 404.
- When the task includes resizing, `PATCH /reservations/{id}` returns 200 with `id` and `quantity`. The quantity is the new total. Apply the increase or decrease and the stock adjustment atomically. Nonpositive quantities return 400. Unknown or cancelled reservations return 404. Insufficient stock returns 409 with both values unchanged.

## Run and test

Use the pinned SDK and locked graphs.

```powershell
dotnet restore Inventory.slnx --locked-mode
dotnet build Inventory.slnx --no-restore
dotnet test tests/AgentTests/AgentTests.csproj --no-build
```

Write new xUnit tests in `tests/AgentTests`. Use `await using var api = await InventoryApi.StartAsync();` inside each test and send HTTP through `api.Client`. Each call launches a separate API and database. The evaluator substitutes prebuilt API binaries through this helper. Keep the project, helper and supplied tests intact. Test observable HTTP responses and state. The evaluator counts test case IDs prefixed by your class or namespace from the TRX output.
