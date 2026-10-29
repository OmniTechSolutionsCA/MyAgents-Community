using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace MyAgents.Generators;

/// <summary>
/// Incremental Roslyn source generator that creates <c>ITool</c> wrappers with compile-time JSON schema generation for methods annotated with <c>[AgentTool]</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AgentToolGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "MyAgents.Core.Tools.AgentToolAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var methodDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => s is MethodDeclarationSyntax m && m.AttributeLists.Count > 0,
                transform: static (ctx, _) => GetToolMethodOrNull(ctx))
            .Where(static m => m is not null);

        var compilationAndMethods = context.CompilationProvider.Combine(methodDeclarations.Collect());

        context.RegisterSourceOutput(compilationAndMethods, static (spc, source) =>
        {
            var (compilation, methods) = source;
            if (methods.IsDefaultOrEmpty)
            {
                return;
            }

            var groupedByType = new Dictionary<INamedTypeSymbol, List<ToolMethodModel>>(SymbolEqualityComparer.Default);
            foreach (var method in methods)
            {
                if (method is null) continue;
                if (!groupedByType.TryGetValue(method.ContainingType, out var list))
                {
                    list = new List<ToolMethodModel>();
                    groupedByType[method.ContainingType] = list;
                }
                list.Add(method);
            }

            foreach (var kvp in groupedByType)
            {
                var typeSymbol = kvp.Key;
                var toolMethods = kvp.Value;
                var sourceCode = GenerateToolFile(typeSymbol, toolMethods);
                var fileName = $"{typeSymbol.ContainingNamespace.ToDisplayString().Replace('.', '_')}_{typeSymbol.Name}_AgentTools.g.cs";
                spc.AddSource(fileName, SourceText.From(sourceCode, Encoding.UTF8));
            }
        });
    }

    private static ToolMethodModel? GetToolMethodOrNull(GeneratorSyntaxContext context)
    {
        var methodDeclaration = (MethodDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(methodDeclaration) is not IMethodSymbol methodSymbol)
        {
            return null;
        }

        AttributeData? toolAttr = null;
        foreach (var attr in methodSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == AttributeFullName)
            {
                toolAttr = attr;
                break;
            }
        }

        if (toolAttr is null)
        {
            return null;
        }

        string? customName = null;
        if (toolAttr.ConstructorArguments.Length > 0 &&
            toolAttr.ConstructorArguments[0].Value is string nameVal &&
            !string.IsNullOrWhiteSpace(nameVal))
        {
            customName = nameVal;
        }

        string? customDescription = null;
        foreach (var namedArg in toolAttr.NamedArguments)
        {
            if (namedArg.Key == "Name" && namedArg.Value.Value is string nVal && !string.IsNullOrWhiteSpace(nVal))
            {
                customName = nVal;
            }
            else if (namedArg.Key == "Description" && namedArg.Value.Value is string descVal)
            {
                customDescription = descVal;
            }
        }

        var parameters = new List<ToolParameterModel>();
        foreach (var p in methodSymbol.Parameters)
        {
            var isCt = p.Type.ToDisplayString() == "System.Threading.CancellationToken";
            string? paramDesc = null;
            foreach (var pAttr in p.GetAttributes())
            {
                if (pAttr.AttributeClass?.Name is "DescriptionAttribute" or "Description" &&
                    pAttr.ConstructorArguments.Length > 0 &&
                    pAttr.ConstructorArguments[0].Value is string d)
                {
                    paramDesc = d;
                    break;
                }
            }

            parameters.Add(new ToolParameterModel(
                Name: p.Name,
                TypeDisplay: p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                TypeSpecial: p.Type.SpecialType,
                IsCancellationToken: isCt,
                HasDefaultValue: p.HasExplicitDefaultValue,
                DefaultValueLiteral: GetDefaultValueLiteral(p),
                IsNullable: p.NullableAnnotation == NullableAnnotation.Annotated,
                Description: paramDesc));
        }

        return new ToolMethodModel(
            ContainingType: methodSymbol.ContainingType,
            MethodName: methodSymbol.Name,
            ToolName: customName ?? methodSymbol.Name,
            Description: customDescription,
            IsStatic: methodSymbol.IsStatic,
            ReturnType: methodSymbol.ReturnType,
            Parameters: parameters);
    }

    private static string? GetDefaultValueLiteral(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue) return null;
        var val = parameter.ExplicitDefaultValue;
        if (val is null) return "null";
        if (val is bool b) return b ? "true" : "false";
        if (val is string s) return $"\"{EscapeString(s)}\"";
        if (val is char c) return $"'{c}'";
        if (val is double d) return $"{d.ToString(System.Globalization.CultureInfo.InvariantCulture)}d";
        if (val is float f) return $"{f.ToString(System.Globalization.CultureInfo.InvariantCulture)}f";
        if (val is decimal m) return $"{m.ToString(System.Globalization.CultureInfo.InvariantCulture)}m";
        return val.ToString();
    }

    private static string EscapeString(string str)
    {
        return str
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private static string GenerateToolFile(INamedTypeSymbol typeSymbol, List<ToolMethodModel> methods)
    {
        var sb = new StringBuilder();
        var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace ? null : typeSymbol.ContainingNamespace.ToDisplayString();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (ns is not null)
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
        }

        var indent = ns is not null ? "    " : "";

        foreach (var method in methods)
        {
            GenerateToolClass(sb, indent, typeSymbol, method);
            sb.AppendLine();
        }

        GenerateExtensionClass(sb, indent, typeSymbol, methods);

        if (ns is not null)
        {
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private static void GenerateToolClass(StringBuilder sb, string indent, INamedTypeSymbol typeSymbol, ToolMethodModel method)
    {
        var toolClassName = $"{typeSymbol.Name}_{method.MethodName}_GeneratedTool";
        var typeFullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var schemaJson = GenerateSchemaJson(method);

        sb.AppendLine($"{indent}/// <summary>");
        sb.AppendLine($"{indent}/// Compile-time generated <see cref=\"global::MyAgents.Core.Tools.ITool\"/> wrapper for <c>{typeSymbol.Name}.{method.MethodName}</c>.");
        sb.AppendLine($"{indent}/// </summary>");
        sb.AppendLine($"{indent}[global::System.CodeDom.Compiler.GeneratedCode(\"MyAgents.Generators\", \"1.0.0.0\")]");
        sb.AppendLine($"{indent}public sealed class {toolClassName} : global::MyAgents.Core.Tools.ITool");
        sb.AppendLine($"{indent}{{");

        if (method.IsStatic)
        {
            sb.AppendLine($"{indent}    public static readonly {toolClassName} Instance = new();");
            sb.AppendLine();
            sb.AppendLine($"{indent}    public {toolClassName}() {{ }}");
        }
        else
        {
            sb.AppendLine($"{indent}    private readonly {typeFullName} _target;");
            sb.AppendLine();
            sb.AppendLine($"{indent}    public {toolClassName}({typeFullName} target)");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        _target = target ?? throw new global::System.ArgumentNullException(nameof(target));");
            sb.AppendLine($"{indent}    }}");
        }

        sb.AppendLine();
        sb.AppendLine($"{indent}    public string Name => \"{EscapeString(method.ToolName)}\";");
        sb.AppendLine($"{indent}    public string? Description => {(method.Description is not null ? $"\"{EscapeString(method.Description)}\"" : "null")};");
        sb.AppendLine($"{indent}    public string? ParametersSchemaJson => Schema;");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public const string Schema = \"{EscapeString(schemaJson)}\";");
        sb.AppendLine();

        GenerateInvokeMethod(sb, indent + "    ", typeSymbol, method);

        sb.AppendLine($"{indent}}}");
    }

    private static string GenerateSchemaJson(ToolMethodModel method)
    {
        var propsSb = new StringBuilder();
        var reqList = new List<string>();

        var first = true;
        foreach (var p in method.Parameters)
        {
            if (p.IsCancellationToken) continue;

            if (!first) propsSb.Append(',');
            first = false;

            var jsonType = GetJsonType(p.TypeSpecial, p.TypeDisplay);
            propsSb.Append($"\"{EscapeString(p.Name)}\":{{\"type\":\"{jsonType}\"");

            if (!string.IsNullOrWhiteSpace(p.Description))
            {
                propsSb.Append($",\"description\":\"{EscapeString(p.Description!)}\"");
            }

            propsSb.Append('}');

            if (!p.HasDefaultValue && !p.IsNullable)
            {
                reqList.Add($"\"{EscapeString(p.Name)}\"");
            }
        }

        var schemaSb = new StringBuilder();
        schemaSb.Append("{\"type\":\"object\",\"properties\":{");
        schemaSb.Append(propsSb.ToString());
        schemaSb.Append('}');

        if (reqList.Count > 0)
        {
            schemaSb.Append(",\"required\":[");
            schemaSb.Append(string.Join(",", reqList));
            schemaSb.Append(']');
        }

        schemaSb.Append('}');
        return schemaSb.ToString();
    }

    private static string GetJsonType(SpecialType specialType, string typeDisplay)
    {
        switch (specialType)
        {
            case SpecialType.System_Boolean:
                return "boolean";
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Int16:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_UInt32:
            case SpecialType.System_UInt64:
            case SpecialType.System_UInt16:
                return "integer";
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
                return "number";
            case SpecialType.System_String:
            case SpecialType.System_Char:
            case SpecialType.System_DateTime:
                return "string";
            default:
                if (typeDisplay.IndexOf("[]", StringComparison.Ordinal) >= 0 ||
                    typeDisplay.IndexOf("List<", StringComparison.Ordinal) >= 0 ||
                    typeDisplay.IndexOf("IEnumerable<", StringComparison.Ordinal) >= 0)
                {
                    return "array";
                }
                return "object";
        }
    }

    private static void GenerateInvokeMethod(StringBuilder sb, string indent, INamedTypeSymbol typeSymbol, ToolMethodModel method)
    {
        var returnTypeStr = method.ReturnType.ToDisplayString();
        var isAsync = returnTypeStr.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal) ||
                      returnTypeStr.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal);

        var asyncKeyword = isAsync ? "async " : "";
        sb.AppendLine($"{indent}public {asyncKeyword}global::System.Threading.Tasks.ValueTask<string> InvokeAsync(string jsonArguments, global::System.Threading.CancellationToken cancellationToken = default)");
        sb.AppendLine($"{indent}{{");

        // Parameter variables initialized with defaults
        foreach (var p in method.Parameters)
        {
            if (p.IsCancellationToken) continue;
            var defaultVal = p.HasDefaultValue && p.DefaultValueLiteral is not null ? p.DefaultValueLiteral : "default!";
            sb.AppendLine($"{indent}    {p.TypeDisplay} {p.Name} = {defaultVal};");
        }

        var argList = string.Join(", ", method.Parameters.Select(p => p.IsCancellationToken ? "cancellationToken" : p.Name));
        var targetPrefix = method.IsStatic
            ? typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : "_target";

        var callExpr = $"{targetPrefix}.{method.MethodName}({argList})";

        var nonCtParams = method.Parameters.Where(p => !p.IsCancellationToken).ToList();
        if (nonCtParams.Count == 0)
        {
            GenerateReturnStatement(sb, indent + "    ", method.ReturnType, callExpr);
            sb.AppendLine($"{indent}}}");
            return;
        }

        // Mirrors DelegateTool: invalid JSON is treated as an empty argument object, while
        // missing required arguments and wrongly typed values are reported to the caller.
        sb.AppendLine($"{indent}    global::System.Text.Json.JsonDocument? __doc = null;");
        sb.AppendLine($"{indent}    if (!string.IsNullOrWhiteSpace(jsonArguments))");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        try {{ __doc = global::System.Text.Json.JsonDocument.Parse(jsonArguments); }}");
        sb.AppendLine($"{indent}        catch (global::System.Text.Json.JsonException) {{ }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine($"{indent}    using (__doc)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        var __root = __doc is not null ? __doc.RootElement : default;");

        foreach (var p in nonCtParams)
        {
            GenerateParameterExtractor(sb, indent + "        ", method.ToolName, p);
        }

        sb.AppendLine();
        GenerateReturnStatement(sb, indent + "        ", method.ReturnType, callExpr);
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
        GenerateBindingHelpers(sb, indent, method.ToolName);
    }

    private static void GenerateBindingHelpers(StringBuilder sb, string indent, string toolName)
    {
        sb.AppendLine($"{indent}private static bool __TryFindArgument(global::System.Text.Json.JsonElement root, string name, out global::System.Text.Json.JsonElement value)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    if (root.ValueKind == global::System.Text.Json.JsonValueKind.Object)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        if (root.TryGetProperty(name, out value)) return true;");
        sb.AppendLine($"{indent}        foreach (var __prop in root.EnumerateObject())");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            if (string.Equals(__prop.Name, name, global::System.StringComparison.OrdinalIgnoreCase))");
        sb.AppendLine($"{indent}            {{");
        sb.AppendLine($"{indent}                value = __prop.Value;");
        sb.AppendLine($"{indent}                return true;");
        sb.AppendLine($"{indent}            }}");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine($"{indent}    value = default;");
        sb.AppendLine($"{indent}    return false;");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
        sb.AppendLine($"{indent}private static global::System.Text.Json.JsonException __InvalidArgument(string name, string typeName) =>");
        sb.AppendLine($"{indent}    new(\"The JSON value for argument '\" + name + \"' of tool '{EscapeString(toolName)}' could not be converted to \" + typeName + \".\");");
    }

    private static void GenerateParameterExtractor(StringBuilder sb, string indent, string toolName, ToolParameterModel p)
    {
        var propName = $"__{p.Name}Prop";
        var nameLiteral = $"\"{EscapeString(p.Name)}\"";
        var invalid = $"throw __InvalidArgument({nameLiteral}, \"{EscapeString(p.TypeDisplay)}\")";

        sb.AppendLine($"{indent}if (__TryFindArgument(__root, {nameLiteral}, out var {propName}))");
        sb.AppendLine($"{indent}{{");

        switch (p.TypeSpecial)
        {
            case SpecialType.System_String:
                sb.AppendLine($"{indent}    if ({propName}.ValueKind == global::System.Text.Json.JsonValueKind.String) {p.Name} = {propName}.GetString()!;");
                sb.AppendLine($"{indent}    else if ({propName}.ValueKind == global::System.Text.Json.JsonValueKind.Null) {p.Name} = null!;");
                sb.AppendLine($"{indent}    else {invalid};");
                break;
            case SpecialType.System_Boolean:
                sb.AppendLine($"{indent}    if ({propName}.ValueKind is global::System.Text.Json.JsonValueKind.True or global::System.Text.Json.JsonValueKind.False) {p.Name} = {propName}.GetBoolean();");
                sb.AppendLine($"{indent}    else {invalid};");
                break;
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Double:
            case SpecialType.System_Single:
            case SpecialType.System_Decimal:
                var getter = p.TypeSpecial switch
                {
                    SpecialType.System_Int32 => "TryGetInt32",
                    SpecialType.System_Int64 => "TryGetInt64",
                    SpecialType.System_Double => "TryGetDouble",
                    SpecialType.System_Single => "TryGetSingle",
                    _ => "TryGetDecimal",
                };
                sb.AppendLine($"{indent}    if ({propName}.ValueKind == global::System.Text.Json.JsonValueKind.Number && {propName}.{getter}(out var __{p.Name}Val)) {p.Name} = __{p.Name}Val;");
                sb.AppendLine($"{indent}    else {invalid};");
                break;
            default:
                // Non-primitive arguments use reflection-based System.Text.Json deserialization,
                // which is not NativeAOT/trimming safe. Errors propagate as JsonException.
                sb.AppendLine($"{indent}    {p.Name} = global::System.Text.Json.JsonSerializer.Deserialize<{p.TypeDisplay}>({propName})!;");
                break;
        }

        sb.AppendLine($"{indent}}}");

        if (!p.HasDefaultValue && !p.IsNullable)
        {
            sb.AppendLine($"{indent}else");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    throw new global::System.ArgumentException(\"Missing required argument '{EscapeString(p.Name)}' for tool '{EscapeString(toolName)}'.\");");
            sb.AppendLine($"{indent}}}");
        }
    }

    // Result formatting mirrors DelegateTool so both tool flavours return identical strings.
    private static void GenerateReturnStatement(StringBuilder sb, string indent, ITypeSymbol returnType, string callExpr)
    {
        var returnTypeStr = returnType.ToDisplayString();

        if (returnType.SpecialType == SpecialType.System_Void)
        {
            sb.AppendLine($"{indent}{callExpr};");
            sb.AppendLine($"{indent}return new global::System.Threading.Tasks.ValueTask<string>(\"null\");");
            return;
        }

        if (returnTypeStr is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
        {
            sb.AppendLine($"{indent}await {callExpr}.ConfigureAwait(false);");
            sb.AppendLine($"{indent}return \"true\";");
            return;
        }

        if (returnType is INamedTypeSymbol { IsGenericType: true } named &&
            (returnTypeStr.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal) ||
             returnTypeStr.StartsWith("System.Threading.Tasks.ValueTask<", StringComparison.Ordinal)))
        {
            sb.AppendLine($"{indent}var __result = await {callExpr}.ConfigureAwait(false);");
            sb.AppendLine($"{indent}return {FormatResultExpression(named.TypeArguments[0], "__result")};");
            return;
        }

        sb.AppendLine($"{indent}var __result = {callExpr};");
        sb.AppendLine($"{indent}return new global::System.Threading.Tasks.ValueTask<string>({FormatResultExpression(returnType, "__result")});");
    }

    private static string FormatResultExpression(ITypeSymbol type, string value)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                return $"{value} ?? \"null\"";
            case SpecialType.System_Boolean:
                return $"{value} ? \"true\" : \"false\"";
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Double:
            case SpecialType.System_Single:
            case SpecialType.System_Decimal:
                return $"{value}.ToString(global::System.Globalization.CultureInfo.InvariantCulture)";
            default:
                return $"{value} is null ? \"null\" : global::System.Text.Json.JsonSerializer.Serialize({value})";
        }
    }

    private static void GenerateExtensionClass(StringBuilder sb, string indent, INamedTypeSymbol typeSymbol, List<ToolMethodModel> methods)
    {
        var extClassName = $"{typeSymbol.Name}AgentToolsExtensions";
        var typeFullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        sb.AppendLine($"{indent}/// <summary>");
        sb.AppendLine($"{indent}/// Extension methods for registering compile-time generated agent tools from <see cref=\"{typeSymbol.Name}\"/>.");
        sb.AppendLine($"{indent}/// </summary>");
        sb.AppendLine($"{indent}[global::System.CodeDom.Compiler.GeneratedCode(\"MyAgents.Generators\", \"1.0.0.0\")]");
        sb.AppendLine($"{indent}public static class {extClassName}");
        sb.AppendLine($"{indent}{{");

        var allStatic = methods.All(m => m.IsStatic);

        if (allStatic)
        {
            foreach (var m in methods)
            {
                var toolClassName = $"{typeSymbol.Name}_{m.MethodName}_GeneratedTool";
                sb.AppendLine($"{indent}    public static global::MyAgents.Core.Tools.ITool As{m.MethodName}Tool()");
                sb.AppendLine($"{indent}        => {toolClassName}.Instance;");
                sb.AppendLine();
            }

            sb.AppendLine($"{indent}    public static global::System.Collections.Generic.IReadOnlyList<global::MyAgents.Core.Tools.ITool> AsAgentTools()");
            sb.AppendLine($"{indent}        => new global::MyAgents.Core.Tools.ITool[]");
            sb.AppendLine($"{indent}        {{");
            foreach (var m in methods)
            {
                var toolClassName = $"{typeSymbol.Name}_{m.MethodName}_GeneratedTool";
                sb.AppendLine($"{indent}            {toolClassName}.Instance,");
            }
            sb.AppendLine($"{indent}        }};");
        }
        else
        {
            foreach (var m in methods)
            {
                var toolClassName = $"{typeSymbol.Name}_{m.MethodName}_GeneratedTool";
                if (m.IsStatic)
                {
                    sb.AppendLine($"{indent}    public static global::MyAgents.Core.Tools.ITool As{m.MethodName}Tool()");
                    sb.AppendLine($"{indent}        => {toolClassName}.Instance;");
                }
                else
                {
                    sb.AppendLine($"{indent}    public static global::MyAgents.Core.Tools.ITool As{m.MethodName}Tool(this {typeFullName} instance)");
                    sb.AppendLine($"{indent}        => new {toolClassName}(instance);");
                }
                sb.AppendLine();
            }

            sb.AppendLine($"{indent}    public static global::System.Collections.Generic.IReadOnlyList<global::MyAgents.Core.Tools.ITool> AsAgentTools(this {typeFullName} instance)");
            sb.AppendLine($"{indent}        => new global::MyAgents.Core.Tools.ITool[]");
            sb.AppendLine($"{indent}        {{");
            foreach (var m in methods)
            {
                var toolClassName = $"{typeSymbol.Name}_{m.MethodName}_GeneratedTool";
                if (m.IsStatic)
                    sb.AppendLine($"{indent}            {toolClassName}.Instance,");
                else
                    sb.AppendLine($"{indent}            new {toolClassName}(instance),");
            }
            sb.AppendLine($"{indent}        }};");
        }

        sb.AppendLine($"{indent}}}");
    }

    private sealed class ToolMethodModel
    {
        public INamedTypeSymbol ContainingType { get; }
        public string MethodName { get; }
        public string ToolName { get; }
        public string? Description { get; }
        public bool IsStatic { get; }
        public ITypeSymbol ReturnType { get; }
        public List<ToolParameterModel> Parameters { get; }

        public ToolMethodModel(
            INamedTypeSymbol ContainingType,
            string MethodName,
            string ToolName,
            string? Description,
            bool IsStatic,
            ITypeSymbol ReturnType,
            List<ToolParameterModel> Parameters)
        {
            this.ContainingType = ContainingType;
            this.MethodName = MethodName;
            this.ToolName = ToolName;
            this.Description = Description;
            this.IsStatic = IsStatic;
            this.ReturnType = ReturnType;
            this.Parameters = Parameters;
        }
    }

    private sealed class ToolParameterModel
    {
        public string Name { get; }
        public string TypeDisplay { get; }
        public SpecialType TypeSpecial { get; }
        public bool IsCancellationToken { get; }
        public bool HasDefaultValue { get; }
        public string? DefaultValueLiteral { get; }
        public bool IsNullable { get; }
        public string? Description { get; }

        public ToolParameterModel(
            string Name,
            string TypeDisplay,
            SpecialType TypeSpecial,
            bool IsCancellationToken,
            bool HasDefaultValue,
            string? DefaultValueLiteral,
            bool IsNullable,
            string? Description)
        {
            this.Name = Name;
            this.TypeDisplay = TypeDisplay;
            this.TypeSpecial = TypeSpecial;
            this.IsCancellationToken = IsCancellationToken;
            this.HasDefaultValue = HasDefaultValue;
            this.DefaultValueLiteral = DefaultValueLiteral;
            this.IsNullable = IsNullable;
            this.Description = Description;
        }
    }
}
