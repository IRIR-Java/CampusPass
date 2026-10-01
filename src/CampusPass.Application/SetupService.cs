using System.Security.Cryptography;
using CampusPass.Domain;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Application;

/// <summary>
/// First run. A fresh install has no accounts at all; the person holding the one-time setup code
/// (printed in the server log, like Jenkins' initial admin password) creates the first admin and the
/// institution settings. Default service departments are created so the system is usable immediately;
/// the admin can rename, edit or disable them.
/// </summary>
public class SetupService(IAppDb db, IClock clock)
{
    public Task<bool> IsCompleteAsync() => db.Users.AnyAsync(u => u.Role == Role.Admin);

    public async Task CompleteAsync(string institution, string semester, DateOnly passValidUntil,
                                    string adminUsername, string adminName, string password)
    {
        if (await IsCompleteAsync()) throw new RuleException("CampusPass is already set up.");
        if (string.IsNullOrWhiteSpace(institution)) throw new RuleException("Enter the institution's name.");
        if (string.IsNullOrWhiteSpace(adminName)) throw new RuleException("Enter the administrator's name.");
        var username = AccountService.Normalize(adminUsername);
        if (username.Length < 3) throw new RuleException("Choose an admin username (3+ characters).");
        AccountService.CheckStrength(username, password);
        if (password.Length < 12) throw new RuleException("The admin password needs at least 12 characters.");

        if (!await db.Settings.AnyAsync())
            db.Settings.Add(new SystemSettings
            {
                Id = 1,
                InstitutionName = institution.Trim(),
                Semester = string.IsNullOrWhiteSpace(semester) ? "Current semester" : semester.Trim(),
                PassValidUntil = passValidUntil,
                PaymentInstructions = "Pay at any bank into the university fee account. Use your registration number as the reference.",
                SupportContact = "In-app: use the service that fits your issue. In person: the ICT Centre help desk, Mon–Fri 8:00–17:00.",
                GatepassKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            });

        if (!await db.Departments.AnyAsync())
            db.Departments.AddRange(DefaultDepartments());

        var (hash, salt) = PasswordHasher.Hash(password);
        db.Users.Add(new UserAccount
        {
            Username = username, DisplayName = adminName.Trim(), Role = Role.Admin,
            PasswordHash = hash, PasswordSalt = salt, MustChangePassword = false, CreatedAt = clock.Now
        });
        await db.SaveChangesAsync();
    }

    /// <summary>The service points found at most universities. Editable in Admin → Services.</summary>
    public static IEnumerable<Department> DefaultDepartments() => new[]
    {
        new Department { Code = "ICT", Name = "ICT & Portal", Description = "Portal issues, system access, IT support", Location = "ICT Centre, Ground Floor", AvgServiceMinutes = 8, Icon = "laptop", Color = "blue", SortOrder = 1 },
        new Department { Code = "FIN", Name = "Finance", Description = "Fees, receipts, payments", Location = "Finance Office, Administration Block", AvgServiceMinutes = 5, Icon = "wallet", Color = "green", SortOrder = 2 },
        new Department { Code = "GP", Name = "GatePass / Security", Description = "Gate pass, access, security", Location = "Main Gate", AvgServiceMinutes = 3, Icon = "shield", Color = "amber", SortOrder = 3 },
        new Department { Code = "ACA", Name = "Academic", Description = "Results, transcripts, exam cards", Location = "Registrar's Office, Academic Block", AvgServiceMinutes = 6, Icon = "book", Color = "red", SortOrder = 4 },
        new Department { Code = "ADM", Name = "Administration", Description = "Documents, letters, certificates", Location = "Records Office, Administration Block", AvgServiceMinutes = 6, Icon = "doc", Color = "teal", SortOrder = 5 },
        new Department { Code = "GEN", Name = "Other", Description = "General inquiries", Location = "Student Help Desk, Library Concourse", AvgServiceMinutes = 5, Icon = "dots", Color = "grey", SortOrder = 6 },
    };
}
