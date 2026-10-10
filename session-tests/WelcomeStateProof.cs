using System.Collections.Concurrent;
using Illyaverse.Transfiguration;

internal static class WelcomeStateProof
{
    private static int checks;

    internal static int Run()
    {
        try
        {
            var initial = new Fixture();
            initial.State.Observe();
            require(initial.Scheduled.IsEmpty && initial.Posts == 0, "offline, unverified login emits no welcome");
            initial.Online = true;
            foreach (var id in new[] { -1, 0, 1 })
            {
                initial.UserId = id;
                initial.State.Observe();
                require(initial.Scheduled.IsEmpty, "guest, placeholder and deleted IDs do not satisfy verified login");
            }
            initial.UserId = 2;
            initial.State.Observe();
            initial.State.Observe();
            require(initial.Scheduled.Count == 1 && initial.Posts == 0, "online transition queues one update-thread notification");
            initial.Online = false;
            initial.FlushScheduled();
            require(initial.Posts == 0, "disconnect before the update callback prevents a welcome");
            initial.Online = true;
            initial.State.Observe();
            initial.FlushScheduled();
            require(initial.Posts == 1, "first verified online callback posts once");
            for (var retry = 0; retry < 8; retry++)
            {
                initial.Online = false;
                initial.State.Observe();
                initial.Online = true;
                initial.State.Observe();
            }
            initial.FlushScheduled();
            require(initial.Posts == 1 && initial.Deferred.IsEmpty, "refresh, retry and network logout transitions do not announce again");
            initial.Online = false;
            initial.State.ExplicitLogout();
            initial.State.Observe();
            require(initial.Scheduled.IsEmpty, "explicit sign-out alone does not announce a login");
            initial.Online = true;
            initial.State.Observe();
            initial.FlushScheduled();
            require(initial.Posts == 2, "verified login after explicit sign-out receives a new welcome");

            var stale = new Fixture { Online = true };
            stale.State.Observe();
            stale.State.ExplicitLogout();
            stale.Online = false;
            stale.FlushScheduled();
            require(stale.Posts == 0 && stale.Deferred.IsEmpty, "queued old epoch is discarded after sign-out");
            stale.Online = true;
            stale.State.Observe();
            stale.State.ExplicitLogout();
            stale.State.Observe();
            require(stale.Scheduled.Count == 2, "new epoch can queue while an old callback remains scheduled");
            stale.FlushScheduled();
            require(stale.Posts == 1, "old and new epoch callbacks produce only the new login welcome");

            var loading = new Fixture { Online = true, Ready = false };
            loading.State.Observe();
            loading.FlushScheduled();
            require(loading.Posts == 0 && loading.Deferred.Count == 1, "remembered login waits for stock notification wiring");
            loading.State.Observe();
            require(loading.Scheduled.IsEmpty && loading.Deferred.Count == 1, "pending UI readiness cannot queue duplicate notifications");
            loading.FlushOneDeferred();
            require(loading.Posts == 0 && loading.Deferred.Count == 1, "readiness retry schedules one later attempt without a tight loop");
            loading.Ready = true;
            loading.FlushOneDeferred();
            require(loading.Posts == 1 && loading.Deferred.IsEmpty, "ready stock overlay receives the deferred welcome exactly once");
            loading.State.Observe();
            require(loading.Scheduled.IsEmpty, "successful deferred welcome remains delivered");

            var logoutWhileLoading = new Fixture { Online = true, Ready = false };
            logoutWhileLoading.State.Observe();
            logoutWhileLoading.FlushScheduled();
            logoutWhileLoading.State.ExplicitLogout();
            logoutWhileLoading.Ready = true;
            logoutWhileLoading.FlushOneDeferred();
            require(logoutWhileLoading.Posts == 0 && logoutWhileLoading.Deferred.IsEmpty, "old deferred welcome cannot survive explicit logout");
            logoutWhileLoading.State.Observe();
            logoutWhileLoading.FlushScheduled();
            require(logoutWhileLoading.Posts == 1, "new login is independent of abandoned readiness callbacks");

            var outageWhileLoading = new Fixture { Online = true, Ready = false };
            outageWhileLoading.State.Observe();
            outageWhileLoading.FlushScheduled();
            outageWhileLoading.Online = false;
            outageWhileLoading.FlushOneDeferred();
            require(outageWhileLoading.Posts == 0 && outageWhileLoading.Deferred.IsEmpty, "outage suspends an undelivered welcome without retrying offline");
            outageWhileLoading.Online = true;
            outageWhileLoading.Ready = true;
            outageWhileLoading.State.Observe();
            outageWhileLoading.FlushScheduled();
            require(outageWhileLoading.Posts == 1, "an undelivered login can finish after connectivity recovers");

            var failingSink = new Fixture { Online = true, ThrowPost = true };
            failingSink.State.Observe();
            failingSink.FlushScheduled();
            failingSink.State.Observe();
            require(failingSink.Posts == 1 && failingSink.Scheduled.IsEmpty, "optional UI failure does not escape or repeatedly notify");
            var failingScheduler = new Fixture { Online = true, ThrowSchedule = true };
            failingScheduler.State.Observe();
            require(failingScheduler.Posts == 0 && failingScheduler.Scheduled.IsEmpty, "disposed scheduler does not interrupt login");
            failingScheduler.ThrowSchedule = false;
            failingScheduler.State.Observe();
            failingScheduler.FlushScheduled();
            require(failingScheduler.Posts == 1, "failed dispatch releases pending state for a later observation");
            var failingDefer = new Fixture { Online = true, Ready = false, ThrowDefer = true };
            failingDefer.State.Observe();
            failingDefer.FlushScheduled();
            require(failingDefer.Posts == 0 && failingDefer.Deferred.IsEmpty, "failed delay queue does not interrupt login");
            failingDefer.Ready = true;
            failingDefer.State.Observe();
            failingDefer.FlushScheduled();
            require(failingDefer.Posts == 1, "failed readiness dispatch permits a later ready observation");

            var concurrent = new Fixture { Online = true };
            Parallel.For(0, 200, _ => concurrent.State.Observe());
            require(concurrent.Scheduled.Count == 1, "concurrent observations queue one login callback");
            concurrent.FlushScheduled();
            require(concurrent.Posts == 1, "concurrent observations still post once");
            var independent = new Fixture { Online = true };
            independent.State.Observe();
            independent.FlushScheduled();
            require(independent.Posts == 1 && concurrent.Posts == 1, "provider instances do not share welcome state");
            Console.WriteLine($"PASS {checks} source-linked welcome state checks; synthetic callbacks only, no game, auth, player data, timers or network");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL welcome_state_fixture " + exception.GetType().Name);
            return 1;
        }
    }

    private sealed class Fixture
    {
        internal bool Online;
        internal int UserId = 2;
        internal bool Ready = true;
        internal bool ThrowPost, ThrowSchedule, ThrowDefer;
        internal int Posts;
        internal readonly ConcurrentQueue<Action> Scheduled = new();
        internal readonly ConcurrentQueue<Action> Deferred = new();
        internal readonly SessionWelcomeState State;

        internal Fixture()
        {
            State = new SessionWelcomeState(() => Online && UserId > 1, () => Ready,
                callback => { if (ThrowSchedule) throw new InvalidOperationException(); Scheduled.Enqueue(callback); },
                callback => { if (ThrowDefer) throw new InvalidOperationException(); Deferred.Enqueue(callback); },
                () => { Posts++; if (ThrowPost) throw new InvalidOperationException(); });
        }

        internal void FlushScheduled()
        {
            while (Scheduled.TryDequeue(out var callback)) callback();
        }

        internal void FlushOneDeferred()
        {
            if (!Deferred.TryDequeue(out var callback)) throw new InvalidOperationException();
            callback();
        }
    }

    private static void require(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("Welcome state fixture failed");
        checks++;
        Console.WriteLine("PASS " + name);
    }
}
