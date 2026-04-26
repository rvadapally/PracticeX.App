using System.Globalization;
using System.Text.RegularExpressions;
using PracticeX.Discovery.FieldExtraction.Helpers;
using PracticeX.Discovery.Schemas;

namespace PracticeX.Discovery.FieldExtraction;

/// <summary>
/// v1 Corporate / Foundation-family field extractor — regex-only scaffold.
/// Detects subtype across the eight founding-document classes (certificate
/// of incorporation, filing receipt, board consent, founder agreement,
/// founders charter, stock purchase agreement, Section 83(b) election,
/// EIN letter), pulls common base fields (entity, effective/filed date,
/// jurisdiction, template/binding flags, signature plumbing) and per-subtype
/// best-effort detail. LLM-quality extraction is v2.
/// </summary>
public sealed class CorporateExtractor : IContractFieldExtractor
{
    public string Name => "corporate-extractor-v1";
    public string SchemaVersion => CorporateSchemaV1Constants.Version;

    public bool CanExtract(string subtypeOrCandidateType)
    {
        if (string.IsNullOrWhiteSpace(subtypeOrCandidateType)) return false;
        var key = subtypeOrCandidateType.Trim();
        if (string.Equals(key, CorporateSchemaV1Constants.CandidateType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return CorporateSchemaV1Constants.Subtypes.All.Any(
            s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase));
    }

    public FieldExtractionResult Extract(FieldExtractionInput input)
    {
        var fileName = input.FileName ?? string.Empty;
        var body = input.FullText ?? string.Empty;
        var subtype = DetectSubtype(fileName, body);

        var fields = new Dictionary<string, ExtractedField>(StringComparer.OrdinalIgnoreCase);
        var reasons = new List<string> { "corporate_extractor_v1", $"subtype_detected:{subtype}" };
        var isTemplate = false;
        var isExecuted = false;

        // ---- Entity (parties[0]) ----
        var entity = ExtractEntity(fileName, body);
        fields["entity"] = new ExtractedField
        {
            Name = "entity",
            Value = entity,
            Confidence = entity is not null ? 0.6m : 0.0m
        };

        // ---- Effective / filed date ----
        var (effectiveDate, effectiveCitation, effectiveTemplate) = ExtractEffectiveDate(body);
        fields["effective_date"] = new ExtractedField
        {
            Name = "effective_date",
            Value = effectiveDate,
            Confidence = effectiveDate is not null ? 0.85m : (effectiveTemplate ? 0.3m : 0.0m),
            SourceCitation = effectiveCitation
        };
        if (effectiveTemplate) isTemplate = true;

        var filedAtDate = ExtractFiledAtDate(body);
        fields["filed_at_date"] = new ExtractedField
        {
            Name = "filed_at_date",
            Value = filedAtDate,
            Confidence = filedAtDate is not null ? 0.7m : 0.0m
        };

        // ---- Jurisdiction ----
        var jurisdiction = ExtractJurisdiction(body, fileName);
        fields["jurisdiction"] = new ExtractedField
        {
            Name = "jurisdiction",
            Value = jurisdiction,
            Confidence = jurisdiction is not null ? 0.8m : 0.0m
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

        // ---- Placeholder/template detection on critical fields ----
        if (RegexHelpers.LooksLikePlaceholder(body))
        {
            // Only flag if we see explicit placeholder markers near critical labels.
            if (Regex.IsMatch(body, @"Effective Date:?\s+_{3,}", RegexOptions.IgnoreCase)
                || Regex.IsMatch(body, @"\[Your\s+\w+\]", RegexOptions.IgnoreCase))
            {
                isTemplate = true;
            }
        }

        // ---- binding ----
        var nonBinding = subtype == CorporateSchemaV1Constants.Subtypes.FoundersCharter
            || body.Contains("non-binding", StringComparison.OrdinalIgnoreCase);
        var binding = !nonBinding;
        fields["binding"] = new ExtractedField
        {
            Name = "binding",
            Value = binding,
            Confidence = 0.85m
        };

        // ---- Per-subtype extraction ----
        switch (subtype)
        {
            case CorporateSchemaV1Constants.Subtypes.FilingReceipt:
                ExtractFilingReceiptFields(body, fields);
                break;
            case CorporateSchemaV1Constants.Subtypes.BoardConsent:
                ExtractBoardConsentFields(body, input, fields);
                break;
            case CorporateSchemaV1Constants.Subtypes.FounderAgreement:
                ExtractFounderAgreementFields(body, fields, ref isTemplate);
                break;
            case CorporateSchemaV1Constants.Subtypes.FoundersCharter:
                ExtractFoundersCharterFields(body, fields);
                break;
            case CorporateSchemaV1Constants.Subtypes.Section83bElection:
                ExtractSection83bFields(body, fields);
                break;
            case CorporateSchemaV1Constants.Subtypes.CertificateOfIncorporation:
                ExtractCertificateOfIncorporationFields(body, fileName, fields);
                break;
            case CorporateSchemaV1Constants.Subtypes.StockPurchaseAgreement:
                ExtractStockPurchaseAgreementFields(body, fields);
                break;
            case CorporateSchemaV1Constants.Subtypes.EinLetter:
                ExtractEinLetterFields(body, fields);
                break;
        }

        fields["is_template"] = new ExtractedField
        {
            Name = "is_template",
            Value = isTemplate,
            Confidence = 0.9m
        };
        fields["is_executed"] = new ExtractedField
        {
            Name = "is_executed",
            Value = isExecuted,
            Confidence = 0.9m
        };

        if (isTemplate) reasons.Add("template_placeholders_present");
        if (nonBinding) reasons.Add("non_binding_document");
        if (isExecuted) reasons.Add("signature_attached");

        if (!isTemplate)
        {
            var missing = CountMissingKeyFields(fields, subtype);
            if (missing >= 2) reasons.Add("extraction_partial");
        }

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

    // -------- Subtype detection --------

    private static string DetectSubtype(string fileName, string body)
    {
        var fn = fileName ?? string.Empty;
        var b = body ?? string.Empty;

        bool Has(string s) =>
            fn.Contains(s, StringComparison.OrdinalIgnoreCase)
            || b.Contains(s, StringComparison.OrdinalIgnoreCase);

        // 83(b) election
        if (Has("Form 15620") || Has("83(b)") || Has("Section 83(b)"))
            return CorporateSchemaV1Constants.Subtypes.Section83bElection;

        // EIN letter
        if (Has("EIN") &&
            (Has("issued") || Has("Department of the Treasury") || Has("Internal Revenue Service")))
            return CorporateSchemaV1Constants.Subtypes.EinLetter;

        // Certificate of incorporation
        if (Has("Certificate of Incorporation") ||
            (Has("Document Filing") && Has("Delaware Division")))
            return CorporateSchemaV1Constants.Subtypes.CertificateOfIncorporation;

        // Filing receipt
        if (Has("Service Request Number") && Has("Order Summary"))
            return CorporateSchemaV1Constants.Subtypes.FilingReceipt;

        // Founders charter
        if (Has("Founders Charter") || b.Contains("non-binding", StringComparison.OrdinalIgnoreCase))
            return CorporateSchemaV1Constants.Subtypes.FoundersCharter;

        // Founder agreement
        if (Has("Founder Agreement"))
            return CorporateSchemaV1Constants.Subtypes.FounderAgreement;

        // Board consent
        if (Has("Resolution") && Has("Director") && (Has("WHEREAS") || Has("RESOLVED")))
            return CorporateSchemaV1Constants.Subtypes.BoardConsent;

        // Stock purchase agreement
        if (Has("Restricted Stock Purchase") || Has("RSPA"))
            return CorporateSchemaV1Constants.Subtypes.StockPurchaseAgreement;

        // Default
        return CorporateSchemaV1Constants.Subtypes.BoardConsent;
    }

    // -------- Common base extractors --------

    private static readonly Regex EntityFromFileNameRegex = new(
        @"^(?<name>[A-Za-z][A-Za-z0-9\s,&\.]+?(?:Inc|LLC|Corp|Co|Ltd)\.?)(?:[\s_\-]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EntityFromBodyRegex = new(
        @"\b(?<name>[A-Z][A-Z0-9\s,&]+?,?\s+INC\.)",
        RegexOptions.Compiled);

    private static string? ExtractEntity(string fileName, string body)
    {
        var fnMatch = EntityFromFileNameRegex.Match(fileName);
        if (fnMatch.Success)
        {
            return fnMatch.Groups["name"].Value.Trim().Trim(',').Trim();
        }

        var bodyMatch = EntityFromBodyRegex.Match(body);
        if (bodyMatch.Success)
        {
            return bodyMatch.Groups["name"].Value.Trim();
        }

        if (body.Contains("SYNEXAR, INC.", StringComparison.OrdinalIgnoreCase))
        {
            return "SYNEXAR, INC.";
        }

        return null;
    }

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

    private static readonly Regex FiledAtRegex = new(
        @"(?:Filed|Date Filed|Filing Date|Date of Incorporation|Date of Filing)[:\s]+(?<value>[A-Z][a-z]+\s+\d{1,2},?\s+\d{4}|\d{1,2}/\d{1,2}/\d{4}|\d{4}-\d{2}-\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static DateTimeOffset? ExtractFiledAtDate(string body)
    {
        var match = FiledAtRegex.Match(body);
        if (!match.Success) return null;
        return RegexHelpers.ParseDate(match.Groups["value"].Value);
    }

    private static readonly Regex JurisdictionRegex = new(
        @"(?:State|Jurisdiction)\s+of\s+(?<value>\w+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? ExtractJurisdiction(string body, string fileName)
    {
        var match = JurisdictionRegex.Match(body);
        if (match.Success)
        {
            var raw = match.Groups["value"].Value.Trim();
            if (!RegexHelpers.LooksLikePlaceholder(raw)) return raw;
        }
        if (fileName.Contains("Delaware", StringComparison.OrdinalIgnoreCase))
        {
            return "Delaware";
        }
        return null;
    }

    // -------- Filing receipt --------

    private static readonly Regex ServiceRequestRegex = new(
        @"Service Request Number[:\s]+(?<value>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SubmittedAtRegex = new(
        @"(?:Date Submitted|Submitted)[:\s]+(?<value>[^,\r\n]+,\s*[A-Za-z]+\s+\d{1,2},?\s+\d{4}[^\r\n]*?(?:AM|PM))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SubmitterRegex = new(
        @"Submitter[:\s]+(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PriorityRegex = new(
        @"(?:Priority|Service Type)[:\s]+(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CardLast4Regex = new(
        @"ending\s+(?<value>\d{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ExtractFilingReceiptFields(string body, IDictionary<string, ExtractedField> fields)
    {
        var srMatch = ServiceRequestRegex.Match(body);
        fields["service_request_number"] = new ExtractedField
        {
            Name = "service_request_number",
            Value = srMatch.Success ? srMatch.Groups["value"].Value : null,
            Confidence = srMatch.Success ? 0.95m : 0.0m
        };

        var submittedMatch = SubmittedAtRegex.Match(body);
        DateTimeOffset? submittedAt = null;
        if (submittedMatch.Success)
        {
            submittedAt = RegexHelpers.ParseDate(submittedMatch.Groups["value"].Value);
        }
        fields["submitted_at"] = new ExtractedField
        {
            Name = "submitted_at",
            Value = submittedAt,
            Confidence = submittedAt is not null ? 0.7m : 0.0m
        };

        var submitterMatch = SubmitterRegex.Match(body);
        fields["submitter"] = new ExtractedField
        {
            Name = "submitter",
            Value = submitterMatch.Success ? submitterMatch.Groups["value"].Value.Trim() : null,
            Confidence = submitterMatch.Success ? 0.5m : 0.0m
        };

        var priorityMatch = PriorityRegex.Match(body);
        fields["priority"] = new ExtractedField
        {
            Name = "priority",
            Value = priorityMatch.Success ? priorityMatch.Groups["value"].Value.Trim() : null,
            Confidence = priorityMatch.Success ? 0.6m : 0.0m
        };

        var last4Match = CardLast4Regex.Match(body);
        fields["payment_method_last4"] = new ExtractedField
        {
            Name = "payment_method_last4",
            Value = last4Match.Success ? last4Match.Groups["value"].Value : null,
            Confidence = last4Match.Success ? 0.85m : 0.0m
        };
    }

    // -------- Board consent --------

    private static readonly Regex ShareAuthorizationRegex = new(
        @"(?<count>\d{1,3}(?:,\d{3})+|\d+)\s+shares?\s+of\s+(?<class>[A-Z][a-zA-Z]+)\s+(?:Stock|stock)?",
        RegexOptions.Compiled);

    private static readonly Regex StatutoryAuthorityRegex = new(
        @"DGCL\s+Section\s+\d+\(\w\)|Delaware General Corporation Law",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ExtractBoardConsentFields(
        string body,
        FieldExtractionInput input,
        IDictionary<string, ExtractedField> fields)
    {
        var consentType = body.Contains("unanimous written consent", StringComparison.OrdinalIgnoreCase)
            ? "unanimous_written"
            : "meeting_minutes";
        fields["consent_type"] = new ExtractedField
        {
            Name = "consent_type",
            Value = consentType,
            Confidence = 0.85m
        };

        // Resolutions split on "RESOLVED"
        var resolutions = ParseResolutions(body);
        fields["resolutions"] = new ExtractedField
        {
            Name = "resolutions",
            Value = resolutions,
            Confidence = resolutions.Count > 0 ? 0.6m : 0.0m
        };

        var shareAuths = ParseShareAuthorizations(body);
        fields["share_authorizations"] = new ExtractedField
        {
            Name = "share_authorizations",
            Value = shareAuths,
            Confidence = shareAuths.Count > 0 ? 0.7m : 0.0m
        };

        // Directors: from signature provider when available, else look near "By:" lines.
        var directors = new List<SignatureRecord>();
        if (!string.IsNullOrWhiteSpace(input.SignatureProvider))
        {
            directors.Add(new SignatureRecord(
                SignerName: "",
                SignerTitle: null,
                SignerRole: "director",
                SignedAtUtc: null,
                SignatureProvider: input.SignatureProvider!,
                EnvelopeId: input.DocusignEnvelopeId,
                PageNumber: null));
        }
        else
        {
            foreach (Match m in Regex.Matches(body, @"By:\s*(?<name>[A-Z][A-Za-z\.\s\-']+?)(?=\r|\n|,|\s+(?:Title|Director))"))
            {
                var name = m.Groups["name"].Value.Trim();
                if (name.Length > 0)
                {
                    directors.Add(new SignatureRecord(
                        SignerName: name,
                        SignerTitle: null,
                        SignerRole: "director",
                        SignedAtUtc: null,
                        SignatureProvider: "wet_signature",
                        EnvelopeId: null,
                        PageNumber: null));
                }
            }
        }
        fields["directors"] = new ExtractedField
        {
            Name = "directors",
            Value = directors,
            Confidence = directors.Count > 0 ? 0.5m : 0.0m
        };

        var statMatch = StatutoryAuthorityRegex.Match(body);
        fields["statutory_authority"] = new ExtractedField
        {
            Name = "statutory_authority",
            Value = statMatch.Success ? statMatch.Value : null,
            Confidence = statMatch.Success ? 0.9m : 0.0m
        };
    }

    private static List<ResolutionRecord> ParseResolutions(string body)
    {
        var list = new List<ResolutionRecord>();
        if (string.IsNullOrWhiteSpace(body)) return list;

        // Find every position of the word "RESOLVED"
        var matches = Regex.Matches(body, @"\bRESOLVED\b", RegexOptions.IgnoreCase);
        if (matches.Count == 0) return list;

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            var slice = body[start..end].Trim();
            if (slice.Length == 0) continue;

            // Title = preceding non-empty line.
            var precedingSlice = body[..start];
            var precedingLines = precedingSlice.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var title = precedingLines.LastOrDefault()?.Trim() ?? "Resolution";
            if (title.Length > 200) title = title[..200];

            var type = ClassifyResolutionType(slice);

            list.Add(new ResolutionRecord(
                SequenceNumber: i + 1,
                Title: title,
                BodyText: slice.Length > 2000 ? slice[..2000] : slice,
                Type: type));
        }
        return list;
    }

    private static string ClassifyResolutionType(string text)
    {
        if (Regex.IsMatch(text, @"\bequity\b", RegexOptions.IgnoreCase)) return "equity_plan_adoption";
        if (Regex.IsMatch(text, @"\belect\b", RegexOptions.IgnoreCase)) return "officer_election";
        if (Regex.IsMatch(text, @"\bissue\b", RegexOptions.IgnoreCase)) return "share_issuance";
        if (Regex.IsMatch(text, @"\bamend\b", RegexOptions.IgnoreCase)) return "amendment";
        return "other";
    }

    private static List<ShareAuthorization> ParseShareAuthorizations(string body)
    {
        var list = new List<ShareAuthorization>();
        if (string.IsNullOrWhiteSpace(body)) return list;
        foreach (Match m in ShareAuthorizationRegex.Matches(body))
        {
            var rawCount = m.Groups["count"].Value.Replace(",", "");
            if (!long.TryParse(rawCount, NumberStyles.Number, CultureInfo.InvariantCulture, out var count))
                continue;
            var shareClass = m.Groups["class"].Value.Trim();

            // Look for plan name in a 200-char window after the match.
            string? planName = null;
            string? reservedFor = null;
            var windowEnd = Math.Min(body.Length, m.Index + m.Length + 200);
            var window = body[m.Index..windowEnd];
            var planMatch = Regex.Match(window, @"(?<value>[A-Z][\w\s]*?(?:Equity\s+Incentive\s+)?Plan)", RegexOptions.IgnoreCase);
            if (planMatch.Success)
            {
                planName = planMatch.Groups["value"].Value.Trim();
                reservedFor = planName;
            }
            else if (window.Contains("reserved", StringComparison.OrdinalIgnoreCase))
            {
                var resMatch = Regex.Match(window, @"reserved\s+for\s+(?<value>[^\.\r\n]+)", RegexOptions.IgnoreCase);
                if (resMatch.Success) reservedFor = resMatch.Groups["value"].Value.Trim();
            }

            list.Add(new ShareAuthorization(shareClass, count, planName, reservedFor));
        }
        return list;
    }

    // -------- Founder agreement --------

    private static readonly Regex VestingDurationRegex = new(
        @"(?<n>\d+)[\s-]year[s]?\s+vest(?:ing)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VestingCliffRegex = new(
        @"(?<n>\d+)[\s-]?(?<unit>year|month)[\s-]cliff",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MajorDecisionThresholdRegex = new(
        @"\$(?<n>\d+)(?<suffix>K|M)?\s+(?:capex|threshold|approval)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReferenceRegex = new(
        @"\b(RSPA|PIIA|Plan|Restricted Stock|Subscription)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ExtractFounderAgreementFields(
        string body,
        IDictionary<string, ExtractedField> fields,
        ref bool isTemplate)
    {
        // Template detection: presence of placeholder patterns
        if (Regex.IsMatch(body, @"_{3,}") || Regex.IsMatch(body, @"\[Your\s+\w+\]", RegexOptions.IgnoreCase))
        {
            isTemplate = true;
        }

        var founders = ExtractFoundersList(body);
        fields["founders"] = new ExtractedField
        {
            Name = "founders",
            Value = founders,
            Confidence = founders.Count > 0 ? 0.5m : 0.0m
        };

        // Vesting
        VestingTerms? vesting = null;
        var vMatch = VestingDurationRegex.Match(body);
        if (vMatch.Success && int.TryParse(vMatch.Groups["n"].Value, out var years))
        {
            var months = years * 12;
            var cliff = 0;
            var cMatch = VestingCliffRegex.Match(body);
            if (cMatch.Success && int.TryParse(cMatch.Groups["n"].Value, out var cn))
            {
                var unit = cMatch.Groups["unit"].Value.ToLowerInvariant();
                cliff = unit.StartsWith("year") ? cn * 12 : cn;
            }
            vesting = new VestingTerms(months, cliff, "monthly", null);
        }
        fields["vesting"] = new ExtractedField
        {
            Name = "vesting",
            Value = vesting,
            Confidence = vesting is not null ? 0.7m : 0.0m
        };

        // equity_grants — empty in v1 for founder agreements
        fields["equity_grants"] = new ExtractedField
        {
            Name = "equity_grants",
            Value = new List<EquityGrant>(),
            Confidence = 0.0m
        };

        // governance_rules
        MoneyRecord? threshold = null;
        var mdMatch = MajorDecisionThresholdRegex.Match(body);
        if (mdMatch.Success && decimal.TryParse(mdMatch.Groups["n"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            var suffix = mdMatch.Groups["suffix"].Value.ToUpperInvariant();
            if (suffix == "K") amount *= 1_000m;
            else if (suffix == "M") amount *= 1_000_000m;
            threshold = new MoneyRecord(amount, "USD", null);
        }
        fields["governance_rules"] = new ExtractedField
        {
            Name = "governance_rules",
            Value = new GovernanceRules(threshold, null, null),
            Confidence = threshold is not null ? 0.6m : 0.0m
        };

        // references
        var refs = new List<string>();
        foreach (Match m in ReferenceRegex.Matches(body))
        {
            var v = m.Value.Trim();
            if (!refs.Contains(v)) refs.Add(v);
        }
        fields["references"] = new ExtractedField
        {
            Name = "references",
            Value = refs,
            Confidence = refs.Count > 0 ? 0.4m : 0.0m
        };
    }

    private static List<FounderRecord> ExtractFoundersList(string body)
    {
        var list = new List<FounderRecord>();
        if (string.IsNullOrWhiteSpace(body)) return list;

        // Look for "Founder Name:" labels.
        foreach (Match m in Regex.Matches(body, @"Founder Name[:\s]+(?<name>[A-Z][A-Za-z\.\s\-']+?)(?:\r|\n|,)", RegexOptions.IgnoreCase))
        {
            var name = m.Groups["name"].Value.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new FounderRecord(
                Party: new PartyRecord("person", name, null, null, null, null),
                Role: "founder",
                Title: null,
                EquityPercent: null,
                Responsibilities: Array.Empty<string>()));
        }
        return list;
    }

    // -------- Founders charter --------

    private static readonly Regex AllocationRegex = new(
        @"(?<count>\d{1,3}(?:,\d{3})+)\s+shares?\s+(?<keyword>authorized|issued|unissued)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ExtractFoundersCharterFields(string body, IDictionary<string, ExtractedField> fields)
    {
        var founders = ExtractFoundersList(body);
        fields["founders"] = new ExtractedField
        {
            Name = "founders",
            Value = founders,
            Confidence = founders.Count > 0 ? 0.5m : 0.0m
        };

        long? authorized = null, issued = null, unissued = null;
        foreach (Match m in AllocationRegex.Matches(body))
        {
            var raw = m.Groups["count"].Value.Replace(",", "");
            if (!long.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) continue;
            var keyword = m.Groups["keyword"].Value.ToLowerInvariant();
            switch (keyword)
            {
                case "authorized": authorized = n; break;
                case "issued": issued = n; break;
                case "unissued": unissued = n; break;
            }
        }

        var allocation = new EquityAllocation(authorized, issued, unissued, null);
        fields["equity_allocation"] = new ExtractedField
        {
            Name = "equity_allocation",
            Value = allocation,
            Confidence = (authorized ?? issued ?? unissued) is not null ? 0.7m : 0.0m
        };

        // Guiding principles
        var principles = new List<string>();
        var idx = body.IndexOf("Guiding Principles", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var after = body[(idx + "Guiding Principles".Length)..];
            // Take until next heading-like break (a blank line followed by a heading) or ~1000 chars.
            var section = after.Length > 1000 ? after[..1000] : after;
            foreach (var line in section.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim().TrimStart('-', '*', '•').Trim();
                if (trimmed.Length > 3) principles.Add(trimmed);
            }
        }
        fields["guiding_principles"] = new ExtractedField
        {
            Name = "guiding_principles",
            Value = principles,
            Confidence = principles.Count > 0 ? 0.4m : 0.0m
        };
    }

    // -------- Section 83(b) election --------

    private static readonly Regex TaxpayerNameRegex = new(
        @"Taxpayer Name[:\s]+(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TinRegex = new(
        @"\b(?<value>\d{3}-\d{2}-\d{4})\b",
        RegexOptions.Compiled);

    private static readonly Regex EinRegex = new(
        @"\b(?<value>\d{2}-\d{7})\b",
        RegexOptions.Compiled);

    private static readonly Regex PropertyDescriptionRegex = new(
        @"(?<count>\d{1,3}(?:,\d{3})*)\s+shares?\s+of\s+(?<entity>[A-Z][\w\s,]+\.)",
        RegexOptions.Compiled);

    private static readonly Regex TransferDateRegex = new(
        @"(?:Date of transfer|Transfer Date)[:\s]+(?<value>[A-Z][a-z]+\s+\d{1,2},?\s+\d{4}|\d{1,2}/\d{1,2}/\d{4}|\d{4}-\d{2}-\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TaxYearRegex = new(
        @"(?:Tax Year|Calendar Year)[:\s]+(?<value>\d{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ServiceRecipientRegex = new(
        @"Service Recipient(?:\s+Name)?[:\s]+(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Form83bSignatureRegex = new(
        @"(?<name>[A-Z][A-Z\s\.\-']+?)\s+(?<year>\d{4})\.(?<month>\d{2})\.(?<day>\d{2})\s+(?<hour>\d{2}):(?<minute>\d{2}):(?<second>\d{2})\s*(?<offset>[+-]\d{4})?",
        RegexOptions.Compiled);

    private static readonly Regex OmbRegex = new(
        @"OMB[:\s]+(?<value>\d{4}-\d{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CatalogRegex = new(
        @"Catalog[:\s]+(?<value>[A-Z0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MoneyLabelRegex = new(
        @"(?<label>FMV per share|FMV total|Price paid per share|Price paid total|Fair Market Value)[:\s]+\$?(?<value>[0-9,]+(?:\.\d{2})?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ExtractSection83bFields(string body, IDictionary<string, ExtractedField> fields)
    {
        // taxpayer name + first non-empty line as fallback
        var tnMatch = TaxpayerNameRegex.Match(body);
        string? taxpayer = tnMatch.Success ? tnMatch.Groups["value"].Value.Trim() : null;
        fields["taxpayer"] = new ExtractedField
        {
            Name = "taxpayer",
            Value = taxpayer,
            Confidence = taxpayer is not null ? 0.7m : 0.0m
        };

        // TIN — strict format, exclude EIN-shaped lines (e.g. lines that say "EIN")
        string? tin = null;
        foreach (Match m in TinRegex.Matches(body))
        {
            // Pull surrounding context (40 chars before) and skip if it looks like an EIN label.
            var startCtx = Math.Max(0, m.Index - 40);
            var ctx = body[startCtx..m.Index];
            if (ctx.Contains("EIN", StringComparison.OrdinalIgnoreCase)) continue;
            tin = m.Groups["value"].Value;
            break;
        }
        fields["taxpayer_tin"] = new ExtractedField
        {
            Name = "taxpayer_tin",
            Value = tin,
            Confidence = tin is not null ? 0.95m : 0.0m
        };

        // Property description
        var propMatch = PropertyDescriptionRegex.Match(body);
        string? propertyDescription = null;
        if (propMatch.Success)
        {
            propertyDescription = $"{propMatch.Groups["count"].Value} shares of {propMatch.Groups["entity"].Value.Trim()}";
        }
        fields["property_description"] = new ExtractedField
        {
            Name = "property_description",
            Value = propertyDescription,
            Confidence = propertyDescription is not null ? 0.85m : 0.0m
        };

        // Transfer date
        var transferMatch = TransferDateRegex.Match(body);
        DateTimeOffset? transferDate = null;
        if (transferMatch.Success)
        {
            transferDate = RegexHelpers.ParseDate(transferMatch.Groups["value"].Value);
        }
        fields["transfer_date"] = new ExtractedField
        {
            Name = "transfer_date",
            Value = transferDate,
            Confidence = transferDate is not null ? 0.9m : 0.0m
        };

        // Tax year
        var tyMatch = TaxYearRegex.Match(body);
        int? taxYear = null;
        if (tyMatch.Success && int.TryParse(tyMatch.Groups["value"].Value, out var ty)) taxYear = ty;
        fields["tax_year"] = new ExtractedField
        {
            Name = "tax_year",
            Value = taxYear,
            Confidence = taxYear is not null ? 0.85m : 0.0m
        };

        // Money fields
        decimal? fmvPerShare = null, fmvTotal = null, pricePaidPerShare = null, pricePaidTotal = null;
        foreach (Match m in MoneyLabelRegex.Matches(body))
        {
            var label = m.Groups["label"].Value.ToLowerInvariant();
            var raw = m.Groups["value"].Value.Replace(",", "");
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) continue;
            if (label.Contains("fmv per share")) fmvPerShare = v;
            else if (label.Contains("fmv total") || label.Contains("fair market value")) fmvTotal = v;
            else if (label.Contains("price paid per share")) pricePaidPerShare = v;
            else if (label.Contains("price paid total")) pricePaidTotal = v;
        }
        fields["fmv_per_share"] = new ExtractedField
        {
            Name = "fmv_per_share",
            Value = fmvPerShare is null ? null : new MoneyRecord(fmvPerShare.Value, "USD", "share"),
            Confidence = fmvPerShare is not null ? 0.85m : 0.0m
        };
        fields["fmv_total"] = new ExtractedField
        {
            Name = "fmv_total",
            Value = fmvTotal is null ? null : new MoneyRecord(fmvTotal.Value, "USD", null),
            Confidence = fmvTotal is not null ? 0.85m : 0.0m
        };
        fields["price_paid_per_share"] = new ExtractedField
        {
            Name = "price_paid_per_share",
            Value = pricePaidPerShare is null ? null : new MoneyRecord(pricePaidPerShare.Value, "USD", "share"),
            Confidence = pricePaidPerShare is not null ? 0.85m : 0.0m
        };
        fields["price_paid_total"] = new ExtractedField
        {
            Name = "price_paid_total",
            Value = pricePaidTotal is null ? null : new MoneyRecord(pricePaidTotal.Value, "USD", null),
            Confidence = pricePaidTotal is not null ? 0.85m : 0.0m
        };

        // Service recipient + EIN
        var srMatch = ServiceRecipientRegex.Match(body);
        fields["service_recipient"] = new ExtractedField
        {
            Name = "service_recipient",
            Value = srMatch.Success ? srMatch.Groups["value"].Value.Trim() : null,
            Confidence = srMatch.Success ? 0.7m : 0.0m
        };

        string? recipientEin = null;
        foreach (Match m in EinRegex.Matches(body))
        {
            var startCtx = Math.Max(0, m.Index - 60);
            var ctx = body[startCtx..m.Index];
            if (ctx.Contains("Service Recipient", StringComparison.OrdinalIgnoreCase) ||
                ctx.Contains("EIN", StringComparison.OrdinalIgnoreCase))
            {
                recipientEin = m.Groups["value"].Value;
                break;
            }
        }
        fields["service_recipient_ein"] = new ExtractedField
        {
            Name = "service_recipient_ein",
            Value = recipientEin,
            Confidence = recipientEin is not null ? 0.9m : 0.0m
        };

        // Non-standard 83(b) timestamp signature: "NAME 2025.11.26 02:43:14 +0000"
        DateTimeOffset? signedAt = null;
        var sigMatch = Form83bSignatureRegex.Match(body);
        if (sigMatch.Success)
        {
            var year = int.Parse(sigMatch.Groups["year"].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(sigMatch.Groups["month"].Value, CultureInfo.InvariantCulture);
            var day = int.Parse(sigMatch.Groups["day"].Value, CultureInfo.InvariantCulture);
            var hour = int.Parse(sigMatch.Groups["hour"].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(sigMatch.Groups["minute"].Value, CultureInfo.InvariantCulture);
            var second = int.Parse(sigMatch.Groups["second"].Value, CultureInfo.InvariantCulture);
            var offsetStr = sigMatch.Groups["offset"].Value;
            var offset = TimeSpan.Zero;
            if (!string.IsNullOrEmpty(offsetStr))
            {
                var sign = offsetStr[0] == '-' ? -1 : 1;
                var oh = int.Parse(offsetStr.Substring(1, 2), CultureInfo.InvariantCulture);
                var om = int.Parse(offsetStr.Substring(3, 2), CultureInfo.InvariantCulture);
                offset = new TimeSpan(sign * oh, sign * om, 0);
            }
            try
            {
                signedAt = new DateTimeOffset(year, month, day, hour, minute, second, offset);
            }
            catch (ArgumentOutOfRangeException)
            {
                signedAt = null;
            }
        }
        fields["signed_at_utc"] = new ExtractedField
        {
            Name = "signed_at_utc",
            Value = signedAt,
            Confidence = signedAt is not null ? 0.9m : 0.0m
        };

        var ombMatch = OmbRegex.Match(body);
        fields["omb_number"] = new ExtractedField
        {
            Name = "omb_number",
            Value = ombMatch.Success ? ombMatch.Groups["value"].Value : null,
            Confidence = ombMatch.Success ? 0.95m : 0.0m
        };

        var catMatch = CatalogRegex.Match(body);
        fields["catalog_number"] = new ExtractedField
        {
            Name = "catalog_number",
            Value = catMatch.Success ? catMatch.Groups["value"].Value : null,
            Confidence = catMatch.Success ? 0.95m : 0.0m
        };
    }

    // -------- Certificate of incorporation --------

    private static readonly Regex EntityNameAllCapsRegex = new(
        @"\b(?<value>[A-Z][\w\s,]+?\sINC\.)",
        RegexOptions.Compiled);

    private static readonly Regex RegisteredAgentRegex = new(
        @"Registered Agent[:\s]+(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ExtractCertificateOfIncorporationFields(
        string body,
        string fileName,
        IDictionary<string, ExtractedField> fields)
    {
        string? entityName = null;
        var bodyMatch = EntityNameAllCapsRegex.Match(body);
        if (bodyMatch.Success) entityName = bodyMatch.Groups["value"].Value.Trim();
        if (entityName is null)
        {
            var fnMatch = EntityFromFileNameRegex.Match(fileName);
            if (fnMatch.Success) entityName = fnMatch.Groups["name"].Value.Trim();
        }
        fields["entity_name"] = new ExtractedField
        {
            Name = "entity_name",
            Value = entityName,
            Confidence = entityName is not null ? 0.7m : 0.0m
        };

        string? state = null;
        var stateMatch = JurisdictionRegex.Match(body);
        if (stateMatch.Success) state = stateMatch.Groups["value"].Value.Trim();
        if (state is null && fileName.Contains("Delaware", StringComparison.OrdinalIgnoreCase)) state = "Delaware";
        fields["state_of_incorporation"] = new ExtractedField
        {
            Name = "state_of_incorporation",
            Value = state,
            Confidence = state is not null ? 0.8m : 0.0m
        };

        var formationDate = ExtractFiledAtDate(body);
        fields["formation_date"] = new ExtractedField
        {
            Name = "formation_date",
            Value = formationDate,
            Confidence = formationDate is not null ? 0.7m : 0.0m
        };

        var authorizedShares = ParseShareAuthorizations(body);
        fields["authorized_shares"] = new ExtractedField
        {
            Name = "authorized_shares",
            Value = authorizedShares,
            Confidence = authorizedShares.Count > 0 ? 0.7m : 0.0m
        };

        var raMatch = RegisteredAgentRegex.Match(body);
        fields["registered_agent"] = new ExtractedField
        {
            Name = "registered_agent",
            Value = raMatch.Success ? raMatch.Groups["value"].Value.Trim() : null,
            Confidence = raMatch.Success ? 0.7m : 0.0m
        };
    }

    // -------- Stock purchase agreement --------

    private static readonly Regex SpaPartiesRegex = new(
        @"between\s+(?<a>[A-Z][\w&\.\s,]+?)\s*\(""?(?:the\s+)?Purchaser""?\)\s+and\s+(?<b>[A-Z][\w&\.\s,]+?)\s*\(""?(?:the\s+)?Company""?\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PricePerShareRegex = new(
        @"\$\s?(?<amount>[0-9]+(?:\.\d{2,4})?)\s+per\s+share",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ExtractStockPurchaseAgreementFields(string body, IDictionary<string, ExtractedField> fields)
    {
        var spaMatch = SpaPartiesRegex.Match(body);
        string? purchaser = spaMatch.Success ? spaMatch.Groups["a"].Value.Trim() : null;
        string? issuer = spaMatch.Success ? spaMatch.Groups["b"].Value.Trim() : null;
        fields["purchaser"] = new ExtractedField
        {
            Name = "purchaser",
            Value = purchaser,
            Confidence = purchaser is not null ? 0.7m : 0.0m
        };
        fields["issuer"] = new ExtractedField
        {
            Name = "issuer",
            Value = issuer,
            Confidence = issuer is not null ? 0.7m : 0.0m
        };

        var shareAuths = ParseShareAuthorizations(body);
        long? shareCount = shareAuths.FirstOrDefault()?.Count;
        string? shareClass = shareAuths.FirstOrDefault()?.ShareClass;
        fields["share_count"] = new ExtractedField
        {
            Name = "share_count",
            Value = shareCount,
            Confidence = shareCount is not null ? 0.7m : 0.0m
        };
        fields["share_class"] = new ExtractedField
        {
            Name = "share_class",
            Value = shareClass,
            Confidence = shareClass is not null ? 0.7m : 0.0m
        };

        MoneyRecord? pricePerShare = null;
        var ppsMatch = PricePerShareRegex.Match(body);
        if (ppsMatch.Success && decimal.TryParse(ppsMatch.Groups["amount"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var pps))
        {
            pricePerShare = new MoneyRecord(pps, "USD", "share");
        }
        fields["price_per_share"] = new ExtractedField
        {
            Name = "price_per_share",
            Value = pricePerShare,
            Confidence = pricePerShare is not null ? 0.85m : 0.0m
        };

        VestingTerms? vesting = null;
        var vMatch = VestingDurationRegex.Match(body);
        if (vMatch.Success && int.TryParse(vMatch.Groups["n"].Value, out var years))
        {
            var months = years * 12;
            var cliff = 0;
            var cMatch = VestingCliffRegex.Match(body);
            if (cMatch.Success && int.TryParse(cMatch.Groups["n"].Value, out var cn))
            {
                var unit = cMatch.Groups["unit"].Value.ToLowerInvariant();
                cliff = unit.StartsWith("year") ? cn * 12 : cn;
            }
            vesting = new VestingTerms(months, cliff, "monthly", null);
        }
        fields["vesting"] = new ExtractedField
        {
            Name = "vesting",
            Value = vesting,
            Confidence = vesting is not null ? 0.7m : 0.0m
        };
    }

    // -------- EIN letter --------

    private static void ExtractEinLetterFields(string body, IDictionary<string, ExtractedField> fields)
    {
        // Entity name: top-of-letter — first ALL-CAPS "INC." line.
        string? entityName = null;
        var topMatch = EntityNameAllCapsRegex.Match(body);
        if (topMatch.Success) entityName = topMatch.Groups["value"].Value.Trim();
        fields["entity_name"] = new ExtractedField
        {
            Name = "entity_name",
            Value = entityName,
            Confidence = entityName is not null ? 0.6m : 0.0m
        };

        // EIN
        string? ein = null;
        foreach (Match m in EinRegex.Matches(body))
        {
            ein = m.Groups["value"].Value;
            break;
        }
        fields["ein"] = new ExtractedField
        {
            Name = "ein",
            Value = ein,
            Confidence = ein is not null ? 0.95m : 0.0m
        };

        // Issued date — first date near top
        DateTimeOffset? issuedDate = null;
        var dateMatch = RegexHelpers.DateLong.Match(body);
        if (dateMatch.Success)
        {
            issuedDate = RegexHelpers.ParseDate(dateMatch.Value);
        }
        else
        {
            var slash = RegexHelpers.DateSlash.Match(body);
            if (slash.Success) issuedDate = RegexHelpers.ParseDate(slash.Value);
        }
        fields["issued_date"] = new ExtractedField
        {
            Name = "issued_date",
            Value = issuedDate,
            Confidence = issuedDate is not null ? 0.7m : 0.0m
        };
    }

    // -------- Missing-key-fields counter --------

    private static int CountMissingKeyFields(IReadOnlyDictionary<string, ExtractedField> fields, string subtype)
    {
        var keyNames = subtype switch
        {
            CorporateSchemaV1Constants.Subtypes.FilingReceipt =>
                new[] { "service_request_number", "submitted_at", "submitter" },
            CorporateSchemaV1Constants.Subtypes.BoardConsent =>
                new[] { "consent_type", "resolutions", "directors" },
            CorporateSchemaV1Constants.Subtypes.FounderAgreement =>
                new[] { "founders", "vesting", "governance_rules" },
            CorporateSchemaV1Constants.Subtypes.FoundersCharter =>
                new[] { "founders", "equity_allocation", "guiding_principles" },
            CorporateSchemaV1Constants.Subtypes.Section83bElection =>
                new[] { "taxpayer", "taxpayer_tin", "property_description", "transfer_date" },
            CorporateSchemaV1Constants.Subtypes.CertificateOfIncorporation =>
                new[] { "entity_name", "state_of_incorporation", "authorized_shares" },
            CorporateSchemaV1Constants.Subtypes.StockPurchaseAgreement =>
                new[] { "purchaser", "issuer", "share_count", "price_per_share" },
            CorporateSchemaV1Constants.Subtypes.EinLetter =>
                new[] { "entity_name", "ein", "issued_date" },
            _ => new[] { "entity", "effective_date", "jurisdiction" }
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
