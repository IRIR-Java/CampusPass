# CampusPass

**Join the queue remotely, wait less, get things done.** A digital queue, smart batching and QR gatepass system for universities, built for Chuka University. COSC 434 · Group 5 · **.NET 8 / ASP.NET Core MVC**

## The problem
About 30,000 students, offices open Mon–Fri 8am–5pm, and 10–15 staff. Clearing just the continuing students takes about 1.5 months. Gatepasses depend on fee clearance, yet many students queue only to learn their balance. Students with a problem get sent from office to office, and downloaded gatepasses can be edited, so staff check them by hand.

## What CampusPass does
| Role | Can do |
|---|---|
| **Student** | See their fee balance. Get an automatic signed **QR gatepass** when cleared. Request a service (ICT & Portal, Finance, GatePass/Security, Academic, Administration, Other) and either **Join Queue** or **Book Appointment**. Get a ticket with a **batch / time window**, position and estimated wait. Watch the live queue. Receive "almost your turn" and "your turn" notifications, tap "I'm on my way", or cancel. |
| **Staff** | Dashboard with today's queue. **Call Next** (the student's fee position is on the same screen). Start service, complete with quick replies, mark a no-show, transfer to the right office (same ticket number). Look up students, see reports, set duty and counter. |
| **Admin** | Dashboard (students, departments, staff on duty, requests per service, queue health). Manage services. Create staff and issue student logins (one-time passwords). Upload Finance's fee file. Set opening hours, batch length, holidays and **faculty registration days**. Reports and settings. |
| **Guard** | Scan a gatepass with any phone camera. The public Verify page shows **VALID / NOT VALID** using live fee data. |

**Smart batching:** each day is split into time windows. A window takes *staff on duty × window minutes ÷ the service's average minutes* students. **Join Queue** takes the earliest window with room, never before the student's faculty registration day.

## Architecture (clean / layered)
```
CampusPass.Web            ASP.NET Core MVC: controllers, Razor views, auth, filters, security headers
   │
CampusPass.Application    Business rules: QueueService, ClearanceService, AccountService, FeeImportService,
   │                      ReportService, SetupService, WorkingCalendar + the IAppDb interface
CampusPass.Domain         Entities: Ticket, Department, StudentRecord, UserAccount, Gatepass, Notification...
CampusPass.Infrastructure EF Core AppDbContext (SQLite) + migrations: implements IAppDb
CampusPass.Tests          33 xUnit tests on a real in-memory SQLite database
```

## Run it
Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
```bash
dotnet run --project src/CampusPass.Web
```
**First run:** there are no accounts and no test data. The server log prints a one-time **setup code**. Open `/Setup`, enter it, and create the institution and the first administrator. The six common service departments are created for you, and you can edit them. Then, as admin:
1. **Fee records**: upload Finance's CSV (`RegNo,Name,Faculty,Phone,TotalFee,Paid`).
2. **Users**: create staff accounts, and issue student logins (one at a time, or all at once as a CSV for SMS/email).
3. **Batches**: add the faculty registration days from the Finance memo.

Everyone signs in with a one-time password and must choose their own.

Data lives in `App_Data/` (SQLite database and cookie keys). Back that folder up.

## Tests
```bash
dotnet test
```

## Hosting
- **From a laptop:** run `host.ps1`. It publishes the app, starts it on this machine only, and opens a Cloudflare tunnel with a public `https://…trycloudflare.com` link. Needs `winget install Cloudflare.cloudflared`.
- **Server / cloud:** `docker build -t campuspass . && docker run -p 8080:8080 -v campuspass-data:/app/App_Data campuspass`.

## Security
- **Passwords:** PBKDF2-SHA512 (210k iterations). 5 failed logins lock the account for 5 minutes. Issued passwords are one-time and must be changed at first sign-in.
- **Gatepass:** the QR holds an HMAC-SHA256-signed random id and no personal data. Verification re-checks live fee status.
- **Web protections:** anti-forgery on every form, CSP with no inline scripts, `X-Frame-Options: DENY`, rate-limited login and verify endpoints, role-based access. Officers act only on tickets they called.

## Notes
The ≤ 5-page architecture notes are in [`docs/Group5-DotNet-Architecture-Notes.docx`](docs/Group5-DotNet-Architecture-Notes.docx) (with a PDF copy).
