using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Schedulry.ViewModels.Editor;

/// <summary>
/// Importing a task from Task Scheduler XML (saved with Save a Copy, or exported from the old
/// window). The XML is registered as it is, so nothing in it is lost; only the name and folder
/// are chosen here, plus the password when the task stores one.
/// </summary>
public sealed partial class ImportViewModel : ObservableObject
{
    readonly MainViewModel _shell;

    public ImportViewModel(MainViewModel shell, string fileName, string xml, string folder)
    {
        _shell = shell;
        FileName = fileName;
        Xml = xml;
        Name = Path.GetFileNameWithoutExtension(fileName);
        FolderPicker = new FolderPicker(shell.Host.Folders(), folder);
        FolderPicker.Changed += () => Folder = FolderPicker.Path;
        Folder = folder;
        NeedsPassword = Regex.IsMatch(xml, @"<LogonType>\s*(Password|InteractiveTokenOrPassword)\s*</LogonType>");
    }

    public string FileName { get; }
    public string Xml { get; }
    public FolderPicker FolderPicker { get; }
    public bool NeedsPassword { get; }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ImportCommand))] public partial string Name { get; set; }
    [ObservableProperty] public partial string Folder { get; set; }
    partial void OnFolderChanged(string value) => FolderPicker.Set(value);
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ImportCommand))] public partial string Password { get; set; } = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ImportCommand))] public partial bool Busy { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblem))] public partial string? Problem { get; private set; }
    public bool HasProblem => Problem is not null;

    bool CanImport() => !Busy && Name.Trim().Length > 0 && (!NeedsPassword || Password.Length > 0);

    [RelayCommand(CanExecute = nameof(CanImport))]
    async Task ImportAsync()
    {
        Busy = true;
        Problem = null;
        var folder = TaskEditorViewModel.NormaliseFolder(Folder);
        var name = Name.Trim();
        var password = NeedsPassword ? Password : null;
        try
        {
            await Platform.Background(() =>
            {
                _shell.Host.Import(folder, name, Xml, password);
                return Task.CompletedTask;
            });
            Password = "";
            _shell.CloseImport(folder == "\\" ? "\\" + name : $"{folder}\\{name}");
        }
        catch (UnauthorizedAccessException)
        {
            Problem = "Windows refused: importing this task needs administrator rights.";
        }
        catch (Exception e)
        {
            Problem = $"Couldn't import: {e.Message}";
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
        _shell.CloseImport(null);
    }
}
