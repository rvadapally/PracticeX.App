## Regulatory Filing — Issue Overlay

This document is a regulatory filing or compliance artifact: BOI report,
SEC filing (Form D, 10-K excerpt, S-1 draft), IRS filing (83(b), Form
2553, Form 8832, Form 1023), state tax registration, sales-tax
certificate, or similar. Regulatory deadlines are unforgiving — surface
every date and confirm filing evidence. Run this overlay in addition
to the master register.

### Beneficial Ownership Information (BOI) — Corporate Transparency Act
- **Filing posture** — BOI reports went live 2024-01-01 for new
  entities and were subject to court-driven on-and-off enforcement
  through 2025. As of late 2025, FinCEN's interim final rule narrowed
  reporting to foreign reporting companies. Verify which regime applies
  to this entity at the document's effective date.
- **Initial vs updated report** — initial within 30 days of formation
  (post-rule changes); updated within 30 days of any change to
  reported information.
- **Reporting persons** — beneficial owners (≥25% ownership OR
  substantial control); company applicants for entities formed
  2024-01-01 or later.
- **Required information** — full legal name, DOB, address, ID number
  + image (passport, driver's license).
- **Penalties** — willful violations: $500/day civil penalty (capped),
  up to $10,000 + 2 years imprisonment criminal. Surface aggressively
  if filing posture is unclear.

### SEC filings — private offerings
- **Form D** — required for Regulation D offerings (506(b), 506(c),
  504); due 15 days after first sale.
- **Bad Actor disqualification** — Rule 506(d) requires the issuer to
  represent that no covered persons (officers, directors, 20%
  beneficial owners, certain placement agents) have disqualifying
  events. Surface if the document is a securities offering and bad-
  actor due diligence isn't evidenced.
- **State blue-sky filings** — Form D is federal pre-emption for
  Reg D, but states still require notice filings + filing fees in
  most cases. Common omission.
- **General solicitation** — 506(b) prohibits general solicitation;
  506(c) permits but requires verified accredited-investor status.
  Flag if marketing language conflicts with the chosen exemption.
- **Regulation A / Crowdfunding** — separate regimes with their own
  filing schedules; surface if applicable.

### IRS filings
- **Form 2553 (S-corp election)** — must file by March 15 of the
  desired effective year, OR within 75 days of incorporation for first
  year. **Late S elections can be cured under Rev. Proc. 2013-30** if
  reasonable cause exists; flag if the document evidences S-corp
  intent without filing evidence.
- **Form 8832 (entity classification election)** — for LLCs electing
  corporate taxation; default partnership taxation can be elected away
  but with timing constraints (60 months between elections, generally).
- **Form 83(b) election** — 30 days from issuance; postmark date is
  the deadline. **The 83(b) miss is a six-figure tax mistake** —
  surface every restricted-stock grant and confirm the election was
  filed.
- **Form 1023 / 1023-EZ (501(c)(3))** — for nonprofits; 27-month
  window from formation for retroactive recognition.
- **Form 8-K equivalents for private companies** — none, but if the
  company has issued an S-1 or registration statement, Form 8-K is
  triggered for material events.

### State tax registrations
- **Sales-tax registration** — required in each state where the
  company has nexus (physical presence, economic nexus per *Wayfair*).
  Common gap: SaaS companies that hit economic nexus thresholds without
  registering.
- **Withholding tax registration** — required where the company has
  employees.
- **Unemployment insurance registration** — same.
- **Annual report / franchise tax** — see CorpFormation overlay.

### Industry-specific regulatory regimes
- **HIPAA covered-entity / business-associate registration** — no formal
  filing in most cases, but BAAs and policies are the evidence. Surface
  if document is from a healthcare-adjacent entity without BAA infra.
- **HIPAA breach notification** — 60 days to notify individuals; 60
  days to notify HHS for breaches affecting <500 individuals (annual
  log) or "without unreasonable delay" for ≥500.
- **State data-breach notification** — every state has its own; California
  CPRA, Virginia VCDPA, Colorado CPA, Connecticut CTDPA are the most
  active.
- **GDPR reporting** — 72 hours to supervisory authority for breaches
  with risk to data subjects.
- **PCI-DSS attestation** — annual SAQ or RoC if accepting card payments.
- **SOC 2 / ISO 27001** — not regulatory but increasingly contractual;
  surface if document references certification.
- **Export controls** — EAR / ITAR registration if dealing with
  controlled technology or services.
- **OFAC** — sanctions screening if doing business internationally.

### Common landmines
- **BOI filing posture unclear** — see above; surface as MEDIUM/HIGH
  given the 2024-2025 enforcement volatility.
- **Form D filed late or not filed** — flag; can be cured but signals
  process gaps.
- **State blue-sky filings missed** — typically discovered in financing
  diligence; back-paper required.
- **83(b) deadline approaching or missed** — see above; CRITICAL if
  active.
- **Sales-tax economic nexus not assessed** — flag; recommend nexus
  study.
- **Privacy posture mismatched to data flows** — if document evidences
  data collection that triggers CCPA/GDPR but no policies / DPAs are
  paired.
- **Lapse of corporate good-standing** — see CorpFormation overlay;
  recommend reinstatement filings.
