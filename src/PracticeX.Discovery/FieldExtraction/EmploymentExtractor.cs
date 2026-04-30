using System.Globalization;
using System.Text.RegularExpressions;
using PracticeX.Discovery.FieldExtraction.Helpers;
using PracticeX.Discovery.Schemas;

namespace PracticeX.Discovery.FieldExtraction;

/// <summary>
/// v1 Employment-family field extractor — regex-only scaffold. Detects the
/// subtype (offer letter / engagement letter / advisor agreement / CIIA /
/// PHI agreement), pulls common fields (effective date, governing law,
/// parties, signature block), and delegates a small per-subtype block for
/// the headline numeric fields. LLM-quality extraction is v2.
/// </summary>
public sealed class EmploymentExtractor : IContractFieldExtractor
{
    public string Name => "employment-extractor-v1";
    public string SchemaVersion => EmploymentSchemaV1Constants.Version;

    public bool CanExtract(string subtypeOrCandidateType)
    {
        if (string.IsNullOrWhiteSpace(subtypeOrCandidateType)) return false;
        var key = subtypeOrCandidateType.Trim();
        if (string.Equals(key, EmploymentSchemaV1Constants.CandidateType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return EmploymentSchemaV1Constants.Subtypes.All.Any(
            s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase));
    }

    public FieldExtractionResult Extract(FieldExtractionInput input)
    {
        var fileName = input.FileName ?? string.Empty;
        var body = input.FullText ?? string.Empty;
        var subtype = DetectSubtype(fileName, body);

        var fields = new Dictionary<string, ExtractedField>(StringComparer.OrdinalIgnoreCase);
        var reasons = new List<string> { "employment_extractor_v1", $"subtype_detected:{subtype}" };
        var isTemplate = false;
        var isExecuted = false;

        // ---- Effective date ----
        var (effectiveDate, effectiveCitation, effectiveTemplate) = ExtractEffectiveDate(body);
        fields["effective_date"] = new ExtractedField
        {
            Name = "effective_date",
            Value = effectiveDate,
            Confidence = effectiveDate is not null ? 0.85m : (effectiveTemplate ? 0.3m : 0.0m),
            SourceCitation = effectiveCitation
        };
        if (effectiveTemplate) isTemplate = true;

        // ---- Governing law ----
        var (governingLaw, lawCitation, lawTemplate) = ExtractGoverningLaw(body);
        fields["governing_law"] = new ExtractedField
        {
            Name = "governing_law",
            Value = governingLaw,
            Confidence = governingLaw is not null ? 0.85m : (lawTemplate ? 0.3m : 0.0m),
            SourceCitation = lawCitation
        };
        if (lawTemplate) isTemplate = true;

        // ---- Parties (best-effort) ----
        var parties = ExtractParties(body);
        fields["parties"] = new ExtractedField
        {
            Name = "parties",
            Value = parties,
            Confidence = parties.Count > 0 ? 0.5m : 0.0m
        };

        // ---- Signature block ----
        var signatures = new List<SignatureRecord>();
        if (!string.IsNullOrWhiteSpace(input.SignatureProvider))
        {
            signatures.Add(new SignatureRecord(
                SignerName: "",
                SignerTitle: null,
                SignerRole: null,
                SignedAtUtc: null,
                SignatureProvider: input.SignatureProvider!,
                EnvelopeId: input.DocusignEnvelopeId,
                PageNumber: null));
            isExecuted = true;
        }
        fields["signature_block"] = new ExtractedField
        {
            Name = "signature_block",
            Value = signatures,
            Confidence = isExecuted ? 0.85m : 0.0m
        };

        // ---- Subtype-specific extraction ----
        switch (subtype)
        {
            case EmploymentSchemaV1Constants.Subtypes.OfferLetter:
                ExtractOfferLetterFields(body, fields);
                break;
            case EmploymentSchemaV1Constants.Subtypes.AdvisorAgreement:
                ExtractAdvisorAgreementFields(body, fields);
                break;
            case EmploymentSchemaV1Constants.Subtypes.Ciia:
                fields["ip_assignment_scope"] = new ExtractedField
                {
                    Name = "ip_assignment_scope",
                    Value = null,
                    Confidence = 0.0m
                };
                break;
            case EmploymentSchemaV1Constants.Subtypes.PhiAgreement:
                ExtractPhiAgreementFields(body, fields);
                break;
            case EmploymentSchemaV1Constants.Subtypes.EngagementLetter:
                ExtractEngagementLetterFields(body, input.Headings, fields);
                break;
        }

        if (isTemplate) reasons.Add("template_placeholders_present");
        if (isExecuted) reasons.Add("signature_attached");

        var missingKeyFields = CountMissingKeyFields(fields, subtype);
        if (missingKeyFields >= 2) reasons.Add("extraction_partial");

        return new FieldExtractionResult
        {
            SchemaVersion = SchemaVersion,
            Subtype = subtype,
            Fields = fields,
            IsTemplate = isTemplate,
            IsExecuted = isExecuted,
            ReasonCodes = reasons
        };
    }

    private static string DetectSubtype(string fileName, string body)
    {
        var fn = fileName.ToLowerInvariant();
        if (fn.Contains("offer")) return EmploymentSchemaV1Constants.Subtypes.OfferLetter;
        if (fn.Contains("engagement")) return EmploymentSchemaV1Constants.Subtypes.EngagementLetter;
        if (fn.Contains("advisor")) return EmploymentSchemaV1Constants.Subtypes.AdvisorAgreement;
        if (fn.Contains("ciia")) return EmploymentSchemaV1Constants.Subtypes.Ciia;
        if (fn.Contains("phi")) return EmploymentSchemaV1Constants.Subtypes.PhiAgreement;

        if (body.Contains("Advisory Services", StringComparison.OrdinalIgnoreCase))
            return EmploymentSchemaV1Constants.Subtypes.AdvisorAgreement;
        if (body.Contains("Confidential Information and Invention Assignment", StringComparison.OrdinalIgnoreCase))
            return EmploymentSchemaV1Constants.Subtypes.Ciia;
        // PHI agreements have a definitive structure. Don't trigger on generic
        // HIPAA mentions inside offer letters or compliance boilerplate —
        // require either the BAA title phrase or a defined-term party label.
        if (PhiStructuralRegex.IsMatch(body))
            return EmploymentSchemaV1Constants.Subtypes.PhiAgreement;

        return EmploymentSchemaV1Constants.Subtypes.AdvisorAgreement;
    }

    private static readonly Regex PhiStructuralRegex = new(
        @"Business\s+Associate\s+Agreement|\(\s*""?\s*Business\s+Associate\s*""?\s*\)|\(\s*""?\s*Covered\s+Entity\s*""?\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EffectiveDateRegex = new(
        @"(?<lead>Effective Date:?\s+|effective as of\s+|dated\s+)(?<value>[A-Z][a-z]+\s+\d{1,2},?\s+\d{4}|\d{1,2}/\d{1,2}/\d{4}|\d{4}-\d{2}-\d{2}|_{3,}|\[[^\]]+\])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (DateTimeOffset? value, string? citation, bool template) ExtractEffectiveDate(string body)
    {
        var match = EffectiveDateRegex.Match(body);
        if (!match.Success) return (null, null, false);
        var raw = match.Groups["value"].Value;
        if (RegexHelpers.LooksLikePlaceholder(raw))
        {
            return (null, "effective_date placeholder", true);
        }
        var parsed = RegexHelpers.ParseDate(raw);
        return (parsed, parsed is null ? null : $"effective date: {raw}", false);
    }

    private static readonly Regex GoverningLawRegex = new(
        @"governed by the laws of (the )?(State of )?(?<value>\[[^\]]+\]|_{3,}|[A-Z][A-Za-z]+)",
        RegexOptions.Compiled);

    private static (string? value, string? citation, bool template) ExtractGoverningLaw(string body)
    {
        var match = GoverningLawRegex.Match(body);
        if (!match.Success) return (null, null, false);
        var raw = match.Groups["value"].Value.Trim();
        if (RegexHelpers.LooksLikePlaceholder(raw))
        {
            return (null, "governing_law placeholder", true);
        }
        return (raw, $"governing law: {raw}", false);
    }

    private static readonly Regex BetweenPartiesRegex = new(
        @"between\s+(?<a>[A-Z][\w&\.\s,]+?)(\s*\(""(?<aRole>[^""]+)""\))?\s+and\s+(?<b>[A-Z][\w&\.\s,]+?)(\s*\(""(?<bRole>[^""]+)""\))?[\.\,]",
        RegexOptions.Compiled);

    private static List<PartyRecord> ExtractParties(string body)
    {
        var list = new List<PartyRecord>();
        var match = BetweenPartiesRegex.Match(body);
        if (!match.Success) return list;
        list.Add(new PartyRecord("organization", match.Groups["a"].Value.Trim(),
            NullIfEmpty(match.Groups["aRole"].Value), null, null, null));
        list.Add(new PartyRecord("person", match.Groups["b"].Value.Trim(),
            NullIfEmpty(match.Groups["bRole"].Value), null, null, null));
        return list;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // -------- Offer letter --------

    private static readonly Regex PositionTitleRegex = new(
        @"as\s+(the\s+|our\s+)?(?<title>[A-Z][\w][\w\s,&\-]+?)(?=[\.,;\n]|\s+(at|with|reporting))",
        RegexOptions.Compiled);

    private static readonly Regex BaseSalaryRegex = new(
        @"\$\s?(?<amount>[0-9]{1,3}(,[0-9]{3})*(\.\d{2})?)\s*(per year|annually|/year)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ExtractOfferLetterFields(string body, IDictionary<string, ExtractedField> fields)
    {
        var titleMatch = PositionTitleRegex.Match(body);
        var title = titleMatch.Success ? titleMatch.Groups["title"].Value.Trim() : null;
        fields["position_title"] = new ExtractedField
        {
            Name = "position_title",
            Value = title,
            Confidence = title is not null ? 0.5m : 0.0m
        };

        var salaryMatch = BaseSalaryRegex.Match(body);
        if (salaryMatch.Success)
        {
            var raw = salaryMatch.Groups["amount"].Value.Replace(",", "");
            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                fields["base_salary"] = new ExtractedField
                {
                    Name = "base_salary",
                    Value = new MoneyRecord(amount, "USD", "year"),
                    Confidence = 0.85m,
                    SourceCitation = $"base salary: {salaryMatch.Value}"
                };
            }
        }
        if (!fields.ContainsKey("base_salary"))
        {
            fields["base_salary"] = new ExtractedField { Name = "base_salary", Value = null, Confidence = 0.0m };
        }

        // start_date — re-uses the effective-date pattern; only applies if a "Start Date:" tag is present.
        var startMatch = Regex.Match(body, @"Start Date:?\s+(?<value>[A-Z][a-z]+\s+\d{1,2},?\s+\d{4}|\d{1,2}/\d{1,2}/\d{4}|\d{4}-\d{2}-\d{2})",
            RegexOptions.IgnoreCase);
        if (startMatch.Success)
        {
            var date = RegexHelpers.ParseDate(startMatch.Groups["value"].Value);
            fields["start_date"] = new ExtractedField
            {
                Name = "start_date",
                Value = date,
                Confidence = date is not null ? 0.85m : 0.3m
            };
        }
    }

    // -------- Advisor agreement --------

    private static readonly Regex CorePctRegex = new(
        @"(?<value>\d+(\.\d+)?)\s*%\s+of\s+fully\s+diluted",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VestRegex = new(
        @"(?<months>\d+)[\-\s]?month\s+vest",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CliffRegex = new(
        @"(?<months>\d+)[\-\s]?month\s+cliff",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GrowthGrantRegex = new(
        @"(?<pct>\d+(\.\d+)?)\s*%\s+per\s+\$(?<unit>\d+(\.\d+)?)\s*(?<unitWord>[KMB])\s+(?<unitDesc>[A-Za-z][A-Za-z\s]+?ARR)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CapRegex = new(
        @"capped\s+at\s+(?<pct>\d+(\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProRataRegex = new(
        @"pro[\-\s]?rata\s+(?<pct>\d+(\.\d+)?)\s*%\s+per\s+\$(?<unit>\d+(\.\d+)?)\s*(?<unitWord>[KMB])(?<unitDesc>[\sA-Za-z]*?ARR)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ExtractAdvisorAgreementFields(string body, IDictionary<string, ExtractedField> fields)
    {
        var grants = new List<EquityGrant>();

        var vestMonths = VestRegex.Match(body) is { Success: true } v && int.TryParse(v.Groups["months"].Value, out var vm) ? vm : 24;
        var cliffMonths = CliffRegex.Match(body) is { Success: true } c && int.TryParse(c.Groups["months"].Value, out var cm) ? cm : 0;
        var vesting = new VestingTerms(vestMonths, cliffMonths, "monthly", null);

        var coreMatch = CorePctRegex.Match(body);
        if (coreMatch.Success && decimal.TryParse(coreMatch.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var corePct))
        {
            grants.Add(new EquityGrant(
                Type: "core_advisory",
                PercentageOfFullyDiluted: corePct,
                ShareCount: null,
                Vesting: vesting,
                CapPercentage: null,
                ProRata: null));
        }

        var growthMatch = GrowthGrantRegex.Match(body);
        if (growthMatch.Success)
        {
            decimal? cap = null;
            var capMatch = CapRegex.Match(body);
            if (capMatch.Success && decimal.TryParse(capMatch.Groups["pct"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var capPct))
            {
                cap = capPct;
            }

            ProRataIncrement? proRata = null;
            var prMatch = ProRataRegex.Match(body);
            if (prMatch.Success &&
                decimal.TryParse(prMatch.Groups["pct"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var prPct))
            {
                var unit = prMatch.Groups["unit"].Value;
                var unitWord = prMatch.Groups["unitWord"].Value.ToUpperInvariant();
                var unitDesc = prMatch.Groups["unitDesc"].Value.Trim();
                if (string.IsNullOrEmpty(unitDesc)) unitDesc = "Net New ARR";
                // Convert percent-string to decimal fraction (0.10% -> 0.001m)
                proRata = new ProRataIncrement(prPct / 100m, $"${unit}{unitWord} {unitDesc}".Trim());
            }

            grants.Add(new EquityGrant(
                Type: "growth",
                PercentageOfFullyDiluted: null,
                ShareCount: null,
                Vesting: vesting,
                CapPercentage: cap,
                ProRata: proRata));
        }

        fields["equity_grants"] = new ExtractedField
        {
            Name = "equity_grants",
            Value = grants,
            Confidence = grants.Count > 0 ? 0.85m : 0.0m
        };
    }

    // -------- PHI agreement --------

    private static readonly Regex BreachWindowRegex = new(
        @"breach\s+notification.{0,40}?(?<days>\d{1,3})\s+days",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static void ExtractPhiAgreementFields(string body, IDictionary<string, ExtractedField> fields)
    {
        // covered_entity vs business_associate
        string? coveredEntity = null;
        string? businessAssociate = null;

        var ceMatch = Regex.Match(body, @"(?<name>[A-Z][\w&\.\s,]+?)\s*\(""?Covered\s+Entity""?\)", RegexOptions.IgnoreCase);
        if (ceMatch.Success) coveredEntity = ceMatch.Groups["name"].Value.Trim();

        var baMatch = Regex.Match(body, @"(?<name>[A-Z][\w&\.\s,]+?)\s*\(""?Business\s+Associate""?\)", RegexOptions.IgnoreCase);
        if (baMatch.Success) businessAssociate = baMatch.Groups["name"].Value.Trim();

        fields["covered_entity"] = new ExtractedField
        {
            Name = "covered_entity",
            Value = coveredEntity,
            Confidence = coveredEntity is not null ? 0.5m : 0.0m
        };
        fields["business_associate"] = new ExtractedField
        {
            Name = "business_associate",
            Value = businessAssociate,
            Confidence = businessAssociate is not null ? 0.5m : 0.0m
        };

        var breachMatch = BreachWindowRegex.Match(body);
        int windowDays;
        decimal confidence;
        if (breachMatch.Success && int.TryParse(breachMatch.Groups["days"].Value, out var d))
        {
            windowDays = d;
            confidence = 0.85m;
        }
        else
        {
            windowDays = EmploymentSchemaV1Constants.DefaultPhiBreachNotificationDays;
            confidence = 0.3m;
        }
        fields["breach_notification"] = new ExtractedField
        {
            Name = "breach_notification",
            Value = new Dictionary<string, object?> { ["window_days"] = windowDays },
            Confidence = confidence
        };
    }

    // -------- Engagement letter --------

    private static void ExtractEngagementLetterFields(
        string body,
        IReadOnlyList<TextExtraction.ExtractedHeading> headings,
        IDictionary<string, ExtractedField> fields)
    {
        string? scope = null;
        for (var i = 0; i < headings.Count; i++)
        {
            var h = headings[i].Text ?? string.Empty;
            if (h.Contains("Scope", StringComparison.OrdinalIgnoreCase) ||
                h.Contains("Services", StringComparison.OrdinalIgnoreCase))
            {
                // Take the first paragraph after the heading by index of heading text in body.
                var idx = body.IndexOf(h, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var after = body[(idx + h.Length)..];
                    var paragraph = after.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0);
                    if (!string.IsNullOrWhiteSpace(paragraph))
                    {
                        scope = paragraph;
                        break;
                    }
                }
            }
        }

        // Fallback — search for "Scope of Services" inline.
        if (scope is null)
        {
            var inline = Regex.Match(body, @"(Scope\s+of\s+Services|Services)[:\s]+(?<text>[^\r\n]+)",
                RegexOptions.IgnoreCase);
            if (inline.Success) scope = inline.Groups["text"].Value.Trim();
        }

        fields["engagement_scope"] = new ExtractedField
        {
            Name = "engagement_scope",
            Value = scope,
            Confidence = scope is not null ? 0.5m : 0.0m
        };

        var feeMatch = BaseSalaryRegex.Match(body);
        if (feeMatch.Success)
        {
            var raw = feeMatch.Groups["amount"].Value.Replace(",", "");
            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                fields["fees"] = new ExtractedField
                {
                    Name = "fees",
                    Value = new MoneyRecord(amount, "USD", "year"),
                    Confidence = 0.85m
                };
            }
        }
        if (!fields.ContainsKey("fees"))
        {
            fields["fees"] = new ExtractedField { Name = "fees", Value = null, Confidence = 0.0m };
        }
    }

    private static int CountMissingKeyFields(IReadOnlyDictionary<string, ExtractedField> fields, string subtype)
    {
        var keyNames = subtype switch
        {
            EmploymentSchemaV1Constants.Subtypes.OfferLetter =>
                new[] { "effective_date", "governing_law", "parties", "position_title", "base_salary" },
            EmploymentSchemaV1Constants.Subtypes.AdvisorAgreement =>
                new[] { "effective_date", "governing_law", "parties", "equity_grants" },
            EmploymentSchemaV1Constants.Subtypes.Ciia =>
                new[] { "effective_date", "parties", "ip_assignment_scope" },
            EmploymentSchemaV1Constants.Subtypes.PhiAgreement =>
                new[] { "effective_date", "covered_entity", "business_associate" },
            EmploymentSchemaV1Constants.Subtypes.EngagementLetter =>
                new[] { "effective_date", "governing_law", "parties", "engagement_scope", "fees" },
            _ => new[] { "effective_date", "governing_law", "parties" }
        };

        var missing = 0;
        foreach (var name in keyNames)
        {
            if (!fields.TryGetValue(name, out var f) || f.Value is null) missing++;
            else if (f.Value is System.Collections.ICollection coll && coll.Count == 0) missing++;
        }
        return missing;
    }
}

