namespace QingSnap.App.Services;

public sealed class HistorySelectionState
{
    private readonly HashSet<long> _selectedIds = [];
    private long? _anchorId;

    public IReadOnlySet<long> SelectedIds => _selectedIds;

    public int Count => _selectedIds.Count;

    public bool Contains(long metadataId) => metadataId > 0 && _selectedIds.Contains(metadataId);

    public void Select(
        long metadataId,
        IReadOnlyList<long> orderedIds,
        bool toggle,
        bool range)
    {
        if (metadataId <= 0)
        {
            return;
        }

        if (range && _anchorId is { } anchorId)
        {
            var anchorIndex = IndexOf(orderedIds, anchorId);
            var targetIndex = IndexOf(orderedIds, metadataId);
            if (anchorIndex >= 0 && targetIndex >= 0)
            {
                if (!toggle)
                {
                    _selectedIds.Clear();
                }

                var start = Math.Min(anchorIndex, targetIndex);
                var end = Math.Max(anchorIndex, targetIndex);
                for (var index = start; index <= end; index++)
                {
                    if (orderedIds[index] > 0)
                    {
                        _selectedIds.Add(orderedIds[index]);
                    }
                }

                return;
            }
        }

        if (toggle)
        {
            if (!_selectedIds.Remove(metadataId))
            {
                _selectedIds.Add(metadataId);
            }
        }
        else
        {
            _selectedIds.Clear();
            _selectedIds.Add(metadataId);
        }

        _anchorId = metadataId;
    }

    public void SelectAll(IEnumerable<long> metadataIds)
    {
        _selectedIds.Clear();
        foreach (var metadataId in metadataIds.Where(id => id > 0))
        {
            _selectedIds.Add(metadataId);
        }

        _anchorId = _selectedIds.Count == 0 ? null : _selectedIds.First();
    }

    public void SelectOnly(long metadataId)
    {
        _selectedIds.Clear();
        if (metadataId > 0)
        {
            _selectedIds.Add(metadataId);
            _anchorId = metadataId;
        }
        else
        {
            _anchorId = null;
        }
    }

    public void Remove(IEnumerable<long> metadataIds)
    {
        foreach (var metadataId in metadataIds)
        {
            _selectedIds.Remove(metadataId);
        }

        if (_anchorId is { } anchorId && !_selectedIds.Contains(anchorId))
        {
            _anchorId = _selectedIds.Count == 0 ? null : _selectedIds.First();
        }
    }

    public void Reconcile(IEnumerable<long> existingIds)
    {
        _selectedIds.IntersectWith(existingIds.Where(id => id > 0));
        if (_anchorId is { } anchorId && !_selectedIds.Contains(anchorId))
        {
            _anchorId = _selectedIds.Count == 0 ? null : _selectedIds.First();
        }
    }

    public void Clear()
    {
        _selectedIds.Clear();
        _anchorId = null;
    }

    private static int IndexOf(IReadOnlyList<long> ids, long target)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            if (ids[index] == target)
            {
                return index;
            }
        }

        return -1;
    }
}
