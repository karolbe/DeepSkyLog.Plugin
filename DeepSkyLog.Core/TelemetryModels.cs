using Newtonsoft.Json;
using System.Collections.Generic;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// Wire contract for POST /api/v1/nina/telemetry.
    ///
    /// Property names are spelled out with [JsonProperty] because the server deserialises into
    /// camelCase Java records and Jackson matches names case-sensitively.
    ///
    /// Every value in <see cref="SessionState"/> is nullable: null means "not reported", never
    /// "zero". The server leaves a null field at its previous value rather than blanking it, so a
    /// device that drops off keeps showing its last reading instead of flashing to 0.
    /// </summary>
    public class TelemetryBatch {

        [JsonProperty("sessionUuid")]
        public string SessionUuid { get; set; }

        [JsonProperty("clientVersion")]
        public string ClientVersion { get; set; }

        [JsonProperty("sentAt")]
        public long SentAt { get; set; }

        [JsonProperty("state")]
        public SessionState State { get; set; }

        [JsonProperty("events")]
        public List<TelemetryEvent> Events { get; set; }
    }

    public class SessionState {

        [JsonProperty("sequenceRunning")]
        public bool? SequenceRunning { get; set; }

        [JsonProperty("targetName")]
        public string TargetName { get; set; }

        [JsonProperty("mountRa")]
        public double? MountRa { get; set; }

        [JsonProperty("mountDec")]
        public double? MountDec { get; set; }

        [JsonProperty("altitude")]
        public double? Altitude { get; set; }

        [JsonProperty("azimuth")]
        public double? Azimuth { get; set; }

        [JsonProperty("pierSide")]
        public string PierSide { get; set; }

        [JsonProperty("tracking")]
        public bool? Tracking { get; set; }

        [JsonProperty("atPark")]
        public bool? AtPark { get; set; }

        [JsonProperty("minutesToMeridianFlip")]
        public double? MinutesToMeridianFlip { get; set; }

        [JsonProperty("safe")]
        public bool? Safe { get; set; }

        [JsonProperty("domeShutter")]
        public string DomeShutter { get; set; }

        [JsonProperty("domeAzimuth")]
        public double? DomeAzimuth { get; set; }

        [JsonProperty("domeSlaved")]
        public bool? DomeSlaved { get; set; }

        [JsonProperty("focuserPosition")]
        public long? FocuserPosition { get; set; }

        [JsonProperty("focuserTemp")]
        public double? FocuserTemp { get; set; }

        [JsonProperty("cameraTemp")]
        public double? CameraTemp { get; set; }

        [JsonProperty("cameraCoolerPower")]
        public double? CameraCoolerPower { get; set; }

        [JsonProperty("guidingRmsTotalArcsec")]
        public double? GuidingRmsTotalArcsec { get; set; }

        [JsonProperty("guidingRmsRaArcsec")]
        public double? GuidingRmsRaArcsec { get; set; }

        [JsonProperty("guidingRmsDecArcsec")]
        public double? GuidingRmsDecArcsec { get; set; }

        [JsonProperty("connectedDevices")]
        public List<string> ConnectedDevices { get; set; }

        public SessionState Clone() {
            return (SessionState)MemberwiseClone();
        }
    }

    public class TelemetryEvent {

        /// <summary>
        /// Unique within a session. The server deduplicates on this, which is what lets the
        /// uploader re-send a batch it is not sure landed without creating duplicate rows.
        /// </summary>
        [JsonProperty("clientEventId")]
        public string ClientEventId { get; set; }

        /// <summary>Epoch milliseconds from the observatory clock, not the arrival time.</summary>
        [JsonProperty("occurredAt")]
        public long OccurredAt { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("severity")]
        public string Severity { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        /// <summary>Type-specific payload; the server stores it verbatim.</summary>
        [JsonProperty("data")]
        public object Data { get; set; }
    }

    /// <summary>Event type names the server accepts. Anything else is rejected with a 400.</summary>
    public static class TelemetryEventType {
        public const string SessionStart = "SESSION_START";
        public const string SessionEnd = "SESSION_END";
        public const string AutofocusStart = "AUTOFOCUS_START";
        public const string AutofocusEnd = "AUTOFOCUS_END";
        public const string AutofocusFailed = "AUTOFOCUS_FAILED";
        public const string SafetyChanged = "SAFETY_CHANGED";
        public const string DomeShutterChanged = "DOME_SHUTTER_CHANGED";
        public const string TargetChanged = "TARGET_CHANGED";
        public const string MeridianFlip = "MERIDIAN_FLIP";
        public const string MountParked = "MOUNT_PARKED";
        public const string MountUnparked = "MOUNT_UNPARKED";
        public const string EquipmentConnected = "EQUIPMENT_CONNECTED";
        public const string EquipmentDisconnected = "EQUIPMENT_DISCONNECTED";
        public const string Error = "ERROR";
    }
}
