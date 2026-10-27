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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using Jellyfin.Xtream.Service;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.Tests;

public class XmlTvFallbackTests(ITestOutputHelper output)
{
    private static XmlTvChannelMatcher Matcher(params (string Id, string[] Names)[] channels) =>
        new(channels.Select(c => new XmlTvGuideService.XmlTvChannelData(c.Id, c.Names[0], [.. c.Names])));

    private static string? MatchId(XmlTvChannelMatcher matcher, string name, string? category = null, string? epgId = null)
    {
        matcher.Match(new XmlTvChannelKey(1, null, epgId, name, category), false, out string? id, out bool _);
        return id;
    }

    [Theory]
    [InlineData("ESPN (S)", "espn")]
    [InlineData("A&E (E)", "ae")]
    [InlineData("Food Network (W)", "foodnetwork")]
    [InlineData("US: HGTV (West) (F)", "hgtvwest")]
    [InlineData("HGTV West", "hgtvwest")]
    [InlineData("HGTV East", "hgtv")]
    [InlineData("HBO FHD (H)", "hbo")]
    [InlineData("US: CNN SD", "cnn")]
    public void NormalizeNameStripsFeedTagsAndKeepsWestFeeds(string name, string expected)
    {
        Assert.Equal(expected, XmlTvChannelMatcher.NormalizeName(name));
    }

    [Theory]
    [InlineData("US: CNN", null, "us")]
    [InlineData("|UK| BBC One", null, "uk")]
    [InlineData("Zee TV (India)", null, "in")]
    [InlineData("Star Plus", "INDIA | Entertainment", "in")]
    [InlineData("CNN", "Canada News", "ca")]
    [InlineData("CNN", null, null)]
    public void CountryHintComesFromNamePrefixOrCategory(string name, string? category, string? expected)
    {
        Assert.Equal(expected, XmlTvChannelMatcher.GetCountryHint(name, category));
    }

    [Theory]
    [InlineData("cnn.us", "us")]
    [InlineData("BBC.One.HD.uk", "uk")]
    [InlineData("Sky.gb", "uk")]
    [InlineData("Channel.usa", "us")]
    [InlineData("WCBS.us2", "us")]
    [InlineData("Some.tv", null)]
    [InlineData("noDots", null)]
    public void IdCountryIsTheDottedSuffix(string id, string? expected)
    {
        Assert.Equal(expected, XmlTvChannelMatcher.GetIdCountry(id));
    }

    [Fact]
    public void CountryHintBreaksATieAndAConflictingCountryIsRejected()
    {
        XmlTvChannelMatcher matcher = Matcher(("cnn.us", ["CNN"]), ("cnn.ca", ["CNN"]));
        Assert.Equal("cnn.us", MatchId(matcher, "US: CNN"));
        Assert.Equal("cnn.ca", MatchId(matcher, "CNN", "Canada | News"));
        Assert.Null(MatchId(matcher, "CNN"));

        // Only a UK feed exists: a US-labelled stream must not take it.
        XmlTvChannelMatcher ukOnly = Matcher(("nickjr.uk", ["Nick Jr."]));
        Assert.Null(MatchId(ukOnly, "Nick Jr.", "US Kids"));
        Assert.Equal("nickjr.uk", MatchId(ukOnly, "Nick Jr."));
    }

    [Fact]
    public void CallSignWinsForLocalStations()
    {
        XmlTvChannelMatcher matcher = Matcher(
            ("cbs.wcbs.newyork.us", ["CBS", "CBS (WCBS) New York"]),
            ("cbs.kcbs.losangeles.us", ["CBS", "CBS (KCBS) Los Angeles"]));
        Assert.Equal("cbs.wcbs.newyork.us", MatchId(matcher, "US: CBS (WCBS) New York HD"));
        Assert.Equal("cbs.kcbs.losangeles.us", MatchId(matcher, "CBS (KCBS)"));
        Assert.Null(MatchId(matcher, "CBS"));
    }

    [Fact]
    public void CallSignFoundOnlyInTheIdStillMatches()
    {
        XmlTvChannelMatcher matcher = Matcher(("WNBC.us", ["NBC 4 New York"]), ("KNBC.us", ["NBC 4 Los Angeles"]));
        Assert.Equal("WNBC.us", MatchId(matcher, "NBC (WNBC)"));
        Assert.Equal(["WNBC"], XmlTvChannelMatcher.GetCallSigns("NBC (WNBC) (WEST)"));
    }

