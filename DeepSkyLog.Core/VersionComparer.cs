using System;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// Orders dotted version numbers. Extracted so the update-check comparison can be unit-tested
    /// without a NINA/WPF plugin host.
    /// </summary>
    public static class VersionComparer {

        /// <summary>
        /// Orders dotted version numbers: negative if <paramref name="a"/> is older than
        /// <paramref name="b"/>, positive if newer, 0 if equal or indeterminate.
        /// </summary>
        /// <remarks>
        /// Missing trailing components count as zero, so the plugin's four-part "1.0.3.0" and a
        /// three-part "1.0.3" in a manifest compare equal. A component that is not a plain number
        /// makes the whole comparison indeterminate rather than "older" — a local build must not be
        /// nagged on every launch.
        /// </remarks>
        public static int Compare(string a, string b) {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) {
                return 0;
            }

            string[] left = a.Trim().Split('.');
            string[] right = b.Trim().Split('.');
            int parts = Math.Max(left.Length, right.Length);

            for (int i = 0; i < parts; i++) {
                if (!TryComponent(left, i, out int l) || !TryComponent(right, i, out int r)) {
                    return 0;
                }
                if (l != r) {
                    return l.CompareTo(r);
                }
            }

            return 0;
        }

        private static bool TryComponent(string[] parts, int index, out int value) {
            if (index >= parts.Length) {
                value = 0;
                return true;
            }
            return int.TryParse(parts[index].Trim(), out value) && value >= 0;
        }
    }
}
