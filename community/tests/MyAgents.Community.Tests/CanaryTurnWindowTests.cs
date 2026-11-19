using MyAgents.Core.Security;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

/// <summary>
/// Bounds tests for <see cref="CanaryTurnWindow"/>.
/// </summary>
/// <remarks>
/// Eviction is a correctness property, not an optimisation: an unbounded window would keep matching
/// fragments the session has long moved past, and would grow the payload handed to the scan cap until
/// every turn failed closed as oversized. These tests pin both bounds.
/// </remarks>
[TestFixture]
public sealed class CanaryTurnWindowTests
{
    [Test]
    public void Appended_turns_are_returned_oldest_first()
    {
        var window = new CanaryTurnWindow(maxTurns: 4);

        window.Append("one");
        window.Append("two");
        window.Append("three");

        Assert.That(window.Snapshot(), Is.EqualTo("onetwothree"));
        Assert.That(window.TurnCount, Is.EqualTo(3));
    }

    [Test]
    public void Oldest_turn_is_evicted_when_the_turn_bound_is_exceeded()
    {
        var window = new CanaryTurnWindow(maxTurns: 2);

        window.Append("one");
        window.Append("two");
        window.Append("three");

        Assert.Multiple(() =>
        {
            Assert.That(window.TurnCount, Is.EqualTo(2));
            Assert.That(window.Snapshot(), Is.EqualTo("twothree"));
            Assert.That(window.Snapshot(), Does.Not.Contain("one"));
        });
    }

    [Test]
    public void Retained_bytes_stay_within_the_budget_as_turns_arrive()
    {
        const int budget = 16;
        var window = new CanaryTurnWindow(maxTurns: 100, maxBytes: budget);

        for (var turn = 0; turn < 20; turn++)
        {
            window.Append("abcdefgh");
            Assert.That(window.RetainedBytes, Is.LessThanOrEqualTo(budget),
                $"the byte budget was exceeded after turn {turn}");
        }
    }

    [Test]
    public void A_single_turn_larger_than_the_budget_is_truncated_to_its_tail()
    {
        const int budget = 10;
        var window = new CanaryTurnWindow(maxBytes: budget);

        window.Append(new string('x', 500) + "TAILMARK");

        Assert.Multiple(() =>
        {
            Assert.That(window.RetainedBytes, Is.LessThanOrEqualTo(budget));
            Assert.That(window.Snapshot(), Does.EndWith("TAILMARK"),
                "the tail is where a fragment is most likely to sit");
        });
    }

    [Test]
    public void Empty_and_null_turns_are_not_retained()
    {
        var window = new CanaryTurnWindow();

        window.Append(string.Empty);
        window.Append(null!);

        Assert.That(window.TurnCount, Is.Zero);
        Assert.That(window.Snapshot(), Is.Empty);
    }

    [Test]
    public void Retained_bytes_counts_utf8_not_characters()
    {
        var window = new CanaryTurnWindow(maxTurns: 100, maxBytes: 64);

        // 'é' is two UTF-8 bytes, so 40 characters occupy 80 bytes and must be trimmed.
        window.Append(new string('é', 40));

        Assert.That(window.RetainedBytes, Is.LessThanOrEqualTo(64));
    }

    [Test]
    public void Constructor_rejects_non_positive_bounds()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CanaryTurnWindow(maxTurns: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CanaryTurnWindow(maxBytes: 0));
        });
    }
}
