using Microsoft.EntityFrameworkCore;
using SmartLine.API.Domain.Entities;

namespace SmartLine.API.Infrastructure.Persistence;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<SystemLog> SystemLogs => Set<SystemLog>();
    public DbSet<ConveyorSetting> ConveyorSettings => Set<ConveyorSetting>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Statistic> Statistics => Set<Statistic>();
    public DbSet<ProductRecipe> ProductRecipes => Set<ProductRecipe>();
    public DbSet<CalibrationProfile> CalibrationProfiles => Set<CalibrationProfile>();
    public DbSet<InspectionRecord> InspectionRecords => Set<InspectionRecord>();
    public DbSet<ProductTrack> ProductTracks => Set<ProductTrack>();
    public DbSet<DatasetSession> DatasetSessions => Set<DatasetSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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

        modelBuilder.Entity<ProductRecipe>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Name).HasMaxLength(80).IsRequired();
            builder.Property(e => e.Description).HasMaxLength(1000).IsRequired();
            builder.Property(e => e.DefinitionJson).HasColumnType("jsonb").IsRequired();
            builder.HasIndex(e => e.Name).IsUnique();
            builder.HasIndex(e => e.IsActive).IsUnique().HasFilter("\"IsActive\" = TRUE");
        });

        modelBuilder.Entity<CalibrationProfile>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Name).HasMaxLength(80).IsRequired();
            builder.Property(e => e.Description).HasMaxLength(1000).IsRequired();
            builder.Property(e => e.DefinitionJson).HasColumnType("jsonb").IsRequired();
            builder.HasIndex(e => e.Name).IsUnique();
            builder.HasIndex(e => e.IsActive).IsUnique().HasFilter("\"IsActive\" = TRUE");
        });

        modelBuilder.Entity<InspectionRecord>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.ProductClass).HasMaxLength(64).IsRequired();
            builder.Property(e => e.ColorClass).HasMaxLength(64).IsRequired();
            builder.Property(e => e.SizeClass).HasMaxLength(64).IsRequired();
            builder.Property(e => e.DefectClassesJson).HasColumnType("jsonb").IsRequired();
            builder.Property(e => e.QualityStatus).HasMaxLength(32).IsRequired();
            builder.Property(e => e.Decision).HasMaxLength(32).IsRequired();
            builder.Property(e => e.DestinationBin).HasMaxLength(64).IsRequired();
            builder.Property(e => e.ModelName).HasMaxLength(80);
            builder.Property(e => e.ModelVersion).HasMaxLength(80);
            builder.Property(e => e.ImageReference).HasMaxLength(300);
            builder.Property(e => e.ResultJson).HasColumnType("jsonb").IsRequired();
            builder.HasIndex(e => e.ProcessedAt);
            builder.HasIndex(e => e.ProductId);
            builder.HasIndex(e => new { e.RecipeId, e.Decision });
            builder.HasIndex(e => e.IsSimulation);
        });

        modelBuilder.Entity<ProductTrack>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.PublicCode).HasMaxLength(32).IsRequired();
            builder.Property(e => e.Stage).HasMaxLength(32).IsRequired();
            builder.Property(e => e.DestinationBin).HasMaxLength(64);
            builder.Property(e => e.Decision).HasMaxLength(32);
            builder.Property(e => e.FailureReason).HasMaxLength(300);
            builder.HasIndex(e => e.PublicCode).IsUnique();
            builder.HasIndex(e => new { e.Stage, e.SortDueAt });
            builder.HasIndex(e => e.IsSimulation);
        });

        modelBuilder.Entity<DatasetSession>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.RecipeName).HasMaxLength(80).IsRequired();
            builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
            builder.Property(e => e.ProductClass).HasMaxLength(64).IsRequired();
            builder.Property(e => e.QualityLabel).HasMaxLength(64).IsRequired();
            builder.Property(e => e.ColorLabel).HasMaxLength(64).IsRequired();
            builder.Property(e => e.SizeLabel).HasMaxLength(64).IsRequired();
            builder.Property(e => e.DefectLabelsJson).HasColumnType("jsonb").IsRequired();
            builder.Property(e => e.Notes).HasMaxLength(1000);
            builder.Property(e => e.CaptureMode).HasMaxLength(20).IsRequired();
            builder.Property(e => e.StoragePath).HasMaxLength(300).IsRequired();
            builder.HasIndex(e => e.Status);
            builder.HasIndex(e => e.Status, "IX_DatasetSessions_OneActive").IsUnique().HasFilter("\"Status\" = 'ACTIVE'");
            builder.HasIndex(e => e.StartedAt);
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
