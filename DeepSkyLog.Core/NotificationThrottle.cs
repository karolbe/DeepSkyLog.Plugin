using System;
using System.Collections.Generic;

namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// Decides when a persistent problem is worth a NINA notification: once when it first appears,
    /// then a reminder at most every <c>reminderInterval</c> while it lasts.
    ///
    /// <para>Each kind of problem is tracked under its own key, so a telemetry refusal cannot
    /// swallow the notification for a rejected frame. A key is re-armed with <see cref="Reset"/>
    /// once the problem clears (an upload got through, the user signed in again), so the next
    /// occurrence is announced straight away rather than waiting out the reminder interval.</para>
    /// </summary>
    public class NotificationThrottle {
        private readonly TimeSpan reminderInterval;
        private readonly Func<DateTime> clock;
        private readonly Dictionary<string, DateTime> lastShownUtc = new(StringComparer.Ordinal);
        private readonly object gate = new();

        public NotificationThrottle(TimeSpan reminderInterval, Func<DateTime> clock = null) {
            this.reminderInterval = reminderInterval;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>True when a notification for <paramref name="key"/> should be shown now.</summary>
        public bool ShouldNotify(string key) {
            lock (gate) {
                DateTime now = clock();
                if (lastShownUtc.TryGetValue(key, out DateTime last) && now - last < reminderInterval) {
                    return false;
                }
                lastShownUtc[key] = now;
                return true;
            }
        }

        /// <summary>The problem behind <paramref name="key"/> has cleared; announce the next one at once.</summary>
        public void Reset(string key) {
            lock (gate) {
                lastShownUtc.Remove(key);
            }
        }
    }
}
