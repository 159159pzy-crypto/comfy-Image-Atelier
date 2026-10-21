using Anima.Launcher.Core;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Anima.Launcher;

/// <summary>View row for one model item; wraps <see cref="ModelStatus"/> with selection state.</summary>
internal sealed class ModelRow(ModelStatus status) : INotifyPropertyChanged
{
    private bool selected;
    public ModelStatus Status { get; } = status;
    public string Group => Status.Model.Group;
    public string Name => Status.Model.Name + (Status.Model.Recommended ? " · 推荐" : "");
    public string StateText => Status.State + (!Status.Model.CanDownload ? " / 手动" : "");
    public string SizeText => $"{Status.Model.Size / 1048576d:N0} MiB";
    public string PathText => Status.FoundPath ?? Status.Destination;
    /// <summary>Only downloadable, still-missing, not-yet-present files may be selected.</summary>
    public bool CanSelect => Status.Model.CanDownload && Status.Missing && Status.Destination.Length > 0
        && (Status.FoundPath is null || Status.Model.IsPackage);
    public bool Selected
    {
        get => selected;
        set { if (CanSelect && selected != value) { selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
