using BookReaderApp.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace BookReaderApp.Services;

/// <summary>Цвета баннеров; значения берутся из активной палитры (<c>Snackbar*</c>), иначе запасной набор.</summary>
public static class NotificationColors
{
  /// <summary>Возвращает цвет фона и текста баннера по уровню (из ресурсов темы или запасные RGB).</summary>
  public static (Color Background, Color Foreground) ForSeverity(AppNotificationSeverity severity)
  {
    var app = Application.Current;
    if (app != null)
    {
      var (bgKey, fgKey) = KeysFor(severity);
      if (app.Resources.TryGetValue(bgKey, out var bgObj) && bgObj is Color bg
          && app.Resources.TryGetValue(fgKey, out var fgObj) && fgObj is Color fg)
        return (bg, fg);
    }

    return severity switch
    {
      AppNotificationSeverity.Error => (Color.FromArgb("#FFEBEE"), Color.FromArgb("#B71C1C")),
      AppNotificationSeverity.Warning => (Color.FromArgb("#FFF8E1"), Color.FromArgb("#E65100")),
      AppNotificationSeverity.Success => (Color.FromArgb("#E8F5E9"), Color.FromArgb("#1B5E20")),
      _ => (Color.FromArgb("#F5F5F5"), Color.FromArgb("#212121"))
    };
  }

  /// <summary>Ключи <c>Snackbar*</c> в палитре для цветовой пары по уровню.</summary>
  static (string Bg, string Fg) KeysFor(AppNotificationSeverity severity) =>
      severity switch
      {
        AppNotificationSeverity.Error => ("SnackbarErrorBackground", "SnackbarErrorForeground"),
        AppNotificationSeverity.Warning => ("SnackbarWarningBackground", "SnackbarWarningForeground"),
        AppNotificationSeverity.Success => ("SnackbarSuccessBackground", "SnackbarSuccessForeground"),
        _ => ("SnackbarInfoBackground", "SnackbarInfoForeground")
      };

  /// <summary>Размытая окантовка баннера на основе цвета акцента уровня.</summary>
  public static Color BorderForSeverity(AppNotificationSeverity severity)
  {
    var (_, fg) = ForSeverity(severity);
    return fg.WithAlpha(0.42f);
  }
}
