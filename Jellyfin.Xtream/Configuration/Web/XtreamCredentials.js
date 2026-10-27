export default function (view) {
  const API_TIMEOUT_MIN = 10;
  const API_TIMEOUT_DEFAULT = 300;
  const MAX_STREAMS_MIN = 0;
  const MAX_STREAMS_DEFAULT = 0;

  const onViewShow = () => import(
    window.ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(0);
    const isCurrent = Xtream.beginRender(view);

    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration(pluginId).then(function (config) {
      if (!isCurrent()) {
        return;
      }
      view.querySelector('#BaseUrl').value = config.BaseUrl;
      view.querySelector('#Username').value = config.Username;
      view.querySelector('#Password').value = config.Password;
      view.querySelector('#UserAgent').value = config.UserAgent;
      // The server ignores a timeout of 0 or less, so show the default instead of an out-of-range value.
      view.querySelector('#ApiTimeoutSeconds').value = config.ApiTimeoutSeconds > 0 ?
        config.ApiTimeoutSeconds :
        API_TIMEOUT_DEFAULT;
      // Older server builds don't have this property yet; treat it as 0 (use the provider's limit).
      view.querySelector('#MaxConcurrentStreams').value = config.MaxConcurrentStreams ?? MAX_STREAMS_DEFAULT;
      Dashboard.hideLoadingMsg();
    });

    const reloadStatus = () => {
      const isStatusCurrent = Xtream.beginRender(view.querySelector('#ProviderTest'));
      const status = view.querySelector("#ProviderStatus");
      const expiry = view.querySelector("#ProviderExpiry");
      const cons = view.querySelector("#ProviderConnections");
      const maxCons = view.querySelector("#ProviderMaxConnections");
      const time = view.querySelector("#ProviderTime");
      const timezone = view.querySelector("#ProviderTimezone");
      const mpegTs = view.querySelector("#ProviderMpegTs");

      Xtream.fetchJson('Xtream/TestProvider').then(response => {
        if (!isStatusCurrent()) {
          return;
        }
        status.innerText = response.Status;
        expiry.innerText = response.ExpiryDate;
        cons.innerText = response.ActiveConnections;
        maxCons.innerText = response.MaxConnections;
        time.innerText = response.ServerTime;
        timezone.innerText = response.ServerTimezone;
        mpegTs.innerText = response.SupportsMpegTs;
      }).catch((_) => {
        if (!isStatusCurrent()) {
          return;
        }
        status.innerText = "Failed. Check server logs.";
        expiry.innerText = "";
        cons.innerText = "";
        maxCons.innerText = "";
        time.innerText = "";
        timezone.innerText = "";
        mpegTs.innerText = "";
      });
    };
    reloadStatus();

    // Handlers are assigned, not added with addEventListener, so showing the page again replaces them
    // instead of stacking another copy (upstream #256).
    view.querySelector('#UserAgentFromBrowser').onclick = (e) => {
      e.preventDefault();
      view.querySelector('#UserAgent').value = navigator.userAgent;
    };

    view.querySelector('#XtreamCredentialsForm').onsubmit = (e) => {
      e.preventDefault();
      Dashboard.showLoadingMsg();

      const apiTimeout = view.querySelector('#ApiTimeoutSeconds');
      const maxStreams = view.querySelector('#MaxConcurrentStreams');
      ApiClient.getPluginConfiguration(pluginId).then((config) => {
        config.BaseUrl = view.querySelector('#BaseUrl').value;
        config.Username = view.querySelector('#Username').value;
        config.Password = view.querySelector('#Password').value;
        config.UserAgent = view.querySelector('#UserAgent').value;
        config.ApiTimeoutSeconds = Xtream.readInt(apiTimeout, API_TIMEOUT_MIN, API_TIMEOUT_DEFAULT);
        config.MaxConcurrentStreams = Xtream.readInt(maxStreams, MAX_STREAMS_MIN, MAX_STREAMS_DEFAULT);
        apiTimeout.value = config.ApiTimeoutSeconds;
        maxStreams.value = config.MaxConcurrentStreams;
        return ApiClient.updatePluginConfiguration(pluginId, config);
      }).then((result) => {
        reloadStatus();
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