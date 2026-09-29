# 🎫 CampusPass

**GatePass Management, Smart Batching & Intelligent Queue Routing System**
COSC 434 group project · Architecture: **.NET 8 (ASP.NET Core MVC)**

A student submits a problem from anywhere → gets a ticket → the system **routes** it to the right department and **batches** it into a suitable service day → the student tracks it → the officer presses **Call Next** → the problem opens ready to handle → **Resolved** (or **re-routed** if it was the wrong desk).

## Run it (2 minutes)

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) only. No database, no setup.

```bash
git clone https://github.com/IRIR-Java/CampusPass.git
cd CampusPass
dotnet run --project src/CampusPass.Web
```

Open the URL printed in the terminal (e.g. `http://localhost:5xxx`). Six demo tickets are pre-loaded (turn off with `"SeedDemoData": false` in `appsettings.json`).

Run the tests: `dotnet test`

## 5-minute demo script

1. **Student** → *Get a ticket* → title `Gate pass cannot be downloaded`, leave sector on "Not sure" → it lands in **ICT / Portal**, gets a number (e.g. `ICT-003`) and a service day.
2. **Track ticket** → see status, department, service day, tickets ahead of you.
3. **Officer desk** → pick *Officer Achieng (ICT)* → **📣 Call Next** → the ticket opens with the student's details already on screen.
4. **Start working** → write a resolution note → **Mark resolved**. Back on the student's tracking page the note is now visible.
5. **Re-route**: call a ticket, choose another department + reason → the student **keeps their place in line**.
6. **Admin** → toggle an officer *Off duty* → daily capacity drops, and new tickets spill into later batches. Watch the batch load bars.

## How it works

| Concern | Rule (all in `src/CampusPass.Application`) |
|---|---|
| **Routing** | `RoutingService`: weighted keyword scoring per sector. Nothing matches → General Help Desk for human triage. The `Route()` method is the seam where ML/LLM classification can replace it. |
| **Smart batching** | `BatchingService`: daily capacity = on-duty officers × per-officer throughput. Normal tickets fill **80%** of a day (20% reserved for urgent). No same-day slots after **16:00** or on weekends. |
| **Queue order** | Urgent first → earliest batch day → first come, first served. |
| **Call Next** | Only tickets whose batch day has arrived. An officer must finish their current ticket first. A lock guarantees two officers can never get the same ticket. |
| **Re-route** | Ticket keeps its number and original timestamp; batch is re-assigned with urgent priority. |

## Architecture

```
CampusPass.Web            ASP.NET Core MVC: controllers + Razor views (Student / Officer / Admin)
   │ uses
CampusPass.Application    Business rules: Routing, Batching, Queue, Reporting  + repository interfaces
   │ uses
CampusPass.Domain         Entities: Ticket, Sector, Officer, Batch, enums (no dependencies)
CampusPass.Infrastructure Implements the repository interfaces (in-memory now; EF Core later)
CampusPass.Tests          xUnit tests for routing, batching and the full queue flow
```

Dependencies point inward: the business rules never know about the web or the database, so swapping in SQL Server or adding an API changes one layer only. A draft write-up for the 5-page architecture notes is in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Workflow gaps we found (and proposed fixes)

| Gap | Why it matters | Proposed fix |
|---|---|---|
| **No student identity check** | Anyone can file under any reg number | Sign in with university credentials (ASP.NET Core Identity / SSO) |
| **Everyone ticks "Urgent"** | Urgent lane becomes the normal lane | Require a reason, let officers downgrade, cap urgent tickets per student |
| **Remote "Called" but student is away** | Officers wait forever | SMS/push alert on call + grace timer (e.g. 10 min) → auto no-show → student can re-queue |
| **Some issues need a physical visit** (e.g. collecting a pass) | "Call Next" means different things | Ticket type: *remote fix* vs *visit desk*; visit tickets show counter/room |
| **Wrong category ping-pong** | Ticket bounces between departments | Limit re-routes (e.g. 2), then escalate to Help Desk supervisor |
| **Routing is keyword-based** | Misroutes unusual wording | Show confidence; low confidence → triage; use re-route history to tune weights |
| **Officer goes off duty after batches are booked** | Booked day now over capacity | Rebalance job moves overflow to the next day and notifies students |
| **Resolved but not actually fixed** | Student must start over | "Reopen" within N days, linked to the original ticket |
| **Duplicate tickets** | Same student, same issue, multiple tickets | Warn if the student already has an open ticket in that sector |
| **Data is in memory** | Restart = everything lost | EF Core + SQL Server / SQLite (interfaces already in place) |

## Suggested next steps / work split

1. **Persistence**: EF Core repositories + migrations (`Infrastructure`).
2. **Auth & roles**: ASP.NET Core Identity (Student / Officer / Admin).
3. **Notifications**: SMS/email when a ticket is called (e.g. Africa's Talking).
4. **Live updates**: SignalR so the desk and tracking pages refresh instantly (they poll every 20-30 s now).
5. **Admin tools**: manage sectors, keywords, staff and capacity from the UI; reports.
6. **UX polish + documentation**: design thinking notes, the 5-page architecture document, demo video.
