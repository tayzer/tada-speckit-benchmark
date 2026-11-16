using Benchmark.Review;
using Benchmark.Runs;
using Xunit;

namespace Benchmark.Tests;

public sealed class AiReviewTests
{
    [Theory]
    [InlineData(".tada/runs/x.json", ".tada/**", true)]
    [InlineData("specs/001-a/spec.md", "specs/**", true)]
    [InlineData("src/specs/x.cs", "specs/**", false)]
    [InlineData("src/Web/packages.lock.json", "**/packages.lock.json", true)]
    [InlineData("packages.lock.json", "**/packages.lock.json", true)]
    [InlineData("src/Infrastructure/Data/Migrations/2026_Add.Designer.cs", "**/Migrations/*.Designer.cs", true)]
    [InlineData("src/Infrastructure/Data/Migrations/2026_Add.cs", "**/Migrations/*.Designer.cs", false)]
    [InlineData("tests/Acceptance/TadaAcceptance.cs", "tests/Acceptance/TadaAcceptance.cs", true)]
    [InlineData("tests/Acceptance/Bindings.cs", "tests/Acceptance/TadaAcceptance.cs", false)]
    [InlineData("tada.json", "tada.json", true)]
    [InlineData("src/tada.json", "tada.json", true)] // a pattern without '/' matches the file name anywhere
    public void Glob_matches_repo_relative_paths(string path, string glob, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(path, glob));

    [Fact]
    public void Patch_is_split_per_file_using_the_new_path()
    {
        var patch = "diff --git a/x.cs b/x.cs\nindex 1..2\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-a\n+b\ndiff --git a/dir/y.md b/dir/y.md\nnew file mode 100644\n+hi\n";
        var chunks = AiReview.SplitPatch(patch);
        Assert.Equal(["x.cs", "dir/y.md"], chunks.Select(c => c.Path));
        Assert.StartsWith("diff --git a/x.cs", chunks[0].Text);
        Assert.EndsWith("+b\n", chunks[0].Text);
        Assert.Contains("+hi", chunks[1].Text);
    }

    private static string Change(int score, string? skip = null) =>
        "{\"scores\":{" + string.Join(",", Rubric.Dimensions.Where(d => d != skip).Select(d => $"\"{d}\":{score}")) + "},"
        + "\"evidence\":{},\"risksDiscovered\":[],\"risksMissed\":[\"r\"],\"incorrectAssumptions\":[],\"regressionAssessment\":\"ok\",\"blindingClues\":[]}";

    [Fact]
    public void Code_review_reply_is_parsed_from_a_fenced_block_and_validated()
    {
        var reply = "Here you go:\n```json\n{\"A\":" + Change(4) + ",\"B\":" + Change(2) + ",\"comparison\":\"A better\"}\n```\n";
        var (value, error) = AiReview.ParseCodeReview(reply);
        Assert.Null(error);
        Assert.Equal(4, value!.Changes["A"].Scores["maintainability"]);
        Assert.Equal(2, value.Changes["B"].Scores["test-quality"]);
        Assert.Equal(["r"], value.Changes["A"].RisksMissed);
        Assert.Equal("A better", value.Comparison);
    }

    [Fact]
    public void Code_review_reply_with_missing_or_out_of_range_scores_is_rejected()
    {
        Assert.NotNull(AiReview.ParseCodeReview("{\"A\":" + Change(4, skip: "error-handling") + ",\"B\":" + Change(2) + "}").Error);
        Assert.NotNull(AiReview.ParseCodeReview("{\"A\":" + Change(6) + ",\"B\":" + Change(2) + "}").Error);
        Assert.NotNull(AiReview.ParseCodeReview("{\"A\":" + Change(3) + "}").Error);
        Assert.NotNull(AiReview.ParseCodeReview("no json at all").Error);
    }

    [Fact]
    public void Process_review_reply_requires_the_classification_lists()
    {
        var (ok, _) = AiReview.ParseProcessReview("{\"workflowSteps\":[1],\"correctivePrompts\":[3],\"toolingInterventions\":[],\"notes\":\"x\"}");
        Assert.Equal([3], ok!.CorrectivePrompts);
        Assert.NotNull(AiReview.ParseProcessReview("{\"notes\":\"x\"}").Error);
    }

    private static CodePass Pass(int pass, int score) => new()
    {
        Pass = pass, Label = pass == 1 ? "A" : "B",
        Review = new CodeReviewOfChange { Scores = Rubric.Dimensions.ToDictionary(d => d, _ => score) },
    };

