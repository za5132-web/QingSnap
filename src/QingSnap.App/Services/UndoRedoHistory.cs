namespace QingSnap.App.Services;

internal sealed class UndoRedoHistory<T>
{
    private const int DefaultCapacity = 200;
    private readonly List<T> _undo = [];
    private readonly List<T> _redo = [];
    private readonly int _capacity;

    public UndoRedoHistory(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public void Record(T previousState)
    {
        Push(_undo, previousState);
        _redo.Clear();
    }

    public bool TryUndo(T currentState, out T targetState)
    {
        if (!TryPop(_undo, out targetState!))
        {
            return false;
        }

        Push(_redo, currentState);
        return true;
    }

    public bool TryRedo(T currentState, out T targetState)
    {
        if (!TryPop(_redo, out targetState!))
        {
            return false;
        }

        Push(_undo, currentState);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private void Push(List<T> stack, T value)
    {
        stack.Add(value);
        if (stack.Count > _capacity)
        {
            stack.RemoveAt(0);
        }
    }

    private static bool TryPop(List<T> stack, out T value)
    {
        if (stack.Count == 0)
        {
            value = default!;
            return false;
        }

        var index = stack.Count - 1;
        value = stack[index];
        stack.RemoveAt(index);
        return true;
    }
}
