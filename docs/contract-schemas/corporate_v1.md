# Corporate / Foundation Family Schema v1

## Scope and intent

The corporate family covers governance, equity, and IRS-filing documents for a single legal entity — the practice / our company. It includes documents that *constitute* the entity (certificate of incorporation, founders charter), *authorise share issuances and elect officers* (board consents, RSPAs, founder agreements), *record IRS elections and identifiers* (83(b), EIN letter), and the *evidence-of-filing* receipts proving a constitutive document was lodged with the state.

Corporate is the most heterogeneous of the three v1 families. A filing receipt, a multi-signer founder agreement, and a fixed IRS AcroForm don't usefully share a fat base. v1 uses a small common base plus per-subtype field tables, with a strong `subtype` discriminator driving extraction. The cap table (sample 16) is **data, not a contract** — tracked as a sibling system-of-record artifact and out of scope here. Also out of scope: separation/severance, insurance binders, vendor MSAs, benefit-plan docs. Signature detection (Slice 2) runs upstream.

## Sub-type discriminator

`subtype` (string, required).

| Value | Description |
|---|---|
| `certificate_of_incorporation` | Constitutive charter filed with the state. |
| `filing_receipt` | State-filing receipt; evidence an underlying document was lodged. |
| `board_consent` | Director resolutions — unanimous written, meeting minutes, or single-director. |
| `founder_agreement` | Binding multi-founder governance + equity agreement. |
| `founders_charter` | Non-binding statement of founder roles, equity expectations, principles. |
| `stock_purchase_agreement` | RSPA — issuer to individual purchaser. |
| `section_83b_election` | IRS Form 15620 election to include restricted-stock value in current-year gross income. |
| `ein_letter` | IRS CP-575 / 147C letter confirming the entity's EIN. |

## Common base (deliberately small)

Every corporate doc carries only these fields. Richer shape lives in the per-subtype table below.

| Field | Type | Required | Notes |
|---|---|:--:|---|
| `entity` | `PartyRecord` (org) | yes | The corporation itself; always `parties[0]`. **REUSED**. |
| `subtype` | string | yes | Discriminator. |
| `effective_date` *or* `filed_at_date` | date | yes | `filed_at_date` for `filing_receipt`, `effective_date` otherwise. |
| `jurisdiction` | string | yes | "Delaware", "Texas", etc. "United States" for IRS forms. |
| `is_template` | bool | yes | True when placeholders present. |
| `is_executed` | bool | yes | Computed from `signature_block`. |
| `binding` | bool | yes | Default **TRUE**; **FALSE** for `founders_charter` and any doc that disclaims legal effect (sample 14 labels itself "non-binding"). |
| `signature_block[]` | `SignatureRecord[]` | yes | **REUSED**. Empty for templates. |
| `source_document_ref` | string | no | For `filing_receipt`, points at the underlying COI / amendment / consent. |

## Per-sub-type field tables

### certificate_of_incorporation

| Field | Type | Notes |
|---|---|---|
| `entity_name` | string | e.g. "Synexar, Inc." |
| `state_of_incorporation` | string | "Delaware" |
| `formation_date` | date | |
| `authorized_shares[]` | `ShareAuthorization[]` | |
| `registered_agent` | `PartyRecord` (org) | |
| `founders[]` | `PartyRecord[]?` | From "Incorporator" / founders section. |
| `filing_evidence_ref` | string? | Links to matching `filing_receipt`. |

### filing_receipt

Sample 11 is the gold sample.

| Field | Type | Notes |
|---|---|---|
| `service_request_number` | string | Required. |
| `submitted_at` | datetime | Required. |
| `submitter` | `PartyRecord` (person) | |
| `priority` | string | e.g. "24 Hour Service". |
| `payment_method_last4` | string? | |
| `return_method` | string? | "Regular Mail", "Email", etc. |
| `related_filing_ref` | string? | Underlying doc this receipt evidences. |

### board_consent

| Field | Type | Notes |
|---|---|---|
| `consent_type` | string | `unanimous_written` / `meeting_minutes` / `single_director`. |
| `resolutions[]` | `ResolutionRecord[]` | |
| `referenced_exhibits[]` | string[] | e.g. `["Plan", "RSA", "Option", "Exercise"]`. |
| `share_authorizations[]` | `ShareAuthorization[]?` | When the consent authorises shares (sample 12 reserves 2M). |
| `directors[]` | `SignatureRecord[]` | Timestamped electronic signatures. |
| `statutory_authority` | string? | e.g. "DGCL Section 141(f)". |

