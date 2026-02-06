#!/usr/bin/env python3
"""
Script to create GitHub issues for all feature requests from the docs.
Uses the GitHub REST API to create issues.

Requirements:
    pip install requests

Usage:
    export GITHUB_TOKEN="your_github_token_here"
    python3 create-feature-issues.py
"""

import os
import sys
import requests
from typing import List, Dict

REPO_OWNER = "nikola-golijanin"
REPO_NAME = "evently"
GITHUB_API_URL = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/issues"


def create_issue(title: str, body: str, labels: List[str], token: str) -> bool:
    """Create a single GitHub issue."""
    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28"
    }
    
    data = {
        "title": title,
        "body": body,
        "labels": labels
    }
    
    try:
        response = requests.post(GITHUB_API_URL, json=data, headers=headers)
        response.raise_for_status()
        print(f"✅ Created: {title}")
        return True
    except requests.exceptions.HTTPError as e:
        print(f"❌ Failed to create {title}: {e}")
        print(f"   Response: {response.text}")
        return False


def get_domain_feature_requests() -> List[Dict[str, str]]:
    """Return list of domain feature requests."""
    return [
        {
            "title": "FR-001: Notifications Module",
            "body": """## Problem
The system performs important actions (order confirmed, tickets issued, event canceled, event rescheduled) but never tells the user. There is no email, SMS, or push notification system.

## Domain Concept
A new **Notifications** bounded context that listens to integration events from all modules and dispatches user-facing messages through configurable channels (email, in-app, push).

## What should trigger notifications

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

## Module Structure
- Consumes integration events from all other modules
- Manages notification preferences per user (opt-in/out per channel)
- Tracks delivery status (sent, delivered, failed)
- Supports templates with variable substitution

**Priority:** 1 (Core Missing Feature)  
**Affected modules:** None changed. New module added.

See: docs/07-Feature-Requests.md"""
        },
        {
            "title": "FR-002: Waitlist When Sold Out",
            "body": """## Problem
When a ticket type sells out, `TicketTypeSoldOutIntegrationEvent` fires but **nothing consumes it**. Customers who arrive late have no way to express interest.

## Domain Concept
When a ticket type is sold out, customers can join a waitlist. When inventory becomes available (ticket archived, capacity increased, order canceled), the system automatically offers tickets to waitlisted customers in FIFO order.

## Entities
- **WaitlistEntry** -- (CustomerId, TicketTypeId, EventId, Quantity, RequestedAtUtc, Status, OfferedAtUtc, ExpiresAtUtc)
- Status: `Waiting -> Offered -> Converted | Expired | Canceled`

## Business Rules
1. Customer can only join waitlist when `AvailableQuantity == 0`
2. One waitlist entry per customer per ticket type
3. When inventory becomes available, offer to next customer in queue
4. Offered customer has a time window (e.g., 30 minutes) to complete purchase
5. If offer expires, move to next customer in queue
6. Customer can cancel their waitlist position at any time

## New Endpoints
- `POST /waitlist` -- Join waitlist for a ticket type
- `GET /waitlist` -- Get customer's waitlist entries
- `DELETE /waitlist/{id}` -- Cancel waitlist entry
- `POST /waitlist/{id}/accept` -- Accept a waitlist offer (creates order)

**Priority:** 1 (Core Missing Feature)  
**Where it lives:** Ticketing module

See: docs/07-Feature-Requests.md"""
        },
        {
            "title": "FR-003: User-Initiated Refund Requests",
            "body": """## Problem
Refund capability exists internally (`RefundPaymentCommand`, `Payment.Refund()`) but is only triggered by event cancellation via the saga. There is no way for a customer to request a refund themselves.

## Domain Concept
Customers can request refunds for their orders, subject to configurable refund policies. The system evaluates the request against the policy and either auto-approves, partially approves, or requires organizer review.

## Entities
- **RefundPolicy** -- (EventId, FullRefundBeforeDays, PartialRefundBeforeDays, PartialRefundPercentage, NonRefundableAfterDays)
- **RefundRequest** -- (OrderId, CustomerId, Reason, RequestedAtUtc, Status, ReviewedAtUtc, RefundAmount)
- Status: `Pending -> Approved | PartiallyApproved | Denied | Processed`

## Business Rules
1. Default refund policy: full refund up to 7 days before event, 50% up to 48 hours, non-refundable after
2. Organizers can customize refund policy per event
3. Ticket types can be marked as non-refundable (e.g., discounted tickets)
4. Refund auto-approves if within full-refund window
5. Partial refunds calculated based on policy
6. Refund triggers existing `Payment.Refund()` domain logic
7. Refunded tickets are archived

## New Endpoints
- `POST /refunds` -- Request refund for an order
- `GET /refunds` -- Get customer's refund requests
- `GET /refunds/{id}` -- Get refund request details

**Priority:** 1 (Core Missing Feature)  
**Affected modules:** Ticketing (new entities + endpoints), Events (RefundPolicy on Event or TicketType)

See: docs/07-Feature-Requests.md"""
        },
        {
            "title": "FR-004: Discount & Promo Codes",
            "body": """## Problem
There is no discount mechanism. Every ticket purchase is at full price. Organizers cannot run promotions, early-bird pricing, or partner discounts.

## Domain Concept
Organizers create promo codes tied to events. Customers apply codes at checkout for percentage or fixed-amount discounts. Codes have usage limits, expiration dates, and optional restrictions.

## Entities
- **PromoCode** -- (Code, EventId, DiscountType, DiscountValue, MaxUses, CurrentUses, ValidFrom, ValidUntil, MinQuantity, ApplicableTicketTypeIds, IsActive)
- DiscountType: `Percentage | FixedAmount`
- **OrderDiscount** -- (OrderId, PromoCodeId, DiscountAmount)

## Business Rules
1. Promo code is unique per event
2. Code must be within valid date range
3. Code must not exceed max usage count
4. Percentage discount applies to each line item proportionally
5. Fixed amount discount applies to order total
6. Discount cannot reduce price below zero
7. Multiple codes per order: organizer-configurable (default: one code per order)
8. Discount tracked on OrderItem level for accurate refund calculations

## New Endpoints
- `POST /promo-codes` -- Create promo code (organizer)
- `GET /promo-codes?eventId=` -- List promo codes for event (organizer)
- `PUT /promo-codes/{id}/deactivate` -- Deactivate code
- `POST /promo-codes/validate` -- Validate code before checkout (customer)
- `PUT /carts/apply-promo` -- Apply promo code to cart

**Priority:** 1 (Core Missing Feature)  
**Where it lives:** Ticketing module

See: docs/07-Feature-Requests.md"""
        },
        {
            "title": "FR-005: Event Organizer Ownership",
            "body": """## Problem
Events have no owner. The `Event` entity has no `OrganizerId` or `CreatedBy` field. Any user with the `ModifyEvents` permission can edit or cancel any event. There is no concept of "my events" for organizers.

## Domain Concept
Events are owned by the user who creates them (the organizer). Only the organizer (or administrators) can modify, publish, or cancel their events. Organizers can view analytics for their events.

## Changes Needed
- `Event` entity gets `OrganizerId` (Guid) property
- `CreateEventCommand` captures the current user as organizer
- All event mutation endpoints check `event.OrganizerId == currentUserId || isAdmin`
- New query: "Get my events" filtered by organizer

## Business Rules
1. The user who creates an event becomes its organizer
2. Only the organizer or an admin can publish, reschedule, or cancel the event
3. Organizers can view orders and attendance stats for their events
4. Future: organizer can delegate management to other users

## New Endpoints
- `GET /events/mine` -- Get events created by the current user

**Priority:** 1 (Core Missing Feature)  
**Affected modules:** Events (Event entity change), Users (possible Organizer role or ownership-based policies)

See: docs/07-Feature-Requests.md"""
        },
        {
            "title": "FR-006: Cart Inventory Reservation",
            "body": """## Problem
The cart is a Redis-based wish list with a 20-minute TTL, but it does **not reserve inventory**. Two customers can add the last ticket to their carts simultaneously, and one will fail at checkout.

## Domain Concept
When a customer adds tickets to their cart, the system temporarily holds (reserves) the inventory. Reserved tickets are unavailable to other customers. If the cart expires or is cleared, the reservation is released.

## New Entity
- **InventoryReservation** -- (CustomerId, TicketTypeId, Quantity, ReservedAtUtc, ExpiresAtUtc)

## Changes Needed
- `AddItemToCartCommand` decrements `AvailableQuantity` and records a reservation
- `RemoveItemFromCartCommand` / `ClearCartCommand` releases reservation (increments back)
- Cart expiration (Redis TTL) triggers reservation release
- `CreateOrderCommand` converts reservation into permanent purchase (no double-decrement)

## Business Rules
1. Reservation lasts 20 minutes (matches cart TTL)
2. Reservation auto-releases on expiry via background job
3. Order creation consumes reservation (no additional quantity check needed)
4. If reservation expired before checkout, order creation falls back to standard pessimistic lock flow

**Priority:** 2 (Important Enhancement)  
**Affected modules:** Ticketing (cart service, ticket type repository, new background job)

See: docs/07-Feature-Requests.md"""
        },
        {
            "title": "FR-007: Ticket Transfers",
            "body": """## Problem
Tickets are permanently bound to the customer who purchased them. If you buy a ticket and can't attend, you can't give it to a friend.

## Domain Concept
A ticket holder can transfer a ticket to another registered user. The transfer invalidates the original ticket code and generates a new one for the recipient. Transfer history is maintained for audit purposes.

## Entities
- **TicketTransfer** -- (TicketId, FromCustomerId, ToCustomerId, InitiatedAtUtc, CompletedAtUtc, Status)
- Status: `Pending -> Accepted | Declined | Canceled`

## Business Rules
1. Only the ticket owner can initiate a transfer
2. Recipient must be a registered user
3. Ticket cannot be transferred if already used (checked in)
4. Ticket cannot be transferred if event is canceled
5. Transfer generates a new ticket code (old one invalidated)
6. Pending transfers can be canceled by the sender
7. Recipient must explicitly accept the transfer
8. Transfer updates the Attendance module's ticket record (new AttendeeId)

## New Endpoints
- `POST /tickets/{id}/transfer` -- Initiate transfer (sender)
- `POST /transfers/{id}/accept` -- Accept transfer (recipient)
- `POST /transfers/{id}/decline` -- Decline transfer (recipient)
- `GET /transfers` -- Get transfer history

**Priority:** 2 (Important Enhancement)  
**Affected modules:** Ticketing (Ticket entity, new TicketTransfer entity), Attendance (update ticket ownership)

See: docs/07-Feature-Requests.md"""
        },
        {
            "title": "FR-008: Event Reviews & Ratings",
            "body": """## Problem
After an event ends, there is no feedback mechanism. Organizers don't know how the event was perceived. Future attendees can't evaluate events based on past reviews.

## Domain Concept
After an event ends, attendees who checked in can leave a review with a rating and optional text. Reviews are tied to the Attendance module since only verified attendees can review.

## Entities
- **Review** -- (EventId, AttendeeId, Rating (1-5), Comment, CreatedAtUtc, UpdatedAtUtc)

## Business Rules
1. Only attendees who checked in can leave a review (verified attendance)
2. One review per attendee per event
3. Reviews can only be submitted after the event ends
4. Reviews can be edited within 30 days of the event
5. Rating is required (1-5 stars), comment is optional
6. Average rating aggregated into EventStatistics (new field)

## New Endpoints
- `POST /events/{id}/reviews` -- Submit review
- `PUT /events/{id}/reviews` -- Update review
- `GET /events/{id}/reviews` -- Get reviews for event (paginated)

**Priority:** 2 (Important Enhancement)  
**Where it lives:** Attendance module

See: docs/07-Feature-Requests.md"""
        },
        {
            "title": "FR-009: Recurring Events & Event Series",
            "body": """## Problem
Every event is a standalone instance. Organizers who run weekly meetups, monthly concerts, or annual conferences must create each occurrence manually.

## Domain Concept
An event can belong to a series. A series defines a recurrence pattern (weekly, biweekly, monthly) and template properties. Individual occurrences can be generated from the series and then customized.

## Entities
- **EventSeries** -- (Title, Description, Location, CategoryId, RecurrencePattern, StartsAtUtc, EndsAtUtc)
- RecurrencePattern: `{ Type: Weekly|Biweekly|Monthly, DayOfWeek?, DayOfMonth?, Count? }`
- Events get optional `SeriesId` (Guid?) to link to parent series

## Business Rules
1. Creating a series generates N future event drafts based on recurrence pattern
2. Each occurrence is a full Event entity that can be individually modified
3. Series-level changes can propagate to future unmodified occurrences
4. Canceling a series cancels all future unpublished occurrences
5. Individual occurrences can be detached from the series

## New Endpoints
- `POST /event-series` -- Create series with recurrence pattern
- `GET /event-series/{id}` -- Get series with all occurrences
- `PUT /event-series/{id}` -- Update series (propagate to future events)
- `DELETE /event-series/{id}` -- Cancel series

**Priority:** 2 (Important Enhancement)  
**Where it lives:** Events module

See: docs/07-Feature-Requests.md"""
        },
        {
            "title": "FR-010: Venue Management",
            "body": """## Problem
Location is a free-text string field on the Event entity. No structured venue data, no reusability across events, no capacity tracking.

## Domain Concept
Venues are first-class entities with structured address data, capacity limits, and reusability across events. Events reference a venue instead of storing a raw location string.

## Entities
- **Venue** -- (Name, Address, City, Country, Capacity, Description, Coordinates?)

## Business Rules
1. Events reference a Venue by ID (optional -- free-text location still supported for backward compatibility)
2. Venue capacity serves as an upper bound for total ticket quantity across all ticket types
3. Venues are reusable across events
4. Publishing an event validates total ticket quantity does not exceed venue capacity

## New Endpoints
- `POST /venues` -- Create venue
- `GET /venues` -- List venues (with search)
- `GET /venues/{id}` -- Get venue details
- `PUT /venues/{id}` -- Update venue

**Priority:** 3 (Advanced Feature)  
**Where it lives:** Events module

See: docs/07-Feature-Requests.md"""
        },
        {
            "title": "FR-011: Seating & Sections",
            "body": """## Problem
All tickets are general admission. No concept of seats, rows, sections, or zones. For concerts, theaters, and conferences with assigned seating, this is essential.

## Domain Concept
Venues have sections (e.g., "Floor", "Balcony", "VIP Area"). Sections have rows and seats. Ticket types can be linked to sections. When purchasing, customers select specific seats or get auto-assigned.

## Entities
- **Section** -- (VenueId, Name, Capacity)
- **Seat** -- (SectionId, Row, Number, Status)
- Seat Status: `Available | Reserved | Sold | Blocked`

## Business Rules
1. Ticket types optionally map to sections
2. Seat selection happens during cart add or checkout
3. Selected seats are reserved (ties into FR-006 Cart Reservation)
4. Seat map can be returned via API for frontend rendering

**Priority:** 3 (Advanced Feature)  
**Depends on:** FR-010 (Venue Management), FR-006 (Cart Reservation)

See: docs/07-Feature-Requests.md"""
        },
        {
            "title": "FR-012: Analytics & Reporting",
            "body": """## Problem
Only basic attendance statistics exist (tickets sold, checked in, duplicates). No revenue reporting, sales trends, conversion rates, or organizer dashboards.

## Domain Concept
Aggregate reporting across events owned by an organizer. Revenue breakdowns, sales velocity, attendance rates, and trends over time.

## Reports
- Revenue per event (total sales, refunds, net revenue)
- Sales velocity (tickets sold per day leading up to event)
- Conversion rate (views -> cart -> purchase)
- Attendance rate (tickets sold vs checked in)
- Category performance (which categories sell best)
- Promo code effectiveness (usage, revenue impact)

**Implementation approach:** Read model projections (same CQRS pattern as EventStatistics). Background jobs aggregate data from existing domain events.

**Priority:** 3 (Advanced Feature)  
**Depends on:** FR-005 (Event Organizer -- to scope reports to "my events")

See: docs/07-Feature-Requests.md"""
        },
        {
            "title": "FR-013: Group Bookings",
            "body": """## Problem
Each order is for one customer. There's no concept of group reservations where an organizer, company, or group leader books tickets for multiple people with a single transaction.

## Domain Concept
A group booking allows purchasing multiple tickets and assigning them to different attendees. The group leader manages the booking and can assign/reassign names to tickets.

## Entities
- **GroupBooking** -- (LeaderCustomerId, EventId, GroupName, Tickets[])
- Each ticket in the group gets an `AssignedTo` field (name/email)

## Business Rules
1. Group leader purchases N tickets in one order
2. Tickets initially unassigned (leader's name)
3. Leader can assign attendee details to each ticket before the event
4. Each assigned ticket generates a unique code for that attendee
5. Minimum group size configurable per event (e.g., 10+)
6. Optional group discount (ties into FR-004 Promo Codes)

## New Endpoints
- `POST /group-bookings` -- Create group booking
- `PUT /group-bookings/{id}/assign` -- Assign attendees to tickets
- `GET /group-bookings/{id}` -- Get group booking details

**Priority:** 3 (Advanced Feature)  
**Where it lives:** Ticketing module

See: docs/07-Feature-Requests.md"""
        }
    ]


