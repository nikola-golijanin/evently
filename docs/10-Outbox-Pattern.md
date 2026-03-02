# Domain Events & Outbox Pattern - Technical Documentation

## Table of Contents

- [1. Overview](#1-overview)
- [2. Domain Events — The Source](#2-domain-events--the-source)
- [3. EF Core Interceptor — Atomic Capture](#3-ef-core-interceptor--atomic-capture)
- [4. Database Schema](#4-database-schema)
- [5. BackgroundService — The Processing Loop](#5-backgroundservice--the-processing-loop)
- [6. Handler Discovery](#6-handler-discovery)
- [7. Idempotency Decorator](#7-idempotency-decorator)
- [8. Serialization](#8-serialization)
- [9. Domain → Integration Bridge](#9-domain--integration-bridge)
- [10. The Inbox — Mirror Pattern](#10-the-inbox--mirror-pattern)
- [11. Key Design Decisions](#11-key-design-decisions)
- [12. End-to-End Flow](#12-end-to-end-flow)

---

## 1. Overview

The Outbox Pattern solves the **dual-write problem**: how to atomically update the database *and* publish a message, without distributed transactions. Evently's implementation ensures that:

- Domain events are **never lost**: they are stored in the same DB transaction as the state change.
- Handlers are **retried** until they succeed (at-least-once delivery).
- Handlers are **idempotent**: running twice has the same effect as running once.

The full pipeline, from an entity method call to a cross-module integration event, passes through five stages:

```
Entity.Method()
    → Raise(domainEvent)           [in memory]
    → SaveChanges()                [EF Core interceptor]
    → outbox_messages table        [same transaction]
    → ProcessOutboxJob (BackgroundService) [background poll]
    → IDomainEventHandler          [decorated with idempotency]
    → IEventBus.PublishAsync()     [MassTransit → inbox of target module]
```

---

## 2. Domain Events — The Source

**Files:**
- `src/Common/Evently.Common.Domain/IDomainEvent.cs`
- `src/Common/Evently.Common.Domain/DomainEvent.cs`
- `src/Common/Evently.Common.Domain/Entity.cs`

### IDomainEvent interface

```csharp
public interface IDomainEvent
{
    Guid Id { get; }
    DateTime OccurredOnUtc { get; }
}
```

### DomainEvent abstract base

```csharp
public abstract class DomainEvent : IDomainEvent
{
    protected DomainEvent()
    {
        Id = Guid.NewGuid();
        OccurredOnUtc = DateTime.UtcNow;
    }

    protected DomainEvent(Guid id, DateTime occurredOnUtc)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
    }

    public Guid Id { get; init; }
    public DateTime OccurredOnUtc { get; init; }
}
```

Each domain event is a plain record that carries the minimal data the event needs (typically just the entity's ID). Enrichment happens later in the handler.

### Entity base class

```csharp
public abstract class Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.ToList();

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}
```

Domain events accumulate in memory as entity methods execute:

```csharp
// Inside Event.Publish():
Raise(new EventPublishedDomainEvent(Id));

// Inside Event.Cancel():
Raise(new EventCanceledDomainEvent(Id, canceledOnUtc));
```

Events remain in the list until `SaveChanges()` is called, at which point the interceptor captures them.

---

## 3. EF Core Interceptor — Atomic Capture

**File:** `src/Common/Evently.Common.Infrastructure/Outbox/InsertOutboxMessagesInterceptor.cs`

```csharp
public sealed class InsertOutboxMessagesInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            InsertOutboxMessages(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void InsertOutboxMessages(DbContext context)
    {
        var outboxMessages = context
            .ChangeTracker
            .Entries<Entity>()
            .Select(entry => entry.Entity)
            .SelectMany(entity =>
            {
                IReadOnlyCollection<IDomainEvent> domainEvents = entity.DomainEvents;
                entity.ClearDomainEvents();
                return domainEvents;
            })
            .Select(domainEvent => new OutboxMessage
            {
                Id = domainEvent.Id,
                Type = domainEvent.GetType().Name,
                Content = JsonConvert.SerializeObject(domainEvent, SerializerSettings.Instance),
                OccurredOnUtc = domainEvent.OccurredOnUtc
            })
            .ToList();

        context.Set<OutboxMessage>().AddRange(outboxMessages);
    }
}
```

**What happens:**
1. EF Core calls `SavingChangesAsync` before writing to the DB.
2. The interceptor iterates all tracked `Entity` instances via `ChangeTracker`.
3. For each entity, it drains `DomainEvents` (calling `ClearDomainEvents()` to prevent double-processing).
4. Each domain event becomes an `OutboxMessage` record added to the same `DbContext`.
5. `base.SavingChangesAsync` proceeds — **all inserts happen in a single transaction**.

The interceptor is registered per-module in each module's `DbContext`. Events from one module only appear in that module's `outbox_messages` table.

---

## 4. Database Schema

Each module has its own set of four tables in its schema (e.g., `events.*`, `ticketing.*`).

### outbox_messages

```sql
CREATE TABLE {schema}.outbox_messages (
    id               UUID        PRIMARY KEY,
    type             TEXT        NOT NULL,   -- Short type name (e.g. "EventPublishedDomainEvent")
    content          JSONB       NOT NULL,   -- Newtonsoft JSON with $type metadata
    occurred_on_utc  TIMESTAMP   NOT NULL,   -- When the domain event was raised
    processed_on_utc TIMESTAMP   NULL,       -- Set on success; NULL = pending
    error            TEXT        NULL        -- Full exception.ToString() on failure
);
```

### outbox_message_consumers

Tracks which handlers have already processed each message (idempotency):

```sql
CREATE TABLE {schema}.outbox_message_consumers (
    outbox_message_id  UUID  NOT NULL  REFERENCES outbox_messages(id),
    name               TEXT  NOT NULL, -- Handler class name (e.g. "EventPublishedDomainEventHandler")
    PRIMARY KEY (outbox_message_id, name)
);
```

### inbox_messages

```sql
CREATE TABLE {schema}.inbox_messages (
    id               UUID        PRIMARY KEY,
    type             TEXT        NOT NULL,
    content          JSONB       NOT NULL,
    occurred_on_utc  TIMESTAMP   NOT NULL,
    processed_on_utc TIMESTAMP   NULL,
    error            TEXT        NULL
);
```

### inbox_message_consumers

```sql
CREATE TABLE {schema}.inbox_message_consumers (
    inbox_message_id  UUID  NOT NULL  REFERENCES inbox_messages(id),
    name              TEXT  NOT NULL,
    PRIMARY KEY (inbox_message_id, name)
);
```

---

## 5. BackgroundService — The Processing Loop

**File (example):** `src/Modules/Events/Evently.Modules.Events.Infrastructure/Outbox/ProcessOutboxJob.cs`

Each module has its own `ProcessOutboxJob` that extends `BackgroundService`. A `while (!stoppingToken.IsCancellationRequested)` loop with `Task.Delay()` provides the polling interval. Single-loop execution is inherently non-concurrent, so no concurrency guard is needed.

### Configuration

```csharp
// OutboxOptions (bound from appsettings)
internal sealed class OutboxOptions
{
    public int IntervalInSeconds { get; init; }
    public int BatchSize { get; init; }
}
```

### Processing logic

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        await ProcessOutboxMessagesAsync(stoppingToken);
        await Task.Delay(TimeSpan.FromSeconds(outboxOptions.Value.IntervalInSeconds), stoppingToken);
    }
}

private async Task ProcessOutboxMessagesAsync(CancellationToken cancellationToken)
{
    await using DbConnection connection = await dbConnectionFactory.OpenConnectionAsync();
    await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

    IReadOnlyList<OutboxMessageResponse> outboxMessages = await GetOutboxMessagesAsync(connection, transaction);

    foreach (OutboxMessageResponse outboxMessage in outboxMessages)
    {
        Exception? exception = null;
        try
        {
            IDomainEvent domainEvent = JsonConvert.DeserializeObject<IDomainEvent>(
                outboxMessage.Content,
                SerializerSettings.Instance)!;

            using IServiceScope scope = serviceScopeFactory.CreateScope();

            IEnumerable<IDomainEventHandler> handlers = DomainEventHandlersFactory.GetHandlers(
                domainEvent.GetType(),
                scope.ServiceProvider,
                Application.AssemblyReference.Assembly);

            foreach (IDomainEventHandler handler in handlers)
            {
                await handler.Handle(domainEvent, cancellationToken);
            }
        }
        catch (Exception caughtException)
        {
            exception = caughtException;
        }

        await UpdateOutboxMessageAsync(connection, transaction, outboxMessage, exception);
    }

    await transaction.CommitAsync(cancellationToken);
}
```

### SQL — selecting pending messages

```sql
SELECT id, content
FROM events.outbox_messages
WHERE processed_on_utc IS NULL
ORDER BY occurred_on_utc
LIMIT {BatchSize}
FOR UPDATE
```

`FOR UPDATE` is a PostgreSQL pessimistic lock. It prevents two concurrent job instances (e.g., from different pods) from picking up the same rows simultaneously.

### SQL — updating after processing

```sql
UPDATE events.outbox_messages
SET processed_on_utc = @ProcessedOnUtc,
    error = @Error
WHERE id = @Id
```

On success: `processed_on_utc` is set, `error` is NULL.
On failure: `processed_on_utc` is set, `error` contains the full `exception.ToString()`.

**Note:** Failed messages are marked processed (with the error). They are **not retried automatically**. To reprocess, reset `processed_on_utc` to NULL and clear `error`.

---

## 6. Handler Discovery

**File:** `src/Common/Evently.Common.Infrastructure/Outbox/DomainEventHandlersFactory.cs`

```csharp
public static class DomainEventHandlersFactory
{
    private static readonly ConcurrentDictionary<string, Type[]> HandlersDictionary = new();

    public static IEnumerable<IDomainEventHandler> GetHandlers(
        Type type,
        IServiceProvider serviceProvider,
        Assembly assembly)
    {
        Type[] domainEventHandlerTypes = HandlersDictionary.GetOrAdd(
            $"{assembly.GetName().Name}{type.Name}",
            _ => assembly.GetTypes()
                     .Where(t => t.IsAssignableTo(typeof(IDomainEventHandler<>).MakeGenericType(type)))
                     .ToArray());

        List<IDomainEventHandler> handlers = [];
        foreach (Type domainEventHandlerType in domainEventHandlerTypes)
        {
            object handler = serviceProvider.GetRequiredService(domainEventHandlerType);
            handlers.Add((handler as IDomainEventHandler)!);
        }

        return handlers;
    }
}
```

**How it works:**
1. Cache key: `"{AssemblyName}{DomainEventTypeName}"` — unique per module and event type.
2. On first call: scans the module's **Application assembly** for all types implementing `IDomainEventHandler<TDomainEvent>`.
3. Stores the discovered handler types in a `ConcurrentDictionary` (process-lifetime cache).
4. Resolves each handler type from DI — this returns the **decorated** (idempotent) version.

**Important:** Discovery is scoped to a single assembly. This means a domain event in the Events module can only be handled by handlers in `Evently.Modules.Events.Application`. Cross-module communication requires publishing an integration event.

The `IntegrationEventHandlersFactory` (inbox side) works identically, but scans the **Presentation assembly** instead, since integration event handlers live there.

---

## 7. Idempotency Decorator

**File (example):** `src/Modules/Events/Evently.Modules.Events.Infrastructure/Outbox/IdempotentDomainEventHandler.cs`

```csharp
internal sealed class IdempotentDomainEventHandler<TDomainEvent>(
    IDomainEventHandler<TDomainEvent> decorated,
    IDbConnectionFactory dbConnectionFactory)
    : DomainEventHandler<TDomainEvent>
    where TDomainEvent : IDomainEvent
{
    public override async Task Handle(TDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await dbConnectionFactory.OpenConnectionAsync();

        var consumer = new OutboxMessageConsumer(domainEvent.Id, decorated.GetType().Name);

        if (await OutboxConsumerExistsAsync(connection, consumer))
        {
            return;  // Already processed — skip
        }

        await decorated.Handle(domainEvent, cancellationToken);

        await InsertOutboxConsumerAsync(connection, consumer);
    }
}
```

**Check SQL:**
```sql
SELECT EXISTS(
    SELECT 1
    FROM events.outbox_message_consumers
    WHERE outbox_message_id = @OutboxMessageId AND name = @Name
)
```

**Insert SQL:**
```sql
INSERT INTO events.outbox_message_consumers(outbox_message_id, name)
VALUES (@OutboxMessageId, @Name)
```

The `name` is the **concrete handler class name** (e.g., `EventPublishedDomainEventHandler`), not the decorator. This means each concrete handler has its own independent idempotency record. If a domain event has three handlers, each gets its own row in `outbox_message_consumers`.

### Decorator Registration

Decorators are wired up in `AddDomainEventHandlers()` inside each module's infrastructure registration:

```csharp
private static void AddDomainEventHandlers(this IServiceCollection services)
{
    Type[] domainEventHandlers = Application.AssemblyReference.Assembly
        .GetTypes()
        .Where(t => t.IsAssignableTo(typeof(IDomainEventHandler)))
        .ToArray();

    foreach (Type domainEventHandler in domainEventHandlers)
    {
        services.TryAddScoped(domainEventHandler);

        Type domainEvent = domainEventHandler
            .GetInterfaces()
            .Single(i => i.IsGenericType)
            .GetGenericArguments()
            .Single();

        Type closedIdempotentHandler = typeof(IdempotentDomainEventHandler<>)
            .MakeGenericType(domainEvent);

        services.Decorate(domainEventHandler, closedIdempotentHandler);
    }
}
```

This uses the **Scrutor** library's `Decorate` extension. When `DomainEventHandlersFactory` resolves `EventPublishedDomainEventHandler` from DI, it actually gets an `IdempotentDomainEventHandler<EventPublishedDomainEvent>` wrapping the real handler.

---

## 8. Serialization

**File:** `src/Common/Evently.Common.Infrastructure/Serialization/SerializerSettings.cs`

```csharp
public static class SerializerSettings
{
    public static readonly JsonSerializerSettings Instance = new()
    {
        TypeNameHandling = TypeNameHandling.All,
        MetadataPropertyHandling = MetadataPropertyHandling.ReadAhead
    };
}
```

`TypeNameHandling.All` causes Newtonsoft.Json to embed a `$type` property in every serialized object:

```json
{
    "$type": "Evently.Modules.Events.Domain.Events.EventPublishedDomainEvent, Evently.Modules.Events.Domain",
    "EventId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "Id": "1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed",
    "OccurredOnUtc": "2024-01-15T10:30:00Z"
}
```

**Why this matters:**
- Deserialization in `ProcessOutboxJob` targets `IDomainEvent` (an interface), not a concrete type.
- `TypeNameHandling.All` makes Newtonsoft embed the concrete type in the JSON so it can instantiate the right class on read.
- `MetadataPropertyHandling.ReadAhead` handles cases where `$type` appears after other properties in the JSON.

**Security note:** `TypeNameHandling.All` is a known deserialization risk when applied to **untrusted input**, because it can instantiate arbitrary types. Here the content comes from the module's own database table, written by the same application, so this is not a concern in practice.

---

## 9. Domain → Integration Bridge

Domain event handlers form the bridge between the module-internal domain model and the cross-module integration event system. A typical handler:

1. **Enriches** the lightweight domain event with full data from the read model.
2. **Publishes** a richer integration event via `IEventBus`.

**Example:** `EventPublishedDomainEventHandler`

```csharp
// File: src/Modules/Events/Evently.Modules.Events.Application/Events/PublishEvent/EventPublishedDomainEventHandler.cs

internal sealed class EventPublishedDomainEventHandler(ISender sender, IEventBus eventBus)
    : DomainEventHandler<EventPublishedDomainEvent>
{
    public override async Task Handle(
        EventPublishedDomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        // 1. Enrich: domain event only has EventId; query the full event
        Result<EventResponse> result = await sender.Send(
            new GetEventQuery(domainEvent.EventId), cancellationToken);

        if (result.IsFailure)
        {
            throw new EventlyException(nameof(GetEventQuery), result.Error);
        }

        // 2. Publish a rich integration event with all the data consumers need
        await eventBus.PublishAsync(
            new EventPublishedIntegrationEvent(
                domainEvent.Id,
                domainEvent.OccurredOnUtc,
                result.Value.Id,
                result.Value.Title,
                result.Value.Description,
                result.Value.Location,
                result.Value.StartsAtUtc,
                result.Value.EndsAtUtc,
                result.Value.TicketTypes.Select(t => new TicketTypeModel { ... }).ToList()),
            cancellationToken);
    }
}
```

`IEventBus` is a thin wrapper around MassTransit's `IBus.Publish()`:

```csharp
// src/Common/Evently.Common.Infrastructure/EventBus/EventBus.cs
public class EventBus : IEventBus
{
    private readonly IBus _bus;

    public async Task PublishAsync<T>(T integrationEvent, CancellationToken ct)
        where T : IIntegrationEvent
    {
        await _bus.Publish(integrationEvent, ct);
    }
}
```

---

## 10. The Inbox — Mirror Pattern

The inbox is the receiving side of the same pattern, applied to integration events arriving from MassTransit.

### IntegrationEventConsumer

**File (example):** `src/Modules/Events/Evently.Modules.Events.Infrastructure/Inbox/IntegrationEventConsumer.cs`

```csharp
internal sealed class IntegrationEventConsumer<TIntegrationEvent>(IDbConnectionFactory dbConnectionFactory)
    : IConsumer<TIntegrationEvent>
    where TIntegrationEvent : IntegrationEvent
{
    public async Task Consume(ConsumeContext<TIntegrationEvent> context)
    {
        TIntegrationEvent integrationEvent = context.Message;

        var inboxMessage = new InboxMessage
        {
            Id = integrationEvent.Id,
            Type = integrationEvent.GetType().Name,
            Content = JsonConvert.SerializeObject(integrationEvent, SerializerSettings.Instance),
            OccurredOnUtc = integrationEvent.OccurredOnUtc
        };

        const string sql =
            """
            INSERT INTO events.inbox_messages(id, type, content, occurred_on_utc)
            VALUES (@Id, @Type, @Content::json, @OccurredOnUtc)
            """;

        await connection.ExecuteAsync(sql, inboxMessage);
    }
}
```

The consumer immediately writes the event to `inbox_messages` and returns. This keeps MassTransit consumers fast and shifts processing to the `ProcessInboxJob` background service.

### ProcessInboxJob

**File (example):** `src/Modules/Events/Evently.Modules.Events.Infrastructure/Inbox/ProcessInboxJob.cs`

The inbox job is structurally identical to the outbox job:

```sql
SELECT id, content
FROM events.inbox_messages
WHERE processed_on_utc IS NULL
ORDER BY occurred_on_utc
LIMIT {BatchSize}
FOR UPDATE
```

It deserializes to `IIntegrationEvent`, discovers handlers via `IntegrationEventHandlersFactory` (scanning the **Presentation assembly**), and executes them through `IdempotentIntegrationEventHandler`.

### IdempotentIntegrationEventHandler

Same decorator pattern as the outbox side, but uses `inbox_message_consumers`:

```sql
SELECT EXISTS(
    SELECT 1
    FROM events.inbox_message_consumers
    WHERE inbox_message_id = @InboxMessageId AND name = @Name
)
```

### AddIntegrationEventHandlers

```csharp
private static void AddIntegrationEventHandlers(this IServiceCollection services)
{
    Type[] integrationEventHandlers = Presentation.AssemblyReference.Assembly
        .GetTypes()
        .Where(t => t.IsAssignableTo(typeof(IIntegrationEventHandler)))
        .ToArray();

    foreach (Type integrationEventHandler in integrationEventHandlers)
    {
        services.TryAddScoped(integrationEventHandler);

        Type integrationEvent = integrationEventHandler
            .GetInterfaces()
            .Single(i => i.IsGenericType)
            .GetGenericArguments()
            .Single();

        Type closedIdempotentHandler = typeof(IdempotentIntegrationEventHandler<>)
            .MakeGenericType(integrationEvent);

        services.Decorate(integrationEventHandler, closedIdempotentHandler);
    }
}
```

Note: integration event handlers are discovered from the **Presentation assembly** (not Application). This is intentional — integration event handlers dispatch MediatR commands, and they need access to the DI-registered command sender.

---

## 11. Key Design Decisions

### Failure behavior: mark, don't retry

When a handler throws, `ProcessOutboxJob` catches the exception, writes it to the `error` column, and **still marks the message as processed** (`processed_on_utc` is set). The message will not be retried on the next job run.

Consequence: a failed message does not block subsequent messages. To reprocess a failed message manually, set `processed_on_utc = NULL` and clear `error`.

This is intentional: blocking the queue on a single bad message would cause cascading delays. The trade-off is that manual intervention is required for failed messages.

### Idempotency scope: per handler, not per message

The `outbox_message_consumers` primary key is `(outbox_message_id, handler_name)`. A single domain event with multiple handlers gets one row per handler. If handler A succeeds and handler B fails, retrying the message will skip A but re-execute B.

### No shared outbox

Each module has its own `outbox_messages` and `inbox_messages` tables in its own schema. There is no shared outbox table. This enforces module isolation at the database level.

### Assembly-scoped handler discovery

`DomainEventHandlersFactory` only finds handlers in the assembly it is given (`Application.AssemblyReference.Assembly`). This means:
- Domain event handlers must live in the module's Application layer.
- Integration event handlers must live in the module's Presentation layer.
- Cross-module domain event handling is impossible by design — it requires the integration event bridge.

### Type name in OutboxMessage

The `Type` column stores `domainEvent.GetType().Name` (short name, e.g. `"EventPublishedDomainEvent"`), but deserialization relies on the `$type` metadata embedded in the `Content` JSON (full assembly-qualified name). The `Type` column is used for observability and debugging, not for deserialization routing.

---

## 12. End-to-End Flow

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│  HTTP Request                                                                   │
│  POST /events/{id}/publish                                                      │
└───────────────────────────────┬─────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  PublishEventCommandHandler                                                     │
│  event.Publish()  →  Raise(new EventPublishedDomainEvent(event.Id))             │
│  unitOfWork.SaveChangesAsync()                                                  │
└───────────────────────────────┬─────────────────────────────────────────────────┘
                                │  EF Core SaveChanges()
                                ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  InsertOutboxMessagesInterceptor.SavingChangesAsync()                           │
│  ─ scan ChangeTracker for Entity instances                                      │
│  ─ drain DomainEvents from each entity                                          │
│  ─ serialize each event with TypeNameHandling.All                               │
│  ─ INSERT INTO events.outbox_messages (same transaction)                        │
└─────────────────────────────────────────────────────────────────────────────────┘
                                │  Transaction committed
                                │  (state change + outbox message are atomic)
                                │
                   [time passes — BackgroundService polls ProcessOutboxJob]
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  ProcessOutboxJob.Execute()                                                     │
│  ─ BEGIN TRANSACTION                                                            │
│  ─ SELECT ... FROM events.outbox_messages WHERE processed_on_utc IS NULL        │
│    ORDER BY occurred_on_utc LIMIT {BatchSize} FOR UPDATE                        │
│  ─ for each row:                                                                │
│      deserialize Content → IDomainEvent                                         │
│      DomainEventHandlersFactory.GetHandlers(domainEvent.GetType(), assembly)    │
│      for each handler:                                                          │
│          handler.Handle(domainEvent)   ← handler is decorated (idempotent)      │
│      UPDATE outbox_messages SET processed_on_utc = now()                        │
│  ─ COMMIT                                                                       │
└───────────────────────────────┬─────────────────────────────────────────────────┘
                                │  handler.Handle() resolves to:
                                ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  IdempotentDomainEventHandler<EventPublishedDomainEvent>                        │
│  ─ SELECT EXISTS ... FROM events.outbox_message_consumers WHERE id = X, name = Y│
│  ─ if exists → return (skip)                                                    │
│  ─ decorated.Handle(domainEvent)                                                │
│  ─ INSERT INTO events.outbox_message_consumers (id, name)                       │
└───────────────────────────────┬─────────────────────────────────────────────────┘
                                │  decorated = real handler:
                                ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  EventPublishedDomainEventHandler                                               │
│  ─ sender.Send(new GetEventQuery(domainEvent.EventId))  [enrich]                │
│  ─ eventBus.PublishAsync(new EventPublishedIntegrationEvent(...))               │
│    └─ MassTransit IBus.Publish()                                                │
└───────────────────────────────┬─────────────────────────────────────────────────┘
                                │  MassTransit delivers to all subscribed modules
                                ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  IntegrationEventConsumer<EventPublishedIntegrationEvent>  (Ticketing module)   │
│  ─ INSERT INTO ticketing.inbox_messages                                         │
└───────────────────────────────┬─────────────────────────────────────────────────┘
                                │
                   [time passes — BackgroundService polls ProcessInboxJob]
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  ProcessInboxJob.Execute()  (Ticketing module)                                  │
│  ─ SELECT ... FROM ticketing.inbox_messages ... FOR UPDATE                      │
│  ─ deserialize → IIntegrationEvent                                              │
│  ─ IntegrationEventHandlersFactory.GetHandlers(...)  [Presentation assembly]    │
│  ─ IdempotentIntegrationEventHandler checks inbox_message_consumers             │
│  ─ EventPublishedIntegrationEventHandler.Handle()                               │
│      → sender.Send(new CreateEventCommand(...))                                 │
│  ─ UPDATE ticketing.inbox_messages SET processed_on_utc = now()                 │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### Key guarantees at each step

| Step | Guarantee |
|------|-----------|
| `Raise()` | In-memory only; no persistence yet |
| `InsertOutboxMessagesInterceptor` | Atomic with state change (same transaction) |
| `ProcessOutboxJob` `FOR UPDATE` | Only one job instance processes each row (no duplicates from concurrency) |
| `IdempotentDomainEventHandler` | Handler executes at most once per `(messageId, handlerName)` pair |
| `IntegrationEventConsumer` | Fast; just writes to inbox (no business logic) |
| `IdempotentIntegrationEventHandler` | Integration handler executes at most once per `(messageId, handlerName)` pair |

---

## Key Infrastructure Files

| Component | Location |
|-----------|----------|
| `IDomainEvent` | `src/Common/Evently.Common.Domain/IDomainEvent.cs` |
| `DomainEvent` | `src/Common/Evently.Common.Domain/DomainEvent.cs` |
| `Entity` | `src/Common/Evently.Common.Domain/Entity.cs` |
| `InsertOutboxMessagesInterceptor` | `src/Common/Evently.Common.Infrastructure/Outbox/InsertOutboxMessagesInterceptor.cs` |
| `OutboxMessage` | `src/Common/Evently.Common.Infrastructure/Outbox/OutboxMessage.cs` |
| `OutboxMessageConsumer` | `src/Common/Evently.Common.Infrastructure/Outbox/OutboxMessageConsumer.cs` |
| `DomainEventHandlersFactory` | `src/Common/Evently.Common.Infrastructure/Outbox/DomainEventHandlersFactory.cs` |
| `InboxMessage` | `src/Common/Evently.Common.Infrastructure/Inbox/InboxMessage.cs` |
| `IntegrationEventHandlersFactory` | `src/Common/Evently.Common.Infrastructure/Inbox/IntegrationEventHandlersFactory.cs` |
| `SerializerSettings` | `src/Common/Evently.Common.Infrastructure/Serialization/SerializerSettings.cs` |
| `ProcessOutboxJob` (Events) | `src/Modules/Events/Evently.Modules.Events.Infrastructure/Outbox/ProcessOutboxJob.cs` |
| `IdempotentDomainEventHandler` (Events) | `src/Modules/Events/Evently.Modules.Events.Infrastructure/Outbox/IdempotentDomainEventHandler.cs` |
| `ProcessInboxJob` (Events) | `src/Modules/Events/Evently.Modules.Events.Infrastructure/Inbox/ProcessInboxJob.cs` |
| `IdempotentIntegrationEventHandler` (Events) | `src/Modules/Events/Evently.Modules.Events.Infrastructure/Inbox/IdempotentIntegrationEventHandler.cs` |
| `IntegrationEventConsumer` (Events) | `src/Modules/Events/Evently.Modules.Events.Infrastructure/Inbox/IntegrationEventConsumer.cs` |
| `AddDomainEventHandlers` registration | `src/Modules/Events/Evently.Modules.Events.Infrastructure/EventsModule.cs` |
