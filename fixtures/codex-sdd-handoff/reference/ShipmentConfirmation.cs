using Microsoft.Data.Sqlite;

internal static class ShipmentConfirmation
{
    public static void Upgrade(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        bool hasFulfilledColumn = false;
        using (var columns = Command(connection, transaction, "PRAGMA table_info(reservations)"))
        using (var rows = columns.ExecuteReader())
        {
            while (rows.Read())
                hasFulfilledColumn |= rows.GetString(1) == "fulfilled";
        }

        if (!hasFulfilledColumn)
        {
            using var migration = Command(connection, transaction,
                "ALTER TABLE reservations ADD COLUMN fulfilled INTEGER NOT NULL DEFAULT 0");
            migration.ExecuteNonQuery();
        }

        using var receipts = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS shipment_receipts (
                reservation_id TEXT NOT NULL,
                receipt_key TEXT NOT NULL,
                increment INTEGER NOT NULL,
                reserved_total INTEGER NOT NULL,
                fulfilled_total INTEGER NOT NULL,
                PRIMARY KEY (reservation_id, receipt_key)
            )
            """);
        receipts.ExecuteNonQuery();
        transaction.Commit();
    }

    public static IResult Confirm(SqliteConnection connection, string id, Quantity request, HttpRequest http)
    {
        var values = http.Headers["Idempotency-Key"];
        if (request.Value <= 0 || values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
            return Results.BadRequest();
        string key = values[0]!;

        using var transaction = connection.BeginTransaction();
        using var lookup = Command(connection, transaction, """
            SELECT increment, reserved_total, fulfilled_total
            FROM shipment_receipts WHERE reservation_id = $id AND receipt_key = $key
            """);
        lookup.Parameters.AddWithValue("$id", id);
        lookup.Parameters.AddWithValue("$key", key);
        using (var previous = lookup.ExecuteReader())
        {
            if (previous.Read())
            {
                if (previous.GetInt32(0) != request.Value) return Results.Conflict();
                var body = new { id, quantity = previous.GetInt32(1), fulfilledQuantity = previous.GetInt32(2) };
                return Results.Ok(body);
            }
        }

        int quantity;
        int fulfilled;
        using (var reservation = Command(connection, transaction, "SELECT quantity, fulfilled, active FROM reservations WHERE id = $id"))
        {
            reservation.Parameters.AddWithValue("$id", id);
            using var current = reservation.ExecuteReader();
            if (!current.Read() || !current.GetBoolean(2)) return Results.NotFound();
            quantity = current.GetInt32(0);
            fulfilled = current.GetInt32(1);
        }
        if (request.Value > quantity - fulfilled) return Results.Conflict();
        int fulfilledQuantity = fulfilled + request.Value;
        using var update = Command(connection, transaction, "UPDATE reservations SET fulfilled = $fulfilled WHERE id = $id");
        update.Parameters.AddWithValue("$fulfilled", fulfilledQuantity);
        update.Parameters.AddWithValue("$id", id);
        update.ExecuteNonQuery();

        using var receipt = Command(connection, transaction, "INSERT INTO shipment_receipts VALUES ($id, $key, $increment, $total, $fulfilled)");
        receipt.Parameters.AddWithValue("$id", id);
        receipt.Parameters.AddWithValue("$key", key);
        receipt.Parameters.AddWithValue("$increment", request.Value);
        receipt.Parameters.AddWithValue("$total", quantity);
        receipt.Parameters.AddWithValue("$fulfilled", fulfilledQuantity);
        receipt.ExecuteNonQuery();
        transaction.Commit();
        return Results.Ok(new { id, quantity, fulfilledQuantity });
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
