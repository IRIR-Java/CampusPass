using CampusPass.Application.Abstractions;
using CampusPass.Domain.Entities;

namespace CampusPass.Infrastructure.Persistence;

// Thread-safe in-memory stores so the prototype runs with ZERO setup (no DB, no NuGet packages).
// To go "real": add EF Core, implement these same interfaces against a DbContext, change 4 lines in Program.cs.

public class InMemorySectorRepository : ISectorRepository
{
    private readonly List<Sector> _items = SeedData.Sectors();
    public IReadOnlyList<Sector> All() => _items.ToList();
    public Sector? Get(int id) => _items.FirstOrDefault(s => s.Id == id);
}

public class InMemoryOfficerRepository : IOfficerRepository
{
    private readonly List<Officer> _items = SeedData.Officers();
    public IReadOnlyList<Officer> All() => _items.ToList();
    public Officer? Get(int id) => _items.FirstOrDefault(o => o.Id == id);
    public IReadOnlyList<Officer> BySector(int sectorId) => _items.Where(o => o.SectorId == sectorId).ToList();
    public void SetOnDuty(int id, bool onDuty) { if (Get(id) is Officer o) o.OnDuty = onDuty; }
}

public class InMemoryBatchRepository : IBatchRepository
{
    private readonly List<Batch> _items = new();
    private readonly object _lock = new();
    private int _nextId = 1;

    public IReadOnlyList<Batch> All() { lock (_lock) return _items.ToList(); }

    public Batch? Find(int sectorId, DateOnly date)
    {
        lock (_lock) return _items.FirstOrDefault(b => b.SectorId == sectorId && b.Date == date);
    }

    public Batch Add(Batch batch)
    {
        lock (_lock)
        {
            batch.Id = _nextId++;
            _items.Add(batch);
            return batch;
        }
    }
}

public class InMemoryTicketRepository : ITicketRepository
{
    private readonly List<Ticket> _items = new();
    private readonly object _lock = new();
    private int _nextId = 1;

    public IReadOnlyList<Ticket> All() { lock (_lock) return _items.ToList(); }

    public Ticket? Get(int id) { lock (_lock) return _items.FirstOrDefault(t => t.Id == id); }

    public Ticket? GetByNumber(string number)
    {
        lock (_lock)
            return _items.FirstOrDefault(t => t.Number.Equals(number, StringComparison.OrdinalIgnoreCase));
    }

    public Ticket Add(Ticket ticket)
    {
        lock (_lock)
        {
            ticket.Id = _nextId++;
            _items.Add(ticket);
            return ticket;
        }
    }

    public void Update(Ticket ticket) { /* objects are shared in memory; a DB repo would SaveChanges here */ }

    /// <summary>Sequence per sector prefix (re-routed tickets keep their original number).</summary>
    public int NextSequence(int sectorId)
    {
        var prefix = SeedData.Sectors().First(s => s.Id == sectorId).Code + "-";
        lock (_lock) return _items.Count(t => t.Number.StartsWith(prefix)) + 1;
    }
}
