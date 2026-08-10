using DeepSkyLog.NINAPlugin.Properties;
using NINA.Core.Utility;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// Ships batches from <see cref="TelemetryCollector"/> to DeepSkyLog on a timer.
    ///
    /// Batching rather than streaming keeps this simple and forgiving: a dropped batch costs
    /// nothing because the next one carries a fresh state snapshot, and its events are deduplicated
    /// server-side on ClientEventId. That is why a failed post just re-queues and moves on — there
    /// is no retry file, no backpressure, and nothing here can block imaging.
    /// </summary>
    public class TelemetryUploader : IDisposable {

        private const string BaseUrl = "https://app.deepskylog.space";

        /// <summary>Matches the server's per-batch cap; anything larger is rejected outright.</summary>
        private const int MaxEventsPerBatch = 200;

        private const int MinIntervalSeconds = 5;
        private const int MaxIntervalSeconds = 300;

        private static readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };

        private static readonly JsonSerializerSettings SerializerSettings = new() {
            // Null means "not reported"; omitting those keys keeps a batch small and lets the
            // server distinguish "unchanged" from "explicitly zero".
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly TelemetryCollector collector;
        private readonly string clientVersion;
        private readonly SemaphoreSlim flushGate = new(1, 1);

        private Timer timer;
        private int currentIntervalSeconds;
        private int consecutiveFailures;
        private int disposed;

        public TelemetryUploader(TelemetryCollector collector) {
            this.collector = collector;
            clientVersion = "DeepSkyLog.NINAPlugin/"
                + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown");
        }

        public void Start() {
            currentIntervalSeconds = ConfiguredIntervalSeconds();
            timer = new Timer(OnTick, null,
                TimeSpan.FromSeconds(currentIntervalSeconds),
                TimeSpan.FromSeconds(currentIntervalSeconds));
            Logger.Info($"DeepSkyLog telemetry uploader started ({currentIntervalSeconds}s interval)");
        }

        private static int ConfiguredIntervalSeconds() {
            int configured = Settings.Default.DeepSkyLogTelemetryIntervalSeconds;
            if (configured < MinIntervalSeconds) return MinIntervalSeconds;
            if (configured > MaxIntervalSeconds) return MaxIntervalSeconds;
            return configured;
        }

        private void OnTick(object ignored) {
            // Fire-and-forget: the timer thread must never wait on the network.
            _ = FlushAsync();
        }

        private async Task FlushAsync() {
            if (!Settings.Default.DeepSkyLogEnabled || !Settings.Default.DeepSkyLogTelemetryEnabled) {
                return;
            }

            string sessionUuid = collector.SessionUuid;
            if (string.IsNullOrEmpty(sessionUuid)) {
                return; // No session is running, so there is nothing live to report.
            }

            string token = TokenStorage.Load();
            if (string.IsNullOrEmpty(token)) {
                return;
            }

            // A slow post must not overlap the next tick and send the same events twice.
            if (!await flushGate.WaitAsync(0)) {
                return;
            }

            List<TelemetryEvent> events = collector.DrainEvents(MaxEventsPerBatch);
            try {
                TelemetryBatch batch = new() {
                    SessionUuid = sessionUuid,
                    ClientVersion = clientVersion,
                    SentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    State = collector.SnapshotState(),
                    Events = events
                };

                if (await PostAsync(batch, token)) {
                    events = null; // Delivered — nothing to put back.
                    collector.MarkSessionClosedIfFinished();
                    OnSuccess();
                } else {
                    OnFailure();
                }
            } catch (Exception e) {
                Logger.Warning($"DeepSkyLog telemetry flush failed: {e.Message}");
                OnFailure();
            } finally {
                if (events != null) {
                    collector.Requeue(events);
                }
                flushGate.Release();
            }
        }

        private async Task<bool> PostAsync(TelemetryBatch batch, string token) {
            var query = new List<string>();
            if (Settings.Default.SelectedLocationId > 0) {
                query.Add($"location={Settings.Default.SelectedLocationId}");
            }
            if (Settings.Default.SelectedEquipmentId > 0) {
                query.Add($"equipment={Settings.Default.SelectedEquipmentId}");
            }
            string url = $"{BaseUrl}/api/v1/nina/telemetry"
                + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

            string json = JsonConvert.SerializeObject(batch, SerializerSettings);

            using var request = new HttpRequestMessage(HttpMethod.Post, url) {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode) {
                Logger.Debug($"DeepSkyLog telemetry accepted ({batch.Events?.Count ?? 0} events)");
                return true;
            }

            string body = await response.Content.ReadAsStringAsync();
            if ((int)response.StatusCode == 400) {
                // The server will never accept this batch, so re-queuing it would jam the queue
                // behind a permanently poisoned event. Log it and let it go.
                Logger.Warning($"DeepSkyLog rejected a telemetry batch: {body}");
                return true;
            }
            Logger.Debug($"DeepSkyLog telemetry post returned {response.StatusCode}: {body}");
            return false;
        }

        private void OnSuccess() {
            if (consecutiveFailures == 0) {
                return;
            }
            consecutiveFailures = 0;
            Reschedule(ConfiguredIntervalSeconds());
        }

        /// <summary>
        /// Backs off up to 5 minutes while the server or the link is down, so a rig that loses its
        /// connection at dusk is not hammering a dead endpoint all night.
        /// </summary>
        private void OnFailure() {
            consecutiveFailures++;
            int backoff = Math.Min(ConfiguredIntervalSeconds() * (1 << Math.Min(consecutiveFailures, 5)),
                                   MaxIntervalSeconds);
            Reschedule(backoff);
        }

        private void Reschedule(int seconds) {
            if (seconds == currentIntervalSeconds || disposed != 0) {
                return;
            }
            currentIntervalSeconds = seconds;
            timer?.Change(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds));
            Logger.Debug($"DeepSkyLog telemetry interval now {seconds}s");
        }

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0) {
                return;
            }
            timer?.Dispose();
            timer = null;
            // flushGate is deliberately not disposed: a flush may still be in flight, and its
            // finally block would then throw ObjectDisposedException on Release().
            GC.SuppressFinalize(this);
        }
    }
}
