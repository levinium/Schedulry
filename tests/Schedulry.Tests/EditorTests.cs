using System.Globalization;
using Schedulry.Core;
using Schedulry.Demo;
using Schedulry.ViewModels;
using Schedulry.ViewModels.Editor;

namespace Schedulry.Tests;

public class EditorTests
{
    DateTime _now = DateTime.Today.AddHours(10);

    public EditorTests()
    {
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        Platform.Background = work => work();
    }

    MainViewModel Open(bool elevated = true)
    {
        var vm = new MainViewModel(new DemoSchedulerHost(() => _now, elevated), new MonitorTiming { StatusEveryTicks = 1, FullEveryTicks = 1000 }, () => _now);
        vm.Monitor.Step();
        return vm;
    }

    [Fact]
    public void Opening_a_task_changes_nothing_until_edited()
    {
        using var vm = Open();
        vm.EditTask(vm.Rows.Single(r => r.Name == "Clear temp folders").Task);
        var editor = vm.Editor!;
        Assert.Empty(editor.Changes);
        Assert.Empty(editor.Problems);
        Assert.False(editor.SaveCommand.CanExecute(null));   // nothing to save
        Assert.True(editor.NoChanges);
    }

    [Fact]
    public void The_folder_is_picked_from_a_list_or_made_inside_one()
    {
        using var vm = Open();
        vm.NewTaskCommand.Execute(null);
        var editor = vm.Editor!;
        var picker = editor.FolderPicker;
        Assert.Equal("\\", editor.Folder);
        Assert.Equal("Top level", picker.Selected.Label);
        Assert.Contains(picker.Choices, c => c.Path == @"\Maintenance" && c.Title == "Maintenance" && c.Depth == 1);

        picker.Selected = picker.Choices.Single(c => c.Path == @"\Maintenance");
        Assert.Equal(@"\Maintenance", editor.Folder);

        // A new folder inside the chosen one; Cancel goes back to the chosen one.
        picker.StartNewFolderCommand.Execute(null);
        picker.NewName = @" Nightly\ ";
        Assert.Equal(@"\Maintenance\Nightly", editor.Folder);
        Assert.Contains(@"\Maintenance", picker.NewFolderNote);
        picker.CancelNewFolderCommand.Execute(null);
        Assert.Equal(@"\Maintenance", editor.Folder);

        // A path set elsewhere (a copied task's, say) that doesn't exist: deepest existing parent, rest is new.
        editor.Folder = @"\Maintenance\A\B";
        Assert.Equal(@"\Maintenance", picker.Selected.Path);
        Assert.True(picker.IsCreating);
        Assert.Equal(@"A\B", picker.NewName);
        Assert.Equal(@"\Maintenance\A\B", editor.Folder);

        editor.Folder = "reports";
        Assert.False(picker.IsCreating);
        Assert.Equal(@"\Reports", picker.Selected.Path);
    }

    [Fact]
    public void Edits_are_listed_in_words_and_saved()
    {
        using var vm = Open();
        vm.EditTask(vm.Rows.Single(r => r.Name == "Clear temp folders").Task);
        var editor = vm.Editor!;
        editor.Triggers[0].Saturday = true;
        editor.Description = "Empties temp folders on Sunday nights.";
        editor.RetryOnFailure = true;

        Assert.Contains(editor.Changes, c => c.StartsWith("Change trigger 1 to: Every Saturday and Sunday at 3:00 AM"));
        Assert.Contains("Change the description.", editor.Changes);
        Assert.Contains(editor.Changes, c => c.StartsWith("If it fails, try again every 5 minutes, up to 1 times"));
        Assert.True(editor.SaveCommand.CanExecute(null));

        editor.SaveCommand.Execute(null);
        Assert.Null(vm.Editor);
        vm.Monitor.Step();
        var saved = vm.Rows.Single(r => r.Name == "Clear temp folders");
        Assert.Equal(Weekdays.Saturday | Weekdays.Sunday, saved.Task.Triggers[0].Days);
        Assert.Equal("Empties temp folders on Sunday nights.", saved.Task.Description);
        Assert.Same(saved, vm.SelectedRow);   // the saved task is selected
    }

    [Fact]
    public void Renaming_into_another_folder_moves_the_task()
    {
        using var vm = Open();
        vm.EditTask(vm.Rows.Single(r => r.Name == "Clear temp folders").Task);
        var editor = vm.Editor!;
        editor.Name = "Clean temp";
        editor.Folder = "Maintenance";   // without slashes is fine
        Assert.Contains("Rename it from \"Clear temp folders\" to \"Clean temp\".", editor.Changes);
        Assert.Contains(editor.Changes, c => c.StartsWith("Move it from the top folder to the folder \\Maintenance."));
        editor.SaveCommand.Execute(null);
        vm.Monitor.Step();
        Assert.DoesNotContain(vm.Rows, r => r.Path == @"\Clear temp folders");
        Assert.Equal(@"\Maintenance\Clean temp", vm.SelectedRow!.Path);
    }

