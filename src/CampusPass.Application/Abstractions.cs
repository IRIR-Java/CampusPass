using CampusPass.Domain;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Application;

/// <summary>The data the business rules need. Implemented by Infrastructure (EF Core + SQLite), so the rules never depend on a database engine.</summary>
public interface IAppDb
{
    DbSet<UserAccount> Users { get; }
    DbSet<Department> Departments { get; }
    DbSet<StudentRecord> Students { get; }
    DbSet<Ticket> Tickets { get; }
    DbSet<Gatepass> Gatepasses { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<FacultyDay> FacultyDays { get; }
    DbSet<SystemSettings> Settings { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface IClock
{
    /// <summary>Campus local time (not the server's zone).</summary>
    DateTime Now { get; }
}

public sealed class CampusClock(TimeZoneInfo zone) : IClock
{
    public DateTime Now => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(DateTime.UtcNow, zone), DateTimeKind.Unspecified);

    public static TimeZoneInfo Resolve(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z) ? z : TimeZoneInfo.Local;
}

/// <summary>A friendly error the UI shows as-is (e.g. "Finish ICT-004 before calling the next student").</summary>
public sealed class RuleException(string message) : Exception(message);

public static class Settings
{
    public static async Task<SystemSettings> GetAsync(this IAppDb db) =>
        await db.Settings.FirstOrDefaultAsync() ?? throw new RuleException("CampusPass has not been set up yet.");
}
