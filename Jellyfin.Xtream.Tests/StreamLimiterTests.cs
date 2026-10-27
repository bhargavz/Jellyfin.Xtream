// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Jellyfin.Xtream.Tests;

public class StreamLimiterTests
{
    [Theory]
    [InlineData(0, null, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(0, 3, 3)]
    [InlineData(2, 1, 2)]
    [InlineData(2, null, 2)]
    [InlineData(-1, 4, 4)]
    public void ResolveLimit_ConfiguredOverridesProvider(int configured, int? provider, int expected)
    {
        Assert.Equal(expected, StreamLimiter.ResolveLimit(configured, provider));
    }

    [Fact]
    public void ZeroLimitIsUnlimited()
    {
        StreamLimiter limiter = new();
        for (int i = 0; i < 50; i++)
        {
            Assert.True(limiter.TryAcquire($"k{i}", $"c{i}", 0, out _, out _));
        }

        Assert.Equal(50, limiter.ActiveCount);
    }

    [Fact]
    public void BlocksAtLimitAndReportsWhatIsInUse()
    {
        StreamLimiter limiter = new();
        Assert.True(limiter.TryAcquire("a", "US: ESPN", 1, out IDisposable? lease, out _));
        Assert.NotNull(lease);

        Assert.False(limiter.TryAcquire("b", "US: ESPN2", 1, out IDisposable? blocked, out IReadOnlyList<string> inUse));
        Assert.Null(blocked);
        Assert.Equal(["US: ESPN"], inUse);
        Assert.Equal(1, limiter.ActiveCount);
    }

    [Fact]
    public void AcquireThrowsClearConflictException()
    {
        StreamLimiter limiter = new();
        using IDisposable lease = limiter.Acquire("a", "US: ESPN", 1);

        StreamLimitReachedException exception = Assert.Throws<StreamLimitReachedException>(() => limiter.Acquire("b", "US: ESPN2", 1));
        Assert.Equal("Xtream stream limit reached (1 of 1 in use: US: ESPN). Stop that stream first.", exception.Message);
        Assert.Equal(1, exception.Limit);
        Assert.IsAssignableFrom<LiveTvConflictException>(exception);
    }

    [Fact]
    public void MessageListsSeveralStreams()
    {
        Assert.Equal(
            "Xtream stream limit reached (2 of 2 in use: A, B). Stop one of those streams first.",
            StreamLimitReachedException.FormatMessage(2, ["A", "B"]));
    }

    [Fact]
    public void DisposeFreesTheSlotOnce()
    {
        StreamLimiter limiter = new();
        IDisposable first = limiter.Acquire("a", "A", 1);
        first.Dispose();
        Assert.Equal(0, limiter.ActiveCount);

        IDisposable second = limiter.Acquire("b", "B", 1);

        // A second dispose of the first lease must not free the slot now held by the second.
        first.Dispose();
        Assert.Equal(1, limiter.ActiveCount);
        Assert.Equal(["B"], limiter.ActiveStreams);
        second.Dispose();
        Assert.Equal(0, limiter.ActiveCount);
    }

    [Fact]
    public void ReleaseByKeyFreesOnlyThatKey()
    {
        StreamLimiter limiter = new();
        IDisposable a = limiter.Acquire("a", "A", 0);
        limiter.Acquire("b", "B", 0);

        Assert.Equal(1, limiter.Release("b"));
        Assert.Equal(0, limiter.Release("b"));
        Assert.Equal(["A"], limiter.ActiveStreams);

        // Disposing a lease already released by key is harmless.
        Assert.Equal(1, limiter.Release("a"));
        a.Dispose();
        Assert.Equal(0, limiter.ActiveCount);
    }

    [Fact]
    public void ActiveStreamsAreOldestFirst()
    {
        StreamLimiter limiter = new();
        limiter.Acquire("1", "First", 0);
        limiter.Acquire("2", "Second", 0);
        limiter.Acquire("3", "Third", 0);
        Assert.Equal(["First", "Second", "Third"], limiter.ActiveStreams);
    }

    [Fact]
    public async Task ConcurrentAcquireNeverExceedsTheLimit()
    {
        StreamLimiter limiter = new();
        ConcurrentBag<IDisposable> leases = [];
        int blocked = 0;

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() =>
        {
            if (limiter.TryAcquire($"k{i}", $"c{i}", 5, out IDisposable? lease, out _))
            {
                leases.Add(lease!);
            }
            else
            {
                Interlocked.Increment(ref blocked);
            }
        })));

        Assert.Equal(5, leases.Count);
        Assert.Equal(195, blocked);
        Assert.Equal(5, limiter.ActiveCount);

        // Racing double disposals still bring the count back to exactly zero.
        await Task.WhenAll(leases.Concat(leases).Select(lease => Task.Run(lease.Dispose)));
        Assert.Equal(0, limiter.ActiveCount);
    }
}
