namespace CampusPass.Domain.Entities;

/// <summary>A service day for one sector, with a capacity cap.</summary>
public class Batch
{
    public int Id { get; set; }
    public int SectorId { get; set; }
    public DateOnly Date { get; set; }
    public int Capacity { get; set; }
}
