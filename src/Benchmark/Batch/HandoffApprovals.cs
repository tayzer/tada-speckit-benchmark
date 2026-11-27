using System.Text.Json.Nodes;
using Benchmark.Config;
using Benchmark.Util;
using static Benchmark.Batch.BatchFixtures;

namespace Benchmark.Batch;

internal static class HandoffApprovals
{
    private sealed record Draft(string Ticket, string Baseline, string Review, string InputsSha256);

    public static void Overlay(string pilot, string fixture, string app, string ticket)
    {
        string source = Path.Combine(pilot, "tada");
        string handoff = Path.Combine(fixture, "tada");
        string acceptance = Path.Combine(app, "Acceptance");
        Fs.CopyDirectory(Path.Combine(source, "Acceptance"), acceptance);
        Fs.CopyDirectory(Path.Combine(source, ".config"), Path.Combine(app, ".config"));
        File.Copy(Path.Combine(handoff, "InventoryBindings.cs"), Path.Combine(acceptance, "InventoryBindings.cs"), overwrite: true);
        File.Copy(Path.Combine(fixture, "shared", "legacy-inventory.dbseed"), Path.Combine(acceptance, "legacy-inventory.dbseed"), overwrite: true);

        string project = Path.Combine(acceptance, "Inventory.Acceptance.csproj");
        Fs.WriteAtomic(project, InventoryFixture.ReplaceOnce(Fs.NormalizeNewlines(File.ReadAllText(project)),
            "    <ReqnrollFeatureFile ", "    <None Update=\"legacy-inventory.dbseed\" CopyToOutputDirectory=\"PreserveNewest\" />\n    <ReqnrollFeatureFile "));
        Fs.WriteAtomic(Path.Combine(app, "tada.json"), InventoryFixture.ReplaceOnce(
            Fs.NormalizeNewlines(File.ReadAllText(Path.Combine(source, "tada.json"))), "\"timeoutSeconds\": 90", "\"timeoutSeconds\": 180"));

        string feature = Fs.NormalizeNewlines(File.ReadAllText(Path.Combine(handoff, "shipment.feature")));
        if (ticket == HandoffFixture.Second)
        {
            feature = InventoryFixture.ReplaceOnce(feature,
                "    When the new reservation total is 4\n    Then the last response is 409\n    And the reservation has total 6 and fulfilled 2\n    And 4 units are available",
                "    When the new reservation total is 4\n    Then the last response is 200\n    And the reservation has total 4 and fulfilled 2\n    And 6 units are available");
            feature = feature.TrimEnd() + "\n\n" + Fs.NormalizeNewlines(File.ReadAllText(Path.Combine(handoff, "revision-scenarios.featurepart")));
        }
        Fs.WriteAtomic(Path.Combine(app, "features", "shipment.feature"), feature);
    }

