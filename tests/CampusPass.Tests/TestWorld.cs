using CampusPass.Application;
using CampusPass.Domain;
using CampusPass.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Tests;

public class FixedClock : IClock
{
    public DateTime Now { get; set; } = new(2026, 10, 6, 9, 10, 0);   // Tuesday 09:10
}

/// <summary>A fresh, set-up CampusPass on an in-memory SQLite database (the same engine as production).</summary>
public sealed class TestWorld : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    public AppDbContext Db { get; }
    public FixedClock Clock { get; } = new();
    public NotificationService Notify { get; }
    public QueueService Queue { get; }
    public AccountService Accounts { get; }
    public ClearanceService Clearance { get; }
    public FeeImportService Fees { get; }
    public ReportService Reports { get; }

    public TestWorld()
    {
        _conn.Open();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        Db.Database.EnsureCreated();
        Notify = new NotificationService(Db, Clock);
        Queue = new QueueService(Db, Clock, Notify);
        Accounts = new AccountService(Db, Clock);
        Clearance = new ClearanceService(Db, Clock);
        Fees = new FeeImportService(Db, Clock, Notify);
        Reports = new ReportService(Db, Clock);
        new SetupService(Db, Clock).CompleteAsync("Test University", "Sep–Dec 2026", new DateOnly(2026, 12, 18), "admin", "Admin", "correct-horse-battery").GetAwaiter().GetResult();
    }

    public Department Dept(string code) => Db.Departments.Single(d => d.Code == code);

    public async Task<UserAccount> StudentAsync(string reg, decimal total = 1000, decimal paid = 1000, string faculty = "Science")
    {
        Db.Students.Add(new StudentRecord { RegNo = reg, Name = "Student " + reg, Faculty = faculty, Phone = "0700", TotalFee = total, Paid = paid, BalanceAsOf = Clock.Now });
        await Db.SaveChangesAsync();
        await Accounts.IssueStudentLoginAsync(reg);
        return Db.Users.Single(u => u.Username == reg);
    }

    public async Task<UserAccount> StaffAsync(string username, string deptCode, string counter = "Counter 1")
    {
        await Accounts.CreateStaffAsync(username, "Officer " + username, Role.Staff, Dept(deptCode).Id, counter);
        return Db.Users.Single(u => u.Username == username.ToUpperInvariant());
    }

    public void Dispose() { Db.Dispose(); _conn.Dispose(); }
}
