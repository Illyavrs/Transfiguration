namespace Illyaverse.Transfiguration;

// A connectivity retry belongs to the same login. Only the explicit sign-out
// action creates a new epoch; old scheduled callbacks cannot welcome that epoch.
internal sealed class SessionWelcomeState(
    Func<bool> verifiedOnline,
    Func<bool> notificationReady,
    Action<Action> schedule,
    Action<Action> defer,
    Action post)
{
    private readonly object gate = new();
    private long epoch;
    private bool pending;
    private bool delivered;

    internal void Observe()
    {
        long current;
        lock (gate)
        {
            try
            {
                if (delivered || pending || !verifiedOnline()) return;
                pending = true;
                current = epoch;
            }
            catch { return; }
        }
        try { schedule(() => attempt(current)); }
        catch { abandon(current); }
    }

    internal void ExplicitLogout()
    {
        lock (gate)
        {
            epoch++;
            pending = false;
            delivered = false;
        }
    }

    private void attempt(long current)
    {
        var retry = false;
        lock (gate)
        {
            if (current != epoch || delivered || !pending) return;
            try
            {
                if (!verifiedOnline()) { pending = false; return; }
                if (!notificationReady()) retry = true;
                else
                {
                    // Mark before invoking an optional UI sink. If it throws,
                    // reconnect must not repeatedly attempt the same notification.
                    pending = false;
                    delivered = true;
                    post();
                }
            }
            catch { pending = false; }
        }
        if (!retry) return;
        try { defer(() => attempt(current)); }
        catch { abandon(current); }
    }

    private void abandon(long current)
    {
        lock (gate)
            if (current == epoch) pending = false;
    }
}
