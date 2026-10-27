#pragma warning disable
// Custom XMLTV configuration API for Jellyfin.Xtream.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Api;

/// <summary>Provides the XMLTV channel list used by the TV Overrides mapping UI.</summary>
[ApiController]
[Route("Xtream/CustomEpg")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class CustomEpgController(IHttpClientFactory httpClientFactory, ILogger<CustomEpgController> logger) : ControllerBase
{
    /// <summary>Gets channels from the XMLTV file or URL saved in plugin configuration.</summary>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Channels")]
    public async Task<ActionResult<IReadOnlyList<XmlTvChannelInfo>>> GetChannels(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        string source = plugin.Configuration.CustomEpgPath;
        IReadOnlyList<string> fallbacks = XmlTvGuideService.ParseSourceList(plugin.Configuration.FallbackEpgUrls);
        if (string.IsNullOrWhiteSpace(source) && fallbacks.Count == 0)
        {
            return Ok(Array.Empty<XmlTvChannelInfo>());
        }

        string cacheFolder = Path.Join(plugin.DataFolderPath, "epg-cache");
        try
        {
            List<XmlTvChannelInfo> channels = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(source))
            {
                string path = await XmlTvGuideService.ResolveSourceAsync(
                    source,
                    httpClientFactory,
                    cacheFolder,
                    plugin.Configuration.CustomEpgRefreshHours,
                    logger,
                    cancellationToken).ConfigureAwait(false);
                channels.AddRange(XmlTvGuideService.GetChannels(path).Where(channel => seen.Add(channel.Id)));
            }

            foreach (string fallback in fallbacks)
            {
                try
                {
                    string path = await XmlTvGuideService.ResolveSourceAsync(
                        fallback,
                        httpClientFactory,
                        cacheFolder,
                        plugin.Configuration.CustomEpgRefreshHours,
                        logger,
                        cancellationToken).ConfigureAwait(false);
                    channels.AddRange(XmlTvGuideService.GetChannels(path).Where(channel => seen.Add(channel.Id)));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Fallback XMLTV source {Source} is unavailable for the channel list", XmlTvGuideService.GetSourceLabel(fallback));
                }
            }

            return Ok(channels
                .OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(channel => channel.Id, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }
        catch (FileNotFoundException exception)
        {
            return NotFound(new { Message = exception.Message });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { Message = exception.Message });
        }
    }
}
#pragma warning restore
