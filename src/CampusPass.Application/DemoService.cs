using System.Security.Cryptography;
using CampusPass.Domain;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Application;

public record DemoSummary(int Students, int Staff, int Tickets);

/// <summary>
/// Loads a demonstration scenario for a twelve-person group: an admin, five service officers (STAFF 1..5) and
/// six students who each hit a different, recognisable issue (their reg number is their login). Every account
/// signs in with a trivial password so testers can jump straight in. This never runs in a normal (shipped)
/// deployment — only when the "Demo" flag is on, or when an admin deliberately clicks "Load demonstration data".
/// </summary>
public class DemoService(IAppDb db, IClock clock)
{
    /// <summary>Deliberately trivial: this is throwaway demonstration data, never production.</summary>
    public const string Password = "1234";

    public Task<bool> HasDataAsync() => db.Tickets.AnyAsync();

    public async Task<DemoSummary> LoadAsync()
    {
        // ---- wipe any previous activity (keeps nothing; a demo always starts clean) ----
        db.Tickets.RemoveRange(await db.Tickets.ToListAsync());
        db.Notifications.RemoveRange(await db.Notifications.ToListAsync());
        db.Gatepasses.RemoveRange(await db.Gatepasses.ToListAsync());
        db.Students.RemoveRange(await db.Students.ToListAsync());
        db.FacultyDays.RemoveRange(await db.FacultyDays.ToListAsync());
        db.Users.RemoveRange(await db.Users.ToListAsync());   // includes admins; the demo admin below replaces them
        await db.SaveChangesAsync();

        var now = clock.Now;
        var today = DateOnly.FromDateTime(now);

        // ---- settings (self-contained: demo mode needs no manual setup) ----
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is null)
        {
            settings = new SystemSettings
            {
                Id = 1,
                InstitutionName = "Chuka University",
                Semester = "Semester 1, 2026/2027",
                PassValidUntil = today.AddMonths(3),
                PaymentInstructions = "Pay at any bank into the university fee account. Use your registration number as the reference.",
                SupportContact = "In-app: use the service that fits your issue. In person: the ICT Centre help desk, Mon–Fri 8:00–17:00.",
                GatepassKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            };
            db.Settings.Add(settings);
        }

        // ---- departments (the standard six, with distinct locations) ----
        var depts = await db.Departments.OrderBy(d => d.SortOrder).ToListAsync();
        if (depts.Count == 0)
        {
            depts = SetupService.DefaultDepartments().ToList();
            db.Departments.AddRange(depts);
        }
        await db.SaveChangesAsync();
        Department D(string code) => depts.First(d => d.Code == code);

        var (hash, salt) = PasswordHasher.Hash(Password);
        UserAccount NewUser(string username, string name, Role role, Department? dept, string? counter, string? reg, string? phone) => new()
        {
            Username = AccountService.Normalize(username), DisplayName = name, Role = role,
            DepartmentId = dept?.Id, Counter = counter, RegNo = reg, Phone = phone, OnDuty = true,
            PasswordHash = hash, PasswordSalt = salt, MustChangePassword = false, CreatedAt = now.AddDays(-10)
        };

        // ---- admin + one service officer per department (super-simple logins) ----
        db.Users.Add(NewUser("admin", "System Administrator", Role.Admin, null, null, null, null));
        var staffByCode = new Dictionary<string, UserAccount>();
        foreach (var (user, name, code, counter) in StaffDefs)
        {
            var st = NewUser(user, name, Role.Staff, D(code), counter, null, null);
            db.Users.Add(st);
            staffByCode[code] = st;
        }

        // ---- students, each a distinct scenario (their reg number is their login) ----
        var userByKey = new Dictionary<string, UserAccount>();
        foreach (var s in StudentDefs)
        {
            var reg = AccountService.Normalize(s.Reg);
            if (s.Record)
                db.Students.Add(new StudentRecord
                {
                    RegNo = reg, Name = s.Name, Faculty = s.Faculty, Phone = "0712 000 000",
                    TotalFee = s.Total, Paid = s.Paid, BalanceAsOf = now.AddHours(-5)
                });
            var u = NewUser(reg, s.Name, Role.Student, null, null, reg, "0712 000 000");
            db.Users.Add(u);
            userByKey[reg] = u;
        }

        db.FacultyDays.Add(new FacultyDay { Faculty = "Business", Date = today });
        db.FacultyDays.Add(new FacultyDay { Faculty = "Education", Date = today.AddDays(1) });
        await db.SaveChangesAsync();   // ids assigned

        // ---- gatepasses for cleared students ----
        var passEnd = settings.PassValidUntil.ToDateTime(new TimeOnly(23, 59, 59));
        foreach (var s in StudentDefs.Where(x => x.Record && x.Paid >= x.Total))
            db.Gatepasses.Add(new Gatepass
            {
                TokenId = GatepassTokens.NewId(), RegNo = AccountService.Normalize(s.Reg),
                Semester = settings.Semester, IssuedAt = now.AddHours(-3), ExpiresAt = passEnd
            });

