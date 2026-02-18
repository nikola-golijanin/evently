# Evently - Domain Documentation

## Table of Contents

- [1. Business Overview](#1-business-overview)
- [2. Domain Model](#2-domain-model)
  - [2.1 Events Module (The Catalog)](#21-events-module-the-catalog)
  - [2.2 Users Module (Identity & Authorization)](#22-users-module-identity--authorization)
  - [2.3 Ticketing Module (Commerce)](#23-ticketing-module-commerce)
  - [2.4 Attendance Module (Operations)](#24-attendance-module-operations)
- [3. Data Ownership & Module Boundaries](#3-data-ownership--module-boundaries)
- [4. Domain Events Summary](#4-domain-events-summary)
- [5. Key Business Flows](#5-key-business-flows)
- [6. Glossary](#6-glossary)

---

## 1. Business Overview

Evently is an **event management and ticketing platform** that allows organizers to create, publish, and manage events while enabling customers to browse events, purchase tickets, and check in at venues. The system handles the full lifecycle from event creation through attendance tracking.

### Core Business Capabilities

| Capability | Description |
|------------|-------------|
| **Event Management** | Create, publish, reschedule, and cancel events with categories and ticket tiers |
| **User Management** | Registration, authentication, profile management with role-based permissions |
| **Ticketing** | Shopping cart, order processing, payment handling, ticket issuance with unique codes |
| **Attendance** | Ticket check-in at events, duplicate/invalid detection, real-time statistics |

---

## 2. Domain Model

The domain is decomposed into **four bounded contexts** (modules), each owning its data and business rules. Modules communicate exclusively through integration events -- they never share database tables or reference each other's internal types.

### 2.1 Events Module (The Catalog)

This is the **source of truth** for event definitions. It manages the event lifecycle and ticket type catalog.

#### Aggregates

**Event** -- The central aggregate representing a scheduled happening.
- Created in `Draft` status
- Must have at least one TicketType before it can be published
- Follows a strict state machine: `Draft -> Published -> Completed` or `Draft/Published -> Canceled`
- Cannot be canceled after it has started
- Rescheduling updates start/end times and notifies downstream modules

**Category** -- Organizational grouping for events.
- Can be archived (soft-deleted) but not hard-deleted
- Name changes propagate as domain events

**TicketType** -- A pricing tier within an event (e.g., "VIP", "General Admission").
- Belongs to exactly one Event
- Defines name, price, currency, and total quantity available
- Price changes propagate to the Ticketing module

#### Key Business Rules
1. An event's end date must be after its start date
2. Events cannot be published without at least one ticket type
3. Only draft events can be published
4. Events cannot be canceled after they've started
5. Categories can be archived but events referencing them remain valid

#### State Machine: Event Lifecycle

```
                    +-----------+
         Create --> |   Draft   |
                    +-----------+
                         |
                    Publish (requires ticket types)
                         |
                         v
                    +-----------+
                    | Published |
                    +-----------+
                         |
                    Completes naturally
                         |
                         v
                    +-----------+
                    | Completed |
                    +-----------+

    (From Draft or Published, before start time)
                         |
                       Cancel
                         |
                         v
                    +-----------+
                    | Canceled  | --> Triggers saga: refund payments, archive tickets
                    +-----------+
```

---

### 2.2 Users Module (Identity & Authorization)

Manages user identity, authentication, and the permission model.

#### Aggregates

**User** -- Represents a registered person in the system.
- Links to an external identity provider (Keycloak) via `IdentityId`
- Assigned the `Member` role by default upon registration
- Profile updates propagate to downstream modules (Ticketing, Attendance)

**Role** (Value Object) -- Named authorization role.
- `Administrator` -- Full access
- `Member` -- Standard access (default)

**Permission** (Value Object) -- Granular permission codes used for endpoint authorization.
- 17 defined permissions covering all CRUD operations across modules
- Mapped to roles via a join table

#### Key Business Rules
1. Every new user gets the `Member` role automatically
2. Email must be in valid format, password minimum 6 characters
3. Registration creates the user both in Keycloak and in the local database
4. Profile updates notify all downstream modules to keep their local copies in sync

#### Permission Model

```
Role: Administrator
  - All 17 permissions

Role: Member
  - users:read, users:update
  - events:read, events:search, events:update
  - categories:read, categories:update
  - ticket-types:read, ticket-types:update
  - carts:read, carts:add, carts:remove
  - orders:read, orders:create
  - tickets:read, tickets:check-in
  - event-statistics:read
```

---

### 2.3 Ticketing Module (Commerce)

Handles the entire purchase flow: cart management, order creation, payment processing, and ticket issuance.

#### Aggregates

**Customer** -- Local representation of a User within the ticketing context.
- Created automatically when a user registers (via integration event)
- Profile stays in sync via `UserProfileUpdatedIntegrationEvent`

**Order** -- A purchase transaction.
- Created from cart items
- Status: `Pending -> Paid -> Refunded` or `Pending -> Canceled`
- Contains OrderItems (line items)
- Triggers ticket generation after creation
- State transitions: `Pay()` (Pending->Paid), `Refund()` (Paid->Refunded), `Cancel()` (Pending->Canceled)
- Both `Refund()` and `Cancel()` are idempotent (no-op if already in terminal state)

**OrderItem** (Value Object) -- A line item within an order.
- Links to a TicketType
- Tracks quantity, unit price, total price, and currency

**Payment** -- Records a financial transaction.
- Linked 1:1 to an Order
- Supports partial refunds (tracks `AmountRefunded`)
- Full refund vs partial refund raises different domain events

**Ticket** -- An individual admission pass.
- Generated with a unique ULID-based code (`tc_{Ulid}`)
- Can be archived (soft-deleted) when event is canceled
- One ticket per attendee per ticket type unit

**Event** (Local Copy) -- Mirror of the Events module's Event.
- Created when an event is published
- Tracks cancellation status locally
- Used to validate ticket operations without cross-module calls

**TicketType** (Local Copy) -- Mirror of Events module's TicketType with availability tracking.
- Tracks `AvailableQuantity` (decremented on purchase)
- Uses pessimistic locking (`FOR UPDATE`) during order creation to prevent overselling
- Raises `SoldOut` event when quantity reaches zero

#### Key Business Rules
1. Cart is stored in Redis (session-based, not persisted to database)
2. Order creation is wrapped in a database transaction with pessimistic locking
3. Available quantity is checked and decremented atomically (prevents overselling)
4. Each ticket gets a unique ULID-based code for scanning
5. Event cancellation triggers bulk refund of all payments, refund of all orders, and archival of all tickets
6. Tickets cannot be issued twice for the same order
7. Only pending orders can be paid (`Pending -> Paid`)
8. Only paid orders can be refunded (`Paid -> Refunded`); idempotent if already refunded
9. Only pending orders can be canceled (`Pending -> Canceled`); idempotent if already canceled

#### Order Creation Flow (Critical Business Process)

```
Customer adds items to Cart (Redis)
         |
         v
CreateOrderCommand
         |
    BEGIN TRANSACTION
         |
    +----+----+
    | For each cart item:
    |   1. Lock TicketType row (FOR UPDATE)
    |   2. Validate available quantity
    |   3. Decrement available quantity
    |   4. Add OrderItem to Order
    +----+----+
         |
    Insert Order
         |
    Charge Payment (external gateway)
         |
    Insert Payment
         |
    COMMIT TRANSACTION
         |
    Clear Cart (Redis)
         |
    Domain Events fire:
      - OrderCreatedDomainEvent -> publishes integration event
      - CreateTicketBatchCommand -> generates individual Ticket records
```

---

### 2.4 Attendance Module (Operations)

Manages day-of-event operations: check-in and statistics.

#### Aggregates

**Attendee** -- Local representation of a User within the attendance context.
- Created automatically when a user registers
- Owns the check-in business logic

**Ticket** (Local Copy) -- Mirror of a Ticketing ticket.
- Tracks `UsedAtUtc` to indicate check-in
- Created when tickets are issued in the Ticketing module

**Event** (Local Copy) -- Mirror of the Events module's Event.
- Created when an event is published

**EventStatistics** (Read Model / Projection) -- Denormalized view for real-time dashboards.
- Tracks: tickets sold, attendees checked in, duplicate check-in codes, invalid check-in codes
- Updated by domain event handlers (CQRS projection pattern)

#### Key Business Rules
1. A ticket can only be checked in once (duplicate check-in raises a distinct domain event)
2. A ticket must belong to the attendee attempting check-in (invalid check-in raises a distinct domain event)
3. Check-in failures are tracked for security/analytics (duplicate and invalid attempts logged)
4. Event statistics are a **projection** -- updated asynchronously via domain event handlers

#### Check-In Flow

```
Attendee presents ticket
         |
         v
CheckInAttendeeCommand (attendeeId, ticketId)
         |
    Fetch Attendee
    Fetch Ticket
         |
    attendee.CheckIn(ticket)
         |
    +----+----+----+
    |              |              |
 Success     DuplicateCheckIn   InvalidCheckIn
    |              |              |
 TicketUsed   DuplicateCheckIn  InvalidCheckIn
 DomainEvent  AttemptedEvent    AttemptedEvent
    |              |              |
    +----+---------+----+---------+
         |
    Update EventStatistics projection
```

---

## 3. Data Ownership & Module Boundaries

Each module owns its database schema. Where the same real-world concept appears in multiple modules, each module keeps its own local copy, synchronized via integration events.

```
+------------------+    +------------------+    +------------------+    +------------------+
|  Events Module   |    |  Users Module    |    | Ticketing Module |    | Attendance Module|
|  Schema: events  |    |  Schema: users   |    | Schema: ticketing|    | Schema: attendance|
+------------------+    +------------------+    +------------------+    +------------------+
| - events         |    | - users          |    | - customers      |    | - attendees      |
| - categories     |    | - roles          |    | - orders         |    | - tickets        |
| - ticket_types   |    | - permissions    |    | - order_items    |    | - events         |
| - outbox_messages|    | - role_permissions|   | - payments       |    | - event_statistics|
| - inbox_messages |    | - user_roles     |    | - tickets        |    | - outbox_messages|
|                  |    | - outbox_messages|    | - events         |    | - inbox_messages |
|                  |    | - inbox_messages |    | - ticket_types   |    |                  |
|                  |    |                  |    | - outbox_messages|    |                  |
|                  |    |                  |    | - inbox_messages |    |                  |
+------------------+    +------------------+    +------------------+    +------------------+
```

### Why Local Copies?

The modular monolith pattern requires **data isolation** -- each module cannot query another module's database tables. This means:
- When a **User registers**, the Ticketing module creates a **Customer** and the Attendance module creates an **Attendee** -- both copies of the same person
- When an **Event is published**, both Ticketing and Attendance create local Event records
- When a **Ticket is issued**, Attendance creates its own Ticket record for check-in tracking

This ensures modules can evolve independently and could be extracted into separate microservices in the future.

---

## 4. Domain Events Summary

### Events Module Publishes
| Event | Trigger | Consumers |
|-------|---------|-----------|
| EventPublishedIntegrationEvent | Event published | Ticketing, Attendance |
| EventCanceledIntegrationEvent | Event canceled | CancelEventSaga |
| EventCancellationStartedIntegrationEvent | Saga started | Ticketing, Attendance |
| EventRescheduledIntegrationEvent | Event rescheduled | Ticketing, Attendance |
| TicketTypePriceChangedIntegrationEvent | Price updated | Ticketing |

### Users Module Publishes
| Event | Trigger | Consumers |
|-------|---------|-----------|
| UserRegisteredIntegrationEvent | User registered | Ticketing, Attendance |
| UserProfileUpdatedIntegrationEvent | Profile updated | Ticketing, Attendance |

### Ticketing Module Publishes
| Event | Trigger | Consumers |
|-------|---------|-----------|
| OrderCreatedIntegrationEvent | Order placed | -- |
| TicketIssuedIntegrationEvent | Tickets generated | Attendance |
| EventPaymentsRefundedIntegrationEvent | Refunds completed | CancelEventSaga |
| EventTicketsArchivedIntegrationEvent | Tickets archived | CancelEventSaga |
| TicketTypeSoldOutIntegrationEvent | Quantity exhausted | -- |

### Attendance Module Publishes
- No outgoing integration events (leaf module)

---

## 5. Key Business Flows

### Flow 1: Organizer Creates and Publishes an Event
1. Organizer creates a Category (if needed)
2. Organizer creates an Event (Draft status)
3. Organizer creates one or more TicketTypes for the event
4. Organizer publishes the event
5. Integration event fires -> Ticketing and Attendance create local copies

### Flow 2: Customer Purchases Tickets
1. Customer browses/searches events
2. Customer adds ticket types to cart (Redis)
3. Customer creates order from cart
4. System locks ticket type quantities, charges payment
5. Individual tickets generated with unique codes
6. Integration event fires -> Attendance creates ticket records

### Flow 3: Attendee Checks In
1. Attendee presents ticket (by ID)
2. System validates ticket belongs to attendee
3. System checks ticket hasn't been used
4. Ticket marked as used, statistics updated
5. Duplicate/invalid attempts tracked for security

### Flow 4: Event Cancellation (Saga)
1. Organizer cancels event
2. Saga orchestrator starts
3. Notifies Ticketing -> refunds all payments, refunds all orders (status updated to Refunded), archives all tickets
4. Notifies Attendance -> cleans up records
5. Saga waits for both confirmations (composite event)
6. Saga completes -> publishes completion event

---

## 6. Glossary

| Term | Definition |
|------|-----------|
| **Event** | A scheduled happening that people can attend (conference, concert, meetup) |
| **Category** | A classification for events (e.g., Music, Technology, Sports) |
| **TicketType** | A pricing tier for an event (e.g., VIP, General Admission, Early Bird) |
| **Customer** | A user in the context of purchasing tickets |
| **Attendee** | A user in the context of attending/checking in at events |
| **Order** | A purchase transaction containing one or more ticket types |
| **Payment** | A financial transaction associated with an order |
| **Ticket** | An individual admission pass with a unique scannable code |
| **Cart** | A temporary collection of desired ticket types (stored in Redis) |
| **Check-In** | The act of presenting a ticket at an event venue |
| **EventStatistics** | Real-time aggregated data about an event's attendance |
| **Saga** | An orchestrated multi-step process spanning multiple modules |
