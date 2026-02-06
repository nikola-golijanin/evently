# Quick Start: Creating Feature Request Issues

## TL;DR - Run This Now

```bash
# Option 1: Using GitHub CLI (recommended)
gh auth login
./create-feature-issues.sh

# Option 2: Using Python with token
export GITHUB_TOKEN="ghp_your_token_here"
python3 create-feature-issues.py
```

## What Gets Created

✅ **25 GitHub Issues** will be created:

### Domain Features (13)
- FR-001: Notifications Module
- FR-002: Waitlist When Sold Out  
- FR-003: User-Initiated Refund Requests
- FR-004: Discount & Promo Codes
- FR-005: Event Organizer Ownership
- FR-006: Cart Inventory Reservation
- FR-007: Ticket Transfers
- FR-008: Event Reviews & Ratings
- FR-009: Recurring Events & Event Series
- FR-010: Venue Management
- FR-011: Seating & Sections
- FR-012: Analytics & Reporting
- FR-013: Group Bookings

### Technical Features (12)
- TFR-001: Replace Keycloak with ASP.NET Identity
- TFR-002: Stabilize MassTransit Version
- TFR-003: Add Resilience with Polly
- TFR-004: Expand Integration Test Coverage
- TFR-005: Add Rate Limiting
- TFR-006: Add API Versioning
- TFR-007: Improve Health Checks
- TFR-008: Add Response Compression & CORS
- TFR-009: Persist Quartz Scheduler State
- TFR-010: Multi-Level Caching (L1 + L2)
- TFR-011: Structured Configuration Validation
- TFR-012: Enhance OpenAPI Documentation

All issues labeled with: **enhancement**

## Getting a GitHub Token

If you need a token:
1. Go to https://github.com/settings/tokens
2. Click "Generate new token (classic)"
3. Select scope: `repo` (or `public_repo` for public repos only)
4. Copy the token
5. Export it: `export GITHUB_TOKEN="your_token"`

## Files Provided

- `create-feature-issues.sh` - Bash script using gh CLI
- `create-feature-issues.py` - Python script using GitHub API
- `FEATURE_ISSUES_README.md` - Detailed documentation
- `FEATURE_ISSUES_ANALYSIS.md` - Complete analysis of all features
- `QUICKSTART.md` - This file

## Verification

After running, check:
```bash
gh issue list --label enhancement --limit 30
```

Or visit: https://github.com/nikola-golijanin/evently/issues?q=is%3Aissue+is%3Aopen+label%3Aenhancement
