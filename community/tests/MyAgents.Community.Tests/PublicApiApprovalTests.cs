using System.Reflection;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

/// <summary>
/// Guards each package's public API surface against unintended change.
/// </summary>
/// <remarks>
/// <para>
/// Every public type, constructor, property, field, event, and method of each shipped package is
/// compared against a checked-in baseline under <c>Baselines/</c>. Adding, removing, or
/// re-signing a public member fails the suite until the baseline is regenerated and reviewed, so
/// an API change is always a deliberate act rather than an accident.
/// </para>
/// <para>
/// To regenerate a baseline after an intentional change, run from the repository root:
/// </para>
/// <code>
/// dotnet run --project eng/PublicApiSnapshotTool -- \
///   community/tests/MyAgents.Community.Tests/bin/Release/net10.0/MyAgents.Core.dll \
///   community/tests/MyAgents.Community.Tests/Baselines/MyAgents.Core.approved.txt
/// </code>
/// <para>Then review the diff before committing.</para>
/// </remarks>
public sealed class PublicApiApprovalTests
{
    [TestCase(typeof(MyAgents.Core.IAgent), "MyAgents.Core.approved.txt")]
    [TestCase(typeof(MyAgents.Agents.LoggingFeature), "MyAgents.Agents.approved.txt")]
    [TestCase(typeof(MyAgents.Runtime.Chat.ChatClientRuntime), "MyAgents.Runtime.Chat.approved.txt")]
    [TestCase(typeof(MyAgents.Memory.IMemoryRetriever), "MyAgents.Memory.approved.txt")]
    [TestCase(typeof(MyAgents.Mcp.McpFeature), "MyAgents.Mcp.approved.txt")]
    [TestCase(typeof(MyAgents.Testing.InMemoryAgent), "MyAgents.Testing.approved.txt")]
    public void Public_api_matches_approved_baseline(Type anchorType, string baselineFileName)
    {
        var baselinePath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Baselines", baselineFileName);
        Assert.That(File.Exists(baselinePath), Is.True,
            $"Missing API baseline: {baselinePath}. Regenerate it with eng/PublicApiSnapshotTool.");

        var actual = CaptureApi(anchorType.Assembly);

        // Normalise line endings so a CRLF checkout and an LF checkout produce identical diffs.
        var expected = File.ReadAllText(baselinePath).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        actual = actual.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            var difference = DescribeFirstDifference(expected, actual);
            Assert.Fail(
                $"Public API of {anchorType.Assembly.GetName().Name} does not match " +
                $"{baselineFileName}{Environment.NewLine}{difference}");
        }
    }

    private static string DescribeFirstDifference(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var max = Math.Max(expectedLines.Length, actualLines.Length);

        for (var i = 0; i < max; i++)
        {
            var exp = i < expectedLines.Length ? expectedLines[i] : "<end of baseline>";
            var act = i < actualLines.Length ? actualLines[i] : "<end of actual API>";

            if (!string.Equals(exp, act, StringComparison.Ordinal))
            {
                return $"First difference at line {i + 1}:{Environment.NewLine}" +
                       $"  baseline: {exp}{Environment.NewLine}" +
                       $"  actual:   {act}";
            }
        }

        return "Baselines differ only in trailing content.";
    }

    /// <summary>
    /// Mirrors eng/PublicApiSnapshotTool so the test and the generation tool agree byte for byte.
    /// </summary>
    private static string CaptureApi(Assembly assembly)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var type in assembly.ExportedTypes
                     .Where(static type => !type.IsSpecialName)
                     .OrderBy(static type => type.FullName, StringComparer.Ordinal))
        {
            builder.AppendLine($"TYPE {FormatTypeDeclaration(type)}");

            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Select(FormatConstructorSignature)
                         .OrderBy(static signature => signature, StringComparer.Ordinal))
            {
                builder.AppendLine($"  CTOR {constructor}");
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                var accessors = new List<string>();
                if (property.GetMethod is { IsPublic: true }) accessors.Add("get");
                if (property.SetMethod is { IsPublic: true }) accessors.Add("set");
                builder.AppendLine($"  PROP {FormatTypeName(property.PropertyType)} {property.Name} {{ {string.Join("; ", accessors)}; }}");
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(static field => !field.IsSpecialName)
                         .OrderBy(static field => field.Name, StringComparer.Ordinal))
            {
                builder.AppendLine($"  FIELD {FormatTypeName(field.FieldType)} {field.Name}");
            }

            foreach (var evt in type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .OrderBy(static evt => evt.Name, StringComparer.Ordinal))
            {
                builder.AppendLine($"  EVENT {FormatTypeName(evt.EventHandlerType!)} {evt.Name}");
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(static method => !method.IsSpecialName)
                         .Select(FormatMethodSignature)
                         .OrderBy(static signature => signature, StringComparer.Ordinal))
            {
                builder.AppendLine($"  METHOD {method}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatTypeDeclaration(Type type)
    {
        var kind = type.IsInterface
            ? "interface"
            : type.IsEnum
                ? "enum"
                : type.IsValueType
                    ? "struct"
                    : "class";

        return $"{kind} {FormatTypeName(type)}";
    }

    private static string FormatParameters(IEnumerable<ParameterInfo> parameters)
        => string.Join(", ", parameters.Select(static parameter => $"{FormatTypeName(parameter.ParameterType)} {parameter.Name}"));

    private static string FormatConstructorSignature(ConstructorInfo constructor)
        => $"{constructor.DeclaringType!.Name}({FormatParameters(constructor.GetParameters())})";

    private static string FormatMethodSignature(MethodInfo method)
        => $"{FormatTypeName(method.ReturnType)} {method.Name}({FormatParameters(method.GetParameters())})";

    private static string FormatTypeName(Type type)
    {
        if (type == typeof(void)) return "void";
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(object)) return "object";
        if (type == typeof(double)) return "double";
        if (type == typeof(float)) return "float";
        if (type == typeof(decimal)) return "decimal";

        if (type.IsByRef)
            return $"{FormatTypeName(type.GetElementType()!)}&";

        if (type.IsArray)
            return $"{FormatTypeName(type.GetElementType()!)}[]";

        if (type.IsGenericType)
        {
            var genericDefinitionName = type.GetGenericTypeDefinition().FullName!;
            genericDefinitionName = genericDefinitionName[..genericDefinitionName.IndexOf('`')];
            var genericArguments = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
            return $"{genericDefinitionName}<{genericArguments}>";
        }

        return type.FullName ?? type.Name;
    }
}
