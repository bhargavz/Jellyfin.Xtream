export default function (view) {
  const showLoadError = (table) => {
    table.replaceChildren();
    const errorRow = document.createElement('tr');
    const errorCell = document.createElement('td');
    errorCell.colSpan = 3;
    errorCell.style.color = '#ff6b6b';
    errorCell.style.padding = '16px';
    errorCell.innerHTML = 'Failed to load categories. Please check:<br>' +
      '1. Xtream credentials are configured (Credentials tab)<br>' +
      '2. Xtream server is accessible<br>' +
      '3. Browser console for detailed errors';
    errorRow.appendChild(errorCell);
    table.appendChild(errorRow);
  };

  const onViewShow = () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(3);

    const form = view.querySelector('#XtreamVodForm');
    // Nothing may be saved until this render's data has loaded.
    form.onsubmit = (e) => {
      e.preventDefault();
      return false;
    };

    const isCurrent = Xtream.beginRender(view);
    const getConfig = ApiClient.getPluginConfiguration(pluginId);
    const visible = view.querySelector("#Visible");
    getConfig.then((config) => {
      if (isCurrent()) {
        visible.checked = config.IsVodVisible;
      }
    });
    const tmdbOverride = view.querySelector("#TmdbOverride");
    getConfig.then((config) => {
      if (isCurrent()) {
        tmdbOverride.checked = config.IsTmdbVodOverride;
      }
    });
    const table = view.querySelector('#VodContent');
    Xtream.populateCategoriesTable(
      table,
      () => getConfig.then((config) => config.Vod),
      () => Xtream.fetchJson('Xtream/VodCategories'),
      (categoryId) => Xtream.fetchJson(`Xtream/VodCategories/${categoryId}`),
    ).then((data) => {
      if (data === null || !isCurrent()) {
        return;
      }
      // Assigned, not added with addEventListener, so a page shown again doesn't save once per earlier show.
      form.onsubmit = (e) => {
        e.preventDefault();
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then((config) => {
          config.IsVodVisible = visible.checked;
          config.IsTmdbVodOverride = tmdbOverride.checked;
          config.Vod = data;
          return ApiClient.updatePluginConfiguration(pluginId, config);
        }).then((result) => {
          Dashboard.processPluginConfigurationUpdateResult(result);
        });

        return false;
      };
    }).catch((error) => {
      console.error('Failed to load VOD categories:', error);
      Dashboard.hideLoadingMsg();
      if (isCurrent()) {
        showLoadError(table);
      }
    });
  });

  // The controller can be invoked more than once for the same cached view; bind viewshow only once.
  if (view.dataset.xtreamInit !== 'true') {
    view.dataset.xtreamInit = 'true';
    view.addEventListener('viewshow', onViewShow);
  }
}