def get_technical_feature_requests() -> List[Dict[str, str]]:
    """Return list of technical feature requests."""
    return [
        {
            "title": "TFR-001: Replace Keycloak with ASP.NET Identity",
            "body": """## Problem
Keycloak adds significant operational complexity. The entire integration boils down to user creation and JWT issuance, while authorization is already fully local (PostgreSQL).

## Proposal
Replace Keycloak with ASP.NET Core Identity + local JWT issuance.

## Why This is a Clean Swap
- `IIdentityProviderService` is already a clean abstraction
- `CustomClaimsTransformation` stays exactly the same
- Permission model (roles, permissions, role_permissions tables) stays exactly the same
- Only the Users module Infrastructure layer changes

## Benefits
- Removes Docker container dependency (800MB+ image)
- Removes Testcontainer in integration tests (~15-20s faster)
- Simplifies configuration
- Removes external service health check dependency

## What Changes

| Component | Current (Keycloak) | New (ASP.NET Identity) |
|-----------|-------------------|----------------------|
| User storage | Keycloak DB + local users table | Identity tables in users schema only |
| Password hashing | Keycloak | ASP.NET Identity (PBKDF2) |
| User registration | Keycloak Admin API call | `UserManager<T>.CreateAsync()` |
| Token issuance | Keycloak OIDC endpoint | Local JWT generation |
| Token validation | Keycloak OIDC metadata | Local key validation |
| Docker services | PostgreSQL + Redis + Keycloak | PostgreSQL + Redis |

## New Endpoints Needed
- `POST /users/login` -- Authenticate with email/password, return JWT + refresh token
- `POST /users/refresh` -- Refresh an expired JWT
- `POST /users/change-password`

**Priority:** 1 (High-Impact Improvement)  
**Risk:** Low. Clean abstraction boundary.

See: docs/08-Technical-Feature-Requests.md"""
        },
        {
            "title": "TFR-002: Stabilize MassTransit Version",
            "body": """## Problem
The project uses `MassTransit 9.0.1-develop.45` -- a **pre-release development build**. Pre-release packages risk breaking changes, missing documentation, and bugs.

Additionally, `Newtonsoft.Json 13.0.5-beta1` is also a beta version.

## Proposal
1. Upgrade to the latest stable MassTransit 9.x release
2. Upgrade Newtonsoft.Json to the latest stable
3. Verify the PostgreSQL transport and saga state machine still work correctly
4. Run all integration tests after upgrade

## What to Check After Upgrade
- `CancelEventSaga` state persistence still works
- `IntegrationEventConsumer<T>` still receives messages
- Outbox/inbox processing still functions
- MassTransit auto-migration still runs

**Priority:** 1 (High-Impact Improvement)  
**Risk:** Low-medium. Test thoroughly.

See: docs/08-Technical-Feature-Requests.md"""
        },
        {
            "title": "TFR-003: Add Resilience with Polly",
            "body": """## Problem
No resilience policies exist anywhere in the system. Database calls, Redis operations, HTTP calls, and MassTransit publishing all lack retry logic, circuit breakers, and timeout policies.

## Current Issues
- Redis connection failure is caught with a bare `catch` block -- no logging, no metrics
- Database transient errors cause immediate hard failures
- No circuit breaker to prevent cascading failures
- No timeout policies beyond default HTTP/DB timeouts

## Proposal
Add `Microsoft.Extensions.Http.Resilience` and `Microsoft.Extensions.Resilience` (built-in Polly v8 integration).

## Where to Add Resilience

| Component | Policy | Rationale |
|-----------|--------|-----------|
| Database calls | Retry (3x, exponential backoff) | Transient connection errors |
| Redis operations | Retry (2x) + Circuit breaker | Redis restart/network blip |
| MassTransit publishing | Retry (3x) | Message bus temporary unavailability |
| Keycloak/Identity HTTP calls | Retry + Timeout + Circuit breaker | External service dependency |
| Payment service | Retry + Timeout | External gateway |

**Priority:** 1 (High-Impact Improvement)

See: docs/08-Technical-Feature-Requests.md"""
        },
        {
            "title": "TFR-004: Expand Integration Test Coverage",
            "body": """## Problem
Only 2 integration test examples exist (`RegisterUser`, `AddItemToCart`). For a project with 31 endpoints and complex cross-module flows, this is minimal coverage.

## Proposal
Build out integration tests for critical paths.

## Priority Test Scenarios
1. Full event lifecycle: Create -> Add ticket types -> Publish -> Verify cross-module propagation
2. Full purchase flow: Register user -> Add to cart -> Create order -> Verify tickets generated
3. Event cancellation saga: Cancel event -> Verify payments refunded + tickets archived + saga completes
4. Check-in flow: Issue ticket -> Check in -> Verify statistics updated
5. Duplicate/invalid check-in: Verify correct error responses and statistics tracking
6. Concurrent order creation: Multiple users buying last tickets -> verify no overselling
7. Cart expiration: Add items -> Wait for TTL -> Verify cart empty

## Infrastructure Improvements
- Add a `BaseIntegrationTest` with common helpers
- Add test data builders using Bogus/Faker for all entities
- Add assertion helpers for cross-module event propagation
- Consider adding `Respawner` for fast database cleanup between tests

**Priority:** 1 (High-Impact Improvement)

See: docs/08-Technical-Feature-Requests.md"""
        },
        {
            "title": "TFR-005: Add Rate Limiting",
            "body": """## Problem
No rate limiting exists. Any client can flood the API with unlimited requests. Critical for:
- Login endpoint (brute force protection)
- Order creation (prevent abuse)
- Registration (prevent spam accounts)

## Proposal
Add ASP.NET Core's built-in rate limiting middleware (`Microsoft.AspNetCore.RateLimiting`).

## Rate Limiting Policies

| Policy | Applies to | Limit | Window |
|--------|-----------|-------|--------|
| `fixed-global` | All endpoints | 100 req | per minute per IP |
| `auth-strict` | `/users/login`, `/users/register` | 10 req | per minute per IP |
| `order-limit` | `POST /orders` | 5 req | per minute per user |
| `sliding-general` | All authenticated endpoints | 60 req | per minute per user |

**Priority:** 2 (API & Infrastructure Hardening)

See: docs/08-Technical-Feature-Requests.md"""
        },
        {
            "title": "TFR-006: Add API Versioning",
            "body": """## Problem
API is hardcoded to "v1" in Swagger with no versioning infrastructure. If we need to make breaking changes to endpoints, there's no mechanism to maintain backward compatibility.

## Proposal
Add `Asp.Versioning.Http` and `Asp.Versioning.Mvc.ApiExplorer` for URL-based API versioning.

## Approach
URL segment versioning (`/api/v1/events`, `/api/v2/events`).

## Implementation
1. Add versioning packages
2. Configure default version (1.0)
3. Add version groups to Swagger
4. Prefix all routes with `/api/v1/`
5. When v2 is needed, add versioned endpoint groups

**Priority:** 2 (API & Infrastructure Hardening)

See: docs/08-Technical-Feature-Requests.md"""
        },
        {
            "title": "TFR-007: Improve Health Checks",
            "body": """## Problem
Current health checks cover PostgreSQL, Redis, and Keycloak (URI check). Missing:
- MassTransit connectivity
- No liveness/readiness distinction (important for Kubernetes)
- No startup probe
- No detailed health check response

## Proposal

### Add Health Check Endpoints
- `GET /health/live` -- Liveness: app is running (always 200 unless crashed)
- `GET /health/ready` -- Readiness: app can serve traffic (all dependencies up)
- `GET /health/startup` -- Startup: app has finished initialization

### Add Missing Checks
- MassTransit bus health
- Outbox/inbox backlog check (alert if unprocessed messages > threshold)
- Quartz scheduler health

**Priority:** 2 (API & Infrastructure Hardening)  
**Note:** After TFR-001, remove Keycloak health check

See: docs/08-Technical-Feature-Requests.md"""
        },
        {
            "title": "TFR-008: Add Response Compression & CORS",
            "body": """## Problem
- No response compression -- JSON responses sent uncompressed
- No CORS configuration visible -- browsers may be blocked from calling the API

## Proposal

### Compression
Enable Brotli and Gzip compression for HTTPS responses.

### CORS
Configure CORS policy to allow frontend requests:
- Allow specific origins (e.g., `http://localhost:3000`)
- Allow any header and method
- Allow credentials

**Priority:** 2 (API & Infrastructure Hardening)

See: docs/08-Technical-Feature-Requests.md"""
        },
        {
            "title": "TFR-009: Persist Quartz Scheduler State",
            "body": """## Problem
Quartz scheduler uses in-memory job storage with a random GUID instance ID. If the application restarts:
- All scheduled jobs are lost
- Outbox/inbox processing timers reset
- No visibility into job execution history
- No cluster support (multiple instances would duplicate jobs)

## Proposal
Switch Quartz to persistent `AdoJobStore` backed by PostgreSQL.

## Benefits
- Jobs survive application restarts
- Job execution history available for debugging
- Cluster mode support (only one instance processes each job)
- Dashboard integration possible (Quartz.NET monitoring)

## Implementation
1. Add `Quartz.Serialization.Json` package
2. Configure `AdoJobStore` with PostgreSQL provider
3. Create Quartz schema tables (built-in migration script)
4. Use clustered mode with `InstanceId = "AUTO"`

**Priority:** 3 (Observability & Performance)

See: docs/08-Technical-Feature-Requests.md"""
        },
        {
            "title": "TFR-010: Multi-Level Caching (L1 + L2)",
            "body": """## Problem
Current caching is single-level (Redis or memory fallback). For frequently read data (event details, categories, permissions), every cache miss goes to Redis over the network.

## Proposal
Add in-memory L1 cache in front of Redis L2 cache.

## Pattern
```
Request -> L1 (IMemoryCache, in-process, ~1ms)
       -> L2 (Redis, network, ~5ms)
       -> Database (~20ms)
```

## What to Cache at L1
- User permissions (queried on every request via `CustomClaimsTransformation`)
- Category list (rarely changes)
- Event details for published events (immutable after publish)

## Cache Invalidation
Use Redis pub/sub to invalidate L1 across multiple instances when data changes.

## Implementation
Create `HybridCacheService` wrapping `IMemoryCache` + `IDistributedCache` with configurable L1 TTL.

**Priority:** 3 (Observability & Performance)

See: docs/08-Technical-Feature-Requests.md"""
        },
        {
            "title": "TFR-011: Structured Configuration Validation",
            "body": """## Problem
Configuration is loaded from multiple JSON files per module (`modules.users.json`, `modules.events.json`, etc.) with no validation. Missing or malformed config values fail at runtime, not startup.

## Proposal
Add `IValidateOptions<T>` implementations for all configuration sections.

## Validate at Startup
- Database connection string is not empty and is valid
- Redis connection string format
- JWT signing key present and minimum length
- Quartz cron expressions valid
- Module-specific settings complete

## Implementation
Use `OptionsBuilder<T>.ValidateOnStart()` pattern so the app fails fast with a clear error message.

**Priority:** 3 (Observability & Performance)

See: docs/08-Technical-Feature-Requests.md"""
        },
        {
            "title": "TFR-012: Enhance OpenAPI Documentation",
            "body": """## Problem
Swagger is minimal -- single version, no security definitions, no request/response examples, no error schema documentation. Endpoints show up but aren't documented enough for a consumer to use confidently.

## Proposal

1. **Add security scheme** -- Document Bearer token authentication in Swagger UI
2. **Add operation summaries** -- Brief descriptions on each endpoint
3. **Add response examples** -- Sample request/response bodies
4. **Add error responses** -- Document 400, 401, 403, 404, 409 responses per endpoint
5. **Group by module** -- Use tags matching module names
6. **Add Scalar or ReDoc** -- Modern alternative to Swagger UI (better DX)

## Implementation
Use `WithOpenApi()` extension on endpoints or XML doc comments with `Swashbuckle.AspNetCore.Annotations`.

**Priority:** 3 (Observability & Performance)

See: docs/08-Technical-Feature-Requests.md"""
        }
    ]


