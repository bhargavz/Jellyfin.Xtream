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
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Service;
using Xunit;

namespace Jellyfin.Xtream.Tests;

public class EventChannelTimeTests
{
    // 2026-10-27 12:00 UTC; US Eastern is on EDT (UTC-4) until 2026-11-01, London on GMT since 2026-10-25.
    private static readonly DateTime _now = Utc(2026, 10, 27, 12, 0);

    private static DateTime Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static DateTime Parse(string name, DateTime? now = null)
    {
        Assert.True(EventChannelTime.TryParseStart(name, now ?? _now, out DateTime start), $"Expected a date in \"{name}\"");
        return start;
    }

    [Theory]
    [InlineData("NFL 14: Eagles vs Cowboys (10.26 8:15PM ET)")]
    [InlineData("NFL 14: Eagles vs Cowboys (10.26 8:15 PM ET)")]
    [InlineData("NFL 14: Eagles vs Cowboys (10/26 8:15 PM)")]
    [InlineData("NFL 14: Eagles vs Cowboys 10-26 8:15pm EST")]
    [InlineData("NFL 14: Eagles vs Cowboys (10.26 @ 8:15 p.m. EDT)")]
    [InlineData("NFL 14: Eagles vs Cowboys | Oct 26 20:15")]
    [InlineData("NFL 14: Eagles vs Cowboys | Oct. 26 8:15PM ET")]
    [InlineData("NFL 14: Eagles vs Cowboys | October 26th, 2026 8:15pm ET")]
    [InlineData("NFL 14: Eagles vs Cowboys | Mon Oct 26 at 8:15 PM ET")]
    [InlineData("NFL 14: Eagles vs Cowboys | 26 Oct 20:15")]
    [InlineData("NFL 14: Eagles vs Cowboys (10/26/2026 8:15 PM ET)")]
    [InlineData("NFL 14: Eagles vs Cowboys (10/26/26 8:15 PM ET)")]
    [InlineData("NFL 14: Eagles vs Cowboys 2026-10-26 20:15")]
    [InlineData("NFL 14: Eagles vs Cowboys 2026-10-26T20:15")]
    [InlineData("NFL 14: Eagles vs Cowboys (5:15PM PT 10.26)", false)]
    public void ParsesEasternEventShapes(string name, bool expected = true)
    {
        Assert.Equal(expected, EventChannelTime.TryParseStart(name, _now, out DateTime start));
        if (expected)
        {
            // 20:15 EDT.
            Assert.Equal(Utc(2026, 10, 27, 0, 15), start);
            Assert.Equal(DateTimeKind.Utc, start.Kind);
        }
    }

    [Fact]
    public void HonoursOtherZones()
    {
        Assert.Equal(Utc(2026, 10, 27, 0, 15), Parse("NBA (10.26 5:15PM PT)"));
        Assert.Equal(Utc(2026, 10, 27, 0, 15), Parse("NBA (10.26 7:15PM CT)"));
        Assert.Equal(Utc(2026, 10, 27, 0, 15), Parse("NBA (10.26 6:15PM MT)"));
        Assert.Equal(Utc(2026, 10, 26, 20, 15), Parse("EPL: Arsenal v Spurs 26 Oct 20:15 UK"));
        Assert.Equal(Utc(2026, 10, 26, 20, 15), Parse("EPL: Arsenal v Spurs 26 Oct 20:15 GMT"));
        Assert.Equal(Utc(2026, 10, 26, 19, 15), Parse("Bundesliga 26.10 20:15 CET"));
        Assert.Equal(Utc(2026, 10, 26, 20, 15), Parse("UFC 10.26 20:15 UTC"));
    }

    [Fact]
    public void FollowsDaylightSavingTime()
    {
        // British Summer Time is still on before 2026-10-25.
        Assert.Equal(Utc(2026, 10, 20, 19, 15), Parse("EPL 20 Oct 20:15 UK"));
        // EST in December.
        Assert.Equal(Utc(2026, 12, 6, 1, 15), Parse("NFL (12.05 8:15PM ET)"));
    }

    [Fact]
    public void HandlesNoonAndMidnight()
    {
        Assert.Equal(Utc(2026, 10, 26, 4, 30), Parse("MLB (10.26 12:30AM ET)"));
        Assert.Equal(Utc(2026, 10, 26, 16, 0), Parse("MLB (10.26 12PM ET)"));
        Assert.Equal(Utc(2026, 10, 26, 4, 0), Parse("MLB (10.26 0:00)"));
    }

    [Fact]
    public void DayFirstWhenTheFirstNumberCannotBeAMonth()
    {
        Assert.Equal(Utc(2026, 10, 27, 0, 15), Parse("Ligue 1 26.10 20:15"));
        Assert.Equal(Utc(2026, 10, 27, 0, 15), Parse("Ligue 1 26/10 8:15 PM"));
    }

