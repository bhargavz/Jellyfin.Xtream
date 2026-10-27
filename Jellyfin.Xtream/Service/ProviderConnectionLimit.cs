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
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Caches the provider's <c>user_info.max_connections</c> so that opening a stream does not call the API each time.
/// </summary>
/// <param name="fetch">Fetches <c>max_connections</c> from the provider.</param>
/// <param name="now">The clock; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
public sealed class ProviderConnectionLimit(Func<CancellationToken, Task<int>> fetch, Func<DateTimeOffset>? now = null)
{
    /// <summary>
    /// How long a known value is reused.
    /// </summary>
    public static readonly TimeSpan SuccessLifetime = TimeSpan.FromHours(6);

    /// <summary>
    /// How long a failed lookup is remembered before it is retried.
    /// </summary>
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly object _lock = new();
    private volatile CacheEntry? _entry;
    private Task<int?>? _pending;
    private string? _pendingKey;

    /// <summary>
    /// Gets the provider's maximum number of connections. Concurrent callers share one lookup.
    /// </summary>
    /// <param name="cacheKey">A key that changes when the credentials change, such as <see cref="Plugin.DataVersion"/>.</param>
    /// <param name="cancellationToken">The cancellation token for waiting; the lookup itself is not cancelled.</param>
    /// <returns>The positive maximum, or null when it is unknown (lookup failed, or the provider reports 0).</returns>
    public Task<int?> GetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        if (TryGetCached(cacheKey, out int? cached))
        {
            return Task.FromResult(cached);
        }

        Task<int?> pending;
        lock (_lock)
        {
            if (_pending is null || _pending.IsCompleted || !string.Equals(_pendingKey, cacheKey, StringComparison.Ordinal))
            {
                _pendingKey = cacheKey;
                _pending = FetchAsync(cacheKey);
            }

            pending = _pending;
        }

        return pending.WaitAsync(cancellationToken);
    }

    private async Task<int?> FetchAsync(string cacheKey)
    {
        int? value;
        TimeSpan lifetime;
        try
        {
            int max = await fetch(CancellationToken.None).ConfigureAwait(false);
            value = max > 0 ? max : null;
            lifetime = SuccessLifetime;
        }
#pragma warning disable CA1031 // An unknown limit means unlimited; the lookup must never block playback.
        catch (Exception)
#pragma warning restore CA1031
        {
            value = null;
            lifetime = FailureLifetime;
        }

        _entry = new CacheEntry(cacheKey, value, _now() + lifetime);
        lock (_lock)
        {
            if (string.Equals(_pendingKey, cacheKey, StringComparison.Ordinal))
            {
                _pending = null;
                _pendingKey = null;
            }
        }

        return value;
    }

    /// <summary>
    /// Stores a value that was fetched elsewhere, such as by the provider test endpoint.
    /// </summary>
    /// <param name="cacheKey">The cache key.</param>
    /// <param name="maxConnections">The provider's <c>max_connections</c>.</param>
    public void Set(string cacheKey, int maxConnections)
    {
        _entry = new CacheEntry(cacheKey, maxConnections > 0 ? maxConnections : null, _now() + SuccessLifetime);
    }

    private bool TryGetCached(string cacheKey, out int? value)
    {
        CacheEntry? entry = _entry;
        if (entry is not null && string.Equals(entry.Key, cacheKey, StringComparison.Ordinal) && entry.Expires > _now())
        {
            value = entry.Value;
            return true;
        }

        value = null;
        return false;
    }

    private sealed record CacheEntry(string Key, int? Value, DateTimeOffset Expires);
}
