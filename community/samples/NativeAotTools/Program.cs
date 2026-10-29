// Sample: NativeAOT-safe agent tools with [AgentTool(JsonContext = ...)].
//
// Demonstrates the source-generated tool authoring path: JSON schemas, argument binding,
// and result serialization are all driven by a JsonSerializerContext, so the whole
// application compiles and runs as a native AOT executable with no JIT and no reflection.
//
// Build & run natively:
//   dotnet publish community/samples/NativeAotTools -c Release
//   ./community/samples/NativeAotTools/bin/Release/net10.0/win-x64/publish/NativeAotTools

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyAgents.Core.Tools;

namespace NativeAotTools;

public sealed record TicketRequest(string Subject, int Priority = 3);

public sealed record TicketReport(int Id, string Subject, int Priority, string Status);

[JsonSerializable(typeof(TicketRequest))]
[JsonSerializable(typeof(TicketReport))]
[JsonSerializable(typeof(TicketReport[]))]
internal sealed partial class TicketJsonContext : JsonSerializerContext;

public static class SupportTools
{
    [AgentTool(Description = "Creates a support ticket", JsonContext = typeof(TicketJsonContext))]
    public static TicketReport CreateTicket(
        [Description("The ticket request")] TicketRequest request)
        => new(Id: 42, request.Subject, request.Priority, "open");

    [AgentTool(Description = "Lists open support tickets", JsonContext = typeof(TicketJsonContext))]
    public static async Task<TicketReport[]> ListOpenTicketsAsync(CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return [new(42, "Printer on fire", 1, "open")];
    }
}

public static class Program
{
    public static int Main()
    {
        var tools = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(SupportTools.CreateTicket)] = SupportToolsAgentToolsExtensions.AsCreateTicketTool(),
            [nameof(SupportTools.ListOpenTicketsAsync)] = SupportToolsAgentToolsExtensions.AsListOpenTicketsAsyncTool(),
        };

        // 1. Advertise tool schemas — complex types carry the real exported schema
        //    (nested properties, types, required fields), not a bare placeholder.
        foreach (var tool in tools.Values)
        {
            Console.WriteLine($"=== {tool.Name}: {tool.Description}");
            Console.WriteLine(tool.ParametersSchemaJson);
        }

        // 2. Simulate model tool calls end to end.
        var created = Invoke(tools, "CreateTicket", """{"request": {"Subject": "Login broken", "Priority": 1}}""");
        Console.WriteLine($"create_ticket -> {created}");
        Check(created == """{"Id":42,"Subject":"Login broken","Priority":1,"Status":"open"}""", "complex argument binding and result serialization");

        var listed = Invoke(tools, "ListOpenTicketsAsync", "{}");
        Console.WriteLine($"list_open_tickets -> {listed}");
        Check(listed == """[{"Id":42,"Subject":"Printer on fire","Priority":1,"Status":"open"}]""", "complex async result serialization");

        // 3. Guardrails: missing and mistyped arguments are rejected by the generated binder.
        CheckThrows<ArgumentException>(() => Invoke(tools, "CreateTicket", "{}"), "missing required argument");
        CheckThrows<JsonException>(() => Invoke(tools, "CreateTicket", """{"request": {"Subject": 99}}"""), "mistyped argument");

        Console.WriteLine("NativeAOT tool sample: all checks passed.");
        return 0;
    }

    private static string Invoke(Dictionary<string, ITool> tools, string name, string jsonArguments)
    {
        var result = tools[name].InvokeAsync(jsonArguments).AsTask().GetAwaiter().GetResult();
        return result;
    }

    private static void Check(bool condition, string what)
    {
        if (!condition)
        {
            Console.Error.WriteLine($"FAILED: {what}");
            Environment.Exit(1);
        }
    }

    private static void CheckThrows<TException>(Action action, string what)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {what} threw {ex.GetType().Name} instead of {typeof(TException).Name}");
            Environment.Exit(1);
        }

        Console.Error.WriteLine($"FAILED: {what} did not throw {typeof(TException).Name}");
        Environment.Exit(1);
    }
}
