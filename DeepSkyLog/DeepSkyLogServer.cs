using NINA.Core.Utility;
using System;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// The DeepSkyLog server this plugin talks to: production, unless the
    /// <c>DEEPSKYLOG_BASE_URL</c> environment variable names another — the same variable the Star
    /// Quality app honours. That is how a test machine is pointed at the beta instance.
    ///
    /// <para>Deliberately not an option in NINA's UI: a user must never end up uploading a night's
    /// frames to beta by accident. For the same reason a malformed value is not quietly replaced by
    /// production — frames meant for a test server must not land in someone's real account — it is
    /// used as given and fails loudly.</para>
    /// </summary>
    internal static class DeepSkyLogServer {

        public const string ProductionUrl = "https://app.deepskylog.space";
        public const string EnvironmentVariable = "DEEPSKYLOG_BASE_URL";

        public static string BaseUrl { get; } = Resolve();

        private static string Resolve() {
            string configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configured)) {
                return ProductionUrl;
            }

            string url = configured.Trim().TrimEnd('/');
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)) {
                Logger.Info($"DeepSkyLog: using server {url} (from {EnvironmentVariable})");
            } else {
                Logger.Error($"DeepSkyLog: {EnvironmentVariable} is set to '{configured}', which is not an "
                             + "http(s) URL. Requests will fail until it is fixed or removed.");
            }
            return url;
        }
    }
}
