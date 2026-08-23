using DeepSkyLog.NINAPlugin;
using Xunit;

namespace DeepSkyLog.Plugin.Tests {

    /// <summary>
    /// The autofocus event reads HFR back from the report NINA writes to disk, because the image
    /// history summary the 3.1 API exposes carries positions only. These fixtures are trimmed from
    /// real reports written by NINA 3.2.0.9001.
    /// </summary>
    public class AutoFocusReportTests {

        private const string RealReport = @"{
          ""Version"": 1,
          ""Filter"": ""L-Enhance"",
          ""Timestamp"": ""2026-08-18T20:58:38.3591031+02:00"",
          ""Temperature"": 5.14,
          ""Method"": ""STARHFR"",
          ""Fitting"": ""HYPERBOLIC"",
          ""InitialFocusPoint"": { ""Position"": 25000.0, ""Value"": ""NaN"", ""Error"": 0.0 },
          ""CalculatedFocusPoint"": { ""Position"": 24947.0, ""Value"": 2.975157403454237, ""Error"": 0.0 },
          ""MeasurePoints"": [
            { ""Position"": 24550.0, ""Value"": 3.2630664535126725, ""Error"": 0.98 },
            { ""Position"": 24600.0, ""Value"": 3.2259740921737157, ""Error"": 0.96 },
            { ""Position"": 24650.0, ""Value"": 3.2414596581823787, ""Error"": 0.98 }
          ],
          ""RSquares"": {
            ""Quadratic"": 0.8177120932285222,
            ""Hyperbolic"": 0.816808971698354,
            ""LeftTrend"": 0.32968127954428617,
            ""RightTrend"": 0.8142917232266946
          },
          ""Duration"": ""00:02:32.1686663""
        }";

        [Fact]
        public void Hfr_ComesFromCalculatedFocusPoint() {
            var report = TelemetryCollector.ParseAutoFocusReport(RealReport);
            Assert.Equal(2.975157403454237, report.Hfr.Value, 6);
        }

        [Fact]
        public void Curve_CarriesPositionAndHfrPairs() {
            var report = TelemetryCollector.ParseAutoFocusReport(RealReport);

            Assert.Equal(3, report.Points.Count);
            Assert.Equal(24550.0, report.Points[0][0]);
            Assert.Equal(3.2630664535126725, report.Points[0][1], 6);
        }

        /// <summary>
        /// RSquares holds one entry per candidate fitting; only the one named by Fitting describes
        /// the curve that was actually applied. Hyperbolic here, not the first-serialised Quadratic.
        /// </summary>
        [Fact]
        public void RSquared_MatchesTheFittingActuallyUsed() {
            var report = TelemetryCollector.ParseAutoFocusReport(RealReport);
            Assert.Equal(0.816808971698354, report.RSquared.Value, 6);
        }

        [Fact]
        public void Duration_ParsedToSeconds() {
            var report = TelemetryCollector.ParseAutoFocusReport(RealReport);
            Assert.Equal(152.2, report.DurationSeconds.Value, 1);
        }

        [Fact]
        public void Method_IsReported() {
            Assert.Equal("STARHFR", TelemetryCollector.ParseAutoFocusReport(RealReport).Method);
        }

        /// <summary>
        /// NINA writes an unmeasured point as the JSON <em>string</em> "NaN" rather than a number,
        /// which a plain cast would either throw on or turn into a NaN that serialises into the
        /// payload. InitialFocusPoint is routinely NaN, which is why there is no "HFR before" here.
        /// </summary>
        [Fact]
        public void NaNString_DoesNotBecomeAValue() {
            const string json = @"{
              ""Fitting"": ""HYPERBOLIC"",
              ""CalculatedFocusPoint"": { ""Position"": 100.0, ""Value"": ""NaN"" },
              ""RSquares"": { ""Hyperbolic"": 0.9 }
            }";
            Assert.Null(TelemetryCollector.ParseAutoFocusReport(json).Hfr);
        }

        [Fact]
        public void CurveDropsUnmeasuredPoints() {
            const string json = @"{
              ""MeasurePoints"": [
                { ""Position"": 100.0, ""Value"": 2.5 },
                { ""Position"": 200.0, ""Value"": ""NaN"" }
              ]
            }";
            var report = TelemetryCollector.ParseAutoFocusReport(json);

            Assert.Single(report.Points);
            Assert.Equal(100.0, report.Points[0][0]);
        }

        /// <summary>A malformed report must cost the event its HFR, never the event itself.</summary>
        [Fact]
        public void MalformedReport_ReturnsNullRatherThanThrowing() {
            Assert.Null(TelemetryCollector.ParseAutoFocusReport("not json at all"));
        }

        [Fact]
        public void MissingSections_LeaveFieldsNull() {
            var report = TelemetryCollector.ParseAutoFocusReport(@"{ ""Filter"": ""R"" }");

            Assert.NotNull(report);
            Assert.Null(report.Hfr);
            Assert.Null(report.Points);
            Assert.Null(report.RSquared);
            Assert.Null(report.DurationSeconds);
        }
    }
}
