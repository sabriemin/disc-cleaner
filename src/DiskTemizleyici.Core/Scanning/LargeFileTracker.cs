namespace DiskTemizleyici.Core.Scanning;

public sealed record LargeFile(string FullPath, long Size, DateTime LastWriteTime);

/// <summary>Thread-safe "top N largest files" collector.</summary>
internal sealed class LargeFileTracker
{
    private readonly int _capacity;
    private readonly PriorityQueue<LargeFile, long> _heap = new();
    private readonly object _lock = new();
    private long _threshold; // smallest size currently kept once the heap is full

    public LargeFileTracker(int capacity) => _capacity = capacity;

    public void Offer(string fullPath, long size, DateTime lastWrite)
    {
        if (size <= Volatile.Read(ref _threshold)) return;
        lock (_lock)
        {
            if (_heap.Count < _capacity)
            {
                _heap.Enqueue(new LargeFile(fullPath, size, lastWrite), size);
            }
            else if (size > _heap.Peek().Size)
            {
                _heap.EnqueueDequeue(new LargeFile(fullPath, size, lastWrite), size);
            }
            if (_heap.Count == _capacity) Volatile.Write(ref _threshold, _heap.Peek().Size);
        }
    }

    public List<LargeFile> ToSortedList()
    {
        lock (_lock)
        {
            return _heap.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Size).ToList();
        }
    }
}
