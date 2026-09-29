using CampusPass.Application.Abstractions;
using CampusPass.Application.Services;
using CampusPass.Domain.Entities;
using CampusPass.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace CampusPass.Web.Controllers;

public class StudentController(QueueService queue, ISectorRepository sectors) : Controller
{
    [HttpGet]
    public IActionResult Submit() => View(new SubmitPageModel { Sectors = sectors.All() });

    [HttpPost]
    public IActionResult Submit(SubmitForm form)
    {
        if (!ModelState.IsValid)
            return View(new SubmitPageModel { Form = form, Sectors = sectors.All() });

        var result = queue.Submit(new SubmitTicketRequest(
            form.StudentName, form.RegNo, form.Phone, form.Title, form.Description,
            form.Urgent ? Urgency.Urgent : Urgency.Normal, form.SectorId));

        return RedirectToAction(nameof(Track), new { number = result.Ticket.Number, created = true });
    }

    [HttpGet]
    public IActionResult Track(string? number, bool created = false)
    {
        var model = new TrackPageModel { Number = number, JustCreated = created };
        if (!string.IsNullOrWhiteSpace(number))
        {
            model.Result = queue.Track(number);
            model.NotFound = model.Result is null;
        }
        return View(model);
    }
}
