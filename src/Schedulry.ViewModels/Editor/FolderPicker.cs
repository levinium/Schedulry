using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Schedulry.ViewModels.Editor;

/// <summary>A Task Scheduler folder in the picker: its path, shown by name and indented under its parent.</summary>
public sealed record FolderChoice(string Path)
{
    public bool IsTop => Path == "\\";
    public int Depth => IsTop ? 0 : Path.Count(c => c == '\\');
    public string Title => IsTop ? "Top level" : Path[(Path.LastIndexOf('\\') + 1)..];
    /// <summary>The whole path, for the closed drop-down.</summary>
    public string Label => IsTop ? "Top level" : Path;
    /// <summary>Indent in pixels, so the drop-down reads as a tree.</summary>
    public double Indent => Depth * 16;
}

/// <summary>
/// Where a task goes: one of the computer's folders from a list, or a new folder inside the chosen
/// one. <see cref="Path"/> is the result either way.
/// </summary>
public sealed partial class FolderPicker : ObservableObject
{
    readonly List<FolderChoice> _choices;

    public FolderPicker(IReadOnlyList<string> folders, string current)
    {
        _choices = folders.Select(f => new FolderChoice(f)).ToList();
        if (!_choices.Any(c => c.IsTop))
            _choices.Insert(0, new FolderChoice("\\"));
        Selected = _choices[0];
        // A folder that doesn't exist yet (a copied task's, say) is offered as a new one.
        Set(current);
    }

    public IReadOnlyList<FolderChoice> Choices => _choices;

    [ObservableProperty] public partial FolderChoice Selected { get; set; }
    [ObservableProperty] public partial bool IsCreating { get; private set; }
    [ObservableProperty] public partial string NewName { get; set; } = "";

    /// <summary>The folder chosen, or the new one inside it once it has a name.</summary>
    public string Path
    {
        get
        {
            var name = NewName.Trim().Trim('\\');
            if (!IsCreating || name.Length == 0)
                return Selected.Path;
            return TaskEditorViewModel.NormaliseFolder(Selected.IsTop ? name : $"{Selected.Path}\\{name}");
        }
    }

    public string NewFolderNote => Selected.IsTop
        ? "Made at the top level when the task is saved."
        : $"Made inside {Selected.Path} when the task is saved.";

    /// <summary>Raised whenever <see cref="Path"/> may have changed.</summary>
    public event Action? Changed;

    bool _quiet;

    void Raise()
    {
        if (!_quiet)
            Changed?.Invoke();
    }

    partial void OnSelectedChanged(FolderChoice value)
    {
        OnPropertyChanged(nameof(NewFolderNote));
        Raise();
    }
    partial void OnIsCreatingChanged(bool value) => Raise();
    partial void OnNewNameChanged(string value) => Raise();

    [RelayCommand]
    void StartNewFolder() => IsCreating = true;

    [RelayCommand]
    void CancelNewFolder()
    {
        NewName = "";
        IsCreating = false;
    }

    /// <summary>Points the picker at a path typed elsewhere (or set by code).</summary>
    public void Set(string path)
    {
        if (string.Equals(Path, TaskEditorViewModel.NormaliseFolder(path), StringComparison.OrdinalIgnoreCase))
            return;
        _quiet = true;
        try
        {
            Choose(path);
        }
        finally
        {
            _quiet = false;
        }
        Changed?.Invoke();
    }

    void Choose(string path)
    {
        if (Find(path) is { } existing)
        {
            NewName = "";
            IsCreating = false;
            Selected = existing;
            return;
        }
        // The deepest folder that exists is chosen; the rest of the path is the new folder.
        var normal = TaskEditorViewModel.NormaliseFolder(path);
        var parent = normal;
        FolderChoice? found = null;
        while (found is null)
        {
            var cut = parent.LastIndexOf('\\');
            parent = cut <= 0 ? "\\" : parent[..cut];
            found = Find(parent);
            if (parent == "\\")
                found ??= _choices[0];
        }
        Selected = found;
        NewName = found.IsTop ? normal[1..] : normal[(found.Path.Length + 1)..];
        IsCreating = true;
    }

    FolderChoice? Find(string path)
    {
        var normal = TaskEditorViewModel.NormaliseFolder(path);
        return _choices.FirstOrDefault(c => string.Equals(c.Path, normal, StringComparison.OrdinalIgnoreCase));
    }
}
