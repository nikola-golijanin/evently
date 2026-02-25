# Evently Simulator - Technical Documentation

## Table of Contents

- [Overview](#overview)
- [1. Why a Simulator?](#1-why-a-simulator)
- [2. Project Structure](#2-project-structure)
- [3. Dev-Only Endpoint: Promote User to Admin](#3-dev-only-endpoint-promote-user-to-admin)
  - [3.1 The Problem](#31-the-problem)
  - [3.2 The Solution](#32-the-solution)
  - [3.3 Users Module Changes](#33-users-module-changes)
- [4. Aspire Integration](#4-aspire-integration)
- [5. Configuration](#5-configuration)
- [6. Runtime State](#6-runtime-state)
- [7. Bootstrap Sequence](#7-bootstrap-sequence)
- [8. Workers](#8-workers)
  - [8.1 AdminWorker](#81-adminworker)
  - [8.2 ShopperWorker](#82-shopperworker)
  - [8.3 AttendeeWorker](#83-attendeeworker)
- [9. Token Management](#9-token-management)
- [10. State Persistence](#10-state-persistence)
- [11. API Contracts Used](#11-api-contracts-used)
- [12. How to Run](#12-how-to-run)
- [13. Design Decisions](#13-design-decisions)

---

## Overview

`Evently.Simulator` is a **.NET Worker Service** that runs alongside the API and continuously generates realistic usage data — categories, events, ticket purchases, and check-ins — by calling the live API as virtual users. It uses Keycloak tokens for authentication and persists registered user credentials across restarts.

**Location:** `src/Simulator/Evently.Simulator/`
**SDK:** `Microsoft.NET.Sdk.Worker`
**Target framework:** `net10.0`

---

## 1. Why a Simulator?

Evently has a strict dependency chain that must be respected for any operation to succeed:

```
categories → event → ticket types → publish → cart → order → tickets → check-in
```

Purely random API calls are unreliable — you cannot buy a ticket for an event that doesn't exist, and you cannot check in with a ticket that hasn't been issued yet. The simulator is scenario-aware and respects this ordering through three coordinated workers.

Beyond exercising the full API surface, the simulator serves several practical purposes:

| Goal | What the simulator does |
|------|------------------------|
| Populate the DB with realistic data | Creates events with varied titles, locations, dates, and ticket types |
| Exercise cross-module integration events | Order → tickets → check-in flows trigger outbox/inbox processing |
| Validate end-to-end auth | Each virtual user holds a real Keycloak JWT; permission failures surface immediately |
| Persist across restarts | Admin credentials are config-driven; regular users are saved to disk |
| Provide load for observability | Structured Serilog logs, OpenTelemetry traces, and DB writes from realistic traffic |

---

## 2. Project Structure

```
src/Simulator/Evently.Simulator/
├── Evently.Simulator.csproj         # Worker SDK; Bogus + Serilog.AspNetCore
├── Program.cs                       # Host setup: DI, Serilog, bootstrapper, workers
├── appsettings.json                 # URL config, intervals, 5 admin credentials
├── SimulatorOptions.cs              # Strongly-typed config bound from "Simulator" section
├── .editorconfig                    # Suppresses CA5394/CA1859 for non-security Random use
│
├── Auth/
│   ├── VirtualUser.cs               # Email, password, cached access token + expiry
│   └── TokenService.cs              # Keycloak password-grant acquire/refresh
│
├── State/
│   ├── SimulatorState.cs            # In-memory pools: admins, users, categories, events, orders
│   └── SimulatorStateStore.cs       # Load/save regular-user credentials → simulator-state.json
│
├── Clients/
│   ├── EventsClient.cs              # GET /categories, POST /categories, POST /events, etc.
│   ├── UsersClient.cs               # POST /users/register, PUT /dev/users/{id}/promote-admin
│   ├── TicketingClient.cs           # PUT /carts/add, POST /orders, GET /orders, GET /tickets/order/{id}
│   └── AttendanceClient.cs          # PUT /attendees/check-in
│
├── Bootstrap/
│   └── SimulatorBootstrapper.cs     # Runs once at startup: admins, categories, events, users
│
└── Workers/
    ├── AdminWorker.cs               # IHostedService: creates + publishes events every 60s
    ├── ShopperWorker.cs             # IHostedService: registers users, buys tickets every 30s
    └── AttendeeWorker.cs            # IHostedService: checks in from pending queue every 45s
```

---

## 3. Dev-Only Endpoint: Promote User to Admin

### 3.1 The Problem

`POST /users/register` always assigns `Role.Member` (hardcoded in `User.Create()`). To make a user an admin, their role must be changed after registration. Pre-seeding admins via EF Core `HasData()` would skip `UserRegisteredDomainEvent`, meaning Ticketing and Attendance modules would never create their `Customer`/`Attendee` records for those users — breaking cross-module consistency.

### 3.2 The Solution

Register via the normal flow, then promote via a dev-only endpoint:

```
PUT /dev/users/{id}/promote-admin
```

- No authorization required (dev tool — never registered in production)
- Returns `204 No Content`
- Registered only when `IHostEnvironment.IsDevelopment()` is true (checked inside `MapEndpoint`)
- Idempotent: if the user is already an admin, returns success without changes

The full bootstrap sequence for a new admin user:
1. `POST /users/register` → `UserRegisteredDomainEvent` fires → Ticketing creates `Customer`, Attendance creates `Attendee`
2. Wait ~2 seconds for integration events to propagate
3. `PUT /dev/users/{userId}/promote-admin` → role switched to Administrator
4. Acquire Keycloak token → admin is ready

### 3.3 Users Module Changes

**`User.cs`** — new domain method:

```csharp
public void PromoteToAdmin()
{
    if (_roles.Any(r => r.Name == Role.Administrator.Name))
    {
        return; // idempotent
    }

    _roles.Clear();
    _roles.Add(Role.Administrator);
}
```

**`IUserRepository`** — new interface method:
```csharp
void Update(User user);
```

**`UserRepository`** — implementation attaches detached `Role` instances so EF Core doesn't try to re-insert seeded roles:
```csharp
public void Update(User user)
{
    foreach (Role role in user.Roles)
    {
        if (context.Entry(role).State == EntityState.Detached)
        {
            context.Attach(role);
        }
    }
}
```

This mirrors the existing `Insert()` pattern. Without attaching, EF Core would attempt to `INSERT` a new `Role.Administrator` row, violating the primary key constraint.

**`PromoteUserToAdmin.cs`** (Presentation):

```csharp
internal sealed class PromoteUserToAdmin : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        IHostEnvironment env = app.ServiceProvider.GetRequiredService<IHostEnvironment>();
        if (!env.IsDevelopment()) return;

        app.MapPut("dev/users/{id:guid}/promote-admin", ...)
           .AllowAnonymous()
           .WithTags(Tags.Users);
    }
}
```

The environment check happens inside `MapEndpoint`, which is called at startup. In non-Development environments, the route is simply never registered — it doesn't exist at runtime.

---

## 4. Aspire Integration

The simulator is registered in `src/Evently.AppHost/AppHost.cs`:

```csharp
IResourceBuilder<ProjectResource> api = builder.AddProject<Projects.Evently_Api>("evently-api")
    // ... (existing config)

builder.AddProject<Projects.Evently_Simulator>("evently-simulator")
    .WaitFor(api)
    .WithEnvironment("Simulator__TargetBaseUrl",
        ReferenceExpression.Create($"{api.GetEndpoint("http")}"))
    .WithEnvironment("Simulator__KeycloakTokenUrl",
        ReferenceExpression.Create($"{keycloakEndpoint}/realms/evently/protocol/openid-connect/token"));
```

Key points:

- **`.WaitFor(api)`** — Aspire holds the simulator until the API's `/health` endpoint returns healthy. This ensures the DB is migrated and all modules are ready before the simulator issues any API calls.
- **`WithEnvironment("Simulator__TargetBaseUrl", ...)`** — injects the API's dynamic Aspire port so the simulator hits the right URL regardless of host port assignment.
- **`WithEnvironment("Simulator__KeycloakTokenUrl", ...)`** — overrides the default `localhost:18080` URL with the Aspire-managed Keycloak container URL.

The `AppHost.csproj` has a `<ProjectReference>` to `Evently.Simulator.csproj`, which causes the Aspire SDK to generate the `Projects.Evently_Simulator` type at build time.

---

## 5. Configuration

All settings live under the `"Simulator"` key in `appsettings.json` and are bound to `SimulatorOptions`.

```json
{
  "Simulator": {
    "TargetBaseUrl": "http://localhost:5000",
    "KeycloakTokenUrl": "http://localhost:18080/realms/evently/protocol/openid-connect/token",
    "PublicClientId": "evently-public-client",
    "AdminWorkerIntervalSeconds": 60,
    "ShopperWorkerIntervalSeconds": 30,
    "AttendeeWorkerIntervalSeconds": 45,
    "NewUserRegistrationChance": 0.25,
    "AdminIntegrationEventPropagationDelayMs": 2000,
    "AdminUsers": [
      { "email": "admin1@evently-sim.com", "password": "SimAdmin1!", "firstName": "Sim", "lastName": "Admin1" },
      ...
    ]
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `TargetBaseUrl` | `http://localhost:5000` | API base URL (overridden by Aspire env var) |
| `KeycloakTokenUrl` | `http://localhost:18080/.../token` | Keycloak token endpoint (overridden by Aspire) |
| `PublicClientId` | `evently-public-client` | Keycloak client ID with Direct Access Grants enabled |
| `AdminWorkerIntervalSeconds` | `60` | How often AdminWorker creates + publishes an event |
| `ShopperWorkerIntervalSeconds` | `30` | How often ShopperWorker attempts a purchase |
| `AttendeeWorkerIntervalSeconds` | `45` | How often AttendeeWorker processes pending check-ins |
| `NewUserRegistrationChance` | `0.25` | Probability that a ShopperWorker tick registers a new user |
| `AdminIntegrationEventPropagationDelayMs` | `2000` | Wait after register before promoting (let outbox process) |
| `AdminUsers` | 5 entries | Admin credentials; re-used across restarts via Keycloak login |

When running under Aspire, `Simulator__TargetBaseUrl` and `Simulator__KeycloakTokenUrl` environment variables override the `appsettings.json` values automatically (standard `__`-separator config override).

---

## 6. Runtime State

`SimulatorState` is a singleton that holds all in-memory pools:

| Property | Type | Contents |
|----------|------|----------|
| `AdminUsers` | `List<VirtualUser>` | Up to 5 admin virtual users, populated at startup |
| `RegularUsers` | `ConcurrentBag<VirtualUser>` | Grows as ShopperWorker registers new users |
| `CategoryIds` | `ConcurrentBag<Guid>` | Seeded at startup from `GET /categories` |
| `PublishedEventIds` | `ConcurrentBag<Guid>` | Grows as AdminWorker publishes events; pre-loaded at startup from `GET /events` |
| `PendingOrders` | `ConcurrentQueue<PendingOrder>` | `(VirtualUser, OrderId)` pairs awaiting check-in |

`PendingOrder` stores the `VirtualUser` reference (not just a user ID) so the `AttendeeWorker` can directly re-acquire the user's token without searching the pools.

---

## 7. Bootstrap Sequence

`SimulatorBootstrapper.RunAsync()` is called in `Program.cs` **before** `host.RunAsync()`, ensuring the state pools are populated before any worker fires:

```
Program.cs
  │
  ├── host = builder.Build()
  │
  ├── await bootstrapper.RunAsync()        ← blocks until complete
  │     │
  │     ├── 1. SetupAdminUsersAsync()
  │     │     For each admin in config:
  │     │       ├── Try Keycloak login
  │     │       │     ✓ Already exists → add to AdminUsers pool
  │     │       │     ✗ 401 → Register → wait 2s → Promote → Login → add to pool
  │     │       └── Log result
  │     │
  │     ├── 2. LoadCategoriesAsync()
  │     │     ├── GET /categories (with admin token)
  │     │     ├── Populate CategoryIds pool
  │     │     └── If empty → SeedCategoriesAsync() → create 5 categories via POST /categories
  │     │
  │     ├── 3. LoadPublishedEventsAsync()
  │     │     └── GET /events → populate PublishedEventIds pool
  │     │
  │     └── 4. LoadRegularUsersAsync()
  │           ├── Read simulator-state.json
  │           ├── Try Keycloak login for each saved user
  │           └── Add successes to RegularUsers pool
  │
  └── await host.RunAsync()                ← workers start here
```

On the **first run** (empty DB): admins are registered and promoted, 5 categories are created, and no events or regular users exist yet.

On **subsequent runs**: admin logins succeed immediately (no re-registration), regular users are reloaded from `simulator-state.json`, and existing events are pre-loaded into `PublishedEventIds`.

---

## 8. Workers

All three workers extend `BackgroundService` and use `PeriodicTimer` for their intervals.

### 8.1 AdminWorker

**Interval:** `AdminWorkerIntervalSeconds` (default: 60s)

Each tick:

1. Pick a random admin from `AdminUsers`, acquire/refresh their token
2. Pick a random `CategoryId` from the pool
3. Generate fake event data with **Bogus**: title (product adjective + name), description (paragraph), location (city + country), start date 7–60 days from now, end date +1 day
4. `POST /events` → get `eventId`
5. Create 1–3 ticket types (General Admission / VIP / Early Bird / Student, random price €10–€250, random quantity 50–500)
6. `PUT /events/{eventId}/publish`
7. Add `eventId` to `PublishedEventIds`

**Log:** `[AdminWorker] Published event "Incredible Electronics Summit" (3fa85f64-...)`

### 8.2 ShopperWorker

**Interval:** `ShopperWorkerIntervalSeconds` (default: 30s)

Each tick:

1. **25% chance** — register a new user (Bogus name/email, fixed-pattern password `User{random}!`), acquire token, add to `RegularUsers`, save to `simulator-state.json`
2. Pick a random user from `RegularUsers` (skip tick if pool is empty)
3. Pick a random `eventId` from `PublishedEventIds` (skip tick if empty)
4. `GET /ticket-types?eventId={id}` → pick a random ticket type
5. `PUT /carts/add` with `quantity: 1`
6. `POST /orders` (returns no body)
7. `GET /orders` → take the most recently created order (by `CreatedAtUtc`)
8. Enqueue `PendingOrder(user, orderId)` into `PendingOrders`

**Log:** `[ShopperWorker] User user42@example.com placed order 7c9e6679-... for event 3fa85f64-...`

> **Note on order ID retrieval:** `POST /orders` returns an empty 200 body. The worker calls `GET /orders` immediately after and selects the newest entry. This is slightly optimistic but acceptable for a dev tool; in the rare race condition where another order arrives in the same millisecond, the wrong order ID is tracked, resulting in a harmless missed check-in.

### 8.3 AttendeeWorker

**Interval:** `AttendeeWorkerIntervalSeconds` (default: 45s)

Each tick:

1. `TryDequeue` from `PendingOrders` (skip tick if queue is empty)
2. Acquire/refresh token for the dequeued user
3. `GET /tickets/order/{orderId}` — wait for tickets to be issued (they arrive asynchronously via outbox/inbox; by the time AttendeeWorker processes the order, typically 45+ seconds have elapsed)
4. For each ticket: `PUT /attendees/check-in` with `{ ticketId: ticket.Id }`

**Log:** `[AttendeeWorker] Checked in ticket 1b9d6bcd-... (code: EVT-00042)`

> **Note on check-in field:** The `PUT /attendees/check-in` endpoint takes a `TicketId` (Guid from the Ticketing module), not a human-readable code string. The `Code` field in the ticket response is included in the log for observability only.

---

## 9. Token Management

`TokenService` handles Keycloak token lifecycle for all virtual users:

```
GetTokenAsync(user)
  ├── Token exists AND expires in > 5 minutes → return cached token
  └── Otherwise → AcquireTokenAsync(user)
        ├── POST {KeycloakTokenUrl}
        │     grant_type=password
        │     client_id={PublicClientId}
        │     username={user.Email}
        │     password={user.Password}
        │     scope=openid
        ├── Success → cache AccessToken + ExpiresAt, return token
        └── Failure → log warning, return null
```

Each `VirtualUser` caches its own token on the object itself (`user.AccessToken`, `user.ExpiresAt`). Token re-acquisition happens on the next call if the cached token is within 5 minutes of expiry (Keycloak default lifetime: 30 minutes).

**Thread safety:** Multiple workers can request tokens for the same user concurrently. In the worst case, two workers both see an expired token and both re-acquire. Keycloak accepts this gracefully (returns a fresh token both times). No locking is applied — the last write wins and both tokens are valid.

**Prerequisite:** The Keycloak `evently-public-client` must have **Direct Access Grants** enabled. Check the realm import file at `.files/` for `"directAccessGrantsEnabled": true` on that client.

---

## 10. State Persistence

`SimulatorStateStore` serializes/deserializes `simulator-state.json` in the working directory:

```json
{
  "regularUsers": [
    { "email": "user42@example.com", "password": "User4782!" },
    { "email": "alice.smith@example.com", "password": "User1234!" }
  ]
}
```

- **Written** by `ShopperWorker` after every new user registration (full rewrite)
- **Read** by `SimulatorBootstrapper` at startup; each saved user gets a Keycloak login attempt — failures are silently skipped (e.g., if the DB was reset)
- Admin credentials are **not** stored here — they live in `appsettings.json` and are always loaded fresh from Keycloak

Categories and published events are **not persisted** — they are re-read from the live API at every startup via `GET /categories` and `GET /events`.

---

## 11. API Contracts Used

| Method | URL | Auth | Description |
|--------|-----|------|-------------|
| `POST` | `/users/register` | None | Register new user; returns `Guid` (userId) |
| `PUT` | `/dev/users/{id}/promote-admin` | None (dev-only) | Promote registered user to Administrator |
| `GET` | `/categories` | Bearer | List all categories |
| `POST` | `/categories` | Bearer (admin) | Create a category |
| `GET` | `/events` | Bearer | List all events |
| `POST` | `/events` | Bearer (admin) | Create draft event |
| `POST` | `/ticket-types` | Bearer (admin) | Create ticket type for event |
| `PUT` | `/events/{id}/publish` | Bearer (admin) | Publish event |
| `GET` | `/ticket-types?eventId={id}` | Bearer | List ticket types for event |
| `PUT` | `/carts/add` | Bearer | Add ticket type to cart |
| `POST` | `/orders` | Bearer | Create order from cart (returns empty body) |
| `GET` | `/orders` | Bearer | List caller's orders |
| `GET` | `/tickets/order/{orderId}` | Bearer | List tickets for an order |
| `PUT` | `/attendees/check-in` | Bearer | Check in a ticket (`{ ticketId: Guid }`) |

---

## 12. How to Run

**Via Aspire (recommended):**

```bash
dotnet run --project src/Evently.AppHost
```

The Aspire dashboard at `http://localhost:18888` shows:
- `evently-simulator` resource status (waiting → running)
- Live console output from the simulator's Serilog logger
- Structured logs from all workers

**Standalone (without Aspire):**

```bash
# Start infra first
docker-compose up -d

# Run the API
dotnet run --project src/API/Evently.Api

# Run the simulator (in a separate terminal)
dotnet run --project src/Simulator/Evently.Simulator
```

The standalone mode uses the default `appsettings.json` URLs (`localhost:5000` for the API, `localhost:18080` for Keycloak).

**Expected first-run log output:**

```
[INF] Starting simulator bootstrap...
[INF] Setting up admin admin1@evently-sim.com...
[INF] Registered admin admin1@evently-sim.com (3fa85f64-...), waiting for integration events...
[INF] Admin admin1@evently-sim.com set up successfully
... (×5 admins)
[INF] No categories found. Seeding initial categories...
[INF] Created category 'Electronics' (7c9e6679-...)
... (×5 categories)
[INF] Loaded 0 events from API
[INF] Loaded 0 regular users from state file
[INF] Bootstrap complete. Admins: 5, Categories: 5, Events: 0, Users: 0
[INF] [AdminWorker] Published event "Incredible Electronics Summit" (1b9d6bcd-...)
[INF] [ShopperWorker] Registered new user alice.smith@example.com
[INF] [ShopperWorker] User alice.smith@example.com placed order 7c9e6679-... for event 1b9d6bcd-...
[INF] [AttendeeWorker] Checked in ticket 4ae71c81-... (code: EVT-00001)
```

**Expected subsequent-run log output (warm start):**

```
[INF] Starting simulator bootstrap...
[INF] Setting up admin admin1@evently-sim.com...
[INF] Admin admin1@evently-sim.com already exists, logged in successfully
... (×5 admins, no registrations)
[INF] Loaded 12 events from API
[INF] Loaded 47 regular users from state file
[INF] Bootstrap complete. Admins: 5, Categories: 5, Events: 12, Users: 47
```

---

## 13. Design Decisions

**Why a Worker Service and not a test project?**
The simulator is long-running, stateful, and designed to run continuously alongside the API. A test project runs once and exits. The Worker Service model with `PeriodicTimer` and `IHostedService` fits the "always-on background process" requirement exactly.

**Why three separate workers instead of one?**
Each worker operates at a different cadence with different dependencies. AdminWorker produces events; ShopperWorker consumes them. AttendeeWorker consumes orders that ShopperWorker produces. Separating them means each can be independently tuned and each failure is isolated — a bad AdminWorker tick doesn't block purchases from happening.

**Why not use typed `HttpClient` (registered via `AddHttpClient<T>`)?**
Typed clients registered as transient, when injected into singletons (workers are singletons as `IHostedService`), become effectively singleton for the application lifetime — this is a captive dependency. Instead, all clients (`EventsClient`, etc.) are registered as explicit singletons and use `IHttpClientFactory.CreateClient()` per method call. The `IHttpClientFactory` manages connection pooling and handler lifetime correctly regardless of how often `CreateClient()` is called.

**Why persist only regular users, not admins or events?**
- **Admins** are defined in config with known credentials; they are always re-verified against Keycloak at startup. Config is the source of truth.
- **Events** are re-read from `GET /events` at startup and accumulate across restarts without any simulator-side tracking needed.
- **Regular users** are the only entities that are _dynamically generated_ at runtime and would otherwise be lost on restart, causing orphaned Keycloak accounts.

**Why the 2-second propagation delay after admin registration?**
`POST /users/register` triggers `UserRegisteredDomainEvent` → outbox → `UserRegisteredIntegrationEvent` → Ticketing creates `Customer`, Attendance creates `Attendee`. The `PUT /dev/users/{id}/promote-admin` endpoint only updates the Users module DB. If called before the integration events propagate, the Ticketing `Customer` and Attendance `Attendee` records don't exist yet — but that doesn't affect the promote operation itself. The delay is a pragmatic safeguard to ensure the user is fully set up across all modules before the simulator starts using it.
