using System.Text;
using Weaver.Controllers;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

public class FullFilePreferenceTests
{
    [Theory]
    [InlineData(false, 1, true)]
    [InlineData(false, 10000, true)]
    [InlineData(true, 499, true)]
    [InlineData(true, 500, false)]
    [InlineData(true, 10000, false)]
    public void FullFileAllowance_OnlyPermitsNewOrSmallExistingFiles(bool fileExists, int length, bool expected)
    {
        Assert.Equal(expected, AgentController.IsFullFileAllowed(fileExists, length));
    }

    [Fact]
    public void FormatSwitchForLargeExistingFile_RequiresTargetedEdit()
    {
        var sb = new StringBuilder();
        EscalationStateMachine.AppendEscalationDirective(
            sb,
            EscalationLevel.FormatSwitch,
            EditStrategy.AnchoredEdit,
            ".ts",
            new string('x', 1200),
            "Add downloaded_painting to event descriptions mapping",
            0);

        var directive = sb.ToString();

        Assert.Contains("PRECISE_TARGETED_EDIT", directive);
        Assert.Contains("fullFile is BLOCKED for existing files", directive);
        Assert.DoesNotContain("{ \"fullFile\":", directive);
        Assert.Contains("oldString", directive);
    }
}
