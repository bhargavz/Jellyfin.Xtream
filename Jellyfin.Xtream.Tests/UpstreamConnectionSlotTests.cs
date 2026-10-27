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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service;
using Xunit;

namespace Jellyfin.Xtream.Tests;

public class UpstreamConnectionSlotTests
{
    [Fact]
    public async Task EnsureTwiceHoldsOneSlot()
    {
        StreamLimiter limiter = new();
        UpstreamConnectionSlot slot = Slot(limiter, "A", 1);

        await slot.EnsureAsync(CancellationToken.None);
        await slot.EnsureAsync(CancellationToken.None);

        Assert.True(slot.IsHeld);
        Assert.Equal(1, limiter.ActiveCount);
    }

    [Fact]
    public async Task ReleaseIsIdempotentAndAllowsReacquire()
    {
        StreamLimiter limiter = new();
        UpstreamConnectionSlot slot = Slot(limiter, "A", 1);
        await slot.EnsureAsync(CancellationToken.None);

        slot.Release();
        slot.Release();
        Assert.False(slot.IsHeld);
        Assert.Equal(0, limiter.ActiveCount);

        // The upstream connection can be reopened later, which takes a slot again.
        await slot.EnsureAsync(CancellationToken.None);
        Assert.Equal(1, limiter.ActiveCount);
    }

    [Fact]
    public async Task SecondChannelIsBlockedUntilTheFirstCloses()
    {
        StreamLimiter limiter = new();
        UpstreamConnectionSlot espn = Slot(limiter, "US: ESPN", 1);
        UpstreamConnectionSlot espn2 = Slot(limiter, "US: ESPN2", 1);
        await espn.EnsureAsync(CancellationToken.None);

        StreamLimitReachedException exception = await Assert.ThrowsAsync<StreamLimitReachedException>(() => espn2.EnsureAsync(CancellationToken.None));
        Assert.Contains("US: ESPN", exception.Message, StringComparison.Ordinal);
        Assert.False(espn2.IsHeld);
        Assert.Equal(1, limiter.ActiveCount);

        espn.Release();
        await espn2.EnsureAsync(CancellationToken.None);
        Assert.Equal(["US: ESPN2"], limiter.ActiveStreams);
    }

    [Fact]
    public async Task NullAcquireDisablesLimiting()
    {
        UpstreamConnectionSlot slot = new(null);
        await slot.EnsureAsync(CancellationToken.None);
        Assert.False(slot.IsHeld);
        slot.Release();
    }

    [Fact]
    public async Task ConcurrentEnsureKeepsExactlyOneLease()
    {
        StreamLimiter limiter = new();
        UpstreamConnectionSlot slot = new(async ct =>
        {
            await Task.Yield();
            return limiter.Acquire("k", "A", 0);
        });

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => slot.EnsureAsync(CancellationToken.None))));

        // EnsureAsync may race past the IsHeld check, but the extra leases are given back.
        Assert.Equal(1, limiter.ActiveCount);
        slot.Release();
        Assert.Equal(0, limiter.ActiveCount);
    }

    private static UpstreamConnectionSlot Slot(StreamLimiter limiter, string name, int limit) =>
        new(_ => Task.FromResult(limiter.Acquire(name, name, limit)));
}
