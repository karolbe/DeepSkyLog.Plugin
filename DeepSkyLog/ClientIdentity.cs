using System;
using System.Net.Http;
using System.Reflection;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// What this plugin calls itself when it talks to DeepSkyLog.
    /// </summary>
    /// <remarks>
    /// The server refuses uploads from plugin builds below a published floor, and can only do that
    /// for clients that say which build they are. Until now the version travelled only inside
    /// telemetry JSON bodies, so a frame upload was anonymous — the exact requests the floor needs
    /// to apply to. These headers go on every HttpClient the plugin owns.
    /// </remarks>
    public static class ClientIdentity {

        /// <summary>
        /// The key the server's published-version manifest uses for this plugin. Must match the
        /// entry in the backend's client-versions.json.
        /// </summary>
        public const string ClientId = "nina-plugin";

        /// <summary>Four-part assembly version, e.g. "1.0.3.0".</summary>
        public static string Version { get; } =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        /// <summary>The value telemetry batches carry, e.g. "DeepSkyLog.NINAPlugin/1.0.3.0".</summary>
        public static string ClientVersionString { get; } = "DeepSkyLog.NINAPlugin/" + Version;

        /// <summary>
        /// Stamps a client's default headers. Safe to call once per static HttpClient at
        /// construction; default headers are shared by every request that client sends.
        /// </summary>
        public static HttpClient WithIdentity(this HttpClient client) {
            try {
                client.DefaultRequestHeaders.Add("X-Client-Id", ClientId);
                client.DefaultRequestHeaders.Add("X-Client-Version", Version);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(ClientVersionString);
            } catch (Exception) {
                // A duplicate or malformed header must never be the reason the plugin fails to
                // load. Being unidentified simply means the server does not enforce a floor on us.
            }
            return client;
        }
    }
}
