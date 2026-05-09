# Counsel's Memo — Stage-2 JSON Extraction

You read the markdown Counsel's Memo above and emit a single JSON object
matching the schema. The memo is ground truth. Do not infer beyond it.

**No prose. No markdown. No code fences. JSON only.**

---

## SCHEMA

```json
{
  "risk_score": 0,
  "risk_rating": "low | modest | elevated | high | severe",
  "headline": "<one-sentence rationale from §1>",
  "issues": [
    {
      "rank": 1,
      "severity": "CRITICAL | HIGH | MEDIUM | LOW",
      "category": "<one of the master prompt category tokens>",
      "title": "<short title>",
      "where": "<section / clause reference>",
      "risk": "<2-4 sentences>",
      "non_standard_reason": "<comparison to market norm or balanced draft>"
    }
  ],
  "redlines": [
    {
      "issue_rank": 1,
      "current_language": "<verbatim or paraphrase>",
      "proposed_language": "<concrete replacement>",
      "rationale": "<1-2 sentences>"
    }
  ],
  "operational_watch_items": [
    "<item if document is signed, else empty array>"
  ],
  "material_disclosures": {
    "board": ["<bullet>"],
    "insurer": ["<bullet>"],
    "lender": ["<bullet>"],
    "ma_due_diligence": ["<bullet>"],
    "regulators": ["<bullet>"]
  },
  "counterparty_posture": "<2-3 sentence summary from §6>",
  "action_items": [
    {
      "rank": 1,
      "owner": "GC | CFO | CEO | Outside_Counsel | Insurance_Broker | HR | IT_Security | Operations",
      "action": "<imperative>",
      "by": "<relative date or trigger>",
      "why_now": "<1 sentence>",
      "done_looks_like": "<1 sentence>"
    }
  ],
  "plain_english_summary": "<3-6 sentences, 8th-grade reading level>"
}
```

---

## RULES

- `risk_score` is an integer 0-100, copied verbatim from the §1
  "Risk Score: N / 100" line.
- `risk_rating` is the one-word token from the same §1 line, lowercased.
- Each issue's `severity` is one of `CRITICAL`, `HIGH`, `MEDIUM`, `LOW`
  (uppercase).
- `category` token must come from the master prompt's category list.
- If a section in the memo is empty (e.g., no CRITICAL/HIGH issues hence
  no redlines, or document is unsigned hence no operational watch items),
  return an empty array for the corresponding field.
- For `material_disclosures`, every category key must be present. If the
  memo wrote "Nothing above the threshold for [category]," return an
  empty array for that key.
- `action_items.owner` token must be from the enumeration above. Default
  to `GC` if the memo did not specify.

---

## INPUT

**Counsel's Memo:**

{LEGAL_MEMO}
