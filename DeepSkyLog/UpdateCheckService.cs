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
                    if (CompareVersions(current, release.Latest) >= 0) {
                        return null;
                    }

                    // Below the floor the situation is not "an update exists" but "uploads are
                    // already failing", and the wording has to say so — otherwise the user reads a
                    // polite suggestion while their night's frames are being refused.
                    if (release.Minimum != null && CompareVersions(current, release.Minimum) < 0) {
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

        /// <summary>
        /// Orders dotted version numbers: negative if <paramref name="a"/> is older than
        /// <paramref name="b"/>, positive if newer, 0 if equal or indeterminate.
        /// </summary>
        /// <remarks>
        /// Missing trailing components count as zero, so the plugin's four-part "1.0.3.0" and a
        /// three-part "1.0.3" in the manifest compare equal. A component that is not a plain number
        /// makes the whole comparison indeterminate rather than "older" — a local build must not be
        /// nagged on every launch.
        /// </remarks>
        internal static int CompareVersions(string a, string b) {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) {
                return 0;
            }

            string[] left = a.Trim().Split('.');
            string[] right = b.Trim().Split('.');
            int parts = Math.Max(left.Length, right.Length);

            for (int i = 0; i < parts; i++) {
                if (!TryComponent(left, i, out int l) || !TryComponent(right, i, out int r)) {
                    return 0;
                }
                if (l != r) {
                    return l.CompareTo(r);
                }
            }

            return 0;
        }

        private static bool TryComponent(string[] parts, int index, out int value) {
            if (index >= parts.Length) {
                value = 0;
                return true;
            }
            return int.TryParse(parts[index].Trim(), out value) && value >= 0;
        }
    }
}