### founder_agreement

| Field | Type | Notes |
|---|---|---|
| `founders[]` | `FounderRecord[]` | |
| `total_equity_pool` | decimal? | e.g. `1.0` for 100%. |
| `vesting` | `VestingTerms` | **REUSED**. |
| `equity_grants[]` | `EquityGrant[]?` | **REUSED**. Inline per-founder grants. |
| `governance_rules` | `GovernanceRules?` | |
| `references[]` | string[] | RSPA, PIIA, etc. |
| `binding` | bool | TRUE. |

### founders_charter

| Field | Type | Notes |
|---|---|---|
| `founders[]` | `FounderRecord[]` | |
| `equity_allocation` | `EquityAllocation?` | Total authorized / issued / unissued. |
| `vesting` | `VestingTerms` | **REUSED**. |
| `binding` | bool | **FALSE** — schema flags as non-binding. |
| `guiding_principles[]` | string[]? | Free-form text array. |

### stock_purchase_agreement (RSPA)

| Field | Type | Notes |
|---|---|---|
| `purchaser` | `PartyRecord` (person) | |
| `issuer` | `PartyRecord` (org) | |
| `share_count` | int | |
| `share_class` | string | "Common" / "Series A Preferred". |
| `price_per_share` | `MoneyRecord` | **REUSED**. |
| `total_consideration` | `MoneyRecord` | **REUSED**. |
| `vesting` | `VestingTerms` | **REUSED**. |
| `repurchase_rights` | string? | |
| `transfer_restrictions` | string? | |

### section_83b_election

Sample 15 is the gold sample. Instruction pages 2–4 are boilerplate — extractor must ignore.

| Field | Type | Notes |
|---|---|---|
| `taxpayer` | `PartyRecord` (person) | |
| `taxpayer_tin` | string | `NNN-NN-NNNN`. Validate. |
| `property_description` | string | e.g. "4,000,000 shares of Synexar, Inc." |
| `transfer_date` | date | |
| `tax_year` | int | |
| `fmv_per_share` | `MoneyRecord` | Sample 15: $0.0001 (printed $0.0000). |
| `fmv_total` | `MoneyRecord` | |
| `price_paid_per_share` | `MoneyRecord` | |
| `price_paid_total` | `MoneyRecord` | |
| `gross_income_inclusion` | `MoneyRecord` | |
| `service_recipient` | `PartyRecord` (org) | |
| `service_recipient_ein` | string | `NN-NNNNNNN`. Validate. |
| `signed_at_utc` | datetime | From "RAGHURAM VADAPALLY 2025.11.26 02:43:14 +0000". |
| `omb_number` | string | "1545-0074". |
| `catalog_number` | string | "95376D". |
| `subject_property_ref` | string? | Parent equity grant (RSPA / Founder shares). |

### ein_letter

| Field | Type | Notes |
|---|---|---|
| `entity_name` | string | |
| `ein` | string | `NN-NNNNNNN`. Validate format. |
| `issued_date` | date | |
| `service_center` | string? | IRS service center. |

## Sub-schemas (Corporate-specific; not reused outside this family)

**ShareAuthorization** — `share_class`, `count` (long), `plan_name?`, `reserved_for?` ("Equity Incentive Plan", "general issuance", etc.).

**ResolutionRecord** — `sequence_number?`, `title`, `body_text`, `type` (`equity_plan_adoption` | `officer_election` | `share_issuance` | `amendment` | `other`).

**FounderRecord** — `party` (`PartyRecord`, type=person, **REUSED**), `role`, `title`, `equity_pct?`, `responsibilities` (string[]).

**EquityAllocation** — `total_authorized`, `total_issued`, `total_unissued`, `per_founder_share_count?` (all long).

**GovernanceRules** — `major_decision_threshold` (`MoneyRecord?` — e.g. $10K capex from sample 13), `decision_authority` (`dict<string,string>?` — domain → founder), `deadlock_resolution?`.

## EquityGrant reuse note

`EquityGrant`, `VestingTerms`, and `ProRataIncrement` (in `EmploymentSchemaV1.cs`) are **reused** by `founder_agreement.equity_grants[]`, `founder_agreement.vesting`, `founders_charter.vesting`, and `stock_purchase_agreement.vesting`. `board_consent.share_authorizations` does **not** reuse `EquityGrant` — consents authorise pools, not individual grants, so the simpler `ShareAuthorization` is the right shape.

