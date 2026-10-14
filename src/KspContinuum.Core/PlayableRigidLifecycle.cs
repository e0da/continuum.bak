using System;
namespace KspContinuum
{
    public enum PlayableRigidAction { None, Acquire, Handback }
    // Pure operator policy. Physics and stock transitions remain host-owned.
    public sealed class PlayableRigidLifecycle
    {
        public bool Enabled { get; private set; }
        public bool Paused { get; private set; }
        public bool Blocked { get; private set; }
        public bool Owned { get; private set; }
        public bool Stopped { get; private set; }
        public long Epoch { get; private set; }
        public string Reason { get; private set; }
        public string State { get { return Stopped ? "stopped" : Blocked ? "faulted-stock" : Owned ? "rigid-owned" : !Enabled ? "stock-disabled" : Paused ? "stock-paused" : "waiting-stock"; } }
        string target;
        int revision;
        double eligibleAfter;
        readonly double settleSeconds;
        public PlayableRigidLifecycle(double settleSeconds = 1)
        { if (!Finite(settleSeconds) || settleSeconds < 0 || settleSeconds > 60) throw new ArgumentException("Invalid settling interval."); this.settleSeconds = settleSeconds; }
        public void ObserveTarget(string identity, int topologyRevision, double wall)
        {
            Clock(wall); if (Stopped) throw new InvalidOperationException("Stopped service cannot rebind."); if (identity != null && (identity.Length == 0 || identity.Length > 128)) throw new ArgumentException("Invalid target identity."); if (topologyRevision < 0) throw new ArgumentException("Invalid topology revision.");
            if (target == identity && revision == topologyRevision) return;
            if (Owned) throw new InvalidOperationException("Release motion ownership before rebinding.");
            long next = checked(Epoch + 1); target = identity; revision = topologyRevision; Epoch = next;
            Blocked = false; eligibleAfter = wall + settleSeconds; Reason = "target-changed";
        }
        public void SetEnabled(bool value, double wall)
        { Clock(wall); if (Stopped && value) throw new InvalidOperationException("Stopped service cannot restart."); if (Enabled == value) return; Enabled = value; if (value) eligibleAfter = wall + settleSeconds; Reason = value ? "operator-enabled" : "operator-disabled"; }
        public void SetPaused(bool value, double wall)
        { Clock(wall); if (Paused == value) return; Paused = value; if (!value) eligibleAfter = wall + settleSeconds; Reason = value ? "operator-paused" : "operator-resumed"; }
        public void Boundary(string reason, double wall)
        { Clock(wall); if (string.IsNullOrEmpty(reason)) throw new ArgumentException("Boundary reason required."); eligibleAfter = wall + settleSeconds; Reason = reason; }
        public void Retry(double wall)
        { Clock(wall); if (Stopped || Owned) throw new InvalidOperationException("Retry requires live stock state."); Blocked = false; eligibleAfter = wall + settleSeconds; Reason = "operator-retry"; }
        public PlayableRigidAction Evaluate(bool eligible, double wall)
        {
            Clock(wall); if (Owned) return Stopped || Blocked || !Enabled || Paused || !eligible ? PlayableRigidAction.Handback : PlayableRigidAction.None;
            return !Stopped && !Blocked && Enabled && !Paused && eligible && target != null && wall >= eligibleAfter ? PlayableRigidAction.Acquire : PlayableRigidAction.None;
        }
        public void Acquired() { if (Stopped || Blocked || !Enabled || Paused || Owned || target == null) throw new InvalidOperationException("Invalid ownership acquisition."); Owned = true; Reason = "acquired"; }
        public void Released(string reason, double wall) { Clock(wall); if (string.IsNullOrEmpty(reason)) throw new ArgumentException("Release reason required."); Owned = false; Boundary(reason, wall); }
        public void Fault(string reason) { if (string.IsNullOrEmpty(reason)) throw new ArgumentException("Fault reason required."); Blocked = true; Reason = reason; }
        public void Stop(double wall) { Clock(wall); Stopped = true; Enabled = false; Reason = "stopped"; }
        static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        static void Clock(double x) { if (!Finite(x) || x < 0) throw new ArgumentException("Invalid monotonic wall time."); }
    }
}
