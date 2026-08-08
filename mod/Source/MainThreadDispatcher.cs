using System;
using System.Collections.Concurrent;
using System.Threading;

namespace BannerlordInspector
{
    /// <summary>
    /// The single most important class here.
    ///
    /// Bannerlord's campaign objects are not thread-safe. The HTTP server answers on background
    /// threads, and touching Campaign.Current from one of those is not "usually fine" - it reads
    /// half-updated state at best and corrupts a save or hard-crashes the process at worst.
    ///
    /// So no request ever touches the game directly. A request enqueues a piece of work, blocks on
    /// an event, and the game's own tick - on the real main thread - runs it and signals back. The
    /// game is never made to wait on the network; the network waits on the game.
    ///
    /// If a tick never comes (game frozen, loading screen, exiting), the waiter times out and the
    /// request fails cleanly rather than hanging forever.
    /// </summary>
    public static class MainThreadDispatcher
    {
        private sealed class WorkItem
        {
            public Func<object> Work;
            public string Label;
            public object Result;
            public Exception Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static readonly ConcurrentQueue<WorkItem> Queue = new ConcurrentQueue<WorkItem>();

        /// <summary>Bounded so a burst of requests can never stall a frame indefinitely.</summary>
        private const int MaxItemsPerTick = 8;

        private static long _served;
        private static long _timedOut;

        public static long Served => Interlocked.Read(ref _served);
        public static long TimedOut => Interlocked.Read(ref _timedOut);
        public static int Pending => Queue.Count;

        /// <summary>
        /// Called from a request thread. Blocks until the main thread has run <paramref name="work"/>.
        /// Throws on timeout or if the work threw.
        /// </summary>
        public static object Run(Func<object> work, int timeoutMs, string label = null)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            var item = new WorkItem { Work = work, Label = label };
            Queue.Enqueue(item);

            if (!item.Done.Wait(timeoutMs))
            {
                Interlocked.Increment(ref _timedOut);

                // The timeout message is where a hang is first noticed, so say what is known about
                // it here rather than making the user go and ask a second question.
                long since = Heartbeat.MillisecondsSinceLastTick;
                string diagnosis = Heartbeat.LooksHung
                    ? $"The game has not ticked for {since} ms - it appears HUNG, not merely busy. "
                      + $"Last thing it was doing: '{Heartbeat.LastPhase}' ({Heartbeat.LastContext}). "
                      + "Ask /hang or /threads - those answer without waiting for the game."
                    : Heartbeat.State == "loading" || Heartbeat.State == "starting"
                        ? $"The game is on a loading screen ({since} ms without a tick, no campaign "
                          + "up). That is normal on a heavy modlist - ask again once you are on the map."
                        : "The game is still ticking, so this request was probably just queued behind "
                          + "slower work.";

                throw new TimeoutException(
                    $"The game did not run this within {timeoutMs} ms. " + diagnosis);
            }

            if (item.Error != null)
            {
                throw new InvalidOperationException(item.Error.Message, item.Error);
            }

            Interlocked.Increment(ref _served);
            return item.Result;
        }

        /// <summary>
        /// Called every frame from the main thread. Runs queued work and signals the waiters.
        /// Never throws - a failing request must not be able to take a frame down with it.
        /// </summary>
        public static void Pump()
        {
            int processed = 0;

            while (processed++ < MaxItemsPerTick && Queue.TryDequeue(out WorkItem item))
            {
                // Leave a breadcrumb BEFORE running. If the work never returns because the game
                // hangs inside it, this is the last thing the trail will show - which is precisely
                // how a hang gets attributed to something.
                Heartbeat.Mark("request: " + (item.Label ?? "unlabelled"));

                try
                {
                    item.Result = item.Work();
                }
                catch (Exception ex)
                {
                    item.Error = ex;
                }
                finally
                {
                    // Must always fire, or the requesting thread blocks until its timeout.
                    item.Done.Set();
                }

                Heartbeat.Mark("done: " + (item.Label ?? "unlabelled"));
            }
        }

        /// <summary>Release anything still waiting, so shutdown does not leave threads blocked.</summary>
        public static void DrainAndFail(string reason)
        {
            while (Queue.TryDequeue(out WorkItem item))
            {
                item.Error = new InvalidOperationException(reason);
                item.Done.Set();
            }
        }
    }
}
