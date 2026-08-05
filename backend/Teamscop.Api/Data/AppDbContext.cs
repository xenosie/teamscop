using Microsoft.EntityFrameworkCore;

namespace Teamscop.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<UninstallTicket> UninstallTickets => Set<UninstallTicket>();
    public DbSet<AgentEvent> AgentEvents => Set<AgentEvent>();
    public DbSet<StaffTrackingConfigEntity> StaffTrackingConfigs => Set<StaffTrackingConfigEntity>();
    public DbSet<AgentSequenceState> AgentSequenceStates => Set<AgentSequenceState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Company>(entity =>
        {
            entity.ToTable("companies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.AvatarUrl).HasMaxLength(500);
            entity.Property(x => x.TokenJti).IsRequired();
            entity.Property(x => x.TokenVersion).HasDefaultValue(1);
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.UninstallTotpSecret).HasMaxLength(128);
            entity.Property(x => x.UninstallTotpEnabled).HasDefaultValue(false);
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DeviceKey).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => x.DeviceKey).IsUnique();
            entity.Property(x => x.Username).HasMaxLength(200).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.AvatarUrl).HasMaxLength(500);
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.HasIndex(x => x.CompanyId);
            entity.HasOne(x => x.Company)
                .WithMany(x => x.Users)
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UninstallTicket>(entity =>
        {
            entity.ToTable("uninstall_tickets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TicketHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => x.TicketHash).IsUnique();
            entity.HasIndex(x => x.CompanyId);
            entity.HasOne(x => x.Company)
                .WithMany()
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentEvent>(entity =>
        {
            entity.ToTable("agent_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PayloadJson).IsRequired();
            entity.Property(x => x.ChainHash).HasMaxLength(128);
            entity.HasIndex(x => new { x.UserId, x.ClientEventId }).IsUnique();
            entity.HasIndex(x => new { x.CompanyId, x.OccurredAt });
            entity.HasIndex(x => new { x.UserId, x.VaultSequence });
            entity.HasIndex(x => x.EventType);
            entity.HasOne(x => x.Company)
                .WithMany()
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StaffTrackingConfigEntity>(entity =>
        {
            entity.ToTable("staff_tracking_configs");
            entity.HasKey(x => x.StaffUserId);
            entity.Property(x => x.ScreenshotQuality).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => x.CompanyId);
            entity.HasOne(x => x.StaffUser)
                .WithOne()
                .HasForeignKey<StaffTrackingConfigEntity>(x => x.StaffUserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Company)
                .WithMany()
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentSequenceState>(entity =>
        {
            entity.ToTable("agent_sequence_states");
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.LastChainHash).HasMaxLength(128);
            entity.HasOne(x => x.User)
                .WithOne()
                .HasForeignKey<AgentSequenceState>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
