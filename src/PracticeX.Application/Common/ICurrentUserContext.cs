namespace PracticeX.Application.Common;

/// <summary>
/// Provides the active tenant + user for the current request, plus their
/// authorization scope. Resolved per-request from the OIDC / Cloudflare
/// Access principal; the demo path falls back to a seeded super-admin.
///
/// Slice 21 — RBAC Phase 1: every read endpoint is responsible for
/// applying <see cref="IsAuthorizedForFacility"/> (or filtering by
/// <see cref="AccessibleFacilityIds"/>) before returning data. The
/// principle is fail-closed: a facility-scoped user must never see data
/// outside their assigned facilities, even via direct GUID URLs.
/// </summary>
public interface ICurrentUserContext
{
    Guid TenantId { get; }
    Guid UserId { get; }
    string ActorType { get; }

    /// <summary>
    /// True when the user transcends all tenant + facility checks. Set on
    /// the AppUser row, not derived from role assignments. Use sparingly —
    /// every super-admin grant is a cross-tenant data exposure.
    /// </summary>
    bool IsSuperAdmin { get; }

    /// <summary>
    /// True when the user is an org administrator for their home tenant —
    /// can see every facility in <see cref="TenantId"/>. Org admins do not
    /// transcend tenants.
    /// </summary>
    bool IsOrgAdmin { get; }

    /// <summary>
    /// Set of facility ids the user can read. <c>null</c> means
    /// "unrestricted within the tenant" (super-admin / org-admin); a set
    /// means strict facility-level scoping.
    ///
    /// Endpoints filtering documents/contracts MUST apply this. Documents
    /// without a facility hint (facility_hint_id IS NULL) are visible only
    /// to super-admin / org-admin.
    /// </summary>
    IReadOnlySet<Guid>? AccessibleFacilityIds { get; }

    /// <summary>
    /// Convenience guard: returns true if the user can read content scoped
    /// to <paramref name="facilityId"/>. Pass <c>null</c> when checking a
    /// document with no facility hint — only super/org admins see those.
    /// </summary>
    bool IsAuthorizedForFacility(Guid? facilityId);
}
