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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service;
using Xunit;

namespace Jellyfin.Xtream.Tests;

public class ProviderConnectionLimitTests
{
    private DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CachesTheProviderValue()
    {
        int calls = 0;
        ProviderConnectionLimit limit = Create(_ => Task.FromResult(++calls > 0 ? 1 : 0));

        Assert.Equal(1, await limit.GetAsync("v1", CancellationToken.None));
        Assert.Equal(1, await limit.GetAsync("v1", CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RefetchesAfterExpiryOrKeyChange()
    {
        int calls = 0;
        ProviderConnectionLimit limit = Create(_ => Task.FromResult(++calls));

        Assert.Equal(1, await limit.GetAsync("v1", CancellationToken.None));
        Assert.Equal(2, await limit.GetAsync("v2", CancellationToken.None));

        _now += ProviderConnectionLimit.SuccessLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(3, await limit.GetAsync("v2", CancellationToken.None));
    }

    [Fact]
    public async Task ZeroMeansUnknown()
    {
        ProviderConnectionLimit limit = Create(_ => Task.FromResult(0));
        Assert.Null(await limit.GetAsync("v1", CancellationToken.None));
    }

    [Fact]
    public async Task FailureIsUnknownAndRetriedLater()
    {
        int calls = 0;
        ProviderConnectionLimit limit = Create(_ =>
        {
            calls++;
            return calls == 1 ? Task.FromException<int>(new HttpRequestException("offline")) : Task.FromResult(1);
        });

        Assert.Null(await limit.GetAsync("v1", CancellationToken.None));
        Assert.Null(await limit.GetAsync("v1", CancellationToken.None));
        Assert.Equal(1, calls);

        _now += ProviderConnectionLimit.FailureLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(1, await limit.GetAsync("v1", CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SynchronousThrowIsUnknown()
    {
        ProviderConnectionLimit limit = Create(_ => throw new InvalidOperationException("bad"));
        Assert.Null(await limit.GetAsync("v1", CancellationToken.None));
    }

    [Fact]
    public async Task SetPrimesTheCache()
    {
        int calls = 0;
        ProviderConnectionLimit limit = Create(_ => Task.FromResult(++calls > 0 ? 9 : 0));

        limit.Set("v1", 1);
        Assert.Equal(1, await limit.GetAsync("v1", CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneLookup()
    {
        int calls = 0;
        TaskCompletionSource<int> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderConnectionLimit limit = Create(_ =>
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        });

        Task<int?>[] waits = Enumerable.Range(0, 20).Select(_ => limit.GetAsync("v1", CancellationToken.None)).ToArray();
        gate.SetResult(1);
        int?[] results = await Task.WhenAll(waits);

        Assert.All(results, result => Assert.Equal(1, result));
        Assert.Equal(1, calls);
    }

    private ProviderConnectionLimit Create(Func<CancellationToken, Task<int>> fetch) => new(fetch, () => _now);
}
