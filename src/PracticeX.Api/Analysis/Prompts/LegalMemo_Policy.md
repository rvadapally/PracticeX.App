## Privacy Policy / Terms of Service — Issue Overlay

This document is a public-facing privacy policy, terms of service /
terms of use, cookie notice, EULA, or similar consumer-facing artifact.
Posture differs from a negotiated contract — these are unilateral and
serve compliance + risk-allocation purposes. The risk is dual: insufficient
disclosure → regulatory exposure; over-aggressive terms → unenforceability +
reputational risk. Run this overlay in addition to the master register.

### Privacy Policy — disclosure adequacy

#### CCPA / CPRA (California)
- **Categories of personal information collected** — the 11 statutory
  categories (identifiers, commercial info, biometrics, etc.); is the
  policy specific to actual collection or generic boilerplate?
- **Sources of collection** — directly from consumer, automatically,
  from third parties; specify each.
- **Business or commercial purpose** — for each category; selecting
  "to provide services" only is a finding for any company doing
  marketing or analytics.
- **Categories of third parties** with whom information is shared.
- **Sale or sharing of personal information** — CCPA's "sale" is broad
  (includes sharing for cross-context behavioral advertising under
  CPRA). If the answer is "yes," the policy must include the
  "Do Not Sell or Share My Personal Information" link and an opt-out
  mechanism honoring Global Privacy Control signals.
- **Sensitive personal information** — separate disclosure category;
  use of SPI requires opt-out right.
- **Retention period** — actual retention or criteria, by category.
- **Consumer rights** — right to know / delete / correct / opt-out of
  sale & sharing / limit use of SPI / non-discrimination. Process for
  exercising; verification standards; appeals process for denied
  requests.

#### GDPR (EU/UK/Switzerland)
- **Lawful basis** — Article 6 basis for each processing activity:
  consent, contract, legal obligation, vital interest, public task,
  legitimate interest. Generic "we have your consent" is wrong for
  most processing.
- **Special categories** — Article 9 additional basis for sensitive
  data (health, biometrics, race, religion, sexual orientation, etc.).
- **International transfers** — adequacy decision (UK, Israel,
  Switzerland post-2024) vs Standard Contractual Clauses (post-Schrems
  II + DPF for US transfers). Surface if data flows to US and DPF/SCC
  basis isn't disclosed.
- **Data Protection Officer** — required for certain processing;
  contact details disclosed.
- **EU representative** — required for non-EU processors with EU users
  (Article 27).
- **Data subject rights** — access, rectification, erasure, restriction,
  portability, objection, automated decision-making.
- **Retention** — specific period or criteria.
- **Right to lodge complaint** with supervisory authority — must be
  disclosed.