        // ---- the live queues ----
        var wToday = DayWindows(settings, today);
        var wTom = DayWindows(settings, today.AddDays(1));
        var seqByDept = new Dictionary<string, int>();
        var fillerN = 0;

        void Add(string code, TicketStatus status, UserAccount? student, string name, string reg, string? desc, int windowIndex = 2, bool tomorrow = false)
        {
            var wins = tomorrow ? wTom : wToday;
            if (wins.Count == 0) return;
            Window w = wins[Math.Min(windowIndex, wins.Count - 1)];
            var dept = D(code);
            var staff = staffByCode.GetValueOrDefault(code);
            seqByDept.TryGetValue(code, out var seq); seq++; seqByDept[code] = seq;
            var svc = Math.Max(2, dept.AvgServiceMinutes);
            var created = w.Start.AddMinutes(-30).AddSeconds(seq * 12);   // stable order → stable positions
            var t = new Ticket
            {
                Number = $"{code}-{seq:000}", DepartmentId = dept.Id,
                StudentUserId = student?.Id ?? 0, RegNo = AccountService.Normalize(reg), StudentName = name,
                Mode = tomorrow ? TicketMode.Appointment : TicketMode.Queue, Description = desc, Status = status,
                WindowStart = w.Start, WindowEnd = w.End, CreatedAt = created, UpdatedAt = created
            };
            if (status is TicketStatus.Completed or TicketStatus.NoShow or TicketStatus.Serving or TicketStatus.Called)
            {
                t.StaffUserId = staff?.Id; t.StaffName = staff?.DisplayName; t.Counter = staff?.Counter;
                t.CalledAt = w.Start.AddMinutes(1);
            }
            switch (status)
            {
                case TicketStatus.Completed:
                    t.ServingAt = w.Start.AddMinutes(2); t.ClosedAt = t.UpdatedAt = w.Start.AddMinutes(2 + svc);
                    t.Resolution = "Resolved at the counter."; break;
                case TicketStatus.NoShow:
                    t.ClosedAt = t.UpdatedAt = w.Start.AddMinutes(4); break;
                case TicketStatus.Serving:
                    t.ServingAt = t.UpdatedAt = w.Start.AddMinutes(2); break;
            }
            db.Tickets.Add(t);
        }

        void Filler(string code, TicketStatus status, int windowIndex = 2, bool tomorrow = false)
        {
            var name = FillerNames[fillerN % FillerNames.Length];
            var n = 7 + fillerN;
            var reg = $"EB{n}/{n:0000}/23";   // same shape as the named students (EB7/0007/23, ...)
            fillerN++;
            Add(code, status, null, name, reg, null, windowIndex, tomorrow);
        }

        UserAccount S(string reg) => userByKey[AccountService.Normalize(reg)];

        // ICT (STAFF 1) — someone in service, one called, and a waiting line: EB1 lands around position 3.
        Filler("ICT", TicketStatus.Completed, 0);
        Filler("ICT", TicketStatus.Completed, 1);
        Filler("ICT", TicketStatus.Serving, 2);
        Filler("ICT", TicketStatus.Called, 2);
        Add("ICT", TicketStatus.Waiting, S("EB1/0001/23"), "Brian Mbuya", "EB1/0001/23", "Portal login not working", 2);
        Filler("ICT", TicketStatus.Waiting, 2);
        Filler("ICT", TicketStatus.Waiting, 2);
        Filler("ICT", TicketStatus.Waiting, 1, tomorrow: true);

        // Finance (STAFF 2)
        Filler("FIN", TicketStatus.Completed, 1);
        Filler("FIN", TicketStatus.Serving, 2);
        Add("FIN", TicketStatus.Waiting, S("EB2/0002/23"), "Faith Wanjiru", "EB2/0002/23", "Fee payment not reflecting", 2);
        Add("FIN", TicketStatus.Waiting, S("EB5/0005/23"), "Samuel Kiptoo", "EB5/0005/23", "Paid in full but not yet cleared", 2);
        Filler("FIN", TicketStatus.Waiting, 2);
        Filler("FIN", TicketStatus.Waiting, 1, tomorrow: true);

        // Academic (STAFF 4) — EB3 is being called right now; a filler missed their turn earlier
        Filler("ACA", TicketStatus.Completed, 1);
        Filler("ACA", TicketStatus.NoShow, 1);
        Add("ACA", TicketStatus.Called, S("EB3/0003/23"), "Amina Yusuf", "EB3/0003/23", "Need my exam card", 2);
        Filler("ACA", TicketStatus.Waiting, 2);
        Filler("ACA", TicketStatus.Waiting, 2);

