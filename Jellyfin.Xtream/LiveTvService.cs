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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream;

/// <summary>
/// Class LiveTvService.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="LiveTvService"/> class.
/// </remarks>
/// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
/// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
/// <param name="memoryCache">Instance of the <see cref="IMemoryCache"/> interface.</param>
/// <param name="xtreamClient">Instance of the <see cref="IXtreamClient"/> interface.</param>
public class LiveTvService(IServerApplicationHost appHost, IHttpClientFactory httpClientFactory, ILogger<LiveTvService> logger, IMemoryCache memoryCache, IXtreamClient xtreamClient) : ILiveTvService, ISupportsDirectStreamProvider
{
    private readonly ConcurrentDictionary<int, string> _channelNames = new();

    /// <inheritdoc />
    public string Name => "Xtream Live";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<ChannelInfo> items = [];
        foreach (StreamInfo channel in await plugin.StreamService.GetLiveStreamsWithOverrides(cancellationToken).ConfigureAwait(false))
        {
            ParsedName parsed = StreamService.ParseName(channel.Name);
            _channelNames[channel.StreamId] = parsed.Title;
            items.Add(new ChannelInfo()
            {
                Id = StreamService.ToGuid(StreamService.LiveTvPrefix, channel.StreamId, 0, 0).ToString(),
                Number = channel.Num.ToString(CultureInfo.InvariantCulture),
                ImageUrl = channel.StreamIcon,
                Name = parsed.Title,
                Tags = parsed.Tags,
            });
        }

