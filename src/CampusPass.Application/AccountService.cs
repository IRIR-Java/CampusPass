using System.Text.RegularExpressions;
using CampusPass.Domain;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Application;

public enum LoginOutcome { Ok, Wrong, Locked }

public record LoginResult(LoginOutcome Outcome, UserAccount? User);

/// <summary>
/// Sign-in and account administration. There is no self-registration: students get an account from their
/// Finance record, staff from the admin. Every issued password is one-time and must be changed on first login.
/// </summary>
public partial class AccountService(IAppDb db, IClock clock)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(5);
    public const int MinPasswordLength = 8;
    private static readonly (string Hash, string Salt) Dummy = PasswordHasher.Hash(Guid.NewGuid().ToString());

    public static string Normalize(string? username) => (username ?? "").Trim().ToUpperInvariant();

    public async Task<LoginResult> LoginAsync(string username, string password)
    {
        var key = Normalize(username);
        var user = await db.Users.Include(u => u.Department).FirstOrDefaultAsync(u => u.Username == key);
        var now = clock.Now;

        if (user is null || user.Disabled)
        {
            PasswordHasher.Verify(password ?? "", Dummy.Hash, Dummy.Salt);   // same cost, so timing does not reveal who exists
            return new LoginResult(LoginOutcome.Wrong, null);
        }
        if (user.LockedUntil > now) return new LoginResult(LoginOutcome.Locked, null);

        if (!PasswordHasher.Verify(password ?? "", user.PasswordHash, user.PasswordSalt))
        {
            user.FailedLogins++;
            if (user.FailedLogins >= MaxFailures) { user.LockedUntil = now + LockFor; user.FailedLogins = 0; }
            await db.SaveChangesAsync();
            return new LoginResult(user.LockedUntil > now ? LoginOutcome.Locked : LoginOutcome.Wrong, null);
        }

        user.FailedLogins = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;
        await db.SaveChangesAsync();
        return new LoginResult(LoginOutcome.Ok, user);
    }

    public async Task ChangePasswordAsync(int userId, string current, string next)
    {
        var user = await db.Users.FindAsync(userId) ?? throw new RuleException("Account not found.");
        if (!PasswordHasher.Verify(current ?? "", user.PasswordHash, user.PasswordSalt))
            throw new RuleException("Your current password is not correct.");
        CheckStrength(user.Username, next);
        if (next == current) throw new RuleException("Choose a new password, not the one you were given.");
        (user.PasswordHash, user.PasswordSalt) = PasswordHasher.Hash(next);
        user.MustChangePassword = false;
        await db.SaveChangesAsync();
    }

    public static void CheckStrength(string username, string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            throw new RuleException($"Use at least {MinPasswordLength} characters.");
        if (password.Contains(username, StringComparison.OrdinalIgnoreCase) || username.Contains(password, StringComparison.OrdinalIgnoreCase))
            throw new RuleException("Your password must not contain your username or reg number.");
    }

    /// <summary>Creates a staff or admin account and returns its one-time password (shown once).</summary>
    public async Task<string> CreateStaffAsync(string username, string displayName, Role role, int? departmentId, string? counter)
    {
        var key = Normalize(username);
        if (!UsernamePattern().IsMatch(key)) throw new RuleException("Username: 3–30 letters, numbers, dots, dashes or underscores.");
        if (string.IsNullOrWhiteSpace(displayName)) throw new RuleException("Enter the staff member's name.");
        if (role == Role.Student) throw new RuleException("Students get their login from their fee record.");
        if (role == Role.Staff && (departmentId is null || !await db.Departments.AnyAsync(d => d.Id == departmentId)))
            throw new RuleException("Choose the department this staff member serves.");
        if (await db.Users.AnyAsync(u => u.Username == key)) throw new RuleException($"The username {key} is already taken.");

        var temp = PasswordHasher.NewTemporaryPassword();
        var (hash, salt) = PasswordHasher.Hash(temp);
        db.Users.Add(new UserAccount
        {
            Username = key, DisplayName = displayName.Trim(), Role = role,
            DepartmentId = role == Role.Staff ? departmentId : null,
            Counter = string.IsNullOrWhiteSpace(counter) ? null : counter.Trim(),
            PasswordHash = hash, PasswordSalt = salt, MustChangePassword = true, CreatedAt = clock.Now
        });
        await db.SaveChangesAsync();
        return temp;
    }

    /// <summary>Gives a student with a fee record a login (username = reg no). Re-issuing resets the password.</summary>
    public async Task<string> IssueStudentLoginAsync(string regNo)
    {
        var key = Normalize(regNo);
        var record = await db.Students.FirstOrDefaultAsync(s => s.RegNo == key)
                     ?? throw new RuleException($"No fee record for {key}. Upload Finance's file first.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == key);
        if (user is not null && user.Role != Role.Student) throw new RuleException($"{key} is a staff username.");

        var temp = PasswordHasher.NewTemporaryPassword();
        var (hash, salt) = PasswordHasher.Hash(temp);
        if (user is null)
        {
            db.Users.Add(user = new UserAccount { Username = key, Role = Role.Student, RegNo = key, CreatedAt = clock.Now });
        }
        user.DisplayName = record.Name;
        user.Phone = record.Phone;
        user.PasswordHash = hash; user.PasswordSalt = salt;
        user.MustChangePassword = true; user.FailedLogins = 0; user.LockedUntil = null; user.Disabled = false;
        await db.SaveChangesAsync();
        return temp;
    }

    public async Task<string> ResetPasswordAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId) ?? throw new RuleException("Account not found.");
        var temp = PasswordHasher.NewTemporaryPassword();
        (user.PasswordHash, user.PasswordSalt) = PasswordHasher.Hash(temp);
        user.MustChangePassword = true; user.FailedLogins = 0; user.LockedUntil = null;
        await db.SaveChangesAsync();
        return temp;
    }

    public async Task SetDisabledAsync(int userId, bool disabled, int actingUserId)
    {
        if (userId == actingUserId) throw new RuleException("You cannot disable your own account.");
        var user = await db.Users.FindAsync(userId) ?? throw new RuleException("Account not found.");
        user.Disabled = disabled;
        await db.SaveChangesAsync();
    }

    [GeneratedRegex("^[A-Z0-9._-]{3,30}$")]
    private static partial Regex UsernamePattern();
}

public class NotificationService(IAppDb db, IClock clock)
{
    public void Add(int userId, string title, string body, string? link = null, string kind = "info") =>
        db.Notifications.Add(new Notification { UserId = userId, Title = title, Body = body, Link = link, Kind = kind, CreatedAt = clock.Now });

    public Task<int> UnreadAsync(int userId) => db.Notifications.CountAsync(n => n.UserId == userId && !n.Read);

    public async Task<IReadOnlyList<Notification>> ListAsync(int userId) =>
        await db.Notifications.Where(n => n.UserId == userId).OrderByDescending(n => n.Id).Take(50).ToListAsync();

    public async Task MarkAllReadAsync(int userId)
    {
        foreach (var n in await db.Notifications.Where(n => n.UserId == userId && !n.Read).ToListAsync()) n.Read = true;
        await db.SaveChangesAsync();
    }
}
