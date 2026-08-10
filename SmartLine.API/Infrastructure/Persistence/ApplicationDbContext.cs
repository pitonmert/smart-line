using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SmartLine.API.Domain.Identity;
using SmartLine.API.Domain.Entities;

namespace SmartLine.API.Infrastructure.Persistence;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<SystemLog> SystemLogs => Set<SystemLog>();
    public DbSet<ConveyorSetting> ConveyorSettings => Set<ConveyorSetting>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Statistic> Statistics => Set<Statistic>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ApplicationUser>(builder =>
        {
            builder.Property(x => x.CreatedAt).IsRequired();
            builder.Property(x => x.Email).HasMaxLength(256);
        });

        modelBuilder.Entity<RefreshToken>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            builder.HasIndex(x => x.TokenHash).IsUnique();
            builder.HasIndex(x => new { x.UserId, x.ExpiresAt });
            builder
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Alert>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.AlertType).HasConversion<string>().HasMaxLength(50).IsRequired();
            builder.HasIndex(e => e.IsActive);
            builder.HasIndex(e => e.TriggeredAt);
            builder.HasIndex(e => e.AlertType);
        });

        modelBuilder.Entity<ConveyorSetting>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.FireDeviceEpoch).HasMaxLength(32);
            builder.HasData(
                new ConveyorSetting
                {
                    Id = 1,
                    Speed = 50,
                    IsRunning = false,
                    IsEmergencyStopped = false,
                    IsFireDetected = false,
                    EmergencyResetGeneration = 0,
                    FireDeviceEpoch = "",
                    LastFireGeneration = 0,
                    IsFireGenerationFault = false,
                    Temperature = 22.0m,
                    Humidity = 45.0m,
                    FanActive = false,
                    LastUpdated = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc),
                }
            );
        });

        modelBuilder.Entity<Product>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Barcode).HasMaxLength(50).IsRequired();
            builder.Property(e => e.Color).HasConversion<string>().HasMaxLength(20);
            builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            builder.HasIndex(e => e.Barcode).IsUnique();
            builder.HasIndex(e => e.PassedAt);
            builder.HasIndex(e => e.Status);
            builder.HasIndex(e => e.Color);
        });

        modelBuilder.Entity<Statistic>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.HasIndex(e => new { e.Date, e.Hour }).IsUnique().AreNullsDistinct(false);
        });

        modelBuilder.Entity<SystemLog>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.EventType).HasConversion<string>().HasMaxLength(50).IsRequired();
            builder.Property(e => e.Severity).HasMaxLength(20).IsRequired();
            builder.HasIndex(e => e.CreatedAt);
            builder.HasIndex(e => e.Severity);
            builder.HasIndex(e => e.EventType);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (
                entry.Entity is Product product
                && entry.State is EntityState.Added or EntityState.Modified
            )
                product.UpdatedAt = now;
            else if (
                entry.Entity is Alert alert
                && entry.State is EntityState.Added or EntityState.Modified
            )
                alert.UpdatedAt = now;
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
