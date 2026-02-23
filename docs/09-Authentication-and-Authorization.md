# Authentication and Authorization

This document describes how authentication and authorization work in the current codebase. Every piece traces back to actual files and code.

## Table of Contents

1. [Overview](#1-overview)
2. [Infrastructure Components](#2-infrastructure-components)
3. [User Registration](#3-user-registration)
4. [Login](#4-login)
5. [JWT Validation](#5-jwt-validation)
6. [Claims Transformation](#6-claims-transformation)
7. [Authorization](#7-authorization)
8. [Configuration Reference](#8-configuration-reference)
9. [Integration Tests](#9-integration-tests)

---

## 1. Overview

Authentication is split across two systems that work together:

- **Keycloak** — handles password storage and JWT issuance. It is an external service running in Docker.
- **Our code** — handles everything else: roles, permissions, claims enrichment, and authorization policy enforcement.

The handoff point is the `identity_id` column in `users.users`. When a user registers, Keycloak assigns them a UUID. We store that UUID as `identity_id`. On every subsequent authenticated request, we use `identity_id` to look up that user's permissions in our own database.

```
Keycloak owns:  password hash, JWT signing key, user UUID
We own:         user profile, roles, permissions, authorization logic
```

---

## 2. Infrastructure Components

| Component | Role |
|---|---|
| `Keycloak` (Docker: `evently.identity`, port 18080) | Stores password hashes. Issues and signs JWTs using RSA. Exposes OIDC discovery endpoint. |
| `KeycloakIdentityProviderService` | Calls Keycloak's Admin REST API to create users during registration. |
| `KeyCloakClient` | The typed `HttpClient` wrapper that sends the actual HTTP requests to Keycloak. |
| `KeyCloakAuthDelegatingHandler` | A `DelegatingHandler` that automatically obtains a client-credentials admin token and adds it as a `Bearer` header before each request to `KeyCloakClient`. |
| `JwtBearerConfigureOptions` | Configures ASP.NET Core's JWT middleware to fetch Keycloak's RSA public key via OIDC discovery and validate incoming tokens against it. |
| `CustomClaimsTransformation` | Runs after every successful JWT validation. Looks up the user's internal ID and permissions from PostgreSQL. Adds them as custom claims. |
| `PermissionAuthorizationHandler` | Checks that the current user's claims contain the required permission code for the endpoint being called. |

---

## 3. User Registration

**Endpoint:** `POST /users/register`
**File:** `src/Modules/Users/Evently.Modules.Users.Presentation/Users/RegisterUser.cs`

### What happens, step by step

```
POST /users/register { Email, Password, FirstName, LastName }
         │
         ▼
RegisterUserCommandHandler
src/Modules/Users/Evently.Modules.Users.Application/Users/RegisterUser/RegisterUserCommandHandler.cs
         │
         ├─ calls IIdentityProviderService.RegisterUserAsync(UserModel)
         │         │
         │         ▼
         │   KeycloakIdentityProviderService
         │   src/.../Infrastructure/Identity/IdentityProviderService.cs
         │         │
         │         ├─ delegates to KeyCloakClient.RegisterUserAsync(UserRepresentation)
         │         │   src/.../Infrastructure/Identity/KeyCloakClient.cs
         │         │         │
         │         │         │  Before the request is sent, KeyCloakAuthDelegatingHandler runs:
         │         │         │  src/.../Infrastructure/Identity/KeyCloakAuthDelegatingHandler.cs
         │         │         │
         │         │         │  Step 1 — Get admin token:
         │         │         │    POST {KeyCloakOptions.TokenUrl}
         │         │         │    Body: grant_type=client_credentials
         │         │         │          client_id=ConfidentialClientId
         │         │         │          client_secret=ConfidentialClientSecret
         │         │         │    → short-lived admin access_token
         │         │         │
         │         │         │  Step 2 — Create user:
         │         │         │    POST {KeyCloakOptions.AdminUrl}/users
         │         │         │    Authorization: Bearer <admin_token>
         │         │         │    Body: { username, email, firstName, lastName,
         │         │         │            credentials: [{ type: "password", value: "...", temporary: false }] }
         │         │         │    → 201 Created
         │         │         │    → Location: .../admin/realms/evently/users/<keycloak-uuid>
         │         │         │
         │         │         └─ extracts Keycloak UUID from Location header
         │         │
         │         └─ returns Result<string> where the string is the Keycloak UUID
         │
         ├─ User.Create(email, firstName, lastName, identityId: keycloakUuid)
         │     → identity_id column = Keycloak's UUID
         │     → assigns Role.Member
         │     → raises UserRegisteredDomainEvent
         │
         └─ saves to users.users table
```

### What Keycloak stores vs what we store

| Data | Where it lives |
|---|---|
| Password hash | Keycloak's database |
| User UUID (identity_id) | Both: Keycloak's primary key, our `users.users.identity_id` foreign reference |
| Email, first name, last name | Both: Keycloak user record + our `users.users` table |
| Role (Member/Admin) | Only our `users.user_roles` table |
| Permissions | Only our `users.role_permissions` table |

---

## 4. Login

There is **no login endpoint in our API**. Users authenticate directly against Keycloak's OAuth2 token endpoint.

```
POST http://localhost:18080/realms/evently/protocol/openid-connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=password
&username=user@example.com
&password=secret
&client_id=evently-public-client
```

Response:
```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIsInR5...",
  "refresh_token": "eyJhbGciOiJIUzI1NiIsInR5...",
  "expires_in": 300,
  "token_type": "Bearer"
}
```

The access token is a JWT signed with **Keycloak's RSA private key**. Our API never sees the password — it only ever sees the resulting JWT on subsequent requests.

Token refresh (using `refresh_token`) also goes directly to Keycloak. Our API is not involved.

---

## 5. JWT Validation

Every request with an `Authorization: Bearer <token>` header goes through ASP.NET Core's `JwtBearerHandler`.

### How it is configured

**File:** `src/Common/Evently.Common.Infrastructure/Authentication/JwtBearerConfigureOptions.cs`

```csharp
internal sealed class JwtBearerConfigureOptions(IConfiguration configuration)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options)
    {
        configuration.GetSection("Authentication").Bind(options);
    }
}
```

**Registered in:** `src/Common/Evently.Common.Infrastructure/Authentication/AuthenticationExtensions.cs`

```csharp
services.AddAuthentication().AddJwtBearer();
services.ConfigureOptions<JwtBearerConfigureOptions>();
```

### Understanding how this wiring works

This is the standard ASP.NET Core named options pattern. It is worth understanding clearly because the two lines above are doing a lot of invisible work.

**`services.AddAuthentication().AddJwtBearer()`**

`AddAuthentication()` registers the authentication middleware and sets the default scheme. `.AddJwtBearer()` does two things:

1. Registers a new authentication scheme named `"Bearer"` and maps it to `JwtBearerHandler` — the built-in handler that knows how to read `Authorization: Bearer` headers, parse JWTs, and validate them.
2. Creates a named options slot: `JwtBearerOptions` keyed to the scheme name `"Bearer"`. The handler reads from this slot at runtime to know how to validate tokens.

At this point the slot exists but is empty — no issuer, no key, no audience configured yet.

**`services.ConfigureOptions<JwtBearerConfigureOptions>()`**

This registers `JwtBearerConfigureOptions` as an `IConfigureNamedOptions<JwtBearerOptions>` implementation. The framework will call its `Configure(string? name, JwtBearerOptions options)` method when anyone requests the `JwtBearerOptions` for the `"Bearer"` scheme. Our implementation ignores the name and always calls the single-argument `Configure(options)`, which does the config binding.

The net effect: when `JwtBearerHandler` first activates and reads its options, our `Configure` method runs and populates the options from `appsettings`.

**`configuration.GetSection("Authentication").Bind(options)`**

`Bind` maps JSON property names to C# property names by reflection. So this JSON:

```json
"Authentication": {
  "Audience": "account",
  "MetadataAddress": "http://...",
  "RequireHttpsMetadata": false,
  "TokenValidationParameters": {
    "ValidIssuers": [ "..." ]
  }
}
```

...becomes exactly equivalent to writing this in C#:

```csharp
options.Audience = "account";
options.MetadataAddress = "http://...";
options.RequireHttpsMetadata = false;
options.TokenValidationParameters.ValidIssuers = new[] { "..." };
```

This is how a handful of JSON lines fully configure the JWT validation pipeline — no manual key loading, no manual handler setup.

### Key properties of JwtBearerOptions

**`MetadataAddress`**

The URL of an OIDC discovery document (the `.well-known/openid-configuration` endpoint). When set, `JwtBearerHandler` fetches this document on the first request to find the `jwks_uri` field, then fetches the JWKS (JSON Web Key Set) from that URI to get the server's public signing keys. From that point on, JWT signatures are verified against those keys automatically. This is why setting one URL is enough to validate RSA-signed tokens from Keycloak — the handler does all the key fetching and caching itself.

**`Authority`**

An alternative to `MetadataAddress`. If you set `Authority = "http://host/realms/evently"`, the handler appends `/.well-known/openid-configuration` automatically. `MetadataAddress` is the explicit version.

**`RequireHttpsMetadata`**

Whether to require HTTPS when fetching the discovery document and JWKS. Set to `false` in development because Keycloak runs over plain HTTP locally.

**`Audience`**

A shorthand that sets `TokenValidationParameters.ValidAudience`. The JWT's `aud` claim must match this value, otherwise the token is rejected.

**`TokenValidationParameters`**

The nested object that contains all the fine-grained rules applied to each token. The most important properties:

| Property | What it controls |
|---|---|
| `ValidateIssuer` | Whether to check the `iss` claim at all. Default: `true`. |
| `ValidIssuer` / `ValidIssuers` | The expected value(s) of the `iss` claim. |
| `ValidateAudience` | Whether to check the `aud` claim. Default: `true`. |
| `ValidAudience` / `ValidAudiences` | The expected value(s) of the `aud` claim. |
| `ValidateLifetime` | Whether to check `exp` (expiry) and `nbf` (not before). Default: `true`. |
| `ClockSkew` | How much clock drift to tolerate between the token issuer and our server. Default: 5 minutes. Setting it to `TimeSpan.Zero` means tokens expire exactly on time. |
| `ValidateIssuerSigningKey` | Whether to verify the JWT signature. Default: `true`. Should never be set to `false` in production. |
| `IssuerSigningKey` | The key to verify signatures against. For symmetric (HS256): a `SymmetricSecurityKey`. For asymmetric (RS256): set automatically from the JWKS when `MetadataAddress` is configured — you don't set this manually. |

In the current Keycloak setup, `IssuerSigningKey` is **never set explicitly**. It is populated automatically by the handler after it fetches the JWKS from `MetadataAddress`.

### What happens at startup

On the first authenticated request, `JwtBearerHandler` fetches:

```
GET http://evently.identity:8080/realms/evently/.well-known/openid-configuration
→ JSON document containing, among other things, "jwks_uri"

GET {jwks_uri}
→ Keycloak's RSA public key(s) in JWKS format
```

The keys are cached in memory. Subsequent requests use the cached keys without making any network calls.

### What happens on each request

```
Incoming request with "Authorization: Bearer eyJhbGci..."
         │
         ▼
JwtBearerHandler
  ├─ Decodes the JWT header → algorithm: RS256
  ├─ Verifies the signature using the cached RSA public key from Keycloak's JWKS
  ├─ Checks TokenValidationParameters:
  │    issuer   ∈ ValidIssuers   (must be a Keycloak realm URL)
  │    audience == "account"
  │    exp      > now            (not expired)
  │
  └─ On success: populates HttpContext.User with claims from the JWT payload:
       sub: "abc-123-def-456"   ← Keycloak's UUID for this user
       iss: "http://evently.identity:8080/realms/evently"
       aud: "account"
       exp, iat, jti, ...
```

At this point, `HttpContext.User.Identity.IsAuthenticated` is `true`, but the user only has the raw Keycloak claims — no permissions, no internal user ID yet.

---

## 6. Claims Transformation

After JWT validation, `CustomClaimsTransformation` runs on every authenticated request. It enriches the `ClaimsPrincipal` with data from our database.

**File:** `src/Common/Evently.Common.Infrastructure/Authorization/CustomClaimsTransformation.cs`

```
CustomClaimsTransformation.TransformAsync(principal)
         │
         ├─ Skip if CustomClaims.Sub already present (idempotent guard)
         │
         ├─ Read identityId = principal.FindFirst(ClaimTypes.NameIdentifier).Value
         │    ClaimTypes.NameIdentifier maps to the JWT "sub" claim
         │    → value is Keycloak's UUID, e.g. "abc-123-def-456"
         │
         ▼
IPermissionService.GetUserPermissionsAsync(identityId)
src/Modules/Users/Evently.Modules.Users.Infrastructure/Authorization/PermissionService.cs
         │
         ▼
GetUserPermissionsQueryHandler  (Dapper)
src/Modules/Users/Evently.Modules.Users.Application/Users/GetUserPermissions/GetUserPermissionsQueryHandler.cs
         │
         │   SQL:
         │   SELECT DISTINCT u.id AS UserId, rp.permission_code AS Permission
         │   FROM users.users u
         │   JOIN users.user_roles ur ON ur.user_id = u.id
         │   JOIN users.role_permissions rp ON rp.role_name = ur.role_name
         │   WHERE u.identity_id = @IdentityId
         │
         └─ Returns PermissionsResponse(UserId: Guid, Permissions: HashSet<string>)

Back in CustomClaimsTransformation:
  ├─ Adds claim: CustomClaims.Sub = UserId   ← our internal Guid, NOT Keycloak's UUID
  └─ Adds claims: one per permission code
       e.g. CustomClaims.Permission = "events:read"
            CustomClaims.Permission = "orders:create"
            ...
```

### Why this matters

After transformation, any module can do:

```csharp
// Get our internal user ID (Guid from users.users.id)
Guid userId = httpContext.User.GetUserId();

// Get all permission codes
HashSet<string> permissions = httpContext.User.GetPermissions();
```

The `identity_id` column is the bridge that connects the Keycloak-issued JWT (which only knows the Keycloak UUID) to our internal user record (which has the Guid, roles, and permissions).

---

## 7. Authorization

Permission-based authorization is enforced via a custom policy provider and handler.

**Files:**
- `src/Common/Evently.Common.Infrastructure/Authorization/PermissionAuthorizationHandler.cs`
- `src/Common/Evently.Common.Infrastructure/Authorization/AuthorizationExtensions.cs`

Endpoints declare required permissions like this:

```csharp
app.MapGet("events/{id}", ...)
   .RequireAuthorization(Permissions.EventsRead);
```

At request time:

```
PermissionAuthorizationHandler
  ├─ Gets requirement.Permission (e.g. "events:read")
  ├─ Calls principal.GetPermissions()
  │    → reads all CustomClaims.Permission claims added by CustomClaimsTransformation
  │
  └─ Succeeds if permissions.Contains(requirement.Permission)
       else → 403 Forbidden
```

There are 17 permission codes defined across all modules. All are resolved from `users.role_permissions` — Keycloak has no knowledge of them.

---

## 8. Configuration Reference

### appsettings.Development.json (JWT validation)

```json
"Authentication": {
  "Audience": "account",
  "MetadataAddress": "http://evently.identity:8080/realms/evently/.well-known/openid-configuration",
  "TokenValidationParameters": {
    "ValidIssuers": [
      "http://evently.identity:8080/realms/evently",
      "http://localhost:18080/realms/evently"
    ]
  },
  "RequireHttpsMetadata": false
},
"KeyCloak": {
  "HealthUrl": "http://evently.identity:8080/health/"
}
```

### modules.users.Development.json (Keycloak Admin API)

```json
"Users": {
  "KeyCloak": {
    "AdminUrl": "http://evently.identity:8080/admin/realms/evently/",
    "TokenUrl": "http://evently.identity:8080/realms/evently/protocol/openid-connect/token",
    "ConfidentialClientId": "evently-confidential-client",
    "ConfidentialClientSecret": "PzotcrvZRF9BHCKcUxdKfHWlIPECG49k",
    "PublicClientId": "evently-public-client"
  }
}
```

**Why two clients?**

- `evently-confidential-client` — a server-side client with a secret, used by our backend to call the Admin API (client credentials flow). Never exposed to users.
- `evently-public-client` — a public client (no secret), used by the frontend/Postman to call Keycloak's `/token` endpoint and get access tokens (resource owner password flow).

### docker-compose.yml

```yaml
evently.identity:
  image: quay.io/keycloak/keycloak:latest
  command: start-dev --import-realm
  volumes:
    - ./.containers/identity:/opt/keycloak/data
    - ./.files:/opt/keycloak/data/import   # mounts evently-realm-export.json
  ports:
    - 18080:8080
```

The realm export at `.files/evently-realm-export.json` configures the `evently` realm, both clients (`evently-confidential-client` and `evently-public-client`), and any pre-seeded users used in development.

### Typed options class

**File:** `src/Modules/Users/Evently.Modules.Users.Infrastructure/Identity/KeyCloakOptions.cs`

```csharp
internal sealed class KeyCloakOptions
{
    public string AdminUrl { get; set; }           // base URL for Admin API calls
    public string TokenUrl { get; set; }           // URL to get client-credentials token
    public string ConfidentialClientId { get; set; }
    public string ConfidentialClientSecret { get; set; }
    public string PublicClientId { get; set; }     // not used by our backend directly
}
```

Bound in `UsersModule.cs`:
```csharp
services.Configure<KeyCloakOptions>(configuration.GetSection("Users:KeyCloak"));
```

---

## 9. Integration Tests

**File:** `test/Evently.IntegrationTests/Abstractions/IntegrationTestWebAppFactory.cs`

The test factory starts three Testcontainers:

```csharp
private readonly PostgreSqlContainer _dbContainer = ...
private readonly RedisContainer _redisContainer = ...
private readonly KeycloakContainer _keycloakContainer = new KeycloakBuilder("quay.io/keycloak/keycloak:latest")
    .WithResourceMapping(
        new FileInfo("evently-realm-export.json"),
        new FileInfo("/opt/keycloak/data/import/realm.json"))
    .WithCommand("--import-realm")
    .Build();
```

It then overrides the configuration env vars so the API points at the test container:

```csharp
string keycloakAddress = _keycloakContainer.GetBaseAddress();
string keyCloakRealmUrl = $"{keycloakAddress}realms/evently";

Environment.SetEnvironmentVariable(
    "Authentication:MetadataAddress",
    $"{keyCloakRealmUrl}/.well-known/openid-configuration");

Environment.SetEnvironmentVariable(
    "Authentication:TokenValidationParameters:ValidIssuer",
    keyCloakRealmUrl);

builder.ConfigureTestServices(services =>
{
    services.Configure<KeyCloakOptions>(o =>
    {
        o.AdminUrl = $"{keycloakAddress}admin/realms/evently/";
        o.TokenUrl = $"{keyCloakRealmUrl}/protocol/openid-connect/token";
    });
});
```

Keycloak takes approximately 15–20 seconds to start, which is the dominant cost in test suite startup time.
