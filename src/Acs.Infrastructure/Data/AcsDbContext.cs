using Acs.Domain.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Data;

public class AcsDbContext(DbContextOptions<AcsDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeIdentifier> EmployeeIdentifiers => Set<EmployeeIdentifier>();
    public DbSet<Building> Buildings => Set<Building>();
    public DbSet<BuildingSection> BuildingSections => Set<BuildingSection>();
    public DbSet<Floor> Floors => Set<Floor>();
    public DbSet<Corridor> Corridors => Set<Corridor>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<PlanDevice> PlanDevices => Set<PlanDevice>();
    public DbSet<Reader> Readers => Set<Reader>();
    public DbSet<ReaderDependency> ReaderDependencies => Set<ReaderDependency>();
    public DbSet<ApprovalMatrix> ApprovalMatrices => Set<ApprovalMatrix>();
    public DbSet<ApprovalLevel> ApprovalLevels => Set<ApprovalLevel>();
    public DbSet<Approver> Approvers => Set<Approver>();
    public DbSet<Deputy> Deputies => Set<Deputy>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<AccessRequestItem> AccessRequestItems => Set<AccessRequestItem>();
    public DbSet<ApprovalDecision> ApprovalDecisions => Set<ApprovalDecision>();
    public DbSet<ReaderGroup> ReaderGroups => Set<ReaderGroup>();
    public DbSet<ReaderGroupMember> ReaderGroupMembers => Set<ReaderGroupMember>();
    public DbSet<AutoAssignmentRule> AutoAssignmentRules => Set<AutoAssignmentRule>();
    public DbSet<AccessRequestItemStage> AccessRequestItemStages => Set<AccessRequestItemStage>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>ASP.NET Data Protection klíče — sdílené oběma HA nody (bezestavovost).</summary>
    public DbSet<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey> DataProtectionKeys
        => Set<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.UserName).IsUnique();
            e.Property(u => u.UserName).HasMaxLength(256);
            e.HasOne(u => u.Employee).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Employee>(e =>
        {
            e.HasIndex(x => x.ExternalId);
            e.HasIndex(x => x.AdAccount);
            e.Property(x => x.FirstName).HasMaxLength(128);
            e.Property(x => x.LastName).HasMaxLength(128);
        });

        modelBuilder.Entity<EmployeeIdentifier>(e =>
        {
            e.Property(x => x.Value).HasMaxLength(128);
            e.HasIndex(x => new { x.Type, x.Value });
            e.HasIndex(x => x.EmployeeId);
            e.HasOne(x => x.Employee).WithMany(emp => emp.Identifiers)
                .HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Reader>(e =>
        {
            e.HasIndex(x => x.ExternalId);
            e.HasIndex(x => x.DeviceNumber);
            e.Property(x => x.DeviceNumber).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(256);
            e.HasOne(x => x.Room).WithMany(r => r.Readers).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Corridor).WithMany(c => c.Readers).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ApprovalMatrix).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BuildingSection>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.HasOne(x => x.Building).WithMany(b => b.Sections)
                .HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Floor>(e =>
        {
            e.HasOne(x => x.Section).WithMany(s => s.Floors)
                .HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Corridor>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.HasOne(x => x.Floor).WithMany(f => f.Corridors)
                .HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ParentCorridor).WithMany()
                .HasForeignKey(x => x.ParentCorridorId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Room>(e =>
        {
            e.HasOne(x => x.Corridor).WithMany(c => c.Rooms)
                .HasForeignKey(x => x.CorridorId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PlanDevice>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.HasOne(x => x.Floor).WithMany()
                .HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReaderDependency>(e =>
        {
            e.HasIndex(x => new { x.ReaderId, x.RequiresReaderId }).IsUnique();
            e.HasOne(x => x.Reader).WithMany(r => r.Dependencies)
                .HasForeignKey(x => x.ReaderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.RequiresReader).WithMany()
                .HasForeignKey(x => x.RequiresReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApprovalLevel>(e =>
        {
            e.HasIndex(x => new { x.MatrixId, x.Order }).IsUnique();
            e.HasOne(x => x.Matrix).WithMany(m => m.Levels)
                .HasForeignKey(x => x.MatrixId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Approver>(e =>
        {
            e.HasOne(x => x.Level).WithMany(l => l.Approvers)
                .HasForeignKey(x => x.LevelId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Deputy>(e =>
        {
            e.HasOne(x => x.PrincipalUser).WithMany()
                .HasForeignKey(x => x.PrincipalUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.DeputyUser).WithMany()
                .HasForeignKey(x => x.DeputyUserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccessRequest>(e =>
        {
            e.HasOne(x => x.RequesterUser).WithMany()
                .HasForeignKey(x => x.RequesterUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.TargetEmployee).WithMany()
                .HasForeignKey(x => x.TargetEmployeeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccessRequestItem>(e =>
        {
            e.HasOne(x => x.Request).WithMany(r => r.Items)
                .HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Reader).WithMany()
                .HasForeignKey(x => x.ReaderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReaderGroup).WithMany()
                .HasForeignKey(x => x.ReaderGroupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccessRequestItemStage>(e =>
        {
            e.HasIndex(x => new { x.ItemId, x.Order }).IsUnique();
            e.HasOne(x => x.Item).WithMany(i => i.Stages)
                .HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Matrix).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReaderGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(256);
            e.HasOne(x => x.ApprovalMatrix).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ReaderGroupMember>(e =>
        {
            e.HasOne(x => x.Group).WithMany(g => g.Members)
                .HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Reader).WithMany()
                .HasForeignKey(x => x.ReaderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ChildGroup).WithMany()
                .HasForeignKey(x => x.ChildGroupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AutoAssignmentRule>(e =>
        {
            e.Property(x => x.Department).HasMaxLength(256);
            e.HasOne(x => x.ReaderGroup).WithMany().OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApprovalDecision>(e =>
        {
            e.HasOne(x => x.Item).WithMany(i => i.Decisions)
                .HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ApproverUser).WithMany()
                .HasForeignKey(x => x.ApproverUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Setting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.At);
        });
    }
}
