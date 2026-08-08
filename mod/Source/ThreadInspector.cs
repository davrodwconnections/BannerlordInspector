using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BannerlordInspector
{
    /// <summary>
    /// Looks at the process itself rather than the game, so it keeps working while the game is hung.
    ///
    /// The single most useful thing it answers is WHICH KIND of hang this is:
    ///
    ///   deadlock  the main thread is in a Wait state - blocked on a lock, a handle, something that
    ///             never gets released. CPU is idle. Killing another thread might free it.
    ///   spin      the main thread is Running and burning CPU - an infinite loop. CPU is pegged.
    ///
    /// They look identical from outside the window and have completely different causes, so telling
    /// them apart is most of the diagnosis. All of this comes from OS-level thread information,
    /// which is read-only and safe to take at any time.
    /// </summary>
    public static class ThreadInspector
    {
        public static object Snapshot()
        {
            try
            {
                Process process = Process.GetCurrentProcess();

                var threads = new List<ThreadRow>();
                double totalCpuSeconds = 0;

                foreach (ProcessThread thread in process.Threads)
                {
                    ThreadRow row;
                    try
                    {
                        double cpu = thread.TotalProcessorTime.TotalSeconds;
                        totalCpuSeconds += cpu;

                        row = new ThreadRow
                        {
                            id = thread.Id,
                            state = thread.ThreadState.ToString(),
                            // WaitReason only exists while the thread is actually waiting; reading
                            // it otherwise throws, which is why this is guarded.
                            waitReason = thread.ThreadState == System.Diagnostics.ThreadState.Wait
                                ? SafeWaitReason(thread)
                                : null,
                            cpuSeconds = Math.Round(cpu, 2),
                            priority = SafePriority(thread)
                        };
                    }
                    catch
                    {
                        // Threads come and go while we enumerate; skip the ones that vanish.
                        continue;
                    }

                    threads.Add(row);
                }

                var busiest = threads.OrderByDescending(t => t.cpuSeconds).Take(8).ToArray();
                int running = threads.Count(t => t.state == "Running");
                int waiting = threads.Count(t => t.state == "Wait");

                return new
                {
                    processId = process.Id,
                    threadCount = threads.Count,
                    running,
                    waiting,
                    totalCpuSeconds = Math.Round(totalCpuSeconds, 1),
                    gameLooksHung = Heartbeat.LooksHung,
                    msSinceLastTick = Heartbeat.MillisecondsSinceLastTick,
                    interpretation = Interpret(running, waiting),
                    busiestThreads = busiest
                };
            }
            catch (Exception ex)
            {
                return new { error = "could not read thread information: " + ex.Message };
            }
        }

        /// <summary>
        /// Turns the numbers into the sentence that actually helps. Deliberately hedged: thread
        /// states are a snapshot and a spinning thread can be legitimate work.
        /// </summary>
        private static string Interpret(int running, int waiting)
        {
            if (!Heartbeat.LooksHung)
            {
                return "The game is ticking normally - nothing here needs interpreting.";
            }

            if (running == 0)
            {
                return "The game is not ticking and NO thread is running: this looks like a deadlock. "
                       + "Something is waiting on a lock or handle that is never released. Compare the "
                       + "last breadcrumb with what was on screen.";
            }

            return "The game is not ticking but " + running + " thread(s) are still running: this looks "
                   + "like an infinite loop rather than a deadlock. The busiest thread below is the "
                   + "likeliest offender.";
        }

        private static string SafeWaitReason(ProcessThread thread)
        {
            try { return thread.WaitReason.ToString(); }
            catch { return null; }
        }

        private static string SafePriority(ProcessThread thread)
        {
            try { return thread.CurrentPriority.ToString(); }
            catch { return null; }
        }

        private sealed class ThreadRow
        {
            public int id { get; set; }
            public string state { get; set; }
            public string waitReason { get; set; }
            public double cpuSeconds { get; set; }
            public string priority { get; set; }
        }
    }
}
