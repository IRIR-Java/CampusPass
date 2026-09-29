namespace CampusPass.Domain.Entities;

/// <summary>Lifecycle of a ticket: Waiting -> Called -> InProgress -> Resolved.</summary>
public enum TicketStatus
{
    Waiting,
    Called,
    InProgress,
    Resolved,
    NoShow
}

public enum Urgency
{
    Normal,
    Urgent
}
