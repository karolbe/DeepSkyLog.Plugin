using Newtonsoft.Json;
using NINA.Core.Utility;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// Asks DeepSkyLog whether a newer build of this plugin has been published.
    /// </summary>
    /// <remarks>
    /// Notification only — NINA owns plugin installation, so this must never try to update itself.
    /// The user is pointed at NINA's Plugin Manager, which is the only thing that can actually do
    /// the install.
    /// <para>
    /// Every failure is silent. An observatory PC with no internet is normal, and a plugin that
    /// complained about it on every startup would be worse than one that said nothing.
    /// </para>
    /// </remarks>
    public static class UpdateCheckService {

        private const string BaseUrl = "https://app.deepskylog.space";

        private static readonly HttpClient client =
            new HttpClient { Timeout = TimeSpan.FromSeconds(10) }.WithIdentity();

        public class ClientRelease {

            [JsonProperty("latest")]
            public string Latest { get; set; }

            /// <summary>Below this the server refuses uploads outright with 426.</summary>
            [JsonProperty("minimum")]
            public string Minimum { get; set; }

            [JsonProperty("releaseNotesUrl")]
            public string ReleaseNotesUrl { get; set; }
        }

        /// <summary>
        /// Returns the notice to show the user, or null when there is nothing to say — this build
        /// is current, or the question could not be answered.
        /// </summary>
        public static async Task<string> CheckAsync() {
            try {
                string url = $"{BaseUrl}/api/public/client-versions/{ClientIdentity.ClientId}";
                using (var response = await client.GetAsync(url)) {
                    if (!response.IsSuccessStatusCode) {
                        return null;
                    }

                    string json = await response.Content.ReadAsStringAsync();
                    var release = JsonConvert.DeserializeObject<ClientRelease>(json);
                    if (release?.Latest == null) {
                        return null;
                    }

                    string current = ClientIdentity.Version;
                    if (VersionComparer.Compare(current, release.Latest) >= 0) {
                        return null;
                    }

                    // Below the floor the situation is not "an update exists" but "uploads are
                    // already failing", and the wording has to say so — otherwise the user reads a
                    // polite suggestion while their night's frames are being refused.
                    if (release.Minimum != null && VersionComparer.Compare(current, release.Minimum) < 0) {
                        return $"DeepSkyLog {release.Latest} is required — the server no longer "
                            + $"accepts uploads from version {current}. Update in NINA's Plugin Manager.";
                    }

                    return $"DeepSkyLog {release.Latest} is available (you have {current}). "
                        + "Update in NINA's Plugin Manager.";
                }
            } catch (Exception ex) {
                Logger.Debug($"DeepSkyLog: update check failed: {ex.Message}");
                return null;
            }
        }
    }
}
