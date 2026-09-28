using System.Reflection;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Conformance;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core;

namespace UnityRuntimeAnalysisAgent.Protocol.Tests;

/// <summary>
/// Replays the pinned protocol's golden fixtures through its package, as consumed by the agent. Handler conformance
/// (the agent's own responses to fixture requests) is added here as the handlers land.
/// </summary>
public sealed class ProtocolFixtureTests
{
    private static readonly string Root = typeof(ProtocolFixtureTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "ProtocolRoot").Value!;

    private static readonly Lazy<IReadOnlyDictionary<string, FixtureCase>> Fixtures =
        new(() => FixtureCatalog.Load(Path.Combine(Root, "fixtures", "agent")).ToDictionary(f => f.Id));

    private static readonly Lazy<IReadOnlyList<FileFixture>> Files = new(() => FileFixtures.Load(Path.Combine(Root, "fixtures", "files")));

    public static TheoryData<string> FixtureIds => new(Fixtures.Value.Keys.Order(StringComparer.Ordinal));

    public static TheoryData<string> FileFixtureIds => new(Files.Value.Select(f => f.Id));

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void Fixture_replays(string id)
    {
        var problems = FixtureReplay.Check(Fixtures.Value[id]);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [MemberData(nameof(FileFixtureIds))]
    public void File_fixture_replays(string id)
    {
        var problems = FileFixtures.Check(Files.Value.Single(f => f.Id == id));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_protocol_method_and_event_has_fixtures()
    {
        // Guards against an empty or wrong submodule checkout.
        var fixtures = Fixtures.Value.Values.ToList();
        Assert.NotEmpty(fixtures);

        var methodsWithFixtures = fixtures.Where(f => !f.IsEvent && !f.IsGeneric).Select(f => f.Group).ToHashSet();
        var eventsWithFixtures = fixtures.Where(f => f.IsEvent).Select(f => f.Group).ToHashSet();
        var methodsWithout = MethodRegistry.All.Keys.Where(m => !methodsWithFixtures.Contains(m)).ToList();
        var eventsWithout = EventRegistry.All.Keys.Where(e => !eventsWithFixtures.Contains(e)).ToList();
        Assert.True(methodsWithout.Count == 0, "Methods without fixtures: " + string.Join(", ", methodsWithout));
        Assert.True(eventsWithout.Count == 0, "Events without fixtures: " + string.Join(", ", eventsWithout));
    }

    [Fact]
    public void The_agent_speaks_the_pinned_protocol_version()
    {
        Assert.Equal(ProtocolVersion.Text, AgentIdentity.ProtocolVersionText);
    }
}
