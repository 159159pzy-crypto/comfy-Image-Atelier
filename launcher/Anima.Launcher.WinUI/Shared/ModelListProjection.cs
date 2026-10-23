using Anima.Launcher.Core;
using System.Collections.ObjectModel;

namespace Anima.Launcher;

/// <summary>A group published to the model view, with observable child rows.</summary>
internal sealed class ModelGroup(string name, IEnumerable<ModelRow> items) : ObservableCollection<ModelRow>(items)
{
    public string Name { get; } = name;
    public new ObservableCollection<ModelRow> Items => this;
}

/// <summary>Build a complete snapshot before publishing groups to an already-bound view.</summary>
internal static class ModelListProjection
{
    public static void Refresh(ObservableCollection<ModelRow> rows, ObservableCollection<ModelGroup> groups,
        IEnumerable<ModelStatus> scan)
    {
        var selected = rows.Where(row => row.Selected).Select(row => row.Status.Model.Id).ToHashSet(StringComparer.Ordinal);
        var nextRows = scan.Select(status => new ModelRow(status)
        {
            Selected = selected.Contains(status.Model.Id),
        }).ToArray();
        var nextGroups = nextRows.GroupBy(row => row.Group)
            .Select(group => new ModelGroup(group.Key, group)).ToArray();

        rows.Clear();
        foreach (var row in nextRows) rows.Add(row);
        groups.Clear();
        foreach (var group in nextGroups) groups.Add(group);
    }
}
