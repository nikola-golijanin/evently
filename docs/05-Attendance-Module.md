# Attendance Module - Technical Documentation

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
  - [3.3 Domain Event Handlers (Read Model Projections)](#33-domain-event-handlers-read-model-projections)
- [4. Infrastructure Layer](#4-infrastructure-layer)
- [5. Presentation Layer (API Endpoints)](#5-presentation-layer-api-endpoints)
- [6. Integration Event Handlers (Incoming)](#6-integration-event-handlers-incoming)
- [7. CQRS Projection Architecture](#7-cqrs-projection-architecture)
- [8. Check-In Flow (Complete End-to-End)](#8-check-in-flow-complete-end-to-end)

---

## Overview

The Attendance module handles **day-of-event operations**: check-in validation, duplicate/invalid detection, and real-time attendance statistics. It is a **leaf module** -- it consumes integration events from other modules but does not publish any integration events of its own.

This module demonstrates the **CQRS projection pattern** where domain events update a denormalized read model (`EventStatistics`) for fast querying.

**Namespace:** `Evently.Modules.Attendance`
**Database Schema:** `attendance`

---

## 1. Layer Structure

```
Evently.Modules.Attendance.Domain/              -- Entities, aggregates, domain events, errors
Evently.Modules.Attendance.Application/         -- Commands, queries, handlers, validators
Evently.Modules.Attendance.Infrastructure/      -- EF Core, repositories, module config
Evently.Modules.Attendance.Presentation/        -- Minimal API endpoints, integration event handlers
```

Note: The Attendance module does **not** have an IntegrationEvents project because it does not publish any integration events.

---

## 2. Domain Layer

### 2.1 Entities & Aggregates

#### Attendee (Aggregate Root)
**File:** `Domain/Attendees/Attendee.cs`

Local copy of a User, created when `UserRegisteredIntegrationEvent` is received.

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Same as User.Id |
| Email | string | Attendee email |
| FirstName | string | First name |
| LastName | string | Last name |

**Methods:**
- `static Create(id, email, firstName, lastName)` -- Factory.
- `Update(firstName, lastName)` -- Updates profile.
- `CheckIn(ticket)` -- **Core business logic.** Validates and processes check-in:
  1. Validates ticket belongs to this attendee (`ticket.AttendeeId == Id`)
     - If not: raises `InvalidCheckInAttemptedDomainEvent`, returns failure
  2. Checks ticket hasn't been used (`ticket.UsedAtUtc == null`)
     - If used: raises `DuplicateCheckInAttemptedDomainEvent`, returns failure
  3. Calls `ticket.MarkAsUsed()` -- sets `UsedAtUtc` to current UTC time
  4. Raises `AttendeeCheckedInDomainEvent`
  5. Returns success

#### Ticket
**File:** `Domain/Tickets/Ticket.cs`

Local copy of a Ticketing module ticket, created when `TicketIssuedIntegrationEvent` is received.

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Same as Ticketing.Ticket.Id |
| AttendeeId | Guid | FK to Attendee |
| EventId | Guid | FK to Event |
| Code | string | Unique ticket code (from Ticketing) |
| UsedAtUtc | DateTime? | When the ticket was checked in (null = unused) |

**Methods:**
- `static Create(ticketId, attendee, event, code)` -- Factory. Raises `TicketCreatedDomainEvent`.
- `MarkAsUsed()` -- Internal method called by `Attendee.CheckIn()`. Sets `UsedAtUtc = DateTime.UtcNow`.

#### Event (Local Copy)
**File:** `Domain/Events/Event.cs`

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Same as Events.Event.Id |
| Title | string | Event title |
| Description | string | Event description |
| Location | string | Venue |
| StartsAtUtc | DateTime | Start time |
| EndsAtUtc | DateTime? | End time |

**Methods:**
- `static Create(id, title, description, location, startsAtUtc, endsAtUtc)` -- Factory.

#### EventStatistics (Read Model / Projection)
**File:** `Domain/Events/EventStatistics.cs`

Denormalized view of attendance data, updated asynchronously by domain event handlers.

| Property | Type | Description |
|----------|------|-------------|
| EventId | Guid | PK, same as Event.Id |
| Title | string | Event title |
| Description | string | Event description |
| Location | string | Venue |
| StartsAtUtc | DateTime | Start time |
| EndsAtUtc | DateTime? | End time |
| TicketsSold | int | Total tickets issued for this event |
| AttendeesCheckedIn | int | Total successful check-ins |
| DuplicateCheckInTickets | List\<string\> | Ticket codes checked in more than once |
| InvalidCheckInTickets | List\<string\> | Ticket codes with invalid check-in attempts |

**Methods:**
- `static Create(...)` -- Factory. Initializes counters to 0, lists to empty.

### 2.2 Domain Events

| Event | Properties | When Raised |
|-------|-----------|-------------|
| AttendeeCheckedInDomainEvent | AttendeeId, EventId | Attendee.CheckIn() success |
| DuplicateCheckInAttemptedDomainEvent | AttendeeId, EventId, TicketId, TicketCode | Attendee.CheckIn() -- ticket already used |
| InvalidCheckInAttemptedDomainEvent | AttendeeId, EventId, TicketId, TicketCode | Attendee.CheckIn() -- wrong attendee |
| TicketCreatedDomainEvent | TicketId, EventId | Ticket.Create() |
| TicketUsedDomainEvent | TicketId | Ticket.MarkAsUsed() |
| EventCreatedDomainEvent | EventId, Title, Description, Location, StartsAtUtc, EndsAtUtc | Event.Create() |

### 2.3 Repository Interfaces

**IAttendeeRepository** (File named `ICustomerRepository.cs`)
- `Task<Attendee?> GetAsync(Guid id, CancellationToken ct)`
- `void Insert(Attendee attendee)`

**ITicketRepository**
- `Task<Ticket?> GetAsync(Guid id, CancellationToken ct)`
- `void Insert(Ticket ticket)`

**IEventRepository**
- `Task<Event?> GetAsync(Guid id, CancellationToken ct)`
- `void Insert(Event event)`

### 2.4 Domain Errors

**AttendeeErrors:**
- `NotFound(Guid attendeeId)` -- Attendee not found

**TicketErrors:**
- `NotFound` -- Ticket not found
- `InvalidCheckIn` -- Ticket doesn't belong to the attendee
- `DuplicateCheckIn` -- Ticket already used

**EventErrors:**
- `NotFound(Guid eventId)` -- Event not found

---

## 3. Application Layer

### 3.1 Commands

#### CheckInAttendeeCommand (Core Business Command)
**Handler:** `CheckInAttendeeCommandHandler`
```
Input:  AttendeeId, TicketId
Output: Result
```
1. Fetches Attendee (error if not found)
2. Fetches Ticket (error if not found)
3. Calls `attendee.CheckIn(ticket)` -- delegates to domain aggregate
4. Result may be:
   - **Success:** Ticket marked as used, `AttendeeCheckedInDomainEvent` raised
   - **InvalidCheckIn:** Wrong attendee, `InvalidCheckInAttemptedDomainEvent` raised
   - **DuplicateCheckIn:** Already used, `DuplicateCheckInAttemptedDomainEvent` raised
5. Logs warnings for failures
6. Saves changes

**Validator:**
- AttendeeId: NotEmpty
- TicketId: NotEmpty

#### CreateAttendeeCommand (Integration Event Handler)
```
Input:  AttendeeId, Email, FirstName, LastName
Output: Result
```
Creates local Attendee. Triggered by `UserRegisteredIntegrationEvent`.

#### UpdateAttendeeCommand (Integration Event Handler)
```
Input:  AttendeeId, FirstName, LastName
Output: Result
```
Updates local Attendee. Triggered by `UserProfileUpdatedIntegrationEvent`.

#### CreateEventCommand (Integration Event Handler)
```
Input:  EventId, Title, Description, Location, StartsAtUtc, EndsAtUtc
Output: Result
```
Creates local Event. Triggered by `EventPublishedIntegrationEvent`.

#### CreateTicketCommand (Integration Event Handler)
```
Input:  TicketId, AttendeeId, EventId, Code
Output: Result
```
1. Fetches Attendee
2. Fetches Event
3. Creates `Ticket.Create(ticketId, attendee, event, code)`
4. Inserts and saves

Triggered by `TicketIssuedIntegrationEvent` from Ticketing.

### 3.2 Queries

#### GetEventStatisticsQuery
```
Input:  EventId (Guid)
Output: EventStatisticsResponse
```
**Implementation:** Dapper query from the `attendance.event_statistics` table.

**Response DTO:**
```
EventStatisticsResponse {
    EventId: Guid
    Title: string
    Description: string
    Location: string
    StartsAtUtc: DateTime
    EndsAtUtc: DateTime?
    TicketsSold: int
    AttendeesCheckedIn: int
    DuplicateCheckInTickets: string[]
    InvalidCheckInTickets: string[]
}
```

### 3.3 Domain Event Handlers (Read Model Projections)

These handlers maintain the `EventStatistics` read model. They are the core of the CQRS projection pattern in this module.

#### EventCreatedDomainEventHandler
**Triggered by:** `EventCreatedDomainEvent` (when event is created locally)
**Action:** INSERT into `attendance.event_statistics` with:
- Event details (title, description, location, dates)
- `tickets_sold = 0`
- `attendees_checked_in = 0`
- Empty arrays for duplicate/invalid tickets

#### TicketCreatedDomainEventHandler
**Triggered by:** `TicketCreatedDomainEvent` (when ticket is created locally)
**Action:** UPDATE `attendance.event_statistics` SET `tickets_sold = tickets_sold + 1`

#### AttendeeCheckedInDomainEventHandler
**Triggered by:** `AttendeeCheckedInDomainEvent`
**Action:** UPDATE `attendance.event_statistics` by recounting tickets with `used_at_utc IS NOT NULL`

#### DuplicateCheckInAttemptedDomainEventHandler
**Triggered by:** `DuplicateCheckInAttemptedDomainEvent`
**Action:** Appends ticket code to `duplicate_check_in_tickets` array in `event_statistics`

#### InvalidCheckInAttemptedDomainEventHandler
**Triggered by:** `InvalidCheckInAttemptedDomainEvent`
**Action:** Appends ticket code to `invalid_check_in_tickets` array in `event_statistics`

---

## 4. Infrastructure Layer

### 4.1 Database Context
**AttendanceDbContext** -- EF Core DbContext with schema `attendance`

**Tables:**
- `attendance.attendees` -- Attendee aggregate
- `attendance.tickets` -- Ticket entity (with UsedAtUtc)
- `attendance.events` -- Local Event copy
- `attendance.event_statistics` -- Read model projection
- `attendance.outbox_messages`
- `attendance.inbox_messages`
- `attendance.outbox_message_consumers`
- `attendance.inbox_message_consumers`

### 4.2 Attendance Context
**IAttendanceContext** -- Resolves the current attendee from HTTP context.
- Extracts user ID from bearer token claims
- Used by check-in endpoint to know which attendee is checking in

### 4.3 Repositories
Standard EF Core implementations for all repository interfaces.

### 4.4 Module Registration
**AttendanceModule.cs** -- Implements `IModule`:
- Registers DbContext, repositories, and Dapper data sources
- Configures MassTransit consumers:
  - `IntegrationEventConsumer<UserRegisteredIntegrationEvent>`
  - `IntegrationEventConsumer<UserProfileUpdatedIntegrationEvent>`
  - `IntegrationEventConsumer<EventPublishedIntegrationEvent>`
  - `IntegrationEventConsumer<TicketIssuedIntegrationEvent>`
  - `IntegrationEventConsumer<EventCancellationStartedIntegrationEvent>`

---

## 5. Presentation Layer (API Endpoints)

| Method | Route | Permission | Handler |
|--------|-------|------------|---------|
| GET | `/event-statistics/{id}` | GetEventStatistics | GetEventStatisticsQuery |
| PUT | `/attendees/check-in` | CheckInTicket | CheckInAttendeeCommand |

**Total: 2 endpoints**

---

## 6. Integration Event Handlers (Incoming)

| From Module | Event | Handler Action |
|-------------|-------|---------------|
| Users | UserRegisteredIntegrationEvent | CreateAttendeeCommand |
| Users | UserProfileUpdatedIntegrationEvent | UpdateAttendeeCommand |
| Events | EventPublishedIntegrationEvent | CreateEventCommand |
| Ticketing | TicketIssuedIntegrationEvent | CreateTicketCommand |
| Events | EventCancellationStartedIntegrationEvent | Cleanup (TBD) |

---

## 7. CQRS Projection Architecture

The Attendance module demonstrates a clean CQRS read model pattern:

```
                    WRITE SIDE                          READ SIDE
                    ----------                          ---------

            +-------------------+
            | CheckInAttendee   |
            | Command           |
            +-------------------+
                    |
                    v
            +-------------------+
            | Attendee.CheckIn()|
            | (Domain Logic)    |
            +-------------------+
                    |
            Raises Domain Events
                    |
        +-----------+-----------+
        |           |           |
        v           v           v
  AttendeeChecked  Duplicate   Invalid
  InDomainEvent    CheckIn     CheckIn
        |           |           |
        v           v           v
  +-----+-----+  +-+--------+  +--------+
  | Update     |  | Append   |  | Append |     +-------------------+
  | checked_in |  | to dup   |  | to inv |---->| event_statistics   |
  | count      |  | array    |  | array  |     | (Read Model Table) |
  +-----+------+  +----------+  +--------+     +-------------------+
                                                         |
                                                         v
                                                +-------------------+
                                                | GET /event-       |
                                                | statistics/{id}   |
                                                | (Dapper Query)    |
                                                +-------------------+

Data Flow for Ticket Creation:

  TicketIssuedIntegrationEvent (from Ticketing)
        |
        v
  CreateTicketCommand
        |
        v
  Ticket.Create() -> TicketCreatedDomainEvent
        |
        v
  TicketCreatedDomainEventHandler
        |
        v
  UPDATE event_statistics SET tickets_sold = tickets_sold + 1
```

### Benefits of this Approach
1. **Fast reads:** Statistics are pre-computed, no joins needed at query time
2. **Separation:** Write model (entities) and read model (statistics) evolve independently
3. **Auditability:** Domain events provide a full audit trail of every check-in attempt
4. **Security:** Duplicate and invalid attempts are tracked for fraud detection

---

## 8. Check-In Flow (Complete End-to-End)

```
1. Attendee arrives at venue with ticket (physical or digital)

2. PUT /attendees/check-in { ticketId }
   - AttendeeId extracted from bearer token via IAttendanceContext

3. CheckInAttendeeCommandHandler:
   a. Fetch Attendee (error if not registered)
   b. Fetch Ticket (error if ticket doesn't exist)
   c. attendee.CheckIn(ticket)

4. Domain Logic (Attendee.CheckIn):

   IF ticket.AttendeeId != this.Id:
       Raise InvalidCheckInAttemptedDomainEvent
       RETURN Result.Failure(TicketErrors.InvalidCheckIn)

   IF ticket.UsedAtUtc != null:
       Raise DuplicateCheckInAttemptedDomainEvent
       RETURN Result.Failure(TicketErrors.DuplicateCheckIn)

   ticket.MarkAsUsed()  // sets UsedAtUtc = DateTime.UtcNow
   Raise AttendeeCheckedInDomainEvent
   RETURN Result.Success()

5. SaveChangesAsync():
   - Ticket.UsedAtUtc persisted
   - Domain events captured in outbox

6. ProcessOutboxJob (async):
   - AttendeeCheckedInDomainEventHandler:
     UPDATE event_statistics SET attendees_checked_in = (
       SELECT COUNT(*) FROM tickets
       WHERE event_id = @eventId AND used_at_utc IS NOT NULL
     )

   OR (on failure):

   - DuplicateCheckInAttemptedDomainEventHandler:
     UPDATE event_statistics
     SET duplicate_check_in_tickets = array_append(duplicate_check_in_tickets, @code)

   - InvalidCheckInAttemptedDomainEventHandler:
     UPDATE event_statistics
     SET invalid_check_in_tickets = array_append(invalid_check_in_tickets, @code)
```
