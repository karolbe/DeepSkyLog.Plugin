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
using OxyPlot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        // Autofocus run in progress: points accumulate between start and end so the finished run
        // can be reported as one event carrying the whole V-curve.
        private readonly List<double[]> autofocusPoints = new();
        private DateTime autofocusStartedAt;
        private bool autofocusRunning;

        private int disposed;

        public TelemetryCollector(ITelescopeMediator telescopeMediator,
                                  ISafetyMonitorMediator safetyMonitorMediator,
                                  IDomeMediator domeMediator,
                                  IFocuserMediator focuserMediator,
                                  IGuiderMediator guiderMediator,
                                  ICameraMediator cameraMediator,
                                  ISequenceMediator sequenceMediator,
                                  IImageSaveMediator imageSaveMediator) {
            this.telescopeMediator = telescopeMediator;
            this.safetyMonitorMediator = safetyMonitorMediator;
            this.domeMediator = domeMediator;
            this.focuserMediator = focuserMediator;
            this.guiderMediator = guiderMediator;
            this.cameraMediator = cameraMediator;
            this.sequenceMediator = sequenceMediator;
            this.imageSaveMediator = imageSaveMediator;

            telescopeMediator?.RegisterConsumer(this);
            safetyMonitorMediator?.RegisterConsumer(this);
            domeMediator?.RegisterConsumer(this);
            focuserMediator?.RegisterConsumer(this);
            guiderMediator?.RegisterConsumer(this);
            cameraMediator?.RegisterConsumer(this);

            if (sequenceMediator != null) {
                sequenceMediator.SequenceStarting += OnSequenceStarting;
                sequenceMediator.SequenceFinished += OnSequenceFinished;
            }
            if (imageSaveMediator != null) {
                imageSaveMediator.ImageSaved += OnImageSaved;
            }

            Logger.Info("DeepSkyLog telemetry collector attached");
        }

        /// <summary>Null while no session is open, which is the uploader's cue to stay quiet.</summary>
        public string SessionUuid {
            get { lock (stateLock) { return sessionClosed ? null : sessionUuid; } }
        }

        // ------------------------------------------------------------- session lifecycle

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
                state.DomeSlaved = info.DriverFollowing || info.ApplicationFollowing;
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

            lock (stateLock) {
                if (info.RMSError != null) {
                    state.GuidingRmsTotalArcsec = Finite(info.RMSError.Total?.Arcseconds);
                    state.GuidingRmsRaArcsec = Finite(info.RMSError.RA?.Arcseconds);
                    state.GuidingRmsDecArcsec = Finite(info.RMSError.Dec?.Arcseconds);
                }
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

        public void AutoFocusRunStarting() {
            lock (stateLock) {
                autofocusPoints.Clear();
                autofocusStartedAt = DateTime.UtcNow;
                autofocusRunning = true;
            }
            Enqueue(TelemetryEventType.AutofocusStart, "Autofocus started", null);
        }

        public void NewAutoFocusPoint(DataPoint dataPoint) {
            lock (stateLock) {
                if (autofocusRunning) {
                    autofocusPoints.Add(new[] { dataPoint.X, dataPoint.Y });
                }
            }
        }

        public void UpdateEndAutoFocusRun(AutoFocusInfo info) {
            if (info == null) return;

            List<double[]> points;
            double durationSeconds;
            lock (stateLock) {
                points = new List<double[]>(autofocusPoints);
                durationSeconds = autofocusRunning
                    ? Math.Round((DateTime.UtcNow - autofocusStartedAt).TotalSeconds, 1)
                    : 0;
                autofocusRunning = false;
                autofocusPoints.Clear();
                state.FocuserPosition = (long)info.Position;
                state.FocuserTemp = Finite(info.Temperature);
            }

            Enqueue(TelemetryEventType.AutofocusEnd,
                    $"Autofocus {info.Filter} to {info.Position:F0}",
                    new {
                        filter = info.Filter,
                        position = info.Position,
                        temperature = info.Temperature,
                        durationSeconds,
                        points
                    });
        }

        public void UpdateUserFocused(FocuserInfo info) {
            UpdateDeviceInfo(info);
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

        private void Enqueue(string type, string message, object data, string severity = "INFO") {
            Enqueue(new TelemetryEvent {
                ClientEventId = Guid.NewGuid().ToString("N"),
                OccurredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = type,
                Severity = severity,
                Message = message,
                Data = data
            });
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

            if (sequenceMediator != null) {
                sequenceMediator.SequenceStarting -= OnSequenceStarting;
                sequenceMediator.SequenceFinished -= OnSequenceFinished;
            }
            if (imageSaveMediator != null) {
                imageSaveMediator.ImageSaved -= OnImageSaved;
            }

            GC.SuppressFinalize(this);
        }
    }
}
