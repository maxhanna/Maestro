using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

public class AgentUtilitiesEdgeCasesTests
{
    [Fact]
    public void GeneratePlanJsonCandidates_RepairsUnquotedKeysAndTruncation()
    {
        var raw = "{ plan: [{ file: \"a.cs\", change: \"add\" }";
        var candidates = AgentPlanParsing.GeneratePlanJsonCandidates(raw).ToList();

        Assert.Contains(candidates, c => c.Contains("\"plan\""));
        Assert.Contains(candidates, c => c.Contains("\"file\""));
    }

    [Fact]
    public void GeneratePlanJsonCandidates_RepairsNewlinesInStrings()
    {
        var raw = "{\"plan\":[{\"file\":\"a.cs\",\"change\":\"line1\nline2\"}]}";
        var candidates = AgentPlanParsing.GeneratePlanJsonCandidates(raw).ToList();

        Assert.Contains(candidates, c => c.Contains("line1\\nline2") || c.Contains("line1\\r\\nline2"));
    }

    [Fact]
    public void TryRepairTruncatedPlanJson_AppendsClosingQuoteAndBrackets()
    {
        var truncated = "{\"plan\":[{\"file\":\"a.cs\",\"change\":\"missing end";
        var repaired = AgentPlanParsing.TryRepairTruncatedPlanJson(truncated);

        Assert.NotNull(repaired);
        Assert.EndsWith("]}", repaired);
    }
}
