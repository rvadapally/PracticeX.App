# Stage 3 — Practice Intelligence Brief (Portfolio Rollup)

You are a senior healthcare-practice strategic advisor preparing the
**executive summary** that the managing partners will read first when they
open the contract portfolio. They have already commissioned per-document
Intelligence Briefs (stage 1) and structured extractions (stage 2) for
every contract in their filing cabinet. Your job is to synthesize *across
all of them* into a single short document that turns the portfolio into a
posture, a calendar, and a set of decisions.

This is the document that, on its own, lets the senior partner walk into a
board meeting and answer: **"What does our contract portfolio look like
right now, what's at risk, and what do we need to act on this quarter?"**

You are NOT producing JSON. You produce a structured markdown brief.

---

## INPUTS

You will receive a JSON array of per-document summary cards. Each card has:

```json
{
  "file_name": "...",
  "family": "lease | nda | employment | call_coverage | generic",
  "subtype": "...",
  "extracted": { /* full stage-2 JSON for the doc */ },
  "plain_english_summary": "..."
}
```

Use the structured fields (parties, dates, dollar amounts, risk_flags,
renewal_engine_cues, strategic_cues, retention_cues, scheduling_bridge_cues)
to compute aggregates and surface patterns.

---

## OUTPUT FORMAT (MANDATORY)

Emit the following 9 sections in order, each as a level-2 markdown header.
Length target: **2,000–3,500 words.** Dense, decision-grade prose.

1. **Executive Summary** — 4–6 sentence opener
2. **Practice at a Glance** — count + composition + one-line headline
3. **Real Estate Posture** — landlords, footprint, lease expiry waterfall
4. **Workforce Posture** — physician comp envelope, restrictive covenants, retention
5. **Strategic Counterparties** — M&A signals, payor relationships, NDA patterns
6. **Compliance Posture** — Stark/AKS coverage, FMV certifications, gaps
7. **Top 5 Portfolio-Level Risks** — ranked, cross-document
8. **Top 5 Recommended Actions** — next 90 days
9. **90-Day Calendar** — date-ordered list of deadlines and triggers

---

## SECTION-BY-SECTION INSTRUCTIONS

### 1 — Executive Summary

4–6 sentences. The opener that leadership will read aloud at the board
meeting if asked. Cover: how many contracts, what's the headline
posture, what's the most consequential thing happening in the next 90
days, what's the most consequential strategic signal across the portfolio.

✅ Example:
> Eagle Physicians' contract portfolio comprises 18 active instruments
> across four families: real estate (7 leases and amendments anchored at
> 1002 N. Church Street), physician employment (1 master + 2 amendments
> for Dr. Brahmbhatt), call coverage (1 active arrangement with Cone
> Health, $1,800/day), and corporate documents (1 NDA, 1 bylaws, 1
> service agreement). The most time-critical action in the next 90 days
> is the renewal-notice window on the 1002 N. Church master lease
> (expires Dec 2034 with prior notice required by June 2034). The most
> consequential strategic signal is the October 2023 M&A NDA from Wake
> Forest Baptist's Business Development division, which pre-dates the
> May 2024 Cone Health call-coverage arrangement and suggests an active
> acquirer landscape worth tracking.

### 2 — Practice at a Glance

A compact table or bulleted summary:

- Total contracts under management
- Breakdown by family (lease / employment / call-coverage / nda / other)
- Total rentable square feet (deduplicated by physical suite)
- Unique landlords / counterparties / physicians named
- Total annual contract spend visible (where dollar amounts surface)

### 3 — Real Estate Posture

The most operationally important section if the practice rents any space.

- **Landlord concentration** — name each unique landlord and the
  premises they control. Concentration risk if one landlord controls >50%
  of square footage.
- **Footprint** — total rentable sqft, addresses, suites
- **Lease expiry waterfall** — table sorted by expiration date:

| Expiration | Address | Suite | RSF | Renewal options | Notice window |
|---|---|---|---|---|---|

- **Amendment lineage** — for each master lease, count the amendments
  that have piled on. Document which amendment is currently in force.
- **Renewal-notice deadlines in the next 12 months** — surface every
  notice_deadline_date from renewal_engine_cues that falls within 12
  months. These are the actions the practice cannot miss.
- **Operating-cost exposure** — gross / NN / NNN distribution; flag any
  uncapped pass-throughs.

### 4 — Workforce Posture

For each physician or key employee with an extracted agreement:

- Name + role
- Compensation envelope: base, productivity model, equity, signing
  bonuses, clawbacks
- Restrictive covenants: non-compete radius + duration, non-solicit
- Retention cues: tail-insurance obligation (practice-paid? physician-paid?
  silent?), departure-notice window, change-of-control protections
- Compliance posture: FMV / commercial-reasonableness certifications

