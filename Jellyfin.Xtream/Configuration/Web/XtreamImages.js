export default function (view) {
  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(5);

    const getConfig = ApiClient.getPluginConfiguration(pluginId);
    const channelInputs = view.querySelectorAll('.channel-image');
    getConfig.then((config) => channelInputs.forEach((input) => {
      input.value = config.ChannelImages[input.dataset.key] ?? '';
    }));

    const populate = (table, type, selected, fetchCategories) => Promise.all([getConfig, fetchCategories()])
      .then(([config, categories]) => {
        table.innerHTML = '';
        const keys = Object.keys(selected(config));
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
            input.value = config.CategoryImages[input.dataset.key] ?? '';
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

    view.querySelector('#XtreamImagesForm').addEventListener('submit', (e) => {
      Dashboard.showLoadingMsg();

      ApiClient.getPluginConfiguration(pluginId).then((config) => {
        config.ChannelImages = collect('.channel-image');
        config.CategoryImages = collect('.category-image');
        ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
          Dashboard.processPluginConfigurationUpdateResult(result);
        });
      });

      e.preventDefault();
      return false;
    });
  }));
}
