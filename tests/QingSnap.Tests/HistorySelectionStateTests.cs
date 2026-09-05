using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class HistorySelectionStateTests
{
    [Fact]
    public void SingleToggleAndRangeSelectionUseMetadataIds()
    {
        long[] visibleIds = [11, 12, 13, 14, 15];
        var selection = new HistorySelectionState();

        selection.Select(12, visibleIds, toggle: false, range: false);
        selection.Select(14, visibleIds, toggle: false, range: true);

        Assert.Equal([12, 13, 14], selection.SelectedIds.Order());

        selection.Select(13, visibleIds, toggle: true, range: false);

        Assert.Equal([12, 14], selection.SelectedIds.Order());
    }

    [Fact]
    public void SelectAllAndReconcileSurviveItemInstanceRecycling()
    {
        var selection = new HistorySelectionState();
        selection.SelectAll([21, 22, 23]);

        selection.Reconcile([22, 23, 24]);

        Assert.Equal([22, 23], selection.SelectedIds.Order());
    }
}
