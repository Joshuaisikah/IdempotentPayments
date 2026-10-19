using Microsoft.EntityFrameworkCore;

namespace IdempotentPayments.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options) { }

    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var p = modelBuilder.Entity<Payment>();
        p.HasKey(p => p.Id);
        p.Property(p => p.Id).ValueGeneratedNever();
        p.Property(p => p.Amount).HasPrecision(18, 2).IsRequired();
        p.Property(p => p.Currency).HasMaxLength(3).IsRequired();
        p.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        p.Property(p => p.ProviderReference).HasMaxLength(64);
        p.Property(p => p.FailureReason).HasMaxLength(256);
        p.Property(p => p.CreatedAt).IsRequired();
        p.Property(p => p.UpdatedAt).IsRequired();
        p.Property(p => p.CompletedAt);
        p.HasIndex(p => new { p.Status, p.UpdatedAt });
    }
}
