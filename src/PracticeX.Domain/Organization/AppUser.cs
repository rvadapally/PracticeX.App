using PracticeX.Domain.Common;

namespace PracticeX.Domain.Organization;

public sealed class AppUser : Entity
{
    public Guid TenantId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "invited";
    public DateTimeOffset? LastLoginAt { get; set; }

    // Slice 21 — RBAC Phase 1.
    // Super-admin transcends every tenant + facility check. Set true for
    // platform operators (currently: Raghu). Distinct from the org_admin
    // role (which is per-tenant) so that promoting a user to super-admin
    // does not require touching role_assignments rows.
    public bool IsSuperAdmin { get; set; }
}

public static class StandardRoleNames
{
    public const string SuperAdmin = "super_admin";       // bypasses all access checks
    public const string OrgAdmin = "org_admin";           // all facilities in their tenant
    public const string FacilityAdmin = "facility_admin"; // facility-scoped admin, limited to assigned facilities
    public const string FacilityUser = "facility_user";   // only facilities listed in role_assignments
}
