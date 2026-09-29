using CampusPass.Application;
using CampusPass.Domain;

namespace CampusPass.Tests;

public class QueueTests : IDisposable
{
    private readonly TestWorld w = new();
    public void Dispose() => w.Dispose();

    [Fact]
    public async Task JoinQueue_BooksTheCurrentWindow_AndNumbersByDepartment()
    {
        var s = await w.StudentAsync("CS/1");
        var (t, existing) = await w.Queue.RequestAsync(s, w.Dept("ICT").Id, TicketMode.Queue, null, "Can't log in");
        Assert.False(existing);
        Assert.Equal("ICT-001", t.Number);
        Assert.Equal(new DateTime(2026, 10, 6, 9, 0, 0), t.WindowStart);   // 09:00–10:00 is still open at 09:10
        Assert.Equal(new DateTime(2026, 10, 6, 10, 0, 0), t.WindowEnd);
    }

    [Fact]
    public async Task TappingAgain_ReturnsTheSameTicket()
    {
        var s = await w.StudentAsync("CS/1");
        var first = (await w.Queue.RequestAsync(s, w.Dept("FIN").Id, TicketMode.Queue, null, null)).Ticket;
        var (again, existing) = await w.Queue.RequestAsync(s, w.Dept("FIN").Id, TicketMode.Queue, null, null);
        Assert.True(existing);
        Assert.Equal(first.Id, again.Id);
    }

    [Fact]
    public async Task FullWindow_SpillsIntoTheNextBatch()
    {
        // No staff on duty -> treated as 1; Finance 5 min per student, 60-min windows -> 12 per window.
        var fin = w.Dept("FIN");
        for (var i = 1; i <= 12; i++)
        {
            var s = await w.StudentAsync($"CS/{i}");
            Assert.Equal(9, (await w.Queue.RequestAsync(s, fin.Id, TicketMode.Queue, null, null)).Ticket.WindowStart.Hour);
        }
        var late = await w.StudentAsync("CS/13");
        Assert.Equal(10, (await w.Queue.RequestAsync(late, fin.Id, TicketMode.Queue, null, null)).Ticket.WindowStart.Hour);
    }

    [Fact]
    public async Task MoreStaffOnDuty_MeansBiggerBatches()
    {
        var fin = w.Dept("FIN");
        Assert.Equal(12, (await w.Queue.SlotsAsync(fin, new DateOnly(2026, 10, 6)))[0].Capacity);
        await w.StaffAsync("fin1", "FIN");
        await w.StaffAsync("fin2", "FIN");
        Assert.Equal(24, (await w.Queue.SlotsAsync(fin, new DateOnly(2026, 10, 6)))[0].Capacity);
    }

    [Fact]
    public async Task Windows_SkipLunch_AndPastTimes()
    {
        var slots = await w.Queue.SlotsAsync(w.Dept("ICT"), new DateOnly(2026, 10, 7));   // tomorrow: full day
        Assert.Equal(new[] { 8, 9, 10, 11, 12, 14, 15, 16 }, slots.Select(s => s.Start.Hour));
        Assert.Equal(6, slots.Single(s => s.Start.Hour == 14).Batch);
        Assert.DoesNotContain(await w.Queue.SlotsAsync(w.Dept("ICT"), new DateOnly(2026, 10, 6)), s => s.Start.Hour == 8);
        Assert.Empty(await w.Queue.SlotsAsync(w.Dept("ICT"), new DateOnly(2026, 10, 10)));   // Saturday
    }

    [Fact]
    public async Task FacultyDay_QueueTicketsStartOnThatDay()
    {
        w.Db.FacultyDays.Add(new FacultyDay { Faculty = "Science", Date = new DateOnly(2026, 10, 9) });
        await w.Db.SaveChangesAsync();
        var s = await w.StudentAsync("SC/1", faculty: "Science");
        var t = (await w.Queue.RequestAsync(s, w.Dept("ACA").Id, TicketMode.Queue, null, null)).Ticket;
        Assert.Equal(new DateTime(2026, 10, 9, 8, 0, 0), t.WindowStart);
    }

    [Fact]
    public async Task Appointment_BooksTheChosenWindow_AndRejectsPastOnes()
    {
        var s = await w.StudentAsync("CS/1");
        var at = new DateTime(2026, 10, 7, 14, 0, 0);
        var t = (await w.Queue.RequestAsync(s, w.Dept("ADM").Id, TicketMode.Appointment, at, null)).Ticket;
        Assert.Equal(at, t.WindowStart);
        Assert.Equal(TicketMode.Appointment, t.Mode);

        var s2 = await w.StudentAsync("CS/2");
        await Assert.ThrowsAsync<RuleException>(() => w.Queue.RequestAsync(s2, w.Dept("ADM").Id, TicketMode.Appointment, new DateTime(2026, 10, 6, 8, 0, 0), null));
    }

