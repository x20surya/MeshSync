using System;
using System.Windows.Media;
using CoreLib.Diagnostics;
using Microsoft.Win32;
// Both WinForms and WPF are referenced, so Application must be disambiguated.
using WpfApp = System.Windows.Application;

namespace WinDaemon
{
    /// <summary>
    /// Light is the canonical identity. The dark variant exists so the window does not
    /// glare on a machine running Windows in dark mode; it overwrites the palette colours
    /// in place, and every brush in MeshTheme.xaml binds to them dynamically.
    /// </summary>
    public static class ThemeManager
    {
        private static readonly (string Key, string Light, string Dark)[] Palette =
        {
            ("C.Bg",           "#F7F6F3", "#17181A"),
            ("C.Surface",      "#FFFFFF", "#1F2124"),
            ("C.SurfaceAlt",   "#F1EFEA", "#282B2E"),
            ("C.Border",       "#E5E1DA", "#2E3134"),
            ("C.BorderStrong", "#D3CEC5", "#3E4246"),
            ("C.Text",         "#262523", "#E9E7E3"),
            ("C.TextMuted",    "#77726A", "#9C978F"),
            ("C.TextFaint",    "#A39D94", "#726E68"),
            ("C.Accent",       "#2F7A6B", "#4FA894"),
            ("C.AccentHover",  "#28695C", "#5FBAA5"),
            ("C.AccentSoft",   "#E6F1EE", "#1C2E2A"),
            ("C.Warn",         "#B0722F", "#D69A5A"),
            ("C.WarnSoft",     "#FBF1E3", "#2E2620"),
            ("C.Danger",       "#B0524A", "#D4776E"),
            ("C.DangerSoft",   "#FAEBE9", "#2E2220"),
        };

        public enum Preference { System, Light, Dark }

        private const string SettingsKeyPath = @"SOFTWARE\MeshSync";
        private static WpfApp? _app;

        public static bool IsDark { get; private set; }
        public static Preference Current { get; private set; } = Preference.System;

        public static void Apply(WpfApp app)
        {
            _app = app;
            Current = LoadPreference();
            IsDark = Resolve(Current);
            Repaint(app);

            try
            {
                SystemEvents.UserPreferenceChanged += (_, e) =>
                {
                    if (e.Category != UserPreferenceCategory.General) return;
                    if (Current != Preference.System) return; // pinned, ignore the OS

                    bool dark = SystemPrefersDark();
                    if (dark == IsDark) return;

                    IsDark = dark;
                    app.Dispatcher.Invoke(() => Repaint(app));
                    Log.Write("UI", $"Followed Windows to the {(dark ? "dark" : "light")} theme.");
                };
            }
            catch (Exception ex)
            {
                Log.Write("UI", "Could not subscribe to theme changes", ex);
            }
        }

        public static void SetPreference(Preference preference)
        {
            Current = preference;
            SavePreference(preference);

            bool dark = Resolve(preference);
            if (dark == IsDark || _app == null) return;

            IsDark = dark;
            _app.Dispatcher.Invoke(() => Repaint(_app));
            Log.Write("UI", $"Appearance set to {preference}.");
        }

        private static bool Resolve(Preference preference) => preference switch
        {
            Preference.Light => false,
            Preference.Dark => true,
            _ => SystemPrefersDark()
        };

        private static Preference LoadPreference()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                return (key?.GetValue("Appearance") as string) switch
                {
                    "Light" => Preference.Light,
                    "Dark" => Preference.Dark,
                    _ => Preference.System
                };
            }
            catch
            {
                return Preference.System;
            }
        }

        private static void SavePreference(Preference preference)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue("Appearance", preference.ToString());
            }
            catch (Exception ex)
            {
                Log.Write("UI", "Saving the appearance preference failed", ex);
            }
        }

        private static void Repaint(WpfApp app)
        {
            foreach (var (key, light, dark) in Palette)
            {
                var value = (System.Windows.Media.Color)
                    System.Windows.Media.ColorConverter.ConvertFromString(IsDark ? dark : light)!;
                app.Resources[key] = value;
            }
        }

        private static bool SystemPrefersDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                // The value is 1 when apps should use the *light* theme.
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
