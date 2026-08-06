using System.Reflection;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the planning/editing thinking token cap (Controllers/AgentController.cs,
/// GetPlanningTokenCap). Per-step pre-plan reasoning must stay in a tight 120-840
/// token range that scales with complexity — independent of the user's overall
/// Thinking Max Tokens slider (which is the budget for accumulated deep thinking,
/// NOT this per-step planning output). Scaling: 0→120, 50→480, 100→840.
/// </summary>
public class PlanningTokenCapTests
{
    private static readonly MethodInfo CapMethod = typeof(Weaver.Controllers.AgentController)
        .GetMethod("GetPlanningTokenCap", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("GetPlanningTokenCap static method not found.");

    private static int Cap(int complexity)
        => (int)(CapMethod.Invoke(null, new object?[] { complexity }) ?? -1);

    [Theory]
    [InlineData(0, 120)]
    [InlineData(5, 156)]
    [InlineData(10, 192)]
    [InlineData(25, 300)]
    [InlineData(30, 336)]
    [InlineData(50, 480)]
    [InlineData(75, 660)]
    [InlineData(90, 768)]
    [InlineData(100, 840)]
    public void Cap_ScalesLinearly_Within120To840(int complexity, int expected)
        => Assert.Equal(expected, Cap(complexity));

    [Fact]
    public void Cap_NeverExceeds840() => Assert.Equal(840, Cap(200));

    [Fact]
    public void Cap_NeverBelow120() => Assert.Equal(120, Cap(-50));

    [Fact]
    public void Cap_IsSmallerThanDefaultOverallThinkingBudget()
        => Assert.True(Cap(100) < 4096, "planning cap must never approach the overall thinking budget");
}
