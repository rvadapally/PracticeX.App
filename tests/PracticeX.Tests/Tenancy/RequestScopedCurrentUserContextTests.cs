using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PracticeX.Domain.Organization;
using PracticeX.Infrastructure.Persistence;
using PracticeX.Infrastructure.Tenancy;
using PracticeX.Tests.SourceDiscovery.Support;

namespace PracticeX.Tests.Tenancy;

public sealed class RequestScopedCurrentUserContextTests
{
    [Fact]
    public void Resolves_active_user_from_cloudflare_email_case_insensitively()
    {
        using var db = CreateDbContext(nameof(Resolves_active_user_from_cloudflare_email_case_insensitively));
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "PracticeX",
            Status = "active",
            DataRegion = "us",
            BaaStatus = "signed",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Users.Add(new AppUser
        {
            Id = userId,
            TenantId = tenantId,
            Email = "rvadapally@synexar.ai",
            Name = "Raghuram Vadapally",
            Status = "active",
            IsSuperAdmin = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var http = new DefaultHttpContext();
        http.Request.Headers["Cf-Access-Authenticated-User-Email"] = "RVADAPALLY@SYNEXAR.AI";
        var accessor = new HttpContextAccessor { HttpContext = http };

        var currentUser = new RequestScopedCurrentUserContext(accessor, db);

        Assert.Equal(userId, currentUser.UserId);
        Assert.Equal("rvadapally@synexar.ai", currentUser.Email);
        Assert.True(currentUser.IsSuperAdmin);
    }

    [Fact]
    public async Task Demo_seed_adds_requested_super_admin_aliases()
    {
        await using var db = CreateDbContext(nameof(Demo_seed_adds_requested_super_admin_aliases));

        await DemoCurrentUserContext.EnsureSeededAsync(db, CancellationToken.None);

        var emails = await db.Users
            .Where(u => u.IsSuperAdmin)
            .Select(u => u.Email)
            .ToListAsync();

        Assert.Contains("rvadapally@practicex.ai", emails);
        Assert.Contains("rvadapally@synexar.ai", emails);
        Assert.Contains("rvadapally@gmail.com", emails);
    }

    private static PracticeXDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<PracticeXDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new TestPracticeXDbContext(options);
    }
}
