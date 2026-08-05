using Microsoft.EntityFrameworkCore;

namespace Teamscop.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<UninstallTicket> UninstallTickets => Set<UninstallTicket>();
    public DbSet<UsbSessionTicket> UsbSessionTickets => Set<UsbSessionTicket>();
    public DbSet<AgentEvent> AgentEvents => Set<AgentEvent>();
    public DbSet<StaffTrackingConfigEntity> StaffTrackingConfigs => Set<StaffTrackingConfigEntity>();
    public DbSet<AgentSequenceState> AgentSequenceStates => Set<AgentSequenceState>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<PolicemanAuthorityGrant> PolicemanAuthorityGrants => Set<PolicemanAuthorityGrant>();

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
            entity.Property(x => x.BusinessTimeZoneId).HasMaxLength(100).HasDefaultValue("UTC");
            entity.Property(x => x.BusinessClockVersion).HasDefaultValue(0L);
            entity.Property(x => x.BusinessClockSynchronized).HasDefaultValue(false);
            entity.Property(x => x.OrgStructureVersion).HasDefaultValue(0L);
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
            entity.Property(x => x.AccessTotpSecret).HasMaxLength(128);
            entity.Property(x => x.AccessTotpEnabled).HasDefaultValue(false);
            entity.Property(x => x.IsPoliceman).HasDefaultValue(false);
            entity.Property(x => x.AuthorityVersion).HasDefaultValue(0L);
            entity.HasIndex(x => x.CompanyId);
            entity.HasIndex(x => new { x.CompanyId, x.IsPoliceman });
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

        modelBuilder.Entity<UsbSessionTicket>(entity =>
        {
            entity.ToTable("usb_session_tickets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TicketHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DeviceInstanceId).HasMaxLength(512);
            entity.HasIndex(x => x.TicketHash).IsUnique();
            entity.HasIndex(x => x.CompanyId);
            entity.HasIndex(x => x.DeviceUserId);
            entity.HasOne(x => x.Company)
                .WithMany()
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.DeviceUser)
                .WithMany()
                .HasForeignKey(x => x.DeviceUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentEvent>(entity =>
        {
            entity.ToTable("agent_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PayloadJson).IsRequired();
            entity.Property(x => x.ChainHash).HasMaxLength(128);
            entity.Property(x => x.BusinessTimeZoneId).HasMaxLength(100);
            entity.HasIndex(x => new { x.UserId, x.ClientEventId }).IsUnique();
            entity.HasIndex(x => new { x.CompanyId, x.OccurredAt });
            entity.HasIndex(x => new { x.CompanyId, x.BusinessOccurredAt });
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

        modelBuilder.Entity<Team>(entity =>
        {
            entity.ToTable("teams");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => x.CompanyId);
            entity.HasIndex(x => x.LeaderUserId).IsUnique();
            entity.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
            entity.HasOne(x => x.Company)
                .WithMany(c => c.Teams)
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Leader)
                .WithMany()
                .HasForeignKey(x => x.LeaderUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TeamMember>(entity =>
        {
            entity.ToTable("team_members");
            entity.HasKey(x => new { x.TeamId, x.StaffUserId });
            entity.HasIndex(x => x.StaffUserId).IsUnique();
            entity.HasOne(x => x.Team)
                .WithMany(t => t.Members)
                .HasForeignKey(x => x.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.StaffUser)
                .WithMany()
                .HasForeignKey(x => x.StaffUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PolicemanAuthorityGrant>(entity =>
        {
            entity.ToTable("policeman_authority_grants");
            entity.HasKey(x => new { x.StaffUserId, x.PackageId });
            entity.Property(x => x.PackageId).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.PackageId);
            entity.HasOne(x => x.StaffUser)
                .WithMany(u => u.AuthorityGrants)
                .HasForeignKey(x => x.StaffUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
