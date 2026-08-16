using System;
using CoreLib.Diagnostics;
using Microsoft.Win32;
using WpfApp = System.Windows.Application;
using WpfResourceDictionary = System.Windows.ResourceDictionary;

namespace WinDaemon
{
    /// <summary>
    /// Light is the canonical identity; the dark variant exists so the window does not glare
    /// on a machine running Windows in dark mode.
    ///
    /// Switching swaps the whole palette dictionary rather than reassigning colour keys.
    /// Reassigning looked correct and even logged correctly, but nothing repainted: a brush
    /// that has already been handed to a rendered element does not re-resolve when the
    /// colour behind it changes. Replacing a merged dictionary does invalidate every
    /// DynamicResource pointing into it, which is what actually redraws the window.
    /// </summary>
    public static class ThemeManager
    {
        public enum Preference { System, Light, Dark }

        private const string SettingsKeyPath = @"SOFTWARE\MeshSync";
        private const string LightPalette = "pack://application:,,,/WinDaemon;component/Themes/Palette.Light.xaml";
        private const string DarkPalette = "pack://application:,,,/WinDaemon;component/Themes/Palette.Dark.xaml";

        private static WpfApp? _app;
        private static WpfResourceDictionary? _palette;

        public static bool IsDark { get; private set; }
        public static Preference Current { get; private set; } = Preference.System;

        public static void Apply(WpfApp app)
        {
            _app = app;
            Current = LoadPreference();
            IsDark = Resolve(Current);

            _palette = new WpfResourceDictionary { Source = new Uri(IsDark ? DarkPalette : LightPalette) };
            // Inserted first so the styles merged after it can resolve these keys.
            app.Resources.MergedDictionaries.Insert(0, _palette);

            try
            {
                SystemEvents.UserPreferenceChanged += (_, e) =>
                {
                    if (e.Category != UserPreferenceCategory.General) return;
                    if (Current != Preference.System) return; // pinned, ignore the OS

                    bool dark = SystemPrefersDark();
                    if (dark == IsDark) return;

                    IsDark = dark;
                    app.Dispatcher.Invoke(SwapPalette);
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
            _app.Dispatcher.Invoke(SwapPalette);
            Log.Write("UI", $"Appearance set to {preference}.");
        }

        private static void SwapPalette()
        {
            if (_app == null) return;

            var replacement = new WpfResourceDictionary
            {
                Source = new Uri(IsDark ? DarkPalette : LightPalette)
            };

            var dictionaries = _app.Resources.MergedDictionaries;
            int index = _palette == null ? -1 : dictionaries.IndexOf(_palette);

            if (index >= 0) dictionaries[index] = replacement;
            else dictionaries.Insert(0, replacement);

            _palette = replacement;
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