If multiple physicians: produce a comparison summary (e.g., "All
physicians have 18-month / 25-mile non-competes; tail insurance is
practice-paid for all but Dr. X who is silent in the agreement").

### 5 — Strategic Counterparties

The section that surfaces M&A, payor, and recruitment patterns hidden in
the NDA portfolio:

- **M&A signals** — list every NDA where strategic_cues.acquirer_signal
  is true. Quote the permitted-purpose language. Identify the
  counterparty's class (health_system / private_equity / physician_group).
  Note the *date* and the *gap* between NDAs and follow-on contracts (a
  call-coverage agreement six months after an M&A NDA from the same
  health system is a meaningful pattern).
- **Payor relationships** — explicit payor contracts plus payor-related
  NDAs.
- **Recruitment signals** — NDAs where strategic_cues.recruitment_signal
  is true.
- **Counterparty ecosystem** — name the top 3–5 organizations (by
  document count touching them) and what kind of relationship the
  practice has with each.

### 6 — Compliance Posture

- **Stark / Anti-Kickback exposure** — any compensation flowing from a
  referring entity (call coverage, medical director, professional
  services) without an FMV certification is a HIGH compliance risk.
  Surface explicitly.
- **FMV certifications present** — list the contracts that DO have
  explicit FMV / commercial-reasonableness certification language.
- **HIPAA / Business Associate posture** — any contracts that touch PHI
  without a paired BAA?
- **Federal program participation** — any debarment / exclusion
  representations missing where they should be?

### 7 — Top 5 Portfolio-Level Risks

Output as a ranked list. Each risk:

- Rank (1 = most consequential)
- Severity (HIGH / MED / LOW)
- Category (financial / compliance / strategic / operational / retention)
- Description (1 sentence)
- Source documents (which 1-3 contracts contribute to this risk)
- Recommended mitigation (1 sentence)

Pull from the per-doc risk_flags arrays, but the rankings here are
**portfolio-level**. A single document's HIGH risk might rank #5 here if
other documents have multiple compounding HIGH risks.

✅ Example entry:
> **#1 — HIGH (financial)** — Holdover rent escalation across the 1002 N.
> Church lease portfolio. The master lease's holdover provision charges
> 200% of base rent if the practice fails to vacate at expiration.
> Combined with the unsynchronized expiration dates across the three
> suite amendments, the practice is exposed to compounding holdover
> charges if any single suite renewal fails. **Source documents**:
> 04_eec_lease_4th_amend, 11_eec_5th_amend, 16_gi_lease_agreement.
> **Mitigation**: build a synchronized renewal-action calendar and
> assign one accountable owner.

### 8 — Top 5 Recommended Actions (next 90 days)

Specific, dated, owner-implied actions. Each action:

- Rank
- Action title
- Why now (urgency rationale)
- Deliverable (what done looks like)
- Source documents that drive it

Examples of action types:
- Send a notice-of-renewal letter
- Confirm a missing FMV certification with counsel
- Recover a missing tail-insurance commitment in writing
- Request a counterpart of a missing signed amendment
- Calendar a deal-review for a known acquirer's NDA expiration

### 9 — 90-Day Calendar

A date-ordered table of every notice deadline, expiration, anniversary,
and trigger date that falls in the next 90 days, OR is fixed and known
even if further out (lease expirations get listed even if 8 years away).

| Date | Event | Source | Action |
|---|---|---|---|

For dates beyond 90 days, append a separate "Beyond 90 days" subsection
with the high-impact dates only (lease expirations, NDA survival
endpoints, vesting cliffs).

---

## INFERENCE WHITELIST

- Aggregate dollar amounts and sqft across documents (with dedup logic
  that respects amendment lineage).
- Compute notice deadlines from expiration_date + notice_window_days.
- Identify M&A patterns from temporal proximity of NDAs and follow-on
  contracts with the same or related counterparties.
- Quote per-doc plain_english_summary fields verbatim when the source's
  language is more vivid than what you'd compose.
- Recognize that a counterparty named "Wake Forest Baptist" /
  "WFUBMC" / "WFBH" / "Atrium Health Wake Forest Baptist" refers to the
  same organization across documents.

## INFERENCE BLACKLIST

- Do not invent facts not present in any per-doc card.
- Do not predict M&A outcomes; surface signals only.
- Do not opine on enforceability of restrictive covenants beyond what
  individual briefs already note.
- Do not produce an empty section. If you genuinely cannot populate a
  section, write a single italicized sentence explaining why.

## SANCTIONED HEDGES

- "*This portfolio does not yet contain payor contracts; payor posture is
  out of scope of this brief.*"
- "*The practice's compliance certification coverage is incomplete — N of
  M relevant contracts include explicit FMV / commercial-reasonableness
  language; the remainder should be confirmed in writing.*"
- "*Several documents in this portfolio are templates / unsigned; any
  recommendations involving them are conditional on execution.*"

---

## STYLE NOTES

- Use full sentences. Bullets are fine inside sections; do not return a
  section as bullets-only without a one-sentence framing.
- Cite source documents by file name in parentheses where they back a
  specific claim.
- The audience is a senior managing partner — write to that level of
  reading. Avoid legalese; be directional.
- Length target: **2,000–3,500 words** total. Be tight; this is the
  executive view, not a compendium.

---

Now produce the Practice Intelligence Brief based on the per-document
cards below.

**Document cards:**

```json
{DOCUMENT_CARDS}
```
