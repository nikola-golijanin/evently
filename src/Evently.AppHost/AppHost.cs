using StackExchange.Redis;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL with persistent volume
IResourceBuilder<PostgresServerResource> postgres = builder
    .AddPostgres("postgres")
    .WithHostPort(5432)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("evently-db")
    .WithPgAdmin();

IResourceBuilder<PostgresDatabaseResource> eventlyDb = postgres.AddDatabase("Database");

// Redis with persistent volume
IResourceBuilder<RedisResource> cache = builder
    .AddRedis("Cache")
    .WithDataVolume("evently-redis-data");

// Keycloak with realm import from .files/ directory
IResourceBuilder<KeycloakResource> keycloak = builder
    .AddKeycloak("keycloak", port: 18080)
    .WithDataVolume("evently-keycloak-data")
    .WithRealmImport("../../../evently/.files");

EndpointReference keycloakEndpoint = keycloak.GetEndpoint("http");

// Evently API
builder.AddProject<Projects.Evently_Api>("evently-api")
    .WithReference(eventlyDb)
    .WaitFor(eventlyDb)
    .WithReference(cache)
    .WaitFor(cache)
    .WaitFor(keycloak)
    // Override all Keycloak URLs (appsettings.Development.json uses Docker hostname "evently.identity")
    .WithEnvironment("Authentication__MetadataAddress", ReferenceExpression.Create($"{keycloakEndpoint}/realms/evently/.well-known/openid-configuration"))
    .WithEnvironment("Authentication__TokenValidationParameters__ValidIssuers__0", ReferenceExpression.Create($"{keycloakEndpoint}/realms/evently"))
    .WithEnvironment("KeyCloak__HealthUrl", ReferenceExpression.Create($"{keycloakEndpoint}/health/"))
    .WithEnvironment("Users__KeyCloak__AdminUrl", ReferenceExpression.Create($"{keycloakEndpoint}/admin/realms/evently/"))
    .WithEnvironment("Users__KeyCloak__TokenUrl", ReferenceExpression.Create($"{keycloakEndpoint}/realms/evently/protocol/openid-connect/token"));

await builder.Build().RunAsync();
