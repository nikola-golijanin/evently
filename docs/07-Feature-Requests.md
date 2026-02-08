# Feature Requests

Features to implement, analyzed from a domain perspective. Organized by priority based on how critical they are to a real event management and ticketing platform.

## Table of Contents

- [Priority 1: Core Missing Features](#priority-1-core-missing-features)
  - [FR-001: Notifications Module](#fr-001-notifications-module)
  - [FR-002: Waitlist When Sold Out](#fr-002-waitlist-when-sold-out)
  - [FR-003: User-Initiated Refund Requests](#fr-003-user-initiated-refund-requests)
  - [FR-004: Discount & Promo Codes](#fr-004-discount--promo-codes)
  - [FR-005: Event Organizer Ownership](#fr-005-event-organizer-ownership)
  - [FR-014: Order Status Update on Event Cancellation](#fr-014-order-status-update-on-event-cancellation)
- [Priority 2: Important Enhancements](#priority-2-important-enhancements)
  - [FR-006: Cart Inventory Reservation](#fr-006-cart-inventory-reservation)
  - [FR-007: Ticket Transfers](#fr-007-ticket-transfers)
  - [FR-008: Event Reviews & Ratings](#fr-008-event-reviews--ratings)
  - [FR-009: Recurring Events & Event Series](#fr-009-recurring-events--event-series)
- [Priority 3: Advanced Features](#priority-3-advanced-features)
  - [FR-010: Venue Management](#fr-010-venue-management)
  - [FR-011: Seating & Sections](#fr-011-seating--sections)
  - [FR-012: Analytics & Reporting](#fr-012-analytics--reporting)
  - [FR-013: Group Bookings](#fr-013-group-bookings)
- [Feature Dependency Map](#feature-dependency-map)

---

## Priority 1: Core Missing Features

These features fill critical domain gaps. Without them, the platform lacks functionality that users of any ticketing system would expect.

---

### FR-001: Notifications Module

**Problem:** The system performs important actions (order confirmed, tickets issued, event canceled, event rescheduled) but never tells the user. There is no email, SMS, or push notification system. A customer buys tickets and receives no confirmation. An event gets rescheduled and attendees don't know.

**Domain Concept:** A new **Notifications** bounded context that listens to integration events from all modules and dispatches user-facing messages through configurable channels (email, in-app, push).

**What should trigger notifications:**

| Trigger Event | Notification | Recipient |
|---------------|-------------|-----------|
| UserRegisteredIntegrationEvent | Welcome email | New user |
| OrderCreatedIntegrationEvent | Order confirmation with ticket details | Customer |
| TicketIssuedIntegrationEvent | Ticket with QR/barcode code | Customer |
| EventRescheduledIntegrationEvent | "Your event has been rescheduled" | All ticket holders |
| EventCancellationCompletedIntegrationEvent | "Event canceled, refund processed" | All ticket holders |
| PaymentRefundedDomainEvent | Refund confirmation | Customer |
| AttendeeCheckedInDomainEvent | Check-in confirmation | Attendee |
| Event reminder (scheduled) | "Event starts tomorrow" | All ticket holders |

**Module structure:**
- Consumes integration events from all other modules
- Manages notification preferences per user (opt-in/out per channel)
- Tracks delivery status (sent, delivered, failed)
- Supports templates with variable substitution

**Note:** A placeholder `SendOrderConfirmationDomainEventHandler` already exists in the Ticketing module but contains no implementation.

**New integration events needed:** None -- this module only consumes existing events.

**Affected modules:** None changed. New module added.

---

### FR-002: Waitlist When Sold Out

**Problem:** When a ticket type sells out, `TicketTypeSoldOutIntegrationEvent` fires but **nothing consumes it**. Customers who arrive late have no way to express interest. If tickets become available (e.g., order canceled, event capacity increased), there's no mechanism to notify waiting customers.

**Domain Concept:** When a ticket type is sold out, customers can join a waitlist. When inventory becomes available (ticket archived, capacity increased, order canceled), the system automatically offers tickets to waitlisted customers in FIFO order.

**Entities:**
- **WaitlistEntry** -- (CustomerId, TicketTypeId, EventId, Quantity, RequestedAtUtc, Status, OfferedAtUtc, ExpiresAtUtc)
- Status: `Waiting -> Offered -> Converted | Expired | Canceled`

**Business rules:**
1. Customer can only join waitlist when `AvailableQuantity == 0`
2. One waitlist entry per customer per ticket type
3. When inventory becomes available, offer to next customer in queue
4. Offered customer has a time window (e.g., 30 minutes) to complete purchase
5. If offer expires, move to next customer in queue
6. Customer can cancel their waitlist position at any time

**Integration with existing events:**
- Consumes `TicketTypeSoldOutIntegrationEvent` -- enables waitlist for that ticket type
- Consumes `TicketArchivedIntegrationEvent` -- triggers offer to next waitlisted customer (inventory freed up)
- Publishes `WaitlistOfferMadeIntegrationEvent` -- Notifications module sends email

**Where it lives:** Ticketing module (extends existing ticket type management).

**New endpoints:**
- `POST /waitlist` -- Join waitlist for a ticket type
- `GET /waitlist` -- Get customer's waitlist entries
- `DELETE /waitlist/{id}` -- Cancel waitlist entry
- `POST /waitlist/{id}/accept` -- Accept a waitlist offer (creates order)

---

### FR-003: User-Initiated Refund Requests

**Problem:** Refund capability exists internally (`RefundPaymentCommand`, `Payment.Refund()`) but is only triggered by event cancellation via the saga. There is no way for a customer to request a refund themselves. No refund policy enforcement exists (time windows, partial vs full, non-refundable ticket types).

**Domain Concept:** Customers can request refunds for their orders, subject to configurable refund policies. The system evaluates the request against the policy and either auto-approves, partially approves, or requires organizer review.

**Entities:**
- **RefundPolicy** -- (EventId, FullRefundBeforeDays, PartialRefundBeforeDays, PartialRefundPercentage, NonRefundableAfterDays)
- **RefundRequest** -- (OrderId, CustomerId, Reason, RequestedAtUtc, Status, ReviewedAtUtc, RefundAmount)
- Status: `Pending -> Approved | PartiallyApproved | Denied | Processed`

**Business rules:**
1. Default refund policy: full refund up to 7 days before event, 50% up to 48 hours, non-refundable after
2. Organizers can customize refund policy per event
3. Ticket types can be marked as non-refundable (e.g., discounted tickets)
4. Refund auto-approves if within full-refund window
5. Partial refunds calculated based on policy
6. Refund triggers existing `Payment.Refund()` domain logic
7. Refunded tickets are archived

**New endpoints:**
- `POST /refunds` -- Request refund for an order
- `GET /refunds` -- Get customer's refund requests
- `GET /refunds/{id}` -- Get refund request details

**Affected modules:** Ticketing (new entities + endpoints), Events (RefundPolicy on Event or TicketType).

---

### FR-004: Discount & Promo Codes

**Problem:** There is no discount mechanism. Every ticket purchase is at full price. Organizers cannot run promotions, early-bird pricing, or partner discounts. This is a standard feature for any ticketing platform.

**Domain Concept:** Organizers create promo codes tied to events. Customers apply codes at checkout for percentage or fixed-amount discounts. Codes have usage limits, expiration dates, and optional restrictions (specific ticket types, minimum quantity).

**Entities:**
- **PromoCode** -- (Code, EventId, DiscountType, DiscountValue, MaxUses, CurrentUses, ValidFrom, ValidUntil, MinQuantity, ApplicableTicketTypeIds, IsActive)
- DiscountType: `Percentage | FixedAmount`
- **OrderDiscount** -- (OrderId, PromoCodeId, DiscountAmount) -- tracks applied discount per order

**Business rules:**
1. Promo code is unique per event
2. Code must be within valid date range
3. Code must not exceed max usage count
4. Percentage discount applies to each line item proportionally
5. Fixed amount discount applies to order total
6. Discount cannot reduce price below zero
7. Multiple codes per order: organizer-configurable (default: one code per order)
8. Discount tracked on OrderItem level for accurate refund calculations

**Integration with order creation:**
- Cart gets a `PromoCode` field
- `CreateOrderCommand` validates and applies discount during checkout
- Discount reflected in `OrderItem.Price` or tracked separately

**New endpoints:**
- `POST /promo-codes` -- Create promo code (organizer)
- `GET /promo-codes?eventId=` -- List promo codes for event (organizer)
- `PUT /promo-codes/{id}/deactivate` -- Deactivate code
- `POST /promo-codes/validate` -- Validate code before checkout (customer)
- `PUT /carts/apply-promo` -- Apply promo code to cart

**Where it lives:** Ticketing module (PromoCode entity, discount logic in order creation).

---

### FR-005: Event Organizer Ownership

**Problem:** Events have no owner. The `Event` entity has no `OrganizerId` or `CreatedBy` field. Any user with the `ModifyEvents` permission can edit or cancel any event. There is no concept of "my events" for organizers. This is a fundamental domain gap -- in any real event platform, organizers manage their own events.

**Domain Concept:** Events are owned by the user who creates them (the organizer). Only the organizer (or administrators) can modify, publish, or cancel their events. Organizers can view analytics for their events. This introduces a new authorization dimension: ownership-based access control.

**Changes needed:**
- `Event` entity gets `OrganizerId` (Guid) property
- `CreateEventCommand` captures the current user as organizer
- All event mutation endpoints check `event.OrganizerId == currentUserId || isAdmin`
- New query: "Get my events" filtered by organizer

**Business rules:**
1. The user who creates an event becomes its organizer
2. Only the organizer or an admin can publish, reschedule, or cancel the event
3. Organizers can view orders and attendance stats for their events
4. Future: organizer can delegate management to other users

**New endpoints:**
- `GET /events/mine` -- Get events created by the current user

**Affected modules:** Events (Event entity change), Users (possible Organizer role or ownership-based policies).

---

### FR-014: Order Status Update on Event Cancellation

**Problem:** When an event is canceled, the `CancelEventSaga` orchestrates payment refunds and ticket archiving, but `Order.Status` is never updated. Orders remain `Paid` even though their payments are fully refunded and their tickets are archived. This creates an inconsistent state — the order looks like a successful purchase, but the tickets are gone and the money is returned.

**Domain Concept:** `Refunded` and `Canceled` represent two distinct terminal states for an order:

| Transition | Meaning |
|------------|---------|
| `Paid → Refunded` | Order was paid and fulfilled, then unwound (event canceled, or future user-initiated refund). A legitimate transaction that got reversed. |
| `Pending → Canceled` | Order was never paid — abandoned, timed out, or explicitly canceled before payment. The transaction never completed. |

When an event is canceled, orders should transition `Paid → Refunded` (not `Canceled`) because they were real, completed transactions. This keeps order status consistent with payment status — `Payment.AmountRefunded == Payment.Amount` aligns with `Order.Status == Refunded`.

**Changes needed:**

- **Domain:** `Order.Refund()` method that transitions status `Paid → Refunded` and raises `OrderRefundedDomainEvent`. Guard: only `Paid` orders can be refunded (idempotent — already refunded orders are skipped).
- **Domain:** `Order.Cancel()` method that transitions status `Pending → Canceled` and raises `OrderCanceledDomainEvent`. Guard: only `Pending` orders can be canceled. Reserved for future use (abandoned carts, unpaid order timeout).
- **Application:** `RefundOrdersForEventCommand` + handler (follows the existing `RefundPaymentsForEventCommand` pattern)
- **Infrastructure:** `IOrderRepository.GetForEventAsync(Guid eventId)` — query orders via their tickets' event ID
- **Domain Event Handler:** `RefundOrdersEventCancellationStartedHandler` — triggered when `EventCancellationStartedIntegrationEvent` is received (alongside the existing refund and archive handlers)

**Business rules:**
1. `Paid → Refunded`: triggered by event cancellation (and future user-initiated refunds via FR-003)
2. `Pending → Canceled`: triggered when an unpaid order is abandoned or explicitly canceled (future use)
3. Order status update happens in parallel with payment refund and ticket archiving (all triggered by the same integration event)
4. Both transitions are idempotent — running them twice for the same event has no additional effect
5. Future: partial refunds (FR-003) may introduce `PartiallyRefunded` status, mirroring how `Payment` already distinguishes `PaymentRefundedDomainEvent` vs `PaymentPartiallyRefundedDomainEvent`

**Optional saga extension:**
- Publish `EventOrdersRefundedIntegrationEvent` from the Ticketing module
- Extend the saga's composite event to a 3-way wait: payments refunded + tickets archived + orders refunded
- This adds consistency guarantees but increases saga complexity

**Where it lives:** Ticketing module (domain, application, infrastructure layers).

**Affected modules:** Ticketing only. No new cross-module contracts needed unless the saga extension is implemented.

---

## Priority 2: Important Enhancements

These features improve the platform significantly but the system works without them.

---

### FR-006: Cart Inventory Reservation

**Problem:** The cart is a Redis-based wish list with a 20-minute TTL, but it does **not reserve inventory**. Adding items to the cart checks current availability but doesn't decrement `AvailableQuantity`. This means:
- Two customers can add the last ticket to their carts simultaneously
- One of them will fail at checkout with `TicketTypeErrors.NotEnoughQuantity`
- Bad user experience -- they think they have a ticket but lose it at payment

**Domain Concept:** When a customer adds tickets to their cart, the system temporarily holds (reserves) the inventory. Reserved tickets are unavailable to other customers. If the cart expires or is cleared, the reservation is released and inventory returns to the available pool.

**Changes needed:**
- `AddItemToCartCommand` decrements `AvailableQuantity` and records a reservation
- `RemoveItemFromCartCommand` / `ClearCartCommand` releases reservation (increments back)
- Cart expiration (Redis TTL) triggers reservation release
- `CreateOrderCommand` converts reservation into permanent purchase (no double-decrement)

**New entity:**
- **InventoryReservation** -- (CustomerId, TicketTypeId, Quantity, ReservedAtUtc, ExpiresAtUtc)

**Business rules:**
1. Reservation lasts 20 minutes (matches cart TTL)
2. Reservation auto-releases on expiry via background job
3. Order creation consumes reservation (no additional quantity check needed)
4. If reservation expired before checkout, order creation falls back to standard pessimistic lock flow

**Complexity note:** This requires coordinating between Redis (cart) and PostgreSQL (inventory). The reservation could live in the database while the cart stays in Redis.

**Affected modules:** Ticketing (cart service, ticket type repository, new background job).

---

### FR-007: Ticket Transfers

**Problem:** Tickets are permanently bound to the customer who purchased them. If you buy a ticket and can't attend, you can't give it to a friend. The only option is the non-existent refund system. Ticket transfers are a standard feature in modern ticketing platforms.

**Domain Concept:** A ticket holder can transfer a ticket to another registered user. The transfer invalidates the original ticket code and generates a new one for the recipient. Transfer history is maintained for audit purposes.

**Entities:**
- **TicketTransfer** -- (TicketId, FromCustomerId, ToCustomerId, InitiatedAtUtc, CompletedAtUtc, Status)
- Status: `Pending -> Accepted | Declined | Canceled`

**Business rules:**
1. Only the ticket owner can initiate a transfer
2. Recipient must be a registered user
3. Ticket cannot be transferred if already used (checked in)
4. Ticket cannot be transferred if event is canceled
5. Transfer generates a new ticket code (old one invalidated)
6. Pending transfers can be canceled by the sender
7. Recipient must explicitly accept the transfer
8. Transfer updates the Attendance module's ticket record (new AttendeeId)

**Integration events:**
- `TicketTransferredIntegrationEvent` -- consumed by Attendance (updates ticket ownership)
- Consumed by Notifications (email to both parties)

**New endpoints:**
- `POST /tickets/{id}/transfer` -- Initiate transfer (sender)
- `POST /transfers/{id}/accept` -- Accept transfer (recipient)
- `POST /transfers/{id}/decline` -- Decline transfer (recipient)
- `GET /transfers` -- Get transfer history

**Affected modules:** Ticketing (Ticket entity, new TicketTransfer entity), Attendance (update ticket ownership).

---

### FR-008: Event Reviews & Ratings

**Problem:** After an event ends, there is no feedback mechanism. Organizers don't know how the event was perceived. Future attendees can't evaluate events based on past reviews. The `Completed` event status exists but triggers nothing.

**Domain Concept:** After an event ends, attendees who checked in can leave a review with a rating and optional text. Reviews are tied to the Attendance module since only verified attendees (those who checked in) can review.

**Entities:**
- **Review** -- (EventId, AttendeeId, Rating (1-5), Comment, CreatedAtUtc, UpdatedAtUtc)

**Business rules:**
1. Only attendees who checked in can leave a review (verified attendance)
2. One review per attendee per event
3. Reviews can only be submitted after the event ends
4. Reviews can be edited within 30 days of the event
5. Rating is required (1-5 stars), comment is optional
6. Average rating aggregated into EventStatistics (new field)

**New endpoints:**
- `POST /events/{id}/reviews` -- Submit review
- `PUT /events/{id}/reviews` -- Update review
- `GET /events/{id}/reviews` -- Get reviews for event (paginated)

**Where it lives:** Attendance module (extends existing EventStatistics with average rating).

---

### FR-009: Recurring Events & Event Series

**Problem:** Every event is a standalone instance. Organizers who run weekly meetups, monthly concerts, or annual conferences must create each occurrence manually. No way to link related events or inherit settings.

**Domain Concept:** An event can belong to a series. A series defines a recurrence pattern (weekly, biweekly, monthly) and template properties. Individual occurrences can be generated from the series and then customized.

**Entities:**
- **EventSeries** -- (Title, Description, Location, CategoryId, RecurrencePattern, StartsAtUtc, EndsAtUtc)
- RecurrencePattern: `{ Type: Weekly|Biweekly|Monthly, DayOfWeek?, DayOfMonth?, Count? }`
- Events get optional `SeriesId` (Guid?) to link to parent series

**Business rules:**
1. Creating a series generates N future event drafts based on recurrence pattern
2. Each occurrence is a full Event entity that can be individually modified
3. Series-level changes can propagate to future unmodified occurrences
4. Canceling a series cancels all future unpublished occurrences
5. Individual occurrences can be detached from the series

**New endpoints:**
- `POST /event-series` -- Create series with recurrence pattern
- `GET /event-series/{id}` -- Get series with all occurrences
- `PUT /event-series/{id}` -- Update series (propagate to future events)
- `DELETE /event-series/{id}` -- Cancel series

**Where it lives:** Events module (new EventSeries entity, Event gets optional SeriesId FK).

---

## Priority 3: Advanced Features

Features that add significant value for larger-scale or specialized use cases.

---

### FR-010: Venue Management

**Problem:** Location is a free-text string field on the Event entity. No structured venue data, no reusability across events, no capacity tracking. An organizer who hosts events at the same venue must retype the address each time.

**Domain Concept:** Venues are first-class entities with structured address data, capacity limits, and reusability across events. Events reference a venue instead of storing a raw location string.

**Entities:**
- **Venue** -- (Name, Address, City, Country, Capacity, Description, Coordinates?)

**Business rules:**
1. Events reference a Venue by ID (optional -- free-text location still supported for backward compatibility)
2. Venue capacity serves as an upper bound for total ticket quantity across all ticket types
3. Venues are reusable across events
4. Publishing an event validates total ticket quantity does not exceed venue capacity

**New endpoints:**
- `POST /venues` -- Create venue
- `GET /venues` -- List venues (with search)
- `GET /venues/{id}` -- Get venue details
- `PUT /venues/{id}` -- Update venue

**Where it lives:** Events module (new Venue entity, Event gets optional VenueId FK).

---

### FR-011: Seating & Sections

**Problem:** All tickets are general admission. No concept of seats, rows, sections, or zones. For concerts, theaters, and conferences with assigned seating, this is essential.

**Domain Concept:** Venues have sections (e.g., "Floor", "Balcony", "VIP Area"). Sections have rows and seats. Ticket types can be linked to sections. When purchasing, customers select specific seats or get auto-assigned.

**Entities:**
- **Section** -- (VenueId, Name, Capacity)
- **Seat** -- (SectionId, Row, Number, Status)
- Seat Status: `Available | Reserved | Sold | Blocked`

**Business rules:**
1. Ticket types optionally map to sections
2. Seat selection happens during cart add or checkout
3. Selected seats are reserved (ties into FR-006 Cart Reservation)
4. Seat map can be returned via API for frontend rendering

**Depends on:** FR-010 (Venue Management), FR-006 (Cart Reservation).

---

### FR-012: Analytics & Reporting

**Problem:** Only basic attendance statistics exist (tickets sold, checked in, duplicates). No revenue reporting, sales trends, conversion rates, or organizer dashboards.

**Domain Concept:** Aggregate reporting across events owned by an organizer. Revenue breakdowns, sales velocity, attendance rates, and trends over time.

**Reports:**
- Revenue per event (total sales, refunds, net revenue)
- Sales velocity (tickets sold per day leading up to event)
- Conversion rate (views -> cart -> purchase)
- Attendance rate (tickets sold vs checked in)
- Category performance (which categories sell best)
- Promo code effectiveness (usage, revenue impact)

**Implementation approach:** Read model projections (same CQRS pattern as EventStatistics). Background jobs aggregate data from existing domain events.

**Depends on:** FR-005 (Event Organizer -- to scope reports to "my events").

---

### FR-013: Group Bookings

**Problem:** Each order is for one customer. There's no concept of group reservations where an organizer, company, or group leader books tickets for multiple people with a single transaction.

**Domain Concept:** A group booking allows purchasing multiple tickets and assigning them to different attendees. The group leader manages the booking and can assign/reassign names to tickets.

**Entities:**
- **GroupBooking** -- (LeaderCustomerId, EventId, GroupName, Tickets[])
- Each ticket in the group gets an `AssignedTo` field (name/email)

**Business rules:**
1. Group leader purchases N tickets in one order
2. Tickets initially unassigned (leader's name)
3. Leader can assign attendee details to each ticket before the event
4. Each assigned ticket generates a unique code for that attendee
5. Minimum group size configurable per event (e.g., 10+)
6. Optional group discount (ties into FR-004 Promo Codes)

**New endpoints:**
- `POST /group-bookings` -- Create group booking
- `PUT /group-bookings/{id}/assign` -- Assign attendees to tickets
- `GET /group-bookings/{id}` -- Get group booking details

**Where it lives:** Ticketing module.

---

## Feature Dependency Map

Some features build on others. This shows what to implement in what order.

```
FR-001 Notifications ──────────────── (standalone, high value)
FR-005 Event Organizer Ownership ──── (standalone, enables FR-012)
FR-014 Order Status on Cancel ─────── (standalone, completes CancelEventSaga lifecycle)
FR-004 Discount & Promo Codes ─────── (standalone, enables FR-013)
FR-003 User-Initiated Refunds ─────── (standalone)
FR-002 Waitlist ────────────────────── (standalone, uses existing SoldOut event)

FR-006 Cart Reservation ────────────── (standalone, enables FR-011)

FR-007 Ticket Transfers ───────────── (standalone)
FR-008 Event Reviews ──────────────── (standalone)
FR-009 Recurring Events ───────────── (standalone)

FR-010 Venue Management ───────────── (standalone, enables FR-011)
FR-011 Seating & Sections ─────────── (depends on FR-010 + FR-006)
FR-012 Analytics & Reporting ──────── (depends on FR-005)
FR-013 Group Bookings ─────────────── (optionally enhanced by FR-004)
```

**Suggested implementation order for maximum incremental value:**
1. FR-014 Order Status Update on Event Cancellation (small change, fixes data inconsistency)
2. FR-005 Event Organizer Ownership (small change, fundamental domain fix)
3. FR-001 Notifications Module (new module, consumes existing events)
4. FR-002 Waitlist (activates currently-dead integration events)
5. FR-003 User-Initiated Refunds (exposes existing internal capability)
6. FR-004 Discount & Promo Codes (standard commerce feature)
7. FR-006 Cart Inventory Reservation (fixes race condition)
8. FR-007 Ticket Transfers
9. FR-008 Event Reviews & Ratings
10. FR-009 Recurring Events
11. FR-010 Venue Management
12. FR-012 Analytics & Reporting
13. FR-011 Seating & Sections
14. FR-013 Group Bookings
