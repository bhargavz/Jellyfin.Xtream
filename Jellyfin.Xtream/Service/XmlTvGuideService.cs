#pragma warning disable
// Custom XMLTV guide support for Jellyfin.Xtream.
// This file is part of the Custom EPG overlay and remains GPL-3.0-or-later,
// consistent with the upstream Jellyfin.Xtream project.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A channel exposed by an XMLTV file.
/// </summary>
public sealed class XmlTvChannelInfo
{
    /// <summary>Gets or sets the XMLTV channel id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred display name.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Identifies one Live TV stream for guide matching.
/// </summary>
/// <param name="StreamId">The Xtream stream id.</param>
/// <param name="OverrideEpgId">The XMLTV id chosen by the user, if any.</param>
/// <param name="ProviderEpgId">The epg_channel_id reported by the provider.</param>
/// <param name="Name">The channel name.</param>
public sealed record XmlTvChannelKey(int StreamId, string? OverrideEpgId, string? ProviderEpgId, string Name);

/// <summary>
/// Loads and caches an XMLTV guide from a local file or an http(s) URL.
/// Only programmes of channels that map to a configured stream are kept in memory,
/// so very large provider guides (hundreds of MB) stay cheap.
/// </summary>
public static partial class XmlTvGuideService
{
    private static readonly object CacheLock = new();
    private static readonly SemaphoreSlim DownloadLock = new(1, 1);
    private static XmlTvCache? _cache;
    private static XmlTvChannelsCache? _channelsCache;

    /// <summary>Returns whether the configured source is a URL rather than a local file.</summary>
    /// <param name="source">The configured source.</param>
    /// <returns>True for http(s) URLs.</returns>
    public static bool IsUrl(string source)
    {
        return Uri.TryCreate(source.Trim(), UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// Resolves the configured source to a local file, downloading and caching it when it is a URL.
    /// A stale download is reused when refreshing fails.
    /// </summary>
    public static async Task<string> ResolveSourceAsync(
        string source,
        IHttpClientFactory httpClientFactory,
        string cacheDirectory,
        int refreshHours,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidOperationException("The custom XMLTV source is empty.");
        }

        source = source.Trim();
        if (!IsUrl(source))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(source));
        }

        Uri uri = new(source);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
        string target = Path.Join(cacheDirectory, $"xmltv-{hash}.xml");
        TimeSpan maxAge = TimeSpan.FromHours(Math.Max(1, refreshHours));

        if (IsFresh(target, maxAge))
        {
            return target;
        }

        await DownloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsFresh(target, maxAge))
            {
                return target;
            }

            Directory.CreateDirectory(cacheDirectory);
            string raw = target + ".download";
            string part = target + ".part";
            try
            {
                using HttpClient client = httpClientFactory.CreateClient(NamedClient.Default);
                using HttpRequestMessage request = new(HttpMethod.Get, uri);
                request.Headers.AcceptEncoding.ParseAdd("gzip");
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    FileStream file = new(raw, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.Asynchronous);
                    await using (file.ConfigureAwait(false))
                    {
                        await body.CopyToAsync(file, timeout.Token).ConfigureAwait(false);
                    }
                }

                await DecompressIfNeededAsync(raw, part, timeout.Token).ConfigureAwait(false);
                File.Move(part, target, overwrite: true);
                lock (CacheLock)
                {
                    // A new download invalidates the parsed guide even if size and timestamp happen to match.
                    _cache = null;
                    _channelsCache = null;
                }

