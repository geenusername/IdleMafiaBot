using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace IdleMafiaBot
{

    public sealed class FeedItem
    {
        public string Who { get; set; }
        public Brush Color { get; set; }
        public Brush Stripe { get; set; }
        public Geometry Icon { get; set; }
        public string Time { get; set; }
        public string Text { get; set; }
    }

    public sealed class OpsRow
    {
        public string Name { get; set; }
        public string Left { get; set; }
        public GridLength A { get; set; }
        public GridLength B { get; set; }
    }

    public sealed class PerkView
    {
        public string Name { get; set; }
        public string Effect { get; set; }
        public string Level { get; set; }
        public string Progress { get; set; }
        public Brush NameBrush { get; set; }
        public FontWeight NameWeight { get; set; }
        public Brush RowBg { get; set; }
        public GridLength A { get; set; }
        public GridLength B { get; set; }
    }

    public sealed class FoundRow
    {
        public string Title { get; set; }
        public string Text { get; set; }
    }

    public sealed class ChangeRow
    {
        public string Name { get; set; }
        public string Old { get; set; }
        public string New { get; set; }
        public string Reason { get; set; }
        public string Note { get; set; }
        public bool Use { get; set; }
        internal string Field;
        internal object Value;
    }

    public sealed class JobRow
    {
        public string Header { get; set; }
        public bool IsHeader { get; set; }
        public string Summary { get; set; }
        public string Sign { get; set; }
        public ICommand Toggle { get; set; }
        public string Name { get; set; }
        public string Cost { get; set; }
        public string Xp { get; set; }
        public string Rank { get; set; }
        public Brush RankBrush { get; set; }
        public Brush NameBrush { get; set; }
        public FontWeight NameWeight { get; set; }
        public Brush RowBg { get; set; }
        public GridLength A { get; set; }
        public GridLength B { get; set; }
        public ICommand Pick { get; set; }
        public bool Pickable { get; set; }
        public bool Hover { get; set; }
    }

    public sealed class BigPayerView
    {
        public string Name { get; set; }
        public string Biggest { get; set; }
        public string Last { get; set; }
        public string When { get; set; }
        public string Record { get; set; }
        public string RecordTip { get; set; }
        public Brush RowBg { get; set; }
        public ICommand Forget { get; set; }
    }

    public sealed class RelayCommand : ICommand
    {
        readonly Action run;
        public RelayCommand(Action run) { this.run = run; }
        public bool CanExecute(object parameter) { return true; }
        public void Execute(object parameter) { run(); }
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }

    sealed class UiWindow
    {
        sealed class Crew
        {
            public string Name, Color, Icon;
            public Crew(string name, string color, string icon) { Name = name; Color = color; Icon = icon; }
        }

        static readonly Crew Runner = new Crew("Jobs", "#E08C32", "IcoJobs");
        static readonly Crew Fixer = new Crew("Heists", "#8B6ABB", "IcoHeist");
        static readonly Crew Enforcer = new Crew("Fights", "#C84B3F", "IcoSwords");
        static readonly Crew Capo = new Crew("Bosses", "#D4AF37", "IcoCrown");
        static readonly Crew Accountant = new Crew("Bank", "#7EBE5A", "IcoBank");
        static readonly Crew Consigliere = new Crew("Operations", "#5B8FB9", "IcoHourglass");
        static readonly Crew Collector = new Crew("Rewards", "#C9956A", "IcoGift");
        static readonly Crew Items = new Crew("Items", "#C9956A", "IcoGift");
        static readonly Crew Landlord = new Crew("Properties", "#7EBE5A", "IcoBank");
        static readonly Crew Underboss = new Crew("Family", "#4FB3A5", "IcoFamily");
        static readonly Crew Henchmen = new Crew("Crew", "#E0709A", "IcoCrew");
        static readonly Crew Don = new Crew("Bot", "#E8E2D0", "IcoFedora");

        static readonly string[] Pages = { "Overview", "Jobs", "Settings" };
        static readonly string[] Titles = { "Home", "Tasks", "Settings" };
        static readonly string[] RarityBrushes = { "RarityCommon", "RarityUncommon", "RarityRare", "RarityEpic", "RarityLegendary", "RarityMythic", "RaritySecret", "RarityForbidden" };
        const string ClosestPerk = "Closest to its next level";

        public readonly Window Window;
        readonly Settings s;
        readonly Bot bot;
        readonly string dir;
        readonly ObservableCollection<FeedItem> feed = new ObservableCollection<FeedItem>();
        readonly ObservableCollection<OpsRow> ops = new ObservableCollection<OpsRow>();
        readonly ObservableCollection<JobRow> jobRows = new ObservableCollection<JobRow>();
        readonly ObservableCollection<PerkView> perkRows = new ObservableCollection<PerkView>();
        readonly ObservableCollection<BigPayerView> payerRows = new ObservableCollection<BigPayerView>();
        FightMemory payers;
        readonly ObservableCollection<FoundRow> foundRows = new ObservableCollection<FoundRow>();
        readonly ObservableCollection<ChangeRow> changeRows = new ObservableCollection<ChangeRow>();
        readonly ObservableCollection<ChangeRow> keptRows = new ObservableCollection<ChangeRow>();
        Profile profile;
        string profileOf;
        List<string> perkChoices;
        FamilyInfo fam;
        readonly DateTime started = DateTime.Now;
        Header last;
        string playerName;
        bool jobsDirty = true;

        public UiWindow(Settings settings, Bot bot, string dir, string[] args)
        {
            s = settings;
            this.bot = bot;
            this.dir = dir;
            this.args = args;
            UseDecoFont();
            Window = (Window)LoadXaml("MainWindow.xaml");

            var work = SystemParameters.WorkArea;
            if (work.Width > 0 && work.Height > 0)
            {
                Window.MinWidth = Math.Min(Window.MinWidth, work.Width);
                Window.MinHeight = Math.Min(Window.MinHeight, work.Height);
                Window.Width = Math.Min(Window.Width, work.Width);
                Window.Height = Math.Min(Window.Height, work.Height);
            }

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string iconName = s.Theme == ThemeChoice.Halloween ? "IdleMafiaBot.ui.app_halloween.ico" : "IdleMafiaBot.ui.app.ico";
                var ico = asm.GetManifestResourceStream(iconName) ?? asm.GetManifestResourceStream("IdleMafiaBot.ui.app.ico");
                if (ico != null) Window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(ico, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            }
            catch (Exception) { }

            WindowChrome.SetWindowChrome(Window, new WindowChrome
            {
                CaptionHeight = 84,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });

            WirePages();
            WireSettings();
            WireButtons();
            WireTiles();
            El<ItemsControl>("Feed").ItemsSource = feed;
            WireHome();
            El<ItemsControl>("OpsList").ItemsSource = ops;
            El<ItemsControl>("JobList").ItemsSource = jobRows;
            El<ItemsControl>("PerkList").ItemsSource = perkRows;
            El<ItemsControl>("BigPayerList").ItemsSource = payerRows;
            UsePayers(bot.Memory);
            El<ItemsControl>("FoundList").ItemsSource = foundRows;
            El<ItemsControl>("ChangeList").ItemsSource = changeRows;
            El<ItemsControl>("KeptList").ItemsSource = keptRows;
            Pulse("RunGlow");
            Pulse("NowGlow");
            WireHalloween();
            WireHalloweenArt();
            WireSpiderGrab();
            WireHalloweenHaunts();
            Window.Loaded += (a, b) => Window.Dispatcher.BeginInvoke(new Action(CalmIdleAnimations), System.Windows.Threading.DispatcherPriority.Background);

            bot.Logged += line => Ui(() => AddFeed(line));
            bot.StateChanged += st => Ui(() => ShowState(st));
            bot.HeaderRead += h => Ui(() => ShowHeader(h));
            bot.InfoChanged += (k, v) => Ui(() => ShowInfo(k, v));
            bot.OpsRead += slots => Ui(() => ShowOps(slots));
            bot.CatalogChanged += () => Ui(() => { jobsDirty = true; RefreshJobs(); });

            bot.AccountChanged += name => Ui(() =>
            {
                if (accountShown != null && !string.Equals(Profile.Key(accountShown), Profile.Key(name), StringComparison.OrdinalIgnoreCase)) ForgetAccount();
                accountShown = name;
                RefreshControls(); UpdateDerived(); jobsDirty = true; RefreshJobs(); AccountSeen(name); ShowCrew(null);
            });
            bot.InfoChanged += (k, v) => { if (k == "ocr") Ui(() => ShowOcr(v)); };
            bot.FamilyRead += fi => Ui(() => ShowFamily(fi));
            bot.CrewRead += ci => Ui(() => ShowCrew(ci));
            bot.SafehouseRead += info => Ui(() => ShowSafehouse(info));
            bot.Discord.Moment += (k, title, text) => Ui(() => ShowMoment(k, title, text));
            bot.ProfileRead += p => Ui(() => CheckDone(p));
            bot.ProfileChanged += p => Ui(() => { if (profileOf == Profile.Key(p.Name)) { profile = p; UpdateCheckWhen(); if (ReviewShowing) RefreshReview(); } });

            Window.Title = "Idle Mafia Bot " + Program.Version;
            El<TextBlock>("VersionText").Text = "version " + Program.Version;
            El<TextBlock>("SupportVersion").Text = "version " + Program.Version;
            El<TextBlock>("WelcomeVersion").Text = "Version " + Program.Version;
            ShowWelcome(!s.WelcomeSeen);
            ShowRules(!args.Contains("--demo") || args.Contains("--rules"));

            Window.SourceInitialized += (a, b) =>
            {
                var hwnd = new WindowInteropHelper(Window).Handle;
                HwndSource.FromHwnd(hwnd).AddHook(WndProc);
                if (!Native.RegisterHotKey(hwnd, 1, 0, 0x75)) AddFeed(DateTime.Now.ToString("HH:mm:ss") + "  F6 is taken by another program. Use the Start button.");
            };

            if (Application.Current != null)
                Application.Current.SessionEnding += (a, b) => closedBy = "Windows (" + (b.ReasonSessionEnding == ReasonSessionEnding.Logoff ? "signing out" : "shutting down, or closing the program that started the bot") + ")";
            Window.Closing += (a, b) =>
            {
                Closing = true;
                bot.Log("Window closed " + (closedBy != null ? "by " + closedBy : "by another program or Windows (not its Close button)"));
                bool wasRunning = bot.Running;
                bot.Stop();
                Save();

                if (ClosedMessage(wasRunning, closedBy))
                    bot.Discord.Problem("closed", "\U0001F6D1 The bot was closed", "By " + (closedBy ?? "another program or Windows") + ".", Discord.ShotOfRoblox, false, 0);

                bot.Discord.Flush((closedBy ?? "").StartsWith("you") ? 800 : 4000);

                bot.Stats.Closing(closedBy);
            };

            bool scan = args.Contains("--scan"), check = args.Contains("--check"), start = args.Contains("--start"), exit = args.Contains("--exit");
            if (args.Contains("--once")) { bot.Once = true; start = true; }
            var page = args.FirstOrDefault(a => a.StartsWith("--page="));
            if (page != null) { var rail = Window.FindName("Rail" + page.Substring(7)) as RadioButton; if (rail != null) rail.IsChecked = true; }
            bool demo = args.Contains("--demo");

            if (!demo && Program.InTempFolder(dir))
            {
                ShowWarning("Unzip the bot first", "The bot runs from a temporary folder, so its settings and log will be lost. Close it, right-click the zip, "
                            + "pick Extract All, and start IdleMafiaBot.exe from the folder that makes.", false);
                bot.Log("The bot runs from a temporary folder (inside the zip?) - unzip the download (right-click > Extract All) and start it from there, or its settings get lost");
            }

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string problem = Ocr.Check();
                Ui(() =>
                {
                    ShowOcr(problem);
                    if (problem != null) { bot.Log("Problem: " + problem); return; }
                    if (Ocr.Warning != null) { ShowWarning("Text is read in another language", Ocr.Warning, true); bot.Log(Ocr.Warning); }
                    if (!demo) bot.StartWatching();
                });
            });

            bool rerollOnly = args.Contains("--rerollonly");
            if ((start || rerollOnly) && args.Contains("--restarted"))
                Window.Loaded += (a, b) => { if (!bot.Running) { if (rerollOnly) bot.StartRerollOnly(); else bot.Start(); } };
            Window.ContentRendered += (a, b) =>
            {
                shownAt = DateTime.UtcNow;
                if (scan) bot.RequestScan(); else if (check) bot.RequestCheck(); else if (rerollOnly && !bot.Running) bot.StartRerollOnly(); else if (start && !bot.Running) bot.Start();
                ShowState(bot.Running ? (check ? "Checking" : "Starting") : "Stopped");
            };
            if (exit) bot.StateChanged += st => { if (st == "Stopped") Ui(() => { closedBy = "--exit (the run is over)"; Window.Close(); }); };

            ShowState("Stopped");
            ShowFavors(-1);
            ShowFamily(null);
            ShowCrew(null);
            RefreshJobCard();
            RefreshJobs();
            UpdateTotals();
            UpdateCheckWhen();
            UpdateProblems();

            Window.Activated += (a, b) => UpdateProblems();
            if (demo) Demo();
            else bot.Stats.Begin();
        }

        public static object LoadXaml(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("IdleMafiaBot.ui." + name))
            {
                if (stream == null) throw new InvalidOperationException("Missing UI file " + name);
                return XamlReader.Load(stream);
            }
        }

        T El<T>(string name) where T : class
        {
            var o = Window.FindName(name) as T;
            if (o == null) throw new InvalidOperationException("UI element missing: " + name);
            return o;
        }

        Brush Res(string key) { return (Brush)Window.FindResource(key); }
        static Brush Hex(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        void Ui(Action a) { if (!Window.Dispatcher.HasShutdownStarted) Window.Dispatcher.BeginInvoke(a); }

        void Pulse(string name)
        {
            var anim = new DoubleAnimation(0.08, 0.35, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            El<Ellipse>(name).BeginAnimation(UIElement.OpacityProperty, anim);
        }

        readonly Random hauntRand = new Random();
        readonly HashSet<string> batsFlying = new HashSet<string>();
        System.Windows.Threading.DispatcherTimer batTimer;
        readonly Dictionary<string, System.Windows.Media.Imaging.BitmapFrame> artCache = new Dictionary<string, System.Windows.Media.Imaging.BitmapFrame>();

        System.Windows.Media.Imaging.BitmapFrame Art(string name)
        {
            System.Windows.Media.Imaging.BitmapFrame f;
            if (artCache.TryGetValue(name, out f)) return f;
            try
            {
                var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("IdleMafiaBot.ui." + name);
                if (s != null)
                {
                    f = System.Windows.Media.Imaging.BitmapFrame.Create(s, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    artCache[name] = f;
                }
            }
            catch (Exception) { }
            return f;
        }

        void SetArt(string element, string art)
        {
            try
            {
                var img = El<System.Windows.Controls.Image>(element);
                var f = Art(art);
                if (f != null) img.Source = f;
            }
            catch (Exception) { }
        }

        void WireHalloweenArt()
        {
            if (s.Theme != ThemeChoice.Halloween) return;
            Ui(() =>
            {
                SetArt("ImgBack", "bg_halloween_back.png");
                SetArt("ImgMid", "bg_halloween_mid.png");
                SetArt("ImgTomb", "tombstone_halloween.png");
                SetArt("ImgFog", "fog_halloween.png");
                SetArt("Bat1", "bat_halloween.png");
                SetArt("Bat2", "bat_halloween.png");
                SetArt("Bat3", "bat_halloween.png");
                SetArt("GhostBack", "ghost_halloween.png");
                SetArt("GhostHero", "ghost_halloween.png");
                SetArt("HImgBack", "bg_halloween_back.png");
                SetArt("HImgMid", "bg_halloween_mid.png");
            });
        }

        void CalmIdleAnimations()
        {
            try
            {
                if (s.Theme == ThemeChoice.Halloween) { StopOpacity(Window.FindName("SkyWinBack") as UIElement); return; }
                StopOpacity(Window.FindName("MoonGlow") as UIElement);
                StopOpacity(Window.FindName("HauntedLabel") as UIElement);
                foreach (var n in new[] { "ImgFog", "GhostBack", "GhostHero", "WitchHat", "Spider" })
                    StopTransformAnim(Window.FindName(n) as FrameworkElement);
            }
            catch (Exception) { }
        }

        static void StopOpacity(UIElement el)
        {
            if (el != null) el.BeginAnimation(UIElement.OpacityProperty, null);
        }

        static void StopTransformAnim(FrameworkElement el)
        {
            var t = el == null ? null : el.RenderTransform;
            if (t is TranslateTransform)
            {
                var tt = (TranslateTransform)t;
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.BeginAnimation(TranslateTransform.YProperty, null);
            }
            else if (t is RotateTransform) ((RotateTransform)t).BeginAnimation(RotateTransform.AngleProperty, null);
            else if (t is TransformGroup)
                foreach (var c in ((TransformGroup)t).Children) if (c is RotateTransform) ((RotateTransform)c).BeginAnimation(RotateTransform.AngleProperty, null);
        }

        void WireHalloween()
        {
            if (s.Theme != ThemeChoice.Halloween) return;
            batTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            batTimer.Tick += (a, b) =>
            {
                try
                {
                    batTimer.Interval = TimeSpan.FromSeconds(2.5 + hauntRand.NextDouble() * 3.5);
                    FlyBat();
                }
                catch (Exception) { }
            };
            batTimer.Start();
            Window.Closed += (a, b) => { try { if (batTimer != null) batTimer.Stop(); } catch (Exception) { } };
            Ui(() => FlyBat());
        }

        void FlyBat()
        {
            var names = new[] { "Bat1", "Bat2", "Bat3" };
            var free = names.Where(n => !batsFlying.Contains(n)).ToList();
            if (free.Count == 0) return;
            string name = free[hauntRand.Next(free.Count)];
            FrameworkElement el;
            try { el = El<FrameworkElement>(name); }
            catch (Exception) { return; }
            bool leftToRight = hauntRand.Next(2) == 0;
            double fromX = leftToRight ? 1660 : -160, toX = leftToRight ? -160 : 1660;
            double top = 15 + hauntRand.NextDouble() * 130;
            double secs = 9 + hauntRand.NextDouble() * 11;
            double wobble = 10 + hauntRand.NextDouble() * 14;
            el.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    batsFlying.Add(name);
                    Canvas.SetLeft(el, fromX);
                    Canvas.SetTop(el, top);
                    var sb = new Storyboard();
                    var fly = new DoubleAnimation(fromX, toX, new Duration(TimeSpan.FromSeconds(secs)));
                    Storyboard.SetTarget(fly, el);
                    Storyboard.SetTargetProperty(fly, new PropertyPath("(Canvas.Left)"));
                    var wob = new DoubleAnimation(-wobble, wobble, new Duration(TimeSpan.FromSeconds(1.4 + hauntRand.NextDouble())));
                    wob.AutoReverse = true;
                    wob.RepeatBehavior = new RepeatBehavior(Math.Max(2, (int)(secs / 2.4)));
                    wob.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
                    Storyboard.SetTarget(wob, el);
                    Storyboard.SetTargetProperty(wob, new PropertyPath("RenderTransform.(TranslateTransform.Y)"));
                    sb.Children.Add(fly);
                    sb.Children.Add(wob);
                    sb.Completed += (a2, b2) => batsFlying.Remove(name);
                    sb.Begin();
                }
                catch (Exception) { batsFlying.Remove(name); }
            }));
        }

        Path spiderEl;
        Line webLine;
        TranslateTransform spiderDrag;
        ScaleTransform spiderStretch;
        bool spiderHeld;
        Point spiderGrabAt;
        System.Windows.Threading.DispatcherTimer spiderCycle;

        void StartSpiderCycle()
        {
            try
            {
                if (spiderCycle != null) spiderCycle.Stop();
                spiderCycle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5 + hauntRand.NextDouble() * 4) };
                spiderCycle.Tick += (a, b) => { try { spiderCycle.Stop(); } catch (Exception) { } SpiderDive(); };
                spiderCycle.Start();
            }
            catch (Exception) { }
        }

        void SpiderDive()
        {
            try
            {
                if (spiderEl == null) { StartSpiderCycle(); return; }
                bool deep = hauntRand.Next(100) < 20;
                double target = deep ? CashSpiderTop() : 128;
                AnimateSpiderTop(target, deep ? 4.5 : 3.0, () => HoldSpider(deep));
            }
            catch (Exception) { StartSpiderCycle(); }
        }

        void HoldSpider(bool deep)
        {
            try
            {
                var hold = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(deep ? 2.2 : 1.2) };
                hold.Tick += (a, b) => { try { hold.Stop(); } catch (Exception) { } AnimateSpiderTop(70, deep ? 3.0 : 2.5, () => StartSpiderCycle()); };
                hold.Start();
            }
            catch (Exception) { StartSpiderCycle(); }
        }

        void AnimateSpiderTop(double to, double secs, Action done)
        {
            try
            {
                var an = new DoubleAnimation(to, new Duration(TimeSpan.FromSeconds(secs)));
                an.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
                Storyboard.SetTarget(an, spiderEl);
                Storyboard.SetTargetProperty(an, new PropertyPath("(Canvas.Top)"));
                var sb = new Storyboard();
                sb.Children.Add(an);
                if (done != null) sb.Completed += (a, b) => done();
                sb.Begin();
            }
            catch (Exception) { if (done != null) done(); }
        }

        double CashSpiderTop()
        {
            try
            {
                var page = El<FrameworkElement>("PageOverview");
                var cash = El<TextBlock>("CashText");
                if (page.Visibility != Visibility.Visible || cash.ActualHeight <= 0) return 320;
                var c = cash.TransformToAncestor(Window).Transform(new Point(cash.ActualWidth / 2, cash.ActualHeight / 2));
                var sp = spiderEl.TransformToAncestor(Window).Transform(new Point(0, 0));
                return Math.Max(150, Math.Min(600, c.Y - sp.Y - 13));
            }
            catch (Exception) { return 320; }
        }

        void WireSpiderGrab()
        {
            if (s.Theme != ThemeChoice.Halloween) return;
            try
            {
                spiderEl = El<Path>("Spider");
                webLine = El<Line>("WebLine");
                spiderDrag = (TranslateTransform)Window.FindName("SpiderDrag");
                spiderStretch = (ScaleTransform)Window.FindName("SpiderStretch");
            }
            catch (Exception) { return; }
            StartSpiderCycle();
            System.Windows.Media.CompositionTarget.Rendering += (a, b) =>
            {
                try
                {
                    if (spiderEl == null || webLine == null || spiderDrag == null) return;
                    double top = (double)spiderEl.GetValue(Canvas.TopProperty);
                    webLine.X2 = 60 + spiderDrag.X * 0.9;
                    webLine.Y2 = top + spiderDrag.Y + 4;
                }
                catch (Exception) { }
            };
            Window.PreviewMouseLeftButtonDown += (a, e) =>
            {
                try
                {
                    if (spiderEl.Visibility != Visibility.Visible) return;
                    var at = e.GetPosition(Window);
                    var c = spiderEl.TransformToAncestor(Window).Transform(new Point(13, 13));
                    if ((at - c).Length > 30) return;
                    spiderHeld = true;
                    spiderGrabAt = at;
                    Window.CaptureMouse();
                    e.Handled = true;
                }
                catch (Exception) { }
            };
            Window.PreviewMouseMove += (a, e) =>
            {
                try
                {
                    if (spiderEl.Visibility != Visibility.Visible)
                    {
                        if (!spiderHeld) System.Windows.Input.Mouse.OverrideCursor = null;
                        return;
                    }
                    var at = e.GetPosition(Window);
                    if (spiderHeld)
                    {
                        double dx = Math.Max(-140, Math.Min(140, at.X - spiderGrabAt.X));
                        double dy = Math.Max(-140, Math.Min(140, at.Y - spiderGrabAt.Y));
                        spiderDrag.X = dx;
                        spiderDrag.Y = dy;
                        double k = Math.Min(0.4, (at - spiderGrabAt).Length / 350.0);
                        spiderStretch.ScaleY = 1 + k;
                        spiderStretch.ScaleX = 1 - k * 0.4;
                        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Hand;
                    }
                    else
                    {
                        var c = spiderEl.TransformToAncestor(Window).Transform(new Point(13, 13));
                        System.Windows.Input.Mouse.OverrideCursor = (at - c).Length <= 30 ? System.Windows.Input.Cursors.Hand : null;
                    }
                }
                catch (Exception) { }
            };
            Window.PreviewMouseUp += (a, e) =>
            {
                if (!spiderHeld) return;
                spiderHeld = false;
                try { Window.ReleaseMouseCapture(); }
                catch (Exception) { }
                System.Windows.Input.Mouse.OverrideCursor = null;
                try
                {
                    var sb = new Storyboard();
                    foreach (var p in new[] { "X", "Y" })
                    {
                        var an = new DoubleAnimation(0, new Duration(TimeSpan.FromSeconds(0.7)));
                        an.EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 4 };
                        Storyboard.SetTarget(an, spiderDrag);
                        Storyboard.SetTargetProperty(an, new PropertyPath(p));
                        sb.Children.Add(an);
                    }
                    foreach (var p in new[] { "ScaleX", "ScaleY" })
                    {
                        var an = new DoubleAnimation(1, new Duration(TimeSpan.FromSeconds(0.7)));
                        an.EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 4 };
                        Storyboard.SetTarget(an, spiderStretch);
                        Storyboard.SetTargetProperty(an, new PropertyPath(p));
                        sb.Children.Add(an);
                    }
                    sb.Begin();
                }
                catch (Exception)
                {
                    try { spiderDrag.X = spiderDrag.Y = 0; spiderStretch.ScaleX = spiderStretch.ScaleY = 1; }
                    catch (Exception) { }
                }
            };
        }

        FrameworkElement hauntFlash;
        Canvas trailLayer;
        readonly List<System.Windows.Controls.Image> trailPool = new List<System.Windows.Controls.Image>();
        int trailNext;
        DateTime trailLast;
        FrameworkElement eyeL, eyeR;
        TranslateTransform eyeLMove, eyeRMove;
        ScaleTransform eyeLBlink, eyeRBlink;

        void WireHalloweenHaunts()
        {
            if (s.Theme != ThemeChoice.Halloween) return;
            try { hauntFlash = El<FrameworkElement>("Lightning"); }
            catch (Exception) { hauntFlash = null; }
            try { trailLayer = El<Canvas>("TrailLayer"); }
            catch (Exception) { trailLayer = null; }
            try
            {
                eyeL = El<FrameworkElement>("EyeL");
                eyeR = El<FrameworkElement>("EyeR");
                eyeLMove = (TranslateTransform)Window.FindName("EyeLMove");
                eyeRMove = (TranslateTransform)Window.FindName("EyeRMove");
                eyeLBlink = (ScaleTransform)Window.FindName("EyeLBlink");
                eyeRBlink = (ScaleTransform)Window.FindName("EyeRBlink");
            }
            catch (Exception) { eyeL = eyeR = null; }
            if (trailLayer != null)
            {
                var bat = Art("bat_halloween.png");
                for (int i = 0; i < 8; i++)
                {
                    var img = new System.Windows.Controls.Image { Width = 14, Stretch = Stretch.Uniform, Opacity = 0, IsHitTestVisible = false };
                    if (bat != null) img.Source = bat;
                    trailLayer.Children.Add(img);
                    trailPool.Add(img);
                }
            }
            Window.PreviewMouseMove += (a, e) =>
            {
                try
                {
                    if (s.Theme != ThemeChoice.Halloween) return;
                    var at = e.GetPosition(Window);
                    if (eyeL != null && eyeL.Visibility == Visibility.Visible)
                    {
                        var c = eyeL.TransformToAncestor(Window).Transform(new Point(4.5, 5.5));
                        double dx = Math.Max(-7, Math.Min(7, (at.X - c.X) / 45));
                        double dy = Math.Max(-7, Math.Min(7, (at.Y - c.Y) / 45));
                        eyeLMove.X = eyeRMove.X = dx;
                        eyeLMove.Y = eyeRMove.Y = dy;
                    }
                    if (trailPool.Count > 0 && (DateTime.UtcNow - trailLast).TotalMilliseconds >= 220)
                    {
                        trailLast = DateTime.UtcNow;
                        var img = trailPool[trailNext++ % trailPool.Count];
                        var lp = e.GetPosition(trailLayer);
                        Canvas.SetLeft(img, lp.X - 7 + hauntRand.Next(-6, 7));
                        Canvas.SetTop(img, lp.Y - 6 + hauntRand.Next(-4, 5));
                        var sb = new Storyboard();
                        var fade = new DoubleAnimation(0.5, 0, new Duration(TimeSpan.FromSeconds(0.6)));
                        Storyboard.SetTarget(fade, img);
                        Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
                        var rise = new DoubleAnimation(0, -16, new Duration(TimeSpan.FromSeconds(0.6)));
                        Storyboard.SetTarget(rise, img);
                        Storyboard.SetTargetProperty(rise, new PropertyPath("RenderTransform.(TranslateTransform.Y)"));
                        img.RenderTransform = new TranslateTransform();
                        sb.Children.Add(fade);
                        sb.Children.Add(rise);
                        sb.Begin();
                    }
                }
                catch (Exception) { }
            };
            ScheduleLightning();
            ScheduleBlink();
        }

        void ScheduleLightning()
        {
            try
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(90 + hauntRand.NextDouble() * 150) };
                t.Tick += (a, b) => { try { t.Stop(); } catch (Exception) { } LightningStrike(); ScheduleLightning(); };
                t.Start();
            }
            catch (Exception) { }
        }

        void LightningStrike()
        {
            try
            {
                if (hauntFlash == null) return;
                var sb = new Storyboard();
                var flash = new DoubleAnimationUsingKeyFrames { RepeatBehavior = new RepeatBehavior(1) };
                Storyboard.SetTarget(flash, hauntFlash);
                Storyboard.SetTargetProperty(flash, new PropertyPath("Opacity"));
                flash.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.28, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.06))));
                flash.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.04, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.14))));
                flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.2))));
                flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.5))));
                sb.Children.Add(flash);
                sb.Begin();
            }
            catch (Exception) { }
        }

        void ScheduleBlink()
        {
            try
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5 + hauntRand.NextDouble() * 4.5) };
                t.Tick += (a, b) =>
                {
                    try { t.Stop(); } catch (Exception) { }
                    try
                    {
                        if (eyeLBlink != null && eyeL != null && eyeL.Visibility == Visibility.Visible)
                        {
                            var sb = new Storyboard();
                            foreach (var tr in new ScaleTransform[] { eyeLBlink, eyeRBlink })
                            {
                                var an = new DoubleAnimation(1, 0.08, new Duration(TimeSpan.FromSeconds(0.09))) { AutoReverse = true };
                                an.RepeatBehavior = new RepeatBehavior(2);
                                Storyboard.SetTarget(an, tr);
                                Storyboard.SetTargetProperty(an, new PropertyPath("ScaleY"));
                                sb.Children.Add(an);
                            }
                            sb.Begin();
                        }
                    }
                    catch (Exception) { }
                    ScheduleBlink();
                };
                t.Start();
            }
            catch (Exception) { }
        }

        void WirePages()
        {
            for (int i = 0; i < Pages.Length; i++)
            {
                int page = i;
                El<RadioButton>("Rail" + Pages[i]).Checked += (a, b) => ShowPage(page);
            }
            WireLayout();
            ShowPage(0);
        }

        const double ColsMinWidth = 840, FillMinList = 150;
        readonly List<Grid> fills = new List<Grid>();

        void WireLayout()
        {
            var all = new List<FrameworkElement>();
            Collect(Window, all);
            foreach (var e in all)
            {
                var fold = e as ToggleButton;
                if (fold != null && fold.Name.StartsWith("Fold_"))
                {
                    var part = El<FrameworkElement>(fold.Name.Substring(5));
                    RoutedEventHandler show = (a, b) => { part.Visibility = fold.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; FitFillsSoon(); };
                    fold.Checked += show;
                    fold.Unchecked += show;
                    show(null, null);
                }
                var grid = e as Grid;
                if (grid == null) continue;
                if ("Cols".Equals(grid.Tag))
                {
                    var g = grid;
                    EvenColumns(g);
                    var right = g.Children.OfType<UIElement>().Where(c => Grid.GetColumn(c) == 2).ToList();
                    var widths = g.ColumnDefinitions.Select(c => c.Width).ToList();
                    g.SizeChanged += (a, b) => FitCols(g, right, widths);
                }
                if ("Fill".Equals(grid.Tag))
                {
                    var g = grid;
                    fills.Add(g);
                    g.IsVisibleChanged += (a, b) => FitFillsSoon();
                    foreach (FrameworkElement c in g.Children) if (Grid.GetRow(c) < g.RowDefinitions.Count - 1) c.SizeChanged += (a, b) => FitFillsSoon();
                    var page = Page(g);
                    if (page != null) page.SizeChanged += (a, b) => FitFillsSoon();
                }
            }
        }

        static void EvenColumns(Grid g)
        {
            foreach (var stack in g.Children.OfType<StackPanel>().Where(p => p.Orientation == Orientation.Vertical).ToList())
            {
                var dock = new DockPanel { LastChildFill = true, Margin = stack.Margin, Visibility = stack.Visibility };
                Grid.SetColumn(dock, Grid.GetColumn(stack));
                Grid.SetRow(dock, Grid.GetRow(stack));
                Grid.SetColumnSpan(dock, Grid.GetColumnSpan(stack));
                var kids = stack.Children.OfType<UIElement>().ToList();
                stack.Children.Clear();
                foreach (var k in kids)
                {
                    DockPanel.SetDock(k, Dock.Top);
                    var fe = k as FrameworkElement;
                    if (fe != null && k == kids.Last()) fe.VerticalAlignment = VerticalAlignment.Stretch;
                    dock.Children.Add(k);
                }
                int at = g.Children.IndexOf(stack);
                g.Children.RemoveAt(at);
                g.Children.Insert(at, dock);
            }
        }

        static void Collect(DependencyObject d, List<FrameworkElement> all)
        {
            var fe = d as FrameworkElement;
            if (fe != null) all.Add(fe);
            foreach (var child in LogicalTreeHelper.GetChildren(d))
            {
                var c = child as DependencyObject;
                if (c != null) Collect(c, all);
            }
        }

        static ScrollViewer Page(DependencyObject d)
        {
            while (d != null && !(d is ScrollViewer && ((ScrollViewer)d).Name.StartsWith("Page"))) d = LogicalTreeHelper.GetParent(d);
            return d as ScrollViewer;
        }

        static bool Shown(FrameworkElement e, ScrollViewer page)
        {
            for (DependencyObject d = e; d != null; d = LogicalTreeHelper.GetParent(d))
            {
                var u = d as UIElement;
                if (u != null && u.Visibility != Visibility.Visible) return false;
                if (d == page) return true;
            }
            return false;
        }

        static void FitCols(Grid g, List<UIElement> right, List<GridLength> widths)
        {
            bool one = g.ActualWidth > 0 && g.ActualWidth < ColsMinWidth;
            for (int i = 0; i < g.ColumnDefinitions.Count; i++)
            {
                var w = one && i > 0 ? new GridLength(0) : widths[i];
                if (g.ColumnDefinitions[i].Width != w) g.ColumnDefinitions[i].Width = w;
            }
            foreach (var r in right)
            {
                if (Grid.GetColumn(r) != (one ? 0 : 2)) Grid.SetColumn(r, one ? 0 : 2);
                if (Grid.GetRow(r) != (one ? 1 : 0)) Grid.SetRow(r, one ? 1 : 0);
            }
        }

        bool fitting;

        void FitFillsSoon()
        {
            if (fitting) return;
            fitting = true;
            Window.Dispatcher.BeginInvoke(new Action(() => { fitting = false; FitFills(); }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        void FitFills()
        {
            foreach (var g in fills)
            {
                var page = Page(g);
                if (page == null || !Shown(g, page) || page.ViewportHeight <= 0) continue;
                var content = page.Content as FrameworkElement;
                double top = g.TranslatePoint(new Point(0, 0), page).Y + page.VerticalOffset;
                double rows = 0;
                for (int i = 0; i < g.RowDefinitions.Count - 1; i++) rows += g.RowDefinitions[i].ActualHeight;
                double h = Math.Max(page.ViewportHeight - top - (content != null ? content.Margin.Bottom : 0), rows + FillMinList);
                if (double.IsNaN(g.Height) || Math.Abs(g.Height - h) > 0.5) g.Height = h;
            }
        }

        void ShowPage(int page)
        {
            for (int i = 0; i < Pages.Length; i++)
                El<ScrollViewer>("Page" + Pages[i]).Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;

            El<ScrollViewer>("Page" + Pages[page]).BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            El<TextBlock>("PageTitle").Text = Titles[page].ToUpperInvariant();

            shownPage = page;
            El<Button>("BtnStart").Visibility = page == 0 ? Visibility.Collapsed : Visibility.Visible;
            El<Border>("StatePill").Visibility = page == 0 ? Visibility.Collapsed : Visibility.Visible;
            FitStrip();
            FitFillsSoon();
            if (Pages[page] == "Settings") UpdateProblems();
        }

        string optionsOpen;
        readonly List<CheckBox> tiles = new List<CheckBox>();
        readonly List<Tuple<HeaderedContentControl, List<CheckBox>>> groups = new List<Tuple<HeaderedContentControl, List<CheckBox>>>();
        string doingTask;

        void WireTiles()
        {
            var all = new List<FrameworkElement>();
            Collect(Window, all);
            var tileStyle = Window.FindResource("TaskTile");
            foreach (var t in all.OfType<CheckBox>().Where(c => c.Style == tileStyle && Page(c) != null && Page(c).Name == "PageJobs")) tiles.Add(t);
            var groupStyle = Window.FindResource("TaskGroup");
            foreach (var g in all.OfType<HeaderedContentControl>().Where(c => c.Style == groupStyle))
            {
                var inside = new List<FrameworkElement>();
                Collect(g, inside);
                if (inside.OfType<CheckBox>().Any()) groups.Add(Tuple.Create(g, inside.OfType<CheckBox>().ToList()));
            }
            foreach (var tile in all.OfType<CheckBox>().Where(c => c.Uid == "1"))
            {
                var t = tile;
                string key = TileKey(t);
                if (Window.FindName("Opt_" + key) == null) continue;
                t.ApplyTemplate();
                var more = t.Template == null ? null : t.Template.FindName("MoreBtn", t) as Button;
                if (more != null) more.Click += (a, b) => { OpenOptions(key, (string)t.Content, t.Tag as Geometry); b.Handled = true; };
            }
            var layer = El<Grid>("OptionsLayer");
            El<Button>("BtnOptionsClose").Click += (a, b) => CloseOptions();
            layer.MouseLeftButtonDown += (a, b) => { if (b.OriginalSource == layer) CloseOptions(); };
            Window.PreviewKeyDown += (a, b) => { if (b.Key == Key.Escape && optionsOpen != null) { CloseOptions(); b.Handled = true; } };

            El<Button>("BtnStartBig").Click += (a, b) => Toggle();
            Pulse("HomeNowGlow");

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (a, b) => UpdateTileStatus();
            timer.Start();
            Window.Loaded += (a, b) => UpdateTileStatus();
        }

        static void UseDecoFont()
        {
            if (Application.Current == null) return;
            try
            {
                string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IdleMafiaBot-fonts");
                string file = System.IO.Path.Combine(folder, "AbrilFatface-Regular.ttf");
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("IdleMafiaBot.ui.AbrilFatface-Regular.ttf"))
                {
                    if (s == null) return;
                    if (!System.IO.File.Exists(file) || new System.IO.FileInfo(file).Length != s.Length)
                    {
                        System.IO.Directory.CreateDirectory(folder);
                        using (var f = System.IO.File.Create(file)) s.CopyTo(f);
                    }
                }
                Application.Current.Resources["Deco"] = new FontFamily(new Uri(folder + System.IO.Path.DirectorySeparatorChar), "./#Abril Fatface");
            }
            catch (Exception) { }
        }

        internal void ShowMoment(Ping k, string title, string text)
        {
            var box = El<StackPanel>("Toasts");
            string head = Regex.Replace(title ?? "", @"^[^\p{L}\p{N}]+", "").Trim();
            string rarity = new[] { "Forbidden", "Secret", "Mythic", "Legendary", "Epic", "Rare" }.FirstOrDefault(r => head.Contains(r));
            Brush accent = k == Ping.Rarity && rarity != null ? Res("Rarity" + rarity) : k == Ping.Level ? Res("Xp") : Res("Gold");
            string icon = k == Ping.Boss ? "IcoCrown" : k == Ping.Property ? "IcoHome" : k == Ping.Level ? "IcoStar" : "IcoCrew";
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = accent, VerticalAlignment = VerticalAlignment.Top };
            badge.Child = new Path { Data = (Geometry)Window.FindResource(icon), Stroke = Res("OnGold"), StrokeThickness = 2, Width = 20, Height = 20,
                                     Stretch = Stretch.Uniform, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            var words = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
            words.Children.Add(new TextBlock { Text = head, FontFamily = (FontFamily)Window.FindResource("Deco"), FontSize = 19, Foreground = accent, TextTrimming = TextTrimming.CharacterEllipsis });
            words.Children.Add(new TextBlock { Text = text ?? "", FontSize = 13, Foreground = Res("Text"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 0), MaxHeight = 54 });
            Grid.SetColumn(words, 1);
            grid.Children.Add(badge);
            grid.Children.Add(words);
            var bg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            bg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#33291A"), 0));
            bg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#18160F"), 1));
            var slide = new TranslateTransform(380, 0);
            var card = new Border { Background = bg, BorderBrush = accent, BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(12),
                                    Padding = new Thickness(14, 12, 16, 12), Margin = new Thickness(0, 0, 0, 10), Child = grid, RenderTransform = slide, Opacity = 0 };
            box.Children.Insert(0, card);
            while (box.Children.Count > 3) box.Children.RemoveAt(box.Children.Count - 1);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(380, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
            var stay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            stay.Tick += (a, b) =>
            {
                stay.Stop();
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450));
                fade.Completed += (c, d) => box.Children.Remove(card);
                card.BeginAnimation(UIElement.OpacityProperty, fade);
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(60, TimeSpan.FromMilliseconds(450)));
            };
            stay.Start();
        }

        static string TileKey(FrameworkElement t) { return t.Name.StartsWith("sw_") ? t.Name.Substring(3) : t.Name.StartsWith("tile_") ? t.Name.Substring(5) : t.Name; }

        static T Part<T>(Control c, string name) where T : class
        {
            if (c.Template == null) c.ApplyTemplate();
            return c.Template == null ? null : c.Template.FindName(name, c) as T;
        }

        string Shown(string name) { var t = Window.FindName(name) as TextBlock; return t == null ? "" : (t.Text ?? "").Trim(); }

        void UpdateTileStatus()
        {
            foreach (var t in tiles)
            {
                var line = Part<TextBlock>(t, "Status");
                if (line == null) continue;
                string text = t.IsChecked == true ? TileLine(TileKey(t)) : "";
                if (line.Text != text) line.Text = text;
            }
            foreach (var g in groups)
            {
                var count = Part<TextBlock>(g.Item1, "Count");
                if (count == null) continue;
                string text = g.Item2.Count(t => t.IsChecked == true) + " of " + g.Item2.Count + " on";
                if (count.Text != text) count.Text = text;
            }
            GlowTiles();
        }

        string TileLine(string key)
        {
            Func<string, bool> known = v => v.Length > 0 && v != "-" && v != "?";
            switch (key)
            {
                case "Jobs": return known(Shown("JobName")) && !Shown("JobName").EndsWith(" off") ? Shown("JobName") : "";
                case "Heists": return known(Shown("FavorsText")) ? Shown("FavorsText").Replace(" left", "") + " favors" + (Shown("FavorsText").EndsWith(" left") ? " left" : "") : "";
                case "Bosses": return known(Shown("BossName")) ? Shown("BossName") + (Shown("BossWhen").Length > 0 ? ", " + Shown("BossWhen") : "") : "";
                case "Fights": return Shown("TotFights") + " this session";
                case "WhaleHunt": return "Up to " + s.WhaleHitsNow + " hits";
                case "CrewFill": return known(Shown("CrewCount")) ? Shown("CrewCount") + " henchmen" : "";
                case "Reroll": return RollMode();
                case "TrainCrew": return s.TrainOrder == TrainOrder.Evenly ? "Everyone evenly" : "The best first";
                case "FamilyWatch": return Shown("FamName") != "Your family" ? Shown("FamName") : "";
                case "GameStats": { var gs = bot.LastGameStats; return gs != null && gs.Level > 0 ? "Level " + gs.Level + (gs.Respect >= 0 ? ", " + gs.Respect.ToString("N0") + " respect" : "") : ""; }
                case "Takedown": return known(Shown("TdFree")) ? Shown("TdFree") + " free attacks" : "";
                case "GiveStamina": return s.StaminaPerk.Trim().Length == 0 ? "Closest perk" : s.StaminaPerk.Trim();
                case "DefendTerritory": return known(Shown("FamTerritories")) ? Shown("FamTerritories") + " districts" : "";
                case "Properties": return known(Shown("SafeIncome")) ? Shown("SafeIncome") + " an hour" : "";
                case "Bank": return Shown("BankText");
                case "SafehouseUpgrade": return known(Shown("SafeLevel")) ? "Level " + Shown("SafeLevel") : "";
                case "SpendPoints": return Shown("PointsText").Length > 0 ? Shown("PointsText") : "On " + Game.StatNames[Math.Max(0, Math.Min(Game.StatNames.Length - 1, s.PointsStat))];
                case "ShopBuy": { int n = Enumerable.Range(0, 8).Count(i => s.ShopWants(i)); return n + (n == 1 ? " rarity" : " rarities"); }
                case "Operations": return ops.Count > 0 ? ops[0].Left : "";
                default: return "";
            }
        }

        static readonly Dictionary<string, string[]> TaskTiles = new Dictionary<string, string[]>
        {
            { "jobs", new[] { "sw_Jobs" } }, { "learn jobs", new[] { "sw_Jobs" } }, { "heists", new[] { "sw_Heists" } },
            { "bosses", new[] { "sw_Bosses" } }, { "fights", new[] { "sw_Fights" } },
            { "crew", new[] { "sw_CrewFill", "tile_Reroll", "sw_TrainCrew" } }, { "rerolls", new[] { "tile_Reroll" } },
            { "family", new[] { "sw_FamilyWatch" } }, { "game stats", new[] { "sw_GameStats" } }, { "takedown", new[] { "sw_Takedown" } }, { "give stamina", new[] { "sw_GiveStamina" } },
            { "properties", new[] { "sw_Properties" } }, { "bank", new[] { "sw_Bank" } }, { "safehouse upgrade", new[] { "sw_SafehouseUpgrade" } },
            { "skill points", new[] { "sw_SpendPoints" } }, { "playtime rewards", new[] { "sw_Playtime" } }, { "contracts", new[] { "sw_Contracts" } },
            { "operations", new[] { "sw_Operations" } }, { "briefcases", new[] { "sw_Briefcases" } }, { "events", new[] { "sw_Events" } },
            { "gear", new[] { "sw_EquipBest", "sw_Crates", "sw_ShopBuy" } }, { "gear back", new[] { "sw_EquipBest" } },
        };

        void GlowTiles()
        {
            string[] lit;
            if (doingTask == null || !TaskTiles.TryGetValue(doingTask, out lit)) lit = new string[0];
            foreach (var t in tiles)
            {
                var glow = Part<Border>(t, "Glow");
                if (glow == null) continue;
                bool on = lit.Contains(t.Name) && t.IsChecked == true;
                if (on == (glow.Visibility == Visibility.Visible)) continue;
                glow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                glow.BeginAnimation(UIElement.OpacityProperty, on
                    ? new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
                    : null);
            }
        }

        internal void OpenOptions(string key, string title, Geometry icon)
        {
            foreach (var p in El<StackPanel>("OptionsBody").Children.OfType<FrameworkElement>())
                p.Visibility = p.Name == "Opt_" + key ? Visibility.Visible : Visibility.Collapsed;
            El<TextBlock>("OptionsTitle").Text = title;
            El<Path>("OptionsIcon").Data = icon;
            El<ScrollViewer>("OptionsScroll").ScrollToTop();
            var layer = El<Grid>("OptionsLayer");

            var group = groups.FirstOrDefault(g => g.Item2.Any(t => TileKey(t) == key));
            if (group != null && group.Item1.Resources.Contains("TileAccent")) layer.Resources["TileAccent"] = group.Item1.Resources["TileAccent"];
            else layer.Resources.Remove("TileAccent");
            layer.Visibility = Visibility.Visible;
            layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            var slide = new TranslateTransform(0, 18);
            El<Border>("OptionsPanel").RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            optionsOpen = key;
            if (key == "Jobs") { jobsDirty = true; RefreshJobs(); }
            if (key == "Fights") RefreshBigPayers();
        }

        internal void CloseOptions()
        {
            optionsOpen = null;
            var layer = El<Grid>("OptionsLayer");
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
            fade.Completed += (a, b) => { if (optionsOpen == null) layer.Visibility = Visibility.Collapsed; };
            layer.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        void WireSettings()
        {
            foreach (var f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var field = f;
                var sw = Window.FindName("sw_" + f.Name) as CheckBox;
                if (sw != null && f.FieldType == typeof(bool))
                {
                    sw.IsChecked = (bool)f.GetValue(s);
                    sw.Checked += (a, b) => Set(field, true);
                    sw.Unchecked += (a, b) => Set(field, false);
                }
                var holder = Window.FindName("num_" + f.Name) as ContentControl;
                if (holder != null && f.FieldType == typeof(int)) holder.Content = Stepper(field, (string)holder.Tag);

                var values = f.FieldType.IsEnum ? Enum.GetNames(f.FieldType).Select(n => (object)Enum.Parse(f.FieldType, n))
                           : f.FieldType == typeof(int) ? Enumerable.Range(0, 10).Select(n => (object)n) : Enumerable.Empty<object>();
                foreach (var v in values)
                {
                    var value = v;
                    var seg = Window.FindName("seg_" + f.Name + "_" + v) as RadioButton;
                    if (seg == null) continue;
                    seg.IsChecked = Equals(f.GetValue(s), v);
                    seg.Checked += (a, b) => Set(field, value);
                }
            }

            Combo("cmb_PointsStat", Game.StatNames, s.PointsStat, i => s.PointsStat = i);

            perkChoices = new List<string> { ClosestPerk };
            perkChoices.AddRange(Game.StaminaPerkNames);
            if (s.StaminaPerk.Trim().Length > 0 && !perkChoices.Any(n => Game.SamePerk(n, s.StaminaPerk))) perkChoices.Add(s.StaminaPerk.Trim());
            int perk = s.StaminaPerk.Trim().Length == 0 ? 0 : perkChoices.FindIndex(n => n != ClosestPerk && Game.SamePerk(n, s.StaminaPerk));
            Combo("cmb_StaminaPerk", perkChoices, Math.Max(0, perk), i => s.StaminaPerk = i == 0 ? "" : perkChoices[i]);

            s.CrewRerollStats = s.CrewRerollStats > 0 ? Settings.Perfect : 0;
            goalValues = Settings.StatsGoals.ToList();
            var goalNames = Settings.StatsGoalNames.ToList();
            Combo("cmb_CrewRerollStats", goalNames, goalValues.IndexOf(s.CrewRerollStats), i => s.CrewRerollStats = goalValues[i]);

            whaleValues = Settings.WhaleAmounts.ToList();
            if (!whaleValues.Contains(s.WhaleCash)) { whaleValues.Add(s.WhaleCash); whaleValues.Sort(); }
            Combo("cmb_WhaleCash", whaleValues.Select(WhaleName).ToList(), whaleValues.IndexOf(s.WhaleCash), i => s.WhaleCash = whaleValues[i]);

            payerValues = Settings.WhaleAmounts.ToList();
            if (!payerValues.Contains(s.BigPayerCash)) { payerValues.Add(s.BigPayerCash); payerValues.Sort(); }
            Combo("cmb_BigPayerCash", payerValues.Select(PayerName).ToList(), payerValues.IndexOf(s.BigPayerCash), i => s.BigPayerCash = payerValues[i]);

            rollValues = Settings.AutoRollLimits.ToList();
            if (!rollValues.Contains(s.AutoRollLimit)) { rollValues.Add(s.AutoRollLimit); rollValues.Sort(); }
            Combo("cmb_AutoRollLimit", rollValues.Select(RollLimitName).ToList(), rollValues.IndexOf(s.AutoRollLimit), i => s.AutoRollLimit = rollValues[i]);

            Combo("cmb_StartHeist", new[] { "Off" }.Concat(Game.HeistNames).ToList(), HeistChoice(),
                  i => { s.StartHeists = i > 0; if (i > 0) s.StartHeist = Game.HeistNames[i - 1]; });

            El<HeaderedContentControl>("MarketCard").Visibility = Settings.MarketRelistShown ? Visibility.Visible : Visibility.Collapsed;

            var whens = new[] { "Problems", "Rarity", "Property", "Summary", "Level", "Boss" }.Select(n => El<CheckBox>("sw_Discord" + n)).ToList();
            Action count = () => El<TextBlock>("DiscordWhenCount").Text = whens.Count(c => c.IsChecked == true) + " of " + whens.Count + " on";
            foreach (var c in whens) { c.Checked += (a, b) => count(); c.Unchecked += (a, b) => count(); }
            count();
            UpdateDerived();
        }

        List<int> goalValues;
        List<long> whaleValues;
        List<long> payerValues;
        List<long> rollValues;

        static string RollLimitName(long v) { return "Stop a slot at " + SafehouseInfo.Money(v) + " spent"; }

        static string WhaleName(long v) { return "A win of " + SafehouseInfo.Money(v) + " or more"; }

        static string PayerName(long v) { return "Paid " + SafehouseInfo.Money(v) + " or more in a win"; }

        void UsePayers(FightMemory m)
        {
            if (payers == m) return;
            if (payers != null) payers.Changed -= PayersChanged;
            payers = m;
            if (payers != null) payers.Changed += PayersChanged;
            RefreshBigPayers();
        }

        void PayersChanged() { Ui(RefreshBigPayers); }

        void RefreshBigPayers()
        {
            if (payers == null) return;
            var now = DateTime.UtcNow;
            var list = payers.BigPayers(s.BigPayerCash, now);
            payerRows.Clear();
            bool odd = false;
            foreach (var p in list)
            {
                string name = p.Name;
                payerRows.Add(new BigPayerView
                {
                    Name = name, Biggest = Money(p.Biggest), Last = p.Last >= 0 ? Money(p.Last) : "-", When = Ago(p.LastBig, now),
                    Record = p.Won + "W " + p.Lost + "L",
                    RecordTip = p.Fights + (p.Fights == 1 ? " fight" : " fights") + " in the last 3 days (the last 10 are kept): " + p.Won + " won, " + p.Lost + " lost",
                    RowBg = odd ? Res("Panel2") : Brushes.Transparent,
                    Forget = new RelayCommand(() => ForgetPayer(name)),
                });
                odd = !odd;
            }
            bool none = payerRows.Count == 0;
            El<TextBlock>("BigPayersEmpty").Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            El<Grid>("BigPayersHead").Visibility = none ? Visibility.Collapsed : Visibility.Visible;
            int n = payers.Count;
            El<TextBlock>("BigPayersRead").Text = n == 0 ? "" : n + (n == 1 ? " player" : " players") + " remembered";
        }

        void ForgetPayer(string name)
        {
            if (payers == null || !payers.Forget(name)) { RefreshBigPayers(); return; }
            if (payers == bot.Memory) bot.Log("Fights: forgot " + name + " (Big payers list)");

        }

        internal static string Ago(DateTime utc, DateTime nowUtc)
        {
            var d = nowUtc - utc;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + " h ago";
            return utc.ToLocalTime().ToString("dd-MM HH:mm", CultureInfo.InvariantCulture);
        }

        void SyncRoll()
        {
            var seg = Window.FindName("seg_CrewRollType_" + s.CrewRollType) as RadioButton;
            if (seg != null && seg.IsChecked != true) seg.IsChecked = true;
        }

        void Combo(string name, IList<string> items, int index, Action<int> set)
        {
            var c = El<ComboBox>(name);
            foreach (var it in items) c.Items.Add(it);
            if (items.Count > 0) c.SelectedIndex = Math.Max(0, Math.Min(items.Count - 1, index));
            c.SelectionChanged += (a, b) => { if (c.SelectedIndex >= 0) { set(c.SelectedIndex); Save(); UpdateDerived(); } };
        }

        FrameworkElement Stepper(FieldInfo f, string tag)
        {
            var p = (tag ?? "0,100,1,").Split(',');
            int min = int.Parse(p[0]), max = int.Parse(p[1]), step = int.Parse(p[2]);
            string suffix = p.Length > 3 && p[3].Length > 0 ? " " + p[3] : "";

            f.SetValue(s, Math.Max(min, Math.Min(max, (int)f.GetValue(s))));
            var value = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 14.5, MinWidth = 70, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Action show = () => value.Text = ((int)f.GetValue(s)).ToString(CultureInfo.InvariantCulture) + suffix;
            Action<int> change = d => { Set(f, Math.Max(min, Math.Min(max, (int)f.GetValue(s) + d))); show(); };
            var minus = new RepeatButton { Content = "−", Style = (Style)Window.FindResource("StepBtn") };
            var plus = new RepeatButton { Content = "+", Style = (Style)Window.FindResource("StepBtn") };
            minus.Click += (a, b) => change(-step);
            plus.Click += (a, b) => change(step);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(value, 1);
            Grid.SetColumn(plus, 2);
            grid.Children.Add(minus);
            grid.Children.Add(value);
            grid.Children.Add(plus);
            var box = new Border
            {
                Background = Res("Field"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10), Height = 40, Padding = new Thickness(3), Child = grid, Margin = new Thickness(16, 0, 0, 0),
                ToolTip = "Click or hold. The mouse wheel works after a click, or with Ctrl held.",
            };

            box.MouseWheel += (a, b) =>
            {
                if (!box.IsKeyboardFocusWithin && (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                change(b.Delta > 0 ? step : -step);
                b.Handled = true;
            };
            show();
            return box;
        }

        void Set(FieldInfo f, object value)
        {
            bool themeChanged = f.Name == "Theme" && !Equals(f.GetValue(s), value);
            f.SetValue(s, value);
            Save();
            UpdateDerived();
            if (themeChanged) SwitchTheme((ThemeChoice)value);
        }

        void SwitchTheme(ThemeChoice t)
        {
            bot.Log("Appearance: " + (t == ThemeChoice.Halloween ? "Halloween 🎃" : "Mafia") + " - restarting to apply it"
                    + (bot.Running && !bot.CheckOrScan ? ", it plays on" : ""));
            StartAgain("close it and start it again to see the new look");
        }

        void Save()
        {
            try { s.Save(System.IO.Path.Combine(dir, "IdleMafiaBot.ini")); } catch (Exception e) { SaveFailed(e); }

            if (s.Account.Length > 0)
                try { System.IO.Directory.CreateDirectory(Accounts.Folder(dir, s.Account)); s.Save(Accounts.Ini(dir, s.Account), true); } catch (Exception e) { SaveFailed(e); }
        }

        string saveFailed;

        void SaveFailed(Exception e)
        {
            if (e.Message == saveFailed) return;
            saveFailed = e.Message;
            bot.Log("Couldn't save the settings: " + e.Message);
        }

        void UpdateDerived()
        {
            RefreshJobCard();
            jobsDirty = true;
            if (optionsOpen == "Jobs") RefreshJobs();
            El<TextBlock>("PerkHint").Text = s.StaminaPerk.Trim().Length == 0
                ? "The perk closest to its next level gets it."
                : "Always " + s.StaminaPerk.Trim() + ", until it's maxed.";
            ShowFamily(null);
            SyncRoll();
            ShowCrewOdds();
            SyncRollMode();

            var names = new[] { "Common", "Uncommon", "Rare", "Epic", "Legendary", "Mythic", "Secret", "Forbidden" };
            var off = names.Where((n, i) => !s.ShopWants(i)).ToList();
            El<TextBlock>("ShopRaritiesHint").Text = off.Count == 0 ? "All of them. Click one off to leave it in the shop, for example to save cash."
                : off.Count == names.Length ? "None: the shop buys nothing."
                : "Leaves " + (off.Count == 1 ? off[0] : string.Join(", ", off.Take(off.Count - 1)) + " and " + off.Last()) + " in the shop.";

            foreach (var n in names) El<CheckBox>("sw_Shop" + n).IsEnabled = s.ShopBuy;
            El<ComboBox>("cmb_WhaleCash").IsEnabled = s.WhaleHunt;

            El<CheckBox>("sw_CrewAutoRoll").IsEnabled = s.CrewReroll;
            El<ComboBox>("cmb_AutoRollLimit").IsEnabled = s.CrewReroll && s.CrewAutoRoll;

            El<Border>("LuckRollBar").IsEnabled = s.Events;
            El<Border>("LuckRollBar").Opacity = s.Events ? 1 : 0.45;

            var hits = El<ContentControl>("num_WhaleHits");
            hits.IsEnabled = s.WhaleHunt;
            hits.Opacity = s.WhaleHunt ? 1 : 0.45;
            int whaleHits = s.WhaleHitsNow;
            El<TextBlock>("WhaleHint").Text = whaleHits == 1 ? "One hit only: nobody is hit again after a big win." : "Hits a player again after a big win, up to " + whaleHits + " times.";
            RefreshBigPayers();
            foreach (var r in new[] { Rarity.Mythic, Rarity.Secret, Rarity.Forbidden })
                El<CheckBox>("sw_ShopBank" + r).IsEnabled = s.ShopBuy && s.ShopWithdraw && s.ShopWants((int)r);
            if (crewSeen != null) ShowCrew(crewSeen);
            if (ReviewShowing) RefreshReview();
        }

        void ShowCrewOdds()
        {
            var ic = CultureInfo.InvariantCulture;
            var odds = El<WrapPanel>("CrewOdds");
            odds.Children.Clear();
            int roll = (int)s.CrewRollType;
            for (int r = 7; r >= 0; r--)
                if (CrewInfo.Odds[roll, r] > 0) odds.Children.Add(RarityChip(r, Game.RarityTitle(r) + " " + CrewInfo.Odds[roll, r].ToString("0.##", ic) + "%"));
            int best = Game.BestRoll(s.CrewRollType);

            El<TextBlock>("CrewRollHint").Text = "Until everyone is " + Game.RarityTitle(best) + ", the best " + s.CrewRollType + " hires can reach."
                + (best < 7 ? " Only Elite can reach Forbidden." : "");
        }

        bool syncingRoll;

        void WireRollMode()
        {
            foreach (var mode in new[] { "Off", "On", "First", "Only" })
            {
                string m = mode;
                El<RadioButton>("RollMode_" + m).Checked += (a, b) => RollModePicked(m);
            }

            var tile = El<CheckBox>("tile_Reroll");
            tile.Checked += (a, b) => { if (!syncingRoll && RollMode() == "Off") El<RadioButton>("RollMode_On").IsChecked = true; };
            tile.Unchecked += (a, b) => { if (!syncingRoll) El<RadioButton>("RollMode_Off").IsChecked = true; };
        }

        void RollModePicked(string m)
        {
            if (syncingRoll) return;
            if (m == "Only")
            {
                if (Covered) { SyncRollMode(); return; }
                bot.StartRerollOnly();
                ShowState("Working: only rerolling the crew");
                return;
            }
            if (bot.RerollOnly) { bot.StopRerollOnly(); ShowState("Stopping"); }
            s.CrewReroll = m != "Off";
            s.RerollsFirst = m == "First";
            Save();
            UpdateDerived();
        }

        string RollMode() { return bot.RerollOnly ? "Only" : !s.CrewReroll ? "Off" : s.RerollsFirst ? "First" : "On"; }

        void SyncRollMode()
        {
            string m = RollMode();
            var rb = El<RadioButton>("RollMode_" + m);
            var tile = El<CheckBox>("tile_Reroll");
            if (rb.IsChecked != true || tile.IsChecked != (m != "Off"))
            {
                syncingRoll = true;
                try { rb.IsChecked = true; tile.IsChecked = m != "Off"; } finally { syncingRoll = false; }
            }
            string then = s.CrewRerollStats > 0 ? ", then their own attack and defense" : "";
            El<TextBlock>("RollModeHint").Text =
                  m == "Off" ? "No rerolls."
                : m == "On" ? "In between the other tasks."
                : m == "First" ? "Before the other tasks. Full bars, the bank and the shop still get their turn. Back to On once the crew is done."
                : "Nothing else: rerolls with the cash on hand until the crew is done" + then + ", then the bot stops.";
        }

        CrewInfo crewSeen;

        void ShowCrew(CrewInfo ci)
        {
            crewSeen = ci;
            var ic = CultureInfo.InvariantCulture;
            El<TextBlock>("CrewWhen").Text = ci == null ? "" : "read at " + ci.At.ToString("HH:mm");
            El<TextBlock>("CrewCount").Text = ci == null || ci.Henchmen < 0 ? "-" : ci.Henchmen + (ci.Slots > 0 ? " / " + ci.Slots : "");
            El<TextBlock>("CrewAttack").Text = ci == null || ci.Attack < 0 ? "-" : "+" + ci.Attack.ToString("N0", ic);
            El<TextBlock>("CrewDefense").Text = ci == null || ci.Defense < 0 ? "-" : "+" + ci.Defense.ToString("N0", ic);
            El<TextBlock>("CrewSlot").Text = ci == null || ci.SlotPrice <= 0 ? "-" : Money(ci.SlotPrice);

            var bar = El<Grid>("CrewBar");
            bar.Children.Clear();
            bar.ColumnDefinitions.Clear();
            var chips = El<WrapPanel>("CrewChips");
            chips.Children.Clear();
            int total = ci == null ? 0 : ci.ByRarity.Sum();
            if (total == 0)
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = Res("Track") });
            else
                for (int r = 7; r >= 0; r--)
                {
                    int n = ci.ByRarity[r];
                    if (n == 0) continue;
                    bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(n, GridUnitType.Star) });
                    var block = new Border { CornerRadius = new CornerRadius(5), Background = Res(RarityBrushes[r]), Margin = new Thickness(0, 0, 3, 0), ToolTip = n + " " + Game.RarityTitle(r) };
                    Grid.SetColumn(block, bar.ColumnDefinitions.Count - 1);
                    bar.Children.Add(block);
                    chips.Children.Add(RarityChip(r, n + " " + Game.RarityTitle(r)));
                }

            string status = ci == null ? "Not read yet. The bot looks at your crew every 30 minutes while it runs, every 6 hours with all its tiles off." : ci.Status;
            if (ci != null && status.Length == 0) status = s.CrewReroll ? "Nothing to do right now." : "Rerolls are off. Pick On below to reroll the worst henchman.";
            if (ci != null && s.CrewReroll && ci.Worst.Length > 0 && !status.StartsWith("Rerolling")) status += " The worst now: " + ci.Worst + ".";
            El<TextBlock>("CrewStatus").Text = status;
            UpdateTotals();
        }

        FrameworkElement RarityChip(int rarity, string text)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6) };
            p.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Res(RarityBrushes[rarity]), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) });
            p.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = Res("Text2"), VerticalAlignment = VerticalAlignment.Center });
            return p;
        }

        void WireDiscord()
        {
            var box = El<PasswordBox>("DiscordLinkBox");
            var test = El<Button>("BtnDiscordTest");
            box.Password = s.DiscordLink ?? "";
            Action show = () =>
            {
                string t = box.Password.Trim(), saved = (s.DiscordLink ?? "").Trim();
                El<TextBlock>("DiscordLinkHint").Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (t.Length == 0) DiscordSays("Off. Nothing is sent without a link.", null);
                else if (t == (s.StatsLink ?? "").Trim()) DiscordSays(Discord.StatsLinkHere, "Red");
                else if (Discord.IsLink(t))
                {
                    string trouble = t == saved ? bot.Discord.Trouble : null;
                    if (trouble != null) DiscordSays(trouble, "Red");
                    else DiscordSays("On. Messages go to your channel. Send a test to check it.", "Green");
                }
                else if (saved.Length > 0) DiscordSays("That isn't a Discord webhook link. The link saved before stays until you paste a new one or press Remove.", "Red");
                else DiscordSays("That isn't a Discord webhook link, so it's off. A link starts with https://discord.com/api/webhooks/", "Red");
            };
            show();
            bot.Discord.Changed += () => Ui(show);
            box.PasswordChanged += (a, b) =>
            {

                string t = box.Password.Trim();
                if ((t.Length == 0 || Discord.IsLink(t)) && t != (s.DiscordLink ?? "") && (t.Length == 0 || t != (s.StatsLink ?? "").Trim()))
                {
                    s.DiscordLink = t;
                    Save();
                    Accounts.ForgetLink(dir, s.Account);
                }
                show();
            };
            test.Click += (a, b) =>
            {
                test.IsEnabled = false;
                DiscordSays("Sending...", null);
                bot.Discord.Test(box.Password, Discord.ShotOfRoblox, why => Ui(() =>
                {
                    test.IsEnabled = true;
                    if (why.Length == 0) { DiscordSays("Sent. Look in your Discord channel.", "Green"); bot.Log("Discord: a test message sent"); }
                    else DiscordSays(why, "Red");
                }));
            };
            El<Button>("BtnDiscordClear").Click += (a, b) => box.Password = "";
        }

        void DiscordSays(string text, string brush)
        {
            var t = El<TextBlock>("DiscordStatus");
            t.Text = text;
            t.Foreground = Res(brush ?? "Text2");
        }

        void WireStats()
        {
            var box = El<PasswordBox>("StatsLinkBox");
            box.Password = s.StatsLink ?? "";
            var hint = El<TextBlock>("StatsHint");
            string plain = hint.Text;
            Action show = () =>
            {
                string t = box.Password.Trim(), saved = (s.StatsLink ?? "").Trim();

                hint.Text = t == saved && bot.Stats.Family
                    ? "This is your family's link. Your level, cash and crew go to your family's Discord, with your Roblox name, the heists you start, big wins and Mythic or better shop items, each with a picture of just that window."
                    : plain;
                El<TextBlock>("StatsLinkHint").Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (t.Length == 0) StatsSays("Off. Nothing is sent without a link.", null);
                else if (t == (s.DiscordLink ?? "").Trim()) StatsSays("That's the link of your own Discord messages (above). Type /link in our Discord and paste the link it gives you here.", "Red");
                else if (StatsCard.IsLink(t))
                {
                    string trouble = t == saved ? bot.Stats.Trouble : null;
                    if (trouble != null) StatsSays(trouble, "Red");
                    else StatsSays(bot.Stats.Status, "Green");
                }
                else if (saved.Length > 0) StatsSays("That isn't the link from /link. The link saved before stays until you paste a new one or press Remove.", "Red");
                else StatsSays("That isn't the link from /link, so it's off. Type /link in our Discord to get yours.", "Red");
            };
            show();
            bot.Stats.Changed += () => Ui(show);
            box.PasswordChanged += (a, b) =>
            {
                string t = box.Password.Trim(), old = (s.StatsLink ?? "").Trim();
                if ((t.Length == 0 || StatsCard.IsLink(t)) && t != old && (t.Length == 0 || t != (s.DiscordLink ?? "").Trim()))
                {
                    s.StatsLink = t;
                    if (old.Length > 0) bot.Stats.Removed(old);
                    Save();
                    if (t.Length > 0) bot.Stats.Wake();
                }
                show();
            };
            El<Button>("BtnStatsClear").Click += (a, b) => box.Password = "";
            var preview = El<Border>("StatsPreviewBox");
            El<Button>("BtnStatsPreview").Click += (a, b) =>
            {
                bool open = preview.Visibility != Visibility.Visible;
                if (open) El<TextBox>("StatsPreview").Text = bot.Stats.Preview();
                preview.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                El<TextBlock>("StatsPreviewLabel").Text = open ? "Hide it" : "What it sends";
            };
        }

        void StatsSays(string text, string brush)
        {
            var t = El<TextBlock>("StatsStatus");
            t.Text = text;
            t.Foreground = Res(brush ?? "Text2");
        }

        void WireButtons()
        {
            El<Button>("BtnStart").Click += (a, b) => Toggle();
            WireRollMode();

            El<Button>("BtnNeverList").Click += (a, b) =>
            {
                var list = El<StackPanel>("NeverList");
                bool show = list.Visibility != Visibility.Visible;
                list.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                El<TextBlock>("NeverListText").Text = show ? "Hide the list" : "Show the full list";
            };
            El<Button>("BtnLog").Click += (a, b) => Open(System.IO.Path.Combine(dir, "IdleMafiaBot.log"));
            El<Button>("BtnProblems").Click += (a, b) =>
            {
                var p = System.IO.Path.Combine(dir, "problems");
                System.IO.Directory.CreateDirectory(p);
                Open(p);
            };
            El<Button>("BtnMin").Click += (a, b) => Window.WindowState = WindowState.Minimized;
            El<Button>("BtnClose").Click += (a, b) => { closedBy = "you"; Window.Close(); };

            El<Button>("BtnReport").Click += (a, b) => MakeReport();
            El<Button>("BtnUpdate").Click += (a, b) => UpdateClicked();
            WireDiscord();
            WireStats();

            El<Button>("BtnDiscord").Click += (a, b) => Open(DiscordLink);
            El<Button>("BtnDiscordTop").Click += (a, b) => Open(DiscordLink);
            El<Button>("BtnWelcomeDiscord").Click += (a, b) => Open(DiscordLink);
            El<Button>("BtnStar").Click += (a, b) => Open(GitHubPage);
            El<Button>("BtnDownloads").Click += (a, b) => Open(DownloadPage);
            El<Button>("BtnReportHome").Click += (a, b) => MakeReport();
            El<Button>("BtnOcrSettings").Click += (a, b) => Open("ms-settings:regionlanguage");
            El<Button>("BtnWelcomeOk").Click += (a, b) => { s.WelcomeSeen = true; Save(); ShowWelcome(false); UpdateCheckCard(); };
            El<Button>("BtnRulesAgree").Click += (a, b) => { bot.Log("Agreed to the guidelines (version " + Program.Version + ")"); ShowRules(false); UpdateCheckCard(); };
            El<Button>("BtnRulesClose").Click += (a, b) => { closedBy = "you (didn't agree to the guidelines)"; Window.Close(); };

            El<Button>("BtnCheck").Click += (a, b) => StartCheck();
            El<Button>("BtnCheckNow").Click += (a, b) => { MarkOffered(); StartCheck(); };
            El<Button>("BtnCheckLater").Click += (a, b) => PutOffCheck();
            El<Button>("BtnReview").Click += (a, b) => { if (profile != null) ShowReview(true); };
            El<Button>("BtnReviewClose").Click += (a, b) => ShowReview(false);
            El<Button>("BtnKeepSettings").Click += (a, b) => { if (changeRows.Count > 0) bot.Log("Kept your settings after the game check"); ShowReview(false); };
            El<Button>("BtnUseSettings").Click += (a, b) => UseProposals();
        }

        const string DiscordLink = "https://discord.gg/xw56YcDFyg", GitHubPage = "https://github.com/geenusername/IdleMafiaBot",
                     DownloadPage = GitHubPage + "/releases";

        static void Open(string path)
        {
            try { System.Diagnostics.Process.Start(path); } catch (Exception) { }
        }

        void Toggle()
        {
            if (!bot.Running && Covered) return;
            if (bot.Running) { bot.Stop(); ShowState("Stopping"); }
            else { bot.Start(); ShowState("Starting"); }
        }

        internal static bool ClosedMessage(bool wasRunning, string closedBy)
        {
            string by = closedBy ?? "";
            return wasRunning && !by.StartsWith("you") && !by.StartsWith("--exit");
        }

        string closedBy;
        internal bool Closing;
        readonly string[] args;
        DateTime shownAt = DateTime.MaxValue;

        internal bool RestartAfterDrawingFailed(Exception e)
        {
            if (Closing || args.Any(a => a == "--once" || a == "--scan" || a == "--check" || a == "--exit" || a == "--demo")) return false;
            if (shownAt == DateTime.MaxValue || DateTime.UtcNow - shownAt < TimeSpan.FromMinutes(2)) return false;
            bot.Log("The window stopped drawing (a problem in Windows' graphics) - starting the bot again"
                    + (!bot.Running || bot.CheckOrScan ? "" : bot.RerollOnly ? ", it goes on only rerolling" : ", it plays on"));
            bot.LogDetails(e.ToString());
            return StartAgain("it plays on behind a window that doesn't draw");
        }

        string startError;

        bool StartAgain(string otherwise)
        {

            bool playing = bot.Running && !bot.CheckOrScan, onlyReroll = playing && bot.RerollOnly;
            Closing = true;
            bot.Stop();
            for (var until = DateTime.UtcNow.AddSeconds(15); bot.Running && DateTime.UtcNow < until; ) System.Threading.Thread.Sleep(100);
            Save();
            bot.Discord.Flush(2000);
            var again = args.Where(a => a != "--start" && a != "--rerollonly" && a != "--restarted").ToList();
            if (playing) again.Add(onlyReroll ? "--rerollonly" : "--start");
            again.Add("--restarted");
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Assembly.GetEntryAssembly().Location,
                    string.Join(" ", again.Select(a => "\"" + a + "\""))) { UseShellExecute = false, WorkingDirectory = dir });
            }
            catch (Exception x)
            {
                startError = x.Message;
                bot.Log("Couldn't start the bot again (" + x.Message + ") - " + otherwise);
                Closing = false;
                if (onlyReroll) bot.StartRerollOnly();
                else if (playing) bot.Start();
                return false;
            }

            System.Diagnostics.Process.GetCurrentProcess().Kill();
            return true;
        }

        Updater.Release update;

        void UpdateClicked()
        {
            var btn = El<Button>("BtnUpdate");
            var status = El<TextBlock>("UpdateStatus");
            var text = El<TextBlock>("UpdateText");
            if (update == null)
            {
                btn.IsEnabled = false;
                text.Text = "Checking...";
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    Updater.Release r = null;
                    string error = null;
                    try { r = Updater.Check(); } catch (Exception e) { error = e.Message; }
                    Ui(() =>
                    {
                        btn.IsEnabled = true;
                        text.Text = "Check for updates";
                        if (r == null) { status.Text = "Couldn't check: " + error + "."; bot.Log("Update: couldn't check GitHub (" + error + ")"); return; }
                        if (!Updater.Newer(r)) { status.Text = "You have the latest version (" + Program.Version + ")."; bot.Log("Update: checked GitHub - " + Program.Version + " is the latest"); return; }
                        update = r;
                        text.Text = "Update to " + r.Tag;
                        El<Border>("UpdateNew").Visibility = Visibility.Visible;
                        status.Text = "Idle Mafia Bot " + r.Tag + " is out. Press Update: the bot downloads it from GitHub, checks it, puts it in place of this one and starts again. Your settings stay.";
                        bot.Log("Update: Idle Mafia Bot " + r.Tag + " is out (you have " + Program.Version + ")");
                    });
                });
                return;
            }

            if (Program.InTempFolder(dir))
            {
                status.Text = "Unzip the bot first (right-click the zip, Extract All) and start it from that folder, then update.";
                bot.Log("Update: not updated - the bot runs from a temporary folder (inside the zip?): unzip it first");
                return;
            }
            var r2 = update;
            btn.IsEnabled = false;
            text.Text = "Downloading...";
            string exe = Assembly.GetEntryAssembly().Location;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = Updater.Install(r2, exe);
                Ui(() =>
                {
                    if (error != null)
                    {
                        btn.IsEnabled = true;
                        text.Text = "Update to " + r2.Tag;
                        status.Text = "Not updated: " + error + ".";
                        bot.Log("Update: not updated to " + r2.Tag + " - " + error);
                        return;
                    }
                    bot.Log("Update: " + r2.Tag + " downloaded and checked - starting the new version");
                    status.Text = "Starting " + r2.Tag + "...";
                    if (StartAgain("the old version goes back in its place")) return;

                    string undo = Updater.Undo(exe);
                    bool blocked = startError != null && startError.IndexOf("Application Control", StringComparison.OrdinalIgnoreCase) >= 0;
                    btn.IsEnabled = true;
                    text.Text = "Update to " + r2.Tag;
                    if (undo == null)
                    {
                        status.Text = (blocked ? "Windows blocked " + r2.Tag + " from starting (Smart App Control: it stops new programs it doesn't know yet)"
                                               : r2.Tag + " didn't start (" + startError + ")")
                                      + ", so the update was undone - you're still on " + Program.Version + ". Try again later.";
                        bot.Log("Update: " + (blocked ? "Windows blocked " + r2.Tag + " from starting" : r2.Tag + " didn't start") + " (" + startError + ") - the update is undone, "
                                + Program.Version + " plays on");
                    }
                    else
                    {
                        status.Text = r2.Tag + " is in place but didn't start (" + startError + "), and the old version couldn't be put back: " + undo + ".";
                        bot.Log("Update: " + r2.Tag + " didn't start and couldn't be undone - " + undo);
                    }
                });
            });
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && wParam.ToInt32() == 1) { Toggle(); handled = true; }
            if (msg == 0x0112 && (wParam.ToInt64() & 0xFFF0) == 0xF060) closedBy = "you";
            return IntPtr.Zero;
        }

        void ShowState(string st)
        {
            bool running = bot.Running && st != "Stopped";
            string lower = st.ToLowerInvariant();
            Brush dot = !running ? Res("Text3")
                      : lower.Contains("roblox") || lower.Contains("can't read") || lower.Contains("problem") ? Res("Red")
                      : lower.Contains("using the mouse") ? Res("Gold") : Res("Green");
            El<Ellipse>("StateDot").Fill = dot;
            El<Ellipse>("RunLamp").Fill = running ? Res("Green") : Res("Text3");
            El<Ellipse>("RunGlow").Fill = running ? Res("Green") : Res("Text3");

            SyncRollMode();
            string shortText = !running ? (st == "Scanning" || st == "Checking" ? st : "Stopped")
                             : bot.RerollOnly && !lower.Contains("using the mouse") ? "Rerolling"
                             : lower.StartsWith("working") ? "Working"
                             : lower.StartsWith("checking") ? "Checking"
                             : lower.Contains("using the mouse") ? "Waiting for you"
                             : lower.Contains("nothing to do") ? "Running" : "Running";
            El<TextBlock>("StateText").Text = shortText;
            stripRunning = running;
            FitStrip();
            El<TextBlock>("NowText").Text = Friendly(st);

            var btn = El<Button>("BtnStart");
            btn.Background = running ? Res("Red") : Res("Gold");
            El<Path>("BtnStartIcon").Data = (Geometry)Window.FindResource(running ? "IcoStop" : "IcoPlay");
            El<TextBlock>("BtnStartText").Text = running ? "Stop" : "Start";

            var big = El<Button>("BtnStartBig");
            big.Background = running ? Res("Red") : Res("Gold");
            El<Path>("BtnStartBigIcon").Data = (Geometry)Window.FindResource(running ? "IcoStop" : "IcoPlay");
            El<TextBlock>("BtnStartBigText").Text = running ? "Stop" : "Start";
            El<TextBlock>("HomeNowTitle").Text = running ? shortText : "Stopped";
            El<TextBlock>("HomeNow").Text = running ? Friendly(st) : "Press Start and the bot plays for you.";
            El<Ellipse>("HomeNowDot").Fill = dot;
            El<Ellipse>("HomeNowGlow").Fill = dot;

            doingTask = null;
            if (running && st.StartsWith("Working: "))
            {
                string w = st.Substring(9);
                int next = w.IndexOf(". Next", StringComparison.Ordinal);
                doingTask = (next >= 0 ? w.Substring(0, next) : w.Split(',')[0]).Trim().ToLowerInvariant();
            }
            GlowTiles();
            UpdateTotals();
        }

        int shownPage;
        bool stripRunning;

        void FitStrip()
        {
            bool show = stripRunning && (shownPage != 0 || El<TextBlock>("StuckText").Visibility == Visibility.Visible);
            El<Border>("NowStrip").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        static string Friendly(string st)
        {
            if (st.StartsWith("Working: ")) return "Now: " + st.Substring(9) + ".";
            if (st.StartsWith("Running - nothing")) return "All done for now. Waiting for the next thing.";
            if (st.StartsWith("Waiting until you stop using")) return "You're using the PC, so the bot waits.";
            return st;
        }

        void ShowHeader(Header h)
        {
            last = h;
            if (h.Level > 0) El<TextBlock>("LevelText").Text = h.Level.ToString();
            if (!string.IsNullOrWhiteSpace(h.Name)) { El<TextBlock>("PlayerName").Text = playerName = h.Name; AccountSeen(h.Name); }
            if (h.Level > 0) UpdateCheckCard();
            ShowPlayerFamily();
            if (h.Xp >= 0 && h.XpNext > 0)
            {
                Bar("Xp", (double)h.Xp / h.XpNext);
                El<TextBlock>("XpText").Text = string.Format(CultureInfo.InvariantCulture, "{0:N0} / {1:N0} XP", h.Xp, h.XpNext);
            }
            if (h.Cash >= 0) El<TextBlock>("CashText").Text = Money(h.Cash);
            if (h.Banked >= 0) El<TextBlock>("BankText").Text = Money(h.Banked) + " banked";
            ShowBar("Energy", h.Energy, h.EnergyMax, h.EnergyTick, "+1 in {0}s");
            ShowBar("Stamina", h.Stamina, h.StaminaMax, h.StaminaTick, "+1 in {0}");
            ShowBar("Health", h.Health, h.HealthMax, -2, null);
            El<TextBlock>("PointsText").Text = h.SkillPoints > 0 ? h.SkillPoints + " skill points to spend" : "";
            RefreshJobCard();
            UpdateTotals();
        }

        void ShowBar(string name, int cur, int max, int tick, string tickFormat)
        {
            if (cur < 0 || max <= 0) return;
            Bar(name, (double)cur / max);
            El<TextBlock>(name + "Text").Text = cur + " / " + max;
            var tickText = Window.FindName(name + "Tick") as TextBlock;
            if (tickText == null) return;
            if (cur >= max || tick == 0) tickText.Text = "full";
            else if (tick > 0) tickText.Text = string.Format(tickFormat, tick >= 60 ? TimeSpan.FromSeconds(tick).ToString(@"m\:ss") : tick.ToString());
            else tickText.Text = "";
        }

        void Bar(string name, double fraction)
        {
            fraction = Math.Max(0, Math.Min(1, fraction));
            El<ColumnDefinition>(name + "A").Width = new GridLength(fraction, GridUnitType.Star);
            El<ColumnDefinition>(name + "B").Width = new GridLength(1 - fraction, GridUnitType.Star);
        }

        static string Money(double v) { return SafehouseInfo.Money(v); }

        void ShowInfo(string key, string value)
        {
            if (key == "favors") { int n; ShowFavors(int.TryParse(value, out n) ? n : -1); }
            else if (key == "bossname") El<TextBlock>("BossName").Text = value;
            else if (key == "bosswhen") El<TextBlock>("BossWhen").Text = value;
            else if (key == "givewhy")
            {

                El<Border>("GiveWhyBox").Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
                El<TextBlock>("GiveWhy").Text = string.IsNullOrEmpty(value) ? "" : "Right now: " + value + ".";
            }
            else if (key == "job") RefreshJobCard();
            else if (key == "fullbars")
            {

                var t = El<TextBlock>("FullBarsText");
                t.Text = value ?? "";
                t.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            }
            else if (key == "stuck")
            {

                var t = El<TextBlock>("StuckText");
                t.Text = value ?? "";
                t.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
                FitStrip();
            }
            else if (key == "roblox")
                El<TextBlock>("PlayerName").Text = value == "found" ? playerName ?? "Roblox found"
                                                 : value == "covered" ? playerName ?? "Roblox is covered"
                                                 : value == "not running" ? "Waiting for Roblox"
                                                 : value == "minimized" ? "Roblox is minimized" : "Can't read the game. Is it open and not covered?";
        }

        const int FavorsPerDay = 30;

        int favorsTop = -1;
        string favorsFor;

        void ShowFavors(int n)
        {
            string who = s.Account ?? "";
            if (who != favorsFor) { favorsFor = who; favorsTop = -1; }
            favorsTop = Math.Max(favorsTop, n);

            bool more = favorsTop > FavorsPerDay;
            El<TextBlock>("FavorsText").Text = n < 0 ? "-" : more ? n + " left" : n + " / " + FavorsPerDay;
            var pips = El<WrapPanel>("FavorPips");
            pips.Children.Clear();
            if (n < 0 || more) return;
            for (int i = 0; i < FavorsPerDay; i++)
                pips.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 4, 4), Background = i < n ? Res("Xp") : Res("Raised2") });
        }

        void ShowOps(List<OpSlot> slots)
        {
            ops.Clear();
            foreach (var sl in slots.Where(x => !x.Locked))
            {
                int total = Game.KnownOpSeconds(sl.Name);
                bool finished = sl.Collect != null || sl.Done;
                double done = finished ? 1 : sl.Running && total > 0 && sl.SecondsLeft >= 0 ? 1 - (double)sl.SecondsLeft / total : 0;
                string left = finished ? "ready to collect" : sl.Running ? (sl.SecondsLeft >= 0 ? Span(sl.SecondsLeft) + " left" : "running") : "free slot";
                ops.Add(new OpsRow
                {
                    Name = sl.Name.Length > 0 ? sl.Name : "Empty slot", Left = left,
                    A = new GridLength(Math.Max(0, done), GridUnitType.Star), B = new GridLength(Math.Max(0, 1 - done), GridUnitType.Star),
                });
            }
            El<TextBlock>("OpsEmpty").Visibility = ops.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        string safeFamily;

        void ShowPlayerFamily()
        {
            string read = last != null ? (last.Family ?? "").Trim() : "";
            if (read.Length == 0) return;
            foreach (var known in new[] { fam != null && fam.Overview != null ? fam.Overview.Name : null, safeFamily, profile != null ? profile.Family : null })
            {
                string name = Regex.Replace(known ?? "", @"\s*\[[^\]]*\]\s*$", "").Trim();
                if (name.Length > 0 && Squash(name) == Squash(read)) { read = name; break; }
            }
            El<TextBlock>("PlayerFamily").Text = read.ToUpperInvariant();
        }

        static string Squash(string s) { return Regex.Replace(s ?? "", @"\s+", "").ToUpperInvariant(); }

        void ShowSafehouse(SafehouseInfo s)
        {
            if (!string.IsNullOrWhiteSpace(s.Family)) { safeFamily = s.Family; ShowPlayerFamily(); }
            var ci = CultureInfo.InvariantCulture;
            Func<int, string> n = v => v >= 0 ? v.ToString("N0", ci) : "-";
            El<TextBlock>("SafeLevel").Text = s.Level > 0 ? s.Level + (s.MaxLevel > 0 ? " / " + s.MaxLevel : "") : "-";
            El<TextBlock>("SafeLevelSub").Text = "safehouse" + (s.Bonus >= 0 ? ", +" + s.Bonus + "%" : "");
            El<TextBlock>("SafeRespect").Text = n(s.Respect);
            El<TextBlock>("SafeIncome").Text = s.Income >= 0 ? Money(s.Income) : "-";
            El<TextBlock>("SafeAttack").Text = n(s.AttackPower);
            El<TextBlock>("SafeDefense").Text = n(s.DefensePower);
            El<TextBlock>("SafeCrew").Text = s.Hired >= 0 ? s.Hired + " / " + s.HireMax : "-";
            El<TextBlock>("SafeCrewSub").Text = "henchmen" + (s.Gear >= 0 ? ", gear " + s.Gear + "/" + s.GearMax : "");
            El<TextBlock>("SafeWhen").Text = "read at " + DateTime.Now.ToString("HH:mm");
        }

        static string Span(int seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? string.Format("{0}h {1:00}m", (int)t.TotalHours, t.Minutes) : string.Format("{0}m", Math.Max(1, t.Minutes));
        }

        void UpdateTotals()
        {
            var c = bot.Count;
            El<TextBlock>("TotJobs").Text = c.Jobs.ToString();
            El<TextBlock>("TotHelps").Text = c.Helps.ToString();
            El<TextBlock>("TotBoss").Text = c.BossHits.ToString();
            El<TextBlock>("TotFights").Text = c.Fights.ToString();
            El<TextBlock>("TotFightsSub").Text = c.Fights > 0 ? string.Format("fights, {0} won", c.Wins) : "fights";
            El<TextBlock>("TotDeposits").Text = c.Deposits.ToString();
            El<TextBlock>("TotRewards").Text = (c.Rewards + c.Crates).ToString();
            try { El<TextBlock>("TombLosses").Text = c.Losses.ToString(); }
            catch (Exception) { }
            try
            {
                var gs = bot.LastGameStats;
                El<TextBlock>("TombWins").Text = gs != null && gs.FightWins >= 0 ? gs.FightWins.ToString("N0") + " wins" : "";
            }
            catch (Exception) { }
            var parts = new List<string>();
            if (c.Rerolls > 0) parts.Add(c.Rerolls + (c.Rerolls == 1 ? " reroll" : " rerolls"));
            if (c.Hired > 0) parts.Add(c.Hired + (c.Hired == 1 ? " hire" : " hires"));
            El<TextBlock>("CrewSession").Text = parts.Count == 0 ? "" : "Since Start: " + string.Join(" and ", parts) + (c.CrewCash > 0 ? " for " + Money(c.CrewCash) : "") + ".";
        }

        void RefreshJobCard()
        {
            ShowJobMode();
            var job = bot.PickJob(last, bot.CatalogView);
            bool oneMode = s.JobMode == JobMode.OneJob;
            El<TextBlock>("JobModeText").Text = oneMode ? "one job" : s.JobMode == JobMode.BestXp ? "best XP" : bot.MasteryDone(job) ? "mastery done: best XP" : "mastery";
            if (!s.Jobs) { El<TextBlock>("JobName").Text = "Jobs are off"; El<TextBlock>("JobMeta").Text = ""; Bar("JobMastery", 0); El<TextBlock>("JobMasteryText").Text = ""; return; }
            if (oneMode)
            {

                JobInfo picked;
                string why = OneJobNow(out picked);
                if (why != null)
                {
                    bool none = why == Bot.OneJobNone;
                    El<TextBlock>("JobName").Text = none ? "No job picked" : s.OneJob.Trim();
                    El<TextBlock>("JobMeta").Text = none ? "Click one in the list below." : "No jobs now: " + why + ".";
                    Bar("JobMastery", picked == null ? 0 : MasteryFill(picked));
                    El<TextBlock>("JobMasteryText").Text = picked == null ? "" : "Mastery " + picked.MasteryLabel;
                    return;
                }
                job = picked;
            }
            if (job == null)
            {
                El<TextBlock>("JobName").Text = s.JobMode == JobMode.Mastery ? "Every job has the mastery you asked for" : "-";
                El<TextBlock>("JobMeta").Text = "";
                Bar("JobMastery", 1);
                El<TextBlock>("JobMasteryText").Text = "";
                return;
            }
            El<TextBlock>("JobName").Text = job.Name;
            El<TextBlock>("JobMeta").Text = string.Format(CultureInfo.InvariantCulture, "{0} energy, {1:N0} XP, {2:0.0} XP per energy", job.Cost, job.Xp, job.XpPerEnergy);
            Bar("JobMastery", MasteryFill(job));
            El<TextBlock>("JobMasteryText").Text = "Mastery " + job.MasteryLabel;
        }

        static double MasteryFill(JobInfo j) { return j.MasteryRank >= 3 ? 1 : j.MasteryGoal > 0 && j.MasteryCur >= 0 ? (double)j.MasteryCur / j.MasteryGoal : 0; }

        string OneJobNow(out JobInfo one)
        {
            string why = bot.OneJobWhy(last, bot.CatalogView, out one);
            if (why != null || one == null) return why;
            var until = bot.JobAsideUntil(one.Name);
            return until == DateTime.MinValue ? null : "missed twice, tries again at " + until.ToLocalTime().ToString("HH:mm");
        }

        void ShowJobMode()
        {
            JobInfo one;
            string why = s.JobMode == JobMode.OneJob ? OneJobNow(out one) : null;
            El<TextBlock>("JobModeHint").Text = s.JobMode == JobMode.BestXp ? "The job with the most XP per energy. It moves up as you level."
                : s.JobMode == JobMode.Mastery ? "Each job from the top until it reaches Gold (100 done, a skill point each), then the next one."
                : why == Bot.OneJobNone ? "Click a job in the list below." : "Only the job you click in the list below.";
            bool notNow = why != null && why != Bot.OneJobNone;
            El<Border>("OneJobWhyBox").Visibility = notNow ? Visibility.Visible : Visibility.Collapsed;
            El<TextBlock>("OneJobWhy").Text = notNow ? "Not doing it now: " + why + "." : "";
        }

        void PickOneJob(string name)
        {
            if (s.JobMode != JobMode.OneJob || name == s.OneJob) return;
            s.OneJob = name;
            Save();
            bot.Log("Jobs: only \"" + name + "\" from now on (One job)");
            UpdateDerived();
        }

        readonly HashSet<string> openCities = new HashSet<string>();
        JobMode listMode;
        bool listShown;

        void RefreshJobs()
        {
            if (!jobsDirty) return;
            jobsDirty = false;
            jobRows.Clear();
            var catalog = bot.CatalogView;

            bool oneMode = s.JobMode == JobMode.OneJob;
            JobInfo target;
            if (oneMode) bot.OneJobWhy(last, catalog, out target);
            else target = bot.PickJob(last, catalog);
            int level = last != null && last.Level > 0 ? last.Level : s.LastLevel;

            if (oneMode && listMode != JobMode.OneJob && listShown && target != null) openCities.Add(target.City);
            listMode = s.JobMode;
            listShown = true;
            var ti = CultureInfo.InvariantCulture.TextInfo;
            int gold = 0, known = 0;
            foreach (var cityJobs in catalog.OrderBy(x => x.Order).GroupBy(x => x.City))
            {
                string city = cityJobs.Key;
                bool open = openCities.Contains(city);
                int cityLevel = cityJobs.Min(x => x.TierLevel), cityGold = cityJobs.Count(x => x.MasteryRank >= 3);
                jobRows.Add(new JobRow
                {
                    Header = ti.ToTitleCase(city.ToLowerInvariant()), IsHeader = true, Sign = open ? "−" : "+",
                    Summary = level > 0 && cityLevel > level ? "Opens at level " + cityLevel : cityGold + " of " + cityJobs.Count() + " at Gold",
                    Toggle = new RelayCommand(() => { if (!openCities.Remove(city)) openCities.Add(city); jobsDirty = true; RefreshJobs(); }),
                    A = new GridLength(0, GridUnitType.Star), B = new GridLength(1, GridUnitType.Star),
                });
                foreach (var j in cityJobs)
                {
                    bool locked = level > 0 && j.TierLevel > level;
                    string rankName; Brush rankBrush; double frac;
                    if (j.MasteryRank >= 3) { rankName = "Gold"; rankBrush = Res("Gold"); frac = 1; gold++; }
                    else if (j.MasteryRank < 0) { rankName = "?"; rankBrush = Res("Text3"); frac = 0; }
                    else
                    {
                        string[] next = { "Bronze", "Silver", "Gold" };
                        string[] colors = { "#C9956A", "#C4C9D4", "#D4AF37" };
                        rankName = (j.MasteryRank == 0 ? "" : (j.MasteryRank == 1 ? "Bronze " : "Silver ")) + (j.MasteryCur >= 0 ? j.MasteryCur + "/" + j.MasteryGoal : "");
                        rankBrush = Hex(colors[Math.Min(2, j.MasteryRank)]);
                        frac = j.MasteryGoal > 0 && j.MasteryCur >= 0 ? (double)j.MasteryCur / j.MasteryGoal : 0;
                        rankName = rankName.Trim().Length == 0 ? "to " + next[Math.Min(2, j.MasteryRank)] : rankName.Trim();
                    }
                    if (j.MasteryRank >= 0) known++;
                    if (locked) { rankName = "level " + j.TierLevel; rankBrush = Res("Text3"); frac = 0; }
                    if (!open) continue;
                    bool isTarget = target == j;
                    string name = j.Name;
                    jobRows.Add(new JobRow
                    {
                        Header = "", Name = j.Name, Cost = j.Cost + " energy", Xp = j.Xp.ToString("N0", CultureInfo.InvariantCulture) + " XP",
                        Rank = rankName, RankBrush = rankBrush,
                        NameBrush = isTarget ? Res("Gold") : locked ? Res("Text3") : Res("Text"),
                        NameWeight = isTarget ? FontWeights.Bold : FontWeights.Normal,
                        RowBg = isTarget ? Res("GoldSoft") : Brushes.Transparent,
                        A = new GridLength(frac, GridUnitType.Star), B = new GridLength(1 - frac, GridUnitType.Star),
                        Pickable = oneMode, Hover = oneMode && !isTarget, Pick = oneMode ? new RelayCommand(() => PickOneJob(name)) : null,
                    });
                }
            }
            El<TextBlock>("MasterySummary").Text = string.Format("{0} of {1} jobs at Gold", gold, catalog.Count) + (known < catalog.Count ? " (some not read yet)" : "");
        }

        void WireHome()
        {
            var page = El<ScrollViewer>("PageOverview");
            var grid = El<Grid>("HomeGrid");
            var scene = El<Grid>("HeroScene");
            Action roundHero = () => scene.Clip = new RectangleGeometry(new Rect(0, 0, scene.ActualWidth, scene.ActualHeight), 13, 13);
            scene.SizeChanged += (a, b) => roundHero();
            scene.Loaded += (a, b) => roundHero();
            page.SizeChanged += (a, b) => FitHome();
            for (int i = 0; i < 2; i++)
            {
                var top = (FrameworkElement)grid.Children[i];
                top.SizeChanged += (a, b) => FitHome();
                top.IsVisibleChanged += (a, b) => Window.Dispatcher.BeginInvoke(new Action(FitHome), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        const double HomeMinColumns = 220;

        void FitHome()
        {
            var page = El<ScrollViewer>("PageOverview");
            var grid = El<Grid>("HomeGrid");
            double top = 0;
            for (int i = 0; i < 2; i++)
            {
                var e = (FrameworkElement)grid.Children[i];
                if (e.Visibility == Visibility.Visible) top += e.ActualHeight + e.Margin.Top + e.Margin.Bottom;
            }
            double h = Math.Max(page.ActualHeight - grid.Margin.Top - grid.Margin.Bottom, top + HomeMinColumns);
            if (page.ActualHeight > 0 && Math.Abs(grid.Height - h) > 0.5 || double.IsNaN(grid.Height)) grid.Height = h;
        }

        void AddFeed(string line)
        {
            if (line.Length < 10) return;
            string time = line.Substring(0, 8), raw = line.Substring(8).Trim(), text = Clean(raw);
            if (raw.StartsWith("Doing:") || raw.StartsWith("You're using the PC")) return;
            var crew = CrewFor(raw);

            if (text.StartsWith(crew.Name + ": ") && text.Length > crew.Name.Length + 3)
                text = char.ToUpperInvariant(text[crew.Name.Length + 2]) + text.Substring(crew.Name.Length + 3);
            string lower = raw.ToLowerInvariant();
            bool good = raw.Contains("VICTORY") || raw.StartsWith("Defeated boss");
            bool bad = raw.Contains(": DEFEAT") || lower.Contains("couldn't") || lower.Contains("problem") || lower.Contains("didn't")
                    || lower.Contains("not recognised") || lower.Contains("refused") || lower.Contains("stopped and") || lower.Contains("is low");
            var color = Hex(crew.Color);
            feed.Insert(0, new FeedItem
            {
                Who = crew.Name, Color = color, Time = time, Text = text,
                Stripe = bad ? Res("Red") : good ? Res("Green") : color,
                Icon = (Geometry)Window.FindResource(crew.Icon),
            });
            while (feed.Count > 60) feed.RemoveAt(feed.Count - 1);
            El<TextBlock>("FeedEmpty").Visibility = Visibility.Collapsed;
            El<TextBlock>("FeedCount").Text = "since " + started.ToString("HH:mm");
            UpdateTotals();
            if (lower.Contains("screenshot")) UpdateProblems();
        }

        static Crew CrewFor(string text)
        {
            string u = text.ToLowerInvariant();

            if (u.StartsWith("lucky briefcases: a free pick")) return Collector;
            if (u.StartsWith("discord")) return Don;
            if (u.StartsWith("lucky briefcases") || u.StartsWith("events:")) return Don;
            if (u.StartsWith("check") || u.StartsWith("--- check") || u.Contains("game check") || u.StartsWith("set up from") || u.Contains("the game's menu")) return Don;

            if (u.StartsWith("properties") || u.StartsWith("safehouse: upgrade") || u.StartsWith("safehouse: saving")) return Landlord;
            if (u.StartsWith("safehouse:") || u.StartsWith("your ")) return Don;

            if (u.StartsWith("crew") && !u.Contains("gear") && !u.Contains("equip")) return Henchmen;
            if (u.StartsWith("shop") || u.StartsWith("crew") || u.StartsWith("collection") || u.Contains("equip") || u.StartsWith("crate")
                || u.Contains("crate(s)")) return Items;
            if (!u.Contains("heist") && (u.Contains("family") || u.Contains("takedown") || u.Contains("stamina perk") || u.Contains("give 5") || u.StartsWith("gave "))) return Underboss;
            if (u.Contains("boss")) return Capo;
            if (u.StartsWith("fight") || u.Contains("attack") || u.Contains("fights")) return Enforcer;
            if (u.Contains("heist") || u.Contains("favor") || u.StartsWith("helped")) return Fixer;
            if (u.StartsWith("bank") || u.Contains("deposit")) return Accountant;
            if (u.Contains("operation")) return Consigliere;
            if (u.Contains("claimed") || u.Contains("skill point") || u.Contains("playtime") || u.Contains("contract")) return Collector;
            if (u.Contains("job") || u.Contains("mastery") || u.Contains("city")) return Runner;
            return Don;
        }

        static string Clean(string t)
        {
            t = Regex.Replace(t, @"\s[&@•»·]\s(?![a-z])", " ");
            t = Regex.Replace(t, @"\s[t]\s(?=[+\-$])", " ");
            t = Regex.Replace(t, @"\b[oO0]\s*/\s*[\d,]+\s*", "");
            t = Regex.Replace(t, @"\s+x\s*$", "");

            t = t.Replace(" -> ", " to ").Replace(" · ", ", ");
            t = Regex.Replace(t, @"\b1 ([a-z ]+?)\(s\)", "1 $1");
            t = t.Replace("(s)", "s");
            var fight = Regex.Match(t, @"^Fight vs level (-?\d+) player: (VICTORY|DEFEAT)\s*\(?(.*?)\)?$");
            if (fight.Success)
            {
                string who = fight.Groups[1].Value == "-1" ? "a player" : "a level " + fight.Groups[1].Value + " player";
                string spoils = fight.Groups[3].Value.Replace(" xp", " XP").Trim();
                t = (fight.Groups[2].Value == "VICTORY" ? "Beat " : "Lost to ") + who + (spoils.Length > 0 ? ": " + spoils : ".");
            }
            var m = Regex.Match(t, @"^Bank: Deposit[a-z]*\.?\s*\$([\d,]+)(?:.*?\((\$[\d,.]+\s?[KMBT]?)\s*fee\))?", RegexOptions.IgnoreCase);
            double v;
            if (m.Success && double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                t = "Put " + Money(v) + " in the bank" + (m.Groups[2].Success ? " (" + m.Groups[2].Value + " fee)." : ".");
            return Regex.Replace(t, @"\s{2,}", " ").Trim();
        }

        void Demo()
        {
            ShowHeader(new Header
            {
                Name = "DemoPlayer", Family = "Demo Family", Level = 99, Xp = 51290, XpNext = 52616, Cash = 3.3e9, Banked = 58.1e9, SkillPoints = 20,
                Energy = 46, EnergyMax = 81, EnergyTick = 4, Stamina = 22, StaminaMax = 40, StaminaTick = 88, Health = 101, HealthMax = 300,
            });
            ShowFavors(9);
            ShowSafehouse(new SafehouseInfo
            {
                Level = 11, MaxLevel = 16, Bonus = 43, Respect = 2974, Income = 9.81e9, AttackPower = 4831, DefensePower = 4697,
                Hired = 23, HireMax = 23, Gear = 69, GearMax = 69,
            });
            ShowInfo("bossname", "Jin Shan");
            ShowInfo("bosswhen", "back in 1h 12m");
            ShowInfo("givewhy", "gave just now, gives again at 14:35");
            ShowInfo("fullbars", "Bars sat full in the last hour: energy 3 min, stamina 0 min");
            ShowOps(new List<OpSlot>
            {
                new OpSlot { Name = "Own the Front Page", Running = true, SecondsLeft = 5325 },
                new OpSlot { Name = "Own the Front Page", Running = true, SecondsLeft = 5325 },
                new OpSlot { Name = "Own the Front Page", Running = true, SecondsLeft = 5538 },
            });
            foreach (var line in new[]
            {
                "14:01:58  Defeated boss \"Maksim Kovac\" in 5 hits: +4,098 XP + IRON CRATE",
                "14:02:16  Defeated boss \"Dragan Kovac\" in 8 hits: +$1.8M +6,141 XP + IRON CRATE",
                "14:02:31  Defeated boss \"Zhao Lin\" in 7 hits: +$3.2M + IRON CRATE +7,156 XP",
                "14:06:18  Crate: got Rail Yard Plate Vest Rare ARMOR",
                "14:07:13  Did \"Burn the Rival Fleet at Anchor\" x1 - mastery now 13/25 to Bronze",
                "14:09:56  Claimed 1 contract task(s)",
                "14:10:02  Helped \"Grand Vault of Caldera\" (+140 XP)",
                "14:10:05  Fight vs level 75 player: VICTORY (+19 Respect +151 xp)",
                "14:10:09  Bank: deposited $4,866,297,570 (no fee)",
                "14:11:30  Crew: 6 rerolls with Professional hires ($60M), better than before: Nico \"The Wire\" Bellini (Epic, +13/+14). The worst now: Dario \"Pockets\" Vale (Rare, +9/+8)",
                "14:11:52  Crew: sorted by rarity, 3 moves up: Sal \"The Clock\" Moreno (Legendary), Lena \"Marbles\" Fox (Legendary), Rico \"Lanterns\" Stone (Legendary)",
                "14:12:40  Takedown: 10 free attacks used, none left, +41,250 damage (94,222 this week, #1 in your Family)",
                "14:13:05  Gave 20 stamina to Hustlers (58 of 160 -> 78 of 160), stamina now 20/40",
            }) AddFeed(line);

            var crew = new CrewInfo
            {
                At = DateTime.Today.AddHours(14).AddMinutes(11), Henchmen = 23, Slots = 23, Attack = 1979, Defense = 2007, SlotPrice = 9.06e9,
                Worst = "Dario \"Pockets\" Vale (Rare, +37/+40)", Status = "Rerolls wait: saving up for Professional hires ($10M each, $4.2M on hand).",
            };
            crew.ByRarity[6] = 2; crew.ByRarity[5] = 5; crew.ByRarity[4] = 14; crew.ByRarity[3] = 1; crew.ByRarity[2] = 1;
            ShowCrew(crew);
            DemoFamily();
            DemoPayers();
        }

        void DemoPayers()
        {
            var m = new FightMemory(null);
            var now = DateTime.UtcNow;

            foreach (var f in new[]
            {
                Tuple.Create("VelvetKnuckles", 1900, true, 4.2e9), Tuple.Create("VelvetKnuckles", 400, true, 8.6e9), Tuple.Create("VelvetKnuckles", 130, true, 2.9e9),
                Tuple.Create("DockRat_77", 2500, false, -1.0), Tuple.Create("DockRat_77", 300, true, 3.1e9),
                Tuple.Create("SilentMarco", 700, true, 1.45e9), Tuple.Create("SilentMarco", 95, true, 960e6),
                Tuple.Create("NeonCasino", 3000, true, 640e6), Tuple.Create("NeonCasino", 1300, true, 715e6), Tuple.Create("NeonCasino", 1250, false, -1.0),
                Tuple.Create("LuckyPaulie", 42, true, 312e6),
                Tuple.Create("TinCanTony", 3600, true, 180e6), Tuple.Create("TinCanTony", 3500, true, 205e6),
                Tuple.Create("QuietHarbor", 600, true, 12e6),
            }) m.Add(f.Item1, f.Item3, f.Item4, now.AddMinutes(-f.Item2));
            UsePayers(m);
        }

        void DemoFamily()
        {
            var at = DateTime.Today.AddHours(14).AddMinutes(12);
            Func<string, int, int, long, long, string, PerkRow> perk = (n, lv, max, have, need, eff) =>
                new PerkRow { Name = n, Level = lv, LevelMax = max, Have = have, Need = need, Effect = eff };
            ShowFamily(new FamilyInfo
            {
                Overview = new FamilyOverview
                {
                    Name = "DEMO FAMILY", Tag = "DEMO", Founded = "September 01, 2026", Donated = "$3.5B and 35 gold bars", Level = 6, Xp = 62441, XpNext = 114218,
                    Members = 18, MembersMax = 50, Respect = 209, PerkLevels = 63, PerkLevelsMax = 350, Territories = 0, TerritoriesMax = 10, Vault = 592e6, Gold = 10,
                },
                OverviewAt = at,
                Takedown = new TakedownState
                {
                    Page = true, Name = "Castellane Estate", Free = 0, FreeMax = 10, Tickets = 0, Damage = 94222, Place = "#1 in your Family", Health = 229, HealthMax = 300,
                    Stage = 3, StageName = "Grand Ballroom", StageHp = 337649, StageHpMax = 500000, NewSeconds = 536460,
                },
                TakedownAt = at,
                Perks = new List<PerkRow>
                {
                    perk("Enforcers", 11, 20, 86, 1700, "+11% attack power"), perk("Guardians", 6, 20, 12, 440, "+6% defense power"),
                    perk("Hustlers", 3, 20, 78, 160, "+3% job experience"), perk("Medics", 3, 20, 15, 160, "+3% health regen"),
                    perk("Runners", 5, 20, 5, 330, "+2.5% stamina regen"),
                },
                PerksAt = at.AddMinutes(1),
                Attacks = 10, StaminaGiven = 20,
            });
        }

        void ShowFamily(FamilyInfo fi)
        {
            if (fi != null) { fam = fi; ShowPlayerFamily(); }
            fi = fam;
            var ic = CultureInfo.InvariantCulture;
            bool none = fi != null && fi.NotInFamily;
            var o = fi == null ? null : fi.Overview;
            El<TextBlock>("FamName").Text = o != null && o.Name.Length > 0 ? o.Name : none ? "No family" : "Your family";
            El<TextBlock>("FamTag").Text = o != null && o.Tag.Length > 0 ? "[" + o.Tag + "]" : "";
            El<TextBlock>("FamLevel").Text = o != null && o.Level > 0 ? o.Level.ToString(ic) : "?";
            bool xp = o != null && o.Xp >= 0 && o.XpNext > 0;
            Bar("FamXp", xp ? (double)o.Xp / o.XpNext : 0);
            El<TextBlock>("FamXpText").Text = xp ? string.Format(ic, "{0:N0} / {1:N0} family XP", o.Xp, o.XpNext) : "";
            El<TextBlock>("FamVault").Text = o != null && o.Vault >= 0 ? Money(o.Vault) : "-";
            El<TextBlock>("FamGold").Text = o != null && o.Gold >= 0 ? o.Gold.ToString(ic) + " gold bars" : "";
            El<TextBlock>("FamMembers").Text = o != null && o.Members >= 0 ? o.Members + " / " + o.MembersMax : "-";
            El<TextBlock>("FamRespect").Text = o != null && o.Respect >= 0 ? o.Respect.ToString("N0", ic) : "-";
            El<TextBlock>("FamPerkLevels").Text = o != null && o.PerkLevels >= 0 ? o.PerkLevels + " / " + o.PerkLevelsMax : "-";
            El<TextBlock>("FamTerritories").Text = o != null && o.Territories >= 0 ? o.Territories + " / " + o.TerritoriesMax : "-";
            El<TextBlock>("FamRead").Text = none ? "You're not in a family. The bot looks again every 6 hours."
                : o == null ? "Not read yet. The bot looks every 3 hours."
                : "Read at " + fi.OverviewAt.ToString("HH:mm") + "." + (o.Founded.Length > 0 ? " Founded " + o.Founded + "." : "") + (o.Donated.Length > 0 ? " You donated " + o.Donated + "." : "");

            var t = fi == null ? null : fi.Takedown;
            El<TextBlock>("TdDamage").Text = t != null && t.Damage >= 0 ? t.Damage.ToString("N0", ic) : "-";
            El<TextBlock>("TdPlace").Text = t != null ? t.Place : "";
            El<TextBlock>("TdFree").Text = t != null && t.Free >= 0 ? t.Free + " / " + t.FreeMax : "-";
            var pips = El<WrapPanel>("TdPips");
            pips.Children.Clear();
            if (t != null && t.Free >= 0)
                for (int i = 0; i < Math.Min(30, t.FreeMax); i++)
                    pips.Children.Add(new Border { Width = 11, Height = 7, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 3, 3), Background = i < t.Free ? Res("Gold") : Res("Raised2") });
            bool stage = t != null && t.Stage > 0;
            El<TextBlock>("TdStageLabel").Text = stage ? "Stage " + t.Stage : "Stage";
            El<TextBlock>("TdStage").Text = stage ? (t.StageName.Length > 0 ? t.StageName : "Stage " + t.Stage) : "-";
            bool hp = stage && t.StageHpMax > 0;
            Bar("TdHp", hp ? (double)t.StageHp / t.StageHpMax : 0);
            El<TextBlock>("TdHp").Text = hp ? string.Format(ic, "{0:N0} / {1:N0} health left", t.StageHp, t.StageHpMax) : "";
            var td = new List<string>();
            if (t != null)
            {
                if (t.Name.Length > 0) td.Add("This week: " + t.Name + ".");
                td.Add("Read at " + fi.TakedownAt.ToString("HH:mm") + ".");
                if (t.NewSeconds > 0)
                {
                    var left = fi.TakedownAt.AddSeconds(t.NewSeconds) - DateTime.Now;
                    if (left.TotalMinutes > 1) td.Add(left.TotalDays >= 1 ? string.Format("Next one in {0}d {1}h.", (int)left.TotalDays, left.Hours) : string.Format("Next one in {0}h {1:00}m.", (int)left.TotalHours, left.Minutes));
                }
            }
            if (fi != null && fi.Attacks > 0) td.Add(fi.Attacks + " free attacks used since Start.");
            El<TextBlock>("TdRead").Text = none ? "You're not in a family." : td.Count == 0 ? "Not read yet." : string.Join(" ", td);

            perkRows.Clear();
            var rows = fi == null || fi.Perks == null ? new List<PerkRow>() : fi.Perks;

            string target = s.StaminaPerk.Trim().Length > 0 ? s.StaminaPerk.Trim()
                          : rows.Where(r => r.Left > 0 && !r.Maxed).OrderBy(r => r.Left).Select(r => r.Name).FirstOrDefault();
            foreach (var r in rows)
            {
                bool gets = s.GiveStamina && target != null && Game.SamePerk(r.Name, target);
                double frac = r.Have >= 0 && r.Need > 0 ? Math.Min(1, (double)r.Have / r.Need) : 0;
                perkRows.Add(new PerkView
                {
                    Name = r.Name.Length > 0 ? r.Name : "?", Effect = r.Effect + (gets ? (r.Effect.Length > 0 ? ", " : "") + "gets the spare stamina" : ""),
                    Level = r.Level > 0 ? "level " + r.Level + " / " + r.LevelMax : "",
                    Progress = r.Have >= 0 ? string.Format(ic, "{0:N0} / {1:N0}", r.Have, r.Need) : "-",
                    NameBrush = gets ? Res("Gold") : Res("Text"), NameWeight = gets ? FontWeights.Bold : FontWeights.SemiBold,
                    RowBg = gets ? Res("GoldSoft") : Brushes.Transparent,
                    A = new GridLength(frac, GridUnitType.Star), B = new GridLength(1 - frac, GridUnitType.Star),
                });
            }
            El<TextBlock>("PerksEmpty").Visibility = perkRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            El<TextBlock>("PerksEmpty").Text = none ? "You're not in a family." : "Not read yet.";
            El<TextBlock>("PerksRead").Text = fi == null || fi.Perks == null ? "" : "read at " + fi.PerksAt.ToString("HH:mm") + (fi.StaminaGiven > 0 ? ", " + fi.StaminaGiven + " stamina given" : "");
        }

        void UpdateProblems()
        {
            var p = System.IO.Path.Combine(dir, "problems", "active");
            int n = System.IO.Directory.Exists(p) ? System.IO.Directory.GetFiles(p, "*.png").Length : 0;
            El<TextBlock>("ProblemsText").Text = n > 0 ? "Problems (" + n + ")" : "Problems";
            El<RadioButton>("RailSettings").Uid = n > 0 ? n.ToString() : "";
        }

        bool ReviewShowing { get { return El<Grid>("Review").Visibility == Visibility.Visible; } }

        void ShowReview(bool show)
        {
            if (show) RefreshReview();
            El<Grid>("Review").Visibility = show && profile != null ? Visibility.Visible : Visibility.Collapsed;
        }

        string accountShown;

        void ForgetAccount()
        {
            ShowReview(false);
            fam = null;
            safeFamily = null;
            ShowFamily(null);
            ShowOps(new List<OpSlot>());
            ShowSafehouse(new SafehouseInfo());
            El<TextBlock>("SafeWhen").Text = "read once a day";
        }

        void AccountSeen(string name)
        {
            string key = Profile.Key(name);
            if (key == profileOf) return;
            profileOf = key;
            profile = Profile.Load(Profile.FileFor(dir, name));
            UpdateCheckWhen();
            UpdateCheckCard();
        }

        void StartCheck()
        {
            if (Covered) return;
            if (bot.Running) { AddFeed(DateTime.Now.ToString("HH:mm:ss") + "  Stop the bot first, then press Check my game."); return; }
            ShowReview(false);
            El<Border>("CheckCard").Visibility = Visibility.Collapsed;
            bot.RequestCheck();
            ShowState("Checking");
        }

        void CheckDone(Profile p)
        {
            profile = p;
            profileOf = Profile.Key(p.Name);
            UpdateCheckWhen();
            UpdateCheckCard();
            ShowReview(true);
        }

        void RefreshReview()
        {
            if (profile == null) return;
            var p = profile;
            El<TextBlock>("ReviewSub").Text = (p.Name.Length > 0 ? p.Name : "Your account") + (p.Level > 0 ? ", level " + p.Level : "")
                + ", checked " + p.Checked.ToString("dd-MM", CultureInfo.InvariantCulture) + " at " + p.Checked.ToString("HH:mm", CultureInfo.InvariantCulture);
            foundRows.Clear();
            foreach (var r in Setup.Found(p)) foundRows.Add(new FoundRow { Title = r[0], Text = r[1] });
            El<TextBlock>("FocusHint").Text = Setup.FocusHint(s.Focus);
            changeRows.Clear();
            keptRows.Clear();
            foreach (var pr in Setup.Propose(p, s.Focus, s, bot.CatalogView))
            {

                string own = Setup.OwnChoice(pr, s);
                var row = new ChangeRow
                {
                    Name = pr.Name, Old = Setup.Show(pr.Field, pr.Current(s)), New = Setup.Show(pr.Field, pr.Value), Reason = pr.Reason,
                    Note = own ?? "", Use = own == null, Field = pr.Field, Value = pr.Value,
                };
                (pr.Changes(s) ? changeRows : keptRows).Add(row);
            }
            El<TextBlock>("ChangesEmpty").Visibility = changeRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            El<TextBlock>("KeptTitle").Visibility = keptRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            El<Button>("BtnUseSettings").Visibility = changeRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            El<TextBlock>("KeepText").Text = changeRows.Count > 0 ? "Keep my settings" : "Close";
            El<TextBlock>("ReviewNote").Text = changeRows.Count > 0
                ? "Nothing changes until you press Use these settings. Switch a line off to keep your own setting."
                : "Your settings already match this focus.";
            El<TextBlock>("UseText").Text = changeRows.Count == 1 ? "Use this setting" : "Use these settings";
        }

        void UseProposals()
        {
            var chosen = changeRows.Where(r => r.Use).ToList();
            foreach (var r in chosen) typeof(Settings).GetField(r.Field).SetValue(s, r.Value);
            Save();
            ShowReview(false);
            RefreshControls();
            bot.Log(chosen.Count == 0 ? "Kept your settings after the game check"
                : "Set up from your game check (" + s.Focus + " focus): " + string.Join(", ", chosen.Select(r => r.Name + " " + r.New)));
        }

        void RefreshControls()
        {
            foreach (var f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var sw = Window.FindName("sw_" + f.Name) as CheckBox;
                if (sw != null && f.FieldType == typeof(bool)) sw.IsChecked = (bool)f.GetValue(s);
                var holder = Window.FindName("num_" + f.Name) as ContentControl;
                if (holder != null && f.FieldType == typeof(int)) holder.Content = Stepper(f, (string)holder.Tag);
                var values = f.FieldType.IsEnum ? Enum.GetNames(f.FieldType).Select(n => (object)Enum.Parse(f.FieldType, n))
                           : f.FieldType == typeof(int) ? Enumerable.Range(0, 10).Select(n => (object)n) : Enumerable.Empty<object>();
                foreach (var v in values)
                {
                    var seg = Window.FindName("seg_" + f.Name + "_" + v) as RadioButton;
                    if (seg != null) seg.IsChecked = Equals(f.GetValue(s), v);
                }
            }
            El<ComboBox>("cmb_PointsStat").SelectedIndex = Math.Max(0, Math.Min(Game.StatNames.Length - 1, s.PointsStat));
            if (whaleValues != null)
            {

                if (!whaleValues.Contains(s.WhaleCash))
                {
                    whaleValues.Add(s.WhaleCash);
                    whaleValues.Sort();
                    var c = El<ComboBox>("cmb_WhaleCash");
                    c.Items.Insert(whaleValues.IndexOf(s.WhaleCash), WhaleName(s.WhaleCash));
                }
                El<ComboBox>("cmb_WhaleCash").SelectedIndex = whaleValues.IndexOf(s.WhaleCash);
            }
            if (payerValues != null)
            {
                if (!payerValues.Contains(s.BigPayerCash))
                {
                    payerValues.Add(s.BigPayerCash);
                    payerValues.Sort();
                    El<ComboBox>("cmb_BigPayerCash").Items.Insert(payerValues.IndexOf(s.BigPayerCash), PayerName(s.BigPayerCash));
                }
                El<ComboBox>("cmb_BigPayerCash").SelectedIndex = payerValues.IndexOf(s.BigPayerCash);
            }
            if (rollValues != null)
            {
                if (!rollValues.Contains(s.AutoRollLimit))
                {
                    rollValues.Add(s.AutoRollLimit);
                    rollValues.Sort();
                    El<ComboBox>("cmb_AutoRollLimit").Items.Insert(rollValues.IndexOf(s.AutoRollLimit), RollLimitName(s.AutoRollLimit));
                }
                El<ComboBox>("cmb_AutoRollLimit").SelectedIndex = rollValues.IndexOf(s.AutoRollLimit);
            }
            if (goalValues != null)
            {
                s.CrewRerollStats = s.CrewRerollStats > 0 ? Settings.Perfect : 0;
                El<ComboBox>("cmb_CrewRerollStats").SelectedIndex = goalValues.IndexOf(s.CrewRerollStats);
            }
            if (perkChoices != null)
            {
                if (s.StaminaPerk.Trim().Length > 0 && !perkChoices.Any(n => n != ClosestPerk && Game.SamePerk(n, s.StaminaPerk)))
                {
                    perkChoices.Add(s.StaminaPerk.Trim());
                    El<ComboBox>("cmb_StaminaPerk").Items.Add(s.StaminaPerk.Trim());
                }
                int perk = s.StaminaPerk.Trim().Length == 0 ? 0 : perkChoices.FindIndex(n => n != ClosestPerk && Game.SamePerk(n, s.StaminaPerk));
                El<ComboBox>("cmb_StaminaPerk").SelectedIndex = Math.Max(0, perk);
            }

            El<ComboBox>("cmb_StartHeist").SelectedIndex = HeistChoice();
            UpdateDerived();
        }

        int HeistChoice()
        {
            if (!s.StartHeists) return 0;
            int heist = Array.FindIndex(Game.HeistNames, n => n == s.StartHeist);
            return 1 + (heist < 0 ? 3 : heist);
        }

        void UpdateCheckWhen()
        {
            El<TextBlock>("CheckWhen").Text = profile == null
                ? "Reads your game for about a minute and suggests settings. Nothing changes until you say so."
                : string.Format(CultureInfo.InvariantCulture, "Last check {0:dd-MM} at {0:HH:mm}{1}. Check again every few levels.",
                    profile.Checked, profile.Level > 0 ? ", at level " + profile.Level : "");

            El<Button>("BtnReview").Visibility = profile != null ? Visibility.Visible : Visibility.Collapsed;
        }

        void UpdateCheckCard()
        {
            var card = El<Border>("CheckCard");
            int level = last != null && last.Level > 0 ? last.Level : -1;
            string text = null, title = null;
            if (!Covered && profileOf != null && !bot.Running)
            {
                if (profile == null && !Offered(profileOf))
                {
                    title = "Set the bot up for your account?";
                    text = "It reads your game for about a minute and suggests settings. Nothing changes until you say so.";
                }
                else if (profile != null && level > 0)
                {
                    text = Setup.RecheckHint(profile, level, s.CheckHintLevel);
                    title = "Check your game again?";
                }
            }
            if (text != null) { El<TextBlock>("CheckCardTitle").Text = title; El<TextBlock>("CheckCardText").Text = text; }
            card.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        }

        bool Offered(string key) { return s.CheckOffered.Split(',').Any(n => n.Trim() == key); }

        void MarkOffered()
        {
            if (profileOf == null || Offered(profileOf)) return;
            s.CheckOffered = s.CheckOffered.Trim().Length == 0 ? profileOf : s.CheckOffered.Trim() + "," + profileOf;
            Save();
        }

        void PutOffCheck()
        {
            if (profile == null) MarkOffered();
            else if (last != null && last.Level > 0) { s.CheckHintLevel = last.Level; Save(); }
            El<Border>("CheckCard").Visibility = Visibility.Collapsed;
        }

        void ShowWelcome(bool show) { El<Grid>("Welcome").Visibility = show ? Visibility.Visible : Visibility.Collapsed; }

        bool WelcomeShowing { get { return El<Grid>("Welcome").Visibility == Visibility.Visible; } }

        void ShowRules(bool show) { El<Grid>("Rules").Visibility = show ? Visibility.Visible : Visibility.Collapsed; }

        bool RulesShowing { get { return El<Grid>("Rules").Visibility == Visibility.Visible; } }

        bool Covered { get { return RulesShowing || WelcomeShowing; } }

        void ShowOcr(string problem)
        {
            ocrProblem = problem;
            ShowBanner();
        }

        string ocrProblem;
        readonly List<string[]> warnings = new List<string[]>();

        void ShowWarning(string title, string text, bool language)
        {
            warnings.Add(new[] { title, text, language ? "lang" : "" });
            ShowBanner();
        }

        void ShowBanner()
        {
            bool any = ocrProblem != null || warnings.Count > 0;
            El<Border>("OcrBanner").Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (!any) return;
            El<TextBlock>("OcrTitle").Text = ocrProblem != null ? "The bot can't read the game yet" : warnings[0][0];
            El<TextBox>("OcrText").Text = ocrProblem ?? string.Join("\n\n", warnings.Select(w => w[1]));
            El<Button>("BtnOcrSettings").Visibility = ocrProblem != null || warnings.Any(w => w[2] == "lang") ? Visibility.Visible : Visibility.Collapsed;
        }

        void MakeReport()
        {
            var btn = El<Button>("BtnReport");
            btn.IsEnabled = false;
            El<Button>("BtnReportHome").IsEnabled = false;
            El<TextBlock>("ReportText").Text = El<TextBlock>("ReportHomeText").Text = "Making the report...";
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string zip = null, error = null, desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                bool onDesktop = desktop.Length > 0 && System.IO.Directory.Exists(desktop);
                var before = DateTime.Now;
                int moved = 0;
                try
                {
                    zip = ProblemReport.Write(dir, onDesktop ? desktop : dir, ProblemReport.RobloxWindowInfo());
                    moved = ProblemReport.MoveReported(dir, before);
                }
                catch (Exception e) { error = e.Message; }
                Ui(() =>
                {
                    btn.IsEnabled = true;
                    El<Button>("BtnReportHome").IsEnabled = true;
                    El<TextBlock>("ReportText").Text = El<TextBlock>("ReportHomeText").Text = "Report a problem";
                    if (zip == null) { bot.Log("Couldn't make the report: " + error); return; }
                    UpdateProblems();
                    long size = new System.IO.FileInfo(zip).Length;

                    bool big = size > 10L * 1024 * 1024;
                    bot.Log(string.Format(CultureInfo.InvariantCulture, "Report saved {0}: {1} ({2}). {3}{4}",
                        onDesktop ? "on your Desktop" : "next to the bot", System.IO.Path.GetFileName(zip),
                        size < 1048576 ? Math.Max(1, size / 1024) + " KB" : (size / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
                        big ? "That's too big for Discord, so open an issue on the bot's GitHub page (Settings, Help) and add it there, with a few words about what went wrong."
                            : "Post it in the Discord (Settings, Help) with a few words about what went wrong.",
                        moved > 0 ? " Its " + moved + " problem picture" + (moved == 1 ? "" : "s") + " moved to problems\\reported, so the red number starts from 0 again." : ""));
                    try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + zip + "\""); } catch (Exception) { }
                });
            });
        }
    }
}
