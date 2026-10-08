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
using NINA.Core.Enum;
using NINA.Plugin.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
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

        private readonly IMessageBroker messageBroker;
        private TargetSchedulerSubscriber schedulerSubscriber;

        /// <summary>Throttles the sequence tree walk; the uploader snapshots every few seconds.</summary>
        private DateTime lastStartScanUtc = DateTime.MinValue;
        private static readonly TimeSpan StartScanInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Target Scheduler publishes on NINA's plugin message broker. Subscribing by topic string
        /// means no assembly reference and no hard dependency: with the scheduler absent no message
        /// ever arrives, and the sequence scan below covers the plain case on its own.
        /// </summary>
        private const string SchedulerWaitTopic = "TargetScheduler-WaitStart";
        private const string SchedulerTargetTopic = "TargetScheduler-NewTargetStart";
        private const string SchedulerTargetRepeatTopic = "TargetScheduler-TargetStart";
        private const string SchedulerStoppedTopic = "TargetScheduler-ContainerStopped";
        private static readonly string[] SchedulerTopics = {
            SchedulerWaitTopic, SchedulerTargetTopic, SchedulerTargetRepeatTopic, SchedulerStoppedTopic
        };
        private static readonly Guid SchedulerSenderId =
                new Guid("B4541BA9-7B07-4D71-B8E1-6C73D4933EA0");

        /// <summary>
        /// The instructions that mean "not imaging yet". Matched by type name rather than by type
        /// so a NINA release adding another Wait* does not need a plugin rebuild to be recognised.
        /// </summary>
        private static readonly HashSet<string> WaitInstructionNames = new(StringComparer.Ordinal) {
            "WaitForTime", "WaitForTimeSpan", "WaitForAltitude",
            "WaitForSunAltitude", "WaitForMoonAltitude", "WaitUntilAboveHorizon"
        };

        private int disposed;

        public TelemetryCollector(ITelescopeMediator telescopeMediator,
                                  ISafetyMonitorMediator safetyMonitorMediator,
                                  IDomeMediator domeMediator,
                                  IFocuserMediator focuserMediator,
                                  IGuiderMediator guiderMediator,
                                  ICameraMediator cameraMediator,
                                  ISequenceMediator sequenceMediator,
                                  IImageSaveMediator imageSaveMediator,
                                  IImageHistoryVM imageHistoryVM,
                                  IMessageBroker messageBroker) {
            this.telescopeMediator = telescopeMediator;
            this.safetyMonitorMediator = safetyMonitorMediator;
            this.domeMediator = domeMediator;
            this.focuserMediator = focuserMediator;
            this.guiderMediator = guiderMediator;
            this.cameraMediator = cameraMediator;
            this.sequenceMediator = sequenceMediator;
            this.imageSaveMediator = imageSaveMediator;
            this.messageBroker = messageBroker;
            this.imageHistoryVM = imageHistoryVM;

            telescopeMediator?.RegisterConsumer(this);
            safetyMonitorMediator?.RegisterConsumer(this);
            domeMediator?.RegisterConsumer(this);
            focuserMediator?.RegisterConsumer(this);
            guiderMediator?.RegisterConsumer(this);
            cameraMediator?.RegisterConsumer(this);

            if (messageBroker != null) {
                // Wrapped: a broker that rejects a subscription must not stop telemetry starting.
                try {
                    schedulerSubscriber = new TargetSchedulerSubscriber(this);
                    foreach (string topic in SchedulerTopics) {
                        messageBroker.Subscribe(topic, schedulerSubscriber);
                    }
                } catch (Exception ex) {
                    schedulerSubscriber = null;
                    Logger.Warning($"DeepSkyLog telemetry could not subscribe to Target Scheduler: {ex.Message}");
                }
            }

            if (imageSaveMediator != null) {
                imageSaveMediator.ImageSaved += OnImageSaved;
            }
            if (imageHistoryVM?.AutoFocusPoints != null) {
                imageHistoryVM.AutoFocusPoints.CollectionChanged += OnAutoFocusPointsChanged;
            }

            AttachSequenceEvents();

            Logger.Debug("DeepSkyLog telemetry collector attached");
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
                    Logger.Debug("DeepSkyLog telemetry attached to sequence events");
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
                // Whatever the scheduler said during an earlier run is not current any more.
                SchedulerActivity.SchedulerStopped(state);
            }
            Enqueue(TelemetryEventType.SessionStart, "Sequence started", null);
            Logger.Debug($"DeepSkyLog telemetry session {sessionUuid} started");
            return Task.CompletedTask;
        }

        private Task OnSequenceFinished(object sender, EventArgs e) {
            lock (stateLock) {
                state.SequenceRunning = false;
                sessionEndQueued = true;
                // Covers a sequence aborted before the scheduler could say its container stopped.
                SchedulerActivity.SchedulerStopped(state);
            }
            // Queued before the session is marked closed so the uploader's final flush still
            // carries it under the session it belongs to.
            Enqueue(TelemetryEventType.SessionEnd, "Sequence finished", null);
            Logger.Debug($"DeepSkyLog telemetry session {sessionUuid} finished");
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
                    Logger.Debug($"DeepSkyLog telemetry session {sessionUuid} opened by a frame save");
                }
            }

            string target = msg?.MetaData?.Target?.Name;
            if (!string.IsNullOrEmpty(target)) {
                UpdateTarget(target);
            }

            ApplyRecordedGuiding(msg);
        }

        /// <summary>
        /// Takes guiding from the exposure's own recorded RMS rather than from the live guider info.
        /// <para>
        /// GuiderInfo.RMSError is only filled in by NINA's GuiderVM per guide step; every other path
        /// leaves it at its all-zero default, and the zero guard in UpdateDeviceInfo(GuiderInfo) then
        /// turns that into null. On rigs where that happens no guiding ever reaches the server, even
        /// though NINA is writing real numbers into every FITS header - measured at 0.51 to 2.03
        /// arcseconds on a night that reported nothing.
        /// </para>
        /// <para>
        /// ImageMetaData.Image.RecordedRMS is the accumulation NINA kept over the exposure itself,
        /// which is the same source the frame upload path has always used successfully. Arcseconds
        /// are derived here the same way: the stored values are in pixels, and Scale converts them.
        /// </para>
        /// </summary>
        private void ApplyRecordedGuiding(ImageSavedEventArgs msg) {
            RMS rms = msg?.MetaData?.Image?.RecordedRMS;
            // Total is zero for an exposure that was not guided at all; that is genuinely "no
            // reading" rather than perfect guiding, and must not be reported as 0.00 arcseconds.
            if (rms == null || rms.Total == 0) {
                return;
            }

            lock (stateLock) {
                state.GuidingRmsTotalArcsec = Finite(rms.Total * rms.Scale);
                state.GuidingRmsRaArcsec = Finite(rms.RA * rms.Scale);
                state.GuidingRmsDecArcsec = Finite(rms.Dec * rms.Scale);
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

                return AutoFocusReportParser.Parse(File.ReadAllText(newest.FullName));
            } catch (Exception ex) {
                Logger.Debug($"DeepSkyLog could not read the autofocus report: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------- expected imaging start

        /// <summary>
        /// Records when imaging is expected to begin, or clears it once it has.
        /// </summary>
        /// <param name="startsAtUtc">null clears the estimate</param>
        /// <param name="source">"sequence" or "targetScheduler"</param>
        internal void SetExpectedStart(DateTime? startsAtUtc, string source, string reason) {
            lock (stateLock) {
                // The scheduler is authoritative while it is driving: it knows about targets the
                // sequence tree cannot see. Don't let the generic scan overwrite what it said.
                if (startsAtUtc != null
                        && source == "sequence"
                        && state.ExpectedStartSource == "targetScheduler") {
                    return;
                }
                state.ExpectedStartAt = startsAtUtc == null
                        ? (long?)null
                        : new DateTimeOffset(DateTime.SpecifyKind(startsAtUtc.Value, DateTimeKind.Utc))
                                .ToUnixTimeMilliseconds();
                state.ExpectedStartSource = startsAtUtc == null ? null : source;
                state.ExpectedStartReason = startsAtUtc == null ? null : reason;
            }
        }

        /// <summary>Clears the estimate, but only if the given source is the one that set it.</summary>
        internal void ClearExpectedStart(string source) {
            lock (stateLock) {
                if (state.ExpectedStartSource == null || state.ExpectedStartSource == source) {
                    state.ExpectedStartAt = null;
                    state.ExpectedStartSource = null;
                    state.ExpectedStartReason = null;
                }
            }
        }

        // ------------------------------------------------------- Target Scheduler activity

        /// <summary>
        /// The scheduler picked a target. Reported straight away rather than waiting for the first
        /// frame, which comes only after the slew, centering and often an autofocus run.
        /// </summary>
        internal void OnSchedulerTargetStarted(string project, string target) {
            // A target started: the wait is over, whatever we were told earlier.
            ClearExpectedStart("targetScheduler");
            lock (stateLock) {
                SchedulerActivity.TargetStarted(state, project, target);
            }
            if (!string.IsNullOrEmpty(target)) {
                UpdateTarget(target);
            }
        }

        internal void OnSchedulerWaiting(string project, string target) {
            lock (stateLock) {
                SchedulerActivity.WaitingFor(state, project, target);
            }
        }

        internal void OnSchedulerStopped() {
            ClearExpectedStart("targetScheduler");
            lock (stateLock) {
                SchedulerActivity.SchedulerStopped(state);
            }
        }

        /// <summary>
        /// Looks for a wait instruction currently running in the advanced sequence.
        /// <para>
        /// NINA's wait instructions report a live countdown from GetEstimatedDuration() - WaitForTime,
        /// for instance, returns its target time minus now, rollover included - so the expected start
        /// is simply now plus that. One path covers every Wait* type; none of them are special-cased.
        /// </para>
        /// <para>
        /// Everything here is best-effort. The sequencer view models are built asynchronously and
        /// these accessors reach straight into them, so a throw means "not ready", never an error.
        /// </para>
        /// </summary>
        private void RefreshExpectedStartFromSequence() {
            if (sequenceMediator == null) return;

            DateTime now = DateTime.UtcNow;
            if (now - lastStartScanUtc < StartScanInterval) return;
            lastStartScanUtc = now;

            try {
                if (!sequenceMediator.Initialized || !sequenceMediator.IsAdvancedSequenceRunning()) {
                    ClearExpectedStart("sequence");
                    return;
                }

                ISequenceContainer root = RootContainer();
                ISequenceItem waiting = root == null ? null : FindRunningWait(root);
                if (waiting == null) {
                    ClearExpectedStart("sequence");
                    return;
                }

                TimeSpan remaining = waiting.GetEstimatedDuration();
                if (remaining <= TimeSpan.Zero) {
                    ClearExpectedStart("sequence");
                    return;
                }
                SetExpectedStart(now.Add(remaining), "sequence", waiting.GetType().Name);
            } catch (Exception ex) {
                Logger.Trace($"DeepSkyLog telemetry could not read the sequence start: {ex.Message}");
            }
        }

        /// <summary>
        /// The top of the running sequence. Reached by climbing from any target container, because
        /// ISequenceMediator hands out targets but never the root - and the wait that delays a night
        /// usually sits above the targets, not inside one.
        /// </summary>
        private ISequenceContainer RootContainer() {
            ISequenceContainer node = sequenceMediator.GetAllTargetsInAdvancedSequence()
                    ?.FirstOrDefault() as ISequenceContainer;
            // Bounded rather than while(true): a malformed tree must not spin here.
            for (int depth = 0; node?.Parent != null && depth < 32; depth++) {
                node = node.Parent;
            }
            return node;
        }

        /// <summary>Depth-first search for a running Wait* instruction.</summary>
        private static ISequenceItem FindRunningWait(ISequenceContainer container, int depth = 0) {
            if (container == null || depth > 32) return null;

            IList<ISequenceItem> items;
            try {
                items = container.Items;
            } catch (Exception) {
                // The sequencer mutates these collections on the UI thread; a torn read is not fatal.
                return null;
            }
            if (items == null) return null;

            foreach (ISequenceItem item in items.ToList()) {
                if (item == null || item.Status != SequenceEntityStatus.RUNNING) continue;

                if (WaitInstructionNames.Contains(item.GetType().Name)) {
                    return item;
                }
                if (item is ISequenceContainer child) {
                    ISequenceItem found = FindRunningWait(child, depth + 1);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>
        /// Receives Target Scheduler's pub/sub messages. Separate from the collector so the broker
        /// holds a reference to this and not to the collector's whole surface.
        /// </summary>
        private sealed class TargetSchedulerSubscriber : ISubscriber {

            private readonly TelemetryCollector owner;

            internal TargetSchedulerSubscriber(TelemetryCollector owner) {
                this.owner = owner;
            }

            public Task OnMessageReceived(IMessage message) {
                try {
                    // Topics are plain strings on a shared bus, so check the sender before trusting
                    // a payload that is about to become a time shown to the user.
                    if (message == null || message.SenderId != SchedulerSenderId) {
                        return Task.CompletedTask;
                    }

                    if (message.Topic == SchedulerTargetTopic || message.Topic == SchedulerTargetRepeatTopic) {
                        owner.OnSchedulerTargetStarted(ReadString(message, "ProjectName"),
                                message.Content as string);
                        return Task.CompletedTask;
                    }

                    if (message.Topic == SchedulerStoppedTopic) {
                        owner.OnSchedulerStopped();
                        return Task.CompletedTask;
                    }

                    if (message.Topic != SchedulerWaitTopic) return Task.CompletedTask;

                    owner.OnSchedulerWaiting(ReadString(message, "ProjectName"),
                            ReadString(message, "TargetName"));

                    double? seconds = ReadDouble(message, "SecondsUntilNextTarget");
                    if (seconds == null || seconds < 0) {
                        return Task.CompletedTask;
                    }

                    // Timed from when the scheduler sent it, not from now: the message may have
                    // queued, and the scheduler's countdown started at its own clock.
                    DateTime from = message.SentAt == default
                            ? DateTime.UtcNow
                            : message.SentAt.UtcDateTime;
                    owner.SetExpectedStart(from.AddSeconds(seconds.Value), "targetScheduler",
                            ReadString(message, "TargetName"));
                } catch (Exception ex) {
                    Logger.Trace($"DeepSkyLog telemetry ignored a Target Scheduler message: {ex.Message}");
                }
                return Task.CompletedTask;
            }

            /// <summary>Headers are typed object; the scheduler may send a number or its string form.</summary>
            private static double? ReadDouble(IMessage message, string key) {
                object raw = ReadHeader(message, key);
                if (raw == null) return null;
                if (raw is double d) return d;
                if (raw is int i) return i;
                if (raw is long l) return l;
                return double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)
                        ? parsed : (double?)null;
            }

            private static string ReadString(IMessage message, string key) {
                object raw = ReadHeader(message, key);
                return raw == null ? null : Convert.ToString(raw, CultureInfo.InvariantCulture);
            }

            private static object ReadHeader(IMessage message, string key) {
                IDictionary<string, object> headers = message.CustomHeaders;
                return headers != null && headers.TryGetValue(key, out object value) ? value : null;
            }
        }

        // ---------------------------------------------------------------------- reading

        /// <summary>Snapshot of the current state, safe to serialise off the caller's thread.</summary>
        public SessionState SnapshotState() {
            // Refreshed here rather than on its own timer so it costs nothing between uploads, and
            // outside the lock because it reaches into NINA's view models.
            RefreshExpectedStartFromSequence();
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
            if (messageBroker != null && schedulerSubscriber != null) {
                try {
                    foreach (string topic in SchedulerTopics) {
                        messageBroker.Unsubscribe(topic, schedulerSubscriber);
                    }
                } catch (Exception ex) {
                    Logger.Trace($"DeepSkyLog telemetry unsubscribe failed: {ex.Message}");
                }
                schedulerSubscriber = null;
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
