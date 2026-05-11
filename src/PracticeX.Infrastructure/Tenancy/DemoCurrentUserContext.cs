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
    private static readonly Guid SynexarAliasUserId = new("22222222-2222-2222-2222-222222222223");
    private static readonly Guid GmailAliasUserId = new("22222222-2222-2222-2222-222222222224");

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

        await EnsureSuperAdminUserAsync(
            dbContext,
            DemoUserId,
            "rvadapally@practicex.ai",
            cancellationToken,
            allowLegacyBackfill: true);
        await EnsureSuperAdminUserAsync(
            dbContext,
            SynexarAliasUserId,
            "rvadapally@synexar.ai",
            cancellationToken);
        await EnsureSuperAdminUserAsync(
            dbContext,
            GmailAliasUserId,
            "rvadapally@gmail.com",
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureSuperAdminUserAsync(
        PracticeXDbContext dbContext,
        Guid userId,
        string email,
        CancellationToken cancellationToken,
        bool allowLegacyBackfill = false)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await dbContext.Users.FirstOrDefaultAsync(
            u => u.Id == userId || u.Email.ToLower() == normalizedEmail,
            cancellationToken);

        if (user is null)
        {
            dbContext.Users.Add(new AppUser
            {
                Id = userId,
                TenantId = DemoTenantId,
                Email = email,
                Name = "Raghuram Vadapally",
                Status = "active",
                IsSuperAdmin = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
            return;
        }

        if (allowLegacyBackfill && (user.Name == "Jordan Okafor" || user.Email == "demo@practicex.com"))
        {
            user.Name = "Raghuram Vadapally";
            user.Email = email;
            user.IsSuperAdmin = true;
            user.Status = "active";
            user.UpdatedAt = DateTimeOffset.UtcNow;
            return;
        }

        if (user.TenantId != DemoTenantId
            || !string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(user.Name, "Raghuram Vadapally", StringComparison.Ordinal)
            || user.Status != "active"
            || !user.IsSuperAdmin)
        {
            user.TenantId = DemoTenantId;
            user.Email = email;
            user.Name = "Raghuram Vadapally";
            user.Status = "active";
            user.IsSuperAdmin = true;
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
