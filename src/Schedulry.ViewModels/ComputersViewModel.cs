using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Schedulry.Core;

namespace Schedulry.ViewModels;

/// <summary>A row in the Computers card: this computer, or one connected to before.</summary>
public sealed record ComputerRow(string Title, string Detail, SavedComputer? Saved, bool IsCurrent, bool HasSavedPassword = false)
{
    public bool IsThisComputer => Saved is null;
    public bool CanConnect => !IsCurrent;
    public bool CanForget => Saved is not null && !IsCurrent;
}

/// <summary>
/// The Computers card: switch to this computer or one used before, or connect to another by name,
/// optionally as another account. Connecting happens off the UI thread and can be abandoned; the
/// window switches only once the other computer has answered.
/// </summary>
public sealed partial class ComputersViewModel : ObservableObject
{
    readonly MainViewModel _shell;
    readonly IComputerStore _store;
    readonly IPasswordVault _vault;
    List<SavedComputer> _saved;
    int _attempt;

    public ComputersViewModel(MainViewModel shell, IComputerStore store, IPasswordVault vault)
    {
        _shell = shell;
        _store = store;
        _vault = vault;
        _saved = store.Load();
        RebuildRows();
    }

    public ObservableCollection<ComputerRow> Rows { get; } = [];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))] public partial string Name { get; set; } = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))] public partial bool UseOtherAccount { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))] public partial string Account { get; set; } = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))] public partial string Password { get; set; } = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))] public partial bool IsConnecting { get; private set; }
    /// <summary>Keep the password in Windows Credential Manager for next time (this PC only).</summary>
    [ObservableProperty] public partial bool RememberPassword { get; set; }
    [ObservableProperty] public partial string ConnectingText { get; private set; } = "";
    [ObservableProperty] public partial string? Problem { get; private set; }
    [ObservableProperty] public partial string? Note { get; private set; }

    public bool HasProblem => Problem is not null;
    public bool HasNote => Note is not null;
    public bool IsIdle => !IsConnecting;

    /// <summary>Why Connect is greyed out, shown beside it; null when it can be pressed.</summary>
    public string? ConnectBlocked =>
        IsConnecting ? null
        : string.IsNullOrWhiteSpace(Name) ? "Type a computer's name to connect."
        : UseOtherAccount && string.IsNullOrWhiteSpace(Account) ? "Type the account to sign in as."
        : UseOtherAccount && Password.Length == 0 ? "Type the account's password."
        : null;

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));
    partial void OnNoteChanged(string? value) => OnPropertyChanged(nameof(HasNote));
    partial void OnIsConnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(ConnectBlocked));
        ConnectToCommand.NotifyCanExecuteChanged();
    }
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(ConnectBlocked));
    partial void OnUseOtherAccountChanged(bool value)
    {
        if (!value)
        {
            Password = "";
            RememberPassword = false;
        }
        OnPropertyChanged(nameof(ConnectBlocked));
    }
    partial void OnAccountChanged(string value) => OnPropertyChanged(nameof(ConnectBlocked));
    partial void OnPasswordChanged(string value) => OnPropertyChanged(nameof(ConnectBlocked));

    bool CanConnect() => !IsConnecting && ConnectBlocked is null;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    Task ConnectAsync() => AttemptAsync(new ConnectRequest(
        ComputerNames.Normalise(Name),
        UseOtherAccount ? Account.Trim() : null,
        UseOtherAccount ? Password : null));

    bool CanConnectTo(ComputerRow? row) => row is { CanConnect: true } && !IsConnecting;

    /// <summary>A row's Connect. One saved with another account fills in the form for its password.</summary>
    [RelayCommand(CanExecute = nameof(CanConnectTo))]
    Task ConnectToAsync(ComputerRow? row)
    {
        if (row is null)
            return Task.CompletedTask;
        if (row.Saved is null)
            return AttemptAsync(ConnectRequest.ThisComputer);
        if (row.Saved.Account is { } account)
        {
            if (ReadSaved(row.Saved.Name, account) is { } saved)
                return AttemptAsync(new ConnectRequest(row.Saved.Name, account, saved), fromVault: true);
            AskForPassword(row.Saved.Name, account, $"Type the password for {account}, then Connect.");
            return Task.CompletedTask;
        }
        return AttemptAsync(new ConnectRequest(row.Saved.Name));
    }

    [RelayCommand]
    void Forget(ComputerRow? row)
    {
        if (row?.Saved is null)
            return;
        _saved = ComputerList.Forget(_saved, row.Saved.Name);
        SaveList();
        DeleteSaved(row.Saved.Name);
        RebuildRows();
    }

    /// <summary>Stops waiting. Windows can't be interrupted mid-call, so a late answer is closed quietly.</summary>
    [RelayCommand]
    void CancelConnect()
    {
        _attempt++;
        IsConnecting = false;
        Note = "Stopped connecting.";
    }

    async Task AttemptAsync(ConnectRequest request, bool fromVault = false)
    {
        var remember = !fromVault && RememberPassword && request.Account is not null;
        var attempt = ++_attempt;
        Problem = null;
        Note = null;
        IsConnecting = true;
        ConnectingText = $"Connecting to {request}…";
        ISchedulerHost? host = null;
        string? problem = null;
        await Platform.Background(() =>
        {
            try
            {
                host = Platform.Connect(request);
            }
            catch (Exception e)
            {
                problem = e.Message;
            }
            return Task.CompletedTask;
        });
        Password = "";   // handed to Windows; not kept here, whatever happened
        if (attempt != _attempt)
        {
            host?.Dispose();
            return;
        }
        IsConnecting = false;
        if (host is null)
        {
            if (fromVault)
            {
                // The saved password may be out of date: ask for the current one, ready to save again.
                AskForPassword(request.Computer!, request.Account!, null);
                RememberPassword = true;
                problem += " The saved password was used; type the current one.";
            }
            Problem = problem;
            return;
        }
        if (host.IsRemote)
        {
            _saved = ComputerList.Remember(_saved, new SavedComputer(host.ComputerName, request.Account));
            SaveList();
            // Kept only when asked; connecting without ticking Remember drops an old one.
            if (remember)
                SavePassword(host.ComputerName, request.Account!, request.Password ?? "");
            else if (!fromVault)
                DeleteSaved(host.ComputerName);
        }
        Name = "";
        UseOtherAccount = false;
        Account = "";
        RememberPassword = false;
        _shell.SwitchTo(host);
    }

    void AskForPassword(string computer, string account, string? note)
    {
        Name = computer;
        UseOtherAccount = true;
        Account = account;
        Password = "";
        Problem = null;
        Note = note;
    }

    string? ReadSaved(string computer, string account)
    {
        try
        {
            return _vault.Read(computer, account);
        }
        catch (Exception e)
        {
            Problem = $"Couldn't read the saved password: {e.Message}";
            return null;
        }
    }

    void SavePassword(string computer, string account, string password)
    {
        try
        {
            _vault.Save(computer, account, password);
        }
        catch (Exception e)
        {
            Problem = $"Connected, but couldn't save the password: {e.Message}";
        }
    }

    void DeleteSaved(string computer)
    {
        try
        {
            _vault.Delete(computer);
        }
        catch (Exception e)
        {
            Problem = $"Couldn't remove the saved password: {e.Message}";
        }
    }

    bool HasSaved(string computer, string account)
    {
        try
        {
            return _vault.Read(computer, account) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    void SaveList()
    {
        try
        {
            _store.Save(_saved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Problem = $"Couldn't save the list of computers: {e.Message}";
        }
    }

    void RebuildRows()
    {
        Rows.Clear();
        var remote = _shell.Host.IsRemote;
        Rows.Add(new ComputerRow("This computer", remote ? "Where Schedulry is running" : _shell.ComputerName, null, !remote));
        foreach (var computer in _saved)
        {
            var current = remote && ComputerNames.Same(computer.Name, _shell.ComputerName);
            var hasPassword = computer.Account is { } account && HasSaved(computer.Name, account);
            var detail = computer.Account is null ? "As you" : $"As {computer.Account}";
            Rows.Add(new ComputerRow(computer.Name, detail, computer, current, hasPassword));
        }
    }
}
