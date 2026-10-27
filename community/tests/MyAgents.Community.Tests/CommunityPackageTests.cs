using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using MyAgents.Testing;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class CommunityPackageTests
{
    [Test]
    public async Task InMemoryAgentReturnsQueuedResponseAndRecordsContext()
    {
        var agent = new InMemoryAgent("community")
            .EnqueueResponse("hello", promptTokens: 3, completionTokens: 2);

        using var provider = new ServiceCollection().BuildServiceProvider();
        var context = new AgentTurnContext(
            "session-1",
            Array.Empty<AgentMessage>(),
            new AgentTurnServices(provider));

        var result = await agent.InvokeAsync(context);

        Assert.That(result.Output, Has.Count.EqualTo(1));
        Assert.That(result.Output[0].Content, Is.EqualTo("hello"));
        Assert.That(result.Telemetry.TotalTokens, Is.EqualTo(5));
        Assert.That(agent.InvocationCount, Is.EqualTo(1));
        Assert.That(agent.ReceivedContexts[0], Is.SameAs(context));
    }
}
