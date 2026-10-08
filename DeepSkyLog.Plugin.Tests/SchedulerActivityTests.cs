using DeepSkyLog.NINAPlugin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DeepSkyLog.Plugin.Tests {

    public class SchedulerActivityTests {

        [Fact]
        public void NoSchedulerMessages_ReportsNothing() {
            var state = new SessionState();
            SchedulerActivity.SchedulerStopped(state);

            Assert.Null(state.SchedulerStatus);
            Assert.Null(state.SchedulerTarget);
            Assert.Null(state.SchedulerNextTarget);
        }

        [Fact]
        public void TargetStarted_ReportsCurrentTarget() {
            var state = new SessionState();
            SchedulerActivity.TargetStarted(state, "Barnard 14", "Barnard 14 Panel 2");

            Assert.Equal(SchedulerActivity.Imaging, state.SchedulerStatus);
            Assert.Equal("Barnard 14", state.SchedulerProject);
            Assert.Equal("Barnard 14 Panel 2", state.SchedulerTarget);
        }

        [Fact]
        public void Waiting_ReportsNextTarget() {
            var state = new SessionState();
            SchedulerActivity.TargetStarted(state, "M45", "M45");
            SchedulerActivity.WaitingFor(state, "Horsehead", "Horsehead Nebula Panel 1");

            Assert.Equal(SchedulerActivity.Waiting, state.SchedulerStatus);
            Assert.Equal("Horsehead", state.SchedulerNextProject);
            Assert.Equal("Horsehead Nebula Panel 1", state.SchedulerNextTarget);
        }

        [Fact]
        public void WaitThenStart_SwitchesBackToImaging() {
            var state = new SessionState();
            SchedulerActivity.WaitingFor(state, "Horsehead", "Horsehead Nebula Panel 1");
            SchedulerActivity.TargetStarted(state, "Horsehead", "Horsehead Nebula Panel 1");

            Assert.Equal(SchedulerActivity.Imaging, state.SchedulerStatus);
            Assert.Equal("Horsehead Nebula Panel 1", state.SchedulerTarget);
        }

        [Fact]
        public void Stopped_AfterImaging_ReportsStopped() {
            var state = new SessionState();
            SchedulerActivity.TargetStarted(state, "M45", "M45");
            SchedulerActivity.SchedulerStopped(state);

            Assert.Equal(SchedulerActivity.Stopped, state.SchedulerStatus);
        }

        [Fact]
        public void Serialises_WithServerFieldNames() {
            var state = new SessionState();
            SchedulerActivity.TargetStarted(state, "M45", "M45 core");
            SchedulerActivity.WaitingFor(state, "IC 405", "Flaming Star");

            JObject json = JObject.FromObject(state);
            Assert.Equal("WAITING", (string)json["schedulerStatus"]);
            Assert.Equal("M45", (string)json["schedulerProject"]);
            Assert.Equal("M45 core", (string)json["schedulerTarget"]);
            Assert.Equal("IC 405", (string)json["schedulerNextProject"]);
            Assert.Equal("Flaming Star", (string)json["schedulerNextTarget"]);
        }
    }
}
