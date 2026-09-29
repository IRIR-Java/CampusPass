# Architecture notes (DRAFT: expand to ≤ 5 pages for submission)

## 1. Problem
Students with different gate-pass and administrative problems join one general queue even when they need different departments. Result: long waits, repeated explanations, delays.

## 2. Solution in one paragraph
CampusPass lets a student raise a request remotely, receive a digital ticket, and be **routed** to the right sector and **batched** into a suitable service day. Each sector has a virtual "bank teller" queue: when an officer is free they press *Call Next*, the ticket opens with the student's problem already visible, and the officer resolves it or re-routes it.

## 3. Why .NET
- **.NET 8 is open-source and cross-platform** (Windows, Linux, macOS), unlike the older Windows-only **.NET Framework**. The system can be developed on any laptop and hosted on a Linux server or in the cloud.
- **ASP.NET Core MVC** gives routing, model binding, validation (DataAnnotations), Razor views and anti-forgery protection out of the box.
- **Built-in dependency injection** makes the layers swappable (in-memory ↔ database) and easy to test.
- **C# + LINQ** keep the routing, batching and queue rules short and readable.
- **xUnit** for automated tests; **EF Core** ready for persistence.

## 4. Layered design
| Layer | Responsibility |
|---|---|
| Web (MVC) | HTTP, forms, validation, views for the three roles |
| Application | Business rules: `RoutingService`, `BatchingService`, `QueueService`, `ReportingService`, repository interfaces |
| Domain | Entities and enums, no dependencies |
| Infrastructure | Repository implementations (in-memory now) |

Dependencies point inward. The Application layer defines the repository *interfaces*; Infrastructure implements them (dependency inversion), so replacing memory with SQL Server touches one project.

## 5. Key flows
1. **Submit**: validate → `RoutingService.Route` → `BatchingService.Assign` → create ticket (`ICT-003`) → redirect to tracking.
2. **Call Next**: officer's sector → tickets whose batch day ≤ today → order (urgent, batch day, FIFO) → mark *Called*. Guarded by a lock and by the "finish your current ticket" rule.
3. **Resolve**: requires a note; sets *Resolved* and timestamps used for turnaround reports.
4. **Re-route**: new sector, new batch (urgent priority), original number and timestamp kept, history entry added.

State machine: `Waiting → Called → InProgress → Resolved`, with `Called → NoShow` and `Waiting/Called/InProgress → (re-route) → Waiting`.

## 6. Smart batching rules
Capacity per day = on-duty officers × per-officer throughput. Normal tickets fill 80% of a day, 20% reserved for urgent. No same-day slots after 16:00 or on weekends. Capacity recalculates when staffing changes.

## 7. Known limitations / future work
See the *Workflow gaps* table in the README (identity, urgent abuse, no-show timers, persistence, notifications, real-time updates).

## 8. To add before submission
- Class/sequence diagrams (draw.io) · screenshots of each role's screens · test evidence (`dotnet test` output) · design-thinking notes (users, pain points, prototype iterations).