    [Fact]
    public async Task CallNext_IsFirstComeFirstServed_AndOneStudentPerOfficer()
    {
        var a = await w.StudentAsync("CS/1");
        var b = await w.StudentAsync("CS/2");
        var ta = (await w.Queue.RequestAsync(a, w.Dept("ICT").Id, TicketMode.Queue, null, null)).Ticket;
        await w.Queue.RequestAsync(b, w.Dept("ICT").Id, TicketMode.Queue, null, null);
        var staff = await w.StaffAsync("ict1", "ICT", "Counter 2");

        var called = await w.Queue.CallNextAsync(staff);
        Assert.Equal(ta.Id, called!.Id);
        Assert.Equal(TicketStatus.Called, called.Status);
        Assert.Equal("Counter 2", called.Counter);
        await Assert.ThrowsAsync<RuleException>(() => w.Queue.CallNextAsync(staff));
    }

    [Fact]
    public async Task TwoOfficers_NeverGetTheSameStudent()
    {
        var a = await w.StudentAsync("CS/1");
        await w.Queue.RequestAsync(a, w.Dept("FIN").Id, TicketMode.Queue, null, null);
        var s1 = await w.StaffAsync("fin1", "FIN");
        var s2 = await w.StaffAsync("fin2", "FIN");
        var results = await Task.WhenAll(w.Queue.CallNextAsync(s1), w.Queue.CallNextAsync(s2));
        Assert.Single(results, r => r is not null);
    }

    [Fact]
    public async Task FutureDayTickets_CannotBeCalledToday()
    {
        var s = await w.StudentAsync("CS/1");
        await w.Queue.RequestAsync(s, w.Dept("ICT").Id, TicketMode.Appointment, new DateTime(2026, 10, 8, 9, 0, 0), null);
        Assert.Null(await w.Queue.CallNextAsync(await w.StaffAsync("ict1", "ICT")));
    }

    [Fact]
    public async Task FullServiceFlow_NotifiesTheStudentAtEachStep()
    {
        var s = await w.StudentAsync("CS/1");
        var t = (await w.Queue.RequestAsync(s, w.Dept("ICT").Id, TicketMode.Queue, null, null)).Ticket;
        var staff = await w.StaffAsync("ict1", "ICT");

        await w.Queue.CallNextAsync(staff);
        await w.Queue.StartAsync(t.Id, staff);
        Assert.Equal(TicketStatus.Serving, t.Status);
        await w.Queue.CompleteAsync(t.Id, staff, null);
        Assert.Equal(TicketStatus.Completed, t.Status);
        Assert.Equal("Served by ICT & Portal.", t.Resolution);

        var titles = (await w.Notify.ListAsync(s.Id)).Select(n => n.Title).ToList();
        Assert.Contains("Ticket ICT-001 created", titles);
        Assert.Contains("It's your turn!", titles);
        Assert.Contains("ICT-001 completed", titles);
    }

    [Fact]
    public async Task NextStudentsInLine_AreToldTheyAreAlmostUp_Once()
    {
        var staff = await w.StaffAsync("ict1", "ICT");
        var users = new List<UserAccount>();
        for (var i = 1; i <= 5; i++)
        {
            var u = await w.StudentAsync($"CS/{i}");
            users.Add(u);
            await w.Queue.RequestAsync(u, w.Dept("ICT").Id, TicketMode.Queue, null, null);
        }
        await w.Queue.CallNextAsync(staff);
        int Almost(UserAccount u) => w.Db.Notifications.Count(n => n.UserId == u.Id && n.Kind == "almost");
        Assert.Equal(new[] { 0, 1, 1, 1, 0 }, users.Select(Almost));
    }

