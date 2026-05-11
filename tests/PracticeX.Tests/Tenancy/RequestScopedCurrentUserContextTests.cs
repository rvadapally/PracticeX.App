using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PracticeX.Domain.Organization;
using PracticeX.Infrastructure.Persistence;
using PracticeX.Infrastructure.Tenancy;
using PracticeX.Tests.SourceDiscovery.Support;

namespace PracticeX.Tests.Tenancy;

public sealed class RequestScopedCurrentUserContextTests
{
    private static readonly Guid EagleTenantId = new("e1111111-1111-1111-1111-111111111111");
    private static readonly Guid SynexarTenantId = new("51111111-1111-1111-1111-111111111111");
    private static readonly Guid EagleFacilityId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SynexarFacilityId = new("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Resolves_Org_Admin_As_Tenant_Scoped_Unrestricted()
    {
        using var db = CreateDb();
        SeedTenant(db, EagleTenantId, "Eagle Physicians of Greensboro");
        SeedRole(db, new Guid("a0000001-0000-0000-0000-000000000002"), StandardRoleNames.OrgAdmin);
        SeedUser(db, new Guid("a0000002-0000-0000-0000-000000000001"), EagleTenantId, "dr8382@gmail.com");
        db.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            TenantId = EagleTenantId,
            UserId = new Guid("a0000002-0000-0000-0000-000000000001"),
            RoleId = new Guid("a0000001-0000-0000-0000-000000000002"),
            FacilityId = null,
            Status = "active"
        });
        db.SaveChanges();

        var context = CreateUserContext(db, "dr8382@gmail.com");

        Assert.Equal(EagleTenantId, context.TenantId);
        Assert.Equal(StandardRoleNames.OrgAdmin, context.RoleName);
        Assert.True(context.IsOrgAdmin);
        Assert.False(context.IsSuperAdmin);
        Assert.Null(context.AccessibleFacilityIds);
    }

    [Fact]
    public void Resolves_Facility_Admin_As_Distinct_Role_With_Facility_Scope()
    {
        using var db = CreateDb();
        SeedTenant(db, SynexarTenantId, "Synexar Inc");
        SeedFacility(db, SynexarFacilityId, SynexarTenantId, "Synexar Inc", "SYNX");
        SeedRole(db, new Guid("a0000001-0000-0000-0000-000000000004"), StandardRoleNames.FacilityAdmin);
        SeedUser(db, new Guid("a0000002-0000-0000-0000-000000000020"), SynexarTenantId, "facility.admin@synexar.ai");
        db.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            TenantId = SynexarTenantId,
            UserId = new Guid("a0000002-0000-0000-0000-000000000020"),
            RoleId = new Guid("a0000001-0000-0000-0000-000000000004"),
            FacilityId = SynexarFacilityId,
            Status = "active"
        });
        db.SaveChanges();

        var context = CreateUserContext(db, "facility.admin@synexar.ai");

        Assert.Equal(SynexarTenantId, context.TenantId);
        Assert.Equal(StandardRoleNames.FacilityAdmin, context.RoleName);
        Assert.False(context.IsOrgAdmin);
        Assert.False(context.IsSuperAdmin);
        Assert.NotNull(context.AccessibleFacilityIds);
        Assert.Contains(SynexarFacilityId, context.AccessibleFacilityIds!);
        Assert.True(context.IsAuthorizedForFacility(SynexarFacilityId));
        Assert.False(context.IsAuthorizedForFacility(EagleFacilityId));
        Assert.False(context.IsAuthorizedForFacility(null));
    }

    [Fact]
    public void Facility_Admin_And_Facility_User_Assignments_Both_Grant_Facility_Read_Access()
    {
        using var db = CreateDb();
        SeedTenant(db, SynexarTenantId, "Synexar Inc");
        SeedFacility(db, SynexarFacilityId, SynexarTenantId, "Synexar Inc", "SYNX");
        var secondFacilityId = new Guid("44444444-4444-4444-4444-444444444444");
        SeedFacility(db, secondFacilityId, SynexarTenantId, "Synexar East", "SYNE");
        SeedRole(db, new Guid("a0000001-0000-0000-0000-000000000003"), StandardRoleNames.FacilityUser);
        SeedRole(db, new Guid("a0000001-0000-0000-0000-000000000004"), StandardRoleNames.FacilityAdmin);
        SeedUser(db, new Guid("a0000002-0000-0000-0000-000000000021"), SynexarTenantId, "dual.scope@synexar.ai");
        db.RoleAssignments.AddRange(
            new RoleAssignment
            {
                Id = Guid.NewGuid(),
                TenantId = SynexarTenantId,
                UserId = new Guid("a0000002-0000-0000-0000-000000000021"),
                RoleId = new Guid("a0000001-0000-0000-0000-000000000004"),
                FacilityId = SynexarFacilityId,
                Status = "active"
            },
            new RoleAssignment
            {
                Id = Guid.NewGuid(),
                TenantId = SynexarTenantId,
                UserId = new Guid("a0000002-0000-0000-0000-000000000021"),
                RoleId = new Guid("a0000001-0000-0000-0000-000000000003"),
                FacilityId = secondFacilityId,
                Status = "active"
            });
        db.SaveChanges();

        var context = CreateUserContext(db, "dual.scope@synexar.ai");

        Assert.Equal(StandardRoleNames.FacilityAdmin, context.RoleName);
        Assert.Equal(2, context.AccessibleFacilityIds!.Count);
        Assert.Contains(SynexarFacilityId, context.AccessibleFacilityIds);
        Assert.Contains(secondFacilityId, context.AccessibleFacilityIds);
    }

    private static PracticeXDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PracticeXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestPracticeXDbContext(options);
    }

    private static RequestScopedCurrentUserContext CreateUserContext(PracticeXDbContext db, string email)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Cf-Access-Authenticated-User-Email"] = email;
        return new RequestScopedCurrentUserContext(new HttpContextAccessor { HttpContext = httpContext }, db);
    }

    private static void SeedTenant(PracticeXDbContext db, Guid id, string name)
    {
        db.Tenants.Add(new Tenant
        {
            Id = id,
            Name = name,
            Status = "active",
            DataRegion = "us",
            BaaStatus = "signed"
        });
    }

    private static void SeedFacility(PracticeXDbContext db, Guid id, Guid tenantId, string name, string code)
    {
        db.Facilities.Add(new Facility
        {
            Id = id,
            TenantId = tenantId,
            Name = name,
            Code = code,
            Status = "active"
        });
    }

    private static void SeedRole(PracticeXDbContext db, Guid id, string name)
    {
        db.Roles.Add(new Role
        {
            Id = id,
            TenantId = Guid.NewGuid(),
            Name = name,
            Permissions = JsonDocument.Parse("{}")
        });
    }

    private static void SeedUser(PracticeXDbContext db, Guid id, Guid tenantId, string email)
    {
        db.Users.Add(new AppUser
        {
            Id = id,
            TenantId = tenantId,
            Email = email,
            Name = email,
            Status = "active"
        });
    }
}
