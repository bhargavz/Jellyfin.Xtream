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
using System.Diagnostics;
using System.IO;
using System.Linq;
using Jellyfin.Xtream.Service;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.Tests;

public class XmlTvMatchingTests(ITestOutputHelper output)
{
    private const string Guide = """
        <?xml version="1.0" encoding="utf-8"?>
        <tv>
          <channel id="ESPN.us"><display-name>ESPN</display-name><display-name>ESPN HD</display-name></channel>
          <channel id="cnn.us"><display-name>CNN</display-name></channel>
          <channel id="fox1"><display-name>FOX</display-name></channel>
          <channel id="fox2"><display-name>FOX</display-name></channel>
          <channel id="unused"><display-name>Unused</display-name></channel>
          <programme start="20261026000000 +0000" stop="20261026010000 +0000" channel="unused"><title>Skip 1</title></programme>
          <programme start="20261026000000 +0000" stop="20261026010000 +0000" channel="ESPN.us"><title>SportsCenter</title></programme>
          <programme start="20261026000000 +0000" stop="20261026010000 +0000" channel="unused"><title>Skip 2</title></programme>
          <programme start="20261026010000 +0000" stop="20261026020000 +0000" channel="cnn.us"><title>News</title></programme>
          <programme start="20261026010000 +0000" stop="20261026020000 +0000" channel="ESPN.us"><title>NFL Live</title></programme>
        </tv>
        """;

    [Theory]
    [InlineData("US: ESPN HD", "espn")]
    [InlineData("USA: ESPN (S)", "espn")]
    [InlineData("|UK| Sky Sports Main Event FHD", "skysportsmainevent")]
    [InlineData("ESPN", "espn")]
    [InlineData("A&E", "ae")]
    [InlineData("FOX - Sports 1", "foxsports1")]
    public void NormalizeNameStripsDecorations(string name, string expected)
    {
        Assert.Equal(expected, XmlTvGuideService.NormalizeName(name));
    }

    [Fact]
    public void ChannelsMatchByOverrideThenIdThenUniqueName()
    {
        string path = WriteGuide(Guide);
        try
        {
            XmlTvChannelKey[] keys =
            [
                new(1, null, "espn.US", "Whatever"),   // id, case-insensitive
                new(2, null, "junk [VIP]", "US: CNN HD"), // name
                new(3, null, null, "FOX"),              // ambiguous name -> unmatched
                new(4, "cnn.us", "ESPN.us", "ESPN"),    // override wins
            ];

            Assert.Equal(["SportsCenter", "NFL Live"], Titles(path, keys, 1));
            Assert.Equal(["News"], Titles(path, keys, 2));
            Assert.Null(XmlTvGuideService.GetPrograms(path, keys, null, 3, "c", DateTime.MinValue, DateTime.MaxValue));
            Assert.Equal(["News"], Titles(path, keys, 4));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnmatchedChannelsAreNotKeptAndSkippingDoesNotDropNeighbours()
    {
        string path = WriteGuide(Guide);
        try
        {
            XmlTvChannelKey[] keys = [new(1, null, "ESPN.us", "ESPN")];

            // Both ESPN programmes directly follow a skipped one and must survive.
            Assert.Equal(["SportsCenter", "NFL Live"], Titles(path, keys, 1));
            Assert.Equal(5, XmlTvGuideService.GetChannels(path).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Optional check against a real provider guide: set XTREAM_EPG_FILE to an XMLTV file and
    /// XTREAM_STREAMS_FILE to a saved get_live_streams JSON response.
    /// </summary>
    [Fact]
    public void RealGuideCoverage()
    {
        string? epg = Environment.GetEnvironmentVariable("XTREAM_EPG_FILE");
        string? streams = Environment.GetEnvironmentVariable("XTREAM_STREAMS_FILE");
        if (string.IsNullOrEmpty(epg) || string.IsNullOrEmpty(streams))
        {
            return;
        }

        List<XmlTvChannelKey> keys = JArray.Parse(File.ReadAllText(streams))
            .Select(s => new XmlTvChannelKey((int)s["stream_id"]!, null, (string?)s["epg_channel_id"], (string?)s["name"] ?? string.Empty))
            .DistinctBy(k => k.StreamId)
            .ToList();

        long before = GC.GetTotalMemory(true);
        Stopwatch watch = Stopwatch.StartNew();
        int withPrograms = keys.Count(k => XmlTvGuideService.GetPrograms(epg, keys, null, k.StreamId, "c", DateTime.MinValue, DateTime.MaxValue)?.Any() == true);
        watch.Stop();
        long after = GC.GetTotalMemory(true);

        output.WriteLine($"{withPrograms}/{keys.Count} streams have XMLTV programmes; load {watch.Elapsed.TotalSeconds:F1}s; retained ~{(after - before) / (1024 * 1024)} MB");
        Dictionary<string, string> names = XmlTvGuideService.GetChannels(epg).ToDictionary(c => c.Id, c => c.Name);
        foreach ((int streamId, string epgId) in XmlTvGuideService.GetMapping(epg, keys))
        {
            XmlTvChannelKey key = keys.First(k => k.StreamId == streamId);
            if (!string.Equals(key.ProviderEpgId, epgId, StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine($"name match: {key.Name} -> {epgId} ({names.GetValueOrDefault(epgId)})");
            }
        }

        Assert.True(withPrograms > 0);
    }

    private static List<string> Titles(string path, IReadOnlyCollection<XmlTvChannelKey> keys, int streamId)
    {
        return XmlTvGuideService.GetPrograms(path, keys, null, streamId, "c", DateTime.MinValue, DateTime.MaxValue)!
            .Select(p => p.Name ?? string.Empty)
            .ToList();
    }

    private static string WriteGuide(string content)
    {
        string path = Path.Join(Path.GetTempPath(), $"xmltv-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, content);
        return path;
    }
}
