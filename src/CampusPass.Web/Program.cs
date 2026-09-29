using CampusPass.Application.Abstractions;
using CampusPass.Application.Services;
using CampusPass.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

// --- Storage (in-memory for the prototype). Swap these 4 lines for EF Core repositories later. ---
builder.Services.AddSingleton<ISectorRepository, InMemorySectorRepository>();
builder.Services.AddSingleton<IOfficerRepository, InMemoryOfficerRepository>();
builder.Services.AddSingleton<IBatchRepository, InMemoryBatchRepository>();
builder.Services.AddSingleton<ITicketRepository, InMemoryTicketRepository>();

// --- Business logic ---
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<RoutingService>();
builder.Services.AddSingleton<BatchingService>();
builder.Services.AddSingleton<QueueService>();
builder.Services.AddSingleton<ReportingService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Home/Error");

app.UseStaticFiles();
app.UseRouting();

// Demo tickets so the queues are not empty on first run. Turn off in appsettings.json.
if (app.Configuration.GetValue("SeedDemoData", true))
    DemoData.Load(app.Services.GetRequiredService<QueueService>());

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
