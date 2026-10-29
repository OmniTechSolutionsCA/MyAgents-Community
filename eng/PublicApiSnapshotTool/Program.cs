using System.Reflection;
using System.Runtime.Loader;
using System.Text;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PublicApiSnapshotTool <assembly-path> <output-path>");
    return 1;
}

var assemblyPath = Path.GetFullPath(args[0]);
if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine($"Assembly not found: {assemblyPath}");
    return 1;
}

var outputPath = Path.GetFullPath(args[1]);
var assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
{
    var dependencyPath = Path.Combine(assemblyDirectory, $"{assemblyName.Name}.dll");
    return File.Exists(dependencyPath)
        ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependencyPath)
        : null;
};

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
var content = Generate(assembly);
await File.WriteAllTextAsync(outputPath, content);
return 0;

static string Generate(Assembly assembly)
{
    var builder = new StringBuilder();
    var types = assembly.ExportedTypes
        .Where(static type => !type.IsSpecialName)
        .OrderBy(static type => type.FullName, StringComparer.Ordinal)
        .ToArray();

    foreach (var type in types)
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

static string FormatTypeDeclaration(Type type)
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

static string FormatParameters(IEnumerable<ParameterInfo> parameters)
    => string.Join(", ", parameters.Select(static parameter => $"{FormatTypeName(parameter.ParameterType)} {parameter.Name}"));

static string FormatConstructorSignature(ConstructorInfo constructor)
    => $"{constructor.DeclaringType!.Name}({FormatParameters(constructor.GetParameters())})";

static string FormatMethodSignature(MethodInfo method)
    => $"{FormatTypeName(method.ReturnType)} {method.Name}({FormatParameters(method.GetParameters())})";

static string FormatTypeName(Type type)
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
