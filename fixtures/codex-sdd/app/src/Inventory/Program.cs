using Microsoft.Data.Sqlite;

var app = WebApplication.Create(args);
var database = Environment.GetEnvironmentVariable("BENCHMARK_DATABASE")
    ?? throw new InvalidOperationException("BENCHMARK_DATABASE is required.");
var connectionString = new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString();

using (var connection = Open())
{
    using var init = Command(connection, null, """
        CREATE TABLE IF NOT EXISTS stock (id INTEGER PRIMARY KEY CHECK (id = 1), quantity INTEGER NOT NULL);
        INSERT OR IGNORE INTO stock VALUES (1, 10);
        CREATE TABLE IF NOT EXISTS reservations (id TEXT PRIMARY KEY, quantity INTEGER NOT NULL, active INTEGER NOT NULL);
        """);
    init.ExecuteNonQuery();
}

app.MapGet("/health", () => Results.Ok());
app.MapGet("/stock", () =>
{
    using var connection = Open();
    return Results.Ok(new { available = Stock(connection, null) });
});
app.MapGet("/reservations/{id}", (string id) =>
{
    using var connection = Open();
    var reservation = Find(connection, null, id);
    return reservation is { Active: true } ? Results.Ok(new { id, quantity = reservation.Quantity }) : Results.NotFound();
});
app.MapPost("/reservations", (Quantity request) =>
{
    if (request.Value <= 0) return Results.BadRequest();
    using var connection = Open();
    using var transaction = connection.BeginTransaction();
    if (request.Value > Stock(connection, transaction)) return Results.Conflict();
    string id = Guid.NewGuid().ToString("N");
    using var insert = Command(connection, transaction, "INSERT INTO reservations VALUES ($id, $quantity, 1)");
    insert.Parameters.AddWithValue("$id", id);
    insert.Parameters.AddWithValue("$quantity", request.Value);
    insert.ExecuteNonQuery();
    AddStock(connection, transaction, -request.Value);
    transaction.Commit();
    return Results.Created("/reservations/" + id, new { id, quantity = request.Value });
});
app.MapDelete("/reservations/{id}", (string id) =>
{
    using var connection = Open();
    using var transaction = connection.BeginTransaction();
    var reservation = Find(connection, transaction, id);
    if (reservation is null) return Results.NotFound();
    if (!reservation.Active) return Results.NoContent();
    using var cancel = Command(connection, transaction, "UPDATE reservations SET active = 0 WHERE id = $id");
    cancel.Parameters.AddWithValue("$id", id);
    cancel.ExecuteNonQuery();
    AddStock(connection, transaction, reservation.Quantity);
    transaction.Commit();
    return Results.NoContent();
});
// BEGIN RESIZE
app.MapPatch("/reservations/{id}", (string id, Quantity request) =>
{
    if (request.Value <= 0) return Results.BadRequest();
    using var connection = Open();
    using var transaction = connection.BeginTransaction();
    var reservation = Find(connection, transaction, id);
    if (reservation is not { Active: true }) return Results.NotFound();
    int delta = request.Value - reservation.Quantity;
    if (delta > Stock(connection, transaction)) return Results.Conflict();
    using var update = Command(connection, transaction, "UPDATE reservations SET quantity = $quantity WHERE id = $id");
    update.Parameters.AddWithValue("$id", id);
    update.Parameters.AddWithValue("$quantity", request.Value);
    update.ExecuteNonQuery();
    AddStock(connection, transaction, -delta);
    transaction.Commit();
    return Results.Ok(new { id, quantity = request.Value });
});
// END RESIZE
app.Run();

SqliteConnection Open()
{
    var connection = new SqliteConnection(connectionString);
    connection.Open();
    return connection;
}

static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
{
    var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    return command;
}

static int Stock(SqliteConnection connection, SqliteTransaction? transaction)
{
    using var read = Command(connection, transaction, "SELECT quantity FROM stock WHERE id = 1");
    return Convert.ToInt32(read.ExecuteScalar());
}

static void AddStock(SqliteConnection connection, SqliteTransaction transaction, int delta)
{
    using var update = Command(connection, transaction, "UPDATE stock SET quantity = quantity + $delta WHERE id = 1");
    update.Parameters.AddWithValue("$delta", delta);
    update.ExecuteNonQuery();
}

static Reservation? Find(SqliteConnection connection, SqliteTransaction? transaction, string id)
{
    using var read = Command(connection, transaction, "SELECT quantity, active FROM reservations WHERE id = $id");
    read.Parameters.AddWithValue("$id", id);
    using var rows = read.ExecuteReader();
    return rows.Read() ? new Reservation(rows.GetInt32(0), rows.GetBoolean(1)) : null;
}

record Quantity(int Value);
record Reservation(int Quantity, bool Active);
