using System;
using System.Collections.Specialized;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SqlSmartComplete.Ssms
{
    internal sealed class ConnectionInfo
    {
        public string Server;
        public string Database;
        public bool SqlAuth;
        public string User;
        public string Password;
        public string Source;

        public string Key => $"{Server}|{(SqlAuth ? "sql:" + User : "windows")}".ToLowerInvariant();

        public string ConnectionString(string database)
        {
            var b = new SqlConnectionStringBuilder
            {
                DataSource = Server,
                InitialCatalog = string.IsNullOrEmpty(database) ? "master" : database,
                ApplicationName = "SQL Smart Complete",
                ConnectTimeout = 15,
                TrustServerCertificate = true,
                Pooling = true
            };
            if (SqlAuth)
            {
                b.UserID = User ?? "";
                b.Password = Password ?? "";
            }
            else
            {
                b.IntegratedSecurity = true;
            }
            return b.ConnectionString;
        }

        public override string ToString() => $"{Server} / {Database ?? "(no database)"} ({(SqlAuth ? "SQL login " + User : "Windows")}, from {Source})";
    }

    /// <summary>
    /// Reads the connection of the active SSMS query window through SSMS's own
    /// ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo, using reflection so the
    /// extension does not bind to a specific SSMS build.
    /// </summary>
    internal static class SsmsConnection
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type serviceCacheType;
        private static string lastLogged;

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var p = t.GetProperty(name, All);
            if (p == null)
            {
                foreach (var i in t.GetInterfaces())
                {
                    p = i.GetProperty(name);
                    if (p != null) break;
                }
            }
            return p?.GetValue(obj, null);
        }

        private static Type ServiceCacheType()
        {
            if (serviceCacheType != null) return serviceCacheType;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = asm.GetName().Name;
                if (n != "SqlPackageBase" && n != "SqlWorkbench.Interfaces") continue;
                var t = asm.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache", false);
                if (t != null) return serviceCacheType = t;
            }
            return null;
        }

        private static string SecureToString(SecureString s)
        {
            if (s == null) return null;
            IntPtr p = IntPtr.Zero;
            try
            {
                p = Marshal.SecureStringToGlobalAllocUnicode(s);
                return Marshal.PtrToStringUni(p);
            }
            finally
            {
                if (p != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(p);
            }
        }

        /// <summary>The database selected in the toolbar of the active query window.</summary>
        private static string ActiveWindowDatabase()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var monitor = Package.GetGlobalService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
                if (monitor == null) return null;
                // SEID_WindowFrame = 1, VSFPROPID_DocView = -3001
                if (monitor.GetCurrentElementValue(1, out object frameObj) != 0) return null;
                if (!(frameObj is IVsWindowFrame frame)) return null;
                if (frame.GetProperty(-3001, out object docView) != 0 || docView == null) return null;
                return GetProp(docView, "CurrentDB") as string;
            }
            catch (Exception ex)
            {
                Log.Write("Could not read current database: " + ex.Message);
                return null;
            }
        }

        /// <summary>Must be called on the UI thread.</summary>
        public static ConnectionInfo TryGetActive()
        {
            try
            {
                var sc = ServiceCacheType();
                var factory = sc?.GetProperty("ScriptFactory", All)?.GetValue(null, null);
                var wnd = GetProp(factory, "CurrentlyActiveWndConnectionInfo");
                var ui = GetProp(wnd, "UIConnectionInfo");
                if (ui == null) return Fallback("no active SSMS connection");

                string server = GetProp(ui, "ServerName") as string;
                if (string.IsNullOrEmpty(server)) return Fallback("SSMS connection has no server name");
                int auth = Convert.ToInt32(GetProp(ui, "AuthenticationType") ?? 0);
                var adv = GetProp(ui, "AdvancedOptions") as NameValueCollection;

                var info = new ConnectionInfo { Server = server, Source = "SSMS" };
                if (auth == 0)
                {
                    info.SqlAuth = false;
                }
                else if (auth == 1)
                {
                    info.SqlAuth = true;
                    info.User = GetProp(ui, "UserName") as string;
                    string pwd = null;
                    try { pwd = GetProp(ui, "Password") as string; } catch { /* not persisted */ }
                    if (string.IsNullOrEmpty(pwd))
                    {
                        try { pwd = SecureToString(GetProp(ui, "InMemoryPassword") as SecureString); } catch { /* ignore */ }
                    }
                    info.Password = pwd;
                }
                else
                {
                    return Fallback($"SSMS authentication type {auth} (Microsoft Entra) is not supported");
                }

                info.Database = ActiveWindowDatabase() ?? adv?["DATABASE"];
                if (string.IsNullOrEmpty(info.Database)) info.Database = null;

                var s = info.ToString();
                if (s != lastLogged) { Log.Write("Active connection: " + s); lastLogged = s; }
                return info;
            }
            catch (Exception ex)
            {
                return Fallback("error reading SSMS connection: " + (ex.InnerException ?? ex).Message);
            }
        }

        private static ConnectionInfo Fallback(string reason)
        {
            var f = Settings.Fallback;
            string msg = f != null ? $"{reason}; using settings.ini fallback {f}" : reason;
            if (msg != lastLogged) { Log.Write(msg); lastLogged = msg; }
            return f;
        }
    }
}
