# Creating Feature Request Issues

This directory contains scripts and documentation for creating GitHub issues from the feature requests documented in the `docs/` folder.

## Overview

The `docs/` folder contains:
- **07-Feature-Requests.md**: 13 domain feature requests (FR-001 to FR-013)
- **08-Technical-Feature-Requests.md**: 12 technical feature requests (TFR-001 to TFR-012)

All of these should be created as GitHub issues labeled as "enhancement".

## Option 1: Using the Bash Script (Recommended)

### Prerequisites
- GitHub CLI (`gh`) must be installed
- You must be authenticated with GitHub

### Steps

1. **Authenticate with GitHub CLI:**
   ```bash
   gh auth login
   ```

2. **Run the script:**
   ```bash
   ./create-feature-issues.sh
   ```

This will create all 25 issues (13 domain + 12 technical) in the repository.

## Option 2: Using the Python Script

If you prefer Python or don't have the GitHub CLI:

### Prerequisites
- Python 3.7+
- GitHub Personal Access Token with `repo` scope

### Steps

1. **Set your GitHub token:**
   ```bash
   export GITHUB_TOKEN="your_github_token_here"
   ```

2. **Run the script:**
   ```bash
   python3 create-feature-issues.py
   ```

## Option 3: Manual Creation

If you prefer to create issues manually, refer to `MANUAL_ISSUE_CREATION.md` which contains formatted issue content ready to copy-paste.

## What Gets Created

### Domain Feature Requests (Priority 1 - Core Missing Features)
1. **FR-001**: Notifications Module
2. **FR-002**: Waitlist When Sold Out
3. **FR-003**: User-Initiated Refund Requests
4. **FR-004**: Discount & Promo Codes
5. **FR-005**: Event Organizer Ownership

### Domain Feature Requests (Priority 2 - Important Enhancements)
6. **FR-006**: Cart Inventory Reservation
7. **FR-007**: Ticket Transfers
8. **FR-008**: Event Reviews & Ratings
9. **FR-009**: Recurring Events & Event Series

### Domain Feature Requests (Priority 3 - Advanced Features)
10. **FR-010**: Venue Management
11. **FR-011**: Seating & Sections
12. **FR-012**: Analytics & Reporting
13. **FR-013**: Group Bookings

### Technical Feature Requests (Priority 1 - High-Impact)
1. **TFR-001**: Replace Keycloak with ASP.NET Identity
2. **TFR-002**: Stabilize MassTransit Version
3. **TFR-003**: Add Resilience with Polly
4. **TFR-004**: Expand Integration Test Coverage

### Technical Feature Requests (Priority 2 - API & Infrastructure)
5. **TFR-005**: Add Rate Limiting
6. **TFR-006**: Add API Versioning
7. **TFR-007**: Improve Health Checks
8. **TFR-008**: Add Response Compression & CORS

### Technical Feature Requests (Priority 3 - Observability & Performance)
9. **TFR-009**: Persist Quartz Scheduler State
10. **TFR-010**: Multi-Level Caching (L1 + L2)
11. **TFR-011**: Structured Configuration Validation
12. **TFR-012**: Enhance OpenAPI Documentation

## Notes

- All issues are labeled as **"enhancement"**
- Each issue includes:
  - Clear problem statement
  - Domain concept or technical proposal
  - Implementation details
  - Priority level
  - Reference to source documentation
