using System;
using System.Net.Http;
using System.Reflection;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// What this plugin calls itself when it talks to DeepSkyLog.
    /// </summary>
    /// <remarks>
    /// The server refuses uploads from plugin builds below a published floor, and can only do that
    /// for clients that say which build they are. These headers go on every HttpClient the plugin
    /// owns.
    /// </remarks>
    public static class ClientIdentity {

        /// <summary>
        /// The key the server's published-version manifest uses for this plugin. Must match the
        /// entry in the backend's client-versions.json.
        /// </summary>
        public const string ClientId = "nina-plugin";

        /// <summary>
        /// The value telemetry batches carry, e.g. "DeepSkyLog.NINAPlugin/1.0.3.0".
        /// </summary>
        public const string ClientVersionPrefix = "DeepSkyLog.NINAPlugin/";

        /// <summary>
        /// Reported build, e.g. "1.0.3.0". The plugin sets this once, from its own assembly version,
        /// on load. Defaults to "unknown" so Core-only unit tests (which have no plugin assembly)
        /// still get a non-null value; the server treats a non-numeric version as indeterminate
        /// rather than "too old".
        /// </summary>
        public static string Version { get; set; } = "unknown";

        /// <summary>The version string the telemetry body and headers carry.</summary>
        public static string ClientVersionString => ClientVersionPrefix + Version;

        /// <summary>Reads the plugin's own assembly version; used to stamp ClientIdentity.Version.</summary>
        public static string ReadAssemblyVersion() {
            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        }

        /// <summary>
        /// Stamps a client's default headers. Safe to call once per static HttpClient at
        /// construction; default headers are shared by every request that client sends.
        /// </summary>
        public static HttpClient WithIdentity(this HttpClient client) {
            try {
                client.DefaultRequestHeaders.Add("X-Client-Id", ClientId);
                client.DefaultRequestHeaders.Add("X-Client-Version", Version);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(ClientVersionString);
            } catch {
                // A duplicate or malformed header must never be the reason the plugin fails to
                // load. Being unidentified simply means the server does not enforce a floor on us.
            }
            return client;
        }
    }
}
