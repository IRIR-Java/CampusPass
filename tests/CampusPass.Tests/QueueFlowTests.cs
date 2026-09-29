using CampusPass.Application.Abstractions;
using CampusPass.Application.Services;
using CampusPass.Domain.Entities;
using CampusPass.Infrastructure.Persistence;
using Xunit;

namespace CampusPass.Tests;

/// <summary>Sector ids in seed data: 1 ICT, 2 Finance, 3 GatePass, 4 Registrar, 5 Help Desk.
/// Officer ids: 1-2 ICT, 3-4 Finance, 5-6 Gate, 7 Registrar, 8 Help Desk.</summary>
public class TestClock : IClock
{
    public DateTime Now { get; set; } = new(2026, 9, 29, 10, 0, 0); // Tuesday 10:00
}

public class World
{
    public TestClock Clock { get; } = new();
    public InMemoryTicketRepository Tickets { get; } = new();
    public RoutingService Routing { get; }
    public BatchingService Batching { get; }
    public QueueService Queue { get; }

    public World()
    {
        var sectors = new InMemorySectorRepository();
        var officers = new InMemoryOfficerRepository();
        var batches = new InMemoryBatchRepository();
        Routing = new RoutingService(sectors);
        Batching = new BatchingService(officers, batches, Tickets, Clock);
        Queue = new QueueService(Tickets, sectors, officers, batches, Routing, Batching, Clock);
    }

    public SubmitTicketResult Submit(string title, string desc, Urgency u = Urgency.Normal) =>
        Queue.Submit(new SubmitTicketRequest("Student", "cs/1", "0700", title, desc, u));
}

public class RoutingTests
{
    private readonly World w = new();

    [Fact] public void GatePassDownloadProblem_GoesToIct() =>   // the example from the project brief
        Assert.Equal("ICT", w.Routing.Route("Gate pass cannot be downloaded", "portal error").Sector.Code);

    [Fact] public void FeeProblem_GoesToFinance() =>
        Assert.Equal("FIN", w.Routing.Route("Fee balance", "paid via mpesa").Sector.Code);

    [Fact] public void VisitorWithVehicle_GoesToGatePass() =>
        Assert.Equal("GP", w.Routing.Route("Need visitor pass", "parent with vehicle").Sector.Code);

    [Fact] public void Unclassifiable_GoesToHelpDesk() =>
        Assert.Equal("HD", w.Routing.Route("hello", "random words").Sector.Code);

    [Fact] public void StudentChoice_OverridesAutoRouting()
    {
        var r = w.Queue.Submit(new SubmitTicketRequest("S", "c", "0", "Gate pass cannot be downloaded", "portal", Urgency.Normal, SectorId: 2));
        Assert.Equal(2, r.Ticket.SectorId);
    }
}

public class BatchingTests
{
    private readonly World w = new();

    [Fact] public void BeforeCutoff_BooksToday() =>
        Assert.Equal(new DateOnly(2026, 9, 29), w.Submit("Fees", "fee balance").Batch.Date);

    [Fact] public void AfterCutoff_BooksNextServiceDay()
    {
        w.Clock.Now = new DateTime(2026, 9, 29, 17, 0, 0);
        Assert.Equal(new DateOnly(2026, 9, 30), w.Submit("Fees", "fee").Batch.Date);
    }

    [Fact] public void FridayEvening_BooksMonday()
    {
        w.Clock.Now = new DateTime(2026, 10, 2, 17, 0, 0);
        Assert.Equal(new DateOnly(2026, 10, 5), w.Submit("Fees", "fee").Batch.Date);
    }

    [Fact] public void NormalTickets_SpillToNextDay_OnceEightyPercentIsBooked_ButUrgentStillGetsToday()
    {
        // Finance: 2 officers x 12/day = 24 capacity -> normal limit floor(24*0.8) = 19
        for (var i = 0; i < 19; i++)
            Assert.Equal(new DateOnly(2026, 9, 29), w.Submit("Fees", "fee balance").Batch.Date);

        Assert.Equal(new DateOnly(2026, 9, 30), w.Submit("Fees", "fee balance").Batch.Date);
        Assert.Equal(new DateOnly(2026, 9, 29), w.Submit("Fees", "fee balance", Urgency.Urgent).Batch.Date);
    }
}

public class QueueFlowTests
{
    private readonly World w = new();

