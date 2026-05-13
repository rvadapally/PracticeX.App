# QGenda Lite Standalone Scheduler

Status: draft research and product spec

Owner: product and engineering

Related issue: #13

Last updated: 2026-05-13

## 1. Why this exists

PracticeX already has an explicit roadmap bet on a contract-aware scheduling moat. The open question is whether that moat should begin as a heavyweight enterprise workforce suite or as a fast, standalone scheduler for small-to-mid-sized procedural care facilities.

This document answers that question by:

- benchmarking the current QGenda product surface
- identifying where QGenda is strong versus where it feels heavy or over-engineered
- defining the minimum viable "QGenda Lite" that still counts as a real scheduling system
- specifying a web-first architecture that fits the existing PracticeX stack and product posture

The recommendation is to build a standalone scheduling engine first, with explicit seams for later contract-aware enrichment from PracticeX. In other words: ship a complete scheduler now, but make sure it can later consume call-coverage, employment, and facility constraints extracted from contracts.

## 2. Executive recommendation

QGenda's strongest differentiator is not merely "calendar plus swap requests." It is the combination of:

- weighted rules for complex recurring scheduling
- fairness and availability balancing across long time horizons
- real-time updates across desktop and mobile
- adjacency to on-call, credentialing, and compensation workflows

Its biggest weakness for smaller groups is that the same unified-platform strategy that makes QGenda compelling for enterprise health systems also creates adoption drag:

- too much product surface for groups that mainly need monthly scheduling plus requests and swaps
- mobile UX friction around requests, pending items, date navigation, and notification overload
- a strong bias toward deep integrations and enterprise data plumbing
- a broad configuration model that appears powerful, but expensive to learn, configure, and maintain

Recommendation:

Build "QGenda Lite" as a standalone, mobile-first physician and staff scheduling product for ambulatory and procedural care groups, with four deliberate boundaries:

1. Keep the scheduling engine, publish flow, requests, swaps, open-shift claiming, audit trail, and basic compensation export.
2. Keep lightweight safety gates for credentials and privileges, but do not build full credentialing or payer enrollment in the MVP.
3. Remove EMR dependency completely from the critical path. Scheduling must work from manually managed roster, facility, and rules data.
4. Treat contract-aware scheduling as a differentiating optional input layer, not a prerequisite for the first usable release.

## 3. Research summary

### 3.1 What QGenda clearly does well

Based on QGenda's current public product pages, the platform is built around a centralized scheduling core, then extends outward into on-call, time and attendance, credentialing, capacity, analytics, and integrations. The official positioning emphasizes:

- equitable, rules-based scheduling across physicians, nurses, and staff
- predictive or AI-driven optimization
- real-time updates
- mobile self-service
- enterprise-wide visibility
- integration with HRIS, EHR, payroll, and communication systems

QGenda also publicly describes a fairly sophisticated rule model:

- prioritized scheduling rules
- provider-specific filters
- task-pairing rules
- weighted rule values
- minimum and maximum days-worked sequences
- equitable call allocations across part-time and full-time physicians

This is the part worth emulating. It is the actual scheduling engine, not the surrounding enterprise sprawl.

### 3.2 What QGenda has grown into

QGenda no longer presents as a scheduling product alone. It presents as a healthcare workforce operating system, spanning:

- provider and staff scheduling
- on-call directory and communication
- time and attendance
- compensation and payroll-adjacent rule logic
- credentialing and payer enrollment
- clinical capacity and exam room management
- analytics
- integrations into the broader healthcare IT ecosystem

That breadth is rational for large health systems. It is also the source of unnecessary weight for a standalone scheduler wedge.

### 3.3 Mobile and workflow friction worth exploiting

QGenda's App Store listing shows the intended mobile scope is broad: schedule views, clock in and out, requests, one-way and two-way trades, available shifts, self-scheduling, messaging, calendar sync, and admin approvals.

However, recent public App Store reviews show recurring friction in exactly the workflows that matter most for a standalone scheduler:

- users report poor visibility into pending requests because they appear in long lists rather than calendar context
- users report difficulty performing fast swap actions from mobile
- users report crash-prone or tedious date navigation for swap selection
- users report noisy notifications and limited ability to isolate signal from noise
- users report repeated login friction and weak month-view readability