    [Fact]
    public async Task Position_Ahead_AndEta_AreShownToTheStudent()
    {
        await w.StaffAsync("fin1", "FIN");
        for (var i = 1; i <= 3; i++) await w.Queue.RequestAsync(await w.StudentAsync($"CS/{i}"), w.Dept("FIN").Id, TicketMode.Queue, null, null);
        var me = await w.StudentAsync("CS/9");
        var t = (await w.Queue.RequestAsync(me, w.Dept("FIN").Id, TicketMode.Queue, null, null)).Ticket;

        var v = (await w.Queue.ViewAsync(t.Id, me.Id))!;
        Assert.Equal(4, v.Position);
        Assert.Equal(4, v.WindowTotal);
        Assert.Equal(3, v.Ahead);
        Assert.Equal(15, v.EtaMinutes);            // 3 ahead x 5 min / 1 officer
        Assert.Equal(2, v.Batch);                  // 08:00 is batch 1, 09:00 is batch 2
        Assert.False(v.Almost);
        Assert.Null(await w.Queue.ViewAsync(t.Id, 999));   // other students can't see it
    }

    [Fact]
    public async Task Transfer_KeepsTheNumber_AndMovesToTheRightDesk()
    {
        var s = await w.StudentAsync("CS/1");
        var t = (await w.Queue.RequestAsync(s, w.Dept("ICT").Id, TicketMode.Queue, null, null)).Ticket;
        var ict = await w.StaffAsync("ict1", "ICT");
        await w.Queue.CallNextAsync(ict);
        await w.Queue.TransferAsync(t.Id, ict, w.Dept("FIN").Id);

        Assert.Equal("ICT-001", t.Number);
        Assert.Equal(w.Dept("FIN").Id, t.DepartmentId);
        Assert.Equal(TicketStatus.Waiting, t.Status);
        Assert.Equal("ICT & Portal", t.TransferredFrom);
        Assert.Equal(t.Id, (await w.Queue.CallNextAsync(await w.StaffAsync("fin1", "FIN")))!.Id);
    }

    [Fact]
    public async Task NumbersAreNeverReused_AfterATransferOut()
    {
        var ict = await w.StaffAsync("ict1", "ICT");
        var t1 = (await w.Queue.RequestAsync(await w.StudentAsync("CS/1"), w.Dept("ICT").Id, TicketMode.Queue, null, null)).Ticket;
        await w.Queue.RequestAsync(await w.StudentAsync("CS/2"), w.Dept("ICT").Id, TicketMode.Queue, null, null);
        await w.Queue.CallNextAsync(ict);
        await w.Queue.TransferAsync(t1.Id, ict, w.Dept("FIN").Id);
        var t3 = (await w.Queue.RequestAsync(await w.StudentAsync("CS/3"), w.Dept("ICT").Id, TicketMode.Queue, null, null)).Ticket;
        Assert.Equal("ICT-003", t3.Number);
    }

    [Fact]
    public async Task Student_CanCancel_AndSayOnMyWay()
    {
        var s = await w.StudentAsync("CS/1");
        var t = (await w.Queue.RequestAsync(s, w.Dept("GP").Id, TicketMode.Queue, null, null)).Ticket;
        await w.Queue.OnMyWayAsync(t.Id, s.Id);
        Assert.True(t.OnTheWay);
        await w.Queue.CancelAsync(t.Id, s.Id);
        Assert.Equal(TicketStatus.Cancelled, t.Status);
        await Assert.ThrowsAsync<RuleException>(() => w.Queue.CancelAsync(t.Id, s.Id));
    }

    [Fact]
    public async Task OnlyTheCallingOfficer_CanFinishTheTicket()
    {
        var s = await w.StudentAsync("CS/1");
        var t = (await w.Queue.RequestAsync(s, w.Dept("ICT").Id, TicketMode.Queue, null, null)).Ticket;
        var a = await w.StaffAsync("ict1", "ICT");
        var b = await w.StaffAsync("ict2", "ICT");
        await w.Queue.CallNextAsync(a);
        await Assert.ThrowsAsync<RuleException>(() => w.Queue.CompleteAsync(t.Id, b, "x"));
    }

    [Fact]
    public async Task Dashboards_CountTodaysWork()
    {
        var staff = await w.StaffAsync("ict1", "ICT");
        var s = await w.StudentAsync("CS/1");
        var t = (await w.Queue.RequestAsync(s, w.Dept("ICT").Id, TicketMode.Queue, null, null)).Ticket;
        await w.Queue.CallNextAsync(staff);
        await w.Queue.CompleteAsync(t.Id, staff, "Fixed");

        var d = await w.Reports.StaffAsync(staff);
        Assert.Equal(1, d.ServedToday);
        Assert.Equal(100, d.OnTrackPercent);
        var a = await w.Reports.AdminAsync();
        Assert.Equal(1, a.Students);
        Assert.Equal(1, a.RequestsToday.Single(x => x.Department.Code == "ICT").Count);
    }
}
