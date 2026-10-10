using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace IdleMafiaBot
{

    static class ProblemReport
    {

        const long PictureBudget = 8L * 1024 * 1024, LogTail = 2L * 1024 * 1024, OldLogTail = 1L * 1024 * 1024;

        public static string Write(string appDir, string toDir, string roblox)
        {
            Directory.CreateDirectory(toDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string zip = Path.Combine(toDir, "IdleMafiaBot-report-" + stamp + ".zip");
            for (int n = 2; File.Exists(zip); n++) zip = Path.Combine(toDir, "IdleMafiaBot-report-" + stamp + "-" + n + ".zip");
            var skipped = new List<string>();
            var leftOut = new List<string>();
            try
            {
                using (var z = new ZipArchive(new FileStream(zip, FileMode.CreateNew), ZipArchiveMode.Create))
                {
                    AddText(z, Path.Combine(appDir, "IdleMafiaBot.log"), "IdleMafiaBot.log", LogTail, skipped, leftOut);
                    AddText(z, Path.Combine(appDir, "IdleMafiaBot.1.log"), "IdleMafiaBot.1.log", OldLogTail, skipped, leftOut);
                    AddText(z, Path.Combine(appDir, "jobs.txt"), "jobs.txt", 0, skipped, leftOut);
                    AddSettings(z, Path.Combine(appDir, "IdleMafiaBot.ini"), skipped);
                    foreach (var file in Directory.GetFiles(appDir, "profile-*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        AddText(z, file, Path.GetFileName(file), 0, skipped, leftOut);
                    AddProblems(z, Path.Combine(appDir, "problems"), skipped, leftOut);
                    using (var w = new StreamWriter(z.CreateEntry("system.txt").Open(), new UTF8Encoding(false)))
                        w.Write(NoUser(SystemText(roblox, skipped, leftOut)));
                }
            }
            catch (Exception)
            {
                try { File.Delete(zip); } catch (Exception) { }
                throw;
            }
            return zip;
        }

        public static int MoveReported(string appDir, DateTime before)
        {
            string active = Path.Combine(appDir, "problems", "active"), reported = Path.Combine(appDir, "problems", "reported");
            if (!Directory.Exists(active)) return 0;
            int moved = 0;
            foreach (var file in Directory.GetFiles(active, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTime(file) >= before) continue;
                    string rel = file.Substring(active.Length + 1), to = Path.Combine(reported, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    for (int n = 2; File.Exists(to); n++)
                        to = Path.Combine(Path.GetDirectoryName(to), Path.GetFileNameWithoutExtension(rel) + "-" + n + Path.GetExtension(rel));
                    File.Move(file, to);
                    if (Path.GetDirectoryName(rel).Length == 0 && rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) moved++;
                }
                catch (Exception) { }
            }
            return moved;
        }

        static void AddProblems(ZipArchive z, string problems, List<string> skipped, List<string> leftOut)
        {
            string active = Path.Combine(problems, "active"), reported = Path.Combine(problems, "reported");
            if (Directory.Exists(Path.Combine(problems, "fixed"))) leftOut.Add("problems/fixed (pictures of things already fixed)");

            var files = new List<string>();
            if (Directory.Exists(active))
                files.AddRange(Directory.GetFiles(active, "*", SearchOption.AllDirectories).OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));

            if (Directory.Exists(reported))
            {
                var all = Directory.GetFiles(reported, "*", SearchOption.AllDirectories);
                var since = DateTime.Now.AddDays(-1);
                var recent = all.Where(f => File.GetLastWriteTime(f) >= since).OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
                files.AddRange(recent);
                if (all.Length > recent.Count) leftOut.Add("problems/reported older than a day (pictures sent in an earlier report)");
            }
            if (files.Count == 0) return;
            long used = 0;
            var over = new List<string>();
            foreach (var file in files)
            {

                string name = NoUser("problems/" + file.Substring(problems.Length + 1).Replace('\\', '/'), true);
                if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { AddFile(z, file, name, skipped); continue; }
                if (used >= PictureBudget) { over.Add(name); continue; }
                byte[] jpg;
                try { jpg = Discord.JpgOf(file); }
                catch (Exception ex) { skipped.Add(name + ": " + ex.Message.Replace(file, Path.GetFileName(file))); continue; }
                if (jpg == null) continue;
                if (used > 0 && used + jpg.Length > PictureBudget) { over.Add(name); used = PictureBudget; continue; }
                used += jpg.Length;
                var e = z.CreateEntry(Path.ChangeExtension(name, ".jpg"), CompressionLevel.NoCompression);
                Stamp(e, file);
                using (var dst = e.Open()) dst.Write(jpg, 0, jpg.Length);
            }
            if (over.Count > 0)
            {
                leftOut.Add(over.Count + " older problem pictures (to keep the zip small):");
                foreach (var n in over.Take(100)) leftOut.Add("  " + n);
                if (over.Count > 100) leftOut.Add("  and " + (over.Count - 100) + " more");
            }
        }

        static void AddText(ZipArchive z, string file, string name, long tail, List<string> skipped, List<string> leftOut)
        {
            if (!File.Exists(file)) return;
            try
            {
                byte[] bytes;
                using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long from = tail > 0 && src.Length > tail ? src.Length - tail : 0;
                    src.Seek(from, SeekOrigin.Begin);
                    using (var ms = new MemoryStream()) { src.CopyTo(ms); bytes = ms.ToArray(); }
                    if (from > 0)
                    {

                        int nl = Array.IndexOf(bytes, (byte)'\n');
                        if (nl >= 0) bytes = bytes.Skip(nl + 1).ToArray();
                        leftOut.Add(name + ": its first " + Size(from + (nl + 1)) + " (older lines)");
                    }
                }
                string text = NoUser(new UTF8Encoding(false).GetString(bytes));
                var e = z.CreateEntry(name, CompressionLevel.Optimal);
                Stamp(e, file);
                using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(text);
            }

            catch (IOException ex) { skipped.Add(name + ": " + ex.Message.Replace(file, name)); }
            catch (UnauthorizedAccessException ex) { skipped.Add(name + ": " + ex.Message.Replace(file, name)); }
        }

        static void AddFile(ZipArchive z, string file, string name, List<string> skipped)
        {
            if (!File.Exists(file)) return;
            try
            {
                using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var e = z.CreateEntry(name, CompressionLevel.Optimal);
                    Stamp(e, file);
                    using (var dst = e.Open()) src.CopyTo(dst);
                }
            }
            catch (IOException ex) { skipped.Add(name + ": " + ex.Message.Replace(file, Path.GetFileName(file))); }
            catch (UnauthorizedAccessException ex) { skipped.Add(name + ": " + ex.Message.Replace(file, Path.GetFileName(file))); }
        }

        static void Stamp(ZipArchiveEntry e, string file)
        {
            var time = File.GetLastWriteTime(file);
            if (time.Year >= 1980 && time.Year <= 2107) e.LastWriteTime = time;
        }

        static string Size(long bytes)
        {
            return bytes < 1048576 ? Math.Max(1, bytes / 1024) + " KB" : (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        internal static string NoUser(string text, bool fileName = false)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string home = "", user = "";
            try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? ""; user = Environment.UserName ?? ""; }
            catch (Exception) { }
            home = home.TrimEnd('\\', '/');
            if (home.Length > 3)
            {
                text = Regex.Replace(text, Regex.Escape(home), "%USERPROFILE%", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, Regex.Escape(home.Replace('\\', '/')), "%USERPROFILE%", RegexOptions.IgnoreCase);
            }
            if (user.Length >= 3)
                text = Regex.Replace(text, (fileName ? @"(?<=[\\/-])" : @"(?<=[\\/])") + Regex.Escape(user) + @"(?![A-Za-z0-9])", fileName ? "user" : "<user>", RegexOptions.IgnoreCase);
            return text;
        }

        static void AddSettings(ZipArchive z, string file, List<string> skipped)
        {
            if (!File.Exists(file)) return;
            try
            {
                string text;
                using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(src)) text = r.ReadToEnd();
                text = NoUser(HideLink(text));
                using (var w = new StreamWriter(z.CreateEntry("IdleMafiaBot.ini").Open(), new UTF8Encoding(false))) w.Write(text);
            }
            catch (IOException ex) { skipped.Add("IdleMafiaBot.ini: " + ex.Message.Replace(file, "IdleMafiaBot.ini")); }
            catch (UnauthorizedAccessException ex) { skipped.Add("IdleMafiaBot.ini: " + ex.Message.Replace(file, "IdleMafiaBot.ini")); }
        }

        internal static string HideLink(string text)
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^([ \t]*(?:DiscordLink|StatsLink)[ \t]*=)[ \t]*[^\s][^\r\n]*", "$1(set, left out of this report)");
            return System.Text.RegularExpressions.Regex.Replace(text, @"https?://\S*webhooks/\S+", "(a webhook link, left out)");
        }

        static string SystemText(string roblox, List<string> skipped, List<string> leftOut)
        {
            var sb = new StringBuilder();
            var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
            sb.AppendLine("Idle Mafia Bot " + Program.Version);
            sb.AppendLine("Report made:   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + " (UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm") + ", the log's clock)");
            sb.AppendLine("Windows:       " + WindowsVersion());
            sb.AppendLine(".NET:          " + Environment.Version + (Environment.Is64BitProcess ? ", 64-bit" : ", 32-bit"));

            sb.AppendLine("Memory:        " + Try(() => Native.MemoryText()));
            sb.AppendLine("Monitors (real pixels, Windows scale):");
            var monitors = Monitors();
            for (int i = 0; i < monitors.Count; i++) sb.AppendLine("  " + (i + 1) + ": " + Describe(monitors[i]));
            if (monitors.Count == 0) sb.AppendLine("  none found");
            sb.AppendLine("Roblox window: " + roblox);
            string ocr = Ocr.Check();
            sb.AppendLine("Text recognition (OCR): " + (ocr == null ? Ocr.Language : "not usable - " + ocr));
            sb.AppendLine("  installed on this PC: " + Try(() => string.Join(", ", Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag))));
            sb.AppendLine("  Windows languages: " + Try(() => string.Join(", ", Windows.System.UserProfile.GlobalizationPreferences.Languages)));
            foreach (var s in skipped) sb.AppendLine("Couldn't add " + s);
            if (leftOut.Count > 0) sb.AppendLine("Left out of this zip:");
            foreach (var s in leftOut) sb.AppendLine("  " + s);
            return sb.ToString();
        }

        static string Try(Func<string> f)
        {
            try { string s = f(); return s.Length > 0 ? s : "none"; }
            catch (Exception e) { return "? (" + e.Message + ")"; }
        }

        static string WindowsVersion()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string name = k.GetValue("ProductName") as string ?? "Windows";
                    string release = k.GetValue("DisplayVersion") as string ?? k.GetValue("ReleaseId") as string;
                    string build = k.GetValue("CurrentBuild") as string ?? "?";
                    object ubr = k.GetValue("UBR");
                    int b;

                    if (int.TryParse(build, out b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                    return name + (release != null ? " " + release : "") + " (build " + build + (ubr != null ? "." + ubr : "") + ")"
                        + (Environment.Is64BitOperatingSystem ? ", 64-bit" : ", 32-bit");
                }
            }
            catch (Exception) { return Environment.OSVersion.ToString(); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize;
            public Native.RECT Monitor, Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
        delegate bool MonitorProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        static List<IntPtr> Monitors()
        {
            var list = new List<IntPtr>();
            try
            {
                MonitorProc add = (m, dc, r, d) => { list.Add(m); return true; };
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, add, IntPtr.Zero);
                GC.KeepAlive(add);
            }
            catch (Exception) { }
            return list;
        }

        static string Describe(IntPtr monitor)
        {
            try
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (!GetMonitorInfo(monitor, ref mi)) return "?";
                uint dx, dy;
                string scale = GetDpiForMonitor(monitor, 0, out dx, out dy) == 0 ? (dx * 100 / 96) + "%" : "?";
                var r = mi.Monitor;
                return string.Format(CultureInfo.InvariantCulture, "{0}x{1} at ({2},{3}), scale {4}{5}",
                    r.Right - r.Left, r.Bottom - r.Top, r.Left, r.Top, scale, (mi.Flags & 1) != 0 ? ", main" : "");
            }
            catch (Exception e) { return "? (" + e.Message + ")"; }
        }

        public static string RobloxWindowInfo()
        {
            try
            {
                var w = new RobloxWindow();
                if (!w.Find()) return "not found (Roblox isn't open)";
                if (w.Minimized) return "minimized";
                return w.Width + "x" + w.Height + " client area, " + MonitorOf(w.Handle)
                       + (RobloxWindow.ScreenFirst ? ", read from the screen (Windows gave empty pictures of it)" : "");
            }
            catch (Exception e) { return "? (" + e.Message + ")"; }
        }

        internal static string MonitorOf(IntPtr window)
        {
            int i = Monitors().IndexOf(MonitorFromWindow(window, 2));
            return i >= 0 ? "on monitor " + (i + 1) : "on no monitor";
        }
    }
}