This creates a clear product opening: win the last-mile usability battle on the most frequent daily actions, instead of trying to match the full enterprise suite.

## 4. QGenda feature audit

### 4.1 Scheduling engine and scheduler desktop

Current QGenda capabilities observed from public materials:

- rule-based schedule generation
- weighted and prioritized constraint handling
- equitable call distribution over long horizons
- provider preference handling
- provider grouping and specialty-specific rules
- recurring work patterns and templates
- real-time centralized schedule updates
- enterprise-level visibility with department-specific configuration
- reporting on call, clinic, vacation, services, and non-clinical time
- predictive staffing and optimization positioning

Interpretation:

This is the must-have benchmark. A Lite product does not need the full enterprise layer, but it does need serious constraint modeling or it will be dismissed as a prettier calendar.

### 4.2 Requests, swaps, pickups, and self-service

Current QGenda capabilities observed from public materials:

- time-off and shift requests
- one-way and two-way shift trades
- open or available shift board
- first-come, first-served claiming for eligible users
- optional approval layers
- schedule change notifications
- mobile access to requests, swaps, and pickup workflows
- self-scheduling for some user types

Interpretation:

This is the second must-have benchmark. The Lite version should not reduce this to a manual message board. It should improve this surface and make it the clearest reason to switch.

### 4.3 On-call and directory workflows

Current QGenda capabilities observed from public materials:

- enterprise-wide on-call directory
- real-time provider availability visibility
- ability to identify and contact the correct provider quickly
- credential-aware on-call eligibility
- mobile access for staff and providers

Interpretation:

The Lite MVP should include a lightweight on-call directory and contact workflow, because procedural groups often care about on-call readiness. It should not include enterprise communication orchestration or hospital-wide escalation trees in the first release.

### 4.4 Credentialing and provider readiness

Current QGenda capabilities observed from public materials:

- provider credentialing and privileging workflows
- payer enrollment
- automated primary source verification and monitoring
- provider onboarding and document management
- synchronization between credential updates and on-call schedule eligibility
- centralized dashboards for license and enrollment status

Interpretation:

This is valuable, but mostly over-scoped for the initial wedge. Small and mid-sized procedural groups need guardrails, not a full credentialing platform on day one.

### 4.5 Time, attendance, and compensation

Current QGenda capabilities observed from public materials:

- schedule-first time capture
- clock in and out
- attestation
- actual-time-versus-scheduled-time reconciliation
- configurable pay rules
- payroll-oriented exports and integrations
- support for complex healthcare pay structures

Interpretation:

The useful core is: scheduled shift, actual shift, attestation, and payout-ready totals. The heavyweight part is the very large pay-rule and payroll integration surface. Lite should keep the first and postpone the second.

### 4.6 Integrations and enterprise operations

Current QGenda capabilities observed from public materials:

- HRIS, payroll, and time data exchange
- EHR-connected schedule and census workflows
- clinical communications integrations
- broad integrated partner ecosystem
- single sign-on and single login posture inside a larger enterprise environment

Interpretation:

This is exactly where Lite should refuse complexity in the first release. A standalone scheduling product should not require enterprise IT maturity to become valuable.

## 5. The product gap: where Lite should be better

### 5.1 Product thesis

The winning wedge is not "QGenda, but cheaper." It is:

`The fastest way for a multi-site specialty practice to create, publish, trade, and trust complex clinical schedules without buying an enterprise workforce platform.`

### 5.2 Agentic improvements

These are not just feature ideas. They are places where the Lite version should be intentionally better than the incumbent.

#### A. Calendar-native request review

Do not show pending requests in a long queue detached from time context. Every pending item should render on a calendar timeline with color-coded state:

- requested
- approved
- denied
- needs action
- conflicts with fairness target

This directly addresses public QGenda review pain around reviewing pending requests.

#### B. One-screen hot swap

For urgent sick-call or same-day swaps, the mobile action should be:

1. open shift
2. see eligible replacements immediately
3. view conflicts and fairness impact
4. claim or offer with one confirmation

No date paging. No multi-screen scavenger hunt. No desktop fallback requirement.

