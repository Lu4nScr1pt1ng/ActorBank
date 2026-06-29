var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL for Orleans (clustering + grain storage + reminders). The container starts with the
// "orleans" database and runs the schema in ./db/init on first boot — the same scripts the
// docker-compose setup uses. The connection string is injected into the API as "orleans", which
// AddActorBankSilo() picks up via ConnectionStrings:Orleans.
var orleansDb = builder.AddPostgres("postgres")
    .WithEnvironment("POSTGRES_DB", "orleans")
    .WithBindMount("../db/init", "/docker-entrypoint-initdb.d")
    .AddDatabase("orleans");

// The co-hosted API + Orleans silo. Add .WithReplicas(N) to run several silos in one cluster locally.
// Pin a stable HTTP port (8080) so the k6 tests work against Aspire unchanged — `./tests/run.sh`
// defaults to http://localhost:8080.
builder.AddProject<Projects.ActorBank_Api>("app")
    .WithReference(orleansDb)
    .WaitFor(orleansDb)
    .WithHttpEndpoint(name: "k6", port: 8080);

builder.Build().Run();
