#pragma warning disable
// Custom XMLTV configuration API for Jellyfin.Xtream.
using System;
using System.Collections.Generic;
using System.IO;
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
        if (string.IsNullOrWhiteSpace(source))
        {
            return Ok(Array.Empty<XmlTvChannelInfo>());
        }

        try
        {
            string path = await XmlTvGuideService.ResolveSourceAsync(
                source,
                httpClientFactory,
                Path.Join(plugin.DataFolderPath, "epg-cache"),
                plugin.Configuration.CustomEpgRefreshHours,
                logger,
                cancellationToken).ConfigureAwait(false);
            return Ok(XmlTvGuideService.GetChannels(path));
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
