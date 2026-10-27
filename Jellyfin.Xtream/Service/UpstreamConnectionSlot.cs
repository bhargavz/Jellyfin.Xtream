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
/// Holds at most one <see cref="StreamLimiter"/> lease for the upstream connection of a single owner, such as a
/// <see cref="Restream"/>. Acquiring while a lease is held is a no-op, and releasing is idempotent and thread-safe.
/// </summary>
/// <param name="acquire">Reserves a slot; throws <see cref="StreamLimitReachedException"/> when the limit is reached.
/// Null disables limiting.</param>
public sealed class UpstreamConnectionSlot(Func<CancellationToken, Task<IDisposable>>? acquire)
{
    private IDisposable? _lease;

    /// <summary>
    /// Gets a value indicating whether a slot is currently held.
    /// </summary>
    public bool IsHeld => Volatile.Read(ref _lease) is not null;

    /// <summary>
    /// Reserves a slot unless one is already held.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the slot is held.</returns>
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (acquire is null || IsHeld)
        {
            return;
        }

        IDisposable lease = await acquire(cancellationToken).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _lease, lease, null) is not null)
        {
            // Another caller acquired concurrently; keep theirs.
            lease.Dispose();
        }
    }

    /// <summary>
    /// Releases the slot if one is held.
    /// </summary>
    public void Release()
    {
        Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}