    public static async Task PrepareAsync(string pilot, string fixture, string output, string feed, string plugin, CancellationToken ct)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new BenchmarkException("Choose a new approval directory.");
        var drafts = new List<Draft>();
        foreach (string ticket in HandoffFixture.Tickets)
        {
            string app = Path.Combine(output, ticket);
            HandoffFixture.Materialize(pilot, fixture, app, ticket, "original");
            Overlay(pilot, fixture, app, ticket);
            ConfigureRestore(app, feed, Path.Combine(plugin, "feed"));
            foreach (var args in new string[][]
            {
                ["restore", "Acceptance/Inventory.Acceptance.csproj", "--locked-mode", "--configfile", "NuGet.Config"],
                ["tool", "restore", "--configfile", "NuGet.Config"]
            })
            {
                var result = await ProcessRunner.RunAsync(new ProcessSpec("dotnet", args, app) { Timeout = TimeSpan.FromMinutes(3) }, ct);
                if (!result.Succeeded) throw new BenchmarkException($"Handoff packet setup failed for {ticket}: {result.Stdout}\n{result.Stderr}");
            }
            if (ticket == HandoffFixture.Second)
                CopyOracleState(output, HandoffFixture.First, app, includeApprovals: false);
            string baseline = Required(await LocalTada(app, ["baseline", "prepare"], ct), "baseline");
            string[] arguments = ["baseline", "review", "--baseline", baseline];
            if (ticket == HandoffFixture.Second) arguments = [.. arguments, "--against", drafts[0].Baseline];
            var review = await LocalTada(app, arguments, ct);
            drafts.Add(new Draft(ticket, baseline, Required(review["review"], "id"), OracleHash(app, includeHistory: false)));
            Json.Write(Path.Combine(output, ticket + "-review.json"), review);
        }
        Json.Write(Path.Combine(output, "drafts.json"), drafts);
        string launcher = Path.GetFullPath(Path.Combine(fixture, "..", "..", "scripts", "Approve-CodexHandoffFixtures.ps1"));
        WriteOwnerInstructions(output, launcher, drafts);
    }

    private static void WriteOwnerInstructions(string output, string launcher, List<Draft> drafts)
    {
        var first = drafts[0];
        var second = drafts[1];
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string fence = new((char)96, 3);
        Fs.WriteAtomic(Path.Combine(output, "OWNER-REVIEW.md"), $$"""
            # Owner review for the handoff benchmark

            Review both ticket briefs, the two review JSON files, and the exact scenario files, bindings, legacy data and runner policy in the captured TADA snapshots.

            | Ticket | Baseline | Review |
            |---|---|---|
            | {{first.Ticket}} | {{first.Baseline}} | {{first.Review}} |
            | {{second.Ticket}} | {{second.Baseline}} | {{second.Review}} |

            Ticket 2 supersedes the blocked-resize expectation. Other Ticket 1 obligations continue.

            After reviewing both packets, run the approval command in PowerShell and enter your name. The command records both decisions and registers the approval receipt.

            {{fence}}powershell
            & {{Quote(launcher)}} -PacketRoot {{Quote(output)}}
            {{fence}}
            """);
    }

    public static IReadOnlyDictionary<string, FixtureApproval> Register(string root)
    {
        string receipt = Path.Combine(root, "approvals.json");
        if (File.Exists(receipt)) return Read(root);
        var approvals = Load(root, recorded: null);
        Json.Write(receipt, approvals.Values.ToList());
        return approvals;
    }

    public static IReadOnlyDictionary<string, FixtureApproval> Read(string root)
    {
        string receipt = Path.Combine(root, "approvals.json");
        if (!File.Exists(receipt))
            throw new BenchmarkException($"Registered owner approvals are required for both handoff tickets. See {Path.Combine(root, "OWNER-REVIEW.md")}.");
        return Load(root, Json.Read<List<FixtureApproval>>(receipt));
    }

    private static Dictionary<string, FixtureApproval> Load(string root, List<FixtureApproval>? recorded)
    {
        var drafts = Json.Read<List<Draft>>(Path.Combine(root, "drafts.json"));
        if (!drafts.Select(draft => draft.Ticket).SequenceEqual(HandoffFixture.Tickets))
            throw new BenchmarkException("Handoff fixture receipt requires exactly two ordered tickets.");
        var approvals = new Dictionary<string, FixtureApproval>();
        foreach (var draft in drafts)
        {
            string app = Path.Combine(root, draft.Ticket);
            if (OracleHash(app, includeHistory: false) != draft.InputsSha256)
                throw new BenchmarkException($"Reviewed oracle bytes changed for {draft.Ticket}.");
            string state = Path.Combine(app, ".tada");
            if (!Guid.TryParseExact(draft.Review, "N", out _) || !draft.Baseline.StartsWith("sha256:", StringComparison.Ordinal)
                || draft.Baseline.Length != 71 || !draft.Baseline[7..].All(Uri.IsHexDigit))
                throw new BenchmarkException("Malformed handoff review receipt.");
            var review = Json.Read<JsonObject>(Path.Combine(state, "reviews", draft.Review + ".json"));
            string? previous = approvals.GetValueOrDefault(HandoffFixture.First)?.Baseline;
            if (Required(review, "id") != draft.Review || Required(review, "baseline") != draft.Baseline ||
                review["comparedTo"]?.ToString() != previous)
                throw new BenchmarkException($"Invalid review lineage for {draft.Ticket}.");
            string records = Path.Combine(state, "approvals");
            if (!Directory.Exists(records)) throw new BenchmarkException($"Owner approval is required for {draft.Ticket}.");
            var matching = Directory.EnumerateFiles(records, "*.json").Select(Json.Read<JsonObject>)
                .Where(approval => approval["review"]?.ToString() == draft.Review && approval["baseline"]?.ToString() == draft.Baseline).ToList();
            if (matching.Count != 1) throw new BenchmarkException($"Exactly one owner approval is required for {draft.Ticket}.");
            var mark = matching[0];
            string by = Required(mark, "by");
            string id = Required(mark, "id");
            if (!Guid.TryParseExact(id, "N", out _) || string.IsNullOrWhiteSpace(by) ||
                by.Contains("automated", StringComparison.OrdinalIgnoreCase) || mark["replaces"]?.ToString() != previous)
                throw new BenchmarkException($"Expected a named human fixture approver and valid replacement lineage for {draft.Ticket}.");
            if (previous is not null)
            {
                var first = approvals[HandoffFixture.First];
                string prior = Path.Combine(root, HandoffFixture.First, ".tada", "approvals", first.ApprovalId + ".json");
                if (Fs.Sha256File(Path.Combine(records, first.ApprovalId + ".json")) != Fs.Sha256File(prior))
                    throw new BenchmarkException("The amendment did not retain the original approval bytes.");
            }
            approvals.Add(draft.Ticket, new FixtureApproval(draft.Ticket, draft.Baseline, draft.Review, id, by, OracleHash(app)));
        }
        if (recorded is not null && !recorded.SequenceEqual(approvals.Values))
            throw new BenchmarkException("Registered handoff approval evidence changed.");
        return approvals;
    }
}
