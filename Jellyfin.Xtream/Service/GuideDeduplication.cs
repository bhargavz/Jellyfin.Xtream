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
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Makes sure the plugin never hands Jellyfin two channels or programmes with the same id.
/// Jellyfin derives its database ids from these, and duplicates in one batch end in
/// "UNIQUE constraint failed" errors when the guide is saved (upstream #242).
/// </summary>
public static class GuideDeduplication
{
    /// <summary>
    /// The remedy logged with every duplicate warning.
    /// </summary>
    internal const string Remedy =
        "If Jellyfin logs 'UNIQUE constraint failed' while refreshing the guide, see the troubleshooting section in the plugin's AGENTS.md (upstream issue #242).";

    private const int MaxLoggedIds = 10;

    /// <summary>
    /// Removes channels whose id was already returned, keeping the first one.
    /// </summary>
    /// <param name="channels">The channels.</param>
    /// <param name="logger">Optional logger for the dropped ids.</param>
    /// <returns>The channels with unique ids, in their original order.</returns>
    public static IReadOnlyList<ChannelInfo> DistinctChannels(IEnumerable<ChannelInfo> channels, ILogger? logger)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<ChannelInfo> result = [];
        List<string> dropped = [];
        foreach (ChannelInfo channel in channels)
        {
            if (channel.Id is null || seen.Add(channel.Id))
            {
                result.Add(channel);
            }
            else
            {
                dropped.Add($"{channel.Id} ({channel.Name})");
            }
        }

        if (dropped.Count > 0)
        {
            logger?.LogWarning(
                "Dropped {Count} duplicate Live TV channel id(s): {Ids}. {Remedy}",
                dropped.Count,
                string.Join(", ", dropped.Take(MaxLoggedIds)),
                Remedy);
        }

        return result;
    }

    /// <summary>
    /// Removes programmes whose id was already returned for the channel, keeping the first one.
    /// </summary>
    /// <param name="programs">The programmes of one channel.</param>
    /// <param name="streamId">The provider stream id, for the log message.</param>
    /// <param name="logger">Optional logger for the dropped ids.</param>
    /// <returns>The programmes with unique ids, in their original order.</returns>
    public static IReadOnlyList<ProgramInfo> DistinctPrograms(IEnumerable<ProgramInfo> programs, int streamId, ILogger? logger)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<ProgramInfo> result = [];
        List<string> dropped = [];
        foreach (ProgramInfo program in programs)
        {
            if (program.Id is null || seen.Add(program.Id))
            {
                result.Add(program);
            }
            else
            {
                dropped.Add($"{program.Id} ({program.Name} @ {program.StartDate:u})");
            }
        }

        if (dropped.Count > 0)
        {
            logger?.LogWarning(
                "Dropped {Count} duplicate programme id(s) for provider stream {StreamId}: {Ids}. {Remedy}",
                dropped.Count,
                streamId,
                string.Join(", ", dropped.Take(MaxLoggedIds)),
                Remedy);
        }

        return result;
    }
}
