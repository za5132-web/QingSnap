using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class UndoRedoHistoryTests
{
    [Fact]
    public void ThreeUndoThenThreeRedoRestoresEveryState()
    {
        var history = new UndoRedoHistory<int>();
        var current = 0;
        for (var next = 1; next <= 3; next++)
        {
            history.Record(current);
            current = next;
        }

        for (var expected = 2; expected >= 0; expected--)
        {
            Assert.True(history.TryUndo(current, out var restored));
            current = restored;
            Assert.Equal(expected, current);
        }

        for (var expected = 1; expected <= 3; expected++)
        {
            Assert.True(history.TryRedo(current, out var restored));
            current = restored;
            Assert.Equal(expected, current);
        }
    }

    [Fact]
    public void RecordingAfterUndoInvalidatesRedoHistory()
    {
        var history = new UndoRedoHistory<string>();
        history.Record("empty");
        history.Record("first");

        Assert.True(history.TryUndo("second", out var restored));
        Assert.Equal("first", restored);
        Assert.True(history.CanRedo);

        history.Record(restored);

        Assert.False(history.CanRedo);
        Assert.Equal(2, history.UndoCount);
    }

    [Fact]
    public void OneCheckpointCanRepresentAWholeCompoundOperation()
    {
        var history = new UndoRedoHistory<string[]>();
        var beforeClear = new[] { "rectangle", "text", "arrow" };
        history.Record(beforeClear);

        Assert.True(history.TryUndo([], out var restored));
        Assert.Equal(beforeClear, restored);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void HistoryCapacityPreventsUnboundedLongSessionGrowth()
    {
        var history = new UndoRedoHistory<int>(capacity: 3);

        for (var index = 0; index < 10; index++)
        {
            history.Record(index);
        }

        Assert.Equal(3, history.UndoCount);
        Assert.True(history.TryUndo(10, out var latest));
        Assert.Equal(9, latest);
        Assert.True(history.TryUndo(9, out var middle));
        Assert.Equal(8, middle);
        Assert.True(history.TryUndo(8, out var oldestRetained));
        Assert.Equal(7, oldestRetained);
        Assert.False(history.TryUndo(7, out _));
    }
}
