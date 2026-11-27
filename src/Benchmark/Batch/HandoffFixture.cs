using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Util;

namespace Benchmark.Batch;

internal static class HandoffFixture
{
    public const string Suite = "codex-sdd-handoff";
    public const string First = "ticket-1";
    public const string Second = "ticket-2";
    public const string LegacySeed = "tests/AgentTests/legacy-inventory.dbseed";
    public static readonly string[] Tickets = [First, Second];

    public static IReadOnlyList<string> Variants(string ticket) => ticket switch
    {
        First => ["reference", "original", "duplicate-receipt", "double-debit", "over-restock", "reset-legacy"],
        Second => ["reference", "original", "floor-bypass", "wrong-delta", "partial-write", "lost-receipt"],
        _ => throw new BenchmarkException("Unknown handoff ticket.")
    };

    public static void Materialize(string pilot, string fixture, string app, string ticket, string variant)
    {
        if (!Variants(ticket).Contains(variant)) throw new BenchmarkException("Unknown handoff fixture variant.");
        InventoryFixture.Materialize(pilot, app, "resize", "reference");
        File.Copy(Path.Combine(fixture, ticket + ".md"), Path.Combine(app, "TASK.md"), overwrite: true);
        Fs.CopyDirectory(Path.Combine(fixture, "shared"), Path.Combine(app, "tests", "AgentTests"));
        Fs.AppendLine(Path.Combine(app, "CONTRACT.md"),
            "This release has working resize and repeat cancellation. Use InventoryApi.StartLegacyAsync() for a database with the original schema. " +
            "The legacy fixture has 6 units available, an active 4-unit reservation named legacy-open and a cancelled reservation named legacy-cancelled. " +
            "Use api.RestartAsync() to reopen the same database. The current ticket controls any changes to this contract.");

        if (ticket == First && variant == "original") return;
        string programPath = Path.Combine(app, InventoryFixture.ApiSource);
        string confirmationPath = Path.Combine(app, "src", "Inventory", "ShipmentConfirmation.cs");
        string program = Fs.NormalizeNewlines(File.ReadAllText(programPath));
        string confirmation = Fs.NormalizeNewlines(File.ReadAllText(Path.Combine(fixture, "reference", "ShipmentConfirmation.cs")));
        program = InventoryFixture.ReplaceOnce(program, "    init.ExecuteNonQuery();", "    init.ExecuteNonQuery();\n    ShipmentConfirmation.Upgrade(connection);");
        program = InventoryFixture.ReplaceOnce(program, "Results.Ok(new { id, quantity = reservation.Quantity })",
            "Results.Ok(new { id, quantity = reservation.Quantity, fulfilledQuantity = reservation.Fulfilled })");
        program = InventoryFixture.ReplaceOnce(program, "INSERT INTO reservations VALUES ($id, $quantity, 1)",
            "INSERT INTO reservations (id, quantity, active) VALUES ($id, $quantity, 1)");
        program = InventoryFixture.ReplaceOnce(program, "AddStock(connection, transaction, reservation.Quantity);",
            "AddStock(connection, transaction, reservation.Quantity - reservation.Fulfilled);");
        program = InventoryFixture.ReplaceOnce(program, "SELECT quantity, active FROM reservations",
            "SELECT quantity, active, fulfilled FROM reservations");
        program = InventoryFixture.ReplaceOnce(program, "new Reservation(rows.GetInt32(0), rows.GetBoolean(1))",
            "new Reservation(rows.GetInt32(0), rows.GetBoolean(1), rows.GetInt32(2))");
        program = InventoryFixture.ReplaceOnce(program, "record Reservation(int Quantity, bool Active);",
            "record Reservation(int Quantity, bool Active, int Fulfilled);");
        string guard = ticket == First || variant == "original"
            ? "    if (reservation.Fulfilled > 0) return Results.Conflict();\n"
            : "    if (request.Value < reservation.Fulfilled) return Results.Conflict();\n";
        program = InventoryFixture.ReplaceOnce(program, "    int delta = request.Value - reservation.Quantity;",
            guard + "    int delta = request.Value - reservation.Quantity;");
        program = InventoryFixture.ReplaceOnce(program, "app.Run();", """
            app.MapPost("/reservations/{id}/fulfill", (string id, Quantity request, HttpRequest http) =>
            {
                using var connection = Open();
                return ShipmentConfirmation.Confirm(connection, id, request, http);
            });
            app.Run();
            """);
        (program, confirmation) = Mutate(program, confirmation, variant);
        Fs.WriteAtomic(programPath, program);
        Fs.WriteAtomic(confirmationPath, confirmation);
    }

