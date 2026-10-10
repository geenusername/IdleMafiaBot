using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;

[assembly: AssemblyTitle("Idle Mafia Bot")]
[assembly: AssemblyProduct("Idle Mafia Bot")]
[assembly: AssemblyVersion(IdleMafiaBot.Program.Version)]
[assembly: AssemblyFileVersion(IdleMafiaBot.Program.Version)]

namespace IdleMafiaBot
{
    static class Program
    {

        public const string Version = "1.7.8";

        [STAThread]
        static void Main(string[] args)
        {
            bool first;
            using (var one = new Mutex(true, "IdleMafiaBot.SingleInstance", out first))
            {

                if (!first && Array.IndexOf(args, "--restarted") >= 0)
                    try { first = one.WaitOne(30000); } catch (AbandonedMutexException) { first = true; }
                if (!first)
                {
                    MessageBox.Show("Idle Mafia Bot is already running.", "Idle Mafia Bot");
                    return;
                }

                try { File.Delete(Updater.OldCopy(Assembly.GetEntryAssembly().Location)); } catch (Exception) { }

                string dpi = SetDpiAware();

                CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                Bot bot = null;
                UiWindow ui = null;
                bool shown = false;
                var told = new System.Collections.Generic.HashSet<string>();
                app.DispatcherUnhandledException += (s, e) =>
                {
                    e.Handled = true;

                    if (DrawingFailed(e.Exception) && ui != null && ui.RestartAfterDrawingFailed(e.Exception)) return;

                    if (!told.Add(e.Exception.GetType().Name + ": " + e.Exception.Message)) return;
                    if (bot != null) { bot.Log("Problem in the window: " + e.Exception.Message); bot.LogDetails(e.Exception.ToString()); bot.NoteMemory(e.Exception); }

                    if (ui != null && ui.Closing) return;
                    MessageBox.Show(DrawingFailed(e.Exception) ? DrawingText : e.Exception.Message, "Idle Mafia Bot");
                };
                try
                {
                    app.Resources = (ResourceDictionary)UiWindow.LoadXaml("Theme.xaml");
                    string dir = AppDomain.CurrentDomain.BaseDirectory;
                    var settings = Settings.Load(Path.Combine(dir, "IdleMafiaBot.ini"));
                    bot = new Bot(settings, dir);
                    if (dpi != null) bot.Log(dpi);
                    ui = new UiWindow(settings, bot, dir, args);

                    if (settings.Unreadable != null)
                        bot.Log("Couldn't read your settings file (" + settings.Unreadable + "). Nothing is saved over it this time. Close the bot and start it again.");
                    ui.Window.ContentRendered += (s, e) => shown = true;
                    app.Run(ui.Window);
                }
                catch (Exception e)
                {

                    if (DrawingFailed(e) && ui != null && ui.Closing)
                    {
                        if (bot != null) { bot.Log("The window's drawing had failed (a problem in Windows' graphics) - closed anyway, nothing lost"); bot.LogDetails(e.ToString()); }
                        return;
                    }
                    if (DrawingFailed(e) && ui != null && ui.RestartAfterDrawingFailed(e)) return;
                    string what = shown ? "Idle Mafia Bot stopped because of an error" : "Idle Mafia Bot couldn't start";
                    if (bot != null) { bot.Log(what + ": " + e.Message); bot.LogDetails(e.ToString()); bot.NoteMemory(e); }
                    MessageBox.Show(what + ":\n\n" + e, "Idle Mafia Bot");
                }
            }
        }

        internal static bool DrawingFailed(Exception e)
        {
            for (; e != null; e = e.InnerException)
                if (e.HResult == unchecked((int)0x88980406) || (e.Message ?? "").Contains("UCEERR_")) return true;
            return false;
        }

        internal const string DrawingText = "The bot's window stopped drawing (a problem in Windows' graphics, for example after sleep or a driver update). "
                                          + "The bot keeps playing. Close it and start it again to see the window.";

        static string SetDpiAware()
        {
            try { if (Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) return null; }
            catch (EntryPointNotFoundException)
            {
                try { if (Native.SetProcessDpiAwareness(2) == 0) return null; }
                catch (Exception) { }
            }
            catch (Exception) { }
            try { if (Native.IsProcessDPIAware() || Native.SetProcessDPIAware()) return null; }
            catch (Exception) { }
            return "Windows' display scaling couldn't be turned off for the bot - if its clicks land in the wrong place, set the screen's scale to 100% "
                 + "or remove the high DPI setting in IdleMafiaBot.exe's Properties > Compatibility";
        }

        internal static bool InTempFolder(string dir)
        {
            try
            {
                string temp = Native.LongPath(Path.GetTempPath()).TrimEnd('\\') + "\\", here = Native.LongPath(dir).TrimEnd('\\') + "\\";
                return here.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }
    }
}
