using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

public class StableCollectionTests
{
    private sealed record Row(int Id, string Text);

    [Fact]
    public void DeleteAndRescanKeepSurvivingRowsWithoutResetOrAdd()
    {
        var first = new Row(1, "one");
        var last = new Row(3, "three");
        var rows = new ObservableCollection<Row> { first, new(2, "two"), last };
        var events = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);
        StableCollection.Update(rows, new[] { new Row(1, "one"), new Row(3, "three") }, r => r.Id);
        StableCollection.Update(rows, new[] { new Row(1, "one"), new Row(3, "three") }, r => r.Id);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Remove }, events);
        Assert.Same(first, rows[0]);
        Assert.Same(last, rows[1]);
    }

    [Fact]
    public void ChangesReplaceOnlyChangedRowsAndMaintainRequestedOrder()
    {
        var retained = new Row(1, "one");
        var rows = new ObservableCollection<Row> { retained, new(2, "two") };
        var events = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);
        StableCollection.Update(rows, new[] { new Row(2, "edited"), retained, new Row(3, "added") }, r => r.Id);
        Assert.Equal(new[] { 2, 1, 3 }, rows.Select(r => r.Id));
        Assert.Equal("edited", rows[0].Text);
        Assert.Same(retained, rows[1]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);
    }

    [Fact]
    public void RemovingLastGroupDoesNotRecreateRemainingGroup()
    {
        var remaining = new ObservableCollection<Row> { new(1, "one") };
        var deleted = new ObservableCollection<Row> { new(2, "two") };
        var groups = new ObservableCollection<ObservableCollection<Row>> { deleted, remaining };
        StableCollection.Update(groups, new[] { remaining }, g => g[0].Id, (a, b) => true);
        Assert.Single(groups);
        Assert.Same(remaining, groups[0]);
    }
}
