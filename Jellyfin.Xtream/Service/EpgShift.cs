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
using Jellyfin.Xtream.Configuration;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Per-channel EPG time shift (upstream #217). Some providers send guide times in their own
/// local time while labelling them UTC; the shift moves every programme of a channel by a fixed amount.
/// Adapted from <c>GetEpgShift</c> in closed upstream PR #296, but as a fixed hours/minutes value
/// instead of a timezone pair.
/// </summary>
public static class EpgShift
{
    /// <summary>
    /// The largest shift accepted, in minutes, in either direction (one day).
    /// </summary>
    public const int MaxShiftMinutes = 24 * 60;

    /// <summary>
    /// Gets the shift configured for a channel.
    /// </summary>
    /// <param name="overrides">The channel overrides, if any.</param>
    /// <returns>The shift to add to the programme times; zero when none is set.</returns>
    public static TimeSpan GetEpgShift(ChannelOverrides? overrides)
    {
        int minutes = overrides?.EpgShiftMinutes ?? 0;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, -MaxShiftMinutes, MaxShiftMinutes));
    }

    /// <summary>
    /// Gets the shift configured for a provider stream.
    /// </summary>
    /// <param name="overrides">The Live TV overrides, keyed by stream id.</param>
    /// <param name="streamId">The provider stream id.</param>
    /// <returns>The shift to add to the programme times; zero when none is set.</returns>
    public static TimeSpan GetEpgShift(IReadOnlyDictionary<int, ChannelOverrides>? overrides, int streamId)
    {
        return overrides is not null && overrides.TryGetValue(streamId, out ChannelOverrides? channel)
            ? GetEpgShift(channel)
            : TimeSpan.Zero;
    }

    /// <summary>
    /// Maps a requested guide window bound back to the provider's (unshifted) time, so the
    /// programmes that land in the window after the shift are the ones fetched.
    /// </summary>
    /// <param name="value">The requested bound.</param>
    /// <param name="shift">The channel shift.</param>
    /// <returns>The bound minus the shift, clamped to the valid <see cref="DateTime"/> range.</returns>
    public static DateTime Unshift(DateTime value, TimeSpan shift)
    {
        if (shift > TimeSpan.Zero && value - DateTime.MinValue < shift)
        {
            return DateTime.SpecifyKind(DateTime.MinValue, value.Kind);
        }

        if (shift < TimeSpan.Zero && DateTime.MaxValue - value < -shift)
        {
            return DateTime.SpecifyKind(DateTime.MaxValue, value.Kind);
        }

        return value - shift;
    }

    /// <summary>
    /// Moves the start and end of each programme by the shift. The programme ids are left alone,
    /// so they stay stable when the shift changes.
    /// </summary>
    /// <param name="programs">The programmes; they are changed in place.</param>
    /// <param name="shift">The shift to add.</param>
    /// <returns>The same programmes.</returns>
    public static IReadOnlyList<ProgramInfo> Apply(IEnumerable<ProgramInfo> programs, TimeSpan shift)
    {
        List<ProgramInfo> list = programs as List<ProgramInfo> ?? programs.ToList();
        if (shift == TimeSpan.Zero)
        {
            return list;
        }

        foreach (ProgramInfo program in list)
        {
            program.StartDate += shift;
            program.EndDate += shift;
        }

        return list;
    }
}
