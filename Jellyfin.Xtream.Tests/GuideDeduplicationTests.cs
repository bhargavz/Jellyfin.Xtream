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
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Xtream.Tests;

public class GuideDeduplicationTests
{
    private static readonly DateTime Start = new(2026, 10, 27, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DuplicateChannelIdsAreDroppedAndLogged()
    {
        ListLogger logger = new();
        IReadOnlyList<ChannelInfo> channels = GuideDeduplication.DistinctChannels(
            [
                new ChannelInfo { Id = "c1", Name = "ESPN" },
                new ChannelInfo { Id = "c2", Name = "CNN" },
                new ChannelInfo { Id = "C1", Name = "ESPN (copy)" },
            ],
            logger);

        Assert.Equal(["ESPN", "CNN"], channels.Select(c => c.Name));
        string message = Assert.Single(logger.Messages);
        Assert.Contains("C1 (ESPN (copy))", message, StringComparison.Ordinal);
        Assert.Contains("UNIQUE constraint failed", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UniqueChannelsAreNotLogged()
    {
        ListLogger logger = new();
        IReadOnlyList<ChannelInfo> channels = GuideDeduplication.DistinctChannels(
            [new ChannelInfo { Id = "c1" }, new ChannelInfo { Id = "c2" }],
            logger);

        Assert.Equal(2, channels.Count);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public void DuplicateProgramIdsAreDroppedAndLoggedWithStreamId()
    {
        ListLogger logger = new();
        IReadOnlyList<ProgramInfo> programs = GuideDeduplication.DistinctPrograms(
            [
                new ProgramInfo { Id = "p1", Name = "First", StartDate = Start },
                new ProgramInfo { Id = "p2", Name = "Second", StartDate = Start.AddHours(1) },
                new ProgramInfo { Id = "p1", Name = "Repeat", StartDate = Start },
            ],
            1234,
            logger);

        Assert.Equal(["First", "Second"], programs.Select(p => p.Name));
        string message = Assert.Single(logger.Messages);
        Assert.Contains("1234", message, StringComparison.Ordinal);
        Assert.Contains("p1 (Repeat", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramsWithoutIdAreKept()
    {
        IReadOnlyList<ProgramInfo> programs = GuideDeduplication.DistinctPrograms(
            [new ProgramInfo(), new ProgramInfo()],
            1,
            null);

        Assert.Equal(2, programs.Count);
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