    public static async Task<Dictionary<(string, string), string>> ValidateControlsAsync(BatchWorker worker,
        string pilot, string fixture, string work, string logs, CancellationToken ct)
    {
        var controls = new Dictionary<(string, string), string>();
        foreach (string ticket in Tickets)
        foreach (string variant in Variants(ticket))
        {
            ct.ThrowIfCancellationRequested();
            string app = Path.Combine(work, ticket + "-" + variant);
            string evidence = Path.Combine(logs, ticket + "-" + variant);
            await worker.PrepareWorkspaceAsync(app, Path.Combine(evidence, "workspace"), ct);
            try
            {
                Materialize(pilot, fixture, app, ticket, variant);
                var build = await worker.BuildAsync(app, InventoryFixture.TestProject, Path.Combine(evidence, "build"), ct);
                if (!build.Succeeded)
                    throw new BenchmarkException($"Handoff control {ticket}/{variant} failed build. {BatchWorker.FailureDetail(build)} See {evidence}.");
                if (variant is "reference" or "original")
                {
                    var original = await BatchScoring.RunTestsAsync(worker, app, Path.Combine(app, InventoryFixture.Assembly),
                        Path.Combine(evidence, "client-regression"), ct);
                    if (original.Tests.Count == 0 || original.Tests.Any(test => test.Outcome != TestOutcome.Pass))
                        throw new BenchmarkException($"Original client suite failed for {ticket}/{variant}. See {evidence}.");
                }
                string? previous = ticket == Second ? StageAssembly(controls[(First, "reference")], app, "previous") : null;
                var observed = await HandoffChecks.RunAsync(worker, app, evidence, ticket, previous, ct,
                    variant == "reference" ? null : variant);
                Json.Write(Path.Combine(evidence, "outcomes.json"), observed);
                TestOutcome expected = variant == "reference" ? TestOutcome.Pass : TestOutcome.Fail;
                if (observed.Count == 0 || observed.Any(check => check.Outcome != expected))
                    throw new BenchmarkException($"Handoff control {ticket}/{variant} did not produce {expected}. See {evidence}.");
                controls.Add((ticket, variant), app);
            }
            finally { await worker.AccessAsync(app, false); }
        }
        return controls;
    }

    public static string StageAssembly(string source, string destination, string name)
    {
        string assembly = Path.Combine(source, InventoryFixture.Assembly);
        return StageApiBundle(Path.GetDirectoryName(assembly)!, destination, name);
    }

    public static string StageApiBundle(string source, string destination, string name)
    {
        string target = Path.Combine(destination, ".worker-tmp", name);
        Submission.Copy(source, target, retainEvidence: true);
        return Path.Combine(target, Path.GetFileName(InventoryFixture.Assembly));
    }

