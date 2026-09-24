// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Authentication;

namespace Sarafan.Core.Tests;

[TestFixture]
public sealed class VerificationAttemptStoreTests
{
    [Test]
    public void TryConsume_BoundsBucketsAndEvictsExpiredEntries()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));
        var store = new VerificationAttemptStore(timeProvider);
        var window = TimeSpan.FromMinutes(15);

        for (var index = 0; index < 10_000; index++)
        {
            Assert.That(store.TryConsume($"phone:{index}", 3, window), Is.True);
        }

        Assert.That(store.TryConsume("over-capacity", 3, window), Is.False);

        timeProvider.Advance(window);
        Assert.That(store.TryConsume("after-expiry", 3, window), Is.True);
    }

    [Test]
    public void TryConsume_EnforcesBucketCapacityUnderConcurrency()
    {
        var store = new VerificationAttemptStore(TimeProvider.System);
        var accepted = 0;

        Parallel.For(
            0,
            20_000,
            new ParallelOptions { MaxDegreeOfParallelism = 32 },
            index =>
            {
                if (store.TryConsume($"phone:{index}", 1, TimeSpan.FromMinutes(15)))
                {
                    Interlocked.Increment(ref accepted);
                }
            });

        Assert.That(accepted, Is.EqualTo(10_000));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
