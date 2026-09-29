namespace CampusPass.Domain.Entities;

public class Officer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SectorId { get; set; }
    public bool OnDuty { get; set; } = true;
}
