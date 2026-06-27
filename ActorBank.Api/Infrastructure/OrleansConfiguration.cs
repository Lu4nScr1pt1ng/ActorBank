using System.Data.Common;
using Orleans.Configuration;
using Npgsql;

namespace ActorBank.Api.Infrastructure;

/// <summary>
/// Wires up the co-hosted Orleans silo against PostgreSQL: ADO.NET clustering (so multiple silos
/// can form one cluster), ADO.NET grain storage, the ADO.NET reminder service, and transactions.
/// </summary>
public static class OrleansConfiguration
{
    public static WebApplicationBuilder AddActorBankSilo(this WebApplicationBuilder builder)
    {
        // Orleans' ADO.NET providers resolve a DbProviderFactory by invariant name.
        DbProviderFactories.RegisterFactory("Npgsql", NpgsqlFactory.Instance);

        var connectionString = BuildConnectionString(
            builder.Configuration.GetConnectionString("Orleans")
            ?? "Host=localhost;Port=5432;Database=orleans;Username=orleans;Password=orleans");

        var clusterId = builder.Configuration["Orleans:ClusterId"] ?? "actorbank";
        var serviceId = builder.Configuration["Orleans:ServiceId"] ?? "actorbank";

        builder.UseOrleans(silo =>
        {
            silo.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = clusterId;   // a deployment of cooperating silos
                options.ServiceId = serviceId;   // stable across deployments; namespaces persisted state
            });

            // Membership in PostgreSQL: silos discover each other via the shared tables, so the
            // cluster can run many nodes against the same database.
            silo.UseAdoNetClustering(options =>
            {
                options.Invariant = "Npgsql";
                options.ConnectionString = connectionString;
            });

            // Durable grain storage. "accountStore" holds the small per-account balance, "ledgerStore"
            // the append-only ledger pages, "credentialStore" the login credentials.
            silo.AddAdoNetGrainStorage("accountStore", options =>
            {
                options.Invariant = "Npgsql";
                options.ConnectionString = connectionString;
            });
            silo.AddAdoNetGrainStorage("ledgerStore", options =>
            {
                options.Invariant = "Npgsql";
                options.ConnectionString = connectionString;
            });
            silo.AddAdoNetGrainStorage("credentialStore", options =>
            {
                options.Invariant = "Npgsql";
                options.ConnectionString = connectionString;
            });

            // Durable reminders (scheduled interest) in PostgreSQL.
            silo.UseAdoNetReminderService(options =>
            {
                options.Invariant = "Npgsql";
                options.ConnectionString = connectionString;
            });

            silo.UseTransactions();
        });

        return builder;
    }

    /// <summary>Adds sensible pooling/identification defaults for the app's DB connections.</summary>
    private static string BuildConnectionString(string raw) =>
        new NpgsqlConnectionStringBuilder(raw)
        {
            Pooling = true,
            // Each silo keeps its own pool, so transactional commits don't queue behind a tiny pool
            // under load. Keep (silo count) × MaxPoolSize below PostgreSQL's max_connections — the
            // compose file raises that to 500, leaving headroom for ~3 silos at this size.
            MaxPoolSize = 128,
            ApplicationName = "ActorBank",
        }.ConnectionString;
}
