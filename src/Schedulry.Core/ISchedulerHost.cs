namespace Schedulry.Core;

/// <summary>
/// One computer's Task Scheduler. The Windows host talks to the real service; the demo host
/// simulates one. Calls may be slow (COM, network) and are made from a background thread.
/// </summary>
public interface ISchedulerHost : IDisposable
{
    /// <summary>The computer's name, as shown in the title bar.</summary>
    string ComputerName { get; }

    /// <summary>True when this app runs as administrator, so every task can be changed.</summary>
    bool IsElevated { get; }

    /// <summary>The account this app runs as (DOMAIN\user), to tell which tasks are the user's own.</summary>
    string CurrentUser { get; }

    /// <summary>Every task in every folder, read in full. Folders the account can't open are skipped.</summary>
    IReadOnlyList<TaskSnapshot> ReadAll();

    /// <summary>One task in full, or null when it no longer exists.</summary>
    TaskSnapshot? Read(string path);

    /// <summary>
    /// The live fields of every task. Cheaper than <see cref="ReadAll"/>. Without
    /// <paramref name="includeWindowsTasks"/> the \Microsoft folder is skipped: it holds most of a
    /// computer's tasks, and each one costs a call to the Task Scheduler service.
    /// </summary>
    IReadOnlyList<TaskStatusInfo> ReadStatus(bool includeWindowsTasks = true);

    /// <summary>Instances running right now, hidden tasks included.</summary>
    IReadOnlyList<RunningInstance> ReadRunning();

    /// <summary>The next run times, up to <paramref name="count"/>, within the coming year.</summary>
    IReadOnlyList<DateTime> NextRuns(string path, int count);

    /// <summary>The task's definition as Task Scheduler stores it.</summary>
    string ReadXml(string path);

    /// <summary>Whether Task Scheduler is keeping its history log (off by default in Windows).</summary>
    bool HistoryEnabled { get; }

    /// <summary>
    /// Asks Windows again now, rather than trusting a recent answer: right after the history log
    /// was turned on, so the app and the host agree straight away.
    /// </summary>
    bool RecheckHistory();

    /// <summary>
    /// Raised from any thread with a task's path when the history log records something about it
    /// (started, finished, changed, deleted). Never raised while the history log is off.
    /// </summary>
    event Action<string>? TaskEvent;

    /// <summary>
    /// Runs recorded in the history log since <paramref name="since"/>, newest first: one task's,
    /// or every task's when <paramref name="path"/> is null. Empty while the history log is off.
    /// </summary>
    IReadOnlyList<RunRecord> ReadRuns(string? path, DateTime since, int maxRuns);

    void Run(string path);
    void Stop(string path);
    void SetEnabled(string path, bool enabled);

    /// <summary>Removes the task from Task Scheduler for good.</summary>
    void Delete(string path);

    /// <summary>Every folder, "\" first, including empty ones (for the editor's folder list).</summary>
    IReadOnlyList<string> Folders();

    /// <summary>The XML Task Scheduler would store for this draft, without saving anything.</summary>
    string BuildXml(TaskDraft draft);

    /// <summary>
    /// Creates or updates the task. A draft whose path changed is saved at the new path, then the
    /// old one removed. <paramref name="password"/> is passed straight to Windows and never kept.
    /// </summary>
    void Save(TaskDraft draft, string? password);

    /// <summary>Registers a task from Task Scheduler XML (a file exported from here or the old window).</summary>
    void Import(string folder, string name, string xml, string? password);
}
