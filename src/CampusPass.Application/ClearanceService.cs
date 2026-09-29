using System.Globalization;
using System.Text;
using CampusPass.Domain;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Application;

public record Clearance(StudentRecord Record, bool Cleared, bool Stale, Gatepass? Pass);

public enum VerifyOutcome { Valid, Invalid, NotFound, Expired, Revoked, NotCleared }

public record VerifyResult(VerifyOutcome Outcome, Gatepass? Pass, StudentRecord? Student);

/// <summary>"Am I cleared?" without a queue, the gatepass, and what the gate sees when it scans one.</summary>
public class ClearanceService(IAppDb db, IClock clock)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);

    public async Task<Clearance?> GetAsync(string? regNo)
    {
        var key = AccountService.Normalize(regNo);
        var r = await db.Students.FirstOrDefaultAsync(s => s.RegNo == key);
        if (r is null) return null;
        return new Clearance(r, r.IsCleared, clock.Now - r.BalanceAsOf > StaleAfter, await ActivePassAsync(key));
    }

    /// <summary>Cleared students don't ask for a pass: it is created the first time they need it. Idempotent.</summary>
    public async Task<Gatepass?> EnsurePassAsync(string? regNo)
    {
        var c = await GetAsync(regNo);
        if (c is null || !c.Cleared) return null;
        if (c.Pass is not null) return c.Pass;
        var s = await db.GetAsync();
        var pass = new Gatepass
        {
            TokenId = GatepassTokens.NewId(),
            RegNo = c.Record.RegNo,
            Semester = s.Semester,
            IssuedAt = clock.Now,
            ExpiresAt = s.PassValidUntil.ToDateTime(new TimeOnly(23, 59, 59))
        };
        db.Gatepasses.Add(pass);
        await db.SaveChangesAsync();
        return pass;
    }

    public async Task<string> TokenAsync(Gatepass pass) => new GatepassTokens((await db.GetAsync()).GatepassKey).ToToken(pass.TokenId);

    public async Task<VerifyResult> VerifyAsync(string? token)
    {
        var tokens = new GatepassTokens((await db.GetAsync()).GatepassKey);
        if (!tokens.TryParse(token, out var id)) return new(VerifyOutcome.Invalid, null, null);
        var pass = await db.Gatepasses.FirstOrDefaultAsync(p => p.TokenId == id);
        if (pass is null) return new(VerifyOutcome.NotFound, null, null);
        var student = await db.Students.FirstOrDefaultAsync(s => s.RegNo == pass.RegNo);
        if (pass.Revoked) return new(VerifyOutcome.Revoked, pass, student);
        if (pass.ExpiresAt <= clock.Now) return new(VerifyOutcome.Expired, pass, student);
        if (student is null || !student.IsCleared) return new(VerifyOutcome.NotCleared, pass, student);
        return new(VerifyOutcome.Valid, pass, student);
    }

    private async Task<Gatepass?> ActivePassAsync(string regNo)
    {
        var now = clock.Now;
        return await db.Gatepasses.Where(p => p.RegNo == regNo && !p.Revoked && p.ExpiresAt > now).OrderByDescending(p => p.Id).FirstOrDefaultAsync();
    }
}

public record ImportResult(int Rows, int Added, int Updated, int NowCleared, int PassesRevoked, IReadOnlyList<string> Errors);

/// <summary>
/// Loads Finance's balances: RegNo,Name,Faculty,Phone,TotalFee,Paid. Bad rows are reported by line and skipped.
/// Students who become cleared are told their gatepass is ready; students who now owe lose their pass at the gate.
/// </summary>
public class FeeImportService(IAppDb db, IClock clock, NotificationService notify)
{
    public const string Header = "RegNo,Name,Faculty,Phone,TotalFee,Paid";
    public const int MaxRows = 100_000;

    public async Task<ImportResult> ImportAsync(TextReader csv)
    {
        var errors = new List<string>();
        void Err(string m) { if (errors.Count < 30) errors.Add(m); }

        var head = await csv.ReadLineAsync();
        if (head is null || !Split(head.TrimStart('﻿')).Select(h => h.Trim()).SequenceEqual(Header.Split(','), StringComparer.OrdinalIgnoreCase))
            return new(0, 0, 0, 0, 0, new[] { $"The first line must be exactly: {Header}" });

        var existing = await db.Students.ToDictionaryAsync(s => s.RegNo);
        var users = await db.Users.Where(u => u.Role == Role.Student).ToDictionaryAsync(u => u.Username);
        var now = clock.Now;
        int rows = 0, added = 0, updated = 0, cleared = 0, line = 1;
        var nowOwing = new List<string>();

        while (await csv.ReadLineAsync() is string raw)
        {
            line++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (++rows > MaxRows) { Err($"Stopped after {MaxRows:N0} rows."); break; }
            var f = Split(raw);
            if (f.Count != 6) { Err($"Line {line}: expected 6 columns, found {f.Count}."); continue; }
            var reg = AccountService.Normalize(f[0]);
            if (reg.Length is < 3 or > 30) { Err($"Line {line}: registration number is missing or too long."); continue; }
            if (string.IsNullOrWhiteSpace(f[1])) { Err($"Line {line}: name is missing."); continue; }
            if (!decimal.TryParse(f[4], NumberStyles.Number, CultureInfo.InvariantCulture, out var total) || total < 0 ||
                !decimal.TryParse(f[5], NumberStyles.Number, CultureInfo.InvariantCulture, out var paid) || paid < 0)
            { Err($"Line {line}: TotalFee and Paid must be numbers."); continue; }

            if (existing.TryGetValue(reg, out var s))
            {
                var was = s.IsCleared;
                (s.Name, s.Faculty, s.Phone, s.TotalFee, s.Paid, s.BalanceAsOf) = (f[1].Trim(), f[2].Trim(), f[3].Trim(), total, paid, now);
                updated++;
                if (!was && s.IsCleared) { cleared++; Tell(reg, "You're cleared! 🎉", "Your fees are cleared. Your gatepass is ready in CampusPass."); }
                if (was && !s.IsCleared) nowOwing.Add(reg);
            }
            else
            {
                s = new StudentRecord { RegNo = reg, Name = f[1].Trim(), Faculty = f[2].Trim(), Phone = f[3].Trim(), TotalFee = total, Paid = paid, BalanceAsOf = now };
                db.Students.Add(s);
                existing[reg] = s;
                added++;
            }
        }

        var revoked = 0;
        if (nowOwing.Count > 0)
        {
            var passes = await db.Gatepasses.Where(p => nowOwing.Contains(p.RegNo) && !p.Revoked).ToListAsync();
            foreach (var p in passes) { p.Revoked = true; p.RevokedReason = "Balance due after fee update"; revoked++; }
            foreach (var reg in nowOwing) Tell(reg, "Gatepass paused", "Finance's latest figures show a balance. Open CampusPass to see what to pay.");
        }
        await db.SaveChangesAsync();
        return new(rows, added, updated, cleared, revoked, errors);

        void Tell(string reg, string title, string body)
        {
            if (users.TryGetValue(reg, out var u)) notify.Add(u.Id, title, body, "/Student");
        }
    }

    /// <summary>Minimal RFC 4180 field splitter (quotes and escaped quotes).</summary>
    public static List<string> Split(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
