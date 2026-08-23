using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Model;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// Listens to NINA's equipment mediators and turns them into a state snapshot plus a queue of
    /// discrete events, which <see cref="TelemetryUploader"/> ships to DeepSkyLog.
    ///
    /// The split matters: continuous values (mount position, temperatures, guiding RMS) only ever
    /// ride along in the snapshot, so a dropped batch costs nothing — the next one carries fresh
    /// numbers. Only *transitions* become events, because those are the things you would be sorry
    /// to lose: a safety trip, a roof closing, an autofocus run.
    ///
    /// A session runs from SequenceStarting to SequenceFinished. Between sequences nothing is sent,
    /// because there is no session to report on.
    /// </summary>
    public class TelemetryCollector : ITelescopeConsumer, ISafetyMonitorConsumer, IDomeConsumer,
                                      IFocuserConsumer, IGuiderConsumer, ICameraConsumer {

        /// <summary>
        /// Bounded so a night of disconnection cannot grow the queue without limit. At the point
        /// where 2000 events are backed up, the oldest are the least interesting.
        /// </summary>
        private const int MaxQueuedEvents = 2000;

        private readonly object stateLock = new();
        private readonly ConcurrentQueue<TelemetryEvent> events = new();

        private readonly ITelescopeMediator telescopeMediator;
        private readonly ISafetyMonitorMediator safetyMonitorMediator;
        private readonly IDomeMediator domeMediator;
        private readonly IFocuserMediator focuserMediator;
        private readonly IGuiderMediator guiderMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly ISequenceMediator sequenceMediator;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IImageHistoryVM imageHistoryVM;

        private readonly SessionState state = new();
        private readonly HashSet<string> connectedDevices = new();

        private string sessionUuid;
        private bool sessionClosed = true;
        private bool sessionEndQueued;

        // Previous values, kept only to detect the transitions worth turning into events.
        private bool? lastSafe;
        private string lastShutter;
        private bool? lastAtPark;
        private string lastPierSide;
        private string lastTarget;

        /// <summary>
        /// How often to re-attempt the sequence-event subscription while NINA is still building its
        /// sequencer view models, and how long to keep trying before giving up.
        /// </summary>
        private const int SequenceAttachRetrySeconds = 5;
        private const int SequenceAttachMaxAttempts = 60;

        /// <summary>
        /// How far the on-disk report's timestamp may sit from the run being reported. Wide enough
        /// for the write to land after the collection fires, tight enough that a report from an
        /// earlier run in the same session is never picked up instead.
        /// </summary>
        private const int ReportMatchWindowSeconds = 120;

        /// <summary>Caps the V-curve so an unusual run cannot bloat a telemetry batch.</summary>
        private const int MaxCurvePoints = 100;

        private readonly object sequenceAttachLock = new();
        private Timer sequenceAttachTimer;
        private bool sequenceEventsAttached;
        private int sequenceAttachAttempts;

        private int disposed;

        public TelemetryCollector(ITelescopeMediator telescopeMediator,
                                  ISafetyMonitorMediator safetyMonitorMediator,
                                  IDomeMediator domeMediator,
                                  IFocuserMediator focuserMediator,
                                  IGuiderMediator guiderMediator,
                                  ICameraMediator cameraMediator,
                                  ISequenceMediator sequenceMediator,
                                  IImageSaveMediator imageSaveMediator,
                                  IImageHistoryVM imageHistoryVM) {
            this.telescopeMediator = telescopeMediator;
            this.safetyMonitorMediator = safetyMonitorMediator;
            this.domeMediator = domeMediator;
            this.focuserMediator = focuserMediator;
            this.guiderMediator = guiderMediator;
            this.cameraMediator = cameraMediator;
            this.sequenceMediator = sequenceMediator;
            this.imageSaveMediator = imageSaveMediator;
            this.imageHistoryVM = imageHistoryVM;

            telescopeMediator?.RegisterConsumer(this);
            safetyMonitorMediator?.RegisterConsumer(this);
            domeMediator?.RegisterConsumer(this);
            focuserMediator?.RegisterConsumer(this);
            guiderMediator?.RegisterConsumer(this);
            cameraMediator?.RegisterConsumer(this);

            if (imageSaveMediator != null) {
                imageSaveMediator.ImageSaved += OnImageSaved;
            }
            if (imageHistoryVM?.AutoFocusPoints != null) {
                imageHistoryVM.AutoFocusPoints.CollectionChanged += OnAutoFocusPointsChanged;
            }

            AttachSequenceEvents();

            Logger.Info("DeepSkyLog telemetry collector attached");
        }

        /// <summary>Null while no session is open, which is the uploader's cue to stay quiet.</summary>
        public string SessionUuid {
            get { lock (stateLock) { return sessionClosed ? null : sessionUuid; } }
        }

        // ------------------------------------------------------------- session lifecycle

        /// <summary>
        /// ISequenceMediator's SequenceStarting/SequenceFinished accessors reach straight through to
        /// NINA's sequencer view models, which are built asynchronously *after* plugins are
        /// constructed. Subscribing from the constructor therefore throws, so the subscription is
        /// retried on a timer until the sequencer is up.
        /// </summary>
        private void AttachSequenceEvents() {
            if (sequenceMediator == null || TryAttachSequenceEvents()) {
                return;
            }

            lock (sequenceAttachLock) {
                if (sequenceEventsAttached || sequenceAttachTimer != null) return;
                TimeSpan interval = TimeSpan.FromSeconds(SequenceAttachRetrySeconds);
                sequenceAttachTimer = new Timer(_ => OnSequenceAttachTick(), null, interval, interval);
            }
        }

        private void OnSequenceAttachTick() {
            if (Volatile.Read(ref disposed) != 0 || TryAttachSequenceEvents()) {
                StopSequenceAttachTimer();
                return;
            }

            if (Interlocked.Increment(ref sequenceAttachAttempts) >= SequenceAttachMaxAttempts) {
                StopSequenceAttachTimer();
                Logger.Warning("DeepSkyLog telemetry gave up waiting for the NINA sequencer; "
                             + "session start/end events will not be reported");
            }
        }

        private bool TryAttachSequenceEvents() {
            lock (sequenceAttachLock) {
                if (sequenceEventsAttached) return true;

                // A tick that raced Dispose must not attach after the detach block has already run.
                // Dispose sets the flag before taking this lock, so checking it here is enough;
                // returning true stops the retry timer.
                if (Volatile.Read(ref disposed) != 0) return true;

                try {
                    // Both this check and the subscription below can throw while the sequencer is
                    // still coming up, which is the signal to try again later.
                    if (!sequenceMediator.Initialized) return false;

                    sequenceMediator.SequenceStarting += OnSequenceStarting;
                    sequenceMediator.SequenceFinished += OnSequenceFinished;
                    sequenceEventsAttached = true;
                    Logger.Info("DeepSkyLog telemetry attached to sequence events");
                    return true;
                } catch (Exception ex) {
                    Logger.Trace($"DeepSkyLog telemetry sequencer not ready yet: {ex.Message}");
                    return false;
                }
            }
        }

        private void StopSequenceAttachTimer() {
            lock (sequenceAttachLock) {
                sequenceAttachTimer?.Dispose();
                sequenceAttachTimer = null;
            }
        }

        private Task OnSequenceStarting(object sender, EventArgs e) {
            lock (stateLock) {
                sessionUuid = Guid.NewGuid().ToString("N");
                sessionClosed = false;
                sessionEndQueued = false;
                state.SequenceRunning = true;
            }
            Enqueue(TelemetryEventType.SessionStart, "Sequence started", null);
            Logger.Info($"DeepSkyLog telemetry session {sessionUuid} started");
            return Task.CompletedTask;
        }

        private Task OnSequenceFinished(object sender, EventArgs e) {
            lock (stateLock) {
                state.SequenceRunning = false;
                sessionEndQueued = true;
            }
            // Queued before the session is marked closed so the uploader's final flush still
            // carries it under the session it belongs to.
            Enqueue(TelemetryEventType.SessionEnd, "Sequence finished", null);
            Logger.Info($"DeepSkyLog telemetry session {sessionUuid} finished");
            return Task.CompletedTask;
        }

        /// <summary>
        /// The uploader calls this once it has flushed the batch containing SESSION_END, so the
        /// closing event is never stranded in the queue by the session going quiet first.
        /// </summary>
        public void MarkSessionClosedIfFinished() {
            lock (stateLock) {
                if (sessionEndQueued && !sessionClosed && events.IsEmpty) {
                    sessionClosed = true;
                    sessionEndQueued = false;
                }
            }
        }

        private void OnImageSaved(object sender, ImageSavedEventArgs msg) {
            // A frame landing outside the advanced sequencer (simple sequencer, manual capture)
            // still means a session is under way — open one so the rig shows up as live.
            lock (stateLock) {
                if (sessionClosed) {
                    sessionUuid = Guid.NewGuid().ToString("N");
                    sessionClosed = false;
                    sessionEndQueued = false;
                    Logger.Info($"DeepSkyLog telemetry session {sessionUuid} opened by a frame save");
                }
            }

            string target = msg?.MetaData?.Target?.Name;
            if (!string.IsNullOrEmpty(target)) {
                UpdateTarget(target);
            }
        }

        private void UpdateTarget(string target) {
            bool changed;
            lock (stateLock) {
                changed = !string.Equals(lastTarget, target, StringComparison.Ordinal);
                lastTarget = target;
                state.TargetName = target;
            }
            if (changed) {
                Enqueue(TelemetryEventType.TargetChanged, $"Target: {target}", new { target });
            }
        }

        // --------------------------------------------------------------- device updates

        public void UpdateDeviceInfo(TelescopeInfo info) {
            if (info == null) return;
            TrackConnection("Mount", info.Connected);
            if (!info.Connected) return;

            bool parked;
            bool? previousPark;
            string pierSide = info.SideOfPier.ToString();
            string previousPier;

            lock (stateLock) {
                if (info.Coordinates != null) {
                    state.MountRa = info.Coordinates.RADegrees;
                    state.MountDec = info.Coordinates.Dec;
                }
                state.Altitude = Finite(info.Altitude);
                state.Azimuth = Finite(info.Azimuth);
                state.PierSide = pierSide;
                state.Tracking = info.TrackingEnabled;
                state.AtPark = info.AtPark;
                state.MinutesToMeridianFlip = Finite(info.TimeToMeridianFlip * 60.0);

                parked = info.AtPark;
                previousPark = lastAtPark;
                lastAtPark = parked;
                previousPier = lastPierSide;
                lastPierSide = pierSide;
            }

            if (previousPark.HasValue && previousPark.Value != parked) {
                Enqueue(parked ? TelemetryEventType.MountParked : TelemetryEventType.MountUnparked,
                        parked ? "Mount parked" : "Mount unparked", null);
            }
            // A pier-side change while tracking is a meridian flip; NINA has no dedicated event for
            // it, so the transition is the signal.
            if (previousPier != null && previousPier != pierSide && !parked) {
                Enqueue(TelemetryEventType.MeridianFlip,
                        $"Meridian flip: {previousPier} to {pierSide}",
                        new { from = previousPier, to = pierSide });
            }
        }

        public void UpdateDeviceInfo(SafetyMonitorInfo info) {
            if (info == null) return;
            TrackConnection("SafetyMonitor", info.Connected);
            if (!info.Connected) return;

            bool safe = info.IsSafe;
            bool? previous;
            lock (stateLock) {
                state.Safe = safe;
                previous = lastSafe;
                lastSafe = safe;
            }

            if (previous.HasValue && previous.Value != safe) {
                Enqueue(TelemetryEventType.SafetyChanged,
                        safe ? "Conditions became safe" : "Conditions became UNSAFE",
                        new { safe },
                        safe ? "INFO" : "WARNING");
            }
        }

        public void UpdateDeviceInfo(DomeInfo info) {
            if (info == null) return;
            TrackConnection("Dome", info.Connected);
            if (!info.Connected) return;

            string shutter = info.ShutterStatus.ToString();
            string previous;
            lock (stateLock) {
                state.DomeShutter = shutter;
                state.DomeAzimuth = Finite(info.Azimuth);
                state.DomeSlaved = info.DriverFollowing;
                previous = lastShutter;
                lastShutter = shutter;
            }

            if (previous != null && previous != shutter) {
                bool closing = shutter.IndexOf("Close", StringComparison.OrdinalIgnoreCase) >= 0;
                Enqueue(TelemetryEventType.DomeShutterChanged,
                        $"Roof: {previous} to {shutter}",
                        new { from = previous, to = shutter },
                        closing ? "WARNING" : "INFO");
            }
        }

        public void UpdateDeviceInfo(FocuserInfo info) {
            if (info == null) return;
            TrackConnection("Focuser", info.Connected);
            if (!info.Connected) return;

            lock (stateLock) {
                state.FocuserPosition = info.Position;
                state.FocuserTemp = Finite(info.Temperature);
            }
        }

        public void UpdateDeviceInfo(GuiderInfo info) {
            if (info == null) return;
            TrackConnection("Guider", info.Connected);
            if (!info.Connected) return;

            double? total = Finite(info.RMSError?.Total?.Arcseconds);
            double? ra = Finite(info.RMSError?.RA?.Arcseconds);
            double? dec = Finite(info.RMSError?.Dec?.Arcseconds);

            // A connected-but-idle guider reports a flat zero RMS, and NINA 3.1's GuiderInfo has no
            // "is guiding" flag to tell that apart from a real measurement. Reporting the zero made
            // "not guiding" and "guiding perfectly" identical on the live view, so an all-zero
            // reading is treated as no reading. A genuine RMS over real samples is never exactly 0.
            bool guiding = (total ?? 0) != 0 || (ra ?? 0) != 0 || (dec ?? 0) != 0;

            lock (stateLock) {
                state.GuidingRmsTotalArcsec = guiding ? total : null;
                state.GuidingRmsRaArcsec = guiding ? ra : null;
                state.GuidingRmsDecArcsec = guiding ? dec : null;
            }
        }

        public void UpdateDeviceInfo(CameraInfo info) {
            if (info == null) return;
            TrackConnection("Camera", info.Connected);
            if (!info.Connected) return;

            lock (stateLock) {
                state.CameraTemp = Finite(info.Temperature);
                state.CameraCoolerPower = info.CoolerOn ? Finite(info.CoolerPower) : null;
            }
        }

        // -------------------------------------------------------------------- autofocus

        public void UpdateEndAutoFocusRun(AutoFocusInfo info) {
            if (info == null) return;

            lock (stateLock) {
                state.FocuserPosition = (long)info.Position;
                state.FocuserTemp = Finite(info.Temperature);
            }
        }

        /// <summary>
        /// IFocuserConsumer.NewAutoFocusPoint (the live per-measurement callback) is 3.2-only, so the
        /// run is picked up from the image history VM instead. That summary carries positions but no
        /// HFR, which left the event unable to say whether focus actually improved — so the measured
        /// values are read back from the report NINA writes to disk for every run.
        /// </summary>
        private void OnAutoFocusPointsChanged(object sender, NotifyCollectionChangedEventArgs e) {
            if (e.NewItems == null) return;

            // AsyncObservableCollection raises this on NINA's UI thread, mid-autofocus. The items
            // are snapshotted here, but the report lookup reads from disk, so that part runs on the
            // thread pool rather than stalling the interface.
            List<ImageHistoryPoint> items = e.NewItems.OfType<ImageHistoryPoint>()
                .Where(item => item.AutoFocusPoint != null)
                .ToList();
            if (items.Count == 0) return;

            _ = Task.Run(() => RecordAutoFocusRuns(items));
        }

        private void RecordAutoFocusRuns(List<ImageHistoryPoint> items) {
            try {
                foreach (ImageHistoryPoint item in items) {
                    AutoFocusPoint afPoint = item.AutoFocusPoint;

                    AutoFocusReportData report = ReadAutoFocusReport(afPoint.Time);

                    Enqueue(TelemetryEventType.AutofocusEnd,
                            DescribeAutofocus(afPoint, report),
                            new {
                                filter = afPoint.Filter,
                                position = afPoint.NewPosition,
                                previousPosition = afPoint.OldPosition,
                                temperature = afPoint.Temperature,
                                // HFR at the position autofocus settled on, and the frame HFR that
                                // preceded the run. Null when the report could not be read.
                                hfr = report?.Hfr,
                                previousHfr = Finite(item.HFR) is double h && h > 0 ? h : (double?)null,
                                durationSeconds = report?.DurationSeconds,
                                method = report?.Method,
                                rSquared = report?.RSquared,
                                points = report?.Points
                            });
                }
            } catch (Exception ex) {
                Logger.Error("DeepSkyLog failed to record an autofocus run", ex);
            }
        }

        private static string DescribeAutofocus(AutoFocusPoint afPoint, AutoFocusReportData report) {
            string move = $"Autofocus {afPoint.Filter}: {afPoint.OldPosition:F0} to {afPoint.NewPosition:F0}";
            return report?.Hfr is double hfr ? $"{move} (HFR {hfr:F2})" : move;
        }

        public void UpdateUserFocused(FocuserInfo info) {
            UpdateDeviceInfo(info);
        }

        /// <summary>The parts of NINA's on-disk autofocus report this plugin reports on.</summary>
        internal class AutoFocusReportData {
            public double? Hfr { get; set; }
            public double? DurationSeconds { get; set; }
            public string Method { get; set; }
            public double? RSquared { get; set; }
            public List<double[]> Points { get; set; }
        }

        /// <summary>
        /// Reads the report NINA writes to %localappdata%\NINA\AutoFocus for every run. This is the
        /// only route to the measured HFR on 3.1 — the image-history summary keeps positions only —
        /// and it carries the whole V-curve, which the 3.2 callback would have delivered point by
        /// point.
        ///
        /// <para>Matched on timestamp rather than "newest file", so a run that failed to write, or a
        /// stale report from an earlier session, cannot be attributed to this one. Returns null on
        /// any problem: an event with positions but no HFR beats no event at all.</para>
        /// </summary>
        private static AutoFocusReportData ReadAutoFocusReport(DateTime runTime) {
            try {
                string folder = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AutoFocus");
                if (!Directory.Exists(folder)) return null;

                FileInfo newest = new DirectoryInfo(folder)
                    .GetFiles("*.json")
                    .Where(f => Math.Abs((f.LastWriteTime - runTime).TotalSeconds) <= ReportMatchWindowSeconds)
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();
                if (newest == null) return null;

                return ParseAutoFocusReport(File.ReadAllText(newest.FullName));
            } catch (Exception ex) {
                Logger.Debug($"DeepSkyLog could not read the autofocus report: {ex.Message}");
                return null;
            }
        }

        /// <summary>Maps NINA's report JSON onto the fields reported. Split out to be testable.</summary>
        internal static AutoFocusReportData ParseAutoFocusReport(string json) {
            try {
                JObject report = JObject.Parse(json);

                List<double[]> points = report["MeasurePoints"]?
                    .Select(p => new[] { Value(p["Position"]) ?? double.NaN, Value(p["Value"]) ?? double.NaN })
                    .Where(p => !double.IsNaN(p[0]) && !double.IsNaN(p[1]))
                    .Take(MaxCurvePoints)
                    .ToList();

                return new AutoFocusReportData {
                    Hfr = Value(report["CalculatedFocusPoint"]?["Value"]),
                    DurationSeconds = TimeSpan.TryParse((string)report["Duration"], out TimeSpan d)
                        ? Math.Round(d.TotalSeconds, 1)
                        : (double?)null,
                    Method = (string)report["Method"],
                    RSquared = RSquaredForFitting(report),
                    Points = points != null && points.Count > 0 ? points : null
                };
            } catch (Exception ex) {
                Logger.Debug($"DeepSkyLog could not parse the autofocus report: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Picks the goodness-of-fit for the curve autofocus actually used. <c>RSquares</c> holds one
        /// entry per candidate fitting (Quadratic, Hyperbolic, LeftTrend, RightTrend) and
        /// <c>Fitting</c> names the winner, so reporting any other entry — or whichever happened to
        /// be serialised first — would describe a curve that was not applied.
        /// </summary>
        private static double? RSquaredForFitting(JObject report) {
            JToken squares = report["RSquares"];
            string fitting = (string)report["Fitting"];
            if (squares == null || string.IsNullOrEmpty(fitting)) return null;

            foreach (JProperty candidate in squares.Children<JProperty>()) {
                if (string.Equals(candidate.Name, fitting, StringComparison.OrdinalIgnoreCase)) {
                    return Value(candidate.Value);
                }
            }
            return null;
        }

        /// <summary>
        /// Reads a number out of the report, rejecting the non-finite ones. NINA writes
        /// <c>"NaN"</c> as a JSON <em>string</em> for an unmeasured point — most often
        /// InitialFocusPoint, which is why there is no reliable "HFR before" in the report.
        /// </summary>
        private static double? Value(JToken token) {
            if (token == null || token.Type == JTokenType.Null) return null;
            double parsed;
            if (token.Type == JTokenType.String) {
                if (!double.TryParse((string)token, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) {
                    return null;
                }
            } else {
                try { parsed = (double)token; } catch (Exception) { return null; }
            }
            return Finite(parsed);
        }

        // ---------------------------------------------------------------------- reading

        /// <summary>Snapshot of the current state, safe to serialise off the caller's thread.</summary>
        public SessionState SnapshotState() {
            lock (stateLock) {
                SessionState copy = state.Clone();
                copy.ConnectedDevices = connectedDevices.OrderBy(d => d).ToList();
                return copy;
            }
        }

        /// <summary>Drains up to <paramref name="max"/> queued events.</summary>
        public List<TelemetryEvent> DrainEvents(int max) {
            List<TelemetryEvent> drained = new();
            while (drained.Count < max && events.TryDequeue(out TelemetryEvent item)) {
                drained.Add(item);
            }
            return drained;
        }

        /// <summary>
        /// Puts events back at the tail after a failed upload. Order within a batch is preserved;
        /// interleaving with newer events is harmless because each carries its own timestamp.
        /// </summary>
        public void Requeue(IEnumerable<TelemetryEvent> failed) {
            foreach (TelemetryEvent item in failed) {
                Enqueue(item);
            }
        }

        // ---------------------------------------------------------------------- helpers

        private void TrackConnection(string device, bool connected) {
            bool changed;
            lock (stateLock) {
                changed = connected ? connectedDevices.Add(device) : connectedDevices.Remove(device);
            }
            if (!changed) return;

            Enqueue(connected ? TelemetryEventType.EquipmentConnected
                              : TelemetryEventType.EquipmentDisconnected,
                    $"{device} {(connected ? "connected" : "disconnected")}",
                    new { device });
        }

        /// <summary>
        /// Raised when a new transition is queued, so the uploader can ship it straight away instead
        /// of waiting out the heartbeat interval. Deliberately not raised by <see cref="Requeue"/>:
        /// re-queuing happens after a failed post, and flushing on it would defeat the backoff.
        /// </summary>
        public event Action EventQueued;

        private void Enqueue(string type, string message, object data, string severity = "INFO") {
            Enqueue(new TelemetryEvent {
                ClientEventId = Guid.NewGuid().ToString("N"),
                OccurredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = type,
                Severity = severity,
                Message = message,
                Data = data
            });

            try {
                EventQueued?.Invoke();
            } catch (Exception ex) {
                // Raised from NINA's own callbacks; a subscriber fault must not surface there.
                Logger.Error("DeepSkyLog telemetry event notification failed", ex);
            }
        }

        private void Enqueue(TelemetryEvent item) {
            events.Enqueue(item);
            while (events.Count > MaxQueuedEvents && events.TryDequeue(out _)) {
                // Drop oldest first: a backlog this deep means the link has been down for hours,
                // and the recent state is what anyone looking at the live view wants.
            }
        }

        private static double? Finite(double value) {
            return double.IsNaN(value) || double.IsInfinity(value) ? (double?)null : value;
        }

        private static double? Finite(double? value) {
            return value.HasValue ? Finite(value.Value) : null;
        }

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0) {
                return;
            }

            telescopeMediator?.RemoveConsumer(this);
            safetyMonitorMediator?.RemoveConsumer(this);
            domeMediator?.RemoveConsumer(this);
            focuserMediator?.RemoveConsumer(this);
            guiderMediator?.RemoveConsumer(this);
            cameraMediator?.RemoveConsumer(this);

            StopSequenceAttachTimer();
            lock (sequenceAttachLock) {
                if (sequenceEventsAttached) {
                    try {
                        sequenceMediator.SequenceStarting -= OnSequenceStarting;
                        sequenceMediator.SequenceFinished -= OnSequenceFinished;
                    } catch (Exception ex) {
                        Logger.Trace($"DeepSkyLog telemetry could not detach sequence events: {ex.Message}");
                    }
                    sequenceEventsAttached = false;
                }
            }
            if (imageSaveMediator != null) {
                imageSaveMediator.ImageSaved -= OnImageSaved;
            }
            if (imageHistoryVM?.AutoFocusPoints != null) {
                imageHistoryVM.AutoFocusPoints.CollectionChanged -= OnAutoFocusPointsChanged;
            }

            GC.SuppressFinalize(this);
        }
    }
}
