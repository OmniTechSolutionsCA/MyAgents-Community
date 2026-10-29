using System.Globalization;
using System.Text.Json;
using MyAgents.Core.Tools;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

public sealed record BindingPoint(int X, int Y);

public static class BindingContractTools
{
    [AgentTool(Name = "describe")]
    public static string Describe(int count, string label, bool flag = false) =>
        $"{label}:{count}:{(flag ? "on" : "off")}";

    // Deliberate reflection-fallback coverage: this tool intentionally has no JsonContext,
    // so MYAG002 (reflection-based serialization) is suppressed here to pin the fallback
    // binding contract under test.
#pragma warning disable MYAG002
    [AgentTool(Name = "plot")]
    public static int Plot(BindingPoint point) => point.X + point.Y;
#pragma warning restore MYAG002

    [AgentTool(Name = "ratio")]
    public static double Ratio(double value) => value / 2;

    [AgentTool(Name = "is_positive")]
    public static bool IsPositive(int value) => value > 0;
}

/// <summary>
/// Argument binding contract shared by the runtime <see cref="DelegateTool"/> and
/// compile-time <c>[AgentTool]</c> generated tools. Both must reject bad model input the
/// same way rather than silently substituting default values.
/// </summary>
[TestFixture]
public sealed class ToolArgumentBindingContractTests
{
    public static IEnumerable<TestCaseData> DescribeTools()
    {
        yield return new TestCaseData((Func<ITool>)(() => BindingContractToolsAgentToolsExtensions.AsDescribeTool()))
            .SetArgDisplayNames("generated");
        yield return new TestCaseData((Func<ITool>)(() => DelegateTool.Create(BindingContractTools.Describe, "describe")))
            .SetArgDisplayNames("delegate");
    }

    public static IEnumerable<TestCaseData> PlotTools()
    {
        yield return new TestCaseData((Func<ITool>)(() => BindingContractToolsAgentToolsExtensions.AsPlotTool()))
            .SetArgDisplayNames("generated");
        yield return new TestCaseData((Func<ITool>)(() => DelegateTool.Create(BindingContractTools.Plot, "plot")))
            .SetArgDisplayNames("delegate");
    }

    public static IEnumerable<TestCaseData> RatioTools()
    {
        yield return new TestCaseData((Func<ITool>)(() => BindingContractToolsAgentToolsExtensions.AsRatioTool()))
            .SetArgDisplayNames("generated");
        yield return new TestCaseData((Func<ITool>)(() => DelegateTool.Create(BindingContractTools.Ratio, "ratio")))
            .SetArgDisplayNames("delegate");
    }

    public static IEnumerable<TestCaseData> IsPositiveTools()
    {
        yield return new TestCaseData((Func<ITool>)(() => BindingContractToolsAgentToolsExtensions.AsIsPositiveTool()))
            .SetArgDisplayNames("generated");
        yield return new TestCaseData((Func<ITool>)(() => DelegateTool.Create(BindingContractTools.IsPositive, "is_positive")))
            .SetArgDisplayNames("delegate");
    }

    [TestCaseSource(nameof(DescribeTools))]
    public async Task Binds_valid_arguments(Func<ITool> factory)
    {
        var result = await factory().InvokeAsync("{\"count\":3,\"label\":\"a\",\"flag\":true}");
        Assert.That(result, Is.EqualTo("a:3:on"));
    }

    [TestCaseSource(nameof(DescribeTools))]
    public async Task Matches_property_names_case_insensitively(Func<ITool> factory)
    {
        var result = await factory().InvokeAsync("{\"COUNT\":3,\"Label\":\"a\"}");
        Assert.That(result, Is.EqualTo("a:3:off"));
    }

    [TestCaseSource(nameof(DescribeTools))]
    public void Missing_required_argument_throws(Func<ITool> factory)
    {
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await factory().InvokeAsync("{\"label\":\"a\"}"));
        Assert.That(ex!.Message, Does.Contain("Missing required argument 'count'"));
    }

    [TestCaseSource(nameof(DescribeTools))]
    public void Non_numeric_value_for_int_throws(Func<ITool> factory)
    {
        Assert.ThrowsAsync<JsonException>(async () =>
            await factory().InvokeAsync("{\"count\":\"abc\",\"label\":\"a\"}"));
    }

    [TestCaseSource(nameof(DescribeTools))]
    public void Fractional_value_for_int_throws(Func<ITool> factory)
    {
        Assert.ThrowsAsync<JsonException>(async () =>
            await factory().InvokeAsync("{\"count\":1.5,\"label\":\"a\"}"));
    }

    [TestCaseSource(nameof(DescribeTools))]
    public void Number_for_string_throws(Func<ITool> factory)
    {
        Assert.ThrowsAsync<JsonException>(async () =>
            await factory().InvokeAsync("{\"count\":1,\"label\":5}"));
    }

    [TestCaseSource(nameof(DescribeTools))]
    public void String_for_bool_throws(Func<ITool> factory)
    {
        Assert.ThrowsAsync<JsonException>(async () =>
            await factory().InvokeAsync("{\"count\":1,\"label\":\"a\",\"flag\":\"yes\"}"));
    }

    [TestCaseSource(nameof(PlotTools))]
    public async Task Binds_complex_argument(Func<ITool> factory)
    {
        var result = await factory().InvokeAsync("{\"point\":{\"X\":2,\"Y\":3}}");
        Assert.That(result, Is.EqualTo("5"));
    }

    [TestCaseSource(nameof(PlotTools))]
    public void Malformed_complex_argument_throws(Func<ITool> factory)
    {
        Assert.ThrowsAsync<JsonException>(async () =>
            await factory().InvokeAsync("{\"point\":\"not-an-object\"}"));
    }

    [TestCaseSource(nameof(RatioTools))]
    public async Task Formats_numeric_results_with_invariant_culture(Func<ITool> factory)
    {
        var tool = factory();
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var result = await tool.InvokeAsync("{\"value\":5}");
            Assert.That(result, Is.EqualTo("2.5"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestCaseSource(nameof(IsPositiveTools))]
    public async Task Formats_boolean_results_as_json_literals(Func<ITool> factory)
    {
        var result = await factory().InvokeAsync("{\"value\":1}");
        Assert.That(result, Is.EqualTo("true"));
    }
}
