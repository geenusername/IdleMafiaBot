using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace IdleMafiaBot
{

    public sealed class Frame : IDisposable
    {
        public readonly Bitmap Bitmap;
        readonly byte[] px;
        readonly int stride;

        public Frame(Bitmap bmp)
        {
            Bitmap = bmp;
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            stride = data.Stride;
            px = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);
        }

        public int Width { get { return Bitmap.Width; } }
        public int Height { get { return Bitmap.Height; } }

        public Color Pixel(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return Color.Black;
            int i = y * stride + x * 4;
            return Color.FromArgb(px[i + 2], px[i + 1], px[i]);
        }

        public bool Near(int x, int y, int rgb, int tol)
        {
            var c = Pixel(x, y);
            return Math.Abs(c.R - ((rgb >> 16) & 0xFF)) <= tol
                && Math.Abs(c.G - ((rgb >> 8) & 0xFF)) <= tol
                && Math.Abs(c.B - (rgb & 0xFF)) <= tol;
        }

        public double FillRatio(int x1, int x2, int y, int rgb, int tol)
        {
            int hit = 0, n = 0;
            for (int x = x1; x <= x2; x += 2) { n++; if (Near(x, y, rgb, tol)) hit++; }
            return n == 0 ? 0 : (double)hit / n;
        }

        public bool LooksEmpty()
        {
            int white = 0, n = 0;
            for (int i = 0; i < 48; i++)
                for (int j = 0; j < 27; j++)
                {
                    int k = (int)((j + 0.5) * Height / 27) * stride + (int)((i + 0.5) * Width / 48) * 4;
                    n++;
                    if (px[k] >= 250 && px[k + 1] >= 250 && px[k + 2] >= 250) white++;
                }
            return white * 10 >= n * 3;
        }

        public void Dispose() { Bitmap.Dispose(); }
    }

    public sealed class OcrLine
    {
        public string Text;
        public Rectangle Box;
        public int CenterX { get { return Box.X + Box.Width / 2; } }
        public int CenterY { get { return Box.Y + Box.Height / 2; } }
        public override string ToString() { return string.Format("{0},{1} {2}", Box.X, Box.Y, Text); }
    }

    public enum Prep { None, WhiteText, DarkText, WhiteBright, WhiteSoft, GreyText, Contrast }

    public static class Ocr
    {
        static OcrEngine engine;
        static string problem, language, warning;
        static bool engineChecked;
        static readonly object gate = new object();
        static byte[] buffer = new byte[0];

        public static int Pad = 16;

        public const string HowToFix = "To fix it, add English text recognition, then restart the bot (it checks once, when it starts; changing Windows' display "
            + "language or Roblox's language doesn't add it): in Windows Settings > Time & language > Language & region > Add a language, pick English (United States) "
            + "with its optional features ticked - or, more sure, run this in PowerShell as administrator (it needs internet and takes a few minutes): "
            + "Add-WindowsCapability -Online -Name \"Language.OCR~~~en-US~0.0.1.0\"";

        public static string Language { get { return language; } }

        public static string Check()
        {
            lock (gate)
            {
                if (engineChecked) return problem;
                engineChecked = true;
                try
                {
                    var e = Create();
                    problem = Unusable(e == null ? null : e.RecognizerLanguage);
                    if (problem == null) { engine = e; language = e.RecognizerLanguage.DisplayName; warning = NotEnglish(e.RecognizerLanguage); }
                }
                catch (Exception ex)
                {
                    problem = "Windows text recognition (OCR) doesn't work on this PC (" + ex.Message.Replace("\r", " ").Replace("\n", " ").Trim()
                            + "), so the bot can't read the game. " + HowToFix;
                }
                return problem;
            }
        }

        public static string Warning { get { return warning; } }

        internal static string NotEnglish(Windows.Globalization.Language lang)
        {
            if (lang == null || lang.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return null;
            return "Windows reads text in " + lang.DisplayName + " on this PC, not English, so the bot can miss some words in the game." + Have() + " " + HowToFix;
        }

        internal static string Unusable(Windows.Globalization.Language lang)
        {
            if (lang == null) return "Windows has no English text recognition (OCR) on this PC, so the bot can't read the game." + Have() + " " + HowToFix;
            if (lang.Script != "Latn")
                return "Windows only has text recognition (OCR) for " + lang.DisplayName + " on this PC, which can't read the game's English letters. " + HowToFix;
            return null;
        }

        static OcrEngine Create()
        {
            var e = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            if (e != null) return e;
            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
                if (lang.LanguageTag.StartsWith("en-", StringComparison.OrdinalIgnoreCase) && (e = OcrEngine.TryCreateFromLanguage(lang)) != null) return e;
            var profile = OcrEngine.TryCreateFromUserProfileLanguages();
            var other = ChooseOther(OcrEngine.AvailableRecognizerLanguages.ToList(), profile == null ? null : profile.RecognizerLanguage);
            return (other == null ? null : OcrEngine.TryCreateFromLanguage(other)) ?? profile;
        }

        internal static Windows.Globalization.Language ChooseOther(IList<Windows.Globalization.Language> installed, Windows.Globalization.Language profile)
        {
            if (profile != null && profile.Script == "Latn") return profile;
            return installed.FirstOrDefault(l => l.Script == "Latn");
        }

        static string Have()
        {
            try
            {
                var l = OcrEngine.AvailableRecognizerLanguages;
                return l.Count == 0 ? " It has no text recognition installed at all." : " It only has: " + string.Join(", ", l.Select(x => x.DisplayName)) + ".";
            }
            catch { return ""; }
        }

        static OcrEngine Engine
        {
            get
            {
                string p = Check();
                if (p != null) throw new InvalidOperationException(p);
                return engine;
            }
        }

        public static List<OcrLine> Read(Frame f, Rectangle area, int scale, Prep prep, bool asIs = false)
        {
            area.Intersect(new Rectangle(0, 0, f.Width, f.Height));
            var lines = new List<OcrLine>();
            if (area.Width < 4 || area.Height < 4) return lines;

            int pad = prep == Prep.None ? 0 : Pad;
            int fit = ((int)OcrEngine.MaxImageDimension - 2 * pad) / Math.Max(area.Width, area.Height);
            if (fit < 1) return lines;
            double zoom = Math.Min(ZoomFor(f, scale, asIs), fit);
            OcrResult result;
            using (var bmp = Prepare(f, area, zoom, prep)) result = Recognize(bmp);
            foreach (var line in result.Lines)
            {
                double x1 = double.MaxValue, y1 = double.MaxValue, x2 = 0, y2 = 0;
                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    x1 = Math.Min(x1, r.X - pad); y1 = Math.Min(y1, r.Y - pad);
                    x2 = Math.Max(x2, r.X - pad + r.Width); y2 = Math.Max(y2, r.Y - pad + r.Height);
                }
                lines.Add(new OcrLine
                {
                    Text = line.Text,
                    Box = new Rectangle(area.X + (int)(x1 / zoom), area.Y + (int)(y1 / zoom),
                                        (int)((x2 - x1) / zoom), (int)((y2 - y1) / zoom))
                });
            }
            return lines;
        }

        public static string ReadText(Frame f, Rectangle area, int scale, Prep prep, bool asIs = false)
        {
            var sb = new StringBuilder();
            foreach (var l in Read(f, area, scale, prep, asIs)) { if (sb.Length > 0) sb.Append(' '); sb.Append(l.Text); }
            return sb.ToString();
        }

        public const double BigWindow = 1.75;

        internal static bool FullZoom = false;

        internal static double ZoomFor(Frame f, int scale, bool asIs = false)
        {
            if (asIs || scale <= 1 || !Big(f)) return scale;
            return Math.Max(1, scale / Bigger(f));
        }

        static double Bigger(Frame f) { return Math.Min(f.Height / 1009.0, f.Width / 1790.0); }

        public static bool Big(Frame f) { return !FullZoom && Bigger(f) >= BigWindow; }

        public static void Release() { lock (gate) buffer = new byte[0]; }

        static OcrResult Recognize(Bitmap bmp)
        {
            lock (gate)
            {
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int n = data.Stride * bmp.Height;
                if (buffer.Length < n) buffer = new byte[n];
                Marshal.Copy(data.Scan0, buffer, 0, n);
                bmp.UnlockBits(data);
                using (var sb = SoftwareBitmap.CreateCopyFromBuffer(buffer.AsBuffer(0, n), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied))
                    return Engine.RecognizeAsync(sb).AsTask().Result;
            }
        }

        internal static Bitmap Prepare(Frame f, Rectangle area, double zoom, Prep prep)
        {

            int pad = prep == Prep.None ? 0 : Pad;
            int w = (int)Math.Round(area.Width * zoom), h = (int)Math.Round(area.Height * zoom);
            var big = new Bitmap(w + 2 * pad, h + 2 * pad, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(big))
            {
                g.Clear(Color.White);
                g.InterpolationMode = zoom == 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(f.Bitmap, new Rectangle(pad, pad, w, h), area, GraphicsUnit.Pixel);
            }
            if (prep == Prep.None) return big;

            var data = big.LockBits(new Rectangle(0, 0, big.Width, big.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            var row = new byte[stride];
            if (prep == Prep.Contrast) { Stretch(data, row, pad, w, h); big.UnlockBits(data); return big; }
            for (int y = 0; y < big.Height; y++)
            {
                var at = data.Scan0 + y * stride;
                Marshal.Copy(at, row, 0, stride);
                for (int x = 0; x < big.Width; x++)
                {
                    int i = x * 4;
                    bool inside = x >= pad && y >= pad && x < pad + w && y < pad + h;
                    byte b = row[i], gr = row[i + 1], r = row[i + 2];
                    int max = Math.Max(r, Math.Max(gr, b)), min = Math.Min(r, Math.Min(gr, b));
                    bool ink = inside && (prep == Prep.WhiteText ? (min >= 165 && max - min <= 70)
                                        : prep == Prep.WhiteBright ? (min >= 185 && max - min <= 70)
                                        : prep == Prep.WhiteSoft ? (min >= 130 && max - min <= 90)
                                        : prep == Prep.GreyText ? (min >= 70 && max <= 200 && max - min <= 30)
                                        : (max <= 95));
                    byte v = ink ? (byte)0 : (byte)255;
                    row[i] = v; row[i + 1] = v; row[i + 2] = v; row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, at, stride);
            }
            big.UnlockBits(data);
            return big;
        }

        static void Stretch(BitmapData data, byte[] row, int pad, int w, int h)
        {
            int stride = data.Stride;
            var count = new int[256];
            for (int y = pad; y < pad + h; y++)
            {
                Marshal.Copy(data.Scan0 + y * stride, row, 0, stride);
                for (int x = pad; x < pad + w; x++) { int i = x * 4; count[(row[i] + row[i + 1] + row[i + 2]) / 3]++; }
            }
            int face = 0;
            for (int v = 1; v < 256; v++) if (count[v] > count[face]) face = v;
            int far = 0, beyond = 0;
            for (int d = 255; d > 0 && beyond * 200 < w * h; d--)
            {
                if (face + d < 256) beyond += count[face + d];
                if (face - d >= 0) beyond += count[face - d];
                far = d;
            }
            for (int y = 0; y < h + 2 * pad; y++)
            {
                var at = data.Scan0 + y * stride;
                Marshal.Copy(at, row, 0, stride);
                for (int x = 0; x < w + 2 * pad; x++)
                {
                    int i = x * 4;
                    bool inside = x >= pad && y >= pad && x < pad + w && y < pad + h;
                    int off = Math.Abs((row[i] + row[i + 1] + row[i + 2]) / 3 - face);
                    byte v = inside && far > 0 ? (byte)(255 - Math.Min(255, off * 255 / far)) : (byte)255;
                    row[i] = v; row[i + 1] = v; row[i + 2] = v; row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, at, stride);
            }
        }

        public static List<OcrLine> ReadWords(Frame f, Rectangle area, int scale, Prep prep, bool asIs = false)
        {
            area.Intersect(new Rectangle(0, 0, f.Width, f.Height));
            var words = new List<OcrLine>();
            if (area.Width < 4 || area.Height < 4) return words;
            int pad = prep == Prep.None ? 0 : Pad;
            int fit = ((int)OcrEngine.MaxImageDimension - 2 * pad) / Math.Max(area.Width, area.Height);
            if (fit < 1) return words;
            double zoom = Math.Min(ZoomFor(f, scale, asIs), fit);
            OcrResult result;
            using (var bmp = Prepare(f, area, zoom, prep)) result = Recognize(bmp);
            foreach (var line in result.Lines)
                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    words.Add(new OcrLine
                    {
                        Text = w.Text,
                        Box = new Rectangle(area.X + (int)((r.X - pad) / zoom), area.Y + (int)((r.Y - pad) / zoom), (int)(r.Width / zoom), (int)(r.Height / zoom)),
                    });
                }
            return words;
        }
    }

    public static class Parse
    {

        public static string Key(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in (s ?? "").ToUpperInvariant())
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString().Replace("VV", "W").Replace("0", "O").Replace("1", "I").Replace("L", "I").Replace("5", "S");
        }

        public static bool Has(string text, string phrase) { return Key(text).Contains(Key(phrase)); }

        public static string Digits(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s ?? "")
            {
                char c = ch;
                if (c == 'O' || c == 'o' || c == 'D') c = '0';
                else if (c == 'I' || c == 'l' || c == '|' || c == 'i') c = '1';
                else if (c == 'S' || c == 's') c = '5';
                else if (c == 'B') c = '8';
                if (char.IsDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        public static bool Fraction(string s, out int cur, out int max, bool whole = false)
        {
            cur = max = 0;
            if (s == null) return false;
            var m = Regex.Match(s, whole ? @"(?<![A-Za-z0-9])([0-9OoIlSB]{1,7})\s*[/|]\s*([0-9OoIlSB]{1,7})(?![0-9])"
                                         : @"([0-9OoIlSB]{1,7})\s*[/|]\s*([0-9OoIlSB]{1,7})");
            if (!m.Success) return false;
            return int.TryParse(Digits(m.Groups[1].Value), out cur) && int.TryParse(Digits(m.Groups[2].Value), out max);
        }

        public static bool Money(string s, out double value)
        {
            value = 0;
            if (s == null) return false;
            const string D = "[0-9OoIlS]";

            string num, suffix = "";
            var m = Regex.Match(s, @"\$\s?(" + D + "{1,3}(?:[.,]" + D + @"{1,2})?)\s?([KMBT])(?![A-Za-z])", RegexOptions.IgnoreCase);
            if (m.Success) { num = m.Groups[1].Value.Replace(',', '.'); suffix = m.Groups[2].Value; }
            else
            {
                m = Regex.Match(s, @"\$\s?(" + D + "{1,3}(?:," + D + @"{3})*)(?![0-9OoIlS.,])", RegexOptions.IgnoreCase);
                if (!m.Success) return false;
                num = m.Groups[1].Value.Replace(",", "");
            }
            var sb = new StringBuilder();
            foreach (char ch in num) sb.Append(ch == '.' ? "." : Digits(ch.ToString()));
            double v;
            if (!double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return false;
            switch (suffix.ToUpperInvariant())
            {
                case "K": v *= 1e3; break;
                case "M": v *= 1e6; break;
                case "B": v *= 1e9; break;
                case "T": v *= 1e12; break;
            }
            value = v;
            return true;
        }

        public static int Duration(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            var m = Regex.Match(s, @"(\d{1,2}):(\d{2}):(\d{2})");
            if (m.Success) return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value);

            m = Regex.Match(s, @"(?<![\d:.])(\d{1,3}):(\d{2})(?![\d:.])");
            if (m.Success) return int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
            int total = 0; bool any = false;

            string u0 = s.ToUpperInvariant().Replace("MLN", "MIN").Replace("M1N", "MIN").Replace("HRZ", "HR")
                         .Replace("1-IP", "HR").Replace("1-IR", "HR").Replace("HQ", "HR").Replace("HÄ", "HR");
            bool hours = false;
            foreach (Match p in Regex.Matches(u0, @"(?<![\$\d.,])(?=[0-9OISB]{0,2}[0-9])([0-9OISB]{1,3})\s*(DAYS?|HRS?|HOURS?|MINS?|MINUTES?|SECS?|SECONDS?)(?![A-Z])"))
            {
                int n;
                if (!int.TryParse(Digits(p.Groups[1].Value), out n)) continue;
                string u = p.Groups[2].Value;
                if (u.StartsWith("D")) total += n * 86400;
                else if (u.StartsWith("H")) { total += n * 3600; hours = true; }
                else if (u.StartsWith("M")) total += n * 60;
                else total += n;
                any = true;
            }

            if (!hours)
                foreach (Match p in Regex.Matches(u0, @"(?<![A-Z0-9\$.,])([IOL]{1,2})\s*(HRS?|HOURS?)(?![A-Z])"))
                {
                    int n;
                    if (!int.TryParse(Digits(p.Groups[1].Value.Replace('L', 'I')), out n) || n == 0) continue;
                    total += n * 3600;
                    any = true;
                    break;
                }
            return any ? total : -1;
        }

        public static int Countdown(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            if (Regex.IsMatch(s, @"\d:\d{2}")) return Duration(s);
            int total = 0; bool any = false;
            foreach (Match p in Regex.Matches(s, @"(?<![A-Za-z0-9])([0-9lIO]{1,3})\s*([dhms])(?![a-z])"))
            {
                int n;
                if (!int.TryParse(Digits(p.Groups[1].Value), out n)) continue;
                switch (p.Groups[2].Value) { case "d": total += n * 86400; break; case "h": total += n * 3600; break; case "m": total += n * 60; break; default: total += n; break; }
                any = true;
            }
            return any ? total : Duration(s);
        }

        public static string Short(int seconds)
        {
            if (seconds < 60) return Math.Max(0, seconds) + " s";
            if (seconds < 3600) return (seconds / 60) + " min";
            if (seconds < 86400) return (seconds / 3600) + " h" + (seconds % 3600 >= 60 ? " " + (seconds % 3600 / 60) + " min" : "");
            return (seconds / 86400) + " d" + (seconds % 86400 >= 3600 ? " " + (seconds % 86400 / 3600) + " h" : "");
        }

        public static int RegenTimer(string s)
        {
            var m = Regex.Match(s ?? "", @"\+\s*(\d)\s*(\d{1,2})\s*[:.]\s*(\d{2})");
            return m.Success ? int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value) : -1;
        }

        public static int FirstInt(string s)
        {
            var m = Regex.Match(s ?? "", @"\d+");
            int v;
            return m.Success && int.TryParse(m.Value, out v) ? v : -1;
        }
    }
}