    public static async Task VerifyPromotionAsync(BatchWorker worker, string pilot, string fixture, string output,
        Dictionary<(string, string), string> controls, CancellationToken ct)
    {
        string history = Path.Combine(output, "starting-release");
        Materialize(pilot, fixture, history, First, "original");
        string baseCommit = await CodexBatch.InitializeGitAsync(history, "Handoff control base");
        var slot = new BatchSlot("ticket-1-plain-control", First, "plain", 1);
        string retained = BatchEvidence.AttemptPath(output, slot);
        string coding = Path.Combine(retained, "coding");
        string submission = Path.Combine(coding, "submission");
        Submission.Copy(controls[(First, "reference")], submission);
        BatchEvidence.Seal(coding);
        string bundle = Path.Combine(retained, "evaluation", "promoted-api");
        Submission.Copy(Path.GetDirectoryName(Path.Combine(controls[(First, "reference")], InventoryFixture.Assembly))!,
            bundle, retainEvidence: true);
        BatchEvidence.Save(output, new BatchAttempt
        {
            Slot = slot, Status = "pass", SubmissionSha256 = Fs.Sha256Directory(submission),
            PromotedApiSha256 = Fs.Sha256Directory(bundle, includeBuildOutput: true)
        });
        BatchEvidence.Seal(retained);

        string next = Path.Combine(output, "follow-up");
        string logs = Path.Combine(output, "follow-up-logs");
        await worker.PrepareWorkspaceAsync(next, Path.Combine(logs, "workspace"), ct);
        try
        {
            var previous = BatchEvidence.ReadAttempt(output, slot);
            var promotion = await HandoffPromotion.CreateAsync(worker, next, logs, output, previous, (history, baseCommit), ct);
            if (!promotion.InputsUnchanged(next) || await Git.HeadAsync(next) != promotion.Commit)
                throw new BenchmarkException("The model-free handoff did not preserve the previous release.");
            string candidate = StageAssembly(controls[(Second, "reference")], next, "follow-up-reference");
            await HandoffChecks.CheckPromotedReleaseAsync(worker, next, candidate, promotion, logs, ct);
            Json.Write(Path.Combine(logs, "promotion.json"), promotion.Evidence);
        }
        finally { await worker.AccessAsync(next, false); }
    }

    private static (string Program, string Confirmation) Mutate(string program, string confirmation, string variant)
    {
        switch (variant)
        {
            case "duplicate-receipt":
                confirmation = InventoryFixture.ReplaceOnce(confirmation, "                return Results.Ok(body);", """
                                    previous.Close();
                                    using var duplicate = Command(connection, transaction, "UPDATE reservations SET fulfilled = fulfilled + $amount WHERE id = $id");
                                    duplicate.Parameters.AddWithValue("$id", id);
                                    duplicate.Parameters.AddWithValue("$amount", request.Value);
                                    duplicate.ExecuteNonQuery();
                                    transaction.Commit();
                                    return Results.Ok(body);
                    """);
                break;
            case "double-debit":
                confirmation = InventoryFixture.ReplaceOnce(confirmation, "        receipt.ExecuteNonQuery();", """
                            receipt.ExecuteNonQuery();
                            using var debit = Command(connection, transaction, "UPDATE stock SET quantity = quantity - $amount WHERE id = 1");
                            debit.Parameters.AddWithValue("$amount", request.Value);
                            debit.ExecuteNonQuery();
                    """);
                break;
            case "over-restock":
                program = InventoryFixture.ReplaceOnce(program, "reservation.Quantity - reservation.Fulfilled);", "reservation.Quantity);");
                break;
            case "reset-legacy":
                confirmation = InventoryFixture.ReplaceOnce(confirmation, "            migration.ExecuteNonQuery();", """
                                migration.ExecuteNonQuery();
                                using var drop = Command(connection, transaction, "DELETE FROM reservations WHERE active = 1 AND id = 'legacy-open'");
                                drop.ExecuteNonQuery();
                    """);
                break;
            case "floor-bypass":
                program = InventoryFixture.ReplaceOnce(program, "    if (request.Value < reservation.Fulfilled) return Results.Conflict();\n", "");
                break;
            case "wrong-delta":
                program = InventoryFixture.ReplaceOnce(program, "    AddStock(connection, transaction, -delta);",
                    "    AddStock(connection, transaction, -request.Value);");
                break;
            case "partial-write":
                program = InventoryFixture.ReplaceOnce(program, "    if (delta > Stock(connection, transaction)) return Results.Conflict();\n", "");
                program = InventoryFixture.ReplaceOnce(program, "    AddStock(connection, transaction, -delta);", """
                        if (delta > Stock(connection, transaction))
                        {
                            transaction.Commit();
                            return Results.Conflict();
                        }
                        AddStock(connection, transaction, -delta);
                    """);
                break;
            case "lost-receipt":
                program = InventoryFixture.ReplaceOnce(program, "    AddStock(connection, transaction, -delta);", """
                        AddStock(connection, transaction, -delta);
                        using var forget = Command(connection, transaction, "DELETE FROM shipment_receipts WHERE reservation_id = $id");
                        forget.Parameters.AddWithValue("$id", id);
                        forget.ExecuteNonQuery();
                    """);
                break;
        }
        return (program, confirmation);
    }
}
