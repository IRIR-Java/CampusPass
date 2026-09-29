using System.ComponentModel.DataAnnotations;
using CampusPass.Application.Services;
using CampusPass.Domain.Entities;

namespace CampusPass.Web.Models;

public class SubmitForm
{
    [Required, StringLength(80), Display(Name = "Full name")]
    public string StudentName { get; set; } = "";

    [Required, StringLength(30), Display(Name = "Registration number")]
    public string RegNo { get; set; } = "";

    [Required, Phone, StringLength(20), Display(Name = "Phone (for call alerts)")]
    public string Phone { get; set; } = "";

    [Required, StringLength(120), Display(Name = "What is the problem? (short)")]
    public string Title { get; set; } = "";

    [Required, StringLength(1500), Display(Name = "Tell us more")]
    public string Description { get; set; } = "";

    [Display(Name = "Which service? (leave as Not sure and we will route it)")]
    public int? SectorId { get; set; }

    [Display(Name = "This is urgent (exam / graduation / travel within 3 days)")]
    public bool Urgent { get; set; }
}

public class SubmitPageModel
{
    public SubmitForm Form { get; set; } = new();
    public IReadOnlyList<Sector> Sectors { get; set; } = Array.Empty<Sector>();
}

public class TrackPageModel
{
    public string? Number { get; set; }
    public TrackResult? Result { get; set; }
    public bool JustCreated { get; set; }
    public bool NotFound { get; set; }
}

public class DeskPageModel
{
    public Officer Officer { get; set; } = null!;
    public SectorQueue Queue { get; set; } = null!;
    public Ticket? Current { get; set; }
}

public class OfficerTicketPageModel
{
    public Officer Officer { get; set; } = null!;
    public Ticket Ticket { get; set; } = null!;
    public Sector Sector { get; set; } = null!;
    public IReadOnlyList<Sector> OtherSectors { get; set; } = Array.Empty<Sector>();
}

public class AdminPageModel
{
    public IReadOnlyList<SectorStats> Stats { get; set; } = Array.Empty<SectorStats>();
    public IReadOnlyList<Ticket> Recent { get; set; } = Array.Empty<Ticket>();
    public IReadOnlyList<Officer> Officers { get; set; } = Array.Empty<Officer>();
    public IReadOnlyDictionary<int, Sector> SectorsById { get; set; } = new Dictionary<int, Sector>();
}

public record OfficerPickerModel(IReadOnlyList<Officer> Officers, IReadOnlyDictionary<int, Sector> Sectors);