    [Fact]
    public void A_new_task_needs_a_name_and_a_program()
    {
        using var vm = Open();
        vm.NewTaskCommand.Execute(null);
        var editor = vm.Editor!;
        Assert.Contains("Give the task a name.", editor.Problems);
        Assert.Contains("Action 1 needs a program to run.", editor.Problems);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.Name = "Say hello";
        editor.Actions[0].Program = @"C:\Windows\System32\cmd.exe";
        editor.Actions[0].Arguments = "/c echo hello";
        Assert.Empty(editor.Problems);
        Assert.Equal(["Create the task \"Say hello\" in the top folder."], editor.Changes);
        editor.SaveCommand.Execute(null);
        vm.Monitor.Step();
        Assert.Equal(@"\Say hello", vm.SelectedRow!.Path);
    }

    [Fact]
    public void Stored_password_logons_ask_for_it_and_never_keep_it()
    {
        using var vm = Open();
        vm.EditTask(vm.Rows.Single(r => r.Name == "Clear temp folders").Task);
        var editor = vm.Editor!;
        editor.Logon = editor.LogonChoices.Single(c => c.Kind == LogonKind.Password);
        Assert.True(editor.NeedsPassword);
        Assert.Contains(editor.Problems, p => p.StartsWith("Enter the account's password"));
        editor.Password = "not-a-real-password";
        Assert.Empty(editor.Problems);
        editor.SaveCommand.Execute(null);
        Assert.Equal("", editor.Password);   // cleared once handed over
    }

    [Fact]
    public void Switching_to_a_built_in_account_fills_in_SYSTEM()
    {
        using var vm = Open();
        vm.NewTaskCommand.Execute(null);
        var editor = vm.Editor!;
        editor.Logon = editor.LogonChoices.Single(c => c.Kind == LogonKind.ServiceAccount);
        Assert.Equal("SYSTEM", editor.RunAs);
        editor.Logon = editor.LogonChoices.Single(c => c.Kind == LogonKind.Interactive);
        Assert.Equal(@"CONTOSO\avery.lee", editor.RunAs);
    }

    [Fact]
    public void Without_admin_another_accounts_task_can_be_looked_at_but_not_saved()
    {
        using var vm = Open(elevated: false);
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Mirror data to NAS");
        Assert.False(vm.Detail!.EditCommand.CanExecute(null));
        vm.Detail.CopyCommand.Execute(null);   // a copy is allowed: it would run as you
        var editor = vm.Editor!;
        Assert.Equal("Mirror data to NAS (copy)", editor.Name);
        Assert.NotNull(editor.RightsNote);   // still runs as the service account
        editor.RunAs = @"CONTOSO\avery.lee";
        editor.Logon = editor.LogonChoices.Single(c => c.Kind == LogonKind.Interactive);
        Assert.Null(editor.RightsNote);
    }

    [Fact]
    public void Trigger_editor_round_trips_every_editable_kind()
    {
        var start = new DateTime(2026, 3, 2, 6, 30, 0);
        TriggerSpec[] specs =
        [
            new() { Kind = TriggerKind.Once, Start = start },
            new() { Kind = TriggerKind.Daily, Interval = 2, Start = start, RepeatEvery = TimeSpan.FromMinutes(15), RepeatFor = TimeSpan.FromHours(3) },
            new() { Kind = TriggerKind.Weekly, Days = Weekdays.Workdays, Start = start, End = start.AddMonths(6) },
            new() { Kind = TriggerKind.Monthly, DaysOfMonth = 1 | 1u << 14, LastDayOfMonth = true, Months = MonthSet.All, Start = start },
            new() { Kind = TriggerKind.MonthlyDayOfWeek, Weeks = WeekOfMonth.First | WeekOfMonth.Last, Days = Weekdays.Saturday, Months = MonthSet.All, Start = start },
            new() { Kind = TriggerKind.AtLogOn, UserId = @"CONTOSO\avery.lee", Delay = TimeSpan.FromMinutes(2) },
            new() { Kind = TriggerKind.AtStartup, RepeatEvery = TimeSpan.FromHours(1) },
            new() { Kind = TriggerKind.OnEvent, EventLog = "System", EventSource = "disk", EventId = 153 },
            new() { Kind = TriggerKind.SessionStateChange, SessionChange = "SessionUnlock" },
        ];
        foreach (var spec in specs)
        {
            var editor = new TriggerEditor(new DraftTrigger(spec, 0), _ => { });
            Assert.Equal(spec, editor.ToSpec());
        }
    }

