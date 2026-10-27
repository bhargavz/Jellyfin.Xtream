export default function (view) {
  const onViewShow = () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(5);

    const isCurrent = Xtream.beginRender(view);
    const getConfig = ApiClient.getPluginConfiguration(pluginId);
    const channelInputs = view.querySelectorAll('.channel-image');
    getConfig.then((config) => {
      if (isCurrent()) {
        channelInputs.forEach((input) => {
          input.value = config.ChannelImages?.[input.dataset.key] ?? '';
        });
      }
    });

    const populate = (table, type, selected, fetchCategories) => Promise.all([getConfig, fetchCategories()])
      .then(([config, categories]) => {
        // Only the newest render may fill the table (upstream #256).
        if (!isCurrent()) {
          return;
        }
        table.replaceChildren();
        const keys = Object.keys(selected(config) ?? {});
        categories
          .filter((category) => keys.includes(String(category.Id)))
          .forEach((category) => {
            const tr = document.createElement('tr');
            let td = document.createElement('td');
            td.innerText = category.Name;
            tr.appendChild(td);

            td = document.createElement('td');
            const input = document.createElement('input');
            input.setAttribute('is', 'emby-input');
            input.type = 'text';
            input.dataset.key = `${type}:${category.Id}`;
            input.classList.add('category-image');
            input.value = config.CategoryImages?.[input.dataset.key] ?? '';
            td.appendChild(input);
            tr.appendChild(td);
            table.appendChild(tr);
          });
      });

    Promise.all([
      populate(view.querySelector('#VodImages'), 'Vod', (config) => config.Vod, () => Xtream.fetchJson('Xtream/VodCategories')),
      populate(view.querySelector('#SeriesImages'), 'Series', (config) => config.Series, () => Xtream.fetchJson('Xtream/SeriesCategories')),
    ]).catch((error) => console.error('Failed to load categories:', error));

    const collect = (selector) => Array.from(view.querySelectorAll(selector))
      .filter((input) => input.value.trim().length > 0)
      .reduce((res, input) => (res[input.dataset.key] = input.value.trim(), res), {});

    // Assigned, not added with addEventListener, so a page shown again doesn't save once per earlier show.
    view.querySelector('#XtreamImagesForm').onsubmit = (e) => {
      e.preventDefault();
      Dashboard.showLoadingMsg();

      ApiClient.getPluginConfiguration(pluginId).then((config) => {
        config.ChannelImages = collect('.channel-image');
        config.CategoryImages = collect('.category-image');
        return ApiClient.updatePluginConfiguration(pluginId, config);
      }).then((result) => {
        Dashboard.processPluginConfigurationUpdateResult(result);
      });

      return false;
    };
  });

  // The controller can be invoked more than once for the same cached view; bind viewshow only once.
  if (view.dataset.xtreamInit !== 'true') {
    view.dataset.xtreamInit = 'true';
    view.addEventListener('viewshow', onViewShow);
  }
}
