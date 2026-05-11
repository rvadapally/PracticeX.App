using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PracticeX.Application.Common;
using PracticeX.Domain.Organization;
using PracticeX.Infrastructure.Persistence;

namespace PracticeX.Infrastructure.Tenancy;

/// <summary>
/// Development/demo identity seed. The request-scoped current-user resolver
/// reads <see cref="AppUser"/> rows by email, so local environments need the
/// same platform/eagle/synexar identities that Cloudflare Access admits in
/// the hosted app.
/// </summary>
public static class DemoCurrentUserContext
{
    private static readonly Guid PlatformTenantId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EagleTenantId = new("e1111111-1111-1111-1111-111111111111");
    private static readonly Guid SynexarTenantId = new("51111111-1111-1111-1111-111111111111");
    private static readonly Guid DemoUserId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EagleFacilityId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SynexarFacilityId = new("33333333-3333-3333-3333-333333333333");

    private static readonly Guid SuperAdminRoleId = new("a0000001-0000-0000-0000-000000000001");
    private static readonly Guid OrgAdminRoleId = new("a0000001-0000-0000-0000-000000000002");
    private static readonly Guid FacilityUserRoleId = new("a0000001-0000-0000-0000-000000000003");
    private static readonly Guid FacilityAdminRoleId = new("a0000001-0000-0000-0000-000000000004");

