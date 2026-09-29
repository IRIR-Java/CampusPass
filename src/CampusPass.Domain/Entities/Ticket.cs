namespace CampusPass.Domain.Entities;

public class Ticket
{
    public int Id { get; set; }

    /// <summary>Human-friendly number shown to the student, e.g. ICT-023.</summary>
    public string Number { get; set; } = "";

    public string StudentName { get; set; } = "";
    public string RegNo { get; set; } = "";
    public string Phone { get; set; } = "";

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public Urgency Urgency { get; set; } = Urgency.Normal;

    public int SectorId { get; set; }
    public int? BatchId { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.Waiting;

    public int? OfficerId { get; set; }
    public string? ResolutionNotes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? CalledAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Audit trail: routing, re-routing, status changes.</summary>
    public List<TicketEvent> History { get; set; } = new();
}

public class TicketEvent
{
    public DateTime At { get; set; }
    public string Message { get; set; } = "";
}
