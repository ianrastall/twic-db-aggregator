using System.Windows;
using System.Windows.Media;

namespace TWICDBAggregator
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            TryApplyAccentColor();
        }

        private void TryApplyAccentColor()
        {
            try
            {
                Color accent = SystemParameters.WindowGlassColor;

                Resources["PrimaryAccentColor"] = accent;

                SolidColorBrush accentBrush = new(accent);
                accentBrush.Freeze();
                Resources["PrimaryAccentBrush"] = accentBrush;

                SolidColorBrush accentLightBrush = new(accent) { Opacity = 0.9 };
                accentLightBrush.Freeze();
                Resources["PrimaryAccentLightBrush"] = accentLightBrush;
            }
            catch
            {
                // If the OS accent is unavailable, keep the defaults defined in XAML.
            }
        }
    }
}
