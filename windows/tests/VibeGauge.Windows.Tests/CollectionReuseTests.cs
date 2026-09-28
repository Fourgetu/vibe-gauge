using System.Collections.ObjectModel;
using System.Collections.Specialized;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class CollectionReuseTests
{
    [Fact]
    public void UnchangedRowsKeepInstancesWithoutResetEvents()
    {
        var first = new Row("first", 1);
        var rows = new ObservableCollection<Row> { first, new("second", 2) };
        var changes = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => changes.Add(e.Action);
        rows.ReplaceWith([new("first", 1), new("second", 2)]);
        Assert.Empty(changes);
        Assert.Same(first, rows[0]);
        rows.ReplaceWith([new("first", 1), new("second", 3), new("third", 4)]);
        Assert.Equal([NotifyCollectionChangedAction.Replace, NotifyCollectionChangedAction.Add], changes);
        rows.ReplaceWith([new("first", 1)]);
        Assert.Single(rows);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
    }
    private sealed record Row(string Name, int Value);
}
