using CampusPass.Domain.Entities;

namespace CampusPass.Infrastructure.Persistence;

public static class SeedData
{
    public static List<Sector> Sectors() => new()
    {
        new Sector
        {
            Id = 1, Name = "ICT / Portal", Code = "ICT", TicketsPerOfficerPerDay = 20,
            Keywords = new()
            {
                ["download"] = 3, ["portal"] = 3, ["login"] = 3, ["log in"] = 3, ["password"] = 3,
                ["cannot open"] = 2, ["not loading"] = 3, ["error"] = 2, ["website"] = 2, ["wifi"] = 3,
                ["email"] = 2, ["account locked"] = 3, ["system"] = 1, ["reset"] = 2, ["slow"] = 1
            }
        },
        new Sector
        {
            Id = 2, Name = "Finance", Code = "FIN", TicketsPerOfficerPerDay = 12,
            Keywords = new()
            {
                ["fee"] = 3, ["fees"] = 3, ["balance"] = 2, ["receipt"] = 3, ["payment"] = 3, ["mpesa"] = 3,
                ["m-pesa"] = 3, ["paybill"] = 3, ["bursary"] = 3, ["helb"] = 3, ["refund"] = 3,
                ["invoice"] = 2, ["statement"] = 2, ["overpaid"] = 3
            }
        },
        new Sector
        {
            Id = 3, Name = "GatePass & Security", Code = "GP", TicketsPerOfficerPerDay = 25,
            Keywords = new()
            {
                ["gate pass"] = 2, ["gatepass"] = 2, ["visitor"] = 3, ["vehicle"] = 3, ["entry"] = 2,
                ["exit"] = 2, ["sticker"] = 3, ["lost id"] = 3, ["id card"] = 3, ["security"] = 2,
                ["collect"] = 1, ["luggage"] = 3, ["clearance"] = 1
            }
        },
        new Sector
        {
            Id = 4, Name = "Registrar / Academics", Code = "REG", TicketsPerOfficerPerDay = 10,
            Keywords = new()
            {
                ["transcript"] = 3, ["exam card"] = 3, ["registration"] = 2, ["course"] = 2, ["marks"] = 3,
                ["results"] = 3, ["deferment"] = 3, ["admission letter"] = 3, ["unit"] = 1, ["missing marks"] = 4
            }
        },
        new Sector
        {
            Id = 5, Name = "General Help Desk", Code = "HD", TicketsPerOfficerPerDay = 30, IsDefault = true
            // No keywords: this is where unclassified tickets land for human triage.
        }
    };

    public static List<Officer> Officers() => new()
    {
        new Officer { Id = 1, Name = "Officer Achieng (ICT)", SectorId = 1 },
        new Officer { Id = 2, Name = "Officer Kamau (ICT)", SectorId = 1 },
        new Officer { Id = 3, Name = "Officer Wanjiru (Finance)", SectorId = 2 },
        new Officer { Id = 4, Name = "Officer Otieno (Finance)", SectorId = 2 },
        new Officer { Id = 5, Name = "Officer Mwangi (Gate)", SectorId = 3 },
        new Officer { Id = 6, Name = "Officer Njeri (Gate)", SectorId = 3 },
        new Officer { Id = 7, Name = "Officer Mutua (Registrar)", SectorId = 4 },
        new Officer { Id = 8, Name = "Officer Halima (Help Desk)", SectorId = 5 }
    };
}
