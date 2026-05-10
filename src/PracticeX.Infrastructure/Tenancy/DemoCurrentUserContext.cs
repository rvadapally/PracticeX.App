using Microsoft.EntityFrameworkCore;
using PracticeX.Application.Common;
using PracticeX.Domain.Organization;
using PracticeX.Infrastructure.Persistence;

namespace PracticeX.Infrastructure.Tenancy;

/// <summary>
/// Demo seeder for the default tenant + super-admin user. As of Slice 21
/// this no longer doubles as the <see cref="ICurrentUserContext"/>
/// implementation — that's now <c>RequestScopedCurrentUserContext</c>,
/// which resolves the user from the Cloudflare Access principal. This
/// class only exists to seed the row that the resolver looks up.
/// </summary>
public static class DemoCurrentUserContext
{
    private static readonly Guid DemoTenantId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DemoUserId = new("22222222-2222-2222-2222-222222222222");

    public static async Task EnsureSeededAsync(PracticeXDbContext dbContext, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == DemoTenantId, cancellationToken);
        if (tenant is null)
        {
            dbContext.Tenants.Add(new Tenant
            {
                Id = DemoTenantId,
                Name = "PracticeX",
                Status = "active",
                DataRegion = "us",
                BaaStatus = "signed",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        else if (tenant.Name == "PracticeX Demo Group")
        {
            // Backfill old demo seed name on existing rows.
            tenant.Name = "PracticeX";
            tenant.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == DemoUserId, cancellationToken);
        if (user is null)
        {
            dbContext.Users.Add(new AppUser
            {
                Id = DemoUserId,
                TenantId = DemoTenantId,
                Email = "rvadapally@practicex.ai",
                Name = "Raghuram Vadapally",
                Status = "active",
                IsSuperAdmin = true,  // Slice 21: seed as super-admin
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        else if (user.Name == "Jordan Okafor" || user.Email == "demo@practicex.com")
        {
            user.Name = "Raghuram Vadapally";
            user.Email = "rvadapally@practicex.ai";
            user.IsSuperAdmin = true;
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else if (!user.IsSuperAdmin)
        {
            // Backfill: the seeded demo user must always be super-admin so
            // the no-header local dev path resolves to a usable principal.
            user.IsSuperAdmin = true;
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
