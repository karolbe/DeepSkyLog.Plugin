using System;
using System.IO;
using System.Linq;
using DeepSkyLog.NINAPlugin;
using Xunit;

namespace DeepSkyLog.Plugin.Tests {

    public class GetImageFilePathTests {

        [Fact]
        public void PlusInTargetName_PreservedAsPlus() {
            var uri = new Uri(@"C:\Images\M56+92FlatWizard\frame.fits");
            Assert.Equal(@"C:\Images\M56+92FlatWizard\frame.fits", DeepSkyLogWatcher.GetImageFilePath(uri));
        }

        [Fact]
        public void SpacesInPath_Preserved() {
            var uri = new Uri(@"C:\Astro\Sh2-155 Cave\frame.fits");
            Assert.Equal(@"C:\Astro\Sh2-155 Cave\frame.fits", DeepSkyLogWatcher.GetImageFilePath(uri));
        }

        [Fact]
        public void UncPath_HostAndSharePreserved() {
            var uri = new Uri(@"\\nas\share\Images\frame.fits");
            Assert.Equal(@"\\nas\share\Images\frame.fits", DeepSkyLogWatcher.GetImageFilePath(uri));
        }

        [Fact]
        public void PlainLocalPath_RoundTrips() {
            var uri = new Uri(@"C:\Images\M42\frame.fits");
            Assert.Equal(@"C:\Images\M42\frame.fits", DeepSkyLogWatcher.GetImageFilePath(uri));
        }
    }

    public class FallbackChecksumTests {

        [Fact]
        public void SameInputs_ProduceSameHash() {
            var dt = new DateTime(2026, 7, 28, 22, 30, 0, DateTimeKind.Utc);
            var a = DeepSkyLogWatcher.FallbackChecksum(@"C:\Images\M56+92\frame.fits", dt);
            var b = DeepSkyLogWatcher.FallbackChecksum(@"C:\Images\M56+92\frame.fits", dt);
            Assert.Equal(a, b);
        }

        [Fact]
        public void DifferentPath_ProducesDifferentHash() {
            var dt = new DateTime(2026, 7, 28, 22, 30, 0, DateTimeKind.Utc);
            var a = DeepSkyLogWatcher.FallbackChecksum(@"C:\Images\frame1.fits", dt);
            var b = DeepSkyLogWatcher.FallbackChecksum(@"C:\Images\frame2.fits", dt);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void DifferentTime_ProducesDifferentHash() {
            var dt1 = new DateTime(2026, 7, 28, 22, 30, 0, DateTimeKind.Utc);
            var dt2 = new DateTime(2026, 7, 28, 22, 31, 0, DateTimeKind.Utc);
            var a = DeepSkyLogWatcher.FallbackChecksum(@"C:\Images\frame.fits", dt1);
            var b = DeepSkyLogWatcher.FallbackChecksum(@"C:\Images\frame.fits", dt2);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Result_StartsWithNocksPrefix() {
            var result = DeepSkyLogWatcher.FallbackChecksum(@"C:\Images\frame.fits",
                new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc));
            Assert.StartsWith("nocks-", result);
        }

        [Fact]
        public void NullPath_DoesNotThrow() {
            var result = DeepSkyLogWatcher.FallbackChecksum(null,
                new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc));
            Assert.StartsWith("nocks-", result);
        }
    }

    public class CalculateFileChecksumTests {

        [Fact]
        public void MissingFile_ReturnsNull() {
            var result = DeepSkyLogWatcher.CalculateFileChecksum(@"C:\DoesNotExist\frame.fits");
            Assert.Null(result);
        }

        [Fact]
        public void EmptyFile_ReturnsNull() {
            var path = Path.GetTempFileName();
            try {
                var result = DeepSkyLogWatcher.CalculateFileChecksum(path);
                Assert.Null(result);
            } finally {
                File.Delete(path);
            }
        }

        [Fact]
        public void ValidFile_ReturnsLowercaseHex64() {
            var path = Path.GetTempFileName();
            try {
                File.WriteAllBytes(path, new byte[1024]);
                var result = DeepSkyLogWatcher.CalculateFileChecksum(path);
                Assert.NotNull(result);
                Assert.Matches("^[0-9a-f]{64}$", result);
            } finally {
                File.Delete(path);
            }
        }

        [Fact]
        public void SameFile_ProducesSameHash() {
            var path = Path.GetTempFileName();
            try {
                File.WriteAllBytes(path, Enumerable.Range(0, 512).Select(i => (byte)i).ToArray());
                var a = DeepSkyLogWatcher.CalculateFileChecksum(path);
                var b = DeepSkyLogWatcher.CalculateFileChecksum(path);
                Assert.Equal(a, b);
            } finally {
                File.Delete(path);
            }
        }

        [Fact]
        public void OnlyFirst50KbHashed_LargerFileMatchesTruncated() {
            var path = Path.GetTempFileName();
            try {
                // Write 100 KB: first 50 KB = 0xAA, second 50 KB = 0xBB
                byte[] first = new byte[50 * 1024];
                byte[] second = new byte[50 * 1024];
                Array.Fill(first, (byte)0xAA);
                Array.Fill(second, (byte)0xBB);
                File.WriteAllBytes(path, first.Concat(second).ToArray());

                var hashFull = DeepSkyLogWatcher.CalculateFileChecksum(path);

                // Write only the first 50 KB to a second temp file
                var path2 = Path.GetTempFileName();
                try {
                    File.WriteAllBytes(path2, first);
                    var hashTruncated = DeepSkyLogWatcher.CalculateFileChecksum(path2);
                    Assert.Equal(hashFull, hashTruncated);
                } finally {
                    File.Delete(path2);
                }
            } finally {
                File.Delete(path);
            }
        }
    }

    public class ReformatRATests {

        private static DeepSkyLogWatcher.AcquisitionMetaDataRecord Record() =>
            new DeepSkyLogWatcher.AcquisitionMetaDataRecord();

        [Theory]
        [InlineData("05:34:32", "5h 34m 32s")]
        [InlineData("00:00:00", "0h 0m 0s")]
        [InlineData("23:59:59", "23h 59m 59s")]
        [InlineData("01:02:03", "1h 2m 3s")]
        public void ValidHHmmss_FormatsCorrectly(string input, string expected) {
            Assert.Equal(expected, Record().ReformatRA(input));
        }

        [Fact]
        public void NonMatchingString_ReturnedAsIs() {
            Assert.Equal("invalid", Record().ReformatRA("invalid"));
        }

        [Fact]
        public void NullInput_ReturnsEmpty() {
            Assert.Equal("", Record().ReformatRA(null));
        }
    }
}
