# Cross-Module Communication - Technical Documentation

## Table of Contents

- [Overview](#overview)
- [1. Architecture Layers](#1-architecture-layers)
- [2. Outbox Pattern (Publishing Side)](#2-outbox-pattern-publishing-side)
- [3. Inbox Pattern (Consuming Side)](#3-inbox-pattern-consuming-side)
- [4. Idempotency Guarantees](#4-idempotency-guarantees)
- [5. MassTransit Configuration](#5-masstransit-configuration)
- [6. CancelEventSaga (Orchestration Pattern)](#6-canceleventsaga-orchestration-pattern)
- [7. Complete Integration Event Map](#7-complete-integration-event-map)
- [8. Reliability Guarantees](#8-reliability-guarantees)
- [9. Key Infrastructure Files](#9-key-infrastructure-files)
- [10. Architecture Enforcement](#10-architecture-enforcement)

---

## Overview

Evently uses an **event-driven architecture** for cross-module communication. Modules never call each other directly -- they communicate exclusively through **integration events** delivered via the **Outbox/Inbox pattern** with **MassTransit** as the message transport. This ensures reliable, eventually consistent communication with at-least-once delivery guarantees.

---

## 1. Architecture Layers

```
+-----------------------+     +-----------------------+     +-----------------------+
|     Module A          |     |    MassTransit        |     |     Module B          |
|                       |     |    (PostgreSQL)       |     |                       |
| Entity.Method()       |     |                       |     |                       |
|   |                   |     |                       |     |                       |
|   v                   |     |                       |     |                       |
| DomainEvent raised    |     |                       |     |                       |
|   |                   |     |                       |     |                       |
|   v                   |     |                       |     |                       |
| SaveChanges()         |     |                       |     |                       |
| -> OutboxInterceptor  |     |                       |     |                       |
| -> outbox_messages    |     |                       |     |                       |
|   |                   |     |                       |     |                       |
|   v (Quartz job)      |     |                       |     |                       |
| ProcessOutboxJob      |     |                       |     |                       |
| -> DomainEventHandler |     |                       |     |                       |
|   |                   |     |                       |     |                       |
|   v                   |     |                       |     |                       |
| IEventBus.Publish()   | --> | Message Queue/Topic   | --> | IntegrationEvent      |
|                       |     |                       |     | Consumer              |
|                       |     |                       |     |   |                   |
|                       |     |                       |     |   v                   |
|                       |     |                       |     | inbox_messages        |
|                       |     |                       |     |   |                   |
|                       |     |                       |     |   v (Quartz job)      |
|                       |     |                       |     | ProcessInboxJob       |
|                       |     |                       |     | -> IntegrationEvent   |
|                       |     |                       |     |    Handler            |
|                       |     |                       |     |   |                   |
|                       |     |                       |     |   v                   |
|                       |     |                       |     | MediatR Command       |
+-----------------------+     +-----------------------+     +-----------------------+
```

---

## 2. Outbox Pattern (Publishing Side)

### 2.1 Domain Event Capture

**File:** `src/Common/Evently.Common.Infrastructure/Outbox/InsertOutboxMessagesInterceptor.cs`

This is an EF Core `SaveChangesInterceptor` that intercepts every `SaveChanges()` call:

1. Scans all tracked entities (via `ChangeTracker`)
2. Extracts pending domain events from each entity's `_domainEvents` collection
3. Serializes each domain event to JSON
4. Creates `OutboxMessage` records:
   ```
   {
     Id: Guid.NewGuid(),
     Type: "Evently.Modules.Events.Domain.Events.EventPublishedDomainEvent",
     Content: "{ serialized JSON }",
     OccurredOnUtc: DateTime.UtcNow,
     ProcessedOnUtc: null,
     Error: null
   }
   ```
5. Inserts into the module's `outbox_messages` table
6. Clears domain events from entities

**Key:** This happens in the **same transaction** as the domain model change, guaranteeing atomicity.

### 2.2 Outbox Processing

**File:** Each module has `Infrastructure/Outbox/ProcessOutboxJob.cs`

Quartz scheduled job that runs on a configurable interval:

1. Queries `outbox_messages WHERE processed_on_utc IS NULL ORDER BY occurred_on_utc LIMIT N`
2. For each message:
   a. Deserializes the domain event from JSON
   b. Resolves all registered `IDomainEventHandler<T>` via `DomainEventHandlersFactory`
   c. Executes each handler (wrapped with `IdempotentDomainEventHandler`)
   d. On success: sets `processed_on_utc = DateTime.UtcNow`
   e. On failure: records error message in `error` column

### 2.3 Domain Event Handler -> Integration Event

Domain event handlers enrich the lightweight domain event with full data and publish an integration event:

```csharp
// EventPublishedDomainEventHandler
public async Task Handle(EventPublishedDomainEvent domainEvent, CancellationToken ct)
{
    // 1. Enrich: Query full event data (domain event only has EventId)
    EventResponse eventData = await sender.Send(new GetEventQuery(domainEvent.EventId), ct);

    // 2. Publish integration event via IEventBus (MassTransit)
    await eventBus.PublishAsync(new EventPublishedIntegrationEvent(
        Id: domainEvent.Id,
        OccurredOnUtc: domainEvent.OccurredOnUtc,
        EventId: eventData.Id,
        Title: eventData.Title,
        // ... all fields including ticket types
    ), ct);
}
```

### 2.4 Outbox Message Table Schema

```sql
CREATE TABLE {schema}.outbox_messages (
    id              UUID PRIMARY KEY,
    type            TEXT NOT NULL,          -- Full .NET type name
    content         JSONB NOT NULL,         -- Serialized domain event
    occurred_on_utc TIMESTAMP NOT NULL,     -- When the event happened
    processed_on_utc TIMESTAMP NULL,        -- When it was processed (NULL = pending)
    error           TEXT NULL               -- Error message if processing failed
);
```

---

## 3. Inbox Pattern (Consuming Side)

### 3.1 MassTransit Consumer

**File:** Each module has `Infrastructure/Inbox/IntegrationEventConsumer.cs`

Generic consumer that receives integration events from MassTransit:

```csharp
public class IntegrationEventConsumer<T> : IConsumer<T>
    where T : class, IIntegrationEvent
{
    public async Task Consume(ConsumeContext<T> context)
    {
        // 1. Serialize the integration event
        // 2. Create InboxMessage record
        // 3. Insert into inbox_messages table
    }
}
```

### 3.2 Inbox Processing

**File:** Each module has `Infrastructure/Inbox/ProcessInboxJob.cs`

Quartz scheduled job:

1. Queries `inbox_messages WHERE processed_on_utc IS NULL ORDER BY occurred_on_utc LIMIT N`
2. For each message:
   a. Deserializes the integration event
   b. Resolves all registered `IIntegrationEventHandler<T>` via `IntegrationEventHandlersFactory`
   c. Executes each handler (wrapped with `IdempotentIntegrationEventHandler`)
   d. Marks as processed or records error

### 3.3 Integration Event Handler

Integration event handlers in consuming modules typically dispatch MediatR commands:

```csharp
// In Ticketing module
public class EventPublishedIntegrationEventHandler
    : IntegrationEventHandler<EventPublishedIntegrationEvent>
{
    public override async Task Handle(EventPublishedIntegrationEvent integrationEvent, CancellationToken ct)
    {
        await sender.Send(new CreateEventCommand(
            integrationEvent.EventId,
            integrationEvent.Title,
            // ... maps integration event to command
        ), ct);
    }
}
```

### 3.4 Inbox Message Table Schema

```sql
CREATE TABLE {schema}.inbox_messages (
    id              UUID PRIMARY KEY,
    type            TEXT NOT NULL,
    content         JSONB NOT NULL,
    occurred_on_utc TIMESTAMP NOT NULL,
    processed_on_utc TIMESTAMP NULL,
    error           TEXT NULL
);
```

---

## 4. Idempotency Guarantees

### 4.1 Outbox Idempotency

**File:** Each module's `Infrastructure/Outbox/IdempotentDomainEventHandler.cs`

Decorator pattern wrapping domain event handlers:

```
1. CHECK: SELECT FROM outbox_message_consumers
          WHERE outbox_message_id = @id AND name = @handlerName

2. IF EXISTS: Skip (already processed)

3. IF NOT EXISTS:
   a. Execute wrapped handler
   b. INSERT INTO outbox_message_consumers (outbox_message_id, name)
      VALUES (@id, @handlerName)
```

**Table Schema:**
```sql
CREATE TABLE {schema}.outbox_message_consumers (
    outbox_message_id UUID NOT NULL,
    name              TEXT NOT NULL,
    PRIMARY KEY (outbox_message_id, name)
);
```

### 4.2 Inbox Idempotency

**File:** Each module's `Infrastructure/Inbox/IdempotentIntegrationEventHandler.cs`

Same pattern for integration event handlers:

```sql
CREATE TABLE {schema}.inbox_message_consumers (
    inbox_message_id UUID NOT NULL,
    name             TEXT NOT NULL,
    PRIMARY KEY (inbox_message_id, name)
);
```

### 4.3 Why Idempotency Matters

- Outbox/inbox jobs may crash mid-processing and retry
- Network issues may cause duplicate message delivery
- MassTransit may redeliver messages on consumer failure
- Idempotent handlers ensure **exactly-once semantics** on top of at-least-once delivery

---

## 5. MassTransit Configuration

### 5.1 Transport

**File:** `src/Common/Evently.Common.Infrastructure/InfrastructureConfiguration.cs`

MassTransit is configured with **PostgreSQL transport** (not Redis):

```csharp
services.AddMassTransit(configure =>
{
    // Each module registers its consumers
    foreach (Action<IRegistrationConfigurator> configureConsumers in moduleConfigureConsumers)
    {
        configureConsumers(configure);
    }

    configure.UsingPostgres((context, cfg) =>
    {
        cfg.ConfigureEndpoints(context);
    });
});
```

### 5.2 Consumer Registration by Module

**Events Module:**
```csharp
public void ConfigureConsumers(IRegistrationConfigurator registrationConfigurator)
{
    registrationConfigurator.AddSagaStateMachine<CancelEventSaga, CancelEventState>()
        .EntityFrameworkRepository(r =>
        {
            r.ExistingDbContext<EventsDbContext>();
            r.UsePostgres();
        });
}
```

**Ticketing Module:**
```csharp
public void ConfigureConsumers(IRegistrationConfigurator registrationConfigurator)
{
    registrationConfigurator.AddConsumer<IntegrationEventConsumer<UserRegisteredIntegrationEvent>>();
    registrationConfigurator.AddConsumer<IntegrationEventConsumer<UserProfileUpdatedIntegrationEvent>>();
    registrationConfigurator.AddConsumer<IntegrationEventConsumer<EventPublishedIntegrationEvent>>();
    registrationConfigurator.AddConsumer<IntegrationEventConsumer<TicketTypePriceChangedIntegrationEvent>>();
    registrationConfigurator.AddConsumer<IntegrationEventConsumer<EventCancellationStartedIntegrationEvent>>();
}
```

**Attendance Module:**
```csharp
public void ConfigureConsumers(IRegistrationConfigurator registrationConfigurator)
{
    registrationConfigurator.AddConsumer<IntegrationEventConsumer<UserRegisteredIntegrationEvent>>();
    registrationConfigurator.AddConsumer<IntegrationEventConsumer<UserProfileUpdatedIntegrationEvent>>();
    registrationConfigurator.AddConsumer<IntegrationEventConsumer<EventPublishedIntegrationEvent>>();
    registrationConfigurator.AddConsumer<IntegrationEventConsumer<TicketIssuedIntegrationEvent>>();
    registrationConfigurator.AddConsumer<IntegrationEventConsumer<EventCancellationStartedIntegrationEvent>>();
}
```

**Users Module:** No consumers (only publishes).

### 5.3 Event Bus Implementation

**File:** `src/Common/Evently.Common.Infrastructure/EventBus/EventBus.cs`

```csharp
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

Simple wrapper around MassTransit's `IBus.Publish()`.

---

## 6. CancelEventSaga (Orchestration Pattern)

### 6.1 Overview

Event cancellation is the most complex cross-module flow. It requires:
1. Refunding all payments (Ticketing)
2. Archiving all tickets (Ticketing)
3. Confirming completion only after both are done

This is implemented as a **MassTransit Saga State Machine** in the Events module.

### 6.2 Saga State Machine

**File:** `src/Modules/Events/Evently.Modules.Events.Presentation/Events/CancelEventSaga/CancelEventSaga.cs`

**States:**
- `CancellationStarted` -- Cleanup in progress
- `PaymentsRefunded` -- Ticketing confirmed payments refunded
- `TicketsArchived` -- Ticketing confirmed tickets archived

**Events (Messages):**
- `EventCanceledIntegrationEvent` -- Trigger
- `EventCancellationStartedIntegrationEvent` -- Published by saga
- `EventPaymentsRefundedIntegrationEvent` -- From Ticketing
- `EventTicketsArchivedIntegrationEvent` -- From Ticketing
- `EventCancellationCompletedIntegrationEvent` -- Published by saga on completion

### 6.3 State Machine Definition

```
State Machine: CancelEventSaga
CorrelateBy: EventId

Initial -> CancellationStarted:
    Trigger: EventCanceledIntegrationEvent
    Actions:
        - Publish EventCancellationStartedIntegrationEvent
        - Transition to CancellationStarted

CancellationStarted -> PaymentsRefunded:
    Trigger: EventPaymentsRefundedIntegrationEvent
    Transition to PaymentsRefunded

CancellationStarted -> TicketsArchived:
    Trigger: EventTicketsArchivedIntegrationEvent
    Transition to TicketsArchived

PaymentsRefunded -> Final:
    Trigger: EventTicketsArchivedIntegrationEvent (composite: both received)
    Actions:
        - Publish EventCancellationCompletedIntegrationEvent
        - Finalize

TicketsArchived -> Final:
    Trigger: EventPaymentsRefundedIntegrationEvent (composite: both received)
    Actions:
        - Publish EventCancellationCompletedIntegrationEvent
        - Finalize
```

### 6.4 Saga State Persistence

**File:** `Infrastructure/Database/CancelEventStateConfiguration.cs`

The saga state is persisted to a database table via EF Core:

```sql
CREATE TABLE events.cancel_event_states (
    correlation_id UUID PRIMARY KEY,  -- EventId
    current_state  TEXT NOT NULL,      -- State machine state name
    -- additional saga state fields
);
```

### 6.5 Complete Cancellation Flow

```
User: DELETE /events/{id}/cancel
    |
    v
CancelEventCommandHandler
    -> event.Cancel(utcNow)
    -> Raises EventCanceledDomainEvent
    -> SaveChanges() -> Outbox
    |
    v (ProcessOutboxJob)
EventCanceledDomainEventHandler
    -> Publishes EventCanceledIntegrationEvent via IEventBus
    |
    v (MassTransit)
CancelEventSaga receives EventCanceledIntegrationEvent
    -> Creates saga instance (CorrelationId = EventId)
    -> Publishes EventCancellationStartedIntegrationEvent
    -> State = CancellationStarted
    |
    +---------------------+---------------------+
    |                                           |
    v                                           v
Ticketing: IntegrationEventConsumer             Attendance: IntegrationEventConsumer
    -> inbox_messages                               -> inbox_messages
    |                                               |
    v (ProcessInboxJob)                             v (ProcessInboxJob)
CancelEventCommand                              (Cleanup handler)
    -> event.Cancel()
    -> Raises EventCanceledDomainEvent
    |
    +---------------------------+---------------------------+
    |                           |                           |
    v                           v                           v
ArchiveTickets                 RefundPayments              RefundOrders
DomainEventHandler             DomainEventHandler          DomainEventHandler
    |                           |                           |
    v                           v                           v
ArchiveTicketsForEvent         RefundPaymentsForEvent      RefundOrdersForEvent
Command                        Command                     Command
    |                           |                           |
    v                           v                           v
For each ticket:               For each payment:           For each order:
    ticket.Archive()               payment.Refund(amount)      order.Refund()
    |                           |                           (Paid -> Refunded)
    v                           v
event.TicketsArchived()        event.PaymentsRefunded()
    |                           |
    v                           v
EventTicketsArchived           EventPaymentsRefunded
DomainEvent -> Outbox          DomainEvent -> Outbox
    |                           |
    v                           v
EventTicketsArchived           EventPaymentsRefunded
IntegrationEvent               IntegrationEvent
    |                           |
    +---------------------------+
    |
    v (MassTransit -> Saga)
CancelEventSaga
    Both events received (composite event)
    -> Publishes EventCancellationCompletedIntegrationEvent
    -> State = Finalized (saga deleted)
```

---

## 7. Complete Integration Event Map

### 7.1 Event Flow Diagram

```
    USERS MODULE                EVENTS MODULE               TICKETING MODULE          ATTENDANCE MODULE
    ============                =============               ================          =================

    UserRegistered ────────────────────────────────────────> CreateCustomer
    IntegrationEvent ─────────────────────────────────────────────────────────────────> CreateAttendee

    UserProfileUpdated ───────────────────────────────────> UpdateCustomer
    IntegrationEvent ─────────────────────────────────────────────────────────────────> UpdateAttendee

                            EventPublished ────────────────> CreateEvent (+ TicketTypes)
                            IntegrationEvent ─────────────────────────────────────────> CreateEvent

                            TicketTypePrice ───────────────> UpdateTicketTypePrice
                            ChangedIntegrationEvent

                            EventCanceled ─────> CancelEventSaga
                            IntegrationEvent       |
                                                   v
                            EventCancellation ────> CancelEvent ──> Refund + Archive
                            StartedIntegrationEvent ──────────────────────────────────> Cleanup

                                                    EventPaymentsRefunded <──────────
                                                    IntegrationEvent (to saga)

                                                    EventTicketsArchived <───────────
                                                    IntegrationEvent (to saga)

                            EventCancellation <───── Saga completes
                            CompletedIntegrationEvent

                                                    OrderCreated ────────────────────
                                                    IntegrationEvent (informational)

                                                    TicketIssued ────────────────────> CreateTicket
                                                    IntegrationEvent

                                                    TicketTypeSoldOut ───────────────
                                                    IntegrationEvent (informational)

                                                    TicketArchived ─────────────────
                                                    IntegrationEvent (informational)
```

### 7.2 Event Catalog

| # | Event | Publisher | Consumer(s) | Purpose |
|---|-------|-----------|-------------|---------|
| 1 | UserRegisteredIntegrationEvent | Users | Ticketing, Attendance | Create local customer/attendee |
| 2 | UserProfileUpdatedIntegrationEvent | Users | Ticketing, Attendance | Sync profile changes |
| 3 | EventPublishedIntegrationEvent | Events | Ticketing, Attendance | Create local event copies |
| 4 | EventCanceledIntegrationEvent | Events | Events (Saga) | Trigger cancellation saga |
| 5 | EventCancellationStartedIntegrationEvent | Events (Saga) | Ticketing, Attendance | Start cleanup |
| 6 | EventCancellationCompletedIntegrationEvent | Events (Saga) | -- | Signal completion |
| 7 | EventRescheduledIntegrationEvent | Events | Ticketing, Attendance | Update event times |
| 8 | TicketTypePriceChangedIntegrationEvent | Events | Ticketing | Sync price changes |
| 9 | OrderCreatedIntegrationEvent | Ticketing | -- | Informational |
| 10 | TicketIssuedIntegrationEvent | Ticketing | Attendance | Create check-in ticket |
| 11 | EventPaymentsRefundedIntegrationEvent | Ticketing | Events (Saga) | Signal refund complete |
| 12 | EventTicketsArchivedIntegrationEvent | Ticketing | Events (Saga) | Signal archive complete |
| 13 | TicketArchivedIntegrationEvent | Ticketing | -- | Informational |
| 14 | TicketTypeSoldOutIntegrationEvent | Ticketing | -- | Informational |

---

## 8. Reliability Guarantees

### 8.1 Transactional Outbox
- Domain events are stored in the **same database transaction** as the domain model change
- If the transaction rolls back, the outbox message is also rolled back
- No "ghost events" (events published without the corresponding state change)

### 8.2 At-Least-Once Delivery
- Outbox/inbox jobs retry unprocessed messages
- Messages are only marked as processed **after** successful handler execution
- If processing fails, the message remains unprocessed and will be retried

### 8.3 Idempotent Processing
- `outbox_message_consumers` and `inbox_message_consumers` tables prevent duplicate handler execution
- Even if a message is processed twice, the handler only runs once

### 8.4 Ordered Processing
- Messages are processed in `occurred_on_utc` order (FIFO within a module)
- No cross-module ordering guarantees (eventual consistency)

### 8.5 Error Handling
- Failed messages have their error recorded in the `error` column
- Messages can be investigated and reprocessed manually
- No automatic dead-letter queue (messages remain in the table)

---

## 9. Key Infrastructure Files

| Component | File Location |
|-----------|--------------|
| IEventBus interface | `src/Common/Evently.Common.Application/EventBus/IEventBus.cs` |
| IIntegrationEvent interface | `src/Common/Evently.Common.Application/EventBus/IIntegrationEvent.cs` |
| IntegrationEvent base class | `src/Common/Evently.Common.Application/EventBus/IntegrationEvent.cs` |
| EventBus (MassTransit) | `src/Common/Evently.Common.Infrastructure/EventBus/EventBus.cs` |
| OutboxMessage model | `src/Common/Evently.Common.Infrastructure/Outbox/OutboxMessage.cs` |
| InboxMessage model | `src/Common/Evently.Common.Infrastructure/Inbox/InboxMessage.cs` |
| InsertOutboxMessagesInterceptor | `src/Common/Evently.Common.Infrastructure/Outbox/InsertOutboxMessagesInterceptor.cs` |
| DomainEventHandlersFactory | `src/Common/Evently.Common.Infrastructure/Outbox/DomainEventHandlersFactory.cs` |
| IntegrationEventHandlersFactory | `src/Common/Evently.Common.Infrastructure/Inbox/IntegrationEventHandlersFactory.cs` |
| MassTransit config | `src/Common/Evently.Common.Infrastructure/InfrastructureConfiguration.cs` |
| CancelEventSaga | `src/Modules/Events/Evently.Modules.Events.Presentation/Events/CancelEventSaga/` |

---

## 10. Architecture Enforcement

**File:** `test/Evently.ArchitectureTests/`

Architecture tests (using NetArchTest) enforce module isolation:
- Modules cannot reference other modules' Domain, Application, or Infrastructure layers
- Modules can ONLY reference other modules' `IntegrationEvents` project
- This ensures all cross-module communication goes through integration events
- Prevents developers from accidentally creating tight coupling between modules
