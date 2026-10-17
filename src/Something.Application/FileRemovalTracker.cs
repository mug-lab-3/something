using System.ComponentModel;
using Something.Ntfs;
using Something.Windows;

namespace Something.Application;

public enum FileRemovalMark
{
    None,
    Recycled,
    Missing,
}

public sealed class FileRemovalState : INotifyPropertyChanged
{
    private FileRemovalMark mark;
    private bool busy;
    public FileRemovalMark Mark => mark;
    public bool IsRemoved => mark != FileRemovalMark.None;
    public bool CanRecycle => !IsRemoved && !busy;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetBusy(bool value)
    {
        busy = value;
        PropertyChanged?.Invoke(this, new(nameof(CanRecycle)));
    }

    internal void SetMark(FileRemovalMark value)
    {
        mark = value;
        PropertyChanged?.Invoke(this, new(nameof(Mark)));
        PropertyChanged?.Invoke(this, new(nameof(IsRemoved)));
        PropertyChanged?.Invoke(this, new(nameof(CanRecycle)));
    }
}

/// <summary>Current-session UI evidence only. A hard-link name is not the entire file identity.</summary>
public sealed class FileRemovalTracker
{
    private readonly record struct Key(string Volume, string Path, FileReference Reference);

    private readonly Dictionary<Key, FileRemovalState> states = [];
    private readonly List<(string Volume, string Path, FileRemovalMark Mark)> folders = [];

    public FileRemovalState StateFor(FileRecycleTarget target)
    {
        var key = new Key(target.VolumeKey, target.Path, target.Reference);
        if (states.TryGetValue(key, out var state))
            return state;
        state = new();
        var ancestor = folders.LastOrDefault(f =>
            f.Volume == target.VolumeKey && IsBelow(target.Path, f.Path)
        );
        if (ancestor.Path is not null)
            state.SetMark(ancestor.Mark);
        states.Add(key, state);
        return state;
    }

    public void Mark(FileRecycleTarget target, FileRemovalMark mark)
    {
        StateFor(target).SetMark(mark);
        if (!target.IsDirectory)
            return;
        folders.Add((target.VolumeKey, target.Path, mark));
        foreach (var (key, state) in states)
            if (key.Volume == target.VolumeKey && IsBelow(key.Path, target.Path))
                state.SetMark(mark);
    }

    public void ClearVolume(string volume)
    {
        foreach (var key in states.Keys.Where(k => k.Volume == volume).ToArray())
        {
            states[key].SetMark(FileRemovalMark.None);
            states.Remove(key);
        }
        folders.RemoveAll(f => f.Volume == volume);
    }

    private static bool IsBelow(string path, string parent) =>
        path.StartsWith(parent + "\\", StringComparison.Ordinal);
}
