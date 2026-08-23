using DeepSkyLog.NINAPlugin;
using Xunit;

namespace DeepSkyLog.Plugin.Tests {

    /// <summary>
    /// The comparison decides whether the user is told to update, and whether the wording says
    /// "available" or "required" — so its edge cases matter more than its ordinary ones. The plugin
    /// carries four-part assembly versions while the server's manifest may publish three, which is
    /// the case most likely to go wrong.
    /// </summary>
    public class UpdateCheckVersionTests {

        [Theory]
        [InlineData("1.0.0.0", "1.1.0.0")]
        [InlineData("1.9.0.0", "1.10.0.0")]   // as strings, "1.10" sorts before "1.9"
        [InlineData("1.0.0.5", "1.0.0.6")]
        [InlineData("1.0.3", "1.0.3.1")]
        public void OlderComparesLess(string older, string newer) {
            Assert.True(UpdateCheckService.CompareVersions(older, newer) < 0);
            Assert.True(UpdateCheckService.CompareVersions(newer, older) > 0);
        }

        [Theory]
        [InlineData("1.2.3", "1.2.3")]
        // The assembly version is always four-part; a three-part entry in the manifest names the
        // same release and must not read as newer.
        [InlineData("1.0.3.0", "1.0.3")]
        [InlineData(" 1.0.3.0 ", "1.0.3.0")]
        public void EqualVersionsCompareEqual(string a, string b) {
            Assert.Equal(0, UpdateCheckService.CompareVersions(a, b));
            Assert.Equal(0, UpdateCheckService.CompareVersions(b, a));
        }

        [Theory]
        [InlineData("unknown", "1.0.0")]
        [InlineData("", "1.0.0")]
        [InlineData(null, "1.0.0")]
        [InlineData("1.0.0", null)]
        [InlineData("....", "1.0.0")]
        public void UnreadableVersionsAreIndeterminate(string a, string b) {
            // ClientIdentity.Version falls back to the literal "unknown" when the assembly has no
            // version; that must not be read as "older than everything" and nag on every startup.
            Assert.Equal(0, UpdateCheckService.CompareVersions(a, b));
        }

        [Fact]
        public void ClientIdMatchesTheServerManifestKey() {
            // The server keys its published-version manifest on this exact string; a typo here
            // silently turns off version checking for the plugin.
            Assert.Equal("nina-plugin", ClientIdentity.ClientId);
        }

        [Fact]
        public void ClientVersionStringKeepsTheTelemetryFormat() {
            // The server stores this verbatim in ObservingSession.clientVersion; changing the shape
            // would break the existing rows' comparability.
            Assert.StartsWith("DeepSkyLog.NINAPlugin/", ClientIdentity.ClientVersionString);
        }
    }
}
