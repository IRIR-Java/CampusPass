namespace CampusPass.Domain;

public enum Role { Student = 0, Staff = 1, Admin = 2 }

/// <summary>Anyone who signs in. Students are linked to a fee record by RegNo; staff to the department they serve.</summary>
public class UserAccount
{
    public int Id { get; set; }
    public string Username { get; set; } = "";          // reg no for students (upper case), staff username otherwise
    public string DisplayName { get; set; } = "";
    public Role Role { get; set; }
    public string? RegNo { get; set; }
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public string? Counter { get; set; }                  // e.g. "Counter 2", shown to students when called
    public bool OnDuty { get; set; } = true;
    public string? Phone { get; set; }
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public bool MustChangePassword { get; set; } = true;  // accounts are issued with a one-time password
    public bool Disabled { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>A service point students queue for: ICT &amp; Portal, Finance, GatePass/Security, Academic...</summary>
public class Department
{
    public int Id { get; set; }
    public string Code { get; set; } = "";                 // ticket prefix: ICT-023
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";          // "Login issues, IT support"
    public string Location { get; set; } = "";             // "Main Administration Block"
    public int AvgServiceMinutes { get; set; } = 5;
    public string Icon { get; set; } = "help";
    public string Color { get; set; } = "green";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A student's fee position, loaded from Finance's export. Clearance for a gatepass depends on it.</summary>
public class StudentRecord
{
    public string RegNo { get; set; } = "";
    public string Name { get; set; } = "";
    public string Faculty { get; set; } = "";
    public string Phone { get; set; } = "";
    public decimal TotalFee { get; set; }
    public decimal Paid { get; set; }
    public DateTime BalanceAsOf { get; set; }

    public decimal Balance => TotalFee - Paid;
    public bool IsCleared => Balance <= 0;
}

public enum TicketMode { Queue = 0, Appointment = 1 }

public enum TicketStatus { Waiting = 0, Called = 1, Serving = 2, Completed = 3, NoShow = 4, Cancelled = 5 }

public class Ticket
{
    public int Id { get; set; }
    public string Number { get; set; } = "";               // ICT-023
    public int DepartmentId { get; set; }
    public Department? Department { get; set; }
    public int StudentUserId { get; set; }
    public string RegNo { get; set; } = "";
    public string StudentName { get; set; } = "";
    public TicketMode Mode { get; set; }
    public string? Description { get; set; }
    public TicketStatus Status { get; set; }

    /// <summary>The batch: the time window the student is booked into ("Tue 2:00 - 3:00 PM").</summary>
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? CalledAt { get; set; }
    public DateTime? ServingAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public int? StaffUserId { get; set; }
    public string? StaffName { get; set; }
    public string? Counter { get; set; }
    public bool OnTheWay { get; set; }                     // student tapped "I'm on my way"
    public bool AlmostNotified { get; set; }
    public string? Resolution { get; set; }
    public string? TransferredFrom { get; set; }

    public bool IsOpen => Status is TicketStatus.Waiting or TicketStatus.Called or TicketStatus.Serving;
}

public class Gatepass
{
    public int Id { get; set; }
    public string TokenId { get; set; } = "";
    public string RegNo { get; set; } = "";
    public string Semester { get; set; } = "";
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool Revoked { get; set; }
    public string? RevokedReason { get; set; }

    public string Serial => $"GP-{Id:000000}";
}

public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Link { get; set; }
    public string Kind { get; set; } = "info";             // info | almost | called | done
    public DateTime CreatedAt { get; set; }
    public bool Read { get; set; }
}

/// <summary>Registration day per faculty (from the Finance Officer's memo). Queue tickets are not booked before it.</summary>
public class FacultyDay
{
    public int Id { get; set; }
    public string Faculty { get; set; } = "";
    public DateOnly Date { get; set; }
}

/// <summary>Institution-wide settings edited by the admin (single row).</summary>
public class SystemSettings
{
    public int Id { get; set; } = 1;
    public string InstitutionName { get; set; } = "";
    public string Semester { get; set; } = "";
    public DateOnly PassValidUntil { get; set; }
    public TimeOnly OpenTime { get; set; } = new(8, 0);
    public TimeOnly CloseTime { get; set; } = new(17, 0);
    public TimeOnly LunchStart { get; set; } = new(13, 0);
    public TimeOnly LunchEnd { get; set; } = new(14, 0);
    public int WindowMinutes { get; set; } = 60;
    public int BookingDaysAhead { get; set; } = 10;
    public string PaymentInstructions { get; set; } = "";
    public string SupportContact { get; set; } = "";
    public string Holidays { get; set; } = "";              // comma separated yyyy-MM-dd
    public string GatepassKey { get; set; } = "";           // HMAC key for QR tokens (generated at setup)
}
