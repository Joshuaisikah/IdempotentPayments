using Microsoft.EntityFrameworkCore;

namespace IdempotentPayments.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options) { }

    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var paymentEntity = modelBuilder.Entity<Payment>();
        paymentEntity.HasKey(p => p.Id);
        paymentEntity.Property(p => p.Id).ValueGeneratedNever();
        paymentEntity.Property(p => p.Amount).HasPrecision(18, 2).IsRequired();
        paymentEntity.Property(p => p.Currency).HasMaxLength(3).IsRequired();
        paymentEntity.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        paymentEntity.Property(p => p.ProviderReference).HasMaxLength(64);
        paymentEntity.Property(p => p.FailureReason).HasMaxLength(256);
        paymentEntity.Property(p => p.CreatedAt).IsRequired();
        paymentEntity.Property(p => p.UpdatedAt).IsRequired();
        paymentEntity.Property(p => p.CompletedAt);
        paymentEntity.HasIndex(p => new { p.Status, p.UpdatedAt });

        var keyEntity = modelBuilder.Entity<IdempotencyKey>();
        keyEntity.HasKey(k => k.Key);
        keyEntity.Property(k => k.Key).HasMaxLength(255);
        keyEntity.Property(k => k.RequestHash).HasMaxLength(64).IsRequired();
        keyEntity.Property(k => k.ResponseStatusCode).IsRequired();
        keyEntity.Property(k => k.ResponseBody).IsRequired();
        keyEntity.Property(k => k.PaymentId);
        keyEntity.Property(k => k.CreatedAt).IsRequired();
    }
}
