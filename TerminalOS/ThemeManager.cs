using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace TerminalOS
{
    public enum LotusTheme
    {
        Light,
        Dark,
        Custom
    }

    // ====================================================================
    // ThemeManager
    //
    // Owns the active color palette. Palettes come from three places:
    //   Lotus Light - the <Color> entries in Theme.xaml
    //   Lotus Dark  - the <Color> entries in Theme.Dark.xaml
    //   Custom      - generated here from a single user-picked color
    //
    // Applying a palette writes "<Token>Color" and "<Token>Brush" into
    // Application.Resources. XAML references the brushes with
    // {DynamicResource}, so the whole window repaints immediately. The
    // lotus:// HTML pages are themed through CSS variables built from the
    // same palette (see BuildCssVariables).
    // ====================================================================
    public static class ThemeManager
    {
        // Every themeable color. Each token needs a "<Token>Color" entry in
        // BOTH Theme.xaml and Theme.Dark.xaml, plus a line in BuildCustomPalette.
        public static readonly string[] Tokens =
        {
            "Frame", "Surface", "SurfaceSecondary", "SurfaceSoft", "SurfaceSofter", "BorderSoft",
            "TextPrimary", "TextSecondary", "TextMuted", "TextFaint", "TextDisabled",
            "Accent", "AccentDark", "AccentHover", "OnAccent",
            "HoverSoft", "HoverPink", "TabInactive", "OmniboxFocus",
            "FindBar", "FindBarForeground",
            "CloseHover", "CloseHoverForeground",
            "Secure", "Insecure", "Error",
            "InputBorder", "GhostHover",
            "LogoDeep", "LogoMid", "LogoLight",
            "Shadow"
        };

        // Pleasant starting point the first time someone picks "Custom".
        public const string DefaultCustomColor = "#DCE8F7";

        public static LotusTheme Current { get; private set; } = LotusTheme.Light;
        public static Color CustomColor { get; private set; } = ParseHex(DefaultCustomColor);
        public static bool IsDark { get; private set; }

        // Raised after a new palette has been applied (on the UI thread).
        public static event Action ThemeChanged;

        private static Dictionary<string, Color> _light;
        private static Dictionary<string, Color> _dark;
        private static Dictionary<string, Color> _active;

        // ------------------------------------------------------------
        // Startup
        // ------------------------------------------------------------

        // Call once from App.OnStartup, before any window is created.
        public static void Initialize()
        {
            // Theme.xaml is merged into Application.Resources by App.xaml, and the
            // ResourceDictionary indexer searches merged dictionaries, so this
            // snapshots the Lotus Light palette before anything overrides it.
            _light = ReadPalette(Application.Current.Resources);
            _dark = ReadPalette(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Theme.Dark.xaml", UriKind.Absolute)
            });

            LoadSettings();
            ApplyActivePalette();
        }

        private static Dictionary<string, Color> ReadPalette(ResourceDictionary source)
        {
            var palette = new Dictionary<string, Color>();
            foreach (var token in Tokens)
            {
                palette[token] = (Color)source[token + "Color"];
            }
            return palette;
        }

        // ------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------

        public static string KeyOf(LotusTheme theme)
        {
            switch (theme)
            {
                case LotusTheme.Dark: return "dark";
                case LotusTheme.Custom: return "custom";
                default: return "light";
            }
        }

        public static bool TryParseKey(string key, out LotusTheme theme)
        {
            switch ((key ?? "").Trim().ToLowerInvariant())
            {
                case "light": theme = LotusTheme.Light; return true;
                case "dark": theme = LotusTheme.Dark; return true;
                case "custom": theme = LotusTheme.Custom; return true;
                default: theme = LotusTheme.Light; return false;
            }
        }

        public static string CustomColorHex => ToHex(CustomColor);

        public static void SetTheme(LotusTheme theme)
        {
            if (Current == theme) return;
            Current = theme;
            ApplyActivePalette();
            SaveSettings();
            ThemeChanged?.Invoke();
        }

        // Picking a custom color always switches to the Custom theme.
        public static void SetCustomColor(string hex)
        {
            if (!TryParseHexStrict(hex, out var color)) return;
            if (Current == LotusTheme.Custom && color == CustomColor) return;

            CustomColor = color;
            Current = LotusTheme.Custom;
            ApplyActivePalette();
            SaveSettings();
            ThemeChanged?.Invoke();
        }

        // The "<style id='lotus-theme'>" body for lotus:// pages.
        public static string BuildCssVariables()
        {
            var sb = new StringBuilder();
            sb.Append(":root{color-scheme:").Append(IsDark ? "dark" : "light").Append(';');
            foreach (var token in Tokens)
            {
                sb.Append("--").Append(Kebab(token)).Append(':').Append(ToCss(_active[token])).Append(';');
            }
            // Lotus background image blend (see LotusBackgroundCss in MainWindow):
            // multiplied into the theme's own surface color, so it tints with any theme.
            sb.Append("--bg-blend:multiply;--bg-opacity:").Append(IsDark ? "0.9" : "0.55").Append(';');
            sb.Append('}');
            return sb.ToString();
        }

        // Script that live-updates an already-open lotus:// page (no reload,
        // so e.g. SysInfo keeps its output).
        public static string BuildApplyScript()
        {
            return "(function(){var s=document.getElementById('lotus-theme');" +
                   "if(s){s.textContent=" + JsonSerializer.Serialize(BuildCssVariables()) + ";}})();";
        }

        // "rgb(r,g,b)" for a token - used by the favicon, which can't use CSS variables.
        public static string Rgb(string token)
        {
            var c = _active[token];
            return $"rgb({c.R},{c.G},{c.B})";
        }

        // ------------------------------------------------------------
        // Applying a palette
        // ------------------------------------------------------------

        private static void ApplyActivePalette()
        {
            switch (Current)
            {
                case LotusTheme.Dark:
                    _active = _dark;
                    IsDark = true;
                    break;
                case LotusTheme.Custom:
                    _active = BuildCustomPalette(CustomColor);
                    IsDark = IsDarkBase(CustomColor);
                    break;
                default:
                    _active = _light;
                    IsDark = false;
                    break;
            }

            var resources = Application.Current.Resources;
            foreach (var token in Tokens)
            {
                var color = _active[token];
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                resources[token + "Color"] = color;
                resources[token + "Brush"] = brush;
            }
        }

        // ------------------------------------------------------------
        // Custom palette
        //
        // The picked color becomes the window "frame" color, i.e. the main
        // color of the browser. Everything else is derived from its hue:
        //   - light picks get a light theme with darker surfaces and dark text
        //   - dark picks get a dark theme with lighter hover states and light text
        // Text and accent colors are nudged until they meet contrast targets,
        // so any pick stays readable.
        // ------------------------------------------------------------

        private static bool IsDarkBase(Color c) => Luminance(c) < 0.2;

        private static Dictionary<string, Color> BuildCustomPalette(Color baseColor)
        {
            ToHsl(baseColor, out double h, out double s, out double l);
            bool dark = IsDarkBase(baseColor);

            double tint = Math.Min(s, 0.6);
            Color Shift(double delta) => FromHsl(h, tint, l + delta);

            // Neutral picks (grays) get a blue accent instead of a muddy one.
            double accentHue = s < 0.08 ? 210 : h;
            double accentSat = Math.Clamp(Math.Max(s, 0.55), 0.55, 0.9);
            Color Accent(double lightness) => FromHsl(accentHue, accentSat, lightness);
            double textSat = Math.Min(s, dark ? 0.3 : 0.35);
            Color Text(double lightness) => FromHsl(h, textSat, lightness);

            Color frame = baseColor;
            Color surface, secondary, soft, softer, borderSoft, hoverSoft, hoverPink,
                  omniFocus, findBar, ghostHover, inputBorder;
            Color textPrimary, textSecondary, textMuted, textFaint, textDisabled;
            Color accent, accentDark, accentHover, findFg;

            if (!dark)
            {
                surface = Shift(-0.11);
                secondary = Shift(-0.05);
                soft = frame;
                softer = FromHsl(h, tint, Math.Min(l + 0.02, 0.99));
                borderSoft = frame;
                hoverSoft = Shift(-0.02);
                hoverPink = surface;
                omniFocus = surface;
                ghostHover = Shift(-0.17);
                inputBorder = Shift(-0.07);

                textPrimary = EnsureContrast(Text(0.14), surface, 7.0, false);
                textSecondary = EnsureContrast(Text(0.26), surface, 4.5, false);
                textMuted = EnsureContrast(Text(0.40), surface, 4.0, false);
                textFaint = EnsureContrast(Text(0.60), surface, 2.5, false);
                textDisabled = Text(0.78);

                accent = Accent(0.58);
                accentDark = EnsureContrast(Accent(0.30), surface, 4.5, false);
                accentHover = Accent(0.50);

                findBar = Text(0.28);
                findFg = surface;
            }
            else
            {
                surface = Shift(-0.06);
                secondary = Shift(0.05);
                soft = Shift(0.10);
                softer = Shift(-0.08);
                borderSoft = Shift(0.10);
                hoverSoft = Shift(0.13);
                hoverPink = Shift(0.18);
                omniFocus = hoverSoft;
                ghostHover = Shift(0.24);
                inputBorder = Shift(0.16);

                textPrimary = EnsureContrast(Text(0.94), hoverSoft, 7.0, true);
                textSecondary = EnsureContrast(Text(0.82), hoverSoft, 4.5, true);
                textMuted = EnsureContrast(Text(0.68), hoverSoft, 4.0, true);
                textFaint = EnsureContrast(Text(0.52), hoverSoft, 2.5, true);
                textDisabled = Text(0.38);

                accent = EnsureContrast(Accent(0.66), frame, 3.0, true);
                accentDark = EnsureContrast(Accent(0.80), surface, 4.5, true);
                accentHover = Accent(0.74);

                findBar = Shift(0.15);
                findFg = textPrimary;
            }

            Color white = Color.FromRgb(255, 255, 255);
            Color nearBlack = Color.FromRgb(26, 26, 26);
            Color onAccent = Contrast(white, accent) >= Contrast(nearBlack, accent) ? white : nearBlack;

            return new Dictionary<string, Color>
            {
                ["Frame"] = frame,
                ["Surface"] = surface,
                ["SurfaceSecondary"] = secondary,
                ["SurfaceSoft"] = soft,
                ["SurfaceSofter"] = softer,
                ["BorderSoft"] = borderSoft,
                ["TextPrimary"] = textPrimary,
                ["TextSecondary"] = textSecondary,
                ["TextMuted"] = textMuted,
                ["TextFaint"] = textFaint,
                ["TextDisabled"] = textDisabled,
                ["Accent"] = accent,
                ["AccentDark"] = accentDark,
                ["AccentHover"] = accentHover,
                ["OnAccent"] = onAccent,
                ["HoverSoft"] = hoverSoft,
                ["HoverPink"] = hoverPink,
                ["TabInactive"] = surface,
                ["OmniboxFocus"] = omniFocus,
                ["FindBar"] = findBar,
                ["FindBarForeground"] = findFg,
                ["CloseHover"] = Color.FromRgb(0xE8, 0x11, 0x23),
                ["CloseHoverForeground"] = white,
                ["Secure"] = dark ? Color.FromRgb(0x6F, 0xCF, 0x8A) : Color.FromRgb(0x1E, 0x8E, 0x3E),
                ["Insecure"] = dark ? Color.FromRgb(0xF2, 0x8B, 0x82) : Color.FromRgb(0xD9, 0x30, 0x25),
                ["Error"] = dark ? Color.FromRgb(0xF2, 0xB8, 0xB5) : Color.FromRgb(0xB3, 0x26, 0x1E),
                ["InputBorder"] = inputBorder,
                ["GhostHover"] = ghostHover,
                ["LogoDeep"] = FromHsl(accentHue, 1.0, 0.75),
                ["LogoMid"] = FromHsl(accentHue, 1.0, 0.79),
                ["LogoLight"] = FromHsl(accentHue, 1.0, 0.83),
                ["Shadow"] = dark
                    ? Color.FromArgb(0x66, 0, 0, 0)
                    : Color.FromArgb(0x1F, accentDark.R, accentDark.G, accentDark.B)
            };
        }

        // ------------------------------------------------------------
        // Persistence  (%LocalAppData%\LotusBrowser\theme.json)
        // ------------------------------------------------------------

        private sealed class ThemeSettings
        {
            public string Theme { get; set; } = "light";
            public string CustomColor { get; set; } = DefaultCustomColor;
        }

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LotusBrowser", "theme.json");

        private static void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var settings = JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(SettingsPath));
                if (settings == null) return;

                if (TryParseHexStrict(settings.CustomColor, out var color)) CustomColor = color;
                if (TryParseKey(settings.Theme, out var theme)) Current = theme;
            }
            catch { }
        }

        private static void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new ThemeSettings
                {
                    Theme = KeyOf(Current),
                    CustomColor = CustomColorHex
                }));
            }
            catch { }
        }

        // ------------------------------------------------------------
        // Color helpers
        // ------------------------------------------------------------

        private static readonly Regex HexPattern = new Regex("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

        private static bool TryParseHexStrict(string hex, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex) || !HexPattern.IsMatch(hex.Trim())) return false;
            color = ParseHex(hex.Trim());
            return true;
        }

        private static Color ParseHex(string hex)
        {
            return Color.FromRgb(
                byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        // CSS color: #rrggbb, or rgba() when the color has transparency.
        private static string ToCss(Color c)
        {
            if (c.A == 255) return $"#{c.R:x2}{c.G:x2}{c.B:x2}";
            return string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},{3:0.###})", c.R, c.G, c.B, c.A / 255.0);
        }

        // SurfaceSecondary -> surface-secondary
        private static string Kebab(string token) =>
            Regex.Replace(token, "(?<!^)([A-Z])", "-$1").ToLowerInvariant();

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        private static byte ToByte(double v) => (byte)Math.Round(Clamp01(v) * 255.0);

        private static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            l = (max + min) / 2.0;

            if (d < 1e-9)
            {
                h = 0;
                s = 0;
                return;
            }

            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60.0;
        }

        private static Color FromHsl(double h, double s, double l)
        {
            h = ((h % 360.0) + 360.0) % 360.0;
            s = Clamp01(s);
            l = Clamp01(l);

            double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
            double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
            double m = l - c / 2.0;
            double r, g, b;

            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
        }

        // WCAG relative luminance.
        private static double Luminance(Color c)
        {
            double Channel(byte v)
            {
                double x = v / 255.0;
                return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        private static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        // Steps the color's lightness (lighter or darker) until it reaches the
        // contrast ratio against the background, or runs out of room.
        private static Color EnsureContrast(Color fg, Color bg, double minRatio, bool lighten)
        {
            ToHsl(fg, out double h, out double s, out double l);
            var current = fg;
            for (int i = 0; i < 60 && Contrast(current, bg) < minRatio; i++)
            {
                l += lighten ? 0.02 : -0.02;
                if (l <= 0 || l >= 1) { l = Clamp01(l); current = FromHsl(h, s, l); break; }
                current = FromHsl(h, s, l);
            }
            return current;
        }
    }
}
