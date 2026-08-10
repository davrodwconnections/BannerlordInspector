using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace BannerlordInspector
{
    /// <summary>
    /// Catches exceptions as they are thrown, groups them, and blames an assembly for each one.
    ///
    /// Every other route in this inspector answers "what is the state of the game right now". This
    /// one answers a different question, and the one that actually comes up while testing a fix:
    /// "I did the thing - did anything go wrong?"
    ///
    /// The reason it hooks <c>FirstChanceException</c> rather than reading a log is that the
    /// interesting failures in a heavily-modded install are the ones nobody logs. A mod wraps a
    /// campaign tick in try/catch, swallows the exception, and the symptom the player reports is
    /// "the feature does nothing" - no crash, no log line, nothing to grep for. FirstChance fires
    /// when the exception is THROWN, before any catch block gets a chance to hide it, so a
    /// swallowed exception is just as visible here as a fatal one.
    ///
    /// That power is also the hazard: this handler runs on whatever thread threw, inside the
    /// throw path, and the game throws exceptions during perfectly normal operation. So it is
    /// built to be cheap and to stay cheap under abuse:
    ///
    ///   - identical exceptions collapse into one group with a counter; the stack trace is
    ///     formatted ONCE, the first time a signature is seen, and never again
    ///   - a hard ceiling per second, after which occurrences are only counted, not examined
    ///   - a bounded number of distinct groups, oldest evicted
    ///   - re-entrancy guarded per thread, and the whole handler cannot throw
    ///
    /// Read-only, like everything else here: it observes exceptions, it never handles, swallows
    /// or alters them. The game behaves exactly as it would with this module absent.
    /// </summary>
    public static class ErrorCollector
    {
        /// <summary>Distinct exception signatures kept. Oldest-by-last-seen is evicted past this.</summary>
        private const int MaxGroups = 200;

        /// <summary>
        /// Occurrences examined per second. Past this we still count, but do not touch the
        /// exception object - a game that has started throwing in a tight loop must not be made
        /// slower by the thing that is supposed to be watching it.
        /// </summary>
        private const int MaxPerSecond = 200;

        private const int MessageCap = 300;
        private const int StackCap = 4000;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Group> Groups = new Dictionary<string, Group>(StringComparer.Ordinal);

        [ThreadStatic] private static bool _inHandler;

        private static bool _armed;
        private static long _totalSeen;
        private static long _dropped;
        private static long _fatal;
        private static long _selfSuppressed;
        private static int _windowCount;
        private static DateTime _windowStart = DateTime.UtcNow;
        private static DateTime _armedAt;

        private sealed class Group
        {
            public string Signature;
            public string Type;
            public string Message;
            public string Blame;
            public string Stack;
            public string Thread;
            public DateTime First;
            public DateTime Last;
            public long Count;
            public bool Fatal;
        }

        public static bool IsArmed => _armed;

        /// <summary>
        /// Start watching. Called at module load; safe to call twice.
        /// </summary>
        public static void Arm()
        {
            lock (Sync)
            {
                if (_armed) return;

                AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;

                _armed = true;
                _armedAt = DateTime.UtcNow;
            }

            InspectorLog.Info("Error collector armed (first-chance + unhandled).");
        }

        public static void Disarm()
        {
            lock (Sync)
            {
                if (!_armed) return;

                AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;

                _armed = false;
            }

            InspectorLog.Info("Error collector disarmed.");
        }

        /// <summary>
        /// Forget everything seen so far. The point of this is the test loop: clear, do the thing
        /// in game, ask again - and whatever comes back was caused by the thing.
        /// </summary>
        public static void Clear()
        {
            lock (Sync)
            {
                Groups.Clear();
                _totalSeen = 0;
                _dropped = 0;
                _selfSuppressed = 0;
                _fatal = 0;
                _windowCount = 0;
                _windowStart = DateTime.UtcNow;
            }
        }

        private static void OnFirstChance(object sender, FirstChanceExceptionEventArgs e)
        {
            Record(e?.Exception, false);
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            // The process is on its way out. Get it into the log too - the in-memory groups are
            // about to die with it, and this is the one exception nobody can afford to lose.
            Exception ex = e?.ExceptionObject as Exception;
            Record(ex, true);

            try
            {
                InspectorLog.Error("UNHANDLED - the game is going down.", ex);
            }
            catch
            {
                // Intentionally silent.
            }
        }

        private static void Record(Exception ex, bool fatal)
        {
            if (ex == null) return;

            // An exception thrown by this handler would be caught by this handler. Per-thread flag
            // rather than a lock, because the throw could be on any thread including one already
            // inside here.
            if (_inHandler) return;
            _inHandler = true;

            try
            {
                string type;
                string message;

                try
                {
                    type = ex.GetType().FullName;
                    message = Truncate(ex.Message, MessageCap);
                }
                catch
                {
                    return;
                }

                string signature = type + "|" + message;

                // --- our own noise, discarded before it can pass for a finding ------------
                //
                // The dispatcher throws a TimeoutException whenever the main thread has not ticked
                // in time, which is precisely what a save load looks like from outside. Polling
                // during a load therefore made this recorder report ITS OWN timeouts as game
                // faults: three of the first four findings in its first real session were
                // self-inflicted, and one of them said the game "appears HUNG" while it was busy
                // loading normally.
                //
                // A diagnostic that reports itself teaches people to skim its output, and that
                // costs far more than these entries are worth. Counted rather than dropped in
                // silence, so the suppression is visible.
                if (IsOurOwn(ex))
                {
                    lock (Sync) { _selfSuppressed++; }
                    return;
                }

                // --- cheap path: seen before, or over budget -----------------------------
                lock (Sync)
                {
                    _totalSeen++;
                    if (fatal) _fatal++;

                    DateTime now = DateTime.UtcNow;
                    if ((now - _windowStart).TotalSeconds >= 1.0)
                    {
                        _windowStart = now;
                        _windowCount = 0;
                    }

                    if (Groups.TryGetValue(signature, out Group existing))
                    {
                        existing.Count++;
                        existing.Last = now;
                        if (fatal) existing.Fatal = true;
                        return;
                    }

                    if (_windowCount >= MaxPerSecond)
                    {
                        // Over budget AND unseen. Counting it is honest; formatting it is not
                        // worth a frame.
                        _dropped++;
                        return;
                    }

                    _windowCount++;
                }

                // --- expensive path: first sighting of this signature --------------------
                // Deliberately outside the lock. Walking a stack trace is the slowest thing this
                // class does, and holding the lock through it would make every other thread that
                // throws wait on us.
                string stack = SafeStack(ex);
                string blame = Blame(ex);
                string thread = SafeThread();
                DateTime seenAt = DateTime.UtcNow;

                lock (Sync)
                {
                    // Another thread may have inserted the same signature while we were
                    // formatting. Its copy is just as good.
                    if (Groups.TryGetValue(signature, out Group raced))
                    {
                        raced.Count++;
                        raced.Last = seenAt;
                        if (fatal) raced.Fatal = true;
                        return;
                    }

                    if (Groups.Count >= MaxGroups) EvictOldest();

                    Groups[signature] = new Group
                    {
                        Signature = signature,
                        Type = type,
                        Message = message,
                        Blame = blame,
                        Stack = stack,
                        Thread = thread,
                        First = seenAt,
                        Last = seenAt,
                        Count = 1,
                        Fatal = fatal
                    };
                }
            }
            catch
            {
                // A diagnostic that can break the game is worse than no diagnostic.
            }
            finally
            {
                _inHandler = false;
            }
        }

        /// <summary>Caller must hold the lock.</summary>
        private static void EvictOldest()
        {
            string oldestKey = null;
            DateTime oldest = DateTime.MaxValue;

            foreach (var pair in Groups)
            {
                if (pair.Value.Last < oldest)
                {
                    oldest = pair.Value.Last;
                    oldestKey = pair.Key;
                }
            }

            if (oldestKey != null) Groups.Remove(oldestKey);
        }

        /// <summary>
        /// Which assembly is most likely at fault: the first frame that is neither the engine nor
        /// the framework. That is a heuristic, not a verdict - a mod can perfectly well trip over
        /// a bug in vanilla code - but "TAOM" beats "some exception happened" every time when the
        /// question is which mod to look at first.
        /// </summary>
        /// <summary>
        /// Whether this exception came out of the inspector itself.
        ///
        /// Matched on the declaring type rather than the message, because messages are written for
        /// people and get reworded. The dispatcher's timeout is the one that matters - it fires
        /// every time the main thread is busy, which includes every save load.
        /// </summary>
        private static bool IsOurOwn(Exception ex)
        {
            try
            {
                if (ex is TimeoutException
                    && ex.TargetSite?.DeclaringType?.Namespace == "BannerlordInspector") return true;

                Type declaring = ex.TargetSite?.DeclaringType;
                return declaring?.Assembly == typeof(ErrorCollector).Assembly;
            }
            catch
            {
                return false;
            }
        }

        private static string Blame(Exception ex)
        {
            try
            {
                var trace = new StackTrace(ex, false);
                StackFrame[] frames = trace.GetFrames();
                if (frames == null) return "unknown";

                string firstAny = null;

                foreach (StackFrame frame in frames)
                {
                    MethodBase method = frame.GetMethod();
                    Assembly assembly = method?.DeclaringType?.Assembly;
                    if (assembly == null) continue;

                    string name;
                    try { name = assembly.GetName().Name; }
                    catch { continue; }

                    if (firstAny == null) firstAny = name;
                    if (IsPlatform(name)) continue;

                    return name;
                }

                return firstAny ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private static bool IsPlatform(string assembly)
        {
            return assembly.StartsWith("TaleWorlds", StringComparison.Ordinal)
                   || assembly.StartsWith("System", StringComparison.Ordinal)
                   || assembly.StartsWith("Microsoft", StringComparison.Ordinal)
                   || assembly.Equals("mscorlib", StringComparison.Ordinal)
                   || assembly.Equals("netstandard", StringComparison.Ordinal)
                   || assembly.Equals("0Harmony", StringComparison.Ordinal)
                   || assembly.Equals("BannerlordInspector", StringComparison.Ordinal);
        }

        /// <summary>
        /// The stack, falling back to the calling thread's own when the exception has none yet.
        ///
        /// This is the price of catching exceptions at throw time. FirstChance fires BEFORE the
        /// exception propagates, so <c>ex.StackTrace</c> holds only the frame that threw - and when
        /// the thrower is deep inside the framework, that single frame names the framework and
        /// nobody else. An AmbiguousMatchException reported "at RuntimeType.GetMethodImpl" and
        /// nothing more: true, useless, and unattributable to any mod.
        ///
        /// Walking the live thread stack instead gives the chain that led there, which is where the
        /// culprit actually is. It is the expensive call in this class, so it happens only when the
        /// cheap answer came back too thin, and only once per distinct signature - the same budget
        /// that already governs formatting.
        /// </summary>
        private static string SafeStack(Exception ex)
        {
            string fromException;

            try { fromException = ex.StackTrace; }
            catch { return "(stack unavailable)"; }

            bool tooThin = string.IsNullOrEmpty(fromException)
                           || CountLines(fromException) < 3;

            if (!tooThin) return Truncate(fromException, StackCap);

            try
            {
                // Skip this method and Record(); the caller is what matters. fNeedFileInfo:false -
                // resolving source lines here would cost far more than the answer is worth.
                string fromThread = new StackTrace(2, false).ToString();

                if (string.IsNullOrEmpty(fromThread)) return Truncate(fromException ?? "(no stack)", StackCap);

                return Truncate(
                    (string.IsNullOrEmpty(fromException) ? "" : fromException.TrimEnd() + "\n")
                    + "--- thrown from (calling thread) ---\n" + fromThread,
                    StackCap);
            }
            catch
            {
                return Truncate(fromException ?? "(no stack)", StackCap);
            }
        }

        private static int CountLines(string text)
        {
            int n = 1;
            foreach (char c in text) if (c == '\n') n++;
            return n;
        }

        private static string SafeThread()
        {
            try
            {
                Thread current = Thread.CurrentThread;
                string name = string.IsNullOrEmpty(current.Name) ? "#" + current.ManagedThreadId : current.Name;
                return name;
            }
            catch
            {
                return "?";
            }
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= max ? value : value.Substring(0, max) + "...(truncated)";
        }

        /// <summary>
        /// Answers /errors. Deliberately does NOT go through the main-thread dispatcher: this is
        /// the route you want most when the game is hung or dying, which is exactly when the
        /// dispatcher stops answering.
        /// </summary>
        public static object Report(string blameFilter, string textFilter, int sinceSeconds, int limit, bool full)
        {
            List<Group> snapshot;
            long totalSeen, dropped, fatal, selfSuppressed;
            bool armed;
            DateTime armedAt;

            lock (Sync)
            {
                snapshot = Groups.Values.Select(Copy).ToList();
                totalSeen = _totalSeen;
                dropped = _dropped;
                selfSuppressed = _selfSuppressed;
                fatal = _fatal;
                armed = _armed;
                armedAt = _armedAt;
            }

            IEnumerable<Group> rows = snapshot;

            if (!string.IsNullOrEmpty(blameFilter))
            {
                rows = rows.Where(g => g.Blame != null
                                       && g.Blame.IndexOf(blameFilter, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (!string.IsNullOrEmpty(textFilter))
            {
                rows = rows.Where(g =>
                    (g.Type != null && g.Type.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (g.Message != null && g.Message.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            if (sinceSeconds > 0)
            {
                DateTime cutoff = DateTime.UtcNow.AddSeconds(-sinceSeconds);
                rows = rows.Where(g => g.Last >= cutoff);
            }

            List<Group> ordered = rows
                .OrderByDescending(g => g.Fatal)
                .ThenByDescending(g => g.Last)
                .ToList();

            int matched = ordered.Count;
            if (limit > 0) ordered = ordered.Take(limit).ToList();

            var groups = ordered.Select(g => (object)new
            {
                type = g.Type,
                message = g.Message,
                blame = g.Blame,
                fatal = g.Fatal,
                count = g.Count,
                firstSeen = Local(g.First),
                lastSeen = Local(g.Last),
                secondsAgo = (int)(DateTime.UtcNow - g.Last).TotalSeconds,
                thread = g.Thread,
                stack = full ? g.Stack : Truncate(g.Stack, 900)
            }).ToArray();

            return new
            {
                note = armed
                    ? "Exceptions thrown since the collector was armed, newest first. Identical ones "
                      + "are grouped with a count. 'blame' is the first non-engine assembly on the stack. "
                      + "Test loop: action=clear, do the thing in game, then read this again."
                    : "The collector is NOT armed - nothing is being recorded. Arm it with action=arm, "
                      + "or set collectErrors=true in config.txt.",
                armed,
                armedAt = armed ? Local(armedAt) : null,
                totalSeen,
                distinct = snapshot.Count,
                matched,
                shown = groups.Length,
                fatal,
                droppedOverBudget = dropped,

                // Our own timeouts, kept out of the findings. Reported so the filtering is
                // visible: a recorder that quietly hides things is its own kind of lie.
                selfSuppressed,
                groups
            };
        }

        /// <summary>A one-line answer for /health, so a caller knows to go and look.</summary>
        public static object Summary()
        {
            lock (Sync)
            {
                if (!_armed) return new { armed = false };

                Group newest = null;
                foreach (Group g in Groups.Values)
                {
                    if (newest == null || g.Last > newest.Last) newest = g;
                }

                return new
                {
                    armed = true,
                    totalSeen = _totalSeen,
                    distinct = Groups.Count,
                    fatal = _fatal,
                    newest = newest == null ? null : newest.Type + ": " + newest.Message,
                    newestBlame = newest?.Blame
                };
            }
        }

        private static Group Copy(Group g)
        {
            return new Group
            {
                Signature = g.Signature,
                Type = g.Type,
                Message = g.Message,
                Blame = g.Blame,
                Stack = g.Stack,
                Thread = g.Thread,
                First = g.First,
                Last = g.Last,
                Count = g.Count,
                Fatal = g.Fatal
            };
        }

        private static string Local(DateTime utc) => utc.ToLocalTime().ToString("HH:mm:ss");
    }
}
