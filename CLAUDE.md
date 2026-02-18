# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Documentation

Detailed domain and technical documentation lives in `docs/`. Consult these when you need context about a specific module, business flow, or cross-module interaction.

| Document | Path | What to look up here |
|----------|------|---------------------|
| **Domain Overview** | `docs/01-Domain.md` | Business domain, bounded contexts, entity relationships, state machines, data ownership, key business flows, glossary |
| **Events Module** | `docs/02-Events-Module.md` | Event/Category/TicketType entities, commands, queries, 16 endpoints, domain events, CancelEventSaga state machine |
| **Users Module** | `docs/03-Users-Module.md` | User/Role/Permission model, Keycloak integration, 17 permissions, authorization flow, claims transformation |
| **Ticketing Module** | `docs/04-Ticketing-Module.md` | Order/Payment/Ticket/Cart entities, 14 commands, 10 endpoints, pessimistic locking, Redis cart, payment service |
| **Attendance Module** | `docs/05-Attendance-Module.md` | Attendee/Ticket/EventStatistics entities, check-in logic, CQRS projection pattern, 2 endpoints |
| **Cross-Module Communication** | `docs/06-Cross-Module-Communication.md` | Outbox/inbox pattern, MassTransit config, idempotency, 14 integration events, CancelEventSaga, reliability guarantees |
| **Feature Requests** | `docs/07-Feature-Requests.md` | 14 features (FR-001 to FR-014): notifications, waitlist, refunds, promo codes, organizer ownership, order status on cancel, cart reservation, transfers, reviews, recurring events, venues, seating, analytics, group bookings |
| **Technical Feature Requests** | `docs/08-Technical-Feature-Requests.md` | 12 technical improvements (TFR-001 to TFR-012): Keycloak->Identity migration, MassTransit stabilization, Polly resilience, test coverage, rate limiting, API versioning, health checks, compression, Quartz persistence, multi-level caching, config validation, OpenAPI |

## Build & Test Commands

```bash
# Build
dotnet build Evently.slnx

# Run all tests
dotnet test Evently.slnx

# Run specific test project
dotnet test test/Evently.ArchitectureTests
dotnet test test/Evently.IntegrationTests
dotnet test src/Modules/Events/Evently.Modules.Events.UnitTests

# Run single test
dotnet test --filter "FullyQualifiedName~TestMethodName"

# Run the API
dotnet run --project src/API/Evently.Api

# Start infrastructure (PostgreSQL, Redis, Keycloak, Seq, Jaeger)
docker-compose up -d

# Start infrastructure via Aspire (alternative to docker-compose)
dotnet run --project src/Evently.AppHost
```

## Architecture

**Pattern:** Modular Monolith with Clean Architecture + DDD + CQRS

### Module Structure
Each module (Users, Events, Ticketing, Attendance) follows this layered pattern:
- **Domain** - Entities, aggregates, domain events, repository interfaces
- **Application** - Commands, queries, handlers (MediatR), validators (FluentValidation)
- **Infrastructure** - EF Core DbContext, repository implementations, outbox/inbox
- **Presentation** - Minimal API endpoints implementing `IEndpoint`
- **IntegrationEvents** - Cross-module event contracts

### Key Patterns

**Result Pattern:** All business logic returns `Result<T>` instead of throwing exceptions. Use `Result.Success()` and `Result.Failure(error)`.

**CQRS with MediatR:**
- Commands: `ICommand<TResponse>` for writes
- Queries: `IQuery<TResponse>` for reads
- Pipeline behaviors handle validation, logging, exception handling

**Domain Events & Outbox:** Domain events are raised via `entity.Raise(domainEvent)` and captured by `InsertOutboxMessagesInterceptor` during `SaveChanges()`. Background jobs process the outbox for reliable delivery.

**Module Isolation:** Modules communicate only via integration events. Architecture tests enforce that modules cannot reference other modules' Domain/Application/Infrastructure layers—only their IntegrationEvents.

**Idempotent Handlers:** Use `IdempotentDomainEventHandler<T>` and `IdempotentIntegrationEventHandler<T>` base classes for at-least-once delivery guarantees.

### Shared Libraries (src/Common)
- `Evently.Common.Domain` - Entity base, Result, Error, DomainEvent
- `Evently.Common.Application` - ICommand, IQuery, pipeline behaviors
- `Evently.Common.Infrastructure` - Auth, caching, outbox/inbox, telemetry
- `Evently.Common.Presentation` - IEndpoint, result mapping

## Code Style

- File-scoped namespaces required
- Use explicit types; `var` only when type is apparent
- Braces required on all control statements
- Use `string` not `String`, `int` not `Int32`
- No `this.` qualification
- Database naming: snake_case (via EF Core convention)

## Infrastructure Services

| Service | Port | Purpose |
|---------|------|---------|
| API | 5000 | HTTP |
| PostgreSQL | 5432 | Database |
| Redis | 6379 | Cache |
| Keycloak | 18080 | Identity/OAuth |
| Seq | 8081 | Log viewer |
| Jaeger | 16686 | Distributed tracing |
| Aspire Dashboard | 18888 | Local dev orchestration (replaces Seq/Jaeger for local dev) |

## Tech Stack

- .NET 10, ASP.NET Core Minimal APIs
- Entity Framework Core 9 with Npgsql (PostgreSQL)
- MediatR 14, FluentValidation 12
- MassTransit 9 (PostgreSQL transport)
- .NET Aspire (local dev orchestration)
- Serilog, OpenTelemetry
- xUnit, FluentAssertions, Testcontainers, NetArchTest

## Last Reviewed Commit

`f4fd816` - Add Aspire AppHost for infra orchestration and config
