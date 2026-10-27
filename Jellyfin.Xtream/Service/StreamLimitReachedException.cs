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
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Thrown when opening a new upstream connection would exceed the maximum number of concurrent streams.
/// Derives from <see cref="LiveTvConflictException"/>, the exception Jellyfin uses for "all tuners busy".
/// </summary>
#pragma warning disable CA1032 // LiveTvConflictException has no (message, innerException) constructor.
public class StreamLimitReachedException : LiveTvConflictException
#pragma warning restore CA1032
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StreamLimitReachedException"/> class.
    /// </summary>
    public StreamLimitReachedException()
        : base("Xtream stream limit reached.")
    {
        InUse = [];
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamLimitReachedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public StreamLimitReachedException(string message)
        : base(message)
    {
        InUse = [];
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamLimitReachedException"/> class.
    /// </summary>
    /// <param name="limit">The limit that was reached.</param>
    /// <param name="inUse">The descriptions of the streams that are open.</param>
    public StreamLimitReachedException(int limit, IReadOnlyList<string> inUse)
        : base(FormatMessage(limit, inUse))
    {
        Limit = limit;
        InUse = inUse;
    }

    /// <summary>
    /// Gets the limit that was reached.
    /// </summary>
    public int Limit { get; }

    /// <summary>
    /// Gets the descriptions of the streams that are open.
    /// </summary>
    public IReadOnlyList<string> InUse { get; }

    /// <summary>
    /// Formats the message shown to the client.
    /// </summary>
    /// <param name="limit">The limit that was reached.</param>
    /// <param name="inUse">The descriptions of the streams that are open.</param>
    /// <returns>The message.</returns>
    public static string FormatMessage(int limit, IReadOnlyList<string> inUse)
    {
        ArgumentNullException.ThrowIfNull(inUse);
        string streams = inUse.Count > 0 ? ": " + string.Join(", ", inUse) : string.Empty;
        string advice = inUse.Count > 1 ? "Stop one of those streams first." : "Stop that stream first.";
        return $"Xtream stream limit reached ({inUse.Count} of {limit} in use{streams}). {advice}";
    }
}