                logger.LogInformation("Downloaded XMLTV guide from {Host} ({Size} MB)", uri.Host, new FileInfo(target).Length / (1024 * 1024));
            }
            catch (Exception exception) when (File.Exists(target) && exception is HttpRequestException or IOException or OperationCanceledException or InvalidDataException)
            {
                logger.LogWarning(exception, "Refreshing the XMLTV guide from {Host} failed, reusing the cached copy", uri.Host);
            }
            finally
            {
                File.Delete(raw);
                File.Delete(part);
            }

            return target;
        }
        finally
        {
            DownloadLock.Release();
        }
    }

    /// <summary>Returns all channels in the XMLTV file, without loading programmes.</summary>
    /// <param name="path">The local XMLTV file.</param>
    /// <returns>The channels, ordered by name.</returns>
    public static IReadOnlyList<XmlTvChannelInfo> GetChannels(string path)
    {
        FileInfo file = GetFile(path);
        lock (CacheLock)
        {
            if (_channelsCache is not null && _channelsCache.Matches(file))
            {
                return _channelsCache.Channels;
            }

            List<XmlTvChannelInfo> channels = ReadChannels(file.FullName)
                .Select(channel => new XmlTvChannelInfo { Id = channel.Id, Name = channel.Name })
                .OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(channel => channel.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _channelsCache = new XmlTvChannelsCache(file.FullName, file.LastWriteTimeUtc, file.Length, channels);
            return channels;
        }
    }

    /// <summary>Returns Jellyfin guide programs for one explicitly given XMLTV channel id.</summary>
    public static IEnumerable<ProgramInfo> GetPrograms(
        string path,
        string epgId,
        string jellyfinChannelId,
        DateTime startDateUtc,
        DateTime endDateUtc)
    {
        XmlTvChannelKey key = new(0, epgId, null, string.Empty);
        return GetPrograms(path, [key], null, 0, jellyfinChannelId, startDateUtc, endDateUtc) ?? [];
    }

    /// <summary>
    /// Returns Jellyfin guide programs for one stream, matching it to an XMLTV channel by
    /// override, provider epg id, then normalized name.
    /// </summary>
    /// <returns>The programmes, or null when the stream does not map to an XMLTV channel.</returns>
    public static IEnumerable<ProgramInfo>? GetPrograms(
        string path,
        IReadOnlyCollection<XmlTvChannelKey> keys,
        ILogger? logger,
        int streamId,
        string jellyfinChannelId,
        DateTime startDateUtc,
        DateTime endDateUtc)
    {
        XmlTvCache cache = EnsureCache(path, keys, logger);
        if (!cache.Mapping.TryGetValue(streamId, out string? epgId)
            || !cache.Programs.TryGetValue(epgId, out List<XmlTvProgramData>? programs))
        {
            return null;
        }

        return programs
            .Where(program => program.EndDateUtc >= startDateUtc && program.StartDateUtc < endDateUtc)
            .Select(program => ToProgramInfo(program, jellyfinChannelId))
            .ToList();
    }

    /// <summary>Returns the stream id to XMLTV channel id mapping used for the given keys.</summary>
    internal static IReadOnlyDictionary<int, string> GetMapping(string path, IReadOnlyCollection<XmlTvChannelKey> keys)
    {
        return EnsureCache(path, keys, null).Mapping;
    }

    /// <summary>
    /// Normalizes a channel name for matching: drops country prefixes, quality tags,
    /// bracketed suffixes and punctuation. "US: ESPN HD (S)" becomes "espn".
    /// </summary>
    /// <param name="name">The channel name.</param>
    /// <returns>The normalized name.</returns>
    public static string NormalizeName(string name)
    {
        string value = name.ToLowerInvariant();
        value = BracketRegex().Replace(value, " ");
        value = PrefixRegex().Replace(value, " ");
        value = QualityRegex().Replace(value, " ");
        return NonAlphanumericRegex().Replace(value, string.Empty);
    }

    /// <summary>
    /// Maps each stream to an XMLTV channel id: user override, then the provider epg id
    /// (case-insensitive), then a unique normalized display-name match.
    /// </summary>
    internal static Dictionary<int, string> MapChannels(
        IReadOnlyCollection<XmlTvChannelKey> keys,
        IReadOnlyCollection<XmlTvChannelData> channels,
        out MatchStats stats)
    {
        Dictionary<string, string> byId = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> byName = new(StringComparer.Ordinal);
        foreach (XmlTvChannelData channel in channels)
        {
            byId.TryAdd(channel.Id, channel.Id);
            foreach (string normalized in channel.DisplayNames.Select(NormalizeName).Where(n => n.Length > 0).Distinct())
            {
                // Names claimed by two different XMLTV channels are ambiguous and never matched.
                if (byName.TryGetValue(normalized, out string? existing))
                {
                    if (existing != channel.Id)
                    {
                        byName[normalized] = null;
                    }
                }
                else
                {
                    byName[normalized] = channel.Id;
                }
            }
        }

        stats = new MatchStats();
        Dictionary<int, string> mapping = [];
        foreach (XmlTvChannelKey key in keys)
        {
            if (!string.IsNullOrWhiteSpace(key.OverrideEpgId))
            {
                mapping[key.StreamId] = byId.GetValueOrDefault(key.OverrideEpgId.Trim(), key.OverrideEpgId.Trim());
                stats.Override++;
            }
            else if (!string.IsNullOrWhiteSpace(key.ProviderEpgId) && byId.TryGetValue(key.ProviderEpgId.Trim(), out string? id))
            {
                mapping[key.StreamId] = id;
                stats.ById++;
            }
            else if (byName.TryGetValue(NormalizeName(key.Name), out string? named) && named is not null)
            {
                mapping[key.StreamId] = named;
                stats.ByName++;
            }
            else
            {
                stats.Unmatched++;
            }
        }

        return mapping;
    }

    [GeneratedRegex(@"[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"^\s*\|?[a-z]{2,3}\|?\s*[:|]\s*")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"\b(hd|fhd|uhd|sd|4k|hevc|h265|raw|vip|backup)\b")]
    private static partial Regex QualityRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();
    private static ProgramInfo ToProgramInfo(XmlTvProgramData program, string jellyfinChannelId)
    {
        List<string> categories = program.Categories;
        bool isMovie = HasCategory(categories, "movie") || HasCategory(categories, "film");
        bool isNews = HasCategory(categories, "news");
        bool isSports = HasCategory(categories, "sport");
        bool isKids = HasCategory(categories, "children") || HasCategory(categories, "kids");

        return new ProgramInfo
        {
            Id = string.Format(CultureInfo.InvariantCulture, "{0}_{1:O}", jellyfinChannelId, program.StartDateUtc),
            ChannelId = jellyfinChannelId,
            StartDate = program.StartDateUtc,
            EndDate = program.EndDateUtc,
            Name = string.IsNullOrWhiteSpace(program.Title) ? "Unknown" : program.Title,
            Overview = program.Description,
            EpisodeTitle = program.SubTitle,
            Genres = categories,
            ProductionYear = program.ProductionYear,
            SeasonNumber = program.SeasonNumber,
            EpisodeNumber = program.EpisodeNumber,
            IsSeries = !isMovie && (program.SeasonNumber.HasValue || program.EpisodeNumber.HasValue || !string.IsNullOrWhiteSpace(program.SubTitle)),
            IsRepeat = program.IsPreviouslyShown && !program.IsNew,
            IsPremiere = program.IsPremiere,
            IsLive = program.IsLive,
            IsMovie = isMovie,
            IsNews = isNews,
            IsSports = isSports,
            IsKids = isKids,
            ImageUrl = program.ImageUrl,
            HasImage = !string.IsNullOrWhiteSpace(program.ImageUrl),
        };
    }

    private static bool HasCategory(IEnumerable<string> categories, string value)
    {
        return categories.Any(category => category.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static FileInfo GetFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("The custom XMLTV path is empty.");
        }

        var file = new FileInfo(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())));
        if (!file.Exists)
        {
            throw new FileNotFoundException("The configured XMLTV file was not found.", file.FullName);
        }

        return file;
    }

    private static bool IsFresh(string path, TimeSpan maxAge)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length > 0 && DateTime.UtcNow - file.LastWriteTimeUtc < maxAge;
    }

    private static async Task DecompressIfNeededAsync(string source, string destination, CancellationToken cancellationToken)
    {
        byte[] magic = new byte[2];
        FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (input.ConfigureAwait(false))
        {
            int read = await input.ReadAsync(magic, cancellationToken).ConfigureAwait(false);
            input.Position = 0;
            if (read < 2 || magic[0] != 0x1f || magic[1] != 0x8b)
            {
                await input.DisposeAsync().ConfigureAwait(false);
                File.Move(source, destination, overwrite: true);
                return;
            }

            GZipStream gzip = new(input, CompressionMode.Decompress);
            await using (gzip.ConfigureAwait(false))
            {
                FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.Asynchronous);
                await using (output.ConfigureAwait(false))
                {
                    await gzip.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static string KeysSignature(IReadOnlyCollection<XmlTvChannelKey> keys)
    {
        StringBuilder builder = new();
        foreach (XmlTvChannelKey key in keys.OrderBy(key => key.StreamId))
        {
            builder.Append(key.StreamId).Append('\u001f')
                .Append(key.OverrideEpgId).Append('\u001f')
                .Append(key.ProviderEpgId).Append('\u001f')
                .Append(key.Name).Append('\u001e');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static XmlTvCache EnsureCache(string path, IReadOnlyCollection<XmlTvChannelKey> keys, ILogger? logger)
    {
        FileInfo file = GetFile(path);
        string signature = KeysSignature(keys);

        lock (CacheLock)
        {
            if (_cache is not null && _cache.Matches(file) && _cache.Signature == signature)
            {
                return _cache;
            }

            try
            {
                XmlTvCache replacement = Load(file, keys, signature, logger);
                _cache = replacement;
                return replacement;
            }
            catch when (_cache is not null
                        && string.Equals(_cache.Path, file.FullName, StringComparison.OrdinalIgnoreCase)
                        && _cache.Signature == signature)
            {
                // If a guide generator is replacing the XMLTV file while Jellyfin refreshes,
                // keep serving the last successfully parsed copy instead of dropping the guide.
                return _cache;
            }
        }
    }

    private static XmlReader CreateReader(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CloseInput = true,
        };

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 128, FileOptions.SequentialScan);
        return XmlReader.Create(stream, settings);
    }

    private static List<XmlTvChannelData> ReadChannels(string path)
    {
        Dictionary<string, XmlTvChannelData> channels = new(StringComparer.Ordinal);
        using XmlReader reader = CreateReader(path);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.Name.Equals("channel", StringComparison.OrdinalIgnoreCase))
            {
                XmlTvChannelData? channel = ReadChannel(reader);
                if (channel is not null)
                {
                    channels[channel.Id] = channel;
                }
            }
            else if (reader.Name.Equals("programme", StringComparison.OrdinalIgnoreCase))
            {
                // XMLTV lists all channels before the programmes.
                break;
            }
        }

        return channels.Values.ToList();
    }

    private static XmlTvCache Load(FileInfo file, IReadOnlyCollection<XmlTvChannelKey> keys, string signature, ILogger? logger)
    {
        Dictionary<string, XmlTvChannelData> channels = new(StringComparer.Ordinal);
        var programs = new Dictionary<string, List<XmlTvProgramData>>(StringComparer.Ordinal);
        Dictionary<int, string>? mapping = null;
        HashSet<string>? wanted = null;
        int skipped = 0;

        void BuildMapping()
        {
            mapping = MapChannels(keys, channels.Values, out MatchStats stats);
            wanted = new HashSet<string>(mapping.Values, StringComparer.Ordinal);
            logger?.LogInformation(
                "XMLTV guide matched {Matched} of {Total} channels (override {Override}, epg id {ById}, name {ByName}); {Unmatched} unmatched",
                keys.Count - stats.Unmatched,
                keys.Count,
                stats.Override,
                stats.ById,
                stats.ByName,
                stats.Unmatched);
        }

        using (XmlReader reader = CreateReader(file.FullName))
        {
            // Skip() already moves to the next node, which must not be skipped by another Read().
            bool positioned = false;
            while (positioned || reader.Read())
            {
                positioned = false;
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Name.Equals("channel", StringComparison.OrdinalIgnoreCase))
                {
                    XmlTvChannelData? channel = ReadChannel(reader);
                    if (channel is not null)
                    {
                        channels[channel.Id] = channel;
                    }
                }
                else if (reader.Name.Equals("programme", StringComparison.OrdinalIgnoreCase))
                {
                    if (mapping is null)
                    {
                        BuildMapping();
                    }

                    string? channelId = reader.GetAttribute("channel");
                    if (channelId is null || !wanted!.Contains(channelId))
                    {
                        skipped++;
                        reader.Skip();
                        positioned = true;
                        continue;
                    }

                    XmlTvProgramData? program = ReadProgram(reader);
                    if (program is null)
                    {
                        continue;
                    }

                    if (!programs.TryGetValue(program.ChannelId, out List<XmlTvProgramData>? list))
                    {
                        list = [];
                        programs[program.ChannelId] = list;
                    }

                    list.Add(program);
                }
            }
        }

        if (mapping is null)
        {
            BuildMapping();
        }

        foreach (List<XmlTvProgramData> list in programs.Values)
        {
            list.Sort((left, right) => left.StartDateUtc.CompareTo(right.StartDateUtc));
        }

        logger?.LogInformation(
            "Loaded {Kept} XMLTV programmes for {Channels} channels, skipped {Skipped} for unused channels",
            programs.Values.Sum(list => list.Count),
            programs.Count,
            skipped);

        return new XmlTvCache(file.FullName, file.LastWriteTimeUtc, file.Length, signature, mapping!, programs);
    }

    private static XmlTvChannelData? ReadChannel(XmlReader reader)
    {
        string? id = reader.GetAttribute("id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var displayNames = new List<string>();
        using XmlReader subtree = reader.ReadSubtree();
        bool positioned = false;
        while (positioned || subtree.Read())
        {
            positioned = false;
            if (subtree.NodeType == XmlNodeType.Element
                && subtree.Name.Equals("display-name", StringComparison.OrdinalIgnoreCase))
            {
                // ReadElementContentAsString already moves to the next node, which must not be skipped.
                string value = subtree.ReadElementContentAsString().Trim();
                positioned = true;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    displayNames.Add(value);
                }
            }
        }

        string displayName = displayNames
            .Where(value => value.Length > 0 && !char.IsDigit(value[0]))
            .OrderByDescending(value => value.Length)
            .FirstOrDefault()
            ?? displayNames.OrderByDescending(value => value.Length).FirstOrDefault()
            ?? id;

        return new XmlTvChannelData(id, displayName, displayNames);
    }

    private static XmlTvProgramData? ReadProgram(XmlReader reader)
    {
        string? channelId = reader.GetAttribute("channel");
        string? startText = reader.GetAttribute("start");
        string? stopText = reader.GetAttribute("stop");
        if (string.IsNullOrWhiteSpace(channelId)
            || !TryParseXmlTvDate(startText, out DateTime startDateUtc)
            || !TryParseXmlTvDate(stopText, out DateTime endDateUtc))
        {
            return null;
        }

        string title = string.Empty;
        string? subTitle = null;
        string? description = null;
        string? imageUrl = null;
        int? productionYear = null;
        int? seasonNumber = null;
        int? episodeNumber = null;
        bool previouslyShown = false;
        bool isNew = false;
        bool isPremiere = false;
        bool isLive = false;
        var categories = new List<string>();

        using XmlReader subtree = reader.ReadSubtree();
        bool positioned = false;

        // ReadElementContentAsString already moves to the next node, which must not be skipped.
        string ReadText()
        {
            positioned = true;
            return subtree.ReadElementContentAsString();
        }

        while (positioned || subtree.Read())
        {
            positioned = false;
            if (subtree.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (subtree.Name.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                title = ReadText().Trim();
            }
            else if (subtree.Name.Equals("sub-title", StringComparison.OrdinalIgnoreCase))
            {
                subTitle = NullIfEmpty(ReadText());
            }
            else if (subtree.Name.Equals("desc", StringComparison.OrdinalIgnoreCase))
            {
                description = NullIfEmpty(ReadText());
            }
            else if (subtree.Name.Equals("category", StringComparison.OrdinalIgnoreCase))
            {
                string category = ReadText().Trim();
                if (!string.IsNullOrWhiteSpace(category) && !categories.Contains(category, StringComparer.OrdinalIgnoreCase))
                {
                    categories.Add(category);
                }
            }
            else if (subtree.Name.Equals("icon", StringComparison.OrdinalIgnoreCase))
            {
                imageUrl ??= NullIfEmpty(subtree.GetAttribute("src"));
            }
            else if (subtree.Name.Equals("date", StringComparison.OrdinalIgnoreCase))
            {
                string date = ReadText().Trim();
                if (date.Length >= 4 && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out int year))
                {
                    productionYear = year;
                }
            }
            else if (subtree.Name.Equals("episode-num", StringComparison.OrdinalIgnoreCase))
            {
                string? system = subtree.GetAttribute("system");
                string value = ReadText().Trim();
                if (string.Equals(system, "xmltv_ns", StringComparison.OrdinalIgnoreCase))
                {
                    ParseXmlTvNs(value, out seasonNumber, out episodeNumber);
                }
            }
            else if (subtree.Name.Equals("previously-shown", StringComparison.OrdinalIgnoreCase))
            {
                previouslyShown = true;
            }
            else if (subtree.Name.Equals("new", StringComparison.OrdinalIgnoreCase))
            {
                isNew = true;
            }
            else if (subtree.Name.Equals("premiere", StringComparison.OrdinalIgnoreCase))
            {
                isPremiere = true;
            }
            else if (subtree.Name.Equals("live", StringComparison.OrdinalIgnoreCase))
            {
                isLive = true;
            }
        }

        return new XmlTvProgramData(
            channelId,
            startDateUtc,
            endDateUtc,
            title,
            subTitle,
            description,
            categories,
            imageUrl,
            productionYear,
            seasonNumber,
            episodeNumber,
            previouslyShown,
            isNew,
            isPremiere,
            isLive);
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void ParseXmlTvNs(string value, out int? seasonNumber, out int? episodeNumber)
    {
        seasonNumber = null;
        episodeNumber = null;
        string[] parts = value.Split('.');
        if (parts.Length > 0 && TryReadXmlTvNsPart(parts[0], out int season))
        {
            seasonNumber = season + 1;
        }

        if (parts.Length > 1 && TryReadXmlTvNsPart(parts[1], out int episode))
        {
            episodeNumber = episode + 1;
        }
    }

    private static bool TryReadXmlTvNsPart(string value, out int number)
    {
        string primary = value.Split('/')[0].Trim();
        return int.TryParse(primary, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    private static bool TryParseXmlTvDate(string? value, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string timestamp = parts[0];
        string format = timestamp.Length >= 14 ? "yyyyMMddHHmmss" : "yyyyMMddHHmm";
        if (timestamp.Length > 14)
        {
            timestamp = timestamp[..14];
        }

        if (!DateTime.TryParseExact(timestamp, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            return false;
        }

        TimeSpan offset = TimeSpan.Zero;
        if (parts.Length > 1 && TryParseOffset(parts[1], out TimeSpan parsedOffset))
        {
            offset = parsedOffset;
        }

        utc = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).UtcDateTime;
        return true;
    }

    private static bool TryParseOffset(string value, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (value.Length < 5 || (value[0] != '+' && value[0] != '-'))
        {
            return false;
        }

        string digits = value[1..].Replace(":", string.Empty, StringComparison.Ordinal);
        if (digits.Length < 4
            || !int.TryParse(digits[..2], NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(digits.Substring(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
        {
            return false;
        }

        offset = new TimeSpan(hours, minutes, 0);
        if (value[0] == '-')
        {
            offset = -offset;
        }

        return true;
    }

    private sealed class XmlTvCache(
        string path,
        DateTime lastWriteTimeUtc,
        long length,
        string signature,
        Dictionary<int, string> mapping,
        Dictionary<string, List<XmlTvProgramData>> programs)
    {
        public string Path { get; } = path;
        public DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;
        public long Length { get; } = length;
        public string Signature { get; } = signature;
        public Dictionary<int, string> Mapping { get; } = mapping;
        public Dictionary<string, List<XmlTvProgramData>> Programs { get; } = programs;

        public bool Matches(FileInfo file) =>
            string.Equals(Path, file.FullName, StringComparison.OrdinalIgnoreCase)
            && LastWriteTimeUtc == file.LastWriteTimeUtc
            && Length == file.Length;
    }

    private sealed class XmlTvChannelsCache(string path, DateTime lastWriteTimeUtc, long length, List<XmlTvChannelInfo> channels)
    {
        public List<XmlTvChannelInfo> Channels { get; } = channels;

        public bool Matches(FileInfo file) =>
            string.Equals(path, file.FullName, StringComparison.OrdinalIgnoreCase)
            && lastWriteTimeUtc == file.LastWriteTimeUtc
            && length == file.Length;
    }

    internal sealed record XmlTvChannelData(string Id, string Name, List<string> DisplayNames);

    internal sealed class MatchStats
    {
        public int Override { get; set; }

        public int ById { get; set; }

        public int ByName { get; set; }

        public int Unmatched { get; set; }
    }

    private sealed record XmlTvProgramData(
        string ChannelId,
        DateTime StartDateUtc,
        DateTime EndDateUtc,
        string Title,
        string? SubTitle,
        string? Description,
        List<string> Categories,
        string? ImageUrl,
        int? ProductionYear,
        int? SeasonNumber,
        int? EpisodeNumber,
        bool IsPreviouslyShown,
        bool IsNew,
        bool IsPremiere,
        bool IsLive);
}
#pragma warning restore
