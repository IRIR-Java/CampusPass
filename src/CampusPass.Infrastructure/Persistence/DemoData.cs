using CampusPass.Application.Services;
using CampusPass.Domain.Entities;

namespace CampusPass.Infrastructure.Persistence;

/// <summary>A few realistic tickets so the demo queues aren't empty on first run.</summary>
public static class DemoData
{
    public static void Load(QueueService queue)
    {
        void Add(string name, string reg, string title, string desc, Urgency u = Urgency.Normal) =>
            queue.Submit(new SubmitTicketRequest(name, reg, "0700000000", title, desc, u));

        Add("Faith Njoroge", "CS/2021/101", "Gate pass cannot be downloaded", "Portal shows an error when I click download gate pass.");
        Add("Brian Mwenda", "CS/2021/102", "Fee balance wrong", "I paid via M-Pesa but my fee statement does not show the payment.");
        Add("Mercy Wambui", "CS/2022/055", "Need visitor entry", "My parent is visiting on Saturday with a vehicle, how do I get a visitor pass?");
        Add("Dennis Kiprop", "CS/2020/210", "Missing marks", "My CAT marks for a unit are missing in the results portal.", Urgency.Urgent);
        Add("Lucy Achieng", "CS/2023/044", "Cannot log in", "Password reset email never arrives, account locked.");
        Add("Samuel Kariuki", "CS/2021/330", "Question about hostel", "I want to talk to someone about a room allocation issue.");
    }
}