    [Fact] public void TicketNumbers_UseSectorPrefix_AndIncrease()
    {
        Assert.Equal("ICT-001", w.Submit("Login", "password").Ticket.Number);
        Assert.Equal("ICT-002", w.Submit("Login", "password").Ticket.Number);
        Assert.Equal("FIN-001", w.Submit("Fees", "fee").Ticket.Number);
    }

    [Fact] public void CallNext_IsFirstComeFirstServed_AndNeverHandsOutSameTicketTwice()
    {
        var a = w.Submit("Login", "password").Ticket;
        var b = w.Submit("Login", "password").Ticket;

        Assert.Equal(a.Id, w.Queue.CallNext(1)!.Id);
        Assert.Equal(b.Id, w.Queue.CallNext(2)!.Id);
    }

    [Fact] public void Officer_CannotCallAnotherTicket_UntilCurrentIsFinished()
    {
        w.Submit("Login", "password"); w.Submit("Login", "password");
        w.Queue.CallNext(1);
        Assert.Throws<InvalidOperationException>(() => w.Queue.CallNext(1));
    }

    [Fact] public void EmptyQueue_ReturnsNull() => Assert.Null(w.Queue.CallNext(8));

    [Fact] public void Urgent_JumpsTheQueue()
    {
        w.Submit("Fees", "fee");
        var urgent = w.Submit("Fees", "fee", Urgency.Urgent).Ticket;
        Assert.Equal(urgent.Id, w.Queue.CallNext(3)!.Id);
    }

    [Fact] public void FullHappyPath_WaitingToResolved()
    {
        var t = w.Submit("Gate pass cannot be downloaded", "portal error").Ticket;
        Assert.Equal(TicketStatus.Waiting, t.Status);

        w.Queue.CallNext(1);
        Assert.Equal(TicketStatus.Called, t.Status);

        w.Queue.Start(t.Id, 1);
        Assert.Equal(TicketStatus.InProgress, t.Status);

        w.Queue.Resolve(t.Id, 1, "Cleared cache");
        Assert.Equal(TicketStatus.Resolved, t.Status);
        Assert.NotNull(t.ResolvedAt);
        Assert.Equal("Cleared cache", t.ResolutionNotes);
    }

    [Fact] public void Resolve_RequiresANote()
    {
        var t = w.Submit("Login", "password").Ticket;
        w.Queue.CallNext(1);
        Assert.Throws<ArgumentException>(() => w.Queue.Resolve(t.Id, 1, "  "));
    }

    [Fact] public void OnlyTheAssignedOfficer_CanChangeATicket()
    {
        var t = w.Submit("Login", "password").Ticket;
        w.Queue.CallNext(1);
        Assert.Throws<InvalidOperationException>(() => w.Queue.Resolve(t.Id, 2, "x"));
    }

    [Fact] public void Reroute_MovesSector_KeepsNumber_AndReturnsToWaiting()
    {
        var t = w.Submit("Login", "password").Ticket;
        w.Queue.CallNext(1);
        w.Queue.Reroute(t.Id, 1, newSectorId: 2, reason: "Actually a fees issue");

        Assert.Equal(2, t.SectorId);
        Assert.Equal("ICT-001", t.Number);
        Assert.Equal(TicketStatus.Waiting, t.Status);
        Assert.Equal(t.Id, w.Queue.CallNext(3)!.Id);   // a Finance officer can now call it
    }

    [Fact] public void Reroute_RequiresAReason()
    {
        var t = w.Submit("Login", "password").Ticket;
        w.Queue.CallNext(1);
        Assert.Throws<ArgumentException>(() => w.Queue.Reroute(t.Id, 1, 2, ""));
    }

    [Fact] public void TicketBookedForFutureDay_IsNotCallableUntilThatDay()
    {
        w.Clock.Now = new DateTime(2026, 9, 29, 17, 0, 0);   // after cutoff -> booked for tomorrow
        w.Submit("Fees", "fee");
        Assert.Null(w.Queue.CallNext(3));

        w.Clock.Now = new DateTime(2026, 9, 30, 9, 0, 0);
        Assert.NotNull(w.Queue.CallNext(3));
    }

    [Fact] public void Tracking_ShowsHowManyTicketsAreAhead()
    {
        w.Submit("Fees", "fee");
        var second = w.Submit("Fees", "fee").Ticket;
        Assert.Equal(1, w.Queue.Track(second.Number)!.TicketsAhead);
        Assert.Null(w.Queue.Track("NOPE-999"));
    }
}
