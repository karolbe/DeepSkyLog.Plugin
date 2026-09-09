using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>The parts of NINA's on-disk autofocus report this plugin reports on.</summary>
    public class AutoFocusReportData {
        public double? Hfr { get; set; }
        public double? DurationSeconds { get; set; }
        public string Method { get; set; }
        public double? RSquared { get; set; }
        public List<double[]> Points { get; set; }
    }

    /// <summary>
    /// Maps NINA's on-disk autofocus report JSON onto the fields reported. Split from the plugin so
    /// the edge cases (string "NaN", unmeasured curve points, a wrong-fitting RSquares entry) can be
    /// unit-tested on Linux. The plugin reads the file, this is the pure parse.
    /// </summary>
    public static class AutoFocusReportParser {

        private const int MaxCurvePoints = 100;

        /// <summary>Parses the report. Returns null on any problem: an event with positions but no
        /// HFR beats no event at all.</summary>
        public static AutoFocusReportData Parse(string json) {
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
            } catch {
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
                try { parsed = (double)token; } catch { return null; }
            }
            return (double.IsNaN(parsed) || double.IsInfinity(parsed)) ? (double?)null : parsed;
        }
    }
}
