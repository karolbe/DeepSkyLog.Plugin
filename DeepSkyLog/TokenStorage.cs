using System;
using System.Security.Cryptography;
using System.Text;
using DeepSkyLog.NINAPlugin.Properties;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// Per-user encrypted storage for the DeepSkyLog API token.
    /// Wraps Properties.Settings.DeepSkyLogKey with DPAPI so the token isn't sitting
    /// in the plaintext NINA settings XML where any same-user process can read it.
    ///
    /// Marker prefix DPAPI: distinguishes encrypted blobs from any legacy plaintext
    /// values; on Load() a missing marker triggers a one-time migrate-and-resave.
    /// </summary>
    internal static class TokenStorage {
        private const string EncryptedPrefix = "DPAPI:";
        private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("DeepSkyLog.NINAPlugin");

        public static string Load() {
            string stored = Settings.Default.DeepSkyLogKey;
            if (string.IsNullOrEmpty(stored)) {
                return string.Empty;
            }
            if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal)) {
                // Legacy plaintext value — re-save it encrypted so the next read is safe.
                Save(stored);
                return stored;
            }
            try {
                byte[] cipher = Convert.FromBase64String(stored.Substring(EncryptedPrefix.Length));
                byte[] plain = ProtectedData.Unprotect(cipher, OptionalEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch {
                // Corrupted or written by a different user — treat as no token.
                return string.Empty;
            }
        }

        public static void Save(string token) {
            if (string.IsNullOrEmpty(token)) {
                Clear();
                return;
            }
            byte[] plain = Encoding.UTF8.GetBytes(token);
            byte[] cipher = ProtectedData.Protect(plain, OptionalEntropy, DataProtectionScope.CurrentUser);
            Settings.Default.DeepSkyLogKey = EncryptedPrefix + Convert.ToBase64String(cipher);
            Settings.Default.Save();
        }

        public static void Clear() {
            Settings.Default.DeepSkyLogKey = string.Empty;
            Settings.Default.Save();
        }
    }
}
