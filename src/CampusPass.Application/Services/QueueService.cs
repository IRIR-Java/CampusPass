using CampusPass.Application.Abstractions;
using CampusPass.Domain.Entities;

namespace CampusPass.Application.Services;

public record SubmitTicketRequest(
    string StudentName,
    string RegNo,
    string Phone,
    string Title,
    string Description,
    Urgency Urgency = Urgency.Normal,
    int? SectorId = null);   // null = "I'm not sure" -> auto-route

public record SubmitTicketResult(Ticket Ticket, Sector Sector, Batch Batch, RoutingResult Routing);

public record SectorQueue(
    Sector Sector,
    IReadOnlyList<Ticket> Ready,       // batch day is today (or earlier): can be called now
    IReadOnlyList<Ticket> Scheduled,   // booked for a future service day
    IReadOnlyList<Ticket> Active);     // currently Called / InProgress

public record TrackResult(Ticket Ticket, Sector Sector, Batch? Batch, int TicketsAhead);

/// <summary>
/// The "bank teller" engine: submit -> route -> batch -> call next -> handle -> resolve / re-route.
/// All state changes go through here so the rules live in ONE place.
/// </summary>
public class QueueService(
    ITicketRepository tickets,
    ISectorRepository sectors,
    IOfficerRepository officers,
    IBatchRepository batches,
    RoutingService routing,
    BatchingService batching,
    IClock clock)
{
    // One lock = two officers can never "Call Next" the same ticket.
    // With a real DB replace this with a transaction / row lock / optimistic concurrency.
    private readonly object _gate = new();

    // ---------- Student ----------

    public SubmitTicketResult Submit(SubmitTicketRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Description))
            throw new ArgumentException("Title and description are required.");

        lock (_gate)
        {
            var suggestion = routing.Route(req.Title, req.Description);
            var sector = req.SectorId is int id && sectors.Get(id) is Sector chosen ? chosen : suggestion.Sector;
            var batch = batching.Assign(sector, req.Urgency);
            var now = clock.Now;

            var ticket = new Ticket
            {
                Number = $"{sector.Code}-{tickets.NextSequence(sector.Id):000}",
                StudentName = req.StudentName.Trim(),
                RegNo = req.RegNo.Trim().ToUpperInvariant(),
                Phone = req.Phone.Trim(),
                Title = req.Title.Trim(),
                Description = req.Description.Trim(),
                Urgency = req.Urgency,
                SectorId = sector.Id,
                BatchId = batch.Id,
                CreatedAt = now
            };

            ticket.History.Add(new TicketEvent { At = now, Message = req.SectorId.HasValue
                ? $"Submitted to {sector.Name} (chosen by student)."
                : $"Auto-routed to {sector.Name} (confidence {suggestion.Confidence:P0})." });
            ticket.History.Add(new TicketEvent { At = now, Message = $"Booked into batch {batch.Date:ddd dd MMM}." });

            tickets.Add(ticket);
            return new SubmitTicketResult(ticket, sector, batch, suggestion);
        }
    }

    public TrackResult? Track(string number)
    {
        var t = tickets.GetByNumber(number.Trim());
        if (t is null) return null;
        var sector = sectors.Get(t.SectorId)!;
        var batch = t.BatchId is int b ? batches.All().FirstOrDefault(x => x.Id == b) : null;
        var ahead = t.Status == TicketStatus.Waiting
            ? tickets.All().Count(o => o.SectorId == t.SectorId && o.Status == TicketStatus.Waiting && o.Id != t.Id && Order(o).CompareTo(Order(t)) < 0)
            : 0;
        return new TrackResult(t, sector, batch, ahead);
    }

    // ---------- Officer ----------

    public Ticket? GetTicket(int id) => tickets.Get(id);

    public SectorQueue GetSectorQueue(int sectorId)
    {
        var sector = sectors.Get(sectorId) ?? throw new KeyNotFoundException("Unknown sector.");
        var today = DateOnly.FromDateTime(clock.Now);
        var mine = tickets.All().Where(t => t.SectorId == sectorId).ToList();
        var waiting = mine.Where(t => t.Status == TicketStatus.Waiting).OrderBy(Order).ToList();

        return new SectorQueue(
            sector,
            waiting.Where(t => BatchDate(t) <= today).ToList(),
            waiting.Where(t => BatchDate(t) > today).ToList(),
            mine.Where(t => t.Status is TicketStatus.Called or TicketStatus.InProgress).OrderBy(t => t.CalledAt).ToList());
    }

    /// <summary>Bank-teller "Next customer please". Returns null when nobody is ready.</summary>
    public Ticket? CallNext(int officerId)
    {
        lock (_gate)
        {
            var officer = officers.Get(officerId) ?? throw new KeyNotFoundException("Unknown officer.");

            var current = tickets.All().FirstOrDefault(t =>
                t.OfficerId == officerId && t.Status is TicketStatus.Called or TicketStatus.InProgress);
            if (current is not null)
                throw new InvalidOperationException($"Finish {current.Number} before calling the next ticket.");

            var next = GetSectorQueue(officer.SectorId).Ready.FirstOrDefault();
            if (next is null) return null;

            var now = clock.Now;
            next.Status = TicketStatus.Called;
            next.OfficerId = officerId;
            next.CalledAt = now;
            next.History.Add(new TicketEvent { At = now, Message = $"Called by {officer.Name}." });
            tickets.Update(next);
            return next;
        }
    }

    public Ticket Start(int ticketId, int officerId) => Transition(ticketId, officerId, t =>
    {
        Require(t, TicketStatus.Called);
        t.Status = TicketStatus.InProgress;
        return "Work started.";
    });

    public Ticket Resolve(int ticketId, int officerId, string notes) => Transition(ticketId, officerId, t =>
    {
        if (t.Status is not (TicketStatus.Called or TicketStatus.InProgress))
            throw new InvalidOperationException("Only an active ticket can be resolved.");
        if (string.IsNullOrWhiteSpace(notes))
            throw new ArgumentException("Add a short resolution note for the student.");
        t.Status = TicketStatus.Resolved;
        t.ResolvedAt = clock.Now;
        t.ResolutionNotes = notes.Trim();
        return "Resolved.";
    });

    /// <summary>Student did not respond / show up after being called.</summary>
    public Ticket MarkNoShow(int ticketId, int officerId) => Transition(ticketId, officerId, t =>
    {
        Require(t, TicketStatus.Called);
        t.Status = TicketStatus.NoShow;
        return "Marked as no-show.";
    });

    /// <summary>Wrong department? Send it on WITHOUT losing the student's place in line.</summary>
    public Ticket Reroute(int ticketId, int officerId, int newSectorId, string reason)
    {
        lock (_gate)
        {
            var t = tickets.Get(ticketId) ?? throw new KeyNotFoundException("Unknown ticket.");
            var newSector = sectors.Get(newSectorId) ?? throw new KeyNotFoundException("Unknown sector.");
            var officer = officers.Get(officerId) ?? throw new KeyNotFoundException("Unknown officer.");

            if (t.Status is TicketStatus.Resolved or TicketStatus.NoShow)
                throw new InvalidOperationException("Closed tickets cannot be re-routed.");
            if (t.SectorId == newSectorId)
                throw new InvalidOperationException("Ticket is already in that sector.");
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Say why it is being re-routed.");

            var old = sectors.Get(t.SectorId)!;
            // The student has already waited: treat it as urgent for batch placement, keep original CreatedAt.
            var batch = batching.Assign(newSector, Urgency.Urgent);

            t.SectorId = newSectorId;
            t.BatchId = batch.Id;
            t.Status = TicketStatus.Waiting;
            t.OfficerId = null;
            t.CalledAt = null;
            t.History.Add(new TicketEvent
            {
                At = clock.Now,
                Message = $"Re-routed {old.Name} -> {newSector.Name} by {officer.Name}: {reason.Trim()} (now {batch.Date:ddd dd MMM})."
            });
            tickets.Update(t);
            return t;
        }
    }

    // ---------- helpers ----------

    private Ticket Transition(int ticketId, int officerId, Func<Ticket, string> apply)
    {
        lock (_gate)
        {
            var t = tickets.Get(ticketId) ?? throw new KeyNotFoundException("Unknown ticket.");
            var officer = officers.Get(officerId) ?? throw new KeyNotFoundException("Unknown officer.");
            if (t.OfficerId != officerId)
                throw new InvalidOperationException("This ticket is being handled by another officer.");

            var message = apply(t);
            t.History.Add(new TicketEvent { At = clock.Now, Message = $"{message} ({officer.Name})" });
            tickets.Update(t);
            return t;
        }
    }

    private static void Require(Ticket t, TicketStatus expected)
    {
        if (t.Status != expected)
            throw new InvalidOperationException($"Ticket must be {expected} (currently {t.Status}).");
    }

    private DateOnly BatchDate(Ticket t) =>
        t.BatchId is int id ? batches.All().First(b => b.Id == id).Date : DateOnly.FromDateTime(t.CreatedAt);

    /// <summary>Queue ordering: urgent first, then earliest batch day, then first-come-first-served (Id breaks exact ties).</summary>
    private (int, DateOnly, DateTime, int) Order(Ticket t) =>
        (t.Urgency == Urgency.Urgent ? 0 : 1, BatchDate(t), t.CreatedAt, t.Id);
}