    public static async Task EnsureSeededAsync(PracticeXDbContext dbContext, CancellationToken cancellationToken)
    {
        await UpsertTenantAsync(
            dbContext,
            PlatformTenantId,
            "PracticeX Platform",
            cancellationToken);
        await UpsertTenantAsync(
            dbContext,
            EagleTenantId,
            "Eagle Physicians of Greensboro",
            cancellationToken);
        await UpsertTenantAsync(
            dbContext,
            SynexarTenantId,
            "Synexar Inc",
            cancellationToken);

        await UpsertFacilityAsync(
            dbContext,
            EagleFacilityId,
            EagleTenantId,
            "Eagle Physicians of Greensboro",
            "EAG",
            cancellationToken);
        await UpsertFacilityAsync(
            dbContext,
            SynexarFacilityId,
            SynexarTenantId,
            "Synexar Inc",
            "SYNX",
            cancellationToken);

        await UpsertRoleAsync(
            dbContext,
            SuperAdminRoleId,
            PlatformTenantId,
            StandardRoleNames.SuperAdmin,
            """{"description":"Cross-tenant administrator. Bypasses all access checks."}""",
            cancellationToken);
        await UpsertRoleAsync(
            dbContext,
            OrgAdminRoleId,
            PlatformTenantId,
            StandardRoleNames.OrgAdmin,
            """{"description":"All facilities within the home tenant."}""",
            cancellationToken);
        await UpsertRoleAsync(
            dbContext,
            FacilityUserRoleId,
            PlatformTenantId,
            StandardRoleNames.FacilityUser,
            """{"description":"Specific facility (or facilities) listed in role_assignments."}""",
            cancellationToken);
        await UpsertRoleAsync(
            dbContext,
            FacilityAdminRoleId,
            PlatformTenantId,
            StandardRoleNames.FacilityAdmin,
            """{"description":"Facility-scoped administrator limited to assigned facilities."}""",
            cancellationToken);

        await UpsertUserAsync(
            dbContext,
            DemoUserId,
            PlatformTenantId,
            "rvadapally@practicex.ai",
            "Raghuram Vadapally",
            isSuperAdmin: true,
            cancellationToken);
        await UpsertUserAsync(
            dbContext,
            new Guid("a0000002-0000-0000-0000-000000000010"),
            PlatformTenantId,
            "rvadapally@gmail.com",
            "Raghuram Vadapally",
            isSuperAdmin: true,
            cancellationToken);
        await UpsertUserAsync(
            dbContext,
            new Guid("a0000002-0000-0000-0000-000000000011"),
            PlatformTenantId,
            "rvadapally@synexar.ai",
            "Raghuram Vadapally",
            isSuperAdmin: true,
            cancellationToken);
        await UpsertUserAsync(
            dbContext,
            new Guid("a0000002-0000-0000-0000-000000000001"),
            EagleTenantId,
            "dr8382@gmail.com",
            "Dr. Parag",
            isSuperAdmin: false,
            cancellationToken);
        await UpsertUserAsync(
            dbContext,
            new Guid("a0000002-0000-0000-0000-000000000002"),
            SynexarTenantId,
            "agupta@synexar.ai",
            "Ashutosh Gupta",
            isSuperAdmin: false,
            cancellationToken);
        await UpsertUserAsync(
            dbContext,
            new Guid("a0000002-0000-0000-0000-000000000003"),
            SynexarTenantId,
            "sourabh.sanghi@gmail.com",
            "Sourabh Sanghi",
            isSuperAdmin: false,
            cancellationToken);

        await UpsertRoleAssignmentAsync(
            dbContext,
            new Guid("a0000003-0000-0000-0000-000000000001"),
            EagleTenantId,
            new Guid("a0000002-0000-0000-0000-000000000001"),
            facilityId: null,
            OrgAdminRoleId,
            cancellationToken);
        await UpsertRoleAssignmentAsync(
            dbContext,
            new Guid("a0000003-0000-0000-0000-000000000002"),
            SynexarTenantId,
            new Guid("a0000002-0000-0000-0000-000000000002"),
            SynexarFacilityId,
            FacilityUserRoleId,
            cancellationToken);
        await UpsertRoleAssignmentAsync(
            dbContext,
            new Guid("a0000003-0000-0000-0000-000000000003"),
            SynexarTenantId,
            new Guid("a0000002-0000-0000-0000-000000000003"),
            SynexarFacilityId,
            FacilityUserRoleId,
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpsertTenantAsync(
        PracticeXDbContext dbContext,
        Guid id,
        string name,
        CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tenant is null)
        {
            dbContext.Tenants.Add(new Tenant
            {
                Id = id,
                Name = name,
                Status = "active",
                DataRegion = "us",
                BaaStatus = "signed",
                CreatedAt = DateTimeOffset.UtcNow
            });
            return;
        }

        if (tenant.Name != name || tenant.Status != "active" || tenant.DataRegion != "us" || tenant.BaaStatus != "signed")
        {
            tenant.Name = name;
            tenant.Status = "active";
            tenant.DataRegion = "us";
            tenant.BaaStatus = "signed";
            tenant.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static async Task UpsertFacilityAsync(
        PracticeXDbContext dbContext,
        Guid id,
        Guid tenantId,
        string name,
        string code,
        CancellationToken cancellationToken)
    {
        var facility = await dbContext.Facilities.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (facility is null)
        {
            dbContext.Facilities.Add(new Facility
            {
                Id = id,
                TenantId = tenantId,
                Name = name,
                Code = code,
                Status = "active",
                CreatedAt = DateTimeOffset.UtcNow
            });
            return;
        }

        if (facility.TenantId != tenantId || facility.Name != name || facility.Code != code || facility.Status != "active")
        {
            facility.TenantId = tenantId;
            facility.Name = name;
            facility.Code = code;
            facility.Status = "active";
            facility.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static async Task UpsertRoleAsync(
        PracticeXDbContext dbContext,
        Guid id,
        Guid tenantId,
        string name,
        string permissionsJson,
        CancellationToken cancellationToken)
    {
        var role = await dbContext.Roles.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (role is null)
        {
            dbContext.Roles.Add(new Role
            {
                Id = id,
                TenantId = tenantId,
                Name = name,
                Permissions = JsonDocument.Parse(permissionsJson),
                CreatedAt = DateTimeOffset.UtcNow
            });
            return;
        }

        if (role.TenantId != tenantId || role.Name != name)
        {
            role.TenantId = tenantId;
            role.Name = name;
            role.Permissions = JsonDocument.Parse(permissionsJson);
            role.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static async Task UpsertUserAsync(
        PracticeXDbContext dbContext,
        Guid id,
        Guid tenantId,
        string email,
        string name,
        bool isSuperAdmin,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            dbContext.Users.Add(new AppUser
            {
                Id = id,
                TenantId = tenantId,
                Email = email,
                Name = name,
                Status = "active",
                IsSuperAdmin = isSuperAdmin,
                CreatedAt = DateTimeOffset.UtcNow
            });
            return;
        }

        if (user.TenantId != tenantId
            || user.Email != email
            || user.Name != name
            || user.Status != "active"
            || user.IsSuperAdmin != isSuperAdmin)
        {
            user.TenantId = tenantId;
            user.Email = email;
            user.Name = name;
            user.Status = "active";
            user.IsSuperAdmin = isSuperAdmin;
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static async Task UpsertRoleAssignmentAsync(
        PracticeXDbContext dbContext,
        Guid id,
        Guid tenantId,
        Guid userId,
        Guid? facilityId,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        var assignment = await dbContext.RoleAssignments.FirstOrDefaultAsync(ra => ra.Id == id, cancellationToken);
        if (assignment is null)
        {
            dbContext.RoleAssignments.Add(new RoleAssignment
            {
                Id = id,
                TenantId = tenantId,
                UserId = userId,
                FacilityId = facilityId,
                RoleId = roleId,
                Status = "active",
                CreatedAt = DateTimeOffset.UtcNow
            });
            return;
        }

        if (assignment.TenantId != tenantId
            || assignment.UserId != userId
            || assignment.FacilityId != facilityId
            || assignment.RoleId != roleId
            || assignment.Status != "active")
        {
            assignment.TenantId = tenantId;
            assignment.UserId = userId;
            assignment.FacilityId = facilityId;
            assignment.RoleId = roleId;
            assignment.Status = "active";
            assignment.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
