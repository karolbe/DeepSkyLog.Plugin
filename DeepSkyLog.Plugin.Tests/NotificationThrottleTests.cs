using System;
using DeepSkyLog.NINAPlugin;
using Xunit;

namespace DeepSkyLog.Plugin.Tests {

    public class NotificationThrottleTests {

        private DateTime now = new DateTime(2026, 9, 26, 22, 0, 0, DateTimeKind.Utc);

        private NotificationThrottle Create() => new NotificationThrottle(TimeSpan.FromMinutes(30), () => now);

        [Fact]
        public void FirstOccurrence_Notifies() {
            Assert.True(Create().ShouldNotify("upload"));
        }

        [Fact]
        public void RepeatWithinInterval_IsSuppressed() {
            var throttle = Create();
            throttle.ShouldNotify("upload");
            now = now.AddMinutes(29);
            Assert.False(throttle.ShouldNotify("upload"));
        }

        [Fact]
        public void RepeatAfterInterval_Reminds() {
            var throttle = Create();
            throttle.ShouldNotify("upload");
            now = now.AddMinutes(30);
            Assert.True(throttle.ShouldNotify("upload"));
        }

        [Fact]
        public void KeysAreIndependent() {
            var throttle = Create();
            Assert.True(throttle.ShouldNotify("telemetry"));
            Assert.True(throttle.ShouldNotify("upload"));
        }

        [Fact]
        public void Reset_RearmsImmediately() {
            var throttle = Create();
            throttle.ShouldNotify("upload");
            throttle.Reset("upload");
            Assert.True(throttle.ShouldNotify("upload"));
        }
    }
}
