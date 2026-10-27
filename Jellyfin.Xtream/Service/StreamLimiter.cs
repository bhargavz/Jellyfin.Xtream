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
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Counts the open upstream provider connections and enforces the maximum number of concurrent streams.
/// </summary>
/// <remarks>
/// Each upstream connection holds one <see cref="IDisposable"/> lease. Disposing the lease frees the slot; this is
/// idempotent, so every close path (close, dispose, upstream end, open failure) can release it safely.
/// </remarks>
public sealed class StreamLimiter
{
    private readonly object _lock = new();
    private readonly Dictionary<long, Entry> _active = [];
    private long _nextId;

    /// <summary>
    /// Gets the number of upstream connections that are currently open.
    /// </summary>
    public int ActiveCount
    {
        get
        {
            lock (_lock)
            {
                return _active.Count;
            }
        }
    }

    /// <summary>
    /// Gets the descriptions of the upstream connections that are currently open, oldest first.
    /// </summary>
    public IReadOnlyList<string> ActiveStreams
    {
        get
        {
            lock (_lock)
            {
                return Snapshot();
            }
        }
    }

    /// <summary>
    /// Resolves the effective limit.
    /// </summary>
    /// <param name="configured">The configured maximum; positive values override the provider.</param>
    /// <param name="providerMaxConnections">The provider's <c>max_connections</c>, or null when unknown.</param>
    /// <returns>The maximum number of concurrent streams, or 0 for unlimited.</returns>
    public static int ResolveLimit(int configured, int? providerMaxConnections)
    {
        if (configured > 0)
        {
            return configured;
        }

        return providerMaxConnections is > 0 ? providerMaxConnections.Value : 0;
    }

    /// <summary>
    /// Tries to reserve a slot for a new upstream connection.
    /// </summary>
    /// <param name="key">A key identifying the owner of the connection, used by <see cref="Release(string)"/>.</param>
    /// <param name="description">A human-readable description, such as the channel name.</param>
    /// <param name="limit">The maximum number of concurrent connections; 0 or less means unlimited.</param>
    /// <param name="lease">The lease to dispose when the connection closes, or null when the limit is reached.</param>
    /// <param name="inUse">The descriptions of the connections already open.</param>
    /// <returns>Whether a slot was reserved.</returns>
    public bool TryAcquire(string key, string description, int limit, out IDisposable? lease, out IReadOnlyList<string> inUse)
    {
        lock (_lock)
        {
            if (limit > 0 && _active.Count >= limit)
            {
                lease = null;
                inUse = Snapshot();
                return false;
            }

            long id = ++_nextId;
            _active.Add(id, new Entry(key, description));
            lease = new Lease(this, id);
            inUse = Snapshot();
            return true;
        }
    }

    /// <summary>
    /// Reserves a slot for a new upstream connection, or throws when the limit is reached.
    /// </summary>
    /// <param name="key">A key identifying the owner of the connection.</param>
    /// <param name="description">A human-readable description, such as the channel name.</param>
    /// <param name="limit">The maximum number of concurrent connections; 0 or less means unlimited.</param>
    /// <returns>The lease to dispose when the connection closes.</returns>
    /// <exception cref="StreamLimitReachedException">The limit is reached.</exception>
    public IDisposable Acquire(string key, string description, int limit)
    {
        if (TryAcquire(key, description, limit, out IDisposable? lease, out IReadOnlyList<string> inUse))
        {
            return lease!;
        }

        throw new StreamLimitReachedException(limit, inUse);
    }

    /// <summary>
    /// Releases every slot held under the given key.
    /// </summary>
    /// <param name="key">The key passed when acquiring.</param>
    /// <returns>The number of slots released.</returns>
    public int Release(string key)
    {
        lock (_lock)
        {
            long[] ids = _active.Where(pair => string.Equals(pair.Value.Key, key, StringComparison.Ordinal)).Select(pair => pair.Key).ToArray();
            foreach (long id in ids)
            {
                _active.Remove(id);
            }

            return ids.Length;
        }
    }

    private void Release(long id)
    {
        lock (_lock)
        {
            _active.Remove(id);
        }
    }

    private List<string> Snapshot() => _active.OrderBy(pair => pair.Key).Select(pair => pair.Value.Description).ToList();

    private readonly record struct Entry(string Key, string Description);

    private sealed class Lease(StreamLimiter limiter, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                limiter.Release(id);
            }
        }
    }
}