    [Fact]
    public void Flags_report_order_sensitivity_reviewer_disagreement_leaks_and_draft_protocol()
    {
        var s = new AiReviewSummary
        {
            ProtocolApproved = false,
            Runs =
            [
                new AiRunSummary
                {
                    RunId = "r1", WorkflowId = "w1", LeakCounts = new() { ["tada"] = 3 },
                    Code = new() { ["m1"] = [Pass(1, 5), Pass(2, 3)], ["m2"] = [Pass(1, 2), Pass(2, 2)] },
                    Process = new() { ["m1"] = new ProcessReviewOutput { CorrectivePrompts = [2] }, ["m2"] = new ProcessReviewOutput() },
                },
            ],
        };
        var flags = AiReview.ComputeFlags(s);
        Assert.Contains(flags, f => f.StartsWith("DRAFT"));
        Assert.Contains(flags, f => f.StartsWith("ORDER-SENSITIVE: m1") && f.Contains("5/3"));
        Assert.Contains(flags, f => f.StartsWith("REVIEWERS DISAGREE: w1"));
        Assert.Contains(flags, f => f.StartsWith("Blinding leak") && f.Contains("'tada'×3"));
        Assert.Contains(flags, f => f.StartsWith("REVIEWERS DISAGREE on corrective prompts"));
    }

    [Fact]
    public void Reviewer_cannot_read_harness_key_runs_or_other_passes_but_can_read_base_and_its_own_work()
    {
        var c = TestSupport.RealConfig();
        var data = TestSupport.TempDir("ai-review-roots");
        try
        {
            foreach (var d in new[] { "runs", "evaluations", "workspaces", "base", "ai-reviews", "ai-review-work/set1/code-a-pass1", "ai-review-work/set1/code-a-pass2" })
                Directory.CreateDirectory(Path.Combine(data, d));
            var store = new RunStore(data);
            var own = Path.Combine(data, "ai-review-work", "set1", "code-a-pass1");
            var roots = AiReview.ReviewerForbiddenRoots(c, store, own).Select(Path.GetFullPath).ToList();
            Assert.Contains(Path.GetFullPath(c.HarnessRoot), roots);
            Assert.Contains(Path.GetFullPath(Path.Combine(data, "runs")), roots);
            Assert.Contains(Path.GetFullPath(Path.Combine(data, "ai-reviews")), roots);
            Assert.Contains(Path.GetFullPath(Path.Combine(data, "ai-review-work", "set1", "code-a-pass2")), roots);
            Assert.DoesNotContain(Path.GetFullPath(own), roots);
            Assert.DoesNotContain(Path.GetFullPath(Path.Combine(data, "base")), roots);
            Assert.DoesNotContain(Path.GetFullPath(Path.Combine(data, "ai-review-work")), roots);
        }
        finally { Directory.Delete(data, recursive: true); }
    }

    [Fact]
    public void Protocol_hash_changes_with_filters_and_reviewers()
    {
        var c = TestSupport.RealConfig();
        var before = AiReview.ProtocolSha256(c);
        c.AiReview.ExcludeGlobs.Add("src/**");
        var filtered = AiReview.ProtocolSha256(c);
        c.AiReview.Reviewers.Add(new Config.AiReviewerConfig { Model = "other" });
        Assert.NotEqual(before, filtered);
        Assert.NotEqual(filtered, AiReview.ProtocolSha256(c));
        Assert.False(AiReview.IsApproved(c));
    }
}

public sealed class CitationCheckTests
{
    [Fact]
    public void Citations_past_the_end_of_the_post_change_file_are_counted()
    {
        var after = TestSupport.TempDir("ai-review-after");
        try
        {
            Directory.CreateDirectory(Path.Combine(after, "src", "Web"));
            File.WriteAllLines(Path.Combine(after, "src", "Web", "Checkout.cs"), Enumerable.Range(1, 100).Select(i => $"line {i}"));
            var review = new CodeReviewOfChange
            {
                Evidence = new()
                {
                    ["maintainability"] = "See src/Web/Checkout.cs:42 and Checkout.cs:513-518.",
                    ["test-quality"] = "Unknown.cs:5 is not in the bundle.",
                },
            };
            Assert.Equal((2, 1), AiReview.CheckCitations(review, after));
        }
        finally { Directory.Delete(after, recursive: true); }
    }
}
