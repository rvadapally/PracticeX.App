# NDA Family Schema v1

## Scope and intent

The NDA family covers standalone non-disclosure agreements — one-way (bilateral_individual), mutual between organizations, and the unsigned templates a practice keeps on file to stamp out per recipient. v1 models five sub-types: `bilateral_individual`, `mutual_org`, `investor_template`, `advisor_template`, `demo_participant_template`. These are the five shapes in the NDA roster (samples 1–5).

Out of scope: confidentiality clauses embedded inside other agreements (offer letters, advisor agreements, MSAs). Those ride with their parent contract's schema and surface as `confidentiality_term` or equivalent on that parent — not as separate NDA records. Templates are first-class records here because practices reuse them as recipient-stamped artifacts; treating them as second-class would lose the inventory.

## Sub-type discriminator

`subtype` (string, required).

| Value | Description |
|---|---|
| `bilateral_individual` | Executed one-way NDA between the company and a single individual (e.g. clinician under evaluation). |
| `mutual_org` | Executed mutual NDA between two organizations; both sides disclose and receive. |
| `investor_template` | Unsigned template used during investor diligence. |
| `advisor_template` | Unsigned template used to onboard physician/clinical advisors. |
| `demo_participant_template` | Unsigned template gating product demo access. |

## Common base

Every NDA carries these fields. `PartyRecord`, `TermRecord`, and `SignatureRecord` are **imported from `PracticeX.Discovery.Schemas`** (defined alongside `EmploymentSchemaV1`) — do not redefine.

| Field | Type | Required | Notes |
|---|---|:--:|---|
| `parties[]` | `PartyRecord[]` | yes | Min 2 entries. For templates, `parties[1]` may be null/placeholder; do not flag. |
| `effective_date` | date (ISO-8601) | conditional | Required when `is_template=false`; nullable for templates. Placeholder (`____`, `[Date]`) → null + `is_template=true`. |
| `term` | `TermRecord` | yes | `fixed_months` (typical: 24), `fixed_until` (specific end), or `open_ended` for permanent confidentiality. |
| `governing_law` | string | yes | Jurisdiction string. `[Your State]` → template. |
| `venue` | string | no | Free text. |
| `permitted_purpose` | text | yes | E.g. "evaluating partnership", "advisor onboarding", "demo participation", "investment diligence". |
| `confidential_information_definition` | text | yes | The doc's CI clause verbatim or near-verbatim. |
| `exclusions[]` | text[] | no | Carve-outs: already-public, independently-developed, lawfully-received-from-third-party, required-by-law. |
| `is_mutual` | bool | yes | `true` for `mutual_org`; `false` otherwise. |
| `is_template` | bool | yes | Computed from placeholder rules + sub-type. |
| `is_executed` | bool | yes | Computed from `signature_block`. |
| `non_solicit_term_months` | int | no | Sometimes embedded in advisor/investor NDAs. |
| `non_disparagement` | bool | no | Set when a non-disparagement clause is present. |
| `signature_block[]` | `SignatureRecord[]` | yes | Always empty for templates; populated for executed NDAs. |
| `notes` | string | no | Extractor free-text capture. |

## Sub-type-specific notes

No new fields per sub-type — only behavioural defaults and validation expectations.

### bilateral_individual
- `parties[0]` = company (disclosing); `parties[1]` = individual (receiving).
- `is_mutual = false`, `is_template = false`, `is_executed = true` when signed.
- Typical `permitted_purpose`: clinical/advisory engagement evaluation.
- May execute alongside a downstream `advisor_agreement`. **Do not auto-link** — the relationship is a downstream join, not an extraction concern.

### mutual_org
- `parties[0]` = our org; `parties[1]` = counterparty org. Both act as disclosing and receiving.
- `is_mutual = true`.
- Both signature rows expected in `signature_block`.

### investor_template / advisor_template / demo_participant_template
- `parties[0]` = Synexar (filled into the template); `parties[1]` = blank/placeholder.
- `is_template = true`, `is_executed = false`.
- `effective_date` typically blank — **must not** trigger `extraction_partial` for templates.
- `signature_block` always empty.

## Placeholder / template detection rules

Same model as the Employment family.

- Required base field whose raw value is `____________`, `[Counterparty Name]`, `[Date]`, `[Your State]`, or empty after trim → `is_template = true`; field stays null.
- For `is_template = true`: missing `parties[1]`, `effective_date`, and `signature_block` **must not** emit `extraction_partial`. Templates are first-class records.
- For `is_template = false` and any base required field still null → emit `manual_review_template_detected` and hold in candidate review.

## Extraction priority

### Executed NDAs (`bilateral_individual`, `mutual_org`)
1. `parties` (which side discloses vs receives — for `mutual_org`, both)
2. `effective_date`
3. `permitted_purpose`
4. `term`
5. `signature_block`

### Templates (`investor_template`, `advisor_template`, `demo_participant_template`)
1. `is_template = true` (must be set)
2. `parties[0]` (the Synexar side)
3. `permitted_purpose` — defines what the template is for
4. `governing_law`

## Validation against samples

| # | Path (under `C:\Users\harek\SYNEXAR INC\`) | Sub-type | Key fields to validate |
|---|---|---|---|
| 1 | `NDAs\Synexar__NDA_Dr. Akerman.docx` | `bilateral_individual` | parties (Synexar Inc + Dr. Akerman), effective_date, permitted_purpose (clinical advisory), `is_mutual=false`, signature_block |
| 2 | `NDAs\Synexar_GIQuIC_Mutual_NDA_v2.docx` | `mutual_org` | `is_mutual=true`, parties (both orgs), effective_date, term, signature_block |
| 3 | `NDAs\templates\Synexar_NDA_Investor.docx` | `investor_template` | `is_template=true`, parties[0]=Synexar, parties[1]=null, governing_law (Delaware/Texas — verify) |
| 4 | `NDAs\templates\Synexar_NDA_Physician_Advisor.docx` | `advisor_template` | `is_template=true`, permitted_purpose mentions advisor onboarding |
| 5 | `NDAs\templates\Synexar_NDA_Demo_Participant.docx` | `demo_participant_template` | `is_template=true`, permitted_purpose mentions demo participation |

## Versioning

- Version constant: `nda_v1`.
- Stored as a constant in the C# `NdaSchemaV1Constants` static class (sibling to `EmploymentSchemaV1Constants`).
- Reuses `PartyRecord`, `AddressRecord`, `TermRecord`, `SignatureRecord` from `PracticeX.Discovery.Schemas` — no redefinition.

## Open questions

- For `mutual_org`, should `parties[]` be ordered alphabetically by entity name, or always "us-then-them" (Synexar at index 0)? Affects downstream joins and disclosure-side queries.
- Should the schema track `nda_purpose_normalized` (enum: `advisory_evaluation` | `partnership_eval` | `investment_diligence` | `demo_access`) alongside free-text `permitted_purpose`, or keep it free text only and normalize at query time?
- When a practice keeps multiple variants of the same template (e.g. two advisor templates with different governing law), do we collapse to one record with a `variants[]` array or keep distinct records per file? Inventory cleanliness vs file-of-record fidelity.
- `non_solicit_term_months` — keep on the NDA record when the clause is embedded, or always promote to the parent advisor/investor record? Risks double-counting in restrictive-covenant reporting.
- `confidential_information_definition` — store verbatim, or store a hash + pointer to the source span? Verbatim bloats the record; pointer requires the source artifact to remain accessible.
