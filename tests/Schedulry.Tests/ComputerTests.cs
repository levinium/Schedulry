using System.Runtime.InteropServices;
using Schedulry.Core;
using Schedulry.Demo;
using Schedulry.ViewModels;
using Schedulry.ViewModels.Editor;
using Schedulry.Windows;

namespace Schedulry.Tests;

public class ComputerTests
{
    readonly DateTime _now = DateTime.Today.AddHours(10);
    readonly List<ConnectRequest> _requests = [];

    public ComputerTests()
    {
        Platform.Background = work => work();
        Platform.Connect = request =>
        {
            lock (_requests)
                _requests.Add(request);
            return DemoSchedulerHost.Connect(request, () => _now);
        };
    }

    MainViewModel Open(ISchedulerHost? host = null, IComputerStore? store = null)
    {
        var vm = new MainViewModel(host ?? new DemoSchedulerHost(() => _now), new MonitorTiming { StatusEveryTicks = 1, FullEveryTicks = 1000 },
            () => _now, computers: store ?? new MemoryComputerStore());
        vm.Monitor.Step();
        return vm;
    }

    [Theory]
    [InlineData(@"\\FILESRV01\", "FILESRV01")]
    [InlineData("  build-02 ", "build-02")]
    [InlineData("", null)]
    [InlineData(@"\\", null)]
    public void Names_are_normalised(string typed, string? expected) => Assert.Equal(expected, ComputerNames.Normalise(typed));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData(".", true)]
    [InlineData("contoso-ws01", true)]
    [InlineData("CONTOSO-WS01.contoso.com", true)]
    [InlineData("CONTOSO-WS012", false)]
    [InlineData("FILESRV01", false)]
    public void Names_for_this_computer_are_recognised(string name, bool local) =>
        Assert.Equal(local, ComputerNames.IsThisComputer(name, "CONTOSO-WS01"));

    [Fact]
    public void The_list_keeps_the_latest_first_without_duplicates()
    {
        var list = new List<SavedComputer>();
        list = ComputerList.Remember(list, new("FILESRV01"));
        list = ComputerList.Remember(list, new("BUILD-02"));
        list = ComputerList.Remember(list, new(@"\\filesrv01", @"CONTOSO\ops.admin"));   // same computer, now with an account
        Assert.Equal([new SavedComputer(@"\\filesrv01", @"CONTOSO\ops.admin"), new SavedComputer("BUILD-02")], list);
        for (var i = 0; i < 30; i++)
            list = ComputerList.Remember(list, new($"PC{i}"));
        Assert.Equal(ComputerList.Max, list.Count);
        Assert.Empty(ComputerList.Forget([new("BUILD-02")], "build-02"));
    }

    [Fact]
    public void Connecting_switches_to_the_other_computer_and_remembers_it()
    {
        var store = new MemoryComputerStore();
        using var vm = Open(store: store);
        ISchedulerHost? switched = null;
        vm.SwitchRequested += host => switched = host;
        vm.ToggleComputersCommand.Execute(null);
        var computers = vm.Computers;
        Assert.False(computers.ConnectCommand.CanExecute(null));
        Assert.Equal("Type a computer's name to connect.", computers.ConnectBlocked);

        computers.Name = @"\\filesrv01";
        computers.ConnectCommand.Execute(null);

        using var remote = Assert.IsType<DemoSchedulerHost>(switched);
        Assert.True(remote.IsRemote);
        Assert.Equal("FILESRV01", remote.ComputerName);
        Assert.False(vm.ShowComputers);   // the card closes
        Assert.Null(computers.Problem);
        Assert.Equal([new SavedComputer("FILESRV01")], store.Load());
        Assert.Equal("", computers.Name);
    }

    [Fact]
    public void Another_account_needs_a_name_and_password_which_is_never_kept()
    {
        var store = new MemoryComputerStore();
        using var vm = Open(store: store);
        ISchedulerHost? switched = null;
        vm.SwitchRequested += host => switched = host;
        var computers = vm.Computers;
        computers.Name = "BUILD-02";
        computers.UseOtherAccount = true;
        Assert.Equal("Type the account to sign in as.", computers.ConnectBlocked);
        computers.Account = @"CONTOSO\ops.admin";
        Assert.Equal("Type the account's password.", computers.ConnectBlocked);
        computers.Password = "not-a-real-password";
        Assert.True(computers.ConnectCommand.CanExecute(null));
        computers.ConnectCommand.Execute(null);

        Assert.Equal("not-a-real-password", _requests.Single().Password);   // handed over once
        Assert.Equal("", computers.Password);
        using var _ = switched;
        Assert.Equal(@"CONTOSO\ops.admin", switched!.CurrentUser);
        var saved = Assert.Single(store.Load());
        Assert.Equal(new SavedComputer("BUILD-02", @"CONTOSO\ops.admin"), saved);   // the account, not the password
    }

    [Fact]
    public void A_computer_that_does_not_answer_explains_why_and_stays_put()
    {
        var store = new MemoryComputerStore();
        using var vm = Open(store: store);
        var switched = false;
        vm.SwitchRequested += _ => switched = true;
        vm.Computers.Name = "offline-pc";
        vm.Computers.ConnectCommand.Execute(null);
        Assert.False(switched);
        Assert.StartsWith("OFFLINE-PC didn't answer", vm.Computers.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.Computers.IsConnecting);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void A_saved_computer_with_an_account_asks_for_the_password_first()
    {
        using var vm = Open(store: new MemoryComputerStore([new SavedComputer("BUILD-02", @"CONTOSO\ops.admin"), new SavedComputer("FILESRV01")]));
        var computers = vm.Computers;
        Assert.Equal(["This computer", "BUILD-02", "FILESRV01"], computers.Rows.Select(r => r.Title));
        Assert.True(computers.Rows[0].IsCurrent);
        Assert.False(computers.ConnectToCommand.CanExecute(computers.Rows[0]));   // already showing it

        computers.ConnectToCommand.Execute(computers.Rows[1]);
        Assert.Empty(_requests);   // nothing tried without the password
        Assert.Equal("BUILD-02", computers.Name);
        Assert.True(computers.UseOtherAccount);
        Assert.Equal(@"CONTOSO\ops.admin", computers.Account);
        Assert.Equal(@"Type the password for CONTOSO\ops.admin, then Connect.", computers.Note);

        computers.ForgetCommand.Execute(computers.Rows[2]);
        Assert.Equal(["This computer", "BUILD-02"], computers.Rows.Select(r => r.Title));
    }

    [Fact]
    public void On_another_computer_the_list_marks_it_and_offers_this_one()
    {
        using var vm = Open(DemoSchedulerHost.Connect(new ConnectRequest("FILESRV01"), () => _now),
            new MemoryComputerStore([new SavedComputer("FILESRV01")]));
        Assert.True(vm.IsRemote);
        Assert.True(vm.IsElevated);   // an administrator there, or it wouldn't have opened
        Assert.False(vm.ShowAdminBanner);
        var rows = vm.Computers.Rows;
        Assert.False(rows[0].IsCurrent);
        Assert.True(rows[1].IsCurrent);
        Assert.False(rows[1].CanForget);

        ISchedulerHost? switched = null;
        vm.SwitchRequested += host => switched = host;
        vm.Computers.ConnectToCommand.Execute(rows[0]);
        using var _ = switched;
        Assert.False(switched!.IsRemote);
        Assert.Same(ConnectRequest.ThisComputer, _requests.Single());
    }

    [Fact]
    public void Typing_this_computers_name_opens_it_directly_and_is_not_saved()
    {
        var store = new MemoryComputerStore();
        using var vm = Open(store: store);
        ISchedulerHost? switched = null;
        vm.SwitchRequested += host => switched = host;
        vm.Computers.Name = "localhost";
        vm.Computers.ConnectCommand.Execute(null);
        using var _ = switched;
        Assert.False(switched!.IsRemote);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void History_on_another_computer_turns_on_without_a_prompt()
    {
        var host = new DemoSchedulerHost(() => _now, computer: "FILESRV01");
        using var vm = Open(host);
        Assert.Contains("off on FILESRV01", vm.HistoryBannerText);
        vm.TurnOnHistoryCommand.Execute(null);
        Assert.True(host.HistoryEnabled);
        Assert.True(vm.HistoryEnabled);
        Assert.Null(vm.BannerProblem);
    }

    [Fact]
    public void Browse_is_off_on_another_computer_and_says_why()
    {
        using var vm = Open(new DemoSchedulerHost(() => _now, computer: "FILESRV01"));
        vm.NewTaskCommand.Execute(null);
        var action = vm.Editor!.Actions[0];
        Assert.False(action.BrowseCommand.CanExecute(null));
        Assert.Equal("Type the path as it is on FILESRV01. Browse only sees the files on this computer.", action.BrowseNote);

        using var local = Open();
        local.NewTaskCommand.Execute(null);
        Assert.True(local.Editor!.Actions[0].BrowseCommand.CanExecute(null));
        Assert.Null(local.Editor.Actions[0].BrowseNote);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070035), "Couldn't find SERVER01 on the network.")]
    [InlineData(unchecked((int)0x800706BA), "SERVER01 didn't answer.")]
    [InlineData(unchecked((int)0x8007052E), "SERVER01 didn't accept that account name and password.")]
    [InlineData(unchecked((int)0x80070005), "SERVER01 refused your account.")]
    [InlineData(unchecked((int)0x80004005), "Couldn't connect to SERVER01:")]
    public void Connection_failures_are_put_into_words(int code, string start) =>
        Assert.StartsWith(start, WindowsSchedulerHost.Describe(new COMException("x", code), "SERVER01", null));

    [Fact]
    public void Access_denied_names_the_account_used()
    {
        Assert.StartsWith(@"SERVER01 refused CONTOSO\ops.admin.",
            WindowsSchedulerHost.Describe(new UnauthorizedAccessException(), "SERVER01", @"CONTOSO\ops.admin"));
    }
}
