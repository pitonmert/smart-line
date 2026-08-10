using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartLine.API.Domain.Identity;
using SmartLine.API.Infrastructure.Persistence;

namespace SmartLine.API.Infrastructure.Authentication;

public static class IdentityBootstrapper
{
    public static async Task InitializeAsync(
        IServiceProvider services,
        IHostEnvironment environment,
        CancellationToken ct = default
    )
    {
        using var scope = services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var bootstrap = scope
            .ServiceProvider.GetRequiredService<IOptions<BootstrapAdminOptions>>()
            .Value;
        var logger = scope
            .ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("IdentityBootstrapper");

        foreach (var roleName in AuthRoles.UserRoles)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var roleResult = await roleManager.CreateAsync(
                    new IdentityRole<Guid> { Name = roleName, NormalizedName = roleName.ToUpperInvariant() }
                );
                if (!roleResult.Succeeded)
                    throw new InvalidOperationException(
                        $"Identity role '{roleName}' could not be created: {FormatErrors(roleResult)}"
                    );
            }
        }

        if (await userManager.Users.AnyAsync(ct))
            return;

        if (string.IsNullOrWhiteSpace(bootstrap.Email) || string.IsNullOrWhiteSpace(bootstrap.Password))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                throw new InvalidOperationException(
                    "BootstrapAdmin__Email and BootstrapAdmin__Password are required for an empty production database."
                );

            logger.LogWarning(
                "Database has no users. Set BootstrapAdmin environment variables to create the first admin."
            );
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var admin = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = bootstrap.Email.Trim(),
            Email = bootstrap.Email.Trim(),
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        var createResult = await userManager.CreateAsync(admin, bootstrap.Password);
        if (!createResult.Succeeded)
            throw new InvalidOperationException(
                $"Bootstrap admin could not be created: {FormatErrors(createResult)}"
            );

        var roleResultForAdmin = await userManager.AddToRoleAsync(admin, AuthRoles.Admin);
        if (!roleResultForAdmin.Succeeded)
            throw new InvalidOperationException(
                $"Bootstrap admin role could not be assigned: {FormatErrors(roleResultForAdmin)}"
            );
        await transaction.CommitAsync(ct);
    }

    private static string FormatErrors(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Description}"));
}
