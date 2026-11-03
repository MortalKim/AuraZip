using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AuraZip;

public sealed class FolderNode : INotifyPropertyChanged
{
    public override string ToString() => Name;
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public ObservableCollection<FolderNode> Children { get; } = new();
    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class PathCrumb(string name, string key) { public string Name { get; } = name; public string Key { get; } = key; public override string ToString() => Name; }
public sealed class RecentArchive(string name, string path) { public string Name { get; } = name; public string Path { get; } = path; public override string ToString() => Name; }
