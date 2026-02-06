# Technical Feature Requests

Technical improvements, refactorings, and infrastructure changes. Organized by priority.

## Table of Contents

- [Completed](#completed)
  - [TFR-000: Migrate MassTransit from InMemory to PostgreSQL Transport](#tfr-000-migrate-masstransit-from-inmemory-to-postgresql-transport)
- [Priority 1: High-Impact Improvements](#priority-1-high-impact-improvements)
  - [TFR-001: Replace Keycloak with ASP.NET Identity](#tfr-001-replace-keycloak-with-aspnet-identity)
  - [TFR-002: Stabilize MassTransit Version](#tfr-002-stabilize-masstransit-version)
  - [TFR-003: Add Resilience with Polly](#tfr-003-add-resilience-with-polly)
  - [TFR-004: Expand Integration Test Coverage](#tfr-004-expand-integration-test-coverage)
- [Priority 2: API & Infrastructure Hardening](#priority-2-api--infrastructure-hardening)
  - [TFR-005: Add Rate Limiting](#tfr-005-add-rate-limiting)
  - [TFR-006: Add API Versioning](#tfr-006-add-api-versioning)
  - [TFR-007: Improve Health Checks](#tfr-007-improve-health-checks)
  - [TFR-008: Add Response Compression & CORS](#tfr-008-add-response-compression--cors)
- [Priority 3: Observability & Performance](#priority-3-observability--performance)
  - [TFR-009: Persist Quartz Scheduler State](#tfr-009-persist-quartz-scheduler-state)
  - [TFR-010: Multi-Level Caching (L1 + L2)](#tfr-010-multi-level-caching-l1--l2)
  - [TFR-011: Structured Configuration Validation](#tfr-011-structured-configuration-validation)
  - [TFR-012: Enhance OpenAPI Documentation](#tfr-012-enhance-openapi-documentation)

---

## Completed

---

### TFR-000: Migrate MassTransit from InMemory to PostgreSQL Transport

**Status:** Completed | **Branch:** `feat/move-in-memory-msg-to-postgres-masstransit`

**Problem:** MassTransit was configured with `UsingInMemory`, meaning all messages were lost on application restart. The CancelEventSaga state was stored in Redis via `RedisRepository`, mixing cache and state concerns. This setup was unsuitable for reliable message delivery and saga durability.

**What was done:**

| Component | Before | After |
|-----------|--------|-------|
| Message transport | InMemory (`UsingInMemory`) | PostgreSQL SQL Transport (`UsingPostgres`) |
| Transport schema | N/A | Dedicated `transport` schema with LISTEN/NOTIFY |
| Saga state storage | Redis (`RedisRepository`) | Entity Framework (`EntityFrameworkRepository`) |
| Saga state table | N/A | `events.cancel_event_saga_state` (PostgreSQL) |
| MassTransit packages | `MassTransit 9.0.1-develop.45`, `MassTransit.Redis` | `MassTransit 9.0.0` (stable), `MassTransit.SqlTransport.PostgreSQL`, `MassTransit.EntityFrameworkCore` |
| Infrastructure | Required Redis for both cache + saga | Redis for cache only; PostgreSQL handles transport + saga |
| Schema migration | Manual | `AddPostgresMigrationHostedService()` auto-creates transport tables |

**Files changed:**

- `Directory.Packages.props` -- Added `MassTransit.EntityFrameworkCore` and `MassTransit.SqlTransport.PostgreSQL` (9.0.0 stable), removed `MassTransit.Redis`, stabilized `MassTransit` from `9.0.1-develop.45` to `9.0.0`
- `InfrastructureConfiguration.cs` -- Added `SqlTransportOptions` configuration (parsed from existing DB connection string), `AddPostgresMigrationHostedService()`, replaced `UsingInMemory` with `UsingPostgres`
- `EventsModule.cs` -- Changed `ConfigureConsumers` to use `EntityFrameworkRepository` with `EventsDbContext` instead of `RedisRepository`; removed `redisConnectionString` parameter
- `EventsDbContext.cs` -- Added `DbSet<CancelEventState>` and applied `CancelEventStateConfiguration`
- `Program.cs` -- Updated `EventsModule.ConfigureConsumers()` call (no longer passes Redis connection string)
- `Evently.Common.Infrastructure.csproj` -- Added `MassTransit.SqlTransport.PostgreSQL`, removed `MassTransit.Redis`
- `Evently.Modules.Events.Infrastructure.csproj` -- Added `MassTransit.EntityFrameworkCore`

**New files:**

- `CancelEventStateConfiguration.cs` -- EF Core entity type configuration for `CancelEventState` saga state
- `20260206150427_AddCancelEventSagaState.cs` -- EF migration creating `events.cancel_event_saga_state` table

**Follow-up suggestions:**

1. **Add optimistic concurrency to saga state** -- Implement `ISagaVersion` on `CancelEventState` and configure `builder.Property(x => x.Version).IsRowVersion()` in `CancelEventStateConfiguration`. This prevents concurrent saga updates from silently overwriting each other.
2. **Remove `MassTransitPostgresMigration.md`** -- The migration plan document at the repo root served its purpose and can be deleted after merging.

---

## Priority 1: High-Impact Improvements

---

### TFR-001: Replace Keycloak with ASP.NET Identity

**Problem:** Keycloak adds significant operational complexity for what this project actually uses it for. The entire Keycloak integration boils down to:
1. `POST /admin/realms/{realm}/users` -- create user with password
2. JWT token issuance via OIDC
3. Token validation via OIDC metadata endpoint

Meanwhile the authorization system (roles, permissions, claims transformation) is **already fully local** -- it queries the PostgreSQL database via `IPermissionService`, not Keycloak. The Keycloak dependency forces:
- A Docker container in dev (800MB+ image, slow startup)
- A Testcontainer in integration tests (adds ~15-20s to test suite startup)
- Realm export/import JSON files for configuration
- Client ID/secret management
- Health check for an external service
- Complex `appsettings` with internal Docker hostnames

**Proposal:** Replace Keycloak with ASP.NET Core Identity + local JWT issuance.

**Why this is a clean swap:**
- `IIdentityProviderService` is already a clean abstraction with a single method -- swap implementation, not interface
- `CustomClaimsTransformation` stays exactly the same -- it already queries the local DB
- Permission model (roles, permissions, role_permissions tables) stays exactly the same
- All 17 permissions, both roles, and the authorization pipeline are untouched
- Only the Users module Infrastructure layer changes

**What changes:**

| Component | Current (Keycloak) | New (ASP.NET Identity) |
|-----------|-------------------|----------------------|
| User storage | Keycloak DB + local `users` table | Identity tables in `users` schema only |
| Password hashing | Keycloak | ASP.NET Identity (PBKDF2) |
| User registration | Keycloak Admin API call | `UserManager<T>.CreateAsync()` |
| Token issuance | Keycloak OIDC endpoint | Local JWT generation (`JwtSecurityTokenHandler`) |
| Token validation | Keycloak OIDC metadata | Local symmetric/asymmetric key validation |
| Login endpoint | Keycloak's `/token` endpoint | New `POST /users/login` endpoint |
| Docker services | PostgreSQL + Redis + Keycloak | PostgreSQL + Redis (one less container) |
| Test infrastructure | `KeycloakContainer` testcontainer | Nothing extra needed |
| Config complexity | AdminUrl, TokenUrl, ClientId, ClientSecret, realm config | JWT signing key, token expiry |

**New entities in Users schema:**
- `AspNetUsers` (or extend existing `users` table)
- `AspNetUserTokens` (refresh tokens)
- No need for `AspNetRoles` / `AspNetUserRoles` -- we already have our own role/permission tables

**New endpoints needed:**
- `POST /users/login` -- Authenticate with email/password, return JWT + refresh token
- `POST /users/refresh` -- Refresh an expired JWT using refresh token
- Possibly `POST /users/change-password`

**Implementation approach:**
1. Add `Microsoft.AspNetCore.Identity.EntityFrameworkCore` package
2. Create `IdentityProviderService` implementing existing `IIdentityProviderService`
3. Use `UserManager<ApplicationUser>` for registration, password validation
4. Create `TokenService` for local JWT generation with configurable signing key
5. Add login/refresh endpoints to Users Presentation layer
6. Remove Keycloak from docker-compose, appsettings, health checks
7. Remove `Testcontainers.Keycloak` package
8. Update integration test factory (no Keycloak container)

**What does NOT change:**
- `CustomClaimsTransformation` -- still transforms `sub` claim to permissions
- `IPermissionService` -- still queries local DB
- All `RequireAuthorization("permission-code")` on endpoints
- Role/Permission seed data
- All other modules (Events, Ticketing, Attendance) -- zero changes

**Risk:** Low. The abstraction boundary (`IIdentityProviderService`) is clean. This is an infrastructure swap, not a domain change.

**Estimated scope:** Users.Infrastructure (new Identity implementation), Users.Presentation (login endpoint), Common.Infrastructure (JWT config), Program.cs (remove Keycloak health check), docker-compose (remove Keycloak service).

---

### TFR-002: Stabilize MassTransit Version

**Status:** Partially completed (MassTransit stabilized in TFR-000; Newtonsoft.Json remains)

**Problem:** ~~The project uses `MassTransit 9.0.1-develop.45` -- a **pre-release development build**.~~ MassTransit was stabilized to `9.0.0` as part of TFR-000. However, `Newtonsoft.Json 13.0.5-beta1` is still a beta version.

**Remaining work:**
1. Upgrade Newtonsoft.Json to the latest stable release
2. Verify serialization still works correctly (MassTransit message serialization, outbox/inbox payloads)

**Risk:** Low. Newtonsoft.Json beta-to-stable upgrades rarely have breaking changes.

---

### TFR-003: Add Resilience with Polly

**Problem:** No resilience policies exist anywhere in the system. Database calls, Redis operations, HTTP calls, and MassTransit publishing all lack retry logic, circuit breakers, and timeout policies.

Current issues:
- Redis connection failure is caught with a bare `catch` block that silently falls back to memory cache -- no logging, no metrics
- Database transient errors (connection pool exhaustion, brief network blips) cause immediate hard failures
- No circuit breaker to prevent cascading failures
- No timeout policies beyond default HTTP/DB timeouts

**Proposal:** Add `Microsoft.Extensions.Http.Resilience` and `Microsoft.Extensions.Resilience` (built-in Polly v8 integration in .NET 8+).

**Where to add resilience:**

| Component | Policy | Rationale |
|-----------|--------|-----------|
| Database calls | Retry (3x, exponential backoff) | Transient connection errors |
| Redis operations | Retry (2x) + Circuit breaker | Redis restart/network blip |
| MassTransit publishing | Retry (3x) | Message bus temporary unavailability |
| Keycloak/Identity HTTP calls | Retry + Timeout + Circuit breaker | External service dependency |
| Payment service | Retry + Timeout | External gateway (when real) |

**Implementation approach:**
1. Add `Microsoft.Extensions.Resilience` package
2. Create resilience pipelines in `InfrastructureConfiguration`
3. Fix the bare `catch` block in Redis fallback -- add proper logging
4. Add named resilience pipelines for different scenarios (fast-retry, slow-retry, circuit-breaker)

---

### TFR-004: Expand Integration Test Coverage

**Problem:** Only 2 integration test examples exist (`RegisterUser`, `AddItemToCart`). For a project with 31 endpoints and complex cross-module flows, this is minimal coverage.

**Proposal:** Build out integration tests for critical paths:

**Priority test scenarios:**
1. Full event lifecycle: Create -> Add ticket types -> Publish -> Verify cross-module propagation
2. Full purchase flow: Register user -> Add to cart -> Create order -> Verify tickets generated
3. Event cancellation saga: Cancel event -> Verify payments refunded + tickets archived + saga completes
4. Check-in flow: Issue ticket -> Check in -> Verify statistics updated
5. Duplicate/invalid check-in: Verify correct error responses and statistics tracking
6. Concurrent order creation: Multiple users buying last tickets -> verify no overselling
7. Cart expiration: Add items -> Wait for TTL -> Verify cart empty

**Infrastructure improvements:**
- Add a `BaseIntegrationTest` with common helpers (create user, create event, publish event, etc.)
- Add test data builders using Bogus/Faker for all entities
- Add assertion helpers for cross-module event propagation (wait for outbox/inbox processing)
- Consider adding `Respawner` for fast database cleanup between tests

**Scope:** New test classes in each module's integration test project.

---

## Priority 2: API & Infrastructure Hardening

---

### TFR-005: Add Rate Limiting

**Problem:** No rate limiting exists. Any client can flood the API with unlimited requests. Critical for:
- Login endpoint (brute force protection)
- Order creation (prevent abuse)
- Registration (prevent spam accounts)

**Proposal:** Add ASP.NET Core's built-in rate limiting middleware (`Microsoft.AspNetCore.RateLimiting`).

**Rate limiting policies:**

| Policy | Applies to | Limit | Window |
|--------|-----------|-------|--------|
| `fixed-global` | All endpoints | 100 req | per minute per IP |
| `auth-strict` | `/users/login`, `/users/register` | 10 req | per minute per IP |
| `order-limit` | `POST /orders` | 5 req | per minute per user |
| `sliding-general` | All authenticated endpoints | 60 req | per minute per user |

**Implementation:**
```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("fixed-global", ...)
    options.AddFixedWindowLimiter("auth-strict", ...)
    options.AddTokenBucketLimiter("order-limit", ...)
});
```

---

### TFR-006: Add API Versioning

**Problem:** API is hardcoded to "v1" in Swagger with no versioning infrastructure. If we need to make breaking changes to endpoints, there's no mechanism to maintain backward compatibility.

**Proposal:** Add `Asp.Versioning.Http` and `Asp.Versioning.Mvc.ApiExplorer` for URL-based API versioning.

**Approach:** URL segment versioning (`/api/v1/events`, `/api/v2/events`).

**Implementation:**
1. Add versioning packages
2. Configure default version (1.0)
3. Add version groups to Swagger
4. Prefix all routes with `/api/v1/`
5. When v2 is needed, add versioned endpoint groups

---

### TFR-007: Improve Health Checks

**Problem:** Current health checks cover PostgreSQL, Redis, and Keycloak (URI check). Missing:
- MassTransit connectivity
- No liveness/readiness distinction (important for Kubernetes)
- No startup probe
- No detailed health check response

**Proposal:**

**Add health check endpoints:**
- `GET /health/live` -- Liveness: app is running (always 200 unless crashed)
- `GET /health/ready` -- Readiness: app can serve traffic (all dependencies up)
- `GET /health/startup` -- Startup: app has finished initialization

**Add missing checks:**
- MassTransit bus health (`MassTransit.AspNetCoreIntegration` has built-in health check)
- Outbox/inbox backlog check (alert if unprocessed messages > threshold)
- Quartz scheduler health

**After TFR-001:** Remove Keycloak health check, no replacement needed.

---

### TFR-008: Add Response Compression & CORS

**Problem:**
- No response compression -- JSON responses sent uncompressed
- No CORS configuration visible -- browsers may be blocked from calling the API

**Proposal:**

**Compression:**
```csharp
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
```

**CORS:**
```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("default", policy =>
    {
        policy.WithOrigins("http://localhost:3000") // frontend
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});
```

---

## Priority 3: Observability & Performance

---

### TFR-009: Persist Quartz Scheduler State

**Problem:** Quartz scheduler uses in-memory job storage with a random GUID instance ID. If the application restarts:
- All scheduled jobs are lost
- Outbox/inbox processing timers reset
- No visibility into job execution history
- No cluster support (multiple instances would duplicate jobs)

**Proposal:** Switch Quartz to persistent `AdoJobStore` backed by PostgreSQL.

**Benefits:**
- Jobs survive application restarts
- Job execution history available for debugging
- Cluster mode support (only one instance processes each job)
- Dashboard integration possible (Quartz.NET monitoring)

**Implementation:**
1. Add `Quartz.Serialization.Json` package
2. Configure `AdoJobStore` with PostgreSQL provider
3. Create Quartz schema tables (built-in migration script)
4. Use clustered mode with `InstanceId = "AUTO"`

---

### TFR-010: Multi-Level Caching (L1 + L2)

**Problem:** Current caching is single-level (Redis or memory fallback). For frequently read data (event details, categories, permissions), every cache miss goes to Redis over the network.

**Proposal:** Add in-memory L1 cache in front of Redis L2 cache.

**Pattern:**
```
Request -> L1 (IMemoryCache, in-process, ~1ms)
       -> L2 (Redis, network, ~5ms)
       -> Database (~20ms)
```

**What to cache at L1:**
- User permissions (queried on every request via `CustomClaimsTransformation`)
- Category list (rarely changes)
- Event details for published events (immutable after publish)

**Cache invalidation:** Use Redis pub/sub to invalidate L1 across multiple instances when data changes.

**Implementation:** Create `HybridCacheService` wrapping `IMemoryCache` + `IDistributedCache` with configurable L1 TTL.

---

### TFR-011: Structured Configuration Validation

**Problem:** Configuration is loaded from multiple JSON files per module (`modules.users.json`, `modules.events.json`, etc.) with no validation. Missing or malformed config values fail at runtime, not startup.

**Proposal:** Add `IValidateOptions<T>` implementations for all configuration sections.

**Validate at startup:**
- Database connection string is not empty and is valid
- Redis connection string format
- JWT signing key present and minimum length
- Quartz cron expressions valid
- Module-specific settings complete

**Implementation:** Use `OptionsBuilder<T>.ValidateOnStart()` pattern so the app fails fast with a clear error message.

---

### TFR-012: Enhance OpenAPI Documentation

**Problem:** Swagger is minimal -- single version, no security definitions, no request/response examples, no error schema documentation. Endpoints show up but aren't documented enough for a consumer to use confidently.

**Proposal:**

1. **Add security scheme** -- Document Bearer token authentication in Swagger UI
2. **Add operation summaries** -- Brief descriptions on each endpoint
3. **Add response examples** -- Sample request/response bodies
4. **Add error responses** -- Document 400, 401, 403, 404, 409 responses per endpoint
5. **Group by module** -- Use tags matching module names
6. **Add Scalar or ReDoc** -- Modern alternative to Swagger UI (better DX)

**Implementation:** Use `WithOpenApi()` extension on endpoints or XML doc comments with `Swashbuckle.AspNetCore.Annotations`.

---

## Summary

| ID | Feature | Status | Impact | Effort | Dependencies |
|----|---------|--------|--------|--------|-------------|
| TFR-000 | Migrate MassTransit InMemory to PostgreSQL | Completed | High | Medium | None |
| TFR-001 | Replace Keycloak with ASP.NET Identity | Open | High | Medium | None |
| TFR-002 | Stabilize MassTransit version | Partial | High | Low | None (MassTransit done in TFR-000) |
| TFR-003 | Add resilience with Polly | Open | High | Medium | None |
| TFR-004 | Expand integration test coverage | Open | High | High | TFR-001 simplifies tests |
| TFR-005 | Add rate limiting | Open | Medium | Low | None |
| TFR-006 | Add API versioning | Open | Medium | Low | None |
| TFR-007 | Improve health checks | Open | Medium | Low | TFR-001 (remove Keycloak check) |
| TFR-008 | Response compression & CORS | Open | Medium | Low | None |
| TFR-009 | Persist Quartz state | Open | Medium | Medium | None |
| TFR-010 | Multi-level caching | Open | Medium | Medium | None |
| TFR-011 | Config validation | Open | Low | Low | None |
| TFR-012 | Enhanced OpenAPI docs | Open | Low | Medium | TFR-006 (versioning) |
