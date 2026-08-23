using DeepSkyLog.NINAPlugin.Properties;
using Namotion.Reflection;
using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static NINA.Equipment.Model.CaptureSequence;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// How the server answered an upload, and therefore what to do with the payload.
    /// </summary>
    public enum UploadResult {
        /// <summary>Stored (or already known). Drop the local copy.</summary>
        Success,

        /// <summary>Network error, timeout, throttling or a server fault. Worth retrying as-is.</summary>
        Transient,

        /// <summary>
        /// The server rejected the payload or the account — a stale equipment/location, an
        /// expired subscription, a malformed body. Re-sending the identical request can only fail
        /// again, so it must not go back on the retry queue.
        /// </summary>
        Rejected,

        /// <summary>
        /// The endpoint or the credentials are the problem, not the payload: the route does not
        /// exist on this server (404) or the token is no longer accepted (401).
        ///
        /// <para>Kept distinct from <see cref="Rejected"/> because the data is perfectly good and
        /// will upload once the deploy lands or the user signs in again. Treating these as
        /// "delivered" is how telemetry silently discarded every batch while reporting success.</para>
        /// </summary>
        Unavailable
    }

    public class DeepSkyLogWatcher {
        // 30s to match the telemetry uploader; the default 100s let a black-holed connection stall
        // a retry pass for most of two minutes.
        private static readonly HttpClient client =
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }.WithIdentity();
        private static readonly string TempFolderPath = Path.GetTempPath();
        private static readonly ConcurrentQueue<string> retryQueue = new ();
        private static readonly SemaphoreSlim retrySemaphore = new(1, 1);

        private const string PendingPrefix = "dsl_request_";
        private const string RejectedPrefix = "dsl_rejected_";

        /// <summary>
        /// Ceiling on how many queued payloads one retry pass will attempt. A pass runs per saved
        /// frame, so without a cap a long backlog turns every exposure into a burst of requests.
        /// </summary>
        private const int MaxRetriesPerPass = 50;

        /// <summary>
        /// A retry pass runs once per saved frame. That is harmless when the server is up, but with
        /// the link down it means every exposure fires another <see cref="MaxRetriesPerPass"/>
        /// doomed requests — a night of imaging becomes tens of thousands of failed calls from one
        /// rig alone. After a failing pass the next one is pushed out exponentially instead.
        /// </summary>
        private const int RetryBackoffBaseSeconds = 60;
        private const int RetryBackoffMaxSeconds = 1800;

        /// <summary>
        /// Pending payloads are dropped once they are this old. A rig that is offline for weeks
        /// should not accumulate an unbounded spool that it then dumps on the server all at once.
        /// </summary>
        private const int PendingMaxAgeDays = 14;

        private static readonly Random retryJitter = new();
        private static DateTime nextRetryAllowedUtc = DateTime.MinValue;
        private static int consecutiveRetryFailures;

        public DeepSkyLogWatcher(IImageSaveMediator imageSaveMediator) {
            imageSaveMediator.ImageSaved += ImageSaveMeditator_ImageSaved;
            Logger.Info("DeepSkyLog is loading");
        }
        internal static string GetImageFilePath(Uri imageUri) {
            // Use LocalPath, not UrlDecode(AbsolutePath): AbsolutePath keeps a leading slash and a
            // '+' in the path (e.g. a target folder named "M56+92"), and UrlDecode then turns that
            // '+' into a space, producing a path that doesn't exist on disk. LocalPath resolves the
            // file:// URI to the correct Windows path directly.
            return imageUri.LocalPath;
        }

        private void ImageSaveMeditator_ImageSaved(object sender, ImageSavedEventArgs msg) {
            if (!Settings.Default.DeepSkyLogEnabled) {
                Logger.Debug("DeepSkyLog not enabled");
                return;
            }
            Logger.Info("DeepSkyLog is enabled");

//            if (msg.MetaData.Image.ImageType != ImageTypes.LIGHT && Settings.Default.DeepSkyLogAllowSnapshots == false) {
//                Logger.Debug("Image is not a light, skipping...");
//                return;
//            }

            try {
                Task.Run(() => ProcessImageSave(msg));
            } catch (Exception e) {
                Logger.Warning($"session metadata save failed: {e.Message}");
            }
        }
        private async Task ProcessImageSave(ImageSavedEventArgs msg) {
            try {
                // Attempt to retry any failed requests first
                await RetryFailedRequestsAsync();

                string imageFilePath = GetImageFilePath(msg.PathToImage);
                WeatherMetaDataRecord weatherRecord = new WeatherMetaDataRecord(msg);
                ImageMetaDataRecord imageMetaDataRecord = new ImageMetaDataRecord(msg, imageFilePath);
                AcquisitionMetaDataRecord acquisitionMetaDataRecord = new AcquisitionMetaDataRecord(msg);

                // Calculate checksum of the first 50KB of the image file
                string checksum = CalculateFileChecksum(imageFilePath);
                if (string.IsNullOrEmpty(checksum)) {
                    // File was unreadable or not yet flushed. Fall back to a deterministic key from
                    // the path and exposure start so the frame still uploads and de-duplicates on
                    // re-send — the server rejects a null checksum and drops the whole frame.
                    checksum = FallbackChecksum(imageFilePath, msg.MetaData.Image.ExposureStart);
                    Logger.Warning($"No file checksum for {imageFilePath}; using fallback {checksum}");
                }

                var combinedData = new {
                    weatherRecord,
                    imageMetaDataRecord,
                    acquisitionMetaDataRecord,
                    Checksum = checksum
                };

                // Serialize to JSON
                string json = JsonConvert.SerializeObject(combinedData);
                string tempFilePath = GetTempFilePath(msg.MetaData.Image.ExposureStart);
                Logger.Debug($"Saved DSL file to {tempFilePath}");

                // Get selected location and equipment IDs
                var (locationId, equipmentId) = GetSelectedIds();
                Logger.Debug($"Using location ID: {locationId}, equipment ID: {equipmentId}");

                // Try posting the data with location and equipment parameters
                switch (await TryPostToServerAsync(json, locationId, equipmentId)) {
                    case UploadResult.Transient:
                    case UploadResult.Unavailable:
                        // Good payload, unreachable or unauthenticated server. Spool it: a deploy
                        // or a fresh sign-in makes it uploadable without the user doing anything.
                        SaveFailedRequest(tempFilePath, json);
                        break;

                    case UploadResult.Rejected:
                        QuarantineRequest(tempFilePath, json);
                        break;
                }
            } catch (Exception ex) {
                Logger.Debug($"Unexpected error in ProcessImageSave: {ex.Message}");
            }
        }

        /// <summary>
        /// Decide whether a failed status code is worth retrying.
        ///
        /// <para>Everything used to be retried forever, which turned a permanent rejection — a
        /// deleted equipment, a lapsed subscription — into an unbounded loop: one more queued file
        /// per captured frame, with the whole queue replayed on every subsequent frame.</para>
        /// </summary>
        internal static UploadResult Classify(System.Net.HttpStatusCode status) {
            int code = (int)status;

            // Throttling and timeouts are 4xx but explicitly mean "try again later".
            if (status == System.Net.HttpStatusCode.RequestTimeout ||
                code == 429) {
                return UploadResult.Transient;
            }

            // Server-side faults: the payload is fine, the far end is not.
            if (code >= 500) {
                return UploadResult.Transient;
            }

            // Not the payload's fault: the route is missing on this server, or the token has
            // stopped being accepted. Both fix themselves without the caller changing anything.
            if (code == 401 || code == 404) {
                return UploadResult.Unavailable;
            }

            // 426: this plugin build is below the version the server will accept. Retrying cannot
            // help, but the frames are perfectly good — quarantining them means the user can replay
            // the queue from the options page once they have updated, rather than losing the night.
            if (code == 426) {
                return UploadResult.Rejected;
            }

            // Any other 4xx is about this request and will not fix itself.
            if (code >= 400) {
                return UploadResult.Rejected;
            }

            return UploadResult.Transient;
        }

        /// <summary>
        /// Raised when the server refuses an upload outright — most often a location or equipment
        /// that no longer exists in the account. Until now that verdict lived only in the log; this
        /// lets the options page and a NINA notification put it in front of the user, whose action
        /// (re-selecting in the options) is the only thing that can fix it.
        /// </summary>
        public static event Action<string> UploadRejected;

        /// <summary>Raised on a delivered upload, so a stale-selection warning can clear itself.</summary>
        public static event Action UploadSucceeded;

        /// <summary>How many rejected payloads are parked on disk waiting for the user to retry.</summary>
        public static int CountParkedUploads() {
            try {
                return Directory.GetFiles(TempFolderPath, RejectedPrefix + "*.json").Length;
            } catch (Exception) {
                return 0;
            }
        }

        /// <summary>
        /// User-initiated replay of the parked uploads. Deliberately the only path that requeues
        /// them: replaying as a side effect of a dropdown change meant one mis-click shipped the
        /// whole backlog to the wrong equipment before the user could correct it. An explicit
        /// action also resets the retry cooldown — the user just asked, so answer now — and works
        /// with no session running, when there is no frame save to piggyback on.
        /// </summary>
        public static async Task RetryParkedUploadsAsync() {
            RequeueRejectedRequests();
            nextRetryAllowedUtc = DateTime.MinValue;
            consecutiveRetryFailures = 0;
            await RetryFailedRequestsAsync();
        }

        private static void RaiseUploadOutcome(string rejectionMessage) {
            // Subscriber faults must not surface into the upload path.
            try {
                if (rejectionMessage == null) {
                    UploadSucceeded?.Invoke();
                } else {
                    UploadRejected?.Invoke(rejectionMessage);
                }
            } catch (Exception ex) {
                Logger.Debug($"DeepSkyLog upload-outcome notification failed: {ex.Message}");
            }
        }

        private static async Task<UploadResult> TryPostToServerAsync(string json, string locationId = null, string equipmentId = null) {
            try {
                Logger.Debug($"Preparing server request: {json}");

                // Build query parameters
                var queryParams = new List<string>();
                if (!string.IsNullOrEmpty(locationId))
                    queryParams.Add($"location={Uri.EscapeDataString(locationId)}");
                if (!string.IsNullOrEmpty(equipmentId))
                    queryParams.Add($"equipment={Uri.EscapeDataString(equipmentId)}");
                Logger.Debug($"Request Query Params: location={locationId} equipment={equipmentId}");
                var queryString = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";
                var baseUrl = "https://app.deepskylog.space/api/v1/nina/upload";
                var fullUrl = $"{baseUrl}{queryString}";

                using var request = new HttpRequestMessage(HttpMethod.Post, fullUrl) {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenStorage.Load());

                using var response = await client.SendAsync(request);
                Logger.Debug($"Server response: {response.StatusCode}");

                if (response.IsSuccessStatusCode) {
                    RaiseUploadOutcome(null);
                    return UploadResult.Success;
                }

                string responseContent = await response.Content.ReadAsStringAsync();
                UploadResult result = Classify(response.StatusCode);

                if (result == UploadResult.Rejected) {
                    // Surfaced at Error, not Debug: this is the only place the user is told what
                    // to actually do (e.g. "Re-select your equipment and location in the plugin
                    // settings"), and Debug logging is off for most people.
                    Logger.Error($"DeepSkyLog rejected the upload ({(int)response.StatusCode} {response.StatusCode}): {DescribeError(responseContent)}");
                    RaiseUploadOutcome(DescribeError(responseContent));
                } else if (result == UploadResult.Unavailable) {
                    Logger.Error($"DeepSkyLog is not accepting uploads ({(int)response.StatusCode} {response.StatusCode}): {DescribeError(responseContent)}. "
                                 + "Frames are being kept locally and will upload once this clears — sign in again if it persists.");
                } else {
                    Logger.Warning($"DeepSkyLog upload failed, will retry ({(int)response.StatusCode} {response.StatusCode}): {DescribeError(responseContent)}");
                }
                return result;
            } catch (Exception ex) {
                Logger.Warning($"Error posting data, will retry: {ex.Message}");
            }
            return UploadResult.Transient;
        }

        /// <summary>Pull the human-readable message out of the API error envelope, if there is one.</summary>
        private static string DescribeError(string responseContent) {
            try {
                var error = JsonConvert.DeserializeObject<ApiErrorResponse>(responseContent);
                if (!string.IsNullOrWhiteSpace(error?.Message)) {
                    return string.IsNullOrWhiteSpace(error.Error)
                        ? error.Message
                        : $"{error.Error} - {error.Message}";
                }
            } catch (Exception) {
                // Not our envelope; fall through to the raw body.
            }
            return string.IsNullOrWhiteSpace(responseContent) ? "(no response body)" : responseContent;
        }

        private static void SaveFailedRequest(string filePath, string json) {
            try {
                File.WriteAllText(filePath, json);
                retryQueue.Enqueue(filePath);
                Logger.Debug($"Failed request saved to {filePath} for retry.");
            } catch (Exception ex) {
                Logger.Warning($"Error saving failed request: {ex.Message}");
            }
        }

        /// <summary>
        /// Park a rejected payload under a name the retry scan ignores.
        ///
        /// <para>Kept on disk rather than deleted: the usual cause is a fixable misconfiguration,
        /// and <see cref="RetryParkedUploadsAsync"/> puts these back in the queue when the user
        /// clicks the retry button in the options after fixing their selection.</para>
        /// </summary>
        private static void QuarantineRequest(string filePath, string json) {
            try {
                string rejectedPath = ToRejectedPath(filePath);
                File.WriteAllText(rejectedPath, json);
                if (!string.Equals(rejectedPath, filePath, StringComparison.OrdinalIgnoreCase) && File.Exists(filePath)) {
                    File.Delete(filePath);
                }
                Logger.Warning($"Upload parked at {rejectedPath}; it will be retried if you change your DeepSkyLog location or equipment.");
            } catch (Exception ex) {
                Logger.Warning($"Error parking rejected request: {ex.Message}");
            }
        }

        private static string ToRejectedPath(string pendingPath) {
            string name = Path.GetFileName(pendingPath);
            if (name.StartsWith(PendingPrefix, StringComparison.OrdinalIgnoreCase)) {
                name = RejectedPrefix + name.Substring(PendingPrefix.Length);
            }
            return Path.Combine(TempFolderPath, name);
        }

        /// <summary>
        /// Move parked payloads back onto the retry queue. Called when the user picks a different
        /// location or equipment — the most likely fix for whatever the server objected to.
        /// </summary>
        public static void RequeueRejectedRequests() {
            try {
                int restored = 0;
                foreach (string rejectedPath in Directory.GetFiles(TempFolderPath, RejectedPrefix + "*.json")) {
                    string name = Path.GetFileName(rejectedPath);
                    string pendingPath = Path.Combine(TempFolderPath, PendingPrefix + name.Substring(RejectedPrefix.Length));
                    File.Move(rejectedPath, pendingPath, true);
                    restored++;
                }

                if (restored > 0) {
                    Logger.Info($"DeepSkyLog: re-queued {restored} previously rejected upload(s) after a settings change.");
                }
            } catch (Exception ex) {
                Logger.Warning($"Error re-queueing rejected requests: {ex.Message}");
            }
        }

        private static async Task RetryFailedRequestsAsync() {
            if (!retrySemaphore.Wait(0)) {
                return; // Avoid concurrent retries
            }

            try {
                if (DateTime.UtcNow < nextRetryAllowedUtc) {
                    return; // Still cooling down from a failed pass.
                }

                // Rebuild from disk each pass. The per-pass cap means entries can be left over,
                // and the scan re-adds every file, so without this the queue grows without bound.
                while (retryQueue.TryDequeue(out _)) { }

                foreach (string filePath in Directory.GetFiles(TempFolderPath, PendingPrefix + "*.json")) {
                    if (TryExpirePending(filePath)) {
                        continue;
                    }
                    retryQueue.Enqueue(filePath);
                }

                // Get current selected location and equipment IDs for retries
                var (locationId, equipmentId) = GetSelectedIds();

                int attempted = 0;
                int rejected = 0;
                bool transientFailure = false;

                while (attempted < MaxRetriesPerPass && retryQueue.TryDequeue(out string filePath)) {
                    if (!File.Exists(filePath)) {
                        continue; // Already handled by an earlier pass.
                    }

                    string json = File.ReadAllText(filePath);
                    attempted++;

                    switch (await TryPostToServerAsync(json, locationId, equipmentId)) {
                        case UploadResult.Success:
                            File.Delete(filePath);
                            Logger.Debug($"Retried request successfully sent and removed {filePath}.");
                            break;

                        case UploadResult.Rejected:
                            // Park it so the next frame does not replay the same doomed request.
                            QuarantineRequest(filePath, json);
                            rejected++;
                            break;

                        case UploadResult.Unavailable:
                            // Leave it pending — the payload is fine and the server will take it
                            // once it is back. Backs off on the same curve as a transient failure.
                            transientFailure = true;
                            break;

                        case UploadResult.Transient:
                            // Leave the file in place; the next pass picks it up again. Stop here
                            // rather than working through the backlog: the link is down, and the
                            // remaining files would just be another 49 failed requests.
                            transientFailure = true;
                            break;
                    }

                    if (transientFailure) {
                        break;
                    }
                }

                UpdateRetryBackoff(transientFailure);

                if (rejected > 0) {
                    Logger.Error($"DeepSkyLog: {rejected} upload(s) were rejected and are not being retried. Check your location and equipment selection in the plugin options.");
                }
            } catch (Exception ex) {
                Logger.Warning($"Error during retry process: {ex.Message}");
            } finally {
                retrySemaphore.Release();
            }
        }

        /// <summary>
        /// Pushes the next retry pass out after a failed one, and pulls it back in as soon as a pass
        /// gets through. Jittered so rigs that all lost the same server do not return in lockstep.
        /// </summary>
        private static void UpdateRetryBackoff(bool transientFailure) {
            if (!transientFailure) {
                consecutiveRetryFailures = 0;
                nextRetryAllowedUtc = DateTime.MinValue;
                return;
            }

            consecutiveRetryFailures++;
            int backoff = Math.Min(RetryBackoffBaseSeconds * (1 << Math.Min(consecutiveRetryFailures - 1, 5)),
                                   RetryBackoffMaxSeconds);
            backoff += retryJitter.Next(0, Math.Max(1, backoff / 4));
            nextRetryAllowedUtc = DateTime.UtcNow.AddSeconds(backoff);
            Logger.Debug($"DeepSkyLog retry pass backing off for {backoff}s");
        }

        /// <summary>Deletes a pending payload that is too old to be worth sending. </summary>
        private static bool TryExpirePending(string filePath) {
            try {
                if (File.GetLastWriteTimeUtc(filePath) >= DateTime.UtcNow.AddDays(-PendingMaxAgeDays)) {
                    return false;
                }
                File.Delete(filePath);
                Logger.Warning($"DeepSkyLog dropped a pending upload older than {PendingMaxAgeDays} days: {filePath}");
                return true;
            } catch (Exception ex) {
                Logger.Debug($"Could not expire pending upload {filePath}: {ex.Message}");
                return false;
            }
        }

        private static string GetTempFilePath(DateTime exposureStart) {
            string fileName = $"dsl_request_{exposureStart:yyyyMMdd_HHmmss}.json";
            return Path.Combine(TempFolderPath, fileName);
        }

        public static async Task<List<Location>> GetLocationsAsync(string apiKey) {
            try {
                string baseUrl = "https://app.deepskylog.space";
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/list/locations");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                using var response = await client.SendAsync(request);
                string responseContent = await response.Content.ReadAsStringAsync();

                Logger.Debug($"Locations API response: {response.StatusCode}");

                if (response.IsSuccessStatusCode) {
                    var locationResponse = JsonConvert.DeserializeObject<LocationListResponse>(responseContent);
                    if (locationResponse?.Success == true) {
                        return locationResponse.Locations ?? new List<Location>();
                    }
                } else {
                    var errorResponse = JsonConvert.DeserializeObject<ApiErrorResponse>(responseContent);
                    Logger.Warning($"Failed to fetch locations: {errorResponse?.Message ?? response.StatusCode.ToString()}");
                }
            } catch (Exception ex) {
                Logger.Warning($"Error fetching locations: {ex.Message}");
            }
            return new List<Location>();
        }

        public static async Task<List<Equipment>> GetEquipmentsAsync(string apiKey) {
            try {
                string baseUrl = "https://app.deepskylog.space";
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/list/equipments");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                using var response = await client.SendAsync(request);
                string responseContent = await response.Content.ReadAsStringAsync();

                Logger.Debug($"Equipment API response: {response.StatusCode}");

                if (response.IsSuccessStatusCode) {
                    var equipmentResponse = JsonConvert.DeserializeObject<EquipmentListResponse>(responseContent);
                    if (equipmentResponse?.Success == true) {
                        return equipmentResponse.Equipments ?? new List<Equipment>();
                    }
                } else {
                    var errorResponse = JsonConvert.DeserializeObject<ApiErrorResponse>(responseContent);
                    Logger.Warning($"Failed to fetch equipment: {errorResponse?.Message ?? response.StatusCode.ToString()}");
                }
            } catch (Exception ex) {
                Logger.Warning($"Error fetching equipment: {ex.Message}");
            }
            return new List<Equipment>();
        }

        private static (string locationId, string equipmentId) GetSelectedIds() {
            string locationId = null;
            string equipmentId = null;

            if (Settings.Default.SelectedLocationId > 0) {
                locationId = Settings.Default.SelectedLocationId.ToString();
                Logger.Debug($"Using location ID: {locationId}");
            }

            if (Settings.Default.SelectedEquipmentId > 0) {
                equipmentId = Settings.Default.SelectedEquipmentId.ToString();
                Logger.Debug($"Using equipment ID: {equipmentId}");
            }

            Logger.Debug($"GetSelectedIds returning: location='{locationId}', equipment='{equipmentId}'");
            return (locationId, equipmentId);
        }

        /// <summary>
        /// Check the saved location/equipment IDs against what the account actually has.
        ///
        /// <para>The options dropdowns resolve the saved ID against the fetched list, so a deleted
        /// entry simply renders blank — but uploads keep sending the saved ID, because that is read
        /// straight from settings. The result is a selector that looks merely unset while every
        /// frame is being rejected. This turns that into an explicit message.</para>
        /// </summary>
        /// <returns>A warning to show the user, or null when both selections resolve.</returns>
        public static string ValidateSelectedIds(List<Location> locations, List<Equipment> equipments) {
            var problems = new List<string>();

            int locationId = Settings.Default.SelectedLocationId;
            if (locationId > 0 && locations != null && !locations.Any(l => l.Id == locationId)) {
                problems.Add($"location {locationId}");
            }

            int equipmentId = Settings.Default.SelectedEquipmentId;
            if (equipmentId > 0 && equipments != null && !equipments.Any(e => e.Id == equipmentId)) {
                problems.Add($"equipment {equipmentId}");
            }

            if (problems.Count == 0) {
                return null;
            }

            string warning = $"Your saved {string.Join(" and ", problems)} no longer exists in your DeepSkyLog account. " +
                             "Pick it again in the plugin options — until then, frames are not being saved.";
            Logger.Error($"DeepSkyLog: {warning}");
            return warning;
        }

        // Deterministic stand-in for the file content hash when the file can't be read. Derived
        // from the path and exposure start so the same frame maps to the same key on retry, keeping
        // server-side de-duplication working. Prefixed "nocks-" to mark it as a non-content hash.
        internal static string FallbackChecksum(string filePath, DateTime exposureStart) {
            string seed = (filePath ?? string.Empty) + "|" + exposureStart.ToString("o");
            using (var sha256 = SHA256.Create()) {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(seed));
                return "nocks-" + Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
        }

        internal static string CalculateFileChecksum(string filePath) {
            try {
                if (!File.Exists(filePath)) {
                    Logger.Warning($"File not found for checksum calculation: {filePath}");
                    return null;
                }

                const int bufferSize = 50 * 1024; // 50KB
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha256 = SHA256.Create()) {
                    byte[] buffer = new byte[bufferSize];
                    int bytesRead = fileStream.Read(buffer, 0, bufferSize);
                    
                    if (bytesRead == 0) {
                        Logger.Warning($"File is empty for checksum calculation: {filePath}");
                        return null;
                    }

                    // If we read less than 50KB, resize the buffer to actual bytes read
                    if (bytesRead < bufferSize) {
                        Array.Resize(ref buffer, bytesRead);
                    }

                    byte[] hashBytes = sha256.ComputeHash(buffer);
                    string checksum = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    
                    Logger.Debug($"Calculated checksum for {filePath} (first {bytesRead} bytes): {checksum}");
                    return checksum;
                }
            } catch (Exception ex) {
                Logger.Warning($"Error calculating checksum for {filePath}: {ex.Message}");
                return null;
            }
        }

        public class ImageMetaDataRecord {
            public int ExposureNumber { get; set; }
            public string FilePath { get; set; }
            public string FilterName { get; set; }
            public string ExposureStart { get; set; }
            public double Duration { get; set; }
            public string Binning { get; set; }
            public double CameraTemp { get; set; }
            public double CameraTargetTemp { get; set; }
            public int Gain { get; set; }
            public int Offset { get; set; }
            public double ADUStDev { get; set; }
            public double ADUMean { get; set; }
            public double ADUMedian { get; set; }
            public int ADUMin { get; set; }
            public int ADUMax { get; set; }
            public int DetectedStars { get; set; }
            public double HFR { get; set; }
            public double HFRStDev { get; set; }
            public double FWHM { get; set; }
            public double Eccentricity { get; set; }
            public double GuidingRMS { get; set; }
            public double GuidingRMSArcSec { get; set; }
            public double GuidingRMSRA { get; set; }
            public double GuidingRMSRAArcSec { get; set; }
            public double GuidingRMSDEC { get; set; }
            public double GuidingRMSDECArcSec { get; set; }
            public int? FocuserPosition { get; set; }
            public double FocuserTemp { get; set; }
            public double RotatorPosition { get; set; }
            public string PierSide { get; set; }
            public double Airmass { get; set; }
            public string ExposureStartUTC { get; set; }
            public double MountRA { get; set; }
            public double MountDec { get; set; }

            public ImageMetaDataRecord() {
            }

            public ImageMetaDataRecord(ImageSavedEventArgs msg, string ImageFilePath) {
                ExposureNumber = msg.MetaData.Image.ExposureNumber;
                FilePath = ImageFilePath;
                FilterName = msg.Filter;
                ExposureStart = Utility.Utility.FormatDateTime(msg.MetaData.Image.ExposureStart);
                ExposureStartUTC = Utility.Utility.FormatDateTimeISO8601(msg.MetaData.Image.ExposureStart);
                Duration = Utility.Utility.ReformatDouble(msg.Duration);
                Binning = msg.MetaData.Image.Binning?.ToString();

                CameraTemp = Utility.Utility.ReformatDouble(msg.MetaData.Camera.Temperature);
                CameraTargetTemp = Utility.Utility.ReformatDouble(msg.MetaData.Camera.SetPoint);

                Gain = msg.MetaData.Camera.Gain;
                Offset = msg.MetaData.Camera.Offset;

                ADUStDev = Utility.Utility.ReformatDouble(msg.Statistics.StDev);
                ADUMean = Utility.Utility.ReformatDouble(msg.Statistics.Mean);
                ADUMedian = Utility.Utility.ReformatDouble(msg.Statistics.Median);
                ADUMin = msg.Statistics.Min;
                ADUMax = msg.Statistics.Max;

                DetectedStars = msg.StarDetectionAnalysis.DetectedStars;
                HFR = Utility.Utility.ReformatDouble(msg.StarDetectionAnalysis.HFR);
                HFRStDev = Utility.Utility.ReformatDouble(msg.StarDetectionAnalysis.HFRStDev);

                FWHM = GetHocusFocusMetric(msg.StarDetectionAnalysis, "FWHM");
                Eccentricity = GetHocusFocusMetric(msg.StarDetectionAnalysis, "Eccentricity");

                GuidingRMS = GetGuidingMetric(msg.MetaData.Image, msg.MetaData.Image?.RecordedRMS?.Total);
                GuidingRMSArcSec = GetGuidingMetricArcSec(msg.MetaData.Image, msg.MetaData.Image?.RecordedRMS?.Total);
                GuidingRMSRA = GetGuidingMetric(msg.MetaData.Image, msg.MetaData.Image?.RecordedRMS?.RA);
                GuidingRMSRAArcSec = GetGuidingMetricArcSec(msg.MetaData.Image, msg.MetaData.Image?.RecordedRMS?.RA);
                GuidingRMSDEC = GetGuidingMetric(msg.MetaData.Image, msg.MetaData.Image?.RecordedRMS?.Dec);
                GuidingRMSDECArcSec = GetGuidingMetricArcSec(msg.MetaData.Image, msg.MetaData.Image?.RecordedRMS?.Dec);

                FocuserPosition = msg.MetaData.Focuser.Position;
                FocuserTemp = Utility.Utility.ReformatDouble(msg.MetaData.Focuser.Temperature);
                RotatorPosition = Utility.Utility.ReformatDouble(msg.MetaData.Rotator.Position);
                PierSide = GetPierSide(msg.MetaData.Telescope.SideOfPier);

                Airmass = Utility.Utility.ReformatDouble(msg.MetaData.Telescope.Airmass);

                MountRA = msg.MetaData.Telescope.Coordinates.RADegrees;
                MountDec = msg.MetaData.Telescope.Coordinates.Dec;
            }

            private double GetHocusFocusMetric(IStarDetectionAnalysis starDetectionAnalysis, string propertyName) {
                return starDetectionAnalysis.HasProperty(propertyName) ?
                    (Double)starDetectionAnalysis.GetType().GetProperty(propertyName).GetValue(starDetectionAnalysis) :
                    Double.NaN;
            }

            private double GetGuidingMetric(ImageParameter image, double? metric) {
                return (image.RecordedRMS != null && metric != null) ? Utility.Utility.ReformatDouble((double)metric) : 0.0;
            }

            private double GetGuidingMetricArcSec(ImageParameter image, double? metric) {
                return (image.RecordedRMS != null && metric != null) ? Utility.Utility.ReformatDouble((double)(metric * image.RecordedRMS.Scale)) : 0.0;
            }

            private string GetPierSide(PierSide sideOfPier) {
                switch (sideOfPier) {
                    case NINA.Core.Enum.PierSide.pierEast: return "East";
                    case NINA.Core.Enum.PierSide.pierWest: return "West";
                    default: return "n/a";
                }
            }
        }
        public class WeatherMetaDataRecord {
            public int ExposureNumber { get; set; }
            public string ExposureStart { get; set; }
            public double Temperature { get; set; }
            public double DewPoint { get; set; }
            public double Humidity { get; set; }
            public double Pressure { get; set; }
            public double WindSpeed { get; set; }
            public double WindDirection { get; set; }
            public double WindGust { get; set; }
            public double CloudCover { get; set; }
            public double SkyTemperature { get; set; }
            public double SkyBrightness { get; set; }
            public double SkyQuality { get; set; }
            public string ExposureStartUTC { get; set; }

            public WeatherMetaDataRecord() {
            }

            public WeatherMetaDataRecord(ImageSavedEventArgs msg) {
                ExposureNumber = msg.MetaData.Image.ExposureNumber;
                ExposureStart = Utility.Utility.FormatDateTime(msg.MetaData.Image.ExposureStart);
                ExposureStartUTC = Utility.Utility.FormatDateTimeISO8601(msg.MetaData.Image.ExposureStart);
                WeatherDataParameter weatherData = msg.MetaData.WeatherData;
                Temperature = SafeRound(weatherData.Temperature, 1);
                DewPoint = SafeRound(weatherData.DewPoint, 1);
                Humidity = weatherData.Humidity;
                Pressure = weatherData.Pressure;
                WindSpeed = weatherData.WindSpeed;
                WindDirection = weatherData.WindDirection;
                WindGust = weatherData.WindGust;
                CloudCover = weatherData.CloudCover;
                SkyTemperature = SafeRound(weatherData.SkyTemperature, 1);
                SkyBrightness = weatherData.SkyBrightness;
                SkyQuality = weatherData.SkyQuality;
            }

            private double SafeRound(double value, int digits) {
                return (Double.IsNaN(value)) ? value : Math.Round(value, digits);
            }
        }

        public class AcquisitionMetaDataRecord {
            public string TargetName { get; }
            public string RACoordinates { get; }
            public string DECCoordinates { get; }
            public string TelescopeName { get; }
            public double FocalLength { get; }
            public double FocalRatio { get; }
            public string CameraName { get; }
            public double PixelSize { get; }
            public int BitDepth { get; }
            public double ObserverLatitude { get; }
            public double ObserverLongitude { get; }
            public double ObserverElevation { get; }

            public AcquisitionMetaDataRecord() { }

            public AcquisitionMetaDataRecord(ImageSavedEventArgs msg) {
                TargetName = msg.MetaData.Target.Name;
                RACoordinates = ReformatRA(msg.MetaData.Target.Coordinates?.RAString);
                DECCoordinates = ReformatDEC(msg.MetaData.Target.Coordinates?.DecString);
                TelescopeName = msg.MetaData.Telescope.Name;
                FocalLength = Utility.Utility.ReformatDouble(msg.MetaData.Telescope.FocalLength);
                FocalRatio = Utility.Utility.ReformatDouble(msg.MetaData.Telescope.FocalRatio);
                CameraName = msg.MetaData.Camera.Name;
                PixelSize = Utility.Utility.ReformatDouble(msg.MetaData.Camera.PixelSize);
                BitDepth = msg.Statistics.BitDepth;
                ObserverLatitude = Utility.Utility.ReformatDouble(msg.MetaData.Observer.Latitude);
                ObserverLongitude = Utility.Utility.ReformatDouble(msg.MetaData.Observer.Longitude);
                ObserverElevation = Utility.Utility.ReformatDouble(msg.MetaData.Observer.Elevation);
            }

            public string ReformatRA(string RAString) {
                try {
                    string pattern = @"(\d+):(\d+):(\d+)";
                    if (Regex.IsMatch(RAString, pattern)) {
                        Match match = Regex.Match(RAString, pattern);
                        return $"{Zeros(match.Groups[1].Value)}h {Zeros(match.Groups[2].Value)}m {Zeros(match.Groups[3].Value)}s";
                    } else {
                        return RAString;
                    }
                } catch (Exception) {
                    return "";
                }
            }

            private string Zeros(string value) {
                value = value.TrimStart('0');
                return (value == "") ? "0" : value;
            }

            public string ReformatDEC(string DECString) {
                return DECString != null ? DECString : "";
            }
        }

        public class Equipment {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Telescope { get; set; }
            public string Camera { get; set; }
            public string Focuser { get; set; }
            public string FilterWheel { get; set; }
            public string CaptureSoftware { get; set; }
            public string Hash { get; set; }

            public override string ToString() {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(Name)) parts.Add(Name);
                if (!string.IsNullOrEmpty(Telescope)) parts.Add(Telescope);
                if (!string.IsNullOrEmpty(Camera)) parts.Add(Camera);
                return parts.Count > 0 ? string.Join(" - ", parts) : $"Equipment {Id}";
            }
        }

        public class Location {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public double Longitude { get; set; }
            public double Latitude { get; set; }
            public double Altitude { get; set; }
            public string Timezone { get; set; }

            public override string ToString() {
                return !string.IsNullOrEmpty(Name) ? Name : $"Location {Id}";
            }
        }

        public class EquipmentListResponse {
            public bool Success { get; set; }
            public List<Equipment> Equipments { get; set; }
            public int Count { get; set; }
            public long Timestamp { get; set; }
        }

        public class LocationListResponse {
            public bool Success { get; set; }
            public List<Location> Locations { get; set; }
            public int Count { get; set; }
            public long Timestamp { get; set; }
        }

        public class ApiErrorResponse {
            public bool Success { get; set; }
            public string Error { get; set; }
            public string Message { get; set; }
            public long Timestamp { get; set; }
        }
    }
}