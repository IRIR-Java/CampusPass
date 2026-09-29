using CampusPass.Application;
using CampusPass.Domain;

namespace CampusPass.Tests;

public class AccountTests : IDisposable
{
    private readonly TestWorld w = new();
    public void Dispose() => w.Dispose();

    [Fact]
    public async Task IssuedPassword_WorksOnce_AndMustBeChanged()
    {
        await w.StudentAsync("CS/1");
        var temp = await w.Accounts.IssueStudentLoginAsync("cs/1");
        var r = await w.Accounts.LoginAsync("cs/1", temp);
        Assert.Equal(LoginOutcome.Ok, r.Outcome);
        Assert.True(r.User!.MustChangePassword);

        await Assert.ThrowsAsync<RuleException>(() => w.Accounts.ChangePasswordAsync(r.User.Id, temp, "short"));
        await Assert.ThrowsAsync<RuleException>(() => w.Accounts.ChangePasswordAsync(r.User.Id, temp, temp));
        await w.Accounts.ChangePasswordAsync(r.User.Id, temp, "my-own-password");
        Assert.False(r.User.MustChangePassword);
        Assert.Equal(LoginOutcome.Ok, (await w.Accounts.LoginAsync("CS/1", "my-own-password")).Outcome);
    }

    [Fact]
    public async Task FiveWrongPasswords_LockTheAccount()
    {
        await w.StudentAsync("CS/1");
        var temp = await w.Accounts.IssueStudentLoginAsync("CS/1");
        for (var i = 0; i < 4; i++) Assert.Equal(LoginOutcome.Wrong, (await w.Accounts.LoginAsync("CS/1", "nope")).Outcome);
        Assert.Equal(LoginOutcome.Locked, (await w.Accounts.LoginAsync("CS/1", "nope")).Outcome);
        Assert.Equal(LoginOutcome.Locked, (await w.Accounts.LoginAsync("CS/1", temp)).Outcome);

        w.Clock.Now = w.Clock.Now.AddMinutes(6);
        Assert.Equal(LoginOutcome.Ok, (await w.Accounts.LoginAsync("CS/1", temp)).Outcome);
    }

    [Fact]
    public async Task UnknownUser_AndDisabledUser_LookTheSameAsAWrongPassword()
    {
        Assert.Equal(LoginOutcome.Wrong, (await w.Accounts.LoginAsync("NOBODY", "x")).Outcome);
        var staff = await w.StaffAsync("ict1", "ICT");
        var admin = w.Db.Users.Single(u => u.Role == Role.Admin);
        await w.Accounts.SetDisabledAsync(staff.Id, true, admin.Id);
        Assert.Equal(LoginOutcome.Wrong, (await w.Accounts.LoginAsync("ict1", "anything")).Outcome);
        await Assert.ThrowsAsync<RuleException>(() => w.Accounts.SetDisabledAsync(admin.Id, true, admin.Id));
    }

    [Fact]
    public async Task StudentLogin_NeedsAFeeRecord_AndStaffNeedADepartment()
    {
        await Assert.ThrowsAsync<RuleException>(() => w.Accounts.IssueStudentLoginAsync("XX/404"));
        await Assert.ThrowsAsync<RuleException>(() => w.Accounts.CreateStaffAsync("ict9", "Nobody", Role.Staff, null, null));
        await w.StaffAsync("ict1", "ICT");
        await Assert.ThrowsAsync<RuleException>(() => w.StaffAsync("ICT1", "ICT"));   // usernames are case-insensitive
    }

    [Fact]
    public async Task Usernames_IgnoreSpacesAndCase()
    {
        await w.Accounts.CreateStaffAsync("STAFF 1", "Peter Mutai", Role.Staff, w.Dept("ICT").Id, null);
        await w.Accounts.SetPasswordAsync("staff1", "staff123", mustChange: false);
        var r = await w.Accounts.LoginAsync(" Staff 1 ", "staff123");
        Assert.Equal(LoginOutcome.Ok, r.Outcome);
        Assert.Equal("STAFF1", r.User!.Username);
        Assert.False(r.User.MustChangePassword);
    }

    [Fact]
    public async Task AdminSetPassword_StillEnforcesMinimumRules()
    {
        await w.StudentAsync("EB1/0001/23");
        await Assert.ThrowsAsync<RuleException>(() => w.Accounts.SetPasswordAsync("EB1/0001/23", "short", false));
        await Assert.ThrowsAsync<RuleException>(() => w.Accounts.SetPasswordAsync("NOBODY", "student123", false));
        await w.Accounts.SetPasswordAsync("eb1/0001/23", "student123", mustChange: true);
        Assert.True((await w.Accounts.LoginAsync("EB1/0001/23", "student123")).User!.MustChangePassword);
    }

