using System.ComponentModel;

namespace Schedulry.Core;

/// <summary>How the last run went, for the status chip.</summary>
public enum TaskOutcome
{
    NeverRun,
    Running,
    Succeeded,
    /// <summary>Didn't fail, but didn't do its work either (stopped by someone, skipped because already running).</summary>
    Warning,
    Failed,
}

/// <summary>A last-run result in words. <see cref="Short"/> fits a list column.</summary>
public sealed record ResultText(string Title, string? Detail, TaskOutcome Outcome, string Code)
{
    public string Short { get; init; } = Title;
}

/// <summary>
/// Turns Task Scheduler's result numbers (0x41301, 0x800710E0, an exit code of 1) into sentences.
/// </summary>
public static class ResultCodes
{
    // Task Scheduler's own status codes (SCHED_S_* and SCHED_E_*), with what a person can do about them.
    static readonly Dictionary<uint, (string Title, string? Detail, TaskOutcome Outcome)> Known = new()
    {
        [0x0] = ("Completed successfully", null, TaskOutcome.Succeeded),
        [0x41300] = ("Ready to run", null, TaskOutcome.Succeeded),
        [0x41301] = ("Still running", null, TaskOutcome.Running),
        [0x41302] = ("Task is turned off", null, TaskOutcome.Warning),
        [0x41303] = ("Has not run yet", null, TaskOutcome.NeverRun),
        [0x41304] = ("No more runs scheduled", "Every trigger has passed its end date.", TaskOutcome.Warning),
        [0x41306] = ("Stopped by a person or by its time limit", "Someone ended it, or it ran longer than \"Stop after\" allows.", TaskOutcome.Warning),
        [0x41307] = ("Has no triggers that can start it", "Add a trigger, or turn one back on.", TaskOutcome.Warning),
        [0x41325] = ("Queued", "Waiting for an earlier run or for its conditions.", TaskOutcome.Warning),
        [0x8004131F] = ("Skipped: the previous run was still going", "\"If already running\" is set to not start a new copy.", TaskOutcome.Warning),
        [0x80041326] = ("Task is turned off", null, TaskOutcome.Warning),
        [0x80041323] = ("Task Scheduler was busy", "Try again in a moment.", TaskOutcome.Failed),
        [0x80041324] = ("Not started: conditions weren't met", "Check the power, idle and network conditions.", TaskOutcome.Warning),
        [0x800710E0] = ("Not started: conditions weren't met or it was refused", "Often the account can't sign in this way, or the computer was on battery.", TaskOutcome.Failed),
        [0x80070002] = ("The program wasn't found", "Check the program path in the action.", TaskOutcome.Failed),
        [0x80070003] = ("A folder wasn't found", "Check the program path and the \"Start in\" folder.", TaskOutcome.Failed),
        [0x80070005] = ("Access denied", "The account it runs as can't open the program or a file it needs.", TaskOutcome.Failed),
        [0x8007010B] = ("The \"Start in\" folder is wrong", "The folder doesn't exist. Leave out quotes around it.", TaskOutcome.Failed),
        [0x8007052E] = ("Sign-in failed: wrong user name or password", "The stored password may have changed. Enter it again.", TaskOutcome.Failed),
        [0x80070569] = ("Sign-in refused: the account lacks the \"Log on as a batch job\" right",
            "Running whether or not the user is signed in needs that right. Grant it in Local Security Policy, or run only while signed in.", TaskOutcome.Failed),
        [0x800704DD] = ("Not started: the user isn't signed in", "Set it to run whether the user is signed in or not.", TaskOutcome.Failed),
        [0x8007042B] = ("The program's process ended unexpectedly", null, TaskOutcome.Failed),
        [0xC000013A] = ("Stopped with Ctrl+C or the console closed", null, TaskOutcome.Warning),
        [0xC0000142] = ("The program couldn't start (DLL initialisation failed)", "Often a program that needs a desktop, run without one.", TaskOutcome.Failed),
        [0xC0000005] = ("The program crashed (access violation)", null, TaskOutcome.Failed),
        [0xE0434352] = ("The .NET program crashed with an unhandled error", "The Application event log has the details.", TaskOutcome.Failed),
    };

    public static string Hex(int code) => $"0x{unchecked((uint)code):X}";

    public static TaskOutcome Classify(int? code, DateTime? lastRun)
    {
        if (code is null || lastRun is null)
            return TaskOutcome.NeverRun;
        return Describe(code.Value).Outcome;
    }

    // Two or three words for the list column; the full sentence is in the preview.
    static readonly Dictionary<uint, string> ShortTitles = new()
    {
        [0x0] = "Succeeded",
        [0x41306] = "Stopped",
        [0x41307] = "No triggers",
        [0x8004131F] = "Skipped (still running)",
        [0x80041324] = "Conditions not met",
        [0x800710E0] = "Refused to start",
        [0x80070002] = "Program not found",
        [0x80070003] = "Folder not found",
        [0x8007010B] = "Bad \"Start in\" folder",
        [0x8007052E] = "Wrong password",
        [0x80070569] = "Sign-in refused",
        [0x800704DD] = "User not signed in",
        [0xC000013A] = "Stopped (Ctrl+C)",
        [0xC0000142] = "Couldn't start",
        [0xC0000005] = "Crashed",
        [0xE0434352] = "Crashed (.NET)",
    };

    public static ResultText Describe(int code)
    {
        var key = unchecked((uint)code);
        if (Known.TryGetValue(key, out var known))
            return new ResultText(known.Title, known.Detail, known.Outcome, Hex(code)) { Short = ShortTitles.GetValueOrDefault(key, known.Title) };

        // A small positive number is the program's own exit code: Task Scheduler passes it through.
        if (code is > 0 and < 0x10000)
            return new ResultText($"The program reported an error (exit code {code})",
                "The program itself decides what this number means; check its log or output.", TaskOutcome.Failed, $"{code} ({Hex(code)})")
            { Short = $"Exit code {code}" };

        // A Windows error wrapped as an HRESULT (0x8007xxxx): Windows has its own sentence for it.
        if ((key & 0xFFFF0000) == 0x80070000)
        {
            var message = new Win32Exception((int)(key & 0xFFFF)).Message.TrimEnd('.', ' ');
            return new ResultText(message, null, TaskOutcome.Failed, Hex(code)) { Short = $"Error {Hex(code)}" };
        }

        return new ResultText($"Failed with code {Hex(code)}", null, TaskOutcome.Failed, Hex(code)) { Short = $"Error {Hex(code)}" };
    }
}
