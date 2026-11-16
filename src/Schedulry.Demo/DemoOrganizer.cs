using Schedulry.Core;

namespace Schedulry.Demo;

/// <summary>Stars and groups for the demo computer, so demo mode and screenshots show them in use.</summary>
public static class DemoOrganizer
{
    public static OrganizerData Sample() => new()
    {
        Starred = [@"\Mirror data to NAS", @"\Sync payroll exports"],
        Groups =
        [
            new TaskGroup { Name = "Finance", Paths = [@"\Sync payroll exports", @"\Reports\Monthly usage report", @"\Reports\Daily sales digest"] },
            new TaskGroup { Name = "Overnight jobs", Paths = [@"\Mirror data to NAS", @"\Maintenance\Rotate web logs", @"\Maintenance\Defragment data drive", @"\Microsoft\Windows\Defrag\ScheduledDefrag"] },
        ],
    };
}
