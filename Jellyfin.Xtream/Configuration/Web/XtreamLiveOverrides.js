export default function (view) {
  const EPG_REFRESH_MIN = 1;
  const EPG_REFRESH_DEFAULT = 24;

  const createChannelRow = (channel, overrides) => {
    const tr = document.createElement('tr');
    tr.dataset['channelId'] = channel.Id;

    let td = document.createElement('td');
    const number = document.createElement('input');
    number.type = 'number';
    number.setAttribute('is', 'emby-input');
    number.placeholder = channel.Number;
    number.value = overrides.Number ?? '';
    number.onchange = () => number.value ?
      overrides.Number = parseInt(number.value) :
      delete overrides.Number;
    td.appendChild(number);
    tr.appendChild(td);

    td = document.createElement('td');
    const name = document.createElement('input');
    name.type = 'text';
    name.setAttribute('is', 'emby-input');
    name.placeholder = channel.Name;
    name.value = overrides.Name ?? '';
    name.onchange = () => name.value ?
      overrides.Name = name.value :
      delete overrides.Name;
    td.appendChild(name);
    tr.appendChild(td);

    td = document.createElement('td');
    const image = document.createElement('input');
    image.type = 'text';
    image.setAttribute('is', 'emby-input');
    image.placeholder = channel.LogoUrl;
    image.value = overrides.LogoUrl ?? '';
    image.onchange = () => image.value ?
      overrides.LogoUrl = image.value :
      delete overrides.LogoUrl;
    td.appendChild(image);
    tr.appendChild(td);

    td = document.createElement('td');
    const epg = document.createElement('input');
    epg.type = 'text';
    epg.setAttribute('is', 'emby-input');
    epg.setAttribute('list', 'XmlTvChannelOptions');
    epg.placeholder = 'XMLTV channel id';
    epg.value = overrides.EpgId ?? '';
    epg.onchange = () => epg.value.trim() ?
      overrides.EpgId = epg.value.trim() :
      delete overrides.EpgId;
    td.appendChild(epg);
    tr.appendChild(td);

    // Per-channel guide time shift (upstream #217), stored as EpgShiftMinutes.
    td = document.createElement('td');
    const shiftTotal = overrides.EpgShiftMinutes ?? 0;
    const shiftHours = document.createElement('input');
    shiftHours.type = 'number';
    shiftHours.setAttribute('is', 'emby-input');
    shiftHours.min = '-24';
    shiftHours.max = '24';
    shiftHours.step = '1';
    shiftHours.placeholder = 'h';
    shiftHours.title = 'Guide shift, hours (negative = earlier)';
    shiftHours.value = shiftTotal ? Math.trunc(shiftTotal / 60) : '';
    const shiftMinutes = document.createElement('input');
    shiftMinutes.type = 'number';
    shiftMinutes.setAttribute('is', 'emby-input');
    shiftMinutes.min = '-59';
    shiftMinutes.max = '59';
    shiftMinutes.step = '1';
    shiftMinutes.placeholder = 'min';
    shiftMinutes.title = 'Guide shift, minutes';
    shiftMinutes.value = shiftTotal % 60 ? Math.abs(shiftTotal % 60) * (Math.abs(shiftTotal) < 60 ? Math.sign(shiftTotal) : 1) : '';
    const updateShift = () => {
      const hours = parseInt(shiftHours.value, 10) || 0;
      const minutes = parseInt(shiftMinutes.value, 10) || 0;
      // The sign of the hours applies to the minutes too: -1 h 30 min means -90 minutes.
      const total = hours !== 0 ? hours * 60 + Math.sign(hours) * Math.abs(minutes) : minutes;
      if (total) {
        overrides.EpgShiftMinutes = Math.max(-1440, Math.min(1440, total));
      } else {
        delete overrides.EpgShiftMinutes;
      }
    };
    shiftHours.onchange = updateShift;
    shiftMinutes.onchange = updateShift;
    td.appendChild(shiftHours);
    td.appendChild(shiftMinutes);
    tr.appendChild(td);

    return tr;
  };

  const populateEpgOptions = (channels) => {
    const list = view.querySelector('#XmlTvChannelOptions');
    list.innerHTML = '';
    for (const channel of channels) {
      const option = document.createElement('option');
      option.value = channel.Id;
      option.label = channel.Name;
      list.appendChild(option);
    }
  };

  const onViewShow = () => import(
    ApiClient.getUrl('web/ConfigurationPage', { name: 'Xtream.js' })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(2);
    const form = view.querySelector('#XtreamLiveOverridesForm');
    // Nothing may be saved until this render's data has loaded.
    form.onsubmit = (e) => {
      e.preventDefault();
      return false;
    };
    const isCurrent = Xtream.beginRender(view);
    const table = view.querySelector('#LiveChannels');
    table.replaceChildren();
    const status = view.querySelector('#CustomEpgStatus');
    status.textContent = '';
    const refreshHours = view.querySelector('#CustomEpgRefreshHours');

    Dashboard.showLoadingMsg();
    Promise.all([
      ApiClient.getPluginConfiguration(pluginId),
      Xtream.fetchJson('Xtream/LiveTv'),
    ]).then(([config, channels]) => {
      // A newer show of this page took over; drop this result instead of appending a second copy (upstream #256).
      if (!isCurrent()) {
        return null;
      }
      const data = config.LiveTvOverrides ?? {};
      view.querySelector('#EpgMode').value = config.EpgMode ?? 'Xtream';
      view.querySelector('#CustomEpgPath').value = config.CustomEpgPath ?? '';
      refreshHours.value = config.CustomEpgRefreshHours > 0 ? config.CustomEpgRefreshHours : EPG_REFRESH_DEFAULT;

      table.replaceChildren();

      for (const channel of channels) {
        data[channel.Id] ??= {};
        table.appendChild(createChannelRow(channel, data[channel.Id]));
      }

      const epgPromise = config.CustomEpgPath ?
        Xtream.fetchJson('Xtream/CustomEpg/Channels') :
        Promise.resolve([]);

      return epgPromise.then((epgChannels) => {
        if (!isCurrent()) {
          return null;
        }
        populateEpgOptions(epgChannels);
        status.textContent = config.CustomEpgPath ?
          `Loaded ${epgChannels.length} XMLTV channels.` :
          'Set an XMLTV path and save to load channel-id suggestions.';
        return { config, data };
      }).catch((error) => {
        console.error('Unable to load XMLTV channels', error);
        if (!isCurrent()) {
          return null;
        }
        status.textContent = 'The XMLTV channel list could not be loaded. Check the saved path and Jellyfin server permissions.';
        return { config, data };
      });
    }).then((loaded) => {
      if (loaded === null) {
        return;
      }
      const { data } = loaded;
      Dashboard.hideLoadingMsg();
      form.onsubmit = (e) => {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then((config) => {
          config.EpgMode = view.querySelector('#EpgMode').value;
          config.CustomEpgPath = view.querySelector('#CustomEpgPath').value.trim();
          config.CustomEpgRefreshHours = Xtream.readInt(refreshHours, EPG_REFRESH_MIN, EPG_REFRESH_DEFAULT);
          refreshHours.value = config.CustomEpgRefreshHours;
          config.LiveTvOverrides = Xtream.filter(
            data,
            overrides => Object.keys(overrides).length > 0
          );
          return ApiClient.updatePluginConfiguration(pluginId, config);
        }).then((result) => {
          Dashboard.processPluginConfigurationUpdateResult(result);
        });
        return false;
      };
    }).catch((error) => {
      console.error(error);
      Dashboard.hideLoadingMsg();
      if (isCurrent()) {
        status.textContent = 'Unable to load the TV Overrides page.';
      }
    });
  });

  // The controller can be invoked more than once for the same cached view; bind viewshow only once.
  if (view.dataset.xtreamInit !== 'true') {
    view.dataset.xtreamInit = 'true';
    view.addEventListener('viewshow', onViewShow);
  }
}
