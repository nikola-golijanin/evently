# Events Module - Technical Documentation

## Table of Contents

- [Overview](#overview)
- [1. Layer Structure](#1-layer-structure)
- [2. Domain Layer](#2-domain-layer)
  - [2.1 Entities & Aggregates](#21-entities--aggregates)
  - [2.2 Domain Events](#22-domain-events)
  - [2.3 Repository Interfaces](#23-repository-interfaces)
  - [2.4 Domain Errors](#24-domain-errors)
- [3. Application Layer](#3-application-layer)
  - [3.1 Commands](#31-commands)
  - [3.2 Queries](#32-queries)
  - [3.3 Domain Event Handlers](#33-domain-event-handlers-outbox---integration-event-publishers)
- [4. Infrastructure Layer](#4-infrastructure-layer)
- [5. Presentation Layer (API Endpoints)](#5-presentation-layer-api-endpoints)
- [6. Integration Events (Public Contracts)](#6-integration-events-public-contracts)
- [7. CancelEventSaga (MassTransit State Machine)](#7-canceleventsaga-masstransit-state-machine)

---

## Overview

The Events module is the **catalog and lifecycle manager** for events. It is the authoritative source for event definitions, categories, and ticket type specifications. Other modules (Ticketing, Attendance) create local copies of this data when events are published.

**Namespace:** `Evently.Modules.Events`
**Database Schema:** `events`

---

## 1. Layer Structure

```
Evently.Modules.Events.Domain/              -- Entities, value objects, domain events, errors
Evently.Modules.Events.Application/         -- Commands, queries, handlers, validators
Evently.Modules.Events.Infrastructure/      -- EF Core, repositories, outbox/inbox, module config
Evently.Modules.Events.Presentation/        -- Minimal API endpoints, CancelEventSaga
Evently.Modules.Events.IntegrationEvents/   -- Public event contracts for other modules
Evently.Modules.Events.UnitTests/           -- Unit tests
```

---

## 2. Domain Layer

### 2.1 Entities & Aggregates

#### Event (Aggregate Root)
**File:** `Domain/Events/Event.cs`

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Primary key |
| CategoryId | Guid | FK to Category |
| Title | string | Event title |
| Description | string | Event description |
| Location | string | Venue/location |
| StartsAtUtc | DateTime | Start time (UTC) |
| EndsAtUtc | DateTime? | Optional end time (UTC) |
| Status | EventStatus | Draft, Published, Completed, Canceled |

**Methods:**
- `static Create(categoryId, title, description, location, startsAt, endsAt)` -- Factory. Validates end > start. Raises `EventCreatedDomainEvent`.
- `Publish()` -- Transitions Draft -> Published. Raises `EventPublishedDomainEvent`. Fails if not Draft.
- `Reschedule(startsAt, endsAt)` -- Updates times. Raises `EventRescheduledDomainEvent`.
- `Cancel(utcNow)` -- Cancels if not already canceled and not started. Raises `EventCanceledDomainEvent`.

#### Category (Aggregate Root)
**File:** `Domain/Categories/Category.cs`

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Primary key |
| Name | string | Category name |
| IsArchived | bool | Soft-delete flag |

**Methods:**
- `static Create(name)` -- Factory. Raises `CategoryCreatedDomainEvent`.
- `ChangeName(name)` -- Updates name if different. Raises `CategoryNameChangedDomainEvent`.
- `Archive()` -- Sets `IsArchived = true`. Raises `CategoryArchivedDomainEvent`.

#### TicketType
**File:** `Domain/TicketTypes/TicketType.cs`

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Primary key |
| EventId | Guid | FK to Event |
| Name | string | Tier name (e.g., "VIP") |
| Price | decimal | Ticket price |
| Currency | string | Currency code (e.g., "USD") |
| Quantity | decimal | Total available quantity |

**Methods:**
- `static Create(eventId, name, price, currency, quantity)` -- Factory. Raises `TicketTypeCreatedDomainEvent`.
- `UpdatePrice(price)` -- Updates if different. Raises `TicketTypePriceChangedDomainEvent`.

### 2.2 Domain Events

| Event | Properties | When Raised |
|-------|-----------|-------------|
| EventCreatedDomainEvent | EventId | Event.Create() |
| EventPublishedDomainEvent | EventId | Event.Publish() |
| EventRescheduledDomainEvent | EventId, StartsAtUtc, EndsAtUtc | Event.Reschedule() |
| EventCanceledDomainEvent | EventId | Event.Cancel() |
| CategoryCreatedDomainEvent | CategoryId | Category.Create() |
| CategoryArchivedDomainEvent | CategoryId | Category.Archive() |
| CategoryNameChangedDomainEvent | CategoryId, Name | Category.ChangeName() |
| TicketTypeCreatedDomainEvent | TicketTypeId | TicketType.Create() |
| TicketTypePriceChangedDomainEvent | TicketTypeId, Price | TicketType.UpdatePrice() |

### 2.3 Repository Interfaces

**IEventRepository**
- `Task<Event?> GetAsync(Guid id, CancellationToken ct)`
- `void Insert(Event event)`

**ICategoryRepository**
- `Task<Category?> GetAsync(Guid id, CancellationToken ct)`
- `void Insert(Category category)`

**ITicketTypeRepository**
- `Task<TicketType?> GetAsync(Guid id, CancellationToken ct)`
- `Task<bool> ExistsAsync(Guid eventId, CancellationToken ct)`
- `void Insert(TicketType ticketType)`

### 2.4 Domain Errors

**EventErrors:**
- `NotFound(Guid eventId)` -- "Events.NotFound"
- `StartDateInPast` -- "Events.StartDateInPast"
- `EndDatePrecedesStartDate` -- "Events.EndDatePrecedesStart"
- `NoTicketsFound` -- "Events.NoTicketsFound"
- `NotDraft` -- "Events.NotDraft"
- `AlreadyCanceled` -- "Events.AlreadyCanceled"
- `AlreadyStarted` -- "Events.AlreadyStarted"

**CategoryErrors:**
- `NotFound(Guid categoryId)` -- "Categories.NotFound"
- `AlreadyArchived` -- "Categories.AlreadyArchived"

**TicketTypeErrors:**
- `NotFound(Guid ticketTypeId)` -- "TicketTypes.NotFound"

---

## 3. Application Layer

### 3.1 Commands

#### CreateEventCommand
**Handler:** `CreateEventCommandHandler`
```
Input:  CategoryId, Title, Description, Location, StartsAtUtc, EndsAtUtc?
Output: Result<Guid>
```
1. Validates start date is not in the past
2. Fetches Category (returns error if not found)
3. Calls `Event.Create(...)` factory
4. Inserts via repository
5. Saves changes (triggers outbox interceptor)

**Validator:** CategoryId NotEmpty, Title NotEmpty, Description NotEmpty, Location NotEmpty, StartsAtUtc NotEmpty, EndsAtUtc > StartsAtUtc (when provided)

#### PublishEventCommand
**Handler:** `PublishEventCommandHandler`
```
Input:  EventId
Output: Result
```
1. Fetches Event (error if not found)
2. Checks ticket types exist via `ITicketTypeRepository.ExistsAsync(eventId)`
3. Calls `event.Publish()` -- fails if not in Draft status
4. Saves changes

#### RescheduleEventCommand
**Handler:** `RescheduleEventCommandHandler`
```
Input:  EventId, StartsAtUtc, EndsAtUtc?
Output: Result
```
1. Fetches Event
2. Validates new start date not in past
3. Calls `event.Reschedule(startsAt, endsAt)`
4. Saves changes

**Validator:** StartsAtUtc NotEmpty, EndsAtUtc > StartsAtUtc (when provided)

#### CancelEventCommand
**Handler:** `CancelEventCommandHandler`
```
Input:  EventId
Output: Result
```
1. Fetches Event
2. Calls `event.Cancel(utcNow)` -- validates not already canceled and not started
3. Saves changes

#### CreateCategoryCommand
**Handler:** `CreateCategoryCommandHandler`
```
Input:  Name
Output: Result<Guid>
```
Creates and inserts a new Category.

#### UpdateCategoryCommand
**Handler:** `UpdateCategoryCommandHandler`
```
Input:  CategoryId, Name
Output: Result
```
Fetches category, calls `ChangeName(name)`, saves.

**Validator:** CategoryId NotEmpty, Name NotEmpty

#### ArchiveCategoryCommand
**Handler:** `ArchiveCategoryCommandHandler`
```
Input:  CategoryId
Output: Result
```
Fetches category, calls `Archive()`, saves.

**Validator:** CategoryId NotEmpty

#### CreateTicketTypeCommand
**Handler:** `CreateTicketTypeCommandHandler`
```
Input:  EventId, Name, Price, Currency, Quantity
Output: Result<Guid>
```
1. Fetches Event (error if not found)
2. Creates TicketType via factory
3. Inserts and saves

**Validator:** All fields NotEmpty

#### UpdateTicketTypePriceCommand
**Handler:** `UpdateTicketTypePriceCommandHandler`
```
Input:  TicketTypeId, Price
Output: Result
```
Fetches ticket type, calls `UpdatePrice(price)`, saves.

**Validator:** TicketTypeId NotEmpty, Price NotEmpty

### 3.2 Queries

#### GetEventQuery
```
Input:  EventId (Guid)
Output: Result<EventResponse>
```
**Implementation:** Dapper SQL with LEFT JOIN on `ticket_types` table. Returns event with nested TicketTypeResponse list.

#### GetEventsQuery
```
Input:  (none)
Output: Result<IReadOnlyCollection<EventResponse>>
```
**Implementation:** Dapper SQL returning all events.

#### SearchEventsQuery
```
Input:  CategoryId?, StartDate?, EndDate?, Page, PageSize
Output: Result<SearchEventsResponse>
```
**Implementation:** Dynamic SQL with optional WHERE clauses. Supports pagination.

#### GetCategoryQuery / GetCategoriesQuery
```
Input:  CategoryId (or none for all)
Output: CategoryResponse (Id, Name, IsArchived)
```

#### GetTicketTypeQuery / GetTicketTypesQuery
```
Input:  TicketTypeId (or EventId for all)
Output: TicketTypeResponse (Id, EventId, Name, Price, Currency, Quantity)
```

### 3.3 Domain Event Handlers (Outbox -> Integration Event Publishers)

#### EventPublishedDomainEventHandler
1. Queries full event details via `GetEventQuery` (includes ticket types)
2. Publishes `EventPublishedIntegrationEvent` with:
   - EventId, Title, Description, Location, StartsAtUtc, EndsAtUtc
   - List of TicketTypeModel (Id, EventId, Name, Price, Currency, Quantity)
3. **Consumed by:** Ticketing (creates local event + ticket types), Attendance (creates local event)

#### EventCanceledDomainEventHandler
1. Publishes `EventCanceledIntegrationEvent(EventId)`
2. **Consumed by:** CancelEventSaga (orchestrates cross-module cleanup)

#### EventRescheduledDomainEventHandler
1. Publishes `EventRescheduledIntegrationEvent(EventId, StartsAtUtc, EndsAtUtc)`
2. **Consumed by:** Ticketing, Attendance (update local event copies)

#### TicketTypePriceChangedDomainEventHandler
1. Publishes `TicketTypePriceChangedIntegrationEvent(TicketTypeId, Price)`
2. **Consumed by:** Ticketing (updates local ticket type price)

---

## 4. Infrastructure Layer

### 4.1 Database Context
**EventsDbContext** -- EF Core DbContext with schema `events`

**Tables:**
- `events.events` -- Event aggregate
- `events.categories` -- Category aggregate
- `events.ticket_types` -- TicketType entity
- `events.outbox_messages` -- Outbox for reliable event publishing
- `events.inbox_messages` -- Inbox for reliable event consumption
- `events.outbox_message_consumers` -- Idempotent handler tracking
- `events.inbox_message_consumers` -- Idempotent handler tracking
- `events.cancel_event_states` -- MassTransit saga state persistence

**Conventions:** snake_case naming via EF Core convention.

### 4.2 Repositories
Standard EF Core implementations:
- `EventRepository` -- `dbContext.Events.SingleOrDefaultAsync()`
- `CategoryRepository`
- `TicketTypeRepository` -- includes `ExistsAsync` using `AnyAsync()`

### 4.3 Outbox/Inbox
- `ProcessOutboxJob` -- Quartz scheduled job that processes `outbox_messages`
- `ProcessInboxJob` -- Quartz scheduled job that processes `inbox_messages`
- `IdempotentDomainEventHandler<T>` -- Decorator ensuring at-most-once handler execution
- `IdempotentIntegrationEventHandler<T>` -- Same for integration events

### 4.4 Module Registration
**EventsModule.cs** -- Implements `IModule` interface:
- Registers DbContext, repositories, and Dapper data sources
- Configures MassTransit consumers (CancelEventSaga)
- Registers Quartz jobs for outbox/inbox processing

---

## 5. Presentation Layer (API Endpoints)

### Events

| Method | Route | Permission | Handler |
|--------|-------|------------|---------|
| POST | `/events` | ModifyEvents | CreateEventCommand |
| GET | `/events/{id}` | GetEvents | GetEventQuery |
| GET | `/events` | GetEvents | GetEventsQuery |
| GET | `/events/search` | SearchEvents | SearchEventsQuery |
| PUT | `/events/{id}/publish` | ModifyEvents | PublishEventCommand |
| PUT | `/events/{id}/reschedule` | ModifyEvents | RescheduleEventCommand |
| DELETE | `/events/{id}/cancel` | ModifyEvents | CancelEventCommand |

### Categories

| Method | Route | Permission | Handler |
|--------|-------|------------|---------|
| POST | `/categories` | ModifyCategories | CreateCategoryCommand |
| GET | `/categories` | GetCategories | GetCategoriesQuery |
| GET | `/categories/{id}` | GetCategories | GetCategoryQuery |
| PUT | `/categories/{id}` | ModifyCategories | UpdateCategoryCommand |
| PUT | `/categories/{id}/archive` | ModifyCategories | ArchiveCategoryCommand |

### Ticket Types

| Method | Route | Permission | Handler |
|--------|-------|------------|---------|
| POST | `/ticket-types` | ModifyTicketTypes | CreateTicketTypeCommand |
| GET | `/ticket-types?eventId=` | GetTicketTypes | GetTicketTypesQuery |
| GET | `/ticket-types/{id}` | GetTicketTypes | GetTicketTypeQuery |
| PUT | `/ticket-types/{id}/price` | ModifyTicketTypes | UpdateTicketTypePriceCommand |

**Total: 16 endpoints**

---

## 6. Integration Events (Public Contracts)

**Namespace:** `Evently.Modules.Events.IntegrationEvents`

These are the **only types** other modules can reference from the Events module.

| Event | Properties |
|-------|-----------|
| EventPublishedIntegrationEvent | Id, OccurredOnUtc, EventId, Title, Description, Location, StartsAtUtc, EndsAtUtc, TicketTypes (list) |
| EventCanceledIntegrationEvent | Id, OccurredOnUtc, EventId |
| EventCancellationStartedIntegrationEvent | Id, OccurredOnUtc, EventId |
| EventCancellationCompletedIntegrationEvent | Id, OccurredOnUtc, EventId |
| EventRescheduledIntegrationEvent | Id, OccurredOnUtc, EventId, StartsAtUtc, EndsAtUtc |
| TicketTypePriceChangedIntegrationEvent | Id, OccurredOnUtc, TicketTypeId, Price |

---

## 7. CancelEventSaga (MassTransit State Machine)

**File:** `Presentation/Events/CancelEventSaga/CancelEventSaga.cs`

The event cancellation process spans multiple modules and is orchestrated by a saga (state machine) hosted in the Events module.

### States
- `CancellationStarted` -- Waiting for downstream modules to complete cleanup
- `PaymentsRefunded` -- Ticketing has refunded all payments
- `TicketsArchived` -- Ticketing has archived all tickets

### Flow
```
EventCanceledIntegrationEvent
    |
    v
[Initial] --> Publish EventCancellationStartedIntegrationEvent
    |          --> Transition to CancellationStarted
    v
[CancellationStarted]
    |-- Receives EventPaymentsRefundedIntegrationEvent --> PaymentsRefunded
    |-- Receives EventTicketsArchivedIntegrationEvent  --> TicketsArchived
    v
[PaymentsRefunded OR TicketsArchived]
    |-- Receives the OTHER event (composite event: both must arrive)
    |
    v
Publish EventCancellationCompletedIntegrationEvent
    |
    v
[Finalized] -- Saga complete, state deleted
```

### State Persistence
- **Table:** `events.cancel_event_states`
- **Correlation:** By `EventId`
- **Configuration:** `CancelEventStateConfiguration.cs` (EF Core entity type configuration)
