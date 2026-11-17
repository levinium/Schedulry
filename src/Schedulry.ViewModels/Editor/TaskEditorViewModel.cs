using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Schedulry.Core;

namespace Schedulry.ViewModels.Editor;

/// <summary>A way the task's account signs in, in words, with what it means.</summary>
public sealed record LogonChoice(LogonKind Kind, string Title, string Explanation);

public enum ReviewTab
{
    Changes,
    PowerShell,
    Xml,
}

/// <summary>
/// The one-page task editor. Every edit rebuilds a <see cref="TaskDraft"/>; beside the form, the
/// review says what saving will change (or what's stopping it), and shows the same task as
/// PowerShell and as XML. Nothing reaches Task Scheduler until Save.
/// </summary>
public sealed partial class TaskEditorViewModel : ObservableObject
{
    readonly MainViewModel _shell;
    readonly TaskSnapshot? _original;
    bool _loading = true;

    public static IReadOnlyList<Choice<string>> InstancePolicies { get; } =
    [
        new("IgnoreNew", DraftChanges.InstancesText("IgnoreNew")),
        new("Queue", DraftChanges.InstancesText("Queue")),
        new("Parallel", DraftChanges.InstancesText("Parallel")),
        new("StopExisting", DraftChanges.InstancesText("StopExisting")),
    ];

    /// <summary>Opens the editor on an existing task, a copy of one (<paramref name="copy"/>), or a blank new task.</summary>
    public TaskEditorViewModel(MainViewModel shell, TaskSnapshot? original, bool copy = false, string? folder = null)
    {
        _shell = shell;
        _original = copy ? null : original;
        IsNew = _original is null;
        Title = copy ? "Copy of a Task" : IsNew ? "New Task" : "Edit Task";

        var draft = original is null
            ? new TaskDraft
            {
                Folder = folder ?? "\\",
                Name = "",
                RunAs = shell.CurrentUser,
                Logon = LogonKind.Interactive,
                Triggers = [new DraftTrigger(new TriggerSpec { Kind = TriggerKind.Daily, Start = DateTime.Today.AddDays(1).AddHours(9) })],
                Actions = [new DraftAction(new ActionInfo(ActionKind.Program, ""))],
                Settings = new TaskSettingsInfo { StopAfter = TimeSpan.FromHours(72), RunMissedStart = true },
            }
            : TaskDraft.From(original) with { OriginalPath = copy ? null : original.Path };
        if (copy)
            draft = draft with { Name = original!.Name + " (copy)" };

        Folders = shell.Host.Folders();
        Name = draft.Name;
        Folder = draft.Folder;
        Description = draft.Description ?? "";
        Enabled = draft.Enabled;
        RunAs = draft.RunAs;

        var choices = new List<LogonChoice>
        {
            new(LogonKind.Interactive, "Only while the account is signed in",
                "Runs on that person's desktop, where they can see it. Nothing runs while they're signed out."),
            new(LogonKind.Password, "Whether signed in or not, with its password",
                "Runs in the background at any time. Windows needs the account's password now, and the task stops working when that password changes."),
            new(LogonKind.S4U, "Whether signed in or not, without a password",
                "Runs in the background at any time, with no password to keep up to date. It can't reach network shares or encrypted files, and the account needs the \"Log on as a batch job\" right."),
            new(LogonKind.ServiceAccount, "As a built-in Windows account",
                "SYSTEM, LOCAL SERVICE or NETWORK SERVICE: runs in the background with no password. SYSTEM can do anything on this computer."),
        };
        if (draft.Logon is LogonKind.Group or LogonKind.InteractiveOrPassword)
            choices.Add(new(draft.Logon, draft.Logon == LogonKind.Group ? "For any signed-in member of a group" : "On the desktop when signed in, otherwise with its password",
                draft.Logon == LogonKind.Group ? "Runs for whichever member of the group is signed in." : "Runs visibly while the person is signed in, and in the background with the stored password otherwise."));
        LogonChoices = choices;
        Logon = choices.First(c => c.Kind == draft.Logon);
        HighestPrivileges = draft.HighestPrivileges;

        foreach (var trigger in draft.Triggers)
            AddTriggerEditor(new TriggerEditor(trigger, RemoveTrigger));
        foreach (var action in draft.Actions)
            AddActionEditor(new ActionEditor(action, RemoveAction));

        var s = draft.Settings;
        LimitRunTime = s.StopAfter is not null;
        StopAfter = Watch(new DurationEditor(s.StopAfter ?? TimeSpan.FromHours(72)));
        MultipleInstances = InstancePolicies.FirstOrDefault(p => p.Value == s.MultipleInstances) ?? InstancePolicies[0];
        RunMissedStart = s.RunMissedStart;
        RetryOnFailure = s.RestartCount > 0;
        RetryEvery = Watch(new DurationEditor(s.RestartInterval > TimeSpan.Zero ? s.RestartInterval : TimeSpan.FromMinutes(5)));
        RetryCount = Math.Max(1, s.RestartCount);
        OnlyOnAcPower = s.OnlyOnAcPower;
        OnlyWhenIdle = s.OnlyWhenIdle;
        OnlyWithNetwork = s.OnlyWithNetwork;
        WakeToRun = s.WakeToRun;
        AllowRunOnDemand = s.AllowRunOnDemand;
        Hidden = s.Hidden;
        _settingsBase = s;

        _loading = false;
        Refresh();
    }

