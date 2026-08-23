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

        /// <summary>
        /// Events drained per batch. The server rejects a batch carrying <em>more</em> than its
        /// configured cap — 200 by default — with a 400, and a 400 discards the whole batch.
        ///
        /// <para>Deliberately below that cap rather than equal to it. The server-side limit is a
        /// runtime property, so an operator can lower it without any plugin change; sending
        /// exactly 200 leaves no margin and would start silently dropping every full batch the
        /// moment that happened. The cost of the headroom is one extra request per 180 events.</para>
        /// </summary>
        private const int MaxEventsPerBatch = 180;

        private const int MinIntervalSeconds = 5;
        private const int MaxIntervalSeconds = 300;

        /// <summary>
        /// Floor between event-triggered flushes. Transitions arrive in bursts — six devices
        /// connecting, a sequence starting — and each one does not deserve its own request. This
        /// also caps a misbehaving rig (a flapping USB device, an oscillating safety monitor) at a
        /// request every few seconds instead of one per transition.
        /// </summary>
        private const int MinEventFlushIntervalSeconds = 5;

        /// <summary>
        /// Rejections tolerated at full cadence before the uploader treats them as a server-side
        /// fault and backs off. One poisoned batch should not slow a healthy session down.
        /// </summary>
        private const int RejectionsBeforeBackoff = 5;

        private static readonly HttpClient client =
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }.WithIdentity();

        private static readonly JsonSerializerSettings SerializerSettings = new() {
            // Null means "not reported"; omitting those keys keeps a batch small and lets the
            // server distinguish "unchanged" from "explicitly zero".
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly TelemetryCollector collector;
        private readonly string clientVersion;
        private readonly SemaphoreSlim flushGate = new(1, 1);
        private readonly Random jitter = new();

        /// <summary>
        /// How long telemetry may send nothing before saying so, and how often to repeat it. The
        /// reasons for staying quiet — no session, not signed in, plugin disabled — are all normal
        /// in isolation, so this only speaks up once the silence is long enough to be surprising to
        /// someone watching a live view. Logged at Warning deliberately: a remote observatory is
        /// usually running above Info, and "your live view is stale" is exactly what they need.
        /// </summary>
        private const int IdleWarningAfterSeconds = 120;
        private const int IdleWarningRepeatSeconds = 900;

        private string idleReason;
        private DateTime idleSinceUtc;
        private DateTime lastIdleWarningUtc;

        private Timer timer;
        private int currentIntervalSeconds;
        private int consecutiveFailures;
        private int consecutiveRejections;
        private long lastEventFlushTicks;
        private int disposed;

        public TelemetryUploader(TelemetryCollector collector) {
            this.collector = collector;
            clientVersion = ClientIdentity.ClientVersionString;
            collector.EventQueued += OnEventQueued;
        }

        /// <summary>
        /// Ships a transition as soon as it happens. The timer stays in place as a heartbeat for the
        /// continuously-changing state — mount position, temperatures, guiding RMS — which has no
        /// discrete moment to fire on, and whose steady arrival is what tells the web app the rig is
        /// still alive rather than silently offline.
        /// </summary>
        private void OnEventQueued() {
            if (disposed != 0) {
                return;
            }

            // While the link is down the timer is already backing off. Flushing on an event here
            // would step around that and hammer a server that is failing — the load arrives exactly
            // when the backend can least afford it. The event waits for the next scheduled tick.
            if (consecutiveFailures > 0) {
                return;
            }

            long now = DateTime.UtcNow.Ticks;
            long previous = Interlocked.Read(ref lastEventFlushTicks);
            if (now - previous < TimeSpan.FromSeconds(MinEventFlushIntervalSeconds).Ticks) {
                return; // Recently flushed; this event rides along on the next one.
            }
            if (Interlocked.CompareExchange(ref lastEventFlushTicks, now, previous) != previous) {
                return; // Another thread won the race and is already flushing.
            }

            _ = FlushAsync();
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
            // Telemetry has no user-facing switch: it follows the plugin's own enabled flag, and
            // the token check below means nothing leaves the machine unless the user is signed in.
            if (!Settings.Default.DeepSkyLogEnabled) {
                ReportIdle("the DeepSkyLog plugin is switched off in its options");
                return;
            }

            string sessionUuid = collector.SessionUuid;
            if (string.IsNullOrEmpty(sessionUuid)) {
                // No session is running, so there is nothing live to report. Equipment moving
                // outside a sequence is therefore invisible to the web app — the single most
                // likely reason a live view looks frozen while the rig is plainly doing something.
                ReportIdle("no sequence is running, so there is no live session to report on");
                return;
            }

            string token = TokenStorage.Load();
            if (string.IsNullOrEmpty(token)) {
                ReportIdle("no DeepSkyLog account is signed in");
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

                switch (await PostAsync(batch, token)) {
                    case UploadResult.Success:
                        events = null; // Delivered — nothing to put back.
                        collector.MarkSessionClosedIfFinished();
                        OnSuccess();
                        break;

                    case UploadResult.Rejected:
                        // Refused outright. Re-queuing would jam the queue behind a batch the
                        // server will never take, so the events go — but this is not success.
                        events = null;
                        collector.MarkSessionClosedIfFinished();
                        OnRejected();
                        break;

                    default: // Transient / Unavailable — good payload, keep it and slow down.
                        OnFailure();
                        break;
                }
            } catch (Exception e) {
                // Full exception, not e.Message: a bare message is how the constructor NRE went
                // undiagnosed for a week.
                Logger.Warning($"DeepSkyLog telemetry flush failed: {e}");
                OnFailure();
            } finally {
                if (events != null) {
                    collector.Requeue(events);
                }
                flushGate.Release();
            }
        }

        private async Task<UploadResult> PostAsync(TelemetryBatch batch, string token) {
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
                return UploadResult.Success;
            }

            string body = await response.Content.ReadAsStringAsync();
            UploadResult outcome = DeepSkyLogWatcher.Classify(response.StatusCode);

            if (outcome == UploadResult.Rejected) {
                // The server will never accept this batch, so re-queuing it would jam the queue
                // behind a permanently poisoned event. Covers a lapsed subscription (403) and a
                // malformed or oversized batch (400). Reporting it as delivered is deliberate:
                // the events are gone either way, and the session should keep reporting.
                Logger.Warning($"DeepSkyLog rejected a telemetry batch ({(int)response.StatusCode}): {body}");
                return UploadResult.Rejected;
            }

            if (outcome == UploadResult.Unavailable) {
                // NOT delivered. The route is missing (telemetry not deployed on this server) or
                // the token has stopped being accepted — the batch is fine and would be stored if
                // either were fixed. Counting this as success made the uploader report a healthy
                // session while silently discarding every batch, with the backoff reset each time.
                Logger.Error($"DeepSkyLog is not accepting telemetry ({(int)response.StatusCode}): {body}. "
                             + "Telemetry is paused and will resume automatically; sign in again if it persists.");
                return UploadResult.Unavailable;
            }

            Logger.Debug($"DeepSkyLog telemetry post returned {response.StatusCode}: {body}");
            return UploadResult.Transient;
        }

        /// <summary>
        /// Notes why nothing is being sent, and says so once the silence gets long enough to look
        /// like a fault to someone watching a live view. The reason is tracked rather than logged
        /// immediately because every one of them is normal in passing — between sequences, before
        /// sign-in — and only becomes interesting when it persists.
        /// </summary>
        private void ReportIdle(string reason) {
            if (!string.Equals(reason, idleReason, StringComparison.Ordinal)) {
                idleReason = reason;
                idleSinceUtc = DateTime.UtcNow;
                lastIdleWarningUtc = DateTime.MinValue;
                return;
            }

            DateTime now = DateTime.UtcNow;
            if ((now - idleSinceUtc).TotalSeconds < IdleWarningAfterSeconds) return;
            if ((now - lastIdleWarningUtc).TotalSeconds < IdleWarningRepeatSeconds) return;

            lastIdleWarningUtc = now;
            Logger.Warning($"DeepSkyLog has sent no telemetry for {(now - idleSinceUtc).TotalMinutes:F0} min: {reason}.");
        }

        private void ClearIdle() {
            idleReason = null;
        }

        private void OnSuccess() {
            ClearIdle();
            consecutiveRejections = 0;
            if (consecutiveFailures == 0) {
                return;
            }
            consecutiveFailures = 0;
            Reschedule(ConfiguredIntervalSeconds());
        }

        /// <summary>
        /// A batch the server refused outright. The events are gone either way, so an isolated bad
        /// batch keeps the normal cadence — the session should stay live.
        ///
        /// <para>A <em>run</em> of rejections is different: that is a server-side fault, not one
        /// poisoned event — a schema change, or <c>maxEventsPerBatch</c> lowered below what this
        /// build sends. Resending cannot fix it, and treating each one as success would hold the
        /// client at full cadence indefinitely with every request a 400. After a handful, fall onto
        /// the same backoff curve as a failure.</para>
        /// </summary>
        private void OnRejected() {
            consecutiveRejections++;
            if (consecutiveRejections < RejectionsBeforeBackoff) {
                return;
            }
            if (consecutiveRejections == RejectionsBeforeBackoff) {
                Logger.Error($"DeepSkyLog rejected {consecutiveRejections} telemetry batches in a row; backing off. "
                             + "This usually means the client and server disagree on the batch format.");
            }
            OnFailure();
        }

        /// <summary>
        /// Backs off up to 5 minutes while the server or the link is down, so a rig that loses its
        /// connection at dusk is not hammering a dead endpoint all night.
        /// </summary>
        private void OnFailure() {
            consecutiveFailures++;
            int backoff = Math.Min(ConfiguredIntervalSeconds() * (1 << Math.Min(consecutiveFailures, 5)),
                                   MaxIntervalSeconds);

            // Jitter, because every rig that lost the same server backs off on the same curve and
            // would otherwise come back in lockstep, re-hammering it the moment it recovers.
            backoff += jitter.Next(0, Math.Max(1, backoff / 4));
            Reschedule(Math.Min(backoff, MaxIntervalSeconds));
        }

        private void Reschedule(int seconds) {
            if (seconds == currentIntervalSeconds || disposed != 0) {
                return;
            }
            currentIntervalSeconds = seconds;
            try {
                timer?.Change(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds));
            } catch (ObjectDisposedException) {
                return; // Dispose raced this reschedule; nothing left to pace.
            }
            Logger.Debug($"DeepSkyLog telemetry interval now {seconds}s");
        }

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0) {
                return;
            }
            collector.EventQueued -= OnEventQueued;
            timer?.Dispose();
            timer = null;
            // flushGate is deliberately not disposed: a flush may still be in flight, and its
            // finally block would then throw ObjectDisposedException on Release().
            GC.SuppressFinalize(this);
        }
    }
}
