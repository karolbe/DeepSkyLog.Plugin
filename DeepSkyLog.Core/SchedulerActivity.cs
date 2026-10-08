namespace DeepSkyLog.NINAPlugin {

    /// <summary>
    /// What Target Scheduler is doing, as far as its pub/sub messages tell: imaging a target, or
    /// waiting for the next one.
    ///
    /// <para>The server keeps a null field at its previous value, so a target that is no longer
    /// relevant cannot be cleared by nulling it. <see cref="SessionState.SchedulerStatus"/> says which
    /// names apply instead: the current target while IMAGING, the next one while WAITING, neither
    /// once STOPPED. For a rig without Target Scheduler no message ever arrives and the status
    /// stays null, so nothing here is reported at all.</para>
    /// </summary>
    public static class SchedulerActivity {

        public const string Imaging = "IMAGING";
        public const string Waiting = "WAITING";
        public const string Stopped = "STOPPED";

        public static void TargetStarted(SessionState state, string project, string target) {
            state.SchedulerStatus = Imaging;
            state.SchedulerProject = project;
            state.SchedulerTarget = target;
        }

        public static void WaitingFor(SessionState state, string project, string target) {
            state.SchedulerStatus = Waiting;
            state.SchedulerNextProject = project;
            state.SchedulerNextTarget = target;
        }

        /// <summary>
        /// The scheduler's container ended, or the whole sequence did. A rig that never heard from
        /// the scheduler stays at null rather than suddenly reporting it as stopped.
        /// </summary>
        public static void SchedulerStopped(SessionState state) {
            if (state.SchedulerStatus != null) {
                state.SchedulerStatus = Stopped;
            }
        }
    }
}
