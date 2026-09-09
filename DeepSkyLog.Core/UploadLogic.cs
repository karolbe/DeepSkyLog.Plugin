using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// Pure, cross-platform frame-upload logic. No NINA or WPF types — extracted so it can be
    /// unit-tested on Linux. The plugin delegates here for everything the unit tests exercise.
    /// </summary>
    public static class UploadLogic {

        // ImageTypes.LIGHT / ImageTypes.SNAPSHOT live in NINA.Core.Enum; the values are just the
        // string image-type names, so the core holds copies rather than referencing NINA.
        private const string LightImageType = "LIGHT";
        private const string SnapshotImageType = "SNAPSHOT";

        /// <summary>
        /// Use LocalPath, not UrlDecode(AbsolutePath): AbsolutePath keeps a leading slash and a
        /// '+' in the path (e.g. a target folder named "M56+92"), and UrlDecode then turns that
        /// '+' into a space, producing a path that doesn't exist on disk. LocalPath resolves the
        /// file:// URI to the correct Windows path directly.
        /// </summary>
        public static string GetImageFilePath(Uri imageUri) {
            return imageUri.LocalPath;
        }

        /// <summary>Deterministic fallback key when the file cannot be hashed (unreadable/not yet flushed).</summary>
        public static string FallbackChecksum(string filePath, DateTime exposureStart) {
            string seed = (filePath ?? string.Empty) + "|" + exposureStart.ToString("o");
            using (var sha256 = SHA256.Create()) {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(seed));
                return "nocks-" + Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
        }

        /// <summary>SHA-256 of the first 50 KB, lower-case hex. Null when the file is missing or empty.</summary>
        public static string CalculateFileChecksum(string filePath) {
            try {
                if (!File.Exists(filePath)) {
                    return null;
                }

                const int bufferSize = 50 * 1024; // 50KB
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha256 = SHA256.Create()) {
                    byte[] buffer = new byte[bufferSize];
                    int bytesRead = fileStream.Read(buffer, 0, bufferSize);

                    if (bytesRead == 0) {
                        return null;
                    }

                    // If we read less than 50KB, resize the buffer to the actual bytes read.
                    if (bytesRead < bufferSize) {
                        Array.Resize(ref buffer, bytesRead);
                    }

                    byte[] hashBytes = sha256.ComputeHash(buffer);
                    return Convert.ToHexString(hashBytes).ToLowerInvariant();
                }
            } catch {
                return null;
            }
        }

        /// <summary>
        /// Lights always upload; snapshots on their own switch; anything unclassified (null, unknown,
        /// calibration) follows the calibration switch. Commented-out type checks once let a night of
        /// flat-darks be filed under the previous night's target — pinned by tests.
        /// </summary>
        public static bool ShouldUploadImageType(string imageType, bool allowSnapshots, bool skipCalibrationFrames) {
            if (string.Equals(imageType, LightImageType, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
            if (string.Equals(imageType, SnapshotImageType, StringComparison.OrdinalIgnoreCase)) {
                return allowSnapshots;
            }
            return !skipCalibrationFrames;
        }

        public static string ReformatRA(string raString) {
            try {
                string pattern = @"(\d+):(\d+):(\d+)";
                if (Regex.IsMatch(raString, pattern)) {
                    Match match = Regex.Match(raString, pattern);
                    return $"{Zeros(match.Groups[1].Value)}h {Zeros(match.Groups[2].Value)}m {Zeros(match.Groups[3].Value)}s";
                } else {
                    return raString;
                }
            } catch {
                return "";
            }
        }

        public static string ReformatDEC(string decString) {
            return decString != null ? decString : "";
        }

        private static string Zeros(string value) {
            value = value.TrimStart('0');
            return (value == "") ? "0" : value;
        }
    }
}
