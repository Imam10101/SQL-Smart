using System;
using System.Collections.Generic;
using System.IO;

namespace SqlSmartComplete.Ssms
{
    /// <summary>
    /// User settings in %APPDATA%\SqlSmartComplete\settings.ini. The file is created with
    /// defaults on first use and re-read automatically when it changes.
    /// </summary>
    internal static class Settings
    {
        public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlSmartComplete");
        public static readonly string FilePath = Path.Combine(Folder, "settings.ini");

        private const string DefaultContent =
@"; SQL Smart Complete settings. Changes apply immediately, no restart needed.

; Turn the extension on or off.
enabled = true

; Add an alias when a table is picked after FROM, JOIN or APPLY: Customers c
alias.enabled = true
; Insert 'Customers AS c' instead of 'Customers c'.
alias.useAs = false
; lower gives c, upper gives C.
alias.case = lower

; Complete a fully typed or pasted table or column name (any case) when you press
; space, Tab, Enter, comma or ')' - the same as picking it from the list:
;   from customers<space>  ->  FROM Customers c
autoComplete = true

; Prefix columns with the alias: auto, always or never.
columns.qualify = auto

; Insert the schema in front of names: whenNotDefault or always.
schemaPrefix = whenNotDefault
defaultSchema = dbo

; upper or lower case for inserted keywords and functions.
keywordCase = upper

; Reload database metadata after this many minutes.
refreshMinutes = 5

; Used only when the SSMS connection cannot be read, for example with Microsoft Entra login.
; authentication can be windows or sql.
fallback.server =
fallback.database =
fallback.authentication = windows
fallback.user =
fallback.password =
";

        private static DateTime lastWrite = DateTime.MinValue;
        private static DateTime lastCheck = DateTime.MinValue;
        private static Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static EngineOptions engine = new EngineOptions();
        private static readonly object Gate = new object();

        private static void Refresh()
        {
            lock (Gate)
            {
                if ((DateTime.UtcNow - lastCheck).TotalSeconds < 2) return;
                lastCheck = DateTime.UtcNow;
                try
                {
                    if (!File.Exists(FilePath))
                    {
                        Directory.CreateDirectory(Folder);
                        File.WriteAllText(FilePath, DefaultContent);
                    }
                    var stamp = File.GetLastWriteTimeUtc(FilePath);
                    if (stamp == lastWrite) return;
                    lastWrite = stamp;

                    var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var raw in File.ReadAllLines(FilePath))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        v[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                    values = v;
                    engine = new EngineOptions
                    {
                        AliasEnabled = Bool("alias.enabled", true),
                        AliasUseAs = Bool("alias.useAs", false),
                        AliasUpper = Str("alias.case", "lower").Equals("upper", StringComparison.OrdinalIgnoreCase),
                        QualifyColumns = Str("columns.qualify", "auto").ToLowerInvariant(),
                        SchemaPrefix = Str("schemaPrefix", "whenNotDefault"),
                        DefaultSchema = Str("defaultSchema", "dbo"),
                        KeywordsLower = Str("keywordCase", "upper").Equals("lower", StringComparison.OrdinalIgnoreCase)
                    };
                }
                catch (Exception ex)
                {
                    Log.Write("Could not read settings: " + ex.Message);
                }
            }
        }

        public static string Str(string key, string def)
        {
            return values.TryGetValue(key, out var s) && s.Length > 0 ? s : def;
        }

        public static bool Bool(string key, bool def)
        {
            var s = Str(key, null);
            if (s == null) return def;
            return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        public static EngineOptions Engine { get { Refresh(); return engine; } }
        public static bool Enabled { get { Refresh(); return Bool("enabled", true); } }
        public static bool AutoComplete { get { Refresh(); return Bool("autoComplete", true); } }
        public static int RefreshMinutes { get { Refresh(); return int.TryParse(Str("refreshMinutes", "5"), out var m) && m > 0 ? m : 5; } }

        public static ConnectionInfo Fallback
        {
            get
            {
                Refresh();
                var server = Str("fallback.server", null);
                if (server == null) return null;
                bool sql = Str("fallback.authentication", "windows").Equals("sql", StringComparison.OrdinalIgnoreCase);
                return new ConnectionInfo
                {
                    Server = server,
                    Database = Str("fallback.database", null),
                    SqlAuth = sql,
                    User = Str("fallback.user", null),
                    Password = Str("fallback.password", null),
                    Source = "settings.ini"
                };
            }
        }
    }

    internal static class Log
    {
        private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlSmartComplete");
        public static readonly string FilePath = Path.Combine(Folder, "log.txt");
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never break the editor.
            }
        }
    }
}