#### C. Explainable fairness ledger

Most schedulers do not just want a generated schedule. They want to defend it. Every schedule should expose a provider-level fairness ledger:

- weekday call count
- weekend call count
- holiday count
- late shift count
- total RVU or stipend-bearing assignments
- rule overrides applied

This makes the engine explainable instead of mystical.

#### D. Sandboxed publishing

Schedulers should be able to generate three candidate schedules for the same month, compare them, lock certain assignments, then publish one. QGenda's public posture emphasizes optimization, but the Lite version should foreground comparison and human control.

#### E. Notification hygiene

The mobile product should default to:

- one actionable notification per event
- digest mode for non-urgent changes
- per-notification-type controls
- no forced personal calendar sync as the answer to poor in-app visibility

#### F. Contract-aware mode as a differentiator

PracticeX already extracts call coverage and employment obligations. Lite should be able to consume those as optional rule inputs later:

- minimum call obligations
- protected post-call recovery days
- location-specific duty coverage
- stipend-bearing assignments
- service-line exclusivity or privilege constraints

This should remain optional in v1, but it is the strategic bridge no generic scheduler will have.

## 6. QGenda Lite MVP

### 6.1 Target customer

Primary target:

- independent specialty groups
- ambulatory surgery centers
- anesthesia groups
- GI and procedural groups
- 10 to 250 clinicians across 1 to 20 facilities

Primary buyer:

- operations leader
- practice administrator
- physician scheduler
- medical director in smaller groups

### 6.2 MVP product modules

#### Module 1: Roster and facility setup

- providers, staff, roles, credentials-lite, privilege tags
- facilities, departments, service lines, locations
- shift types, templates, recurrence patterns, holiday calendars
- availability, PTO, blackout dates, preferred patterns

#### Module 2: Scheduling engine

- monthly and quarterly schedule generation
- hard constraints, soft constraints, weighted preferences
- consecutive-day limits, post-call recovery, weekend balancing
- part-time FTE weighting
- multi-site coverage balancing
- schedule drafts, comparisons, locks, and publish snapshots

#### Module 3: Requests and swaps

- request off
- request specific shifts
- open shift marketplace
- one-way trade
- two-way trade
- approve, deny, or auto-approve by policy
- full mobile workflow parity

#### Module 4: On-call directory

- who is on now
- who is next
- facility and service-line filters
- contact card
- eligibility badge

#### Module 5: Time and compensation lite

- clock in and out or shift attest
- scheduled vs actual reconciliation
- simple stipend and premium-shift calculations
- export totals for payroll processing

#### Module 6: Audit and analytics

- schedule publish history
- swap and request audit
- override log
- fairness summary
- fill rate, open-shift aging, approval turnaround time

### 6.3 What to cut from the MVP

- full credentialing, privileging, and payer enrollment workflows
- primary source verification
- HRIS, payroll, and EHR integrations on the critical path
- exam room and clinical capacity management
- residency management
- enterprise-wide workforce analytics suite
- 40,000-rule compensation engine
- broad cross-department staffing orchestration
- chatbot or LLM-based scheduling in the decision loop

The engine should be deterministic first. AI can assist with explanations or scenario suggestions later, but the core schedule generation should stay inspectable.

## 7. Technical architecture

### 7.1 Architecture choice

Recommended delivery model:

- primary runtime: installable PWA
- optional packaging: Capacitor wrapper using the same web app
- frontend stack: React, TypeScript, Vite, React Router, Tailwind CSS, TanStack Query/Table
- backend stack: ASP.NET Core modular monolith with background jobs and typed API contracts
- database: PostgreSQL with dedicated scheduling schema and snake_case identifiers

This matches the current PracticeX ADRs instead of introducing a parallel stack.

### 7.2 Delivery posture: PWA first, Capacitor second

Recommendation:

- treat the PWA as the canonical product
- use Capacitor only as a distribution and device-API shell
- keep all business logic, rendering, data access, validation, and rule editing in TypeScript
- do not create a separate native iOS application codebase

Important constraint:

Capacitor's official push-notifications plugin supports native push, but its iOS path requires platform capability setup and AppDelegate wiring. Because this project explicitly wants no native iOS code, the product should not depend on native iOS push plumbing for core functionality in v1.

