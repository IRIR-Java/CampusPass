using CampusPass.Domain.Entities;

namespace CampusPass.Application.Abstractions;

// Storage contracts. The Infrastructure project implements them (in-memory today;
// swap for EF Core + SQL Server/SQLite without touching any business logic).

public interface ISectorRepository
{
    IReadOnlyList<Sector> All();
    Sector? Get(int id);
}

public interface IOfficerRepository
{
    IReadOnlyList<Officer> All();
    Officer? Get(int id);
    IReadOnlyList<Officer> BySector(int sectorId);
    void SetOnDuty(int id, bool onDuty);
}

public interface IBatchRepository
{
    IReadOnlyList<Batch> All();
    Batch? Find(int sectorId, DateOnly date);
    Batch Add(Batch batch);
}

public interface ITicketRepository
{
    IReadOnlyList<Ticket> All();
    Ticket? Get(int id);
    Ticket? GetByNumber(string number);
    Ticket Add(Ticket ticket);
    void Update(Ticket ticket);
    int NextSequence(int sectorId);
}

public interface IClock
{
    DateTime Now { get; }
}

public class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
}
