using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

public static class Bootstrap
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config, IHostEnvironment environment,
        ILogger logger)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Phase-0 bootstrap only: replace EnsureCreated with reviewed EF migrations before production.
        await db.Database.EnsureCreatedAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var roleName in new[] { "superadmin", "admin", "user" })
            if (!await roles.RoleExistsAsync(roleName))
                await roles.CreateAsync(new IdentityRole<Guid>(roleName));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var username = config["Bootstrap:Username"] ?? "superadmin";
        if (await users.FindByNameAsync(username) is not null) return;
        var password = config["Bootstrap:Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Bootstrap password must be provided in production.");
            password = "superadmin";
        }
        var user = new ApplicationUser { UserName = username, MustChangePassword = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException("Bootstrap user creation failed: " +
                string.Join("; ", result.Errors.Select(e => e.Code)));
        await users.AddToRoleAsync(user, "superadmin");
        logger.LogWarning("Bootstrap superadmin created; password change is required at first login UserId={UserId}", user.Id);
    }
}