    [Fact]
    public void TemporaryPasswords_AreRandomAndReadable()
    {
        var a = PasswordHasher.NewTemporaryPassword();
        Assert.Matches("^[A-Za-z2-9]{5}-[A-Za-z2-9]{5}$", a);
        Assert.NotEqual(a, PasswordHasher.NewTemporaryPassword());
        Assert.DoesNotContain('0', a); Assert.DoesNotContain('O', a); Assert.DoesNotContain('l', a);
    }

    [Fact]
    public async Task SetupRunsOnlyOnce_AndCreatesTheServiceCatalogue()
    {
        Assert.Equal(6, w.Db.Departments.Count());
        await Assert.ThrowsAsync<RuleException>(() => new SetupService(w.Db, w.Clock).CompleteAsync("X", "S", new DateOnly(2027, 1, 1), "admin2", "A", "another-long-password"));
    }
}

public class ClearanceTests : IDisposable
{
    private readonly TestWorld w = new();
    public void Dispose() => w.Dispose();

    [Fact]
    public async Task ClearedStudent_GetsOnePass_ThatVerifies()
    {
        await w.StudentAsync("CS/1");
        var pass = await w.Clearance.EnsurePassAsync("cs/1");
        Assert.NotNull(pass);
        Assert.Equal(pass!.Id, (await w.Clearance.EnsurePassAsync("CS/1"))!.Id);   // idempotent
        var r = await w.Clearance.VerifyAsync(await w.Clearance.TokenAsync(pass));
        Assert.Equal(VerifyOutcome.Valid, r.Outcome);
        Assert.Equal("CS/1", r.Student!.RegNo);
    }

    [Fact]
    public async Task OwingStudent_GetsNoPass()
    {
        await w.StudentAsync("CS/2", total: 60000, paid: 30000);
        Assert.Null(await w.Clearance.EnsurePassAsync("CS/2"));
        Assert.False((await w.Clearance.GetAsync("CS/2"))!.Cleared);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("abc.def")]
    public async Task ForgedTokens_AreRejected(string token) =>
        Assert.Equal(VerifyOutcome.Invalid, (await w.Clearance.VerifyAsync(token)).Outcome);

    [Fact]
    public async Task EditedToken_IsRejected()
    {
        await w.StudentAsync("CS/1");
        var token = await w.Clearance.TokenAsync((await w.Clearance.EnsurePassAsync("CS/1"))!);
        var edited = (token[0] == 'A' ? "B" : "A") + token[1..];
        Assert.Equal(VerifyOutcome.Invalid, (await w.Clearance.VerifyAsync(edited)).Outcome);
    }

    [Fact]
    public async Task FeeUpload_RevokesPassesOfStudentsWhoNowOwe_AndTellsNewlyClearedStudents()
    {
        var joy = await w.StudentAsync("ED/1");
        var brian = await w.StudentAsync("CS/2", total: 60000, paid: 30000);
        var token = await w.Clearance.TokenAsync((await w.Clearance.EnsurePassAsync("ED/1"))!);

        var csv = FeeImportService.Header + "\nED/1,Joy,Education,0700,60000,45000\nCS/2,Brian,Science,0700,60000,60000\nNEW/9,New Student,Business,0700,100,0\nbad,row\n";
        var r = await w.Fees.ImportAsync(new StringReader(csv));

        Assert.Equal((4, 1, 2, 1, 1), (r.Rows, r.Added, r.Updated, r.NowCleared, r.PassesRevoked));
        Assert.Single(r.Errors);
        Assert.Equal(VerifyOutcome.Revoked, (await w.Clearance.VerifyAsync(token)).Outcome);
        Assert.Contains(w.Db.Notifications, n => n.UserId == brian.Id && n.Title.StartsWith("You're cleared"));
        Assert.Contains(w.Db.Notifications, n => n.UserId == joy.Id && n.Title == "Gatepass paused");
    }

    [Fact]
    public async Task FeeUpload_RejectsTheWrongHeader()
    {
        var r = await w.Fees.ImportAsync(new StringReader("reg,name\nx,y"));
        Assert.Equal(0, r.Rows);
        Assert.Single(r.Errors);
    }

    [Fact]
    public void CsvSplitter_HandlesQuotes() =>
        Assert.Equal(new[] { "A", "Smith, John", "say \"hi\"" }, FeeImportService.Split("A,\"Smith, John\",\"say \"\"hi\"\"\""));
}
