using System.Globalization;
using System.Text;
using CampusPass.Application;
using CampusPass.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Web.Controllers;

public class UsersPage
{
    public string Tab { get; init; } = "staff";
    public IReadOnlyList<UserAccount> Staff { get; init; } = Array.Empty<UserAccount>();
    public IReadOnlyList<(StudentRecord Record, UserAccount? Login)> Students { get; init; } = Array.Empty<(StudentRecord, UserAccount?)>();
    public IReadOnlyList<Department> Departments { get; init; } = Array.Empty<Department>();
    public string? Q { get; init; }
    public int StudentTotal { get; init; }
    public int WithoutLogin { get; init; }
}

public class BatchesPage
{
    public SystemSettings Settings { get; init; } = null!;
    public IReadOnlyList<FacultyDay> FacultyDays { get; init; } = Array.Empty<FacultyDay>();
    public IReadOnlyList<string> Faculties { get; init; } = Array.Empty<string>();
    public IReadOnlyList<Window> SampleDay { get; init; } = Array.Empty<Window>();
}

/// <summary>Queue manager / administrator: services, users, batches, fee data, reports and settings.</summary>
[Authorize(Roles = nameof(Role.Admin))]
public class AdminController(IAppDb db, AccountService accounts, FeeImportService fees, ReportService reports, IClock clock) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index() => View(await reports.AdminAsync());

    // ------------------------------------------------------------ services (departments)

    [HttpGet]
    public async Task<IActionResult> Services(int? edit)
    {
        ViewData["Edit"] = edit is int id ? await db.Departments.FindAsync(id) : null;
        ViewData["StaffCounts"] = await db.Users.Where(u => u.Role == Role.Staff && !u.Disabled && u.DepartmentId != null)
            .GroupBy(u => u.DepartmentId!.Value).ToDictionaryAsync(g => g.Key, g => g.Count());
        return View(await db.Departments.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync());
    }

    [HttpPost]
    public async Task<IActionResult> SaveService(int? id, string code, string name, string? description, string? location, int avgMinutes, string icon, string color, int sortOrder, bool isActive)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length is < 2 or > 6 || !code.All(char.IsLetterOrDigit)) throw new RuleException("Code: 2–6 letters or digits (it prefixes ticket numbers).");
        if (string.IsNullOrWhiteSpace(name)) throw new RuleException("Enter a name.");
        if (await db.Departments.AnyAsync(d => d.Code == code && d.Id != id)) throw new RuleException($"The code {code} is already used.");

        var d = id is int x ? await db.Departments.FindAsync(x) ?? throw new RuleException("Service not found.") : new Department();
        (d.Code, d.Name, d.Description, d.Location) = (code, name.Trim(), description?.Trim() ?? "", location?.Trim() ?? "");
        (d.AvgServiceMinutes, d.Icon, d.Color, d.SortOrder, d.IsActive) = (Math.Clamp(avgMinutes, 1, 120), icon, color, sortOrder, isActive);
        if (id is null) db.Departments.Add(d);
        await db.SaveChangesAsync();
        TempData["Success"] = $"{d.Name} saved.";
        return RedirectToAction(nameof(Services));
    }

    // ------------------------------------------------------------ users

    [HttpGet]
    public async Task<IActionResult> Users(string tab = "staff", string? q = null)
    {
        var deps = await db.Departments.OrderBy(d => d.SortOrder).ToListAsync();
        var studentsQ = db.Students.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var k = q.Trim().ToUpperInvariant();
            studentsQ = studentsQ.Where(s => s.RegNo.Contains(k) || s.Name.ToUpper().Contains(k));
        }
        var records = tab == "students" ? await studentsQ.OrderBy(s => s.RegNo).Take(200).ToListAsync() : new List<StudentRecord>();
        var regs = records.Select(r => r.RegNo).ToList();
        var logins = await db.Users.Where(u => u.Role == Role.Student && regs.Contains(u.Username)).ToDictionaryAsync(u => u.Username);
        var total = await db.Students.CountAsync();
        var withLogin = await db.Users.CountAsync(u => u.Role == Role.Student);

        return View(new UsersPage
        {
            Tab = tab == "students" ? "students" : "staff",
            Staff = await db.Users.Include(u => u.Department).Where(u => u.Role != Role.Student).OrderBy(u => u.Role).ThenBy(u => u.DisplayName).ToListAsync(),
            Students = records.Select(r => (r, logins.GetValueOrDefault(r.RegNo))).ToList(),
            Departments = deps, Q = q, StudentTotal = total, WithoutLogin = Math.Max(0, total - withLogin)
        });
    }

    [HttpPost]
    public async Task<IActionResult> CreateStaff(string username, string displayName, Role role, int? departmentId, string? counter)
    {
        var temp = await accounts.CreateStaffAsync(username, displayName, role, departmentId, counter);
        TempData["Credential"] = $"{AccountService.Normalize(username)}|{temp}";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost]
    public async Task<IActionResult> IssueLogin(string regNo, string? q)
    {
        var temp = await accounts.IssueStudentLoginAsync(regNo);
        TempData["Credential"] = $"{AccountService.Normalize(regNo)}|{temp}";
        return RedirectToAction(nameof(Users), new { tab = "students", q });
    }

    /// <summary>One-time passwords for every student without a login, as a CSV for SMS/email distribution.</summary>
    [HttpPost]
    public async Task<IActionResult> IssueAll()
    {
        var existing = await db.Users.Where(u => u.Role == Role.Student).Select(u => u.Username).ToListAsync();
        var todo = await db.Students.Where(s => !existing.Contains(s.RegNo)).OrderBy(s => s.RegNo).Take(5000).ToListAsync();
        var sb = new StringBuilder("RegNo,Name,Phone,OneTimePassword\n");
        foreach (var s in todo)
            sb.Append($"{s.RegNo},\"{s.Name.Replace("\"", "\"\"")}\",{s.Phone},{await accounts.IssueStudentLoginAsync(s.RegNo)}\n");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"campuspass-logins-{clock.Now:yyyyMMdd-HHmm}.csv");
    }

    [HttpPost]
    public async Task<IActionResult> ResetPassword(int id, string tab = "staff", string? q = null)
    {
        var u = await db.Users.FindAsync(id) ?? throw new RuleException("Account not found.");
        TempData["Credential"] = $"{u.Username}|{await accounts.ResetPasswordAsync(id)}";
        return RedirectToAction(nameof(Users), new { tab, q });
    }

    [HttpPost]
    public async Task<IActionResult> SetPassword(string username, string password, bool mustChange, string tab = "staff")
    {
        await accounts.SetPasswordAsync(username, password, mustChange);
        TempData["Success"] = $"Password set for {AccountService.Normalize(username)}{(mustChange ? ". They must change it at next sign-in." : ".")}";
        return RedirectToAction(nameof(Users), new { tab });
    }

    [HttpPost]
    public async Task<IActionResult> ToggleDisabled(int id)
    {
        var u = await db.Users.FindAsync(id) ?? throw new RuleException("Account not found.");
        await accounts.SetDisabledAsync(id, !u.Disabled, User.UserId());
        TempData["Success"] = $"{u.DisplayName} {(u.Disabled ? "disabled" : "enabled")}.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost]
    public async Task<IActionResult> ToggleDuty(int id)
    {
        var u = await db.Users.FindAsync(id) ?? throw new RuleException("Account not found.");
        u.OnDuty = !u.OnDuty;
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Users));
    }

    // ------------------------------------------------------------ fee records

    [HttpGet]
    public async Task<IActionResult> Fees()
    {
        ViewData["Count"] = await db.Students.CountAsync();
        ViewData["Newest"] = await db.Students.OrderByDescending(s => s.BalanceAsOf).Select(s => (DateTime?)s.BalanceAsOf).FirstOrDefaultAsync();
        return View((ImportResult?)null);
    }

    [HttpPost, RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> Fees(IFormFile? file)
    {
        ViewData["Count"] = await db.Students.CountAsync();
        if (file is null || file.Length == 0) throw new RuleException("Choose the CSV file from Finance.");
        if (!Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)) throw new RuleException("Only .csv files are accepted.");
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
        var result = await fees.ImportAsync(reader);
        ViewData["Count"] = await db.Students.CountAsync();
        ViewData["Newest"] = clock.Now;
        return View(result);
    }

    [HttpGet]
    public IActionResult FeeTemplate() =>
        File(Encoding.UTF8.GetBytes(FeeImportService.Header + "\n"), "text/csv", "fee-balances-template.csv");

    // ------------------------------------------------------------ batches (time windows + faculty days)

    [HttpGet]
    public async Task<IActionResult> Batches()
    {
        var s = await db.GetAsync();
        var cal = new WorkingCalendar(s);
        var sample = cal.ServiceDays(DateOnly.FromDateTime(clock.Now), 1).FirstOrDefault();
        return View(new BatchesPage
        {
            Settings = s,
            FacultyDays = await db.FacultyDays.OrderBy(f => f.Date).ThenBy(f => f.Faculty).ToListAsync(),
            Faculties = await db.Students.Select(x => x.Faculty).Distinct().OrderBy(x => x).ToListAsync(),
            SampleDay = cal.Windows(sample)
        });
    }

    [HttpPost]
    public async Task<IActionResult> SaveHours(string open, string close, string lunchStart, string lunchEnd, int windowMinutes, int bookingDaysAhead, string? holidays)
    {
        static TimeOnly T(string v) => TimeOnly.TryParseExact(v, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : throw new RuleException("Times must look like 08:00.");
        var s = await db.GetAsync();
        var (o, c, ls, le) = (T(open), T(close), T(lunchStart), T(lunchEnd));
        if (c <= o) throw new RuleException("Closing time must be after opening time.");
        if (le < ls) throw new RuleException("Lunch must end after it starts.");
        (s.OpenTime, s.CloseTime, s.LunchStart, s.LunchEnd) = (o, c, ls, le);
        s.WindowMinutes = Math.Clamp(windowMinutes, 15, 240);
        s.BookingDaysAhead = Math.Clamp(bookingDaysAhead, 1, 30);
        s.Holidays = string.Join(",", (holidays ?? "").Split(new[] { ',', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(h => DateOnly.TryParseExact(h.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)));
        await db.SaveChangesAsync();
        TempData["Success"] = "Opening hours and batches saved.";
        return RedirectToAction(nameof(Batches));
    }

    [HttpPost]
    public async Task<IActionResult> AddFacultyDay(string faculty, DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(faculty)) throw new RuleException("Enter the faculty exactly as in Finance's file.");
        var row = await db.FacultyDays.FirstOrDefaultAsync(f => f.Faculty == faculty.Trim());
        if (row is null) db.FacultyDays.Add(new FacultyDay { Faculty = faculty.Trim(), Date = date });
        else row.Date = date;
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Batches));
    }

    [HttpPost]
    public async Task<IActionResult> RemoveFacultyDay(int id)
    {
        if (await db.FacultyDays.FindAsync(id) is FacultyDay f) { db.FacultyDays.Remove(f); await db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Batches));
    }

    // ------------------------------------------------------------ reports & settings

    [HttpGet]
    public async Task<IActionResult> Reports(int days = 7)
    {
        ViewData["Days"] = days;
        return View(await reports.RangeAsync(days));
    }

    [HttpGet]
    public async Task<IActionResult> Settings() => View(await db.GetAsync());

    [HttpPost]
    public async Task<IActionResult> Settings(string institutionName, string semester, DateOnly passValidUntil, string? paymentInstructions, string? supportContact)
    {
        if (string.IsNullOrWhiteSpace(institutionName)) throw new RuleException("Enter the institution's name.");
        var s = await db.GetAsync();
        s.InstitutionName = institutionName.Trim();
        s.Semester = string.IsNullOrWhiteSpace(semester) ? s.Semester : semester.Trim();
        if (passValidUntil != s.PassValidUntil)
        {
            s.PassValidUntil = passValidUntil;
            var end = passValidUntil.ToDateTime(new TimeOnly(23, 59, 59));
            foreach (var p in await db.Gatepasses.Where(p => !p.Revoked).ToListAsync()) p.ExpiresAt = end;
        }
        s.PaymentInstructions = paymentInstructions?.Trim() ?? "";
        s.SupportContact = supportContact?.Trim() ?? "";
        await db.SaveChangesAsync();
        TempData["Success"] = "Settings saved.";
        return RedirectToAction(nameof(Settings));
    }
}