    readonly TaskSettingsInfo _settingsBase;

    public string Title { get; }
    public bool IsNew { get; }
    public IReadOnlyList<string> Folders { get; }
    public IReadOnlyList<LogonChoice> LogonChoices { get; }
    public ObservableCollection<TriggerEditor> Triggers { get; } = [];
    public bool NoTriggers => Triggers.Count == 0;
    public ObservableCollection<ActionEditor> Actions { get; } = [];

    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial string Folder { get; set; }
    [ObservableProperty] public partial string Description { get; set; }
    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial string RunAs { get; set; }
    [ObservableProperty] public partial LogonChoice Logon { get; set; }
    [ObservableProperty] public partial bool HighestPrivileges { get; set; }
    /// <summary>Only while saving needs it; handed to Windows and cleared, never stored.</summary>
    [ObservableProperty] public partial string Password { get; set; } = "";

    [ObservableProperty] public partial bool LimitRunTime { get; set; }
    public DurationEditor StopAfter { get; }
    [ObservableProperty] public partial Choice<string> MultipleInstances { get; set; }
    [ObservableProperty] public partial bool RunMissedStart { get; set; }
    [ObservableProperty] public partial bool RetryOnFailure { get; set; }
    public DurationEditor RetryEvery { get; }
    [ObservableProperty] public partial decimal? RetryCount { get; set; }
    [ObservableProperty] public partial bool OnlyOnAcPower { get; set; }
    [ObservableProperty] public partial bool OnlyWhenIdle { get; set; }
    [ObservableProperty] public partial bool OnlyWithNetwork { get; set; }
    [ObservableProperty] public partial bool WakeToRun { get; set; }
    [ObservableProperty] public partial bool AllowRunOnDemand { get; set; }
    [ObservableProperty] public partial bool Hidden { get; set; }

