using CodeStack.Ratchet.Core;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// The run_tests summary parser — the interesting case is a multi-project run, whose
/// per-project summary lines must be SUMMED, not read first-only.
/// </summary>
public sealed class TestToolSummaryTests
{
    [Fact]
    public void SingleProject_ParsesCounts()
    {
        var raw = "Passed!  - Failed: 0, Passed: 42, Skipped: 1, Total: 43";
        var summary = TestTool.Summarize("dotnet test", raw, 0);

        Assert.Contains("PASSED", summary);
        Assert.Contains("passed: 42", summary);
        Assert.Contains("failed: 0", summary);
    }

    [Fact]
    public void MultiProject_SumsCountsAcrossProjects()
    {
        // Two test projects, two summary lines — the old first-match parse reported
        // only project A's numbers and could disagree with the exit code.
        var raw = """
            Passed!  - Failed: 0, Passed: 10, Skipped: 0, Total: 10 - ProjA.dll
            Failed!  - Failed: 2, Passed: 30, Skipped: 1, Total: 33 - ProjB.dll
            """;
        var summary = TestTool.Summarize("dotnet test", raw, 1);

        Assert.Contains("FAILED", summary);
        Assert.Contains("failed: 2", summary);
        Assert.Contains("passed: 40", summary);   // 10 + 30
        Assert.Contains("skipped: 1", summary);
        Assert.Contains("total: 43", summary);     // 10 + 33
    }

    [Fact]
    public void IncidentalFailedInLog_DoesNotInflateTheCount()
    {
        // A line like "connection failed: 3" in test output must not be counted as
        // failures — the pattern is anchored to a line-leading summary label.
        var raw = """
            warning: connection failed: 3 times before retrying
            Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5
            """;
        var summary = TestTool.Summarize("dotnet test", raw, 0);

        Assert.Contains("PASSED", summary);
        Assert.Contains("failed: 0", summary);
    }

    [Fact]
    public void UnrecognizedOutput_FallsBackToTail()
    {
        var summary = TestTool.Summarize("pytest", "some other framework's noise", 1);
        Assert.Contains("no summary recognised", summary);
        Assert.Contains("some other framework", summary);
    }

    [Fact]
    public void FailingTestNames_AreExtracted_FromBothFormats()
    {
        var raw = """
            [xUnit.net]     MyProj.Tests.FooTests.Bar [FAIL]
              Failed MyProj.Tests.BazTests.Qux [12 ms]
            Failed!  - Failed: 2, Passed: 0, Skipped: 0, Total: 2
            """;
        var summary = TestTool.Summarize("dotnet test", raw, 1);

        Assert.Contains("FooTests.Bar", summary);
        Assert.Contains("BazTests.Qux", summary);
    }
}
