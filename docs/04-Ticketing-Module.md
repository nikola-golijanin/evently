# Ticketing Module - Technical Documentation

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
  - [3.3 Domain Event Handlers](#33-domain-event-handlers)
- [4. Infrastructure Layer](#4-infrastructure-layer)
- [5. Presentation Layer (API Endpoints)](#5-presentation-layer-api-endpoints)
- [6. Integration Events (Public Contracts)](#6-integration-events-public-contracts)
- [7. Integration Event Handlers (Incoming)](#7-integration-event-handlers-incoming)
- [8. Critical Flow: Order Creation (Detailed)](#8-critical-flow-order-creation-detailed)

---

## Overview

The Ticketing module is the **commerce engine** of Evently. It handles the full purchase lifecycle: shopping cart management, order processing with pessimistic concurrency control, payment handling, ticket generation with unique codes, and bulk operations for event cancellation (refunds and archival). This is the most complex module in the system.

**Namespace:** `Evently.Modules.Ticketing`
**Database Schema:** `ticketing`

---

## 1. Layer Structure

```
Evently.Modules.Ticketing.Domain/              -- Entities, aggregates, domain events, errors
Evently.Modules.Ticketing.Application/         -- Commands, queries, handlers, validators, cart service
Evently.Modules.Ticketing.Infrastructure/      -- EF Core, Redis cart, repositories, payment gateway
Evently.Modules.Ticketing.Presentation/        -- Minimal API endpoints, integration event handlers
Evently.Modules.Ticketing.IntegrationEvents/   -- Public event contracts
```

---

## 2. Domain Layer

### 2.1 Entities & Aggregates

#### Customer
**File:** `Domain/Customers/Customer.cs`

Local copy of a User, created when `UserRegisteredIntegrationEvent` is received.

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Same as User.Id |
| Email | string | Customer email |
| FirstName | string | First name |
| LastName | string | Last name |

**Methods:**
- `static Create(id, email, firstName, lastName)` -- Factory.
- `Update(firstName, lastName)` -- Updates profile.

#### Order (Aggregate Root)
**File:** `Domain/Orders/Order.cs`

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Primary key |
| CustomerId | Guid | FK to Customer |
| Status | OrderStatus | Pending, Paid, Refunded, Canceled |
| TotalPrice | decimal | Sum of all order items |
| Currency | string | Currency code |
| TicketsIssued | bool | Whether tickets have been generated |
| CreatedAtUtc | DateTime | Order creation time |
| OrderItems | IReadOnlyCollection\<OrderItem\> | Line items |

**Methods:**
- `static Create(customer)` -- Factory. Sets status to Pending. Raises `OrderCreatedDomainEvent`.
- `AddItem(ticketType, quantity, price, currency)` -- Adds OrderItem, recalculates TotalPrice.
- `IssueTickets()` -- Marks `TicketsIssued = true`. Raises `OrderTicketsIssuedDomainEvent`. Fails if already issued.
- `Pay()` -- Transitions status `Pending -> Paid`. Raises `OrderPaidDomainEvent`. Fails if not Pending.
- `Refund()` -- Transitions status `Paid -> Refunded`. Raises `OrderRefundedDomainEvent`. Fails if not Paid. Idempotent (no-op if already Refunded).
- `Cancel()` -- Transitions status `Pending -> Canceled`. Raises `OrderCanceledDomainEvent`. Fails if not Pending. Idempotent (no-op if already Canceled).

#### OrderItem (Value Object)
**File:** `Domain/Orders/OrderItem.cs`

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Primary key |
| OrderId | Guid | FK to Order |
| TicketTypeId | Guid | FK to TicketType |
| Quantity | decimal | Number of tickets |
| UnitPrice | decimal | Price per ticket |
| Price | decimal | Calculated: Quantity * UnitPrice |
| Currency | string | Currency code |

#### Payment (Aggregate Root)
**File:** `Domain/Payments/Payment.cs`

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Primary key |
| OrderId | Guid | FK to Order |
| TransactionId | Guid | External payment gateway transaction ID |
| Amount | decimal | Total charged amount |
| Currency | string | Currency code |
| AmountRefunded | decimal? | Accumulated refund total |
| CreatedAtUtc | DateTime | Payment time |
| RefundedAtUtc | DateTime? | When last refund occurred |

**Methods:**
- `static Create(order, transactionId, amount, currency)` -- Factory. Raises `PaymentCreatedDomainEvent`.
- `Refund(refundAmount)` -- Processes refund. Validates:
  - Not already fully refunded (`AmountRefunded` != `Amount`)
  - Sufficient funds (`AmountRefunded + refundAmount <= Amount`)
  - Raises `PaymentRefundedDomainEvent` (full) or `PaymentPartiallyRefundedDomainEvent` (partial)

#### Ticket
**File:** `Domain/Tickets/Ticket.cs`

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Primary key |
| CustomerId | Guid | FK to Customer |
| OrderId | Guid | FK to Order |
| EventId | Guid | FK to Event |
| TicketTypeId | Guid | FK to TicketType |
| Code | string | Unique code: `tc_{Ulid}` |
| CreatedAtUtc | DateTime | Ticket creation time |
| Archived | bool | Soft-delete flag |

**Methods:**
- `static Create(order, ticketType)` -- Factory. Generates unique ULID-based code. Raises `TicketCreatedDomainEvent`.
- `Archive()` -- Sets `Archived = true`. Raises `TicketArchivedDomainEvent`. No-op if already archived.

**Code Format:** `tc_` prefix + ULID (Universally Unique Lexicographically Sortable Identifier). Example: `tc_01H5K3ABCDEFGHIJKLMNOPQRST`

#### Event (Local Copy)
**File:** `Domain/Events/Event.cs`

Local copy of the Events module's Event, created when `EventPublishedIntegrationEvent` is received.

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Same as Events.Event.Id |
| Title | string | Event title |
| Description | string | Event description |
| Location | string | Venue |
| StartsAtUtc | DateTime | Start time |
| EndsAtUtc | DateTime? | End time |
| Canceled | bool | Cancellation flag |

**Methods:**
- `static Create(...)` -- Factory.
- `Reschedule(startsAt, endsAt)` -- Updates times. Raises `EventRescheduledDomainEvent`.
- `Cancel()` -- Sets `Canceled = true`. Raises `EventCanceledDomainEvent`.
- `PaymentsRefunded()` -- Raises `EventPaymentsRefundedDomainEvent`.
- `TicketsArchived()` -- Raises `EventTicketsArchivedDomainEvent`.

#### TicketType (Local Copy with Availability)
**File:** `Domain/Events/TicketType.cs`

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Same as Events.TicketType.Id |
| EventId | Guid | FK to Event |
| Name | string | Tier name |
| Price | decimal | Current price |
| Currency | string | Currency code |
| Quantity | decimal | Total quantity |
| AvailableQuantity | decimal | Remaining quantity (decremented on purchase) |

**Methods:**
- `static Create(...)` -- Factory. Sets `AvailableQuantity = Quantity`.
- `UpdatePrice(price)` -- Updates price.
- `UpdateQuantity(quantity)` -- Decreases `AvailableQuantity`. Validates sufficient stock. Raises `TicketTypeSoldOutDomainEvent` when quantity reaches 0.

### 2.2 Domain Events

| Event | Properties | When Raised |
|-------|-----------|-------------|
| OrderCreatedDomainEvent | OrderId | Order.Create() |
| OrderTicketsIssuedDomainEvent | OrderId | Order.IssueTickets() |
| PaymentCreatedDomainEvent | PaymentId | Payment.Create() |
| PaymentRefundedDomainEvent | PaymentId, TransactionId, RefundAmount | Payment.Refund() (full) |
| PaymentPartiallyRefundedDomainEvent | PaymentId, TransactionId, RefundAmount | Payment.Refund() (partial) |
| TicketCreatedDomainEvent | TicketId | Ticket.Create() |
| TicketArchivedDomainEvent | TicketId, Code | Ticket.Archive() |
| EventCanceledDomainEvent | EventId | Event.Cancel() |
| EventRescheduledDomainEvent | EventId, StartsAtUtc, EndsAtUtc | Event.Reschedule() |
| EventPaymentsRefundedDomainEvent | EventId | Event.PaymentsRefunded() |
| EventTicketsArchivedDomainEvent | EventId | Event.TicketsArchived() |
| OrderPaidDomainEvent | OrderId | Order.Pay() |
| OrderRefundedDomainEvent | OrderId | Order.Refund() |
| OrderCanceledDomainEvent | OrderId | Order.Cancel() |
| TicketTypeSoldOutDomainEvent | TicketTypeId | TicketType.UpdateQuantity() |

### 2.3 Repository Interfaces

**IOrderRepository**
- `Task<Order?> GetAsync(Guid id, CancellationToken ct)`
- `Task<IReadOnlyCollection<Order>> GetForEventAsync(Guid eventId, CancellationToken ct)` -- Gets all orders for an event (via tickets' event ID)
- `void Insert(Order order)`

**ICustomerRepository**
- `Task<Customer?> GetAsync(Guid id, CancellationToken ct)`
- `void Insert(Customer customer)`

**IPaymentRepository**
- `Task<Payment?> GetAsync(Guid id, CancellationToken ct)`
- `Task<IReadOnlyCollection<Payment>> GetForEventAsync(Event event, CancellationToken ct)`
- `void Insert(Payment payment)`

**ITicketRepository**
- `Task<IReadOnlyCollection<Ticket>> GetForEventAsync(Event event, CancellationToken ct)`
- `void InsertRange(IEnumerable<Ticket> tickets)`

**IEventRepository**
- `Task<Event?> GetAsync(Guid id, CancellationToken ct)`
- `void Insert(Event event)`

**ITicketTypeRepository**
- `Task<TicketType?> GetAsync(Guid id, CancellationToken ct)`
- `Task<TicketType?> GetWithLockAsync(Guid id, CancellationToken ct)` -- **Pessimistic lock** (`FOR UPDATE`)
- `void InsertRange(IEnumerable<TicketType> ticketTypes)`

### 2.4 Domain Errors

**OrderErrors:**
- `NotFound(Guid orderId)` -- Order not found
- `TicketsAlreadyIssued` -- Cannot re-issue tickets
- `NotPaid` -- Order is not in Paid status (cannot refund)
- `AlreadyRefunded` -- Order is already refunded
- `NotPending` -- Order is not in Pending status (cannot pay or cancel)

**CustomerErrors:**
- `NotFound(Guid customerId)` -- Customer not found

**PaymentErrors:**
- `NotFound(Guid paymentId)` -- Payment not found
- `AlreadyRefunded` -- Full refund already processed
- `NotEnoughFunds` -- Refund amount exceeds remaining balance

**TicketErrors:**
- `NotFound(Guid ticketId)` -- By ID
- `NotFound(string code)` -- By code

**EventErrors:**
- `NotFound(Guid eventId)` -- Event not found

**TicketTypeErrors:**
- `NotFound(Guid ticketTypeId)` -- Ticket type not found
- `NotEnoughQuantity(decimal available)` -- Insufficient stock

---

## 3. Application Layer

### 3.1 Commands

#### Cart Management

**AddItemToCartCommand**
```
Input:  CustomerId, TicketTypeId, Quantity
Output: Result
```
1. Validates customer exists
2. Fetches ticket type, checks `AvailableQuantity >= Quantity`
3. Creates `CartItem(ticketTypeId, quantity, price, currency)`
4. Calls `ICartService.AddItemAsync()` (Redis)

**RemoveItemFromCartCommand**
```
Input:  CustomerId, TicketTypeId
Output: Result
```
Removes item from Redis cart.

**ClearCartCommand**
```
Input:  CustomerId
Output: Result
```
Clears entire cart from Redis.

#### Order Processing

**CreateOrderCommand** (Most complex handler in the system)
```
Input:  CustomerId
Output: Result<Guid>
```
1. `BEGIN TRANSACTION`
2. Fetch Customer (error if not found)
3. Create `Order.Create(customer)` -> status Pending
4. Fetch cart from Redis via `ICartService.GetAsync()`
5. **For each cart item:**
   a. `GetWithLockAsync(ticketTypeId)` -- acquires `SELECT ... FOR UPDATE` lock
   b. Validate `AvailableQuantity >= cartItem.Quantity`
   c. `ticketType.UpdateQuantity(cartItem.Quantity)` -- decrements available
   d. `order.AddItem(ticketType, quantity, price, currency)`
6. Insert Order
7. `IPaymentService.ChargeAsync(order)` -- external payment gateway
8. `Payment.Create(order, transactionId, amount, currency)`
9. Insert Payment
10. `SaveChangesAsync()` -- captures domain events in outbox
11. `COMMIT TRANSACTION`
12. `ICartService.ClearAsync(customerId)` -- clear Redis cart

**Concurrency Control:** Uses pessimistic locking (`FOR UPDATE`) on TicketType rows to prevent overselling when multiple customers purchase simultaneously.

**CreateTicketBatchCommand** (Internal -- triggered by domain event)
```
Input:  OrderId
Output: Result
```
1. Fetch Order with OrderItems
2. For each OrderItem, generate `Quantity` number of Ticket entities
3. Each ticket gets unique `tc_{Ulid}` code
4. `InsertRange(tickets)` -- batch insert
5. `order.IssueTickets()` -- marks as issued
6. Save changes

#### Event Management (Triggered by integration events)

**CreateEventCommand** (Ticketing-internal)
```
Input:  EventId, Title, Description, Location, StartsAtUtc, EndsAtUtc, TicketTypes[]
Output: Result
```
Creates local Event + TicketType records. Triggered by `EventPublishedIntegrationEvent`.

**RescheduleEventCommand** (Ticketing-internal)
```
Input:  EventId, StartsAtUtc, EndsAtUtc
Output: Result
```
Updates local Event. Triggered by `EventRescheduledIntegrationEvent`.

**CancelEventCommand** (Ticketing-internal)
```
Input:  EventId
Output: Result
```
Marks local Event as canceled. Triggered by `EventCancellationStartedIntegrationEvent`.

**UpdateTicketTypePriceCommand** (Ticketing-internal)
```
Input:  TicketTypeId, Price
Output: Result
```
Updates local TicketType price. Triggered by `TicketTypePriceChangedIntegrationEvent`.

#### Customer Management (Triggered by integration events)

**CreateCustomerCommand**
```
Input:  CustomerId, Email, FirstName, LastName
Output: Result
```
Creates local Customer. Triggered by `UserRegisteredIntegrationEvent`.

**UpdateCustomerCommand**
```
Input:  CustomerId, FirstName, LastName
Output: Result
```
Updates local Customer. Triggered by `UserProfileUpdatedIntegrationEvent`.

#### Order Operations

**RefundOrdersForEventCommand** (Triggered by event cancellation)
```
Input:  EventId
Output: Result
```
1. Fetches all orders for the event via `IOrderRepository.GetForEventAsync()`
2. Calls `order.Refund()` on each paid order (idempotent -- skips already refunded)
3. Save changes

#### Payment Operations

**RefundPaymentCommand**
```
Input:  PaymentId, Amount
Output: Result
```
Calls `payment.Refund(amount)` on aggregate.

**RefundPaymentsForEventCommand** (Triggered by event cancellation)
```
Input:  EventId
Output: Result
```
1. Fetches event
2. Gets all payments for event via `IPaymentRepository.GetForEventAsync()`
3. Refunds each payment (full amount)
4. Calls `event.PaymentsRefunded()` -- signals saga

#### Ticket Operations

**ArchiveTicketsForEventCommand** (Triggered by event cancellation)
```
Input:  EventId
Output: Result
```
1. Fetches event
2. Gets all tickets for event via `ITicketRepository.GetForEventAsync()`
3. Archives each ticket
4. Calls `event.TicketsArchived()` -- signals saga

### 3.2 Queries

#### GetCartQuery
```
Input:  CustomerId
Output: Cart (Items: CartItem[])
```
Reads from Redis via `ICartService`.

**CartItem:** `{ TicketTypeId, Quantity, Price, Currency, Name }`

#### GetOrderQuery
```
Input:  OrderId
Output: OrderResponse
```
Dapper query joining `orders` and `order_items` tables.

**OrderResponse:** `{ Id, CustomerId, Status, TotalPrice, Currency, CreatedAtUtc, OrderItems[] }`
**OrderItemResponse:** `{ Id, TicketTypeId, Quantity, UnitPrice, Price, Currency }`

#### GetOrdersQuery
```
Input:  CustomerId
Output: IReadOnlyCollection<OrderResponse>
```
All orders for the authenticated customer.

#### GetTicketQuery
```
Input:  TicketId
Output: TicketResponse
```

#### GetTicketByCodeQuery
```
Input:  Code (string)
Output: TicketResponse
```

#### GetTicketsForOrderQuery
```
Input:  OrderId
Output: IReadOnlyCollection<TicketResponse>
```

**TicketResponse:** `{ Id, CustomerId, OrderId, EventId, TicketTypeId, Code, CreatedAtUtc }`

### 3.3 Domain Event Handlers

#### OrderCreatedDomainEventHandler
- Enriches with `GetOrderQuery`
- Publishes `OrderCreatedIntegrationEvent`

#### CreateTicketsDomainEventHandler (OrderCreatedDomainEvent)
- Sends `CreateTicketBatchCommand` via MediatR
- This generates individual tickets for the order

#### OrderTicketsIssuedDomainEventHandler
- Enriches order with ticket details
- Publishes `TicketIssuedIntegrationEvent` for each ticket
- **Consumed by:** Attendance (creates local ticket records)

#### EventPaymentsRefundedDomainEventHandler
- Publishes `EventPaymentsRefundedIntegrationEvent`
- **Consumed by:** CancelEventSaga in Events module

#### EventTicketsArchivedDomainEventHandler
- Publishes `EventTicketsArchivedIntegrationEvent`
- **Consumed by:** CancelEventSaga in Events module

#### ArchiveTicketsEventCanceledDomainEventHandler
- Sends `ArchiveTicketsForEventCommand` via MediatR

#### RefundPaymentsEventCanceledDomainEventHandler
- Sends `RefundPaymentsForEventCommand` via MediatR

#### RefundOrdersEventCanceledDomainEventHandler
- Sends `RefundOrdersForEventCommand` via MediatR
- Runs in parallel with RefundPayments and ArchiveTickets handlers

---

## 4. Infrastructure Layer

### 4.1 Database Context
**TicketingDbContext** -- EF Core DbContext with schema `ticketing`

**Tables:**
- `ticketing.customers`
- `ticketing.orders`
- `ticketing.order_items`
- `ticketing.payments`
- `ticketing.tickets`
- `ticketing.events` (local copy)
- `ticketing.ticket_types` (local copy with `available_quantity`)
- `ticketing.outbox_messages`
- `ticketing.inbox_messages`
- `ticketing.outbox_message_consumers`
- `ticketing.inbox_message_consumers`

### 4.2 Cart Service (Redis)
**ICartService** / **CartService**
- `AddItemAsync(customerId, cartItem)` -- Serializes to Redis hash
- `GetAsync(customerId)` -- Deserializes from Redis
- `RemoveItemAsync(customerId, ticketTypeId)` -- Removes from Redis hash
- `ClearAsync(customerId)` -- Deletes Redis key

**Redis Key Pattern:** `cart:{customerId}`

### 4.3 Payment Service
**IPaymentService** / **PaymentService** (Fake implementation)
- `ChargeAsync(order)` -- Returns a fake `TransactionId` (simulates payment gateway)
- `RefundAsync(transactionId, amount)` -- Simulates refund

In production, this would integrate with Stripe, PayPal, or similar.

### 4.4 Customer Context
**ICustomerContext** -- Resolves the current customer from HTTP context.
- Extracts user ID from bearer token claims
- Used by endpoints to know which customer is making requests

### 4.5 Repositories
- `OrderRepository` -- Includes `OrderItems` navigation property
- `TicketTypeRepository` -- Implements `GetWithLockAsync()` using raw SQL `FOR UPDATE`
- `PaymentRepository` -- Implements `GetForEventAsync()` with join to orders/ticket types
- `TicketRepository` -- Implements batch insert via `AddRange()`

### 4.6 Module Registration
**TicketingModule.cs** -- Implements `IModule`:
- Registers all repositories and services
- Configures MassTransit consumers:
  - `IntegrationEventConsumer<UserRegisteredIntegrationEvent>`
  - `IntegrationEventConsumer<UserProfileUpdatedIntegrationEvent>`
  - `IntegrationEventConsumer<EventPublishedIntegrationEvent>`
  - `IntegrationEventConsumer<TicketTypePriceChangedIntegrationEvent>`
  - `IntegrationEventConsumer<EventCancellationStartedIntegrationEvent>`

---

## 5. Presentation Layer (API Endpoints)

### Carts

| Method | Route | Permission | Handler |
|--------|-------|------------|---------|
| PUT | `/carts/add` | AddToCart | AddItemToCartCommand |
| GET | `/carts` | GetCart | GetCartQuery |
| PUT | `/carts/remove` | RemoveFromCart | RemoveItemFromCartCommand |
| DELETE | `/carts` | RemoveFromCart | ClearCartCommand |

### Orders

| Method | Route | Permission | Handler |
|--------|-------|------------|---------|
| POST | `/orders` | CreateOrder | CreateOrderCommand |
| GET | `/orders/{id}` | GetOrders | GetOrderQuery |
| GET | `/orders` | GetOrders | GetOrdersQuery |

### Tickets

| Method | Route | Permission | Handler |
|--------|-------|------------|---------|
| GET | `/tickets/{id}` | GetTickets | GetTicketQuery |
| GET | `/tickets/code/{code}` | GetTickets | GetTicketByCodeQuery |
| GET | `/tickets/order/{orderId}` | GetTickets | GetTicketsForOrderQuery |

**Total: 10 endpoints**

---

## 6. Integration Events (Public Contracts)

**Namespace:** `Evently.Modules.Ticketing.IntegrationEvents`

| Event | Properties | Consumed By |
|-------|-----------|-------------|
| OrderCreatedIntegrationEvent | Id, OccurredOnUtc, OrderId, CustomerId, TotalPrice, Currency, Items[] | -- |
| TicketIssuedIntegrationEvent | Id, OccurredOnUtc, TicketId, CustomerId, EventId, Code | Attendance |
| EventPaymentsRefundedIntegrationEvent | Id, OccurredOnUtc, EventId | Events (Saga) |
| EventTicketsArchivedIntegrationEvent | Id, OccurredOnUtc, EventId | Events (Saga) |
| TicketArchivedIntegrationEvent | Id, OccurredOnUtc, TicketId, Code | -- |
| TicketTypeSoldOutIntegrationEvent | Id, OccurredOnUtc, TicketTypeId | -- |

---

## 7. Integration Event Handlers (Incoming)

| From Module | Event | Handler Action |
|-------------|-------|---------------|
| Users | UserRegisteredIntegrationEvent | CreateCustomerCommand |
| Users | UserProfileUpdatedIntegrationEvent | UpdateCustomerCommand |
| Events | EventPublishedIntegrationEvent | CreateEventCommand (with ticket types) |
| Events | TicketTypePriceChangedIntegrationEvent | UpdateTicketTypePriceCommand |
| Events | EventCancellationStartedIntegrationEvent | CancelEventCommand -> triggers refund + archive |

---

## 8. Critical Flow: Order Creation (Detailed)

This is the most complex and critical flow in the system:

```
POST /orders
    |
    v
CreateOrderCommand { CustomerId }
    |
    v
CreateOrderCommandHandler
    |
    +-- BEGIN DB TRANSACTION
    |
    +-- Fetch Customer (fail if not found)
    |
    +-- Create Order aggregate (status: Pending)
    |
    +-- Fetch Cart from Redis
    |       { items: [{ ticketTypeId, quantity }] }
    |
    +-- FOR EACH cart item:
    |       |
    |       +-- SELECT ... FROM ticket_types WHERE id = @id FOR UPDATE
    |       |   (Pessimistic lock prevents concurrent overselling)
    |       |
    |       +-- IF available_quantity < requested_quantity
    |       |       RETURN TicketTypeErrors.NotEnoughQuantity
    |       |
    |       +-- ticketType.UpdateQuantity(quantity)
    |       |   (Decrements available_quantity)
    |       |   (May raise TicketTypeSoldOutDomainEvent if quantity = 0)
    |       |
    |       +-- order.AddItem(ticketType, quantity, price, currency)
    |
    +-- Insert Order to DB
    |
    +-- paymentService.ChargeAsync(order)
    |       Returns TransactionId
    |
    +-- Payment.Create(order, transactionId, amount, currency)
    |
    +-- Insert Payment to DB
    |
    +-- order.Pay()
    |       (Transitions status: Pending -> Paid)
    |
    +-- SaveChangesAsync()
    |       Outbox interceptor captures:
    |       - OrderCreatedDomainEvent
    |       - PaymentCreatedDomainEvent
    |       - TicketTypeSoldOutDomainEvent (if any)
    |
    +-- COMMIT TRANSACTION
    |
    +-- cartService.ClearAsync(customerId)
    |       (Redis cart cleared)
    |
    v
RETURN Order.Id

    ... Later (ProcessOutboxJob) ...

OrderCreatedDomainEvent
    |
    +-- OrderCreatedDomainEventHandler
    |       -> Publishes OrderCreatedIntegrationEvent
    |
    +-- CreateTicketsDomainEventHandler
            -> CreateTicketBatchCommand
                -> Generates N tickets with unique codes
                -> order.IssueTickets()
                -> OrderTicketsIssuedDomainEvent
                    -> TicketIssuedIntegrationEvent(s)
                        -> Attendance creates local ticket records
```