#### State privacy laws (Virginia, Colorado, Connecticut, Utah, others)
- **Sensitive data opt-in** (most states require opt-in for sensitive
  data, vs CCPA's opt-out).
- **Universal opt-out signals** (Colorado / Connecticut require
  honoring; California now requires honoring GPC).
- **Profiling opt-out** (most states).

#### COPPA (under-13)
- **Age gate** — does the service collect data from under-13s? if not,
  state policy that the service is not directed at children.
- **Parental consent** mechanism if applicable.

#### Health / financial / education-specific
- **HIPAA** — if covered-entity / business-associate, the privacy
  policy is supplemental to the Notice of Privacy Practices.
- **GLBA** (financial) — privacy notice with specific opt-out
  requirements.
- **FERPA** (education) — special posture for education records.

### Terms of Service — risk allocation

#### Acceptance and modification
- **Click-wrap vs browse-wrap** — courts overwhelmingly enforce
  click-wrap (affirmative click) but reject browse-wrap (continued use
  = acceptance). Flag if the document is browse-wrap.
- **Modification clause** — typical "we may modify these terms at any
  time" — increasingly limited by courts. *Douglas v. U.S. District
  Court* and progeny require reasonable notice + affirmative re-
  acceptance for material changes.

#### Limitation of liability
- **Cap on damages** — typically capped at fees paid in 12 months;
  some services cap at $100. Carve-outs for indemnity / IP infringement /
  breach of confidentiality / gross negligence + willful misconduct?
- **Consequential damages exclusion** — bilateral or only protecting
  the service?
- **Statutory exceptions** — many jurisdictions void disclaimers for
  death / personal injury (California Civil Code §1668); flag if
  service involves any physical risk.

#### Disclaimer of warranties
- **AS-IS / AS-AVAILABLE** — typical; flag if service is healthcare-
  adjacent (FDA-regulated activities can't disclaim safety warranties).
- **Implied warranty disclaimers** — UCC Article 2 implied warranties
  of merchantability + fitness; specific disclaimer language required
  (must be conspicuous, must mention "merchantability" by name).

#### Indemnification (running from user to company)
- **Scope** — typically user indemnifies company for user's own use,
  third-party claims arising from user content. Reasonable.
- **Carve-outs from user indemnity** — none (broad-form) vs carve-out
  for company's gross negligence (more balanced).

#### Dispute resolution
- **Mandatory arbitration** — increasingly mandatory in consumer ToS;
  *Concepcion* and *Italian Colors* protect them. Flag if the document
  has arbitration without:
  - Class-action waiver (industry standard but creates mass-
    arbitration exposure — see *Abernathy v. DoorDash*).
  - Severability clause if class waiver fails.
  - "Bellwether" or "batch" arbitration provisions to limit
    mass-arb exposure.
- **Choice of law** — typically Delaware or California or company's
  home state.
- **Choice of forum** — exclusive vs non-exclusive; consumer-protection
  statutes in some states (NJ, MA, CA) may void.
- **Class-action waiver** — separately enforceable; surface if missing.
- **JAMS vs AAA** — most companies pick one; surface the rules
  designation.
- **Cost-shifting** — fee-shifting provisions face increased scrutiny
  for unconscionability.

#### Account termination
- **Termination for any reason** — typical; balanced with reasonable
  notice obligation.
- **Data return / deletion** — what happens to user data on
  termination? Aligns with privacy-policy retention?
- **Survival** — IP ownership, indemnity, LOL, dispute resolution
  survive termination.

#### Intellectual property
- **License grant from user to company** — for user-generated content;
  scope (sublicensable, royalty-free, perpetual, worldwide); flag if
  scope is broader than service operation needs (publicity, marketing
  uses).
- **DMCA notice + counter-notice** mechanism — required for safe-harbor
  under §512.
- **Trademark notice** — service marks, logos.
- **Open-source attribution** — if applicable.

#### Specific high-risk clauses
- **Auto-renewal** — federal CARD Act / state auto-renewal laws (CA,
  NY, OR, others) require specific disclosures + affirmative consent +
  cancellation methods.
- **Acceptable use policy** — broad enough to cover prohibited
  activities + specific enough not to be vague.
- **API or developer terms** — separate or incorporated; rate limits;
  derivative-work restrictions.
- **AI training disclosure** — increasingly required: does the
  service use customer data to train AI models? what's the opt-out?

### Common landmines
- **CCPA Do-Not-Sell link missing on a service that does sell/share** —
  $7,500/intentional violation civil penalty.
- **GDPR lawful basis vague** — failure to identify a specific Article
  6 basis is a finding in any DPA dispute.
- **GPC signal not honored** — California now requires; other states
  follow.
- **Browse-wrap arbitration** — likely unenforceable; recommend
  click-wrap.
- **Auto-renewal without state-required disclosures** — class-action
  magnet.
- **AI training silent** — increasingly a contractual flashpoint;
  enterprise customers and EU regulators expect disclosure.
- **Mass-arbitration exposure unmanaged** — recommend bellwether /
  batch provisions.
- **Stale third-party processor list** — privacy policy lists
  processors that no longer apply or omits current ones (analytics,
  AI providers).