    [Fact]
    public void EastAndWestFeedsAreKeptApart()
    {
        XmlTvChannelMatcher matcher = Matcher(("foodnetwork.us", ["Food Network"]), ("foodnetworkwest.us", ["Food Network West"]));
        Assert.Equal("foodnetwork.us", MatchId(matcher, "US: Food Network (E)"));
        Assert.Equal("foodnetworkwest.us", MatchId(matcher, "US: Food Network (West) (S)"));
        Assert.Equal("foodnetworkwest.us", MatchId(matcher, "Food Network Pacific"));
    }

    [Fact]
    public void LeadingCategoryWordsAreAWeakerKey()
    {
        XmlTvChannelMatcher matcher = Matcher(("tsn1.ca", ["TSN 1"]));
        Assert.Equal("tsn1.ca", MatchId(matcher, "CA: TSN 1"));
        Assert.Equal(["usespn", "espn"], XmlTvChannelMatcher.GetNameKeys("US ESPN", null));
    }

    [Fact]
    public void LooseAliasesNeedAWordInCommonWithTheChannel()
    {
        // Generated guides list other streams' names as aliases; "Sony Max" is not TV Asia.
        XmlTvChannelMatcher matcher = Matcher(
            ("tvasia.us", ["TV Asia", "Asian (Hindi) Sony Max (S)"]),
            ("racernetwork.us", ["Waterbear Network", "Racer Network"]));
        Assert.Null(MatchId(matcher, "US Asian (Hindi) Sony Max (S)", "US Asian"));
        Assert.Equal("tvasia.us", MatchId(matcher, "US Asian (Hindi) TV Asia (S)", "US Asian"));
        Assert.Equal("racernetwork.us", MatchId(matcher, "US: Racer Network (D)"));
        Assert.Equal(["sony", "max"], XmlTvChannelMatcher.GetSignificantWords("US Asian (Hindi) Sony Max (S)", "US Asian"));
    }

    [Fact]
    public void LanguageWordsAllowTheChannelsHomeCountry()
    {
        XmlTvChannelMatcher matcher = Matcher(("Sony.Max.SD.in", ["Sony Max SD"]), ("Zee.Marathi.in", ["Zee Marathi"]));
        Assert.Equal("Sony.Max.SD.in", MatchId(matcher, "US Asian (Hindi) Sony Max (S)", "US Asian"));
        Assert.Equal("Zee.Marathi.in", MatchId(matcher, "US Zee Marathi (F)", "US Asian"));

        // Without a language word the US tag still rules the Indian channel out.
        Assert.Null(MatchId(matcher, "US: Sony Max"));
        Assert.Equal(["in"], XmlTvChannelMatcher.GetLanguageCountries("US Asian (Hindi) Sony Max (S)"));
    }

    [Fact]
    public void AmbiguousNamesStayUnmatched()
    {
        XmlTvChannelMatcher matcher = Matcher(("fox.a.us", ["FOX"]), ("fox.b.us", ["FOX"]));
        XmlTvMatchKind kind = matcher.Match(new XmlTvChannelKey(1, null, null, "US: FOX"), false, out string? id, out bool ambiguous);
        Assert.Equal(XmlTvMatchKind.None, kind);
        Assert.Null(id);
        Assert.True(ambiguous);
    }

    [Fact]
    public void ParseSourceListSkipsCommentsBlanksAndDuplicates()
    {
        Assert.Equal(
            ["https://a/x.xml.gz", @"D:\g.xml"],
            XmlTvGuideService.ParseSourceList("  https://a/x.xml.gz \r\n\r\n# off\nhttps://A/x.xml.gz\nD:\\g.xml\n"));
        Assert.Empty(XmlTvGuideService.ParseSourceList(null));
    }

    [Theory]
    [InlineData("https://epgshare01.online/epgshare01/epg_ripper_US2.xml.gz", "epgshare01.online/epg_ripper_US2.xml.gz")]
    [InlineData(@"D:\EPG\guide.xml", "guide.xml")]
    public void SourceLabelIsHostAndFileName(string source, string expected)
    {
        Assert.Equal(expected, XmlTvGuideService.GetSourceLabel(source));
    }

