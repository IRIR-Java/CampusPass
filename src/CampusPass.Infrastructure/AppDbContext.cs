using CampusPass.Application;
using CampusPass.Domain;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Infrastructure;

/// <summary>EF Core implementation of <see cref="IAppDb"/>. SQLite here; switching to SQL Server/PostgreSQL is a provider change.</summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDb
{
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<StudentRecord> Students => Set<StudentRecord>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Gatepass> Gatepasses => Set<Gatepass>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<FacultyDay> FacultyDays => Set<FacultyDay>();
    public DbSet<SystemSettings> Settings => Set<SystemSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserAccount>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.RegNo);
            e.Property(u => u.Username).HasMaxLength(40);
            e.Property(u => u.DisplayName).HasMaxLength(120);
            e.HasOne(u => u.Department).WithMany().HasForeignKey(u => u.DepartmentId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<Department>(e =>
        {
            e.HasIndex(d => d.Code).IsUnique();
            e.Property(d => d.Code).HasMaxLength(6);
            e.Property(d => d.Name).HasMaxLength(60);
        });
        b.Entity<StudentRecord>(e =>
        {
            e.HasKey(s => s.RegNo);
            e.Property(s => s.RegNo).HasMaxLength(30);
            e.HasIndex(s => s.Faculty);
        });
        b.Entity<Ticket>(e =>
        {
            e.HasIndex(t => t.Number).IsUnique();
            e.HasIndex(t => new { t.DepartmentId, t.WindowStart });
            e.HasIndex(t => t.StudentUserId);
            e.HasOne(t => t.Department).WithMany().HasForeignKey(t => t.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Gatepass>(e =>
        {
            e.HasIndex(p => p.TokenId).IsUnique();
            e.HasIndex(p => p.RegNo);
        });
        b.Entity<Notification>().HasIndex(n => new { n.UserId, n.Read });
        b.Entity<FacultyDay>().HasIndex(f => f.Faculty).IsUnique();
        b.Entity<SystemSettings>().Property(s => s.Id).ValueGeneratedNever();
    }
}
