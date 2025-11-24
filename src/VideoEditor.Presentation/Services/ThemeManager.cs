using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace VideoEditor.Presentation.Services
{
    public enum ApplicationTheme
    {
        Light,
        Dark
    }

    /// <summary>
    /// 控制全局主题资源切换
    /// </summary>
    public static class ThemeManager
    {
        private static readonly Uri LightThemeUri =
            new("/VideoEditor.Presentation;component/Resources/Themes/ThemeLight.xaml", UriKind.Relative);

        private static readonly Uri DarkThemeUri =
            new("/VideoEditor.Presentation;component/Resources/Themes/ThemeDark.xaml", UriKind.Relative);

        private const string ThemeDictionaryToken = "/Resources/Themes/";

        public static ApplicationTheme CurrentTheme { get; private set; } = ApplicationTheme.Dark;

        public static void ApplyTheme(ApplicationTheme theme)
        {
            var app = Application.Current;
            if (app == null)
            {
                return;
            }

            var dictionaries = app.Resources.MergedDictionaries;
            var existingThemeDictionary = dictionaries.FirstOrDefault(d =>
                d.Source != null &&
                d.Source.OriginalString.Contains(ThemeDictionaryToken, StringComparison.OrdinalIgnoreCase));

            if (existingThemeDictionary != null)
            {
                dictionaries.Remove(existingThemeDictionary);
            }

            var newThemeDictionary = new ResourceDictionary
            {
                Source = theme == ApplicationTheme.Dark ? DarkThemeUri : LightThemeUri
            };

            // 确保主题字典在GlobalStyles之前，这样GlobalStyles中的DynamicResource可以正确引用主题颜色
            var globalStylesDictionary = dictionaries.FirstOrDefault(d =>
                d.Source != null &&
                d.Source.OriginalString.Contains("/Resources/Styles/GlobalStyles.xaml", StringComparison.OrdinalIgnoreCase));

            if (globalStylesDictionary != null)
            {
                dictionaries.Remove(globalStylesDictionary);
                dictionaries.Add(newThemeDictionary);
                dictionaries.Add(globalStylesDictionary);
            }
            else
            {
                dictionaries.Add(newThemeDictionary);
            }

            CurrentTheme = theme;

            // 强制刷新所有窗口的视觉树
            foreach (Window window in app.Windows)
            {
                if (window != null)
                {
                    window.InvalidateVisual();
                    window.UpdateLayout();
                }
            }
        }
    }
}

