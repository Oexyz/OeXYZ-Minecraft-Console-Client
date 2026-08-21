namespace OeXYZ.ConsoleClient;

internal sealed class BoundedDropOldestQueue<T>
{
    private readonly object gate = new();
    private readonly Queue<T> items;
    private long dropped;

    public BoundedDropOldestQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        items = new Queue<T>(capacity);
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (gate) return items.Count;
        }
    }

    public bool IsEmpty => Count == 0;
    public long Dropped => Interlocked.Read(ref dropped);

    public void Enqueue(T item)
    {
        lock (gate)
        {
            if (items.Count == Capacity)
            {
                items.Dequeue();
                Interlocked.Increment(ref dropped);
            }
            items.Enqueue(item);
        }
    }

    public bool TryDequeue(out T item)
    {
        lock (gate)
        {
            if (items.Count == 0)
            {
                item = default!;
                return false;
            }
            item = items.Dequeue();
            return true;
        }
    }

    public void Clear()
    {
        lock (gate) items.Clear();
    }
}
