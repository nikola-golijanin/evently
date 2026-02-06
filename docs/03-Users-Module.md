# Users Module - Technical Documentation

## Table of Contents

- [Overview](#overview)
- [1. Layer Structure](#1-layer-structure)
- [2. Domain Layer](#2-domain-layer)
  - [2.1 Entities & Aggregates](#21-entities--aggregates)
  - [2.2 Value Objects](#22-value-objects)
  - [2.3 Domain Events](#23-domain-events)
  - [2.4 Repository Interfaces](#24-repository-interfaces)
  - [2.5 Domain Errors](#25-domain-errors)
- [3. Application Layer](#3-application-layer)
  - [3.1 Commands](#31-commands)
  - [3.2 Queries](#32-queries)
  - [3.3 Domain Event Handlers](#33-domain-event-handlers)
- [4. Infrastructure Layer](#4-infrastructure-layer)
- [5. Presentation Layer (API Endpoints)](#5-presentation-layer-api-endpoints)
- [6. Integration Events (Public Contracts)](#6-integration-events-public-contracts)
- [7. Data Flow Diagram](#7-data-flow-diagram)
- [8. Role-Permission Seed Data](#8-role-permission-seed-data)

---

## Overview

The Users module manages **identity, authentication, and authorization**. It is the authority for user accounts, roles, and permissions. It integrates with Keycloak as the external identity provider and broadcasts user lifecycle events to all other modules.

**Namespace:** `Evently.Modules.Users`
**Database Schema:** `users`

---

## 1. Layer Structure

```
Evently.Modules.Users.Domain/              -- User entity, Role/Permission value objects, errors
Evently.Modules.Users.Application/         -- Commands, queries, handlers, validators
Evently.Modules.Users.Infrastructure/      -- EF Core, Keycloak integration, repositories
Evently.Modules.Users.Presentation/        -- Minimal API endpoints
Evently.Modules.Users.IntegrationEvents/   -- Public event contracts
```

---

## 2. Domain Layer

### 2.1 Entities & Aggregates

#### User (Aggregate Root)
**File:** `Domain/Users/User.cs`

| Property | Type | Description |
|----------|------|-------------|
| Id | Guid | Primary key |
| Email | string | User email address |
| FirstName | string | First name |
| LastName | string | Last name |
| IdentityId | string | External identity provider ID (Keycloak sub) |
| Roles | IReadOnlyCollection\<Role\> | Assigned roles |

**Methods:**
- `static Create(email, firstName, lastName, identityId)` -- Factory. Assigns `Member` role by default. Raises `UserRegisteredDomainEvent`.
- `Update(firstName, lastName)` -- Updates profile if values changed. Raises `UserProfileUpdatedDomainEvent`.

### 2.2 Value Objects

#### Role
**File:** `Domain/Users/Role.cs`

| Property | Type | Description |
|----------|------|-------------|
| Name | string | Role identifier |

**Predefined Instances:**
- `Role.Administrator` -- "Administrator"
- `Role.Member` -- "Member"

#### Permission
**File:** `Domain/Users/Permission.cs`

| Property | Type | Description |
|----------|------|-------------|
| Code | string | Permission code (e.g., "events:read") |

**All 17 Permissions:**

| Permission Constant | Code | Description |
|---------------------|------|-------------|
| GetUser | users:read | View user profile |
| ModifyUser | users:update | Update user profile |
| GetEvents | events:read | View events |
| SearchEvents | events:search | Search events |
| ModifyEvents | events:update | Create/publish/reschedule/cancel events |
| GetTicketTypes | ticket-types:read | View ticket types |
| ModifyTicketTypes | ticket-types:update | Create/update ticket types |
| GetCategories | categories:read | View categories |
| ModifyCategories | categories:update | Create/update/archive categories |
| GetCart | carts:read | View shopping cart |
| AddToCart | carts:add | Add items to cart |
| RemoveFromCart | carts:remove | Remove items from cart |
| GetOrders | orders:read | View orders |
| CreateOrder | orders:create | Place orders |
| GetTickets | tickets:read | View tickets |
| CheckInTicket | tickets:check-in | Check in at events |
| GetEventStatistics | event-statistics:read | View attendance statistics |

### 2.3 Domain Events

| Event | Properties | When Raised |
|-------|-----------|-------------|
| UserRegisteredDomainEvent | UserId | User.Create() |
| UserProfileUpdatedDomainEvent | UserId, FirstName, LastName | User.Update() |

### 2.4 Repository Interfaces

**IUserRepository**
- `Task<User?> GetAsync(Guid id, CancellationToken ct)` -- By user ID
- `void Insert(User user)`

### 2.5 Domain Errors

**UserErrors:**
- `NotFound(Guid userId)` -- User not found by internal ID
- `NotFound(string identityId)` -- User not found by identity provider ID

---

## 3. Application Layer

### 3.1 Commands

#### RegisterUserCommand
**Handler:** `RegisterUserCommandHandler`
```
Input:  Email, Password, FirstName, LastName
Output: Result<Guid>
```
1. Calls `IIdentityProviderService.RegisterUserAsync(email, password, firstName, lastName)`
   - This creates the user in Keycloak and returns the identity provider ID
2. Creates `User.Create(email, firstName, lastName, identityId)` -- assigns Member role
3. Inserts user via repository
4. Saves changes (outbox captures `UserRegisteredDomainEvent`)

**Validator:**
- FirstName: NotEmpty
- LastName: NotEmpty
- Email: Must be valid email address format
- Password: Minimum length of 6

**Error Handling:** If Keycloak registration fails, the command returns a failure result.

#### UpdateUserCommand
**Handler:** `UpdateUserCommandHandler`
```
Input:  UserId, FirstName, LastName
Output: Result
```
1. Fetches user (error if not found)
2. Calls `user.Update(firstName, lastName)` -- only raises event if values actually changed
3. Saves changes

**Validator:**
- UserId: NotEmpty
- FirstName: NotEmpty
- LastName: NotEmpty

### 3.2 Queries

#### GetUserQuery
```
Input:  UserId (Guid)
Output: Result<UserResponse>
```
**Implementation:** Dapper query from `users.users` table.
**Response DTO:** `UserResponse(Id, Email, FirstName, LastName)`

#### GetUserPermissionsQuery
```
Input:  IdentityId (string)
Output: Result<PermissionsResponse>
```
**Implementation:** Joins users -> user_roles -> roles -> role_permissions -> permissions tables.
**Response DTO:** `PermissionsResponse(UserId, Permissions[])` -- array of permission codes.

**Usage:** Called by the authorization middleware to resolve claims from the bearer token's `sub` claim into application permissions.

### 3.3 Domain Event Handlers

#### UserRegisteredDomainEventHandler
1. Queries user via `GetUserQuery` to enrich with full profile
2. Publishes `UserRegisteredIntegrationEvent(UserId, Email, FirstName, LastName)`
3. **Consumed by:**
   - Ticketing module -> `CreateCustomerCommand`
   - Attendance module -> `CreateAttendeeCommand`

#### UserProfileUpdatedDomainEventHandler
1. Publishes `UserProfileUpdatedIntegrationEvent(UserId, FirstName, LastName)`
2. **Consumed by:**
   - Ticketing module -> `UpdateCustomerCommand`
   - Attendance module -> `UpdateAttendeeCommand`

---

## 4. Infrastructure Layer

### 4.1 Database Context
**UsersDbContext** -- EF Core DbContext with schema `users`

**Tables:**
- `users.users` -- User aggregate
- `users.roles` -- Role definitions (seeded data)
- `users.permissions` -- Permission definitions (seeded data)
- `users.user_roles` -- Many-to-many: Users <-> Roles
- `users.role_permissions` -- Many-to-many: Roles <-> Permissions
- `users.outbox_messages` -- Outbox pattern
- `users.inbox_messages` -- Inbox pattern
- `users.outbox_message_consumers` -- Idempotent tracking
- `users.inbox_message_consumers` -- Idempotent tracking

### 4.2 Identity Provider Integration
**IIdentityProviderService** -- Abstraction over Keycloak.

**Implementation:** `KeycloakIdentityProviderService`
- Uses Keycloak Admin REST API
- Creates users with email + password
- Returns the Keycloak user ID (`sub` claim)

### 4.3 Authorization Infrastructure
**CustomClaimsTransformation** -- ASP.NET Core `IClaimsTransformation` implementation:
1. Extracts `sub` claim from bearer token (Keycloak JWT)
2. Queries `GetUserPermissionsQuery` to resolve permissions
3. Adds permission claims to the `ClaimsPrincipal`
4. These claims are then matched against `RequireAuthorization("permission-code")` on endpoints

### 4.4 Repositories
- `UserRepository` -- EF Core implementation with `Include(u => u.Roles)` for role loading

### 4.5 Module Registration
**UsersModule.cs** -- Implements `IModule`:
- Registers DbContext with PostgreSQL
- Registers identity provider service
- Registers claims transformation
- Configures MassTransit consumers (none currently -- Users only publishes)
- Registers Quartz jobs

---

## 5. Presentation Layer (API Endpoints)

| Method | Route | Permission | Handler | Notes |
|--------|-------|------------|---------|-------|
| POST | `/users/register` | Anonymous | RegisterUserCommand | No auth required |
| GET | `/users/profile` | GetUser | GetUserQuery | Extracts UserId from claims |
| PUT | `/users/profile` | ModifyUser | UpdateUserCommand | Extracts UserId from claims |

**Total: 3 endpoints**

### Authentication Flow

```
1. User registers via POST /users/register (anonymous)
2. Keycloak issues JWT bearer tokens via OAuth2/OIDC
3. User authenticates with Keycloak directly (not through this API)
4. Bearer token included in Authorization header for subsequent requests
5. CustomClaimsTransformation resolves sub -> permissions on each request
6. RequireAuthorization("permission-code") on each endpoint enforces access
```

---

## 6. Integration Events (Public Contracts)

**Namespace:** `Evently.Modules.Users.IntegrationEvents`

| Event | Properties |
|-------|-----------|
| UserRegisteredIntegrationEvent | Id, OccurredOnUtc, UserId, Email, FirstName, LastName |
| UserProfileUpdatedIntegrationEvent | Id, OccurredOnUtc, UserId, FirstName, LastName |

---

## 7. Data Flow Diagram

```
                    +------------------+
                    |    Keycloak      |
                    | (Identity Provider)|
                    +--------+---------+
                             |
                    RegisterUserAsync()
                             |
                             v
+-------------------+    +-------------------+
| POST /users/register | -> | RegisterUserCommandHandler |
+-------------------+    +-------------------+
                             |
                    1. Create in Keycloak
                    2. Create User aggregate (with Member role)
                    3. SaveChanges() -> Outbox
                             |
                             v
                    +-------------------+
                    | UserRegistered    |
                    | DomainEvent       |
                    +-------------------+
                             |
                    ProcessOutboxJob
                             |
                             v
                    +-------------------+
                    | UserRegistered    |
                    | IntegrationEvent  |
                    +-------------------+
                           /    \
                          /      \
                         v        v
              +-----------+    +-----------+
              | Ticketing |    | Attendance|
              | Creates   |    | Creates   |
              | Customer  |    | Attendee  |
              +-----------+    +-----------+
```

---

## 8. Role-Permission Seed Data

The roles and permissions are seeded at application startup:

```
Role: Administrator
  Permissions: ALL 17 permissions

Role: Member
  Permissions: ALL 17 permissions (currently same as Admin)
```

Note: Both roles currently have the same permissions. The distinction exists to support future permission differentiation where Administrators might have additional management capabilities.
