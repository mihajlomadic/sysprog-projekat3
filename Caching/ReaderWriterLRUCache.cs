namespace Caching;

internal class ReaderWriterLRUCache<K, T>
    where K : notnull
    where T : notnull
{
    private Dictionary<K, T> cache;
    private ReaderWriterLockSlim cacheLock;
    private LinkedList<K> lruList;

    int capacity;
    int Capacity => capacity;

    public ReaderWriterLRUCache(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentException("Capacity must be greater than 0.");
        }
        this.capacity = capacity;
        cache = new Dictionary<K, T>(capacity);
        cacheLock = new ReaderWriterLockSlim();
        lruList = new LinkedList<K>();
    }

    public void Write(K key, T value)
    {
        cacheLock.EnterUpgradeableReadLock();
        try
        {
            if (cache.ContainsKey(key))
            {
                // if cache contains key, move it to the front of the list
                // and update the value
                cacheLock.EnterWriteLock();
                try
                {
                    cache[key] = value;
                    lruList.Remove(key);
                    lruList.AddFirst(key);
                }
                finally
                {
                    cacheLock.ExitWriteLock();
                }
            }
            else
            {
                cacheLock.EnterWriteLock();
                try
                {
                    // if cache doesn't contain key, add it to the front of the list
                    cache.Add(key, value);
                    lruList.AddFirst(key);
                    if (cache.Count > Capacity)
                    {
                        // if cache is full, remove the last item from the list
                        var last = lruList.Last;
                        if (last != null)
                            cache.Remove(last.Value);
                        lruList.RemoveLast();
                    }
                }
                finally
                {
                    cacheLock.ExitWriteLock();
                }
            }
        }
        finally
        {
            cacheLock.ExitUpgradeableReadLock();
        }
    }

    public bool TryRead(K key, out T readValue)
    {
        cacheLock.EnterUpgradeableReadLock();
        try
        {
            if (cache.ContainsKey(key))
            {
                // if cache contains key, move it to the front of the list
                // and return the value
                cacheLock.EnterWriteLock();
                try
                {
                    readValue = cache[key];
                    lruList.Remove(key);
                    lruList.AddFirst(key);
                    return true;
                }
                finally
                {
                    cacheLock.ExitWriteLock();
                }
            }
            else
            {
                readValue = default!;
                return false;
            }
        }
        finally
        {
            cacheLock.ExitUpgradeableReadLock();
        }
    }
}