    [Fact]
    public void A_custom_trigger_is_kept_untouched()
    {
        var custom = new TriggerSpec { Kind = TriggerKind.Custom };
        var editor = new TriggerEditor(new DraftTrigger(custom, 3), _ => { });
        Assert.True(editor.IsKept);
        Assert.Equal(new DraftTrigger(custom, 3), editor.ToDraft());
    }

    [Fact]
    public void PowerShell_quotes_safely_and_falls_back_to_xml()
    {
        Assert.Equal("'it''s'", PowerShellScript.Quote("it's"));
        Assert.Equal("'a\u2019\u2019b'", PowerShellScript.Quote("a\u2019b"));   // typographic quote doubled too
        Assert.Equal("'$env:TEMP'", PowerShellScript.Quote("$env:TEMP"));       // no expansion in single quotes

        var draft = new TaskDraft
        {
            Folder = @"\Backups",
            Name = "Copy",
            RunAs = "SYSTEM",
            Logon = LogonKind.ServiceAccount,
            Triggers = [new DraftTrigger(new TriggerSpec { Kind = TriggerKind.Weekly, Days = Weekdays.Monday | Weekdays.Friday, Start = new DateTime(2026, 1, 5, 1, 0, 0) })],
            Actions = [new DraftAction(new ActionInfo(ActionKind.Program, @"C:\Windows\System32\robocopy.exe", @"D:\Data E:\Copy /MIR"))],
        };
        var script = PowerShellScript.For(draft, "<Task />");
        Assert.Contains("-DaysOfWeek Monday,Friday", script);
        Assert.Contains(@"-TaskPath '\Backups\'", script);
        Assert.Contains("-LogonType ServiceAccount", script);
        Assert.DoesNotContain("-Xml", script);

        var monthly = draft with { Triggers = [new DraftTrigger(new TriggerSpec { Kind = TriggerKind.Monthly, DaysOfMonth = 1, Start = DateTime.Today })] };
        Assert.Contains("-Xml $xml", PowerShellScript.For(monthly, "<Task />"));
    }

    [Theory]
    [InlineData(@"AzureAD\avery.lee", "avery.lee", true)]        // Windows stores the bare name
    [InlineData(@"CONTOSO\Avery.Lee", @"contoso\avery.lee", true)]
    [InlineData("avery.lee@contoso.com", @"CONTOSO.COM\avery.lee", false)]   // two domains: not assumed equal
    [InlineData("avery.lee@contoso.com", "avery.lee", true)]
    [InlineData(@"CONTOSO\avery.lee", @"FABRIKAM\avery.lee", false)]
    [InlineData(@"CONTOSO\avery.lee", "avery", false)]
    [InlineData("", "", false)]
    public void Account_names_match_with_or_without_a_domain(string a, string b, bool same) =>
        Assert.Equal(same, Accounts.Same(a, b));

    [Fact]
    public void Your_own_task_stored_without_a_domain_can_be_changed_without_admin()
    {
        using var vm = Open(elevated: false);
        vm.NewTaskCommand.Execute(null);
        var editor = vm.Editor!;
        editor.Name = "Mine";
        editor.Actions[0].Program = "cmd.exe";
        editor.RunAs = "avery.lee";   // as Windows reads it back
        Assert.Null(editor.RightsNote);
        editor.SaveCommand.Execute(null);
        vm.Monitor.Step();
        Assert.True(vm.Detail!.EditCommand.CanExecute(null));
        Assert.True(vm.Detail.RunCommand.CanExecute(null));
    }

    [Fact]
    public void Folder_names_are_normalised()
    {
        Assert.Equal("\\", TaskEditorViewModel.NormaliseFolder(""));
        Assert.Equal("\\", TaskEditorViewModel.NormaliseFolder("\\"));
        Assert.Equal(@"\Backups", TaskEditorViewModel.NormaliseFolder("Backups"));
        Assert.Equal(@"\Backups\Nightly", TaskEditorViewModel.NormaliseFolder(@"\Backups\Nightly\"));
    }

    [Fact]
    public void Import_registers_the_xml_under_the_chosen_name()
    {
        using var vm = Open();
        var import = new ImportViewModel(vm, "Say hello.xml",
            "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec><Command>cmd.exe</Command><Arguments>/c echo hi</Arguments></Exec></Actions></Task>",
            "\\");
        Assert.Equal("Say hello", import.Name);
        Assert.False(import.NeedsPassword);
        import.ImportCommand.Execute(null);
        vm.Monitor.Step();
        Assert.Equal(@"\Say hello", vm.SelectedRow!.Path);
    }
}
