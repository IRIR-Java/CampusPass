namespace CampusPass.Domain.Entities;

/// <summary>A service department (ICT, Finance, GatePass/Security...) with its own queue.</summary>
public class Sector
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Ticket prefix, e.g. "ICT" -> ICT-001.</summary>
    public string Code { get; set; } = "";

    /// <summary>
    /// Phrases that hint a problem belongs here, with a weight (higher = stronger signal).
    /// Used by the routing engine. This is the "tuning knob" of the classifier.
    /// </summary>
    public Dictionary<string, int> Keywords { get; set; } = new();

    /// <summary>How many tickets ONE officer can realistically handle per service day.</summary>
    public int TicketsPerOfficerPerDay { get; set; } = 15;

    /// <summary>The fallback desk that receives tickets we cannot classify.</summary>
    public bool IsDefault { get; set; }
}
