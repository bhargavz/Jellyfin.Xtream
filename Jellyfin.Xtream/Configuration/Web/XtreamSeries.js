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
    Xtream.setTabs(4);

    const form = view.querySelector('#XtreamSeriesForm');
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
        visible.checked = config.IsSeriesVisible;
      }
    });
    const table = view.querySelector('#SeriesContent');
    Xtream.populateCategoriesTable(
      table,
      () => getConfig.then((config) => config.Series),
      () => Xtream.fetchJson('Xtream/SeriesCategories'),
      (categoryId) => Xtream.fetchJson(`Xtream/SeriesCategories/${categoryId}`),
    ).then((data) => {
      if (data === null || !isCurrent()) {
        return;
      }
      // Assigned, not added with addEventListener, so a page shown again doesn't save once per earlier show.
      form.onsubmit = (e) => {
        e.preventDefault();
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then((config) => {
          config.IsSeriesVisible = visible.checked;
          config.Series = data;
          return ApiClient.updatePluginConfiguration(pluginId, config);
        }).then((result) => {
          Dashboard.processPluginConfigurationUpdateResult(result);
        });

        return false;
      };
    }).catch((error) => {
      console.error('Failed to load series categories:', error);
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
