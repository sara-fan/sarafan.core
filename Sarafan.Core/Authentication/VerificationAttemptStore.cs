// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Authentication;

public sealed class VerificationAttemptStore(TimeProvider timeProvider)
{
    private const int CleanupInterval = 128;
    private const int MaxBuckets = 10_000;
    private readonly object _gate = new();
    private readonly Dictionary<string, AttemptWindow> _windows = new(StringComparer.Ordinal);
    private long _operations;

    public bool TryConsume(string key, int limit, TimeSpan window)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            _operations++;
            if (_operations % CleanupInterval == 0 || _windows.Count >= MaxBuckets)
            {
                RemoveExpired(now, window);
            }

            if (!_windows.TryGetValue(key, out var bucket))
            {
                if (_windows.Count >= MaxBuckets)
                {
                    return false;
                }

                bucket = new AttemptWindow();
                _windows.Add(key, bucket);
            }

            while (bucket.Attempts.TryPeek(out var oldest) && now - oldest >= window)
            {
                bucket.Attempts.Dequeue();
            }

            if (bucket.Attempts.Count >= limit)
            {
                return false;
            }

            bucket.Attempts.Enqueue(now);
            bucket.LastTouched = now;
            return true;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _windows.Clear();
            _operations = 0;
        }
    }

    private void RemoveExpired(DateTimeOffset now, TimeSpan window)
    {
        foreach (var pair in _windows.ToArray())
        {
            if (now - pair.Value.LastTouched >= window)
            {
                _windows.Remove(pair.Key);
            }
        }
    }

    private sealed class AttemptWindow
    {
        public Queue<DateTimeOffset> Attempts { get; } = new();
        public DateTimeOffset LastTouched { get; set; }
    }
}