Therefore:

- iOS and iPadOS installed web-app support should use standards-based Web Push
- Android can use either Web Push or Capacitor push, depending on packaging needs
- Capacitor should still be useful for haptics, network status, app-shell polish, and optional store distribution without splitting the codebase

### 7.3 Mobile experience

#### Interaction model

The mobile product should be optimized around five actions:

- check today's schedule
- acknowledge a change
- request off
- pick up or trade a shift
- verify who is on call

#### Clinical Glass aesthetic

The visual direction should extend the PracticeX brand rather than copy consumer scheduling apps:

- warm off-white base surfaces
- deep green primary actions
- restrained orange for risk or conflict states
- translucent glass treatment only on layered controls such as bottom sheets, sticky timeline filters, and quick actions
- serif only for page-level headings; operational text remains dense, sans-serif, and highly legible

#### Mobile patterns

- timeline-first day and week views
- bottom-sheet request composer
- thumb-zone quick actions for offer, claim, approve, and message
- subtle haptic confirmation for publish, claim, approval, and conflict resolution
- badge counts for approvals and open shifts
- per-provider fairness summary visible from the mobile profile card

### 7.4 Offline and sync strategy

Use an offline-first outbox model:

- local persistence: IndexedDB
- query cache persistence: TanStack Query persisted cache
- service worker: Workbox-backed asset and API caching
- outbox records: request_off, swap_offer, swap_claim, shift_attest, note_acknowledge

Important standards constraint:

The Background Synchronization API is not baseline across major browsers, so the product must not rely on browser background sync as the only recovery mechanism. Use it when available, but treat it as an enhancement.

Required sync behavior:

- all writes are stored locally first with durable operation IDs
- when connectivity returns, the client replays the outbox in order
- the backend supports idempotent command handling keyed by operation ID
- conflicts return explicit resolution states, not silent last-write-wins

### 7.5 Notifications

For v1:

- standards-based Web Push for installed PWAs, including iOS Home Screen web apps
- badge counts for approvals, open shifts, and new call assignments
- local in-app notification center
- digest, immediate, and silent preferences per event type

If a packaged app is needed later:

- keep the same notification event contract
- swap transport per platform without changing application logic

### 7.6 Standalone identity and security

Identity should be standalone OIDC, not EMR-tethered:

- passwordless email or passkey sign-in for clinicians
- MFA for scheduler and admin roles
- tenant, facility, and role-scoped authorization
- audit logging for all schedule publishes, overrides, approvals, swaps, and exports

Recommended roles:

- organization_admin
- scheduler_admin
- facility_scheduler
- provider
- staff_member
- readonly_leadership

Security posture:

- every API request scoped by tenant and facility access
- append-only audit event stream
- immutable publish snapshots for each released schedule
- signed export artifacts
- PHI-minimized product surface by default

## 8. Data model and backend modules

### 8.1 Proposed PostgreSQL schemas

Use a dedicated `schedule` schema rather than overloading existing contract tables.

Representative tables:

- `schedule.provider_profiles`
- `schedule.provider_facility_memberships`
- `schedule.shift_definitions`
- `schedule.shift_templates`
- `schedule.shift_instances`
- `schedule.availability_windows`
- `schedule.rule_sets`
- `schedule.rule_definitions`
- `schedule.schedule_runs`
- `schedule.schedule_run_assignments`
- `schedule.publish_snapshots`
- `schedule.request_events`
- `schedule.swap_offers`
- `schedule.shift_attestations`
- `schedule.compensation_rules_lite`
- `schedule.fairness_ledgers`

Cross-cutting:

- `audit.audit_events` for all workflow actions
- `org.facilities` and `org.users` reused from the existing platform

### 8.2 Scheduling engine design

The engine should be deterministic, explainable, and replayable.

Suggested pipeline:

1. Expand shift templates into concrete shift instances for the planning window.
2. Apply hard exclusions first: unavailable, expired privilege flag, facility mismatch, required rest, max consecutive days, locked assignments.
3. Seed scarce or high-priority shifts first: holidays, overnight call, cross-site scarce specialties.
4. Score eligible assignments using weighted fairness and preference metrics.
5. Run local optimization passes to improve equity and reduce undesirable streaks.
6. Persist every candidate schedule with rule-satisfaction metrics and fairness summaries.
7. Allow human locks and reruns before publish.

