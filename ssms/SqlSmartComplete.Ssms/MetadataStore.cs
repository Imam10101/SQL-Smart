using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SqlSmartComplete.Ssms
{
    /// <summary>Caches metadata per server, login and database, and refreshes it in the background.</summary>
    internal static class MetadataStore
    {
        private sealed class Entry
        {
            public Task<MetadataIndex> Task;
            public DateTime Started;
            public bool Refreshing;
        }

        private static readonly ConcurrentDictionary<string, Entry> Cache = new ConcurrentDictionary<string, Entry>();

        public static Task<MetadataIndex> GetAsync(ConnectionInfo c, string database)
        {
            string key = c.Key + "|" + database.ToLowerInvariant();
            var now = DateTime.UtcNow;

            if (Cache.TryGetValue(key, out var e))
            {
                if (!e.Task.IsCompleted) return e.Task;
                var idx = e.Task.Result;
                if (idx == null)
                {
                    if ((now - e.Started).TotalSeconds < 30) return e.Task; // recent failure: don't hammer the server
                }
                else
                {
                    if ((DateTime.Now - idx.Data.LoadedAt).TotalMinutes >= Settings.RefreshMinutes && !e.Refreshing)
                    {
                        e.Refreshing = true;
                        var refresh = Start(c, database);
                        refresh.ContinueWith(t =>
                        {
                            if (t.Result != null) Cache[key] = new Entry { Task = t, Started = DateTime.UtcNow };
                            else e.Refreshing = false;
                        }, TaskScheduler.Default);
                    }
                    return e.Task;
                }
            }

            var entry = new Entry { Task = Start(c, database), Started = now };
            Cache[key] = entry;
            return entry.Task;
        }

        /// <summary>Metadata if it is already loaded; never waits. Starts a load otherwise.</summary>
        public static MetadataIndex TryGetLoaded(ConnectionInfo c, string database)
        {
            var t = GetAsync(c, database);
            return t.Status == TaskStatus.RanToCompletion ? t.Result : null;
        }

        private static Task<MetadataIndex> Start(ConnectionInfo c, string database)
        {
            return Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var md = MetadataLoader.Load(c.ConnectionString(database), database);
                    Log.Write($"Loaded {c.Server}/{database}: {md.Objects.Count} objects, {md.ForeignKeys.Count} foreign keys in {sw.ElapsedMilliseconds} ms");
                    return new MetadataIndex(md, Settings.Engine.DefaultSchema);
                }
                catch (Exception ex)
                {
                    Log.Write($"Failed to load metadata for {c.Server}/{database}: {ex.Message}");
                    return null;
                }
            });
        }
    }
}