## Placeholder / template detection rules

- Same model as Employment: `____`, `[Your State]`, `____ Date:`, or empty after trim → `is_template = true`; field stays null.
- For `founder_agreement` (sample 13), extractor MUST flag `is_template = true` even though the body is fully written — `effective_date` and `governing_law` placeholders are dispositive.
- `founders_charter` is a different case: fully written but explicitly non-binding → `is_template = false`, `binding = false`. Don't conflate "non-binding" with "template".

## Extraction priority (per sub-type)

| Sub-type | Top-priority fields |
|---|---|
| `certificate_of_incorporation` | entity_name, state_of_incorporation, formation_date, authorized_shares |
| `filing_receipt` | service_request_number, submitted_at, submitter |
| `board_consent` | resolutions, share_authorizations, directors |
| `founder_agreement` | founders, vesting, governance_rules, is_template |
| `founders_charter` | founders, equity_allocation, binding=false |
| `stock_purchase_agreement` | purchaser, share_count, price_per_share, vesting |
| `section_83b_election` | taxpayer, taxpayer_tin, property_description, transfer_date, fmv_total |
| `ein_letter` | entity_name, ein, issued_date |


## Validation against samples (gold-sample table)

Paths under `C:\Users\harek\SYNEXAR INC\`.

| # | Path | Sub-type | Key fields the extractor must hit |
|---|---|---|---|
| 11 | `FoundationDocs\starting_docs\Document Filing  - Synexar - Delaware.pdf` | `filing_receipt` | service_request_number=20254536349, submitted_at=2025-11-12T11:33:16, submitter=Raghuram Vadapally |
| 12 | `FoundationDocs\starting_docs\Synexar Equity Plan Adoption Resolutions - 11_25_2025.pdf` | `board_consent` | consent_type=unanimous_written, resolutions[0].type=equity_plan_adoption, share_authorizations[0]={Common, 2000000, "Synexar 2025 Equity Incentive Plan"}, directors=[Raghuram Vadapally, Ashutosh Gupta] |
| 13 | `FoundationDocs\starting_docs\Synexar_Founder_Agreement.pdf` | `founder_agreement` | founders=[Raghuram Vadapally CEO 40%, Ashutosh Gupta CMO 40%], is_template=true, vesting=4yr/1yr cliff, binding=true |
| 14 | `FoundationDocs\starting_docs\Synexar, Inc. — Founders Charter.pdf` | `founders_charter` | binding=false, equity_allocation={authorized 10M, issued 8M, unissued 2M, per_founder 4M} |
| 15 | `FoundationDocs\Form 15620 - 83B.pdf` | `section_83b_election` | taxpayer=Raghuram Vadapally, taxpayer_tin=049-98-0888, property_description="4,000,000 shares of Synexar, Inc.", transfer_date=2025-11-25, fmv_total ≈ $400, service_recipient_ein=41-2773035, signed_at_utc=2025-11-26T02:43:14Z |

## Versioning

- Version constant: `corporate_v1`. Mirrored in C# as `CorporateSchemaV1Constants` (sibling to `EmploymentSchemaV1Constants` / `NdaSchemaV1Constants`).
- Reuses `PartyRecord`, `AddressRecord`, `MoneyRecord`, `EquityGrant`, `VestingTerms`, `ProRataIncrement`, `SignatureRecord` from `PracticeX.Discovery.Schemas` — no redefinition.

## Open questions for user review

- **Cap table (sample 16)** — separate top-level entity (current proposal) or snapshot tied to each `board_consent` that issues shares? Latter trades query simplicity for tighter provenance.
- **83(b) `signed_at_utc` parsing** — printed format `"RAGHURAM VADAPALLY 2025.11.26 02:43:14 +0000"` is non-standard (dotted date, signer prefix). Parse on ingest or always require manual review?
- **`founder_agreement.is_template = true` as hard NO-promote** — should a founder agreement with placeholder `effective_date` / `governing_law` be barred from becoming a canonical record until placeholders are filled? Sample 13 is the motivating case.
- **`board_consent.share_authorizations` linkage** — link to a future `share_pool` entity tracking remaining unissued capacity, or keep point-in-time inside the consent? Pool enables "how much of the 2M Equity Plan is left?" queries; point-in-time is simpler.