Scoring inputs should include:

- FTE-normalized fairness
- historical burden ledger
- provider preference weight
- specialty or privilege match
- facility continuity
- shift adjacency and recovery windows
- call-weekend-holiday mix
- stipend or premium-shift balance

### 8.3 Recurring shift logic

Do not model recurring schedules as static copied grids. Model them as:

- shift definitions
- recurrence templates
- seasonal overrides
- holiday calendars
- exception layers
- lock layers

Use RRULE-style recurrence expansion for templates, then overlay exceptions and locks. This is more durable than editing generated grids by hand and is easier to audit.

### 8.4 Background jobs

Required background jobs:

- schedule generation job
- notification fan-out job
- outbox reconciliation job
- fairness recompute job
- publish snapshot export job
- lightweight credential expiry reminder job

## 9. Roadmap recommendation

### Phase 1: Core scheduler

- roster, facilities, shift templates
- deterministic schedule engine
- publish snapshots
- mobile schedule views
- request off, open shifts, and swaps

### Phase 2: Operations fit

- on-call directory
- time and compensation lite
- fairness ledger and reporting
- offline outbox hardening
- Web Push and badge controls

### Phase 3: PracticeX differentiator

- import call obligations from `call_coverage_v1`
- import stipend or compensation cues from employment or coverage agreements
- contract-aware conflict detection
- renewal or amendment alerts that affect scheduling rules

## 10. Final recommendation

PracticeX should not try to rebuild the whole QGenda platform. It should build the subset that smaller, high-acuity procedural groups actually feel every week:

- schedule generation
- requests and swaps
- on-call readiness
- mobile trust
- explainable fairness

That product can stand on its own.

Then, unlike QGenda, PracticeX can connect scheduling back to contracts, obligations, and financial terms when that becomes strategic. That is the real long-term differentiator: not enterprise sprawl, but a scheduler that understands the operational consequences of the agreements underneath it.

## Sources

- QGenda workforce scheduling overview: https://www.qgenda.com/workforce-scheduling-software/
- QGenda provider scheduling details: https://www.qgenda.com/provider-scheduling-review-sites/
- QGenda rules prioritization: https://www.qgenda.com/blog/qgenda-prioritizes-rules-automated-physician-scheduling/
- QGenda equitable call distribution: https://www.qgenda.com/blog/distribute-call-equitably-among-physicians/
- QGenda provider scheduling summary page: https://www.qgenda.com/advanced-scheduling-for-providers-with-qgenda/
- QGenda swap market workflow: https://www.qgenda.com/blog/available-shifts-swap-market/
- QGenda credentialing and on-call integration: https://www.qgenda.com/connect-credentialing-and-on-call-scheduling/
- QGenda credentialing overview: https://www.qgenda.com/credentialing/
- QGenda time and attendance overview: https://www.qgenda.com/time-and-attendance/
- QGenda integrations overview: https://www.qgenda.com/integrated-partners/
- QGenda iOS app listing: https://apps.apple.com/us/app/qgenda/id657634288
- QGenda iOS app public reviews: https://apps.apple.com/us/app/qgenda/id657634288?platform=iphone&see-all=reviews
- Capacitor overview: https://capacitorjs.com/docs
- Capacitor progressive web apps: https://capacitorjs.com/docs/web/progressive-web-apps
- Capacitor push notifications plugin: https://capacitorjs.com/docs/apis/push-notifications
- Capacitor haptics plugin: https://capacitorjs.com/docs/apis/haptics
- Capacitor network plugin: https://capacitorjs.com/docs/apis/network
- MDN Background Synchronization API: https://developer.mozilla.org/en-US/docs/Web/API/Background_Synchronization_API
- MDN Offline and background operation for PWAs: https://developer.mozilla.org/en-US/docs/Web/Progressive_web_apps/Guides/Offline_and_background_operation
- WebKit web push on iOS and iPadOS: https://webkit.org/blog/13878/web-push-for-web-apps-on-ios-and-ipados/