    [Fact]
    public void MonthNameDateWithoutTimeCountsAsEndOfDay()
    {
        // 23:59 EDT.
        Assert.Equal(Utc(2026, 10, 27, 3, 59), Parse("PPV: Boxing Oct 26"));
        Assert.Equal(Utc(2026, 10, 27, 3, 59), Parse("PPV: Boxing October 26"));
        Assert.Equal(Utc(2026, 10, 27, 3, 59), Parse("PPV: Boxing 26 October"));
    }

    [Fact]
    public void InfersLastYearForDatesFarInTheFuture()
    {
        DateTime january = Utc(2026, 1, 5, 12, 0);
        Assert.Equal(Utc(2025, 12, 31, 1, 0), Parse("NHL (12.30 8PM ET)", january));
        Assert.True(EventChannelTime.IsStale("NHL (12.30 8PM ET)", january));
    }

    [Fact]
    public void InfersNextYearForDatesFarInThePast()
    {
        DateTime december = Utc(2026, 12, 30, 12, 0);
        Assert.Equal(Utc(2027, 1, 3, 0, 0), Parse("NFL (01.02 7PM ET)", december));
        Assert.False(EventChannelTime.IsStale("NFL (01.02 7PM ET)", december));
    }

    [Fact]
    public void KeepsNearFutureDatesInThisYear()
    {
        Assert.Equal(Utc(2026, 12, 31, 16, 0), Parse("NYE (12/31 11AM ET)"));
        Assert.Equal(Utc(2026, 6, 1, 0, 0), Parse("Old (05/31 8PM ET)"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("US: ESPN")]
    [InlineData("US: ESPN HD")]
    [InlineData("NBA: Lakers vs Celtics 7:30PM ET")]
    [InlineData("NFL 14: Eagles vs Cowboys")]
    [InlineData("Sky Sports 1.5 HD")]
    [InlineData("Channel 4.0")]
    [InlineData("Dolby 5.1 Test")]
    [InlineData("Ratio 16/9 widescreen")]
    [InlineData("ESPN+ 101")]
    [InlineData("10/26 8")]
    [InlineData("Event 13.13 8PM")]
    [InlineData("Event 02.30 8PM")]
    [InlineData("Event 10.26 25:00")]
    [InlineData("Event 10.26 13PM")]
    [InlineData("Event 10.26 8:75")]
    [InlineData("Mayhem 5")]
    [InlineData("Octagon 26 Live")]
    [InlineData("v1.2.3 20:15")]
    public void NamesWithoutADateAreNotParsed(string? name)
    {
        Assert.False(EventChannelTime.TryParseStart(name, _now, out _));
        Assert.False(EventChannelTime.IsStale(name, _now));
    }

    [Fact]
    public void StaleOnlyAfterTwelveHours()
    {
        const string Name = "NFL 14: Eagles vs Cowboys (10.26 8:15PM ET)";
        DateTime start = Utc(2026, 10, 27, 0, 15);
        Assert.False(EventChannelTime.IsStale(Name, start.AddHours(-1)));
        Assert.False(EventChannelTime.IsStale(Name, start.AddHours(3)));
        Assert.False(EventChannelTime.IsStale(Name, start.AddHours(12)));
        Assert.True(EventChannelTime.IsStale(Name, start.AddHours(12).AddMinutes(1)));
        Assert.True(EventChannelTime.IsStale(Name, start.AddDays(3)));
    }

    [Fact]
    public void ResolvesZonesOnThisPlatform()
    {
        Assert.Equal(TimeSpan.FromHours(-5), EventChannelTime.ResolveZone("ET").BaseUtcOffset);
        Assert.Equal(TimeSpan.FromHours(-5), EventChannelTime.ResolveZone("est").BaseUtcOffset);
        Assert.Equal(TimeSpan.FromHours(-8), EventChannelTime.ResolveZone("PT").BaseUtcOffset);
        Assert.Equal(TimeSpan.Zero, EventChannelTime.ResolveZone("UK").BaseUtcOffset);
        Assert.Equal(TimeSpan.FromHours(1), EventChannelTime.ResolveZone("CET").BaseUtcOffset);
        Assert.Same(TimeZoneInfo.Utc, EventChannelTime.ResolveZone("XYZ"));
    }

    [Fact]
    public void FilterDropsOnlyStaleEventChannels()
    {
        List<StreamInfo> streams =
        [
            new() { StreamId = 1, Name = "US: ESPN" },
            new() { StreamId = 2, Name = "NFL 01: Bears vs Packers (10.25 1:00PM ET)" },
            new() { StreamId = 3, Name = "NFL 14: Eagles vs Cowboys (10.26 8:15PM ET)" },
            new() { StreamId = 4, Name = "NFL 15: Jets vs Bills (10.27 7:00PM ET)" },
            new() { StreamId = 5, Name = "NFL 16: TBA" },
        ];

        List<int> kept = StreamService.WithoutStaleEvents(streams, _now).Select(s => s.StreamId).ToList();
        Assert.Equal([1, 3, 4, 5], kept);
    }
}