    [Fact]
    public void ConditionalHeadersComeFromTheCacheMeta()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "https://example.com/g.xml");
        XmlTvGuideService.AddConditionalHeaders(request, new XmlTvGuideService.CacheMeta("\"abc-1\"", "Mon, 26 Oct 2026 10:00:00 GMT", DateTime.UtcNow));
        Assert.Equal("\"abc-1\"", request.Headers.IfNoneMatch.Single().ToString());
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero), request.Headers.IfModifiedSince);

        using HttpRequestMessage plain = new(HttpMethod.Get, "https://example.com/g.xml");
        XmlTvGuideService.AddConditionalHeaders(plain, null);
        Assert.Empty(plain.Headers.IfNoneMatch);
        Assert.Null(plain.Headers.IfModifiedSince);
    }

    [Fact]
    public void LaterSourcesOnlyFillChannelsTheEarlierOnesMiss()
    {
        string first = Write(Guide(("espn.us", "ESPN", "SportsCenter"), ("empty.us", "Empty Channel", null)));
        string second = Write(Guide(("espn.alt", "ESPN", "Other ESPN"), ("cnn.us", "CNN", "Newsroom"), ("empty.alt", "Empty Channel", "Filled")));
        try
        {
            XmlTvSource[] sources = [new(first, "one"), new(second, "two")];
            XmlTvChannelKey[] keys =
            [
                new(1, null, "espn.us", "ESPN"),
                new(2, null, null, "US: CNN"),
                new(3, null, null, "Nothing Like It"),
                new(4, null, null, "Empty Channel"),
            ];

            Assert.Equal(["SportsCenter"], Titles(sources, keys, 1));
            Assert.Equal(["Newsroom"], Titles(sources, keys, 2));
            Assert.Null(XmlTvGuideService.GetPrograms(sources, keys, null, 3, "c", DateTime.MinValue, DateTime.MaxValue));

            // Listed in the first guide without programmes, so the second guide may cover it.
            Assert.Equal(["Filled"], Titles(sources, keys, 4));

            IReadOnlyDictionary<int, XmlTvGuideService.XmlTvAssignment> assignments = XmlTvGuideService.GetAssignments(sources, keys);
            Assert.Equal(0, assignments[1].Source);
            Assert.Equal(XmlTvMatchKind.ById, assignments[1].Kind);
            Assert.Equal(1, assignments[2].Source);
            Assert.Equal(XmlTvMatchKind.ByName, assignments[2].Kind);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void BrokenFallbackKeepsTheEarlierGuide()
    {
        string first = Write(Guide(("espn.us", "ESPN", "SportsCenter")));
        string broken = Write("<tv><channel id=\"cnn.us\"><display-name>CNN</display-name></channel><programme start=");
        try
        {
            XmlTvSource[] sources = [new(first, "one"), new(broken, "broken")];
            XmlTvChannelKey[] keys = [new(1, null, "espn.us", "ESPN"), new(2, null, null, "CNN")];
            Assert.Equal(["SportsCenter"], Titles(sources, keys, 1));
            Assert.Null(XmlTvGuideService.GetPrograms(sources, keys, null, 2, "c", DateTime.MinValue, DateTime.MaxValue));
        }
        finally
        {
            File.Delete(first);
            File.Delete(broken);
        }
    }

    [Fact]
    public void UnknownOverrideBlocksAutoMatchingInEarlierSources()
    {
        string first = Write(Guide(("espn.us", "ESPN", "Wrong")));
        string second = Write(Guide(("espn.custom", "Something", "Right")));
        try
        {
            XmlTvSource[] sources = [new(first, "one"), new(second, "two")];
            XmlTvChannelKey[] keys = [new(1, "espn.custom", "espn.us", "ESPN")];
            Assert.Equal(["Right"], Titles(sources, keys, 1));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    /// <summary>
    /// Optional coverage measurement: XTREAM_EPG_FILE (provider XMLTV), XTREAM_STREAMS_FILE (saved channel list with
    /// stream_id, name, epg_channel_id and optionally category_name), XTREAM_FALLBACK_EPG_FILES (';'-separated XMLTV
    /// files, measured one at a time after the provider guide) and XTREAM_COVERAGE_OUT (a TSV of all matches).
    /// </summary>
    [Fact]
    public void RealFallbackCoverage()
    {
        string? epg = Environment.GetEnvironmentVariable("XTREAM_EPG_FILE");
        string? streams = Environment.GetEnvironmentVariable("XTREAM_STREAMS_FILE");
        string? fallbacks = Environment.GetEnvironmentVariable("XTREAM_FALLBACK_EPG_FILES");
        if (string.IsNullOrEmpty(epg) || string.IsNullOrEmpty(streams))
        {
            return;
        }

        List<XmlTvChannelKey> keys = JArray.Parse(File.ReadAllText(streams, Encoding.UTF8))
            .Select(s => new XmlTvChannelKey(
                (int)s["stream_id"]!,
                null,
                (string?)s["epg_channel_id"],
                (string?)s["name"] ?? string.Empty,
                (string?)s["category_name"] ?? (string?)s["category"]))
            .DistinctBy(k => k.StreamId)
            .ToList();

        StringBuilder tsv = new();
        tsv.AppendLine("source\tstream_id\tname\tcategory\tkind\tepg_id\tepg_name\tprogrammes");
        int Covered(IReadOnlyList<XmlTvSource> sources, string label, bool all)
        {
            Stopwatch watch = Stopwatch.StartNew();
            IReadOnlyDictionary<int, XmlTvGuideService.XmlTvAssignment> map = XmlTvGuideService.GetAssignments(sources, keys);
            int covered = 0;
            Dictionary<string, string>[] names = sources.Select(s => XmlTvGuideService.GetChannels(s.Path).ToDictionary(c => c.Id, c => c.Name)).ToArray();
            foreach (XmlTvChannelKey key in keys)
            {
                int count = XmlTvGuideService.GetPrograms(sources, keys, null, key.StreamId, "c", DateTime.MinValue, DateTime.MaxValue)?.Count() ?? 0;
                if (count > 0)
                {
                    covered++;
                }

                if (map.TryGetValue(key.StreamId, out XmlTvGuideService.XmlTvAssignment? a) && (all || a.Source > 0))
                {
                    tsv.Append(label).Append('\t').Append(key.StreamId).Append('\t').Append(key.Name).Append('\t').Append(key.Category).Append('\t')
                        .Append(a.Kind).Append('\t').Append(a.EpgId).Append('\t').Append(names[a.Source].GetValueOrDefault(a.EpgId)).Append('\t')
                        .Append(count.ToString(CultureInfo.InvariantCulture)).AppendLine();
                }
            }

            output.WriteLine($"{label}: {covered}/{keys.Count} streams with programmes ({watch.Elapsed.TotalSeconds:F1}s)");
            return covered;
        }

        XmlTvSource provider = new(epg, "provider");
        int baseline = Covered([provider], "provider", all: true);
        foreach (string file in (fallbacks ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int with = Covered([provider, new XmlTvSource(file, Path.GetFileName(file))], Path.GetFileName(file), all: false);
            output.WriteLine($"  +{with - baseline} from {Path.GetFileName(file)}");
        }

        string? combined = Environment.GetEnvironmentVariable("XTREAM_FALLBACK_COMBINED");
        if (!string.IsNullOrEmpty(combined))
        {
            List<XmlTvSource> list = [provider, .. combined.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(f => new XmlTvSource(f, Path.GetFileName(f)))];
            long before = GC.GetTotalMemory(true);
            int with = Covered(list, "combined", all: false);
            output.WriteLine($"  combined +{with - baseline}; retained ~{(GC.GetTotalMemory(true) - before) / (1024 * 1024)} MB");
        }

        string? outFile = Environment.GetEnvironmentVariable("XTREAM_COVERAGE_OUT");
        if (!string.IsNullOrEmpty(outFile))
        {
            File.WriteAllText(outFile, tsv.ToString(), Encoding.UTF8);
        }

        Assert.True(baseline > 0);
    }

    private static List<string> Titles(IReadOnlyList<XmlTvSource> sources, IReadOnlyCollection<XmlTvChannelKey> keys, int streamId) =>
        XmlTvGuideService.GetPrograms(sources, keys, null, streamId, "c", DateTime.MinValue, DateTime.MaxValue)!
            .Select(p => p.Name ?? string.Empty)
            .ToList();

    private static string Guide(params (string Id, string Name, string? Title)[] channels)
    {
        StringBuilder builder = new("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<tv>\n");
        foreach ((string id, string name, string? _) in channels)
        {
            builder.Append(CultureInfo.InvariantCulture, $"<channel id=\"{id}\"><display-name>{name}</display-name></channel>\n");
        }

        foreach ((string id, string _, string? title) in channels.Where(c => c.Title is not null))
        {
            builder.Append(CultureInfo.InvariantCulture, $"<programme start=\"20261026000000 +0000\" stop=\"20261026010000 +0000\" channel=\"{id}\"><title>{title}</title></programme>\n");
        }

        return builder.Append("</tv>\n").ToString();
    }

    private static string Write(string content)
    {
        string path = Path.Join(Path.GetTempPath(), $"xmltv-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, content);
        return path;
    }
}