        // GatePass / Security (STAFF 3) — EB4 is being served
        Filler("GP", TicketStatus.Completed, 1);
        Add("GP", TicketStatus.Serving, S("EB4/0004/23"), "Grace Njeri", "EB4/0004/23", "Gatepass not scanning at the gate", 2);
        Filler("GP", TicketStatus.Waiting, 2);
        Filler("GP", TicketStatus.Waiting, 2);

        // Administration (STAFF 5) — EB6 has no fee record yet
        Filler("ADM", TicketStatus.Completed, 1);
        Add("ADM", TicketStatus.Waiting, S("EB6/0006/23"), "Lucy Atieno", "EB6/0006/23", "General inquiry / no fee record", 2);
        Filler("ADM", TicketStatus.Waiting, 2);

        // Other — waiting tickets (no dedicated officer in this demo)
        Filler("GEN", TicketStatus.Waiting, 2);
        Filler("GEN", TicketStatus.Waiting, 2);

        await db.SaveChangesAsync();

        // ---- a few notifications so bells show unread ----
        void Note(string reg, string title, string body, string kind, int minsAgo)
        {
            var u = S(reg);
            var t = db.Tickets.Local.FirstOrDefault(x => x.StudentUserId == u.Id);
            db.Notifications.Add(new Notification { UserId = u.Id, Title = title, Body = body, Kind = kind, Link = t is null ? "/Student" : $"/Student/Ticket/{t.Id}", CreatedAt = now.AddMinutes(-minsAgo) });
        }
        Note("EB1/0001/23", "You're almost up!", "Head to ICT & Portal, ICT Centre. Your turn is near.", "almost", 3);
        Note("EB3/0003/23", "It's your turn!", "Go to Academic, Registrar's Office. Show your ticket.", "called", 1);
        Note("EB4/0004/23", "It's your turn!", "You're being served at GatePass / Security.", "called", 6);
        await db.SaveChangesAsync();

        return new DemoSummary(StudentDefs.Length, StaffDefs.Length, await db.Tickets.CountAsync());
    }

    // ------------------------------------------------------------ seed definitions

    // A twelve-person group: 1 admin, 5 staff (STAFF 1..5) and 6 students (their reg number is their login).
    private static readonly (string User, string Name, string Code, string Counter)[] StaffDefs =
    {
        ("STAFF 1", "A. Wanjiku", "ICT", "Counter 1"),
        ("STAFF 2", "C. Achieng", "FIN", "Counter 1"),
        ("STAFF 3", "D. Mutua",   "GP",  "Gate 1"),
        ("STAFF 4", "E. Njoroge", "ACA", "Counter 1"),
        ("STAFF 5", "F. Kilonzo", "ADM", "Counter 1"),
    };

    private static readonly (string Reg, string Name, string Faculty, decimal Total, decimal Paid, bool Record)[] StudentDefs =
    {
        ("EB1/0001/23", "Brian Mbuya",   "Computing & Informatics", 96000m,  96000m, true),   // ICT, waiting in line, cleared
        ("EB2/0002/23", "Faith Wanjiru", "Business",                88000m,  40000m, true),   // Finance, owes fees
        ("EB3/0003/23", "Amina Yusuf",   "Education",               84000m,  84000m, true),   // Academic, being called now
        ("EB4/0004/23", "Grace Njeri",   "Nursing",                120000m, 120000m, true),   // GatePass, being served now
        ("EB5/0005/23", "Samuel Kiptoo", "Science",                 92000m,  95000m, true),   // Finance, overpaid (credit)
        ("EB6/0006/23", "Lucy Atieno",   "Education",                   0m,      0m, false),  // no fee record yet
    };

    private static readonly string[] FillerNames =
    {
        "J. Kariuki", "N. Chepkoech", "V. Kimani", "E. Wambui", "B. Ochieng",
        "T. Mwende", "S. Barasa", "L. Cherono", "M. Anyango", "P. Kiprop",
    };

    /// <summary>Today's time windows — like WorkingCalendar, but built even on a weekend so a demo always has a live day.</summary>
    private static List<Window> DayWindows(SystemSettings s, DateOnly day)
    {
        var list = new List<Window>();
        var wm = Math.Clamp(s.WindowMinutes, 15, 240);
        var t = day.ToDateTime(s.OpenTime);
        var close = day.ToDateTime(s.CloseTime);
        var lunchStart = day.ToDateTime(s.LunchStart);
        var lunchEnd = day.ToDateTime(s.LunchEnd);
        var n = 0;
        while (t.AddMinutes(wm) <= close)
        {
            var end = t.AddMinutes(wm);
            if (t < lunchEnd && end > lunchStart) { t = lunchEnd; continue; }
            list.Add(new Window(t, end, ++n));
            t = end;
        }
        return list;
    }
}
