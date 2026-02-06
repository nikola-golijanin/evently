# Feature Requests Analysis & Issue Creation Summary

## Overview

I have analyzed the feature request documents in the `docs/` directory and prepared all content needed to create GitHub issues. 

## Feature Requests Found

### Domain Feature Requests (13 total)
From `docs/07-Feature-Requests.md`:

#### Priority 1: Core Missing Features (5)
1. **FR-001: Notifications Module** - System has no email/SMS/push notification capability
2. **FR-002: Waitlist When Sold Out** - No way for customers to join waitlist when tickets sell out
3. **FR-003: User-Initiated Refund Requests** - Customers cannot request refunds themselves
4. **FR-004: Discount & Promo Codes** - No promotional code or discount system
5. **FR-005: Event Organizer Ownership** - Events have no owner, anyone can edit any event

#### Priority 2: Important Enhancements (4)
6. **FR-006: Cart Inventory Reservation** - Cart doesn't reserve inventory, leading to race conditions
7. **FR-007: Ticket Transfers** - No way to transfer tickets to another person
8. **FR-008: Event Reviews & Ratings** - No feedback mechanism after events
9. **FR-009: Recurring Events & Event Series** - Must manually create each event occurrence

#### Priority 3: Advanced Features (4)
10. **FR-010: Venue Management** - Location is free-text, no structured venue data
11. **FR-011: Seating & Sections** - No assigned seating capability
12. **FR-012: Analytics & Reporting** - No revenue reporting or sales trends
13. **FR-013: Group Bookings** - No group reservation capability

### Technical Feature Requests (12 total)
From `docs/08-Technical-Feature-Requests.md`:

#### Priority 1: High-Impact Improvements (4)
1. **TFR-001: Replace Keycloak with ASP.NET Identity** - Reduce operational complexity
2. **TFR-002: Stabilize MassTransit Version** - Move from pre-release to stable version
3. **TFR-003: Add Resilience with Polly** - Add retry logic and circuit breakers
4. **TFR-004: Expand Integration Test Coverage** - Only 2 tests exist for 31 endpoints

#### Priority 2: API & Infrastructure Hardening (4)
5. **TFR-005: Add Rate Limiting** - No protection against API flooding
6. **TFR-006: Add API Versioning** - No mechanism for backward compatibility
7. **TFR-007: Improve Health Checks** - Missing liveness/readiness distinction
8. **TFR-008: Add Response Compression & CORS** - No response compression or CORS config

#### Priority 3: Observability & Performance (4)
9. **TFR-009: Persist Quartz Scheduler State** - In-memory storage loses jobs on restart
10. **TFR-010: Multi-Level Caching (L1 + L2)** - Single-level caching causes network overhead
11. **TFR-011: Structured Configuration Validation** - Config errors only caught at runtime
12. **TFR-012: Enhance OpenAPI Documentation** - Minimal Swagger documentation

## What I've Created

### 1. Bash Script: `create-feature-issues.sh`
A complete bash script using GitHub CLI (`gh`) that creates all 25 issues with:
- Proper titles (e.g., "FR-001: Notifications Module")
- Complete issue bodies with problem statements, domain concepts, and implementation details
- "enhancement" label on all issues
- References back to source documentation

### 2. Python Script: `create-feature-issues.py`
A Python alternative using the GitHub REST API for environments where gh CLI isn't available or preferred:
- Requires `requests` library (already available in this environment)
- Uses GITHUB_TOKEN environment variable for authentication
- Creates the same 25 issues as the bash script
- Provides progress feedback during execution

### 3. Documentation: `FEATURE_ISSUES_README.md`
Comprehensive guide with:
- Overview of all feature requests
- Three options for creating issues (bash, Python, or manual)
- Prerequisites and step-by-step instructions
- Summary of what gets created

## How to Execute

### Option 1: Using Bash Script (Recommended if gh CLI is available)
```bash
# Authenticate with GitHub
gh auth login

# Run the script
./create-feature-issues.sh
```

### Option 2: Using Python Script
```bash
# Set GitHub token
export GITHUB_TOKEN="your_personal_access_token"

# Run the script
python3 create-feature-issues.py
```

### Option 3: Run Bash Script with Token
```bash
# Set token for gh CLI
export GH_TOKEN="your_personal_access_token"

# Run the script
./create-feature-issues.sh
```

## Expected Outcome

After running either script, you will have:
- **25 new GitHub issues** in the nikola-golijanin/evently repository
- All issues labeled with **"enhancement"**
- Issues numbered and titled clearly (FR-001 through FR-013, TFR-001 through TFR-012)
- Complete descriptions with:
  - Problem statement
  - Domain concept or technical proposal
  - Business rules or implementation details
  - Priority level
  - Dependencies (where applicable)
  - Reference to source documentation

## Notes

### Why I Created Scripts Instead of Running Them Directly

According to my operational constraints, I cannot directly create GitHub issues through git/gh commands or the GitHub API. However, I can:
- Analyze the documentation (✅ completed)
- Extract all feature requests (✅ completed)  
- Create scripts that can be executed (✅ completed)
- Provide comprehensive documentation (✅ completed)

The scripts are ready to run and will create all issues as soon as they're executed with proper authentication.

### Authentication Requirements

To create issues, you need a GitHub token with `repo` scope (or at minimum, `public_repo` if this is a public repository). You can:
1. Use `gh auth login` to authenticate interactively
2. Create a Personal Access Token at https://github.com/settings/tokens
3. Use an existing GitHub Actions token if running in CI/CD

## Verification

After running the scripts, you can verify all issues were created by:
1. Visiting https://github.com/nikola-golijanin/evently/issues
2. Filtering by label: `enhancement`
3. Checking that all 25 issues (FR-001 through FR-013, TFR-001 through TFR-012) exist

The scripts provide feedback during execution showing which issues were successfully created.