    // ---- The review beside the form ----
    [ObservableProperty] public partial IReadOnlyList<string> Changes { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<string> Problems { get; private set; } = [];
    [ObservableProperty] public partial ReviewTab Tab { get; set; } = ReviewTab.Changes;
    [ObservableProperty] public partial string Script { get; private set; } = "";
    [ObservableProperty] public partial string Xml { get; private set; } = "";
    [ObservableProperty] public partial string? SaveProblem { get; private set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] public partial bool Busy { get; private set; }

    public bool NeedsPassword => Logon.Kind is LogonKind.Password or LogonKind.InteractiveOrPassword;
    public bool RunAsEditable => true;
    public string RunAsHint => Logon.Kind == LogonKind.ServiceAccount ? "SYSTEM, LOCAL SERVICE or NETWORK SERVICE" : @"DOMAIN\name or name@domain";
    public bool HasProblems => Problems.Count > 0;
    public bool NoChanges => !IsNew && Changes.Count == 0 && Problems.Count == 0;
    public bool ShowChangesTab => Tab == ReviewTab.Changes;
    public bool ShowScriptTab => Tab == ReviewTab.PowerShell;
    public bool ShowXmlTab => Tab == ReviewTab.Xml;
    public bool HasSaveProblem => SaveProblem is not null;
    public string SaveText => IsNew ? "Create Task" : "Save Changes";
    /// <summary>Whether the person can save here: their own task, or the app runs as administrator.</summary>
    public bool CanChange => _shell.IsElevated || Accounts.Same(RunAs, _shell.CurrentUser)
                             && (_original is null || Accounts.Same(_original.RunAs, _shell.CurrentUser));
    public string? RightsNote => CanChange ? null
        : "Saving a task that runs as another account needs administrator rights. Use Restart as Administrator first; this edit will be lost.";

    partial void OnProblemsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(NoChanges));
    }
    partial void OnChangesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(NoChanges));
    partial void OnSaveProblemChanged(string? value) => OnPropertyChanged(nameof(HasSaveProblem));
    partial void OnTabChanged(ReviewTab value)
    {
        OnPropertyChanged(nameof(ShowChangesTab));
        OnPropertyChanged(nameof(ShowScriptTab));
        OnPropertyChanged(nameof(ShowXmlTab));
        RefreshPreview();
    }
    partial void OnLogonChanged(LogonChoice value)
    {
        OnPropertyChanged(nameof(NeedsPassword));
        OnPropertyChanged(nameof(RunAsHint));
        if (_loading)
            return;
        // Built-in accounts have fixed names; switching back to a person suggests the current user.
        if (value.Kind == LogonKind.ServiceAccount && !IsServiceAccount(RunAs))
            RunAs = "SYSTEM";
        else if (value.Kind != LogonKind.ServiceAccount && IsServiceAccount(RunAs))
            RunAs = _shell.CurrentUser;
    }

    static bool IsServiceAccount(string name) =>
        name.Trim().ToUpperInvariant() is "SYSTEM" or "NT AUTHORITY\\SYSTEM" or "LOCAL SERVICE" or "NT AUTHORITY\\LOCAL SERVICE" or "NETWORK SERVICE" or "NT AUTHORITY\\NETWORK SERVICE";

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is nameof(Name) or nameof(Folder) or nameof(Description) or nameof(Enabled) or nameof(RunAs)
                or nameof(Logon) or nameof(HighestPrivileges) or nameof(LimitRunTime) or nameof(MultipleInstances) or nameof(RunMissedStart)
                or nameof(RetryOnFailure) or nameof(RetryCount) or nameof(OnlyOnAcPower) or nameof(OnlyWhenIdle) or nameof(OnlyWithNetwork)
                or nameof(WakeToRun) or nameof(AllowRunOnDemand) or nameof(Hidden) or nameof(Password))
            Refresh();
    }

    DurationEditor Watch(DurationEditor duration)
    {
        duration.Attach(Refresh);
        return duration;
    }

    [RelayCommand]
    void AddTrigger() => AddTriggerEditor(new TriggerEditor(null, RemoveTrigger));

    [RelayCommand]
    void AddAction() => AddActionEditor(new ActionEditor(null, RemoveAction));

    void AddTriggerEditor(TriggerEditor editor)
    {
        editor.Bind(Refresh);
        Triggers.Add(editor);
        Renumber();
    }

    void AddActionEditor(ActionEditor editor)
    {
        editor.Attach(Refresh);
        Actions.Add(editor);
        Renumber();
    }

    void RemoveTrigger(TriggerEditor editor)
    {
        Triggers.Remove(editor);
        Renumber();
    }

    void RemoveAction(ActionEditor editor)
    {
        Actions.Remove(editor);
        Renumber();
    }

    void Renumber()
    {
        for (var i = 0; i < Triggers.Count; i++)
            Triggers[i].Number = i + 1;
        for (var i = 0; i < Actions.Count; i++)
            Actions[i].Number = i + 1;
        OnPropertyChanged(nameof(NoTriggers));
        Refresh();
    }

    [RelayCommand]
    void ShowTab(ReviewTab tab) => Tab = tab;

    [RelayCommand]
    void CopyPreview() => Platform.CopyText(Tab == ReviewTab.Xml ? Xml : Script);

    /// <summary>The form as a draft.</summary>
    public TaskDraft ToDraft() => new()
    {
        OriginalPath = _original?.Path,
        Folder = NormaliseFolder(Folder),
        Name = Name.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Enabled = Enabled,
        RunAs = RunAs.Trim(),
        Logon = Logon.Kind,
        HighestPrivileges = HighestPrivileges,
        Triggers = Triggers.Select(t => t.ToDraft()).ToList(),
        Actions = Actions.Select(a => a.ToDraft()).ToList(),
        Settings = _settingsBase with
        {
            StopAfter = LimitRunTime ? StopAfter.Span : null,
            MultipleInstances = MultipleInstances.Value,
            RunMissedStart = RunMissedStart,
            RestartOnFailure = RetryOnFailure,
            RestartCount = RetryOnFailure ? (int)Math.Max(1, RetryCount ?? 1) : 0,
            RestartInterval = RetryOnFailure ? RetryEvery.Span : TimeSpan.Zero,
            OnlyOnAcPower = OnlyOnAcPower,
            OnlyWhenIdle = OnlyWhenIdle,
            OnlyWithNetwork = OnlyWithNetwork,
            WakeToRun = WakeToRun,
            AllowRunOnDemand = AllowRunOnDemand,
            Hidden = Hidden,
        },
    };

    /// <summary>"Backups", "\Backups\" and "\backups" all mean \Backups; empty means the top folder.</summary>
    public static string NormaliseFolder(string folder)
    {
        var trimmed = folder.Trim().Trim('\\');
        return trimmed.Length == 0 ? "\\" : "\\" + trimmed;
    }

    void Refresh()
    {
        if (_loading)
            return;
        var draft = ToDraft();
        var problems = draft.Problems().ToList();
        if (draft.NeedsPassword && string.IsNullOrEmpty(Password))
            problems.Add("Enter the account's password: Windows needs it to run the task while the account is signed out.");
        Problems = problems;
        Changes = DraftChanges.Describe(_original, draft);
        OnPropertyChanged(nameof(CanChange));
        OnPropertyChanged(nameof(RightsNote));
        SaveCommand.NotifyCanExecuteChanged();
        RefreshPreview();
    }

    void RefreshPreview()
    {
        if (Tab == ReviewTab.Changes)
            return;
        var draft = ToDraft();
        try
        {
            Xml = draft.Problems().Count == 0 ? _shell.Host.BuildXml(draft) : "Fix the problems listed under Changes to see the XML.";
        }
        catch (Exception e)
        {
            Xml = $"Couldn't build the XML: {e.Message}";
        }
        Script = draft.Problems().Count == 0 ? PowerShellScript.For(draft, Xml) : "Fix the problems listed under Changes to see the PowerShell.";
    }

    bool CanSave() => !Busy && Problems.Count == 0 && (IsNew || Changes.Count > 0) && CanChange;

    [RelayCommand(CanExecute = nameof(CanSave))]
    async Task SaveAsync()
    {
        Busy = true;
        SaveProblem = null;
        var draft = ToDraft();
        var password = NeedsPassword ? Password : null;
        try
        {
            await Platform.Background(() =>
            {
                _shell.Host.Save(draft, password);
                return Task.CompletedTask;
            });
            Password = "";
            _shell.CloseEditor(draft.Path);
        }
        catch (UnauthorizedAccessException)
        {
            SaveProblem = "Windows refused: saving this task needs administrator rights.";
        }
        catch (Exception e)
        {
            SaveProblem = $"Couldn't save: {e.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    void Cancel()
    {
        Password = "";
        _shell.CloseEditor(null);
    }
}
