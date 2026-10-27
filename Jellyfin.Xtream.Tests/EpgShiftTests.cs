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
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Jellyfin.Xtream.Tests;

public class EpgShiftTests
{
    private static readonly DateTime Start = new(2026, 10, 27, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoOverrideMeansNoShift()
    {
        Assert.Equal(TimeSpan.Zero, EpgShift.GetEpgShift((ChannelOverrides?)null));
        Assert.Equal(TimeSpan.Zero, EpgShift.GetEpgShift(new ChannelOverrides()));
        Assert.Equal(TimeSpan.Zero, EpgShift.GetEpgShift(new ChannelOverrides { EpgShiftMinutes = 0 }));
        Assert.Equal(TimeSpan.Zero, EpgShift.GetEpgShift(null, 1));
        Assert.Equal(TimeSpan.Zero, EpgShift.GetEpgShift(new Dictionary<int, ChannelOverrides>(), 1));
    }

    [Theory]
    [InlineData(60, 1, 0)]
    [InlineData(-90, -1, -30)]
    [InlineData(30, 0, 30)]
    [InlineData(-120, -2, 0)]
    public void ShiftIsReadFromMinutes(int minutes, int hours, int remainder)
    {
        Assert.Equal(
            new TimeSpan(hours, remainder, 0),
            EpgShift.GetEpgShift(new ChannelOverrides { EpgShiftMinutes = minutes }));
    }

    [Fact]
    public void ShiftIsClampedToOneDay()
    {
        Assert.Equal(TimeSpan.FromDays(1), EpgShift.GetEpgShift(new ChannelOverrides { EpgShiftMinutes = 99999 }));
        Assert.Equal(TimeSpan.FromDays(-1), EpgShift.GetEpgShift(new ChannelOverrides { EpgShiftMinutes = -99999 }));
    }

    [Fact]
    public void ShiftIsLookedUpPerStream()
    {
        Dictionary<int, ChannelOverrides> overrides = new()
        {
            [7] = new ChannelOverrides { EpgShiftMinutes = -60 },
            [8] = new ChannelOverrides { Name = "No shift" },
        };

        Assert.Equal(TimeSpan.FromHours(-1), EpgShift.GetEpgShift(overrides, 7));
        Assert.Equal(TimeSpan.Zero, EpgShift.GetEpgShift(overrides, 8));
        Assert.Equal(TimeSpan.Zero, EpgShift.GetEpgShift(overrides, 9));
    }

    [Fact]
    public void ApplyMovesStartAndEndButKeepsIds()
    {
        List<ProgramInfo> programs =
        [
            new ProgramInfo { Id = "a", StartDate = Start, EndDate = Start.AddHours(1) },
            new ProgramInfo { Id = "b", StartDate = Start.AddHours(1), EndDate = Start.AddHours(2) },
        ];

        IReadOnlyList<ProgramInfo> shifted = EpgShift.Apply(programs, TimeSpan.FromMinutes(-90));

        Assert.Equal(["a", "b"], shifted.Select(p => p.Id));
        Assert.Equal(Start.AddMinutes(-90), shifted[0].StartDate);
        Assert.Equal(Start.AddMinutes(-30), shifted[0].EndDate);
        Assert.Equal(Start.AddMinutes(-30), shifted[1].StartDate);
        Assert.Equal(Start.AddMinutes(30), shifted[1].EndDate);
    }

    [Fact]
    public void UnshiftMovesWindowBackAndClamps()
    {
        Assert.Equal(Start.AddHours(-2), EpgShift.Unshift(Start, TimeSpan.FromHours(2)));
        Assert.Equal(Start.AddHours(2), EpgShift.Unshift(Start, TimeSpan.FromHours(-2)));
        Assert.Equal(DateTime.MinValue, EpgShift.Unshift(DateTime.MinValue, TimeSpan.FromHours(1)));
        Assert.Equal(DateTime.MaxValue, EpgShift.Unshift(DateTime.MaxValue, TimeSpan.FromHours(-1)));
    }

    [Fact]
    public void ZeroShiftLeavesTimesAlone()
    {
        ProgramInfo program = new() { Id = "a", StartDate = Start, EndDate = Start.AddHours(1) };
        ProgramInfo result = EpgShift.Apply([program], TimeSpan.Zero).Single();
        Assert.Equal(Start, result.StartDate);
        Assert.Equal(Start.AddHours(1), result.EndDate);
    }

    [Fact]
    public void ShiftAppliesToXmlTvProgrammes()
    {
        const string Guide = """
            <?xml version="1.0" encoding="utf-8"?>
            <tv>
              <channel id="news-1"><display-name>News</display-name></channel>
              <programme start="20261027180000 +0000" stop="20261027190000 +0000" channel="news-1"><title>Evening News</title></programme>
            </tv>
            """;
        string path = Path.Join(Path.GetTempPath(), $"xmltv-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, Guide);
        try
        {
            TimeSpan shift = EpgShift.GetEpgShift(new ChannelOverrides { EpgShiftMinutes = 120 });
            ProgramInfo program = EpgShift.Apply(
                XmlTvGuideService.GetPrograms(path, "news-1", "channel", DateTime.MinValue, DateTime.MaxValue),
                shift).Single();

            Assert.Equal(Start.AddHours(2), program.StartDate);
            Assert.Equal(Start.AddHours(3), program.EndDate);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ShiftSurvivesConfigurationSerialization()
    {
        PluginConfiguration config = new();
        config.LiveTvOverrides[42] = new ChannelOverrides { EpgShiftMinutes = -75 };

        XmlSerializer serializer = new(typeof(PluginConfiguration));
        using StringWriter writer = new();
        serializer.Serialize(writer, config);
        using StringReader reader = new(writer.ToString());
        PluginConfiguration copy = (PluginConfiguration)serializer.Deserialize(reader)!;

        Assert.Equal(-75, copy.LiveTvOverrides[42].EpgShiftMinutes);
    }
}
