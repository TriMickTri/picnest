using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using LibVLCSharp.Shared;
using PicNest.Services;

namespace PicNest;

public partial class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        ApplyTheme(PreferencesStore.Current.Theme);
        Core.Initialize();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }

    public void ApplyTheme(AppThemePreference preference)
    {
        RequestedThemeVariant = preference == AppThemePreference.Dark ? ThemeVariant.Dark : ThemeVariant.Light;

        SetBrush("AppPageBackgroundBrush", preference == AppThemePreference.Dark ? "#12191C" : "#F8FAFA");
        SetBrush("AppSurfaceBrush", preference == AppThemePreference.Dark ? "#1A2327" : "#FFFFFF");
        SetBrush("AppToolbarBrush", preference == AppThemePreference.Dark ? "#202C31" : "#EDF2F3");
        SetBrush("AppBorderBrush", preference == AppThemePreference.Dark ? "#34454B" : "#D5DEDF");
        SetBrush("AppTextBrush", preference == AppThemePreference.Dark ? "#E5EEF0" : "#26333A");
        SetBrush("AppMutedTextBrush", preference == AppThemePreference.Dark ? "#A6B5BA" : "#738287");
        SetBrush("AppSubtleTextBrush", preference == AppThemePreference.Dark ? "#BAC7CB" : "#66777D");
        SetBrush("AppBadgeBrush", preference == AppThemePreference.Dark ? "#2A3A40" : "#E4EBED");
        SetBrush("AppBadgeTextBrush", preference == AppThemePreference.Dark ? "#C1D0D4" : "#52666D");
        SetBrush("AppRaisedSurfaceBrush", preference == AppThemePreference.Dark ? "#26343A" : "#FFFFFF");
        SetBrush("AppHoverBrush", preference == AppThemePreference.Dark ? "#2A4553" : "#E5F3FF");
        SetBrush("AppSelectedBrush", preference == AppThemePreference.Dark ? "#245069" : "#CCE8FF");
    }

    private void SetBrush(string key, string color)
    {
        if (Resources[key] is SolidColorBrush brush)
            brush.Color = Color.Parse(color);
    }
}