def main():
    """Main function to create all issues."""
    token = os.environ.get("GITHUB_TOKEN")
    if not token:
        print("❌ Error: GITHUB_TOKEN environment variable not set")
        print("   Please set it with: export GITHUB_TOKEN='your_token_here'")
        sys.exit(1)
    
    print(f"Creating GitHub issues for {REPO_OWNER}/{REPO_NAME}...")
    print()
    
    # Create domain feature requests
    print("📋 Creating domain feature requests (FR-001 to FR-013)...")
    domain_requests = get_domain_feature_requests()
    domain_success = 0
    for request in domain_requests:
        if create_issue(request["title"], request["body"], ["enhancement"], token):
            domain_success += 1
    
    print()
    
    # Create technical feature requests
    print("🔧 Creating technical feature requests (TFR-001 to TFR-012)...")
    technical_requests = get_technical_feature_requests()
    technical_success = 0
    for request in technical_requests:
        if create_issue(request["title"], request["body"], ["enhancement"], token):
            technical_success += 1
    
    print()
    print("=" * 70)
    print(f"✅ Successfully created {domain_success + technical_success} out of {len(domain_requests) + len(technical_requests)} issues")
    print(f"   - Domain feature requests: {domain_success}/{len(domain_requests)}")
    print(f"   - Technical feature requests: {technical_success}/{len(technical_requests)}")
    print("=" * 70)


if __name__ == "__main__":
    main()