        // GetLiveStreams already dedupes by stream id; this guards the ids Jellyfin saves (upstream #242).
        return GuideDeduplication.DistinctChannels(items, logger);
    }

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<TimerInfo>>(new List<TimerInfo>());
    }

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<SeriesTimerInfo>>(new List<SeriesTimerInfo>());
    }

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        MediaSourceInfo source = await GetChannelStream(channelId, string.Empty, cancellationToken).ConfigureAwait(false);
        return [source];
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
    {
        logger.LogInformation("Closing livestream {ChannelId}", id);

        // Restream slots are keyed by the restream's unique id; release any that is still held for it.
        if (Plugin.Instance.StreamLimiter.Release(id) > 0)
        {
            logger.LogInformation("Released the Xtream stream slot of livestream {ChannelId}", id);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
    {
        return Task.FromResult(new SeriesTimerInfo
        {
            PostPaddingSeconds = 120,
            PrePaddingSeconds = 120,
            RecordAnyChannel = false,
            RecordAnyTime = true,
            RecordNewOnly = false
        });
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        Guid guid = Guid.Parse(channelId);
        StreamService.FromGuid(guid, out int prefix, out int streamId, out int _, out int _);
        if (prefix != StreamService.LiveTvPrefix)
        {
            throw new ArgumentException("Unsupported channel");
        }

        Plugin plugin = Plugin.Instance;
        string epgMode = plugin.Configuration.EpgMode ?? "Xtream";
        bool customOnly = string.Equals(epgMode, "CustomXmlTv", StringComparison.OrdinalIgnoreCase);
        bool customWithFallback = string.Equals(epgMode, "CustomXmlTvWithXtreamFallback", StringComparison.OrdinalIgnoreCase);
        TimeSpan shift = EpgShift.GetEpgShift(plugin.Configuration.LiveTvOverrides, streamId);

        if (customOnly || customWithFallback)
        {
            if (!string.IsNullOrWhiteSpace(plugin.Configuration.CustomEpgPath))
            {
                try
                {
                    string path = await XmlTvGuideService.ResolveSourceAsync(
                        plugin.Configuration.CustomEpgPath,
                        httpClientFactory,
                        Path.Join(plugin.DataFolderPath, "epg-cache"),
                        plugin.Configuration.CustomEpgRefreshHours,
                        logger,
                        cancellationToken).ConfigureAwait(false);
                    IReadOnlyCollection<XmlTvChannelKey> keys = await GetGuideKeys(cancellationToken).ConfigureAwait(false);
                    List<ProgramInfo>? customPrograms = XmlTvGuideService.GetPrograms(
                        path,
                        keys,
                        logger,
                        streamId,
                        channelId,
                        EpgShift.Unshift(startDateUtc, shift),
                        EpgShift.Unshift(endDateUtc, shift))?.ToList();

                    // Channels covered by the XMLTV guide skip the per-channel Xtream EPG call (upstream #188).
                    if (customOnly || customPrograms is { Count: > 0 })
                    {
                        return GuideDeduplication.DistinctPrograms(EpgShift.Apply(customPrograms ?? [], shift), streamId, logger);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Unable to load custom XMLTV guide data for stream {StreamId}", streamId);
                    if (customOnly)
                    {
                        return [];
                    }
                }
            }
            else if (customOnly)
            {
                return [];
            }
        }

        string key = string.Format(CultureInfo.InvariantCulture, "xtream-epg-{0}-{1}", channelId, (int)shift.TotalMinutes);
        ICollection<ProgramInfo>? items = null;
        if (memoryCache.TryGetValue(key, out ICollection<ProgramInfo>? o))
        {
            items = o;
        }
        else
        {
            List<ProgramInfo> listings = [];
            EpgListings epgs = await xtreamClient.GetEpgInfoAsync(plugin.Creds, streamId, cancellationToken).ConfigureAwait(false);
            foreach (EpgInfo epg in epgs.Listings)
            {
                listings.Add(new()
                {
                    Id = StreamService.ToGuid(StreamService.EpgPrefix, streamId, epg.Id, 0).ToString(),
                    ChannelId = channelId,
                    StartDate = epg.Start,
                    EndDate = epg.End,
                    Name = epg.Title,
                    Overview = epg.Description,
                });
            }

            items = GuideDeduplication.DistinctPrograms(EpgShift.Apply(listings, shift), streamId, logger).ToList();
            memoryCache.Set(key, items, DateTimeOffset.Now.AddMinutes(10));
        }

        return from epg in items
               where epg.EndDate >= startDateUtc && epg.StartDate < endDateUtc
               select epg;
    }

    private async Task<IReadOnlyCollection<XmlTvChannelKey>> GetGuideKeys(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        string key = $"xtream-guide-keys-{plugin.DataVersion}";
        if (memoryCache.TryGetValue(key, out IReadOnlyCollection<XmlTvChannelKey>? keys) && keys is not null)
        {
            return keys;
        }

        keys = (await plugin.StreamService.GetLiveStreamsWithOverrides(cancellationToken).ConfigureAwait(false))
            .Select(stream =>
            {
                _channelNames[stream.StreamId] = StreamService.ParseName(stream.Name).Title;
                return stream;
            })
            .Select(stream => new XmlTvChannelKey(
                stream.StreamId,
                plugin.Configuration.LiveTvOverrides.TryGetValue(stream.StreamId, out Configuration.ChannelOverrides? overrides) ? overrides.EpgId : null,
                stream.EpgChannelId,
                StreamService.ParseName(stream.Name).Title))
            .ToList();
        memoryCache.Set(key, keys, DateTimeOffset.Now.AddHours(1));
        return keys;
    }

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(string channelId, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        Guid guid = Guid.Parse(channelId);
        StreamService.FromGuid(guid, out int prefix, out int channel, out int _, out int _);
        if (prefix != StreamService.LiveTvPrefix)
        {
            throw new ArgumentException("Unsupported channel");
        }

        Plugin plugin = Plugin.Instance;
        MediaSourceInfo mediaSourceInfo = plugin.StreamService.GetMediaSourceInfo(StreamType.Live, channel, restream: true);
        ILiveStream? stream = currentLiveStreams.Find(stream => stream.TunerHostId == Restream.TunerHost && stream.MediaSource.Id == mediaSourceInfo.Id);
        if (stream == null)
        {
            // Only a new upstream connection counts towards the limit; sharing an existing restream does not.
            Restream restream = new Restream(appHost, httpClientFactory, logger, mediaSourceInfo, (uniqueId, ct) => AcquireConnectionAsync(uniqueId, channel, ct));
            try
            {
                await restream.Open(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                restream.Dispose();
                throw;
            }

            stream = restream;
        }

        stream.ConsumerCount++;
        return stream;
    }

    private async Task<IDisposable> AcquireConnectionAsync(string uniqueId, int streamId, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        int configured = plugin.Configuration.MaxConcurrentStreams;
        int? providerMax = null;
        if (configured <= 0)
        {
            try
            {
                // Don't hold up playback on a slow player_api call; the lookup keeps running and is cached.
                providerMax = await plugin.ProviderConnectionLimit.GetAsync(plugin.DataVersion, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Provider max_connections lookup is slow; opening the stream without a limit");
            }
        }

        int limit = StreamLimiter.ResolveLimit(configured, providerMax);
        string name = _channelNames.TryGetValue(streamId, out string? title) ? title : $"channel {streamId}";

        if (plugin.StreamLimiter.TryAcquire(uniqueId, name, limit, out IDisposable? lease, out IReadOnlyList<string> inUse))
        {
            logger.LogDebug("Xtream stream slot taken for {Channel} ({Count} of {Limit} in use)", name, inUse.Count, limit);
            return lease!;
        }

        logger.LogInformation(
            "Xtream stream limit blocked {Channel}: {Count} of {Limit} in use ({InUse})",
            name,
            inUse.Count,
            limit,
            string.Join(", ", inUse));
        throw new StreamLimitReachedException(limit, inUse);
    }
}
