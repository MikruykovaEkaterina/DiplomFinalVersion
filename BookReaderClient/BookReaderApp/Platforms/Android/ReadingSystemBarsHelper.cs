#if ANDROID

using Android.OS;
using Android.Views;
using AndroidX.Core.View;
using Microsoft.Maui.ApplicationModel;
using MauiColor = Microsoft.Maui.Graphics.Color;

namespace BookReaderApp;

/// <summary>Цвет строки состояния и навигации на Android (не вложенный namespace …Android, чтобы не конфликтовать с Android.*).</summary>
public static class ReadingSystemBarsHelper
{
  public static void Apply(MauiColor barColor, bool darkIcons)
  {
    var act = Platform.CurrentActivity;
    var window = act?.Window;
    if (window == null)
      return;

    int a = (int)(byte)(barColor.Alpha * 255);
    int r = (int)(byte)(barColor.Red * 255);
    int g = (int)(byte)(barColor.Green * 255);
    int b = (int)(byte)(barColor.Blue * 255);
    var androidColor = global::Android.Graphics.Color.Argb(a, r, g, b);

    try
    {
      window.SetStatusBarColor(androidColor);
      window.SetNavigationBarColor(androidColor);
      // Иначе система может наложить свой «контрастный» слой и визуально разойтись с цветом текста/фона чтения.
      if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
      {
        try
        {
          window.NavigationBarContrastEnforced = false;
        }
        catch
        {
        }
      }
      if ((int)Build.VERSION.SdkInt >= 35)
      {
        try
        {
          window.StatusBarContrastEnforced = false;
        }
        catch
        {
        }
      }
      var ctrl = WindowCompat.GetInsetsController(window, window.DecorView);
      if (ctrl != null)
      {
        ctrl.AppearanceLightStatusBars = darkIcons;
        ctrl.AppearanceLightNavigationBars = darkIcons;
      }
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[ReadingSystemBars] {ex.Message}");
    }
  }
}

#endif
