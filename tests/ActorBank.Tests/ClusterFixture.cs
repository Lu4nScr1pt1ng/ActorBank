using Orleans.Hosting;
using Orleans.TestingHost;

namespace ActorBank.Tests;

/// <summary>
/// Configures the in-process test silo to mirror production: the same storage provider names, the
/// transaction subsystem, and an in-memory reminder service — but backed by memory instead of
/// PostgreSQL, so the suite is hermetic and needs no Docker.
/// </summary>
public sealed class TestSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage("accountStore");
        siloBuilder.AddMemoryGrainStorage("ledgerStore");
        siloBuilder.AddMemoryGrainStorage("credentialStore");
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.UseTransactions();
    }
}

/// <summary>Boots one test cluster, shared across the test class.</summary>
public sealed class ClusterFixture : IDisposable
{
    public TestCluster Cluster { get; }

    public ClusterFixture()
    {
        var builder = new TestClusterBuilder(initialSilosCount: 1);
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        Cluster = builder.Build();
        Cluster.Deploy();
    }

    public void Dispose() => Cluster.StopAllSilos();
}
