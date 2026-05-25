using BookReaderApp.Models;
using BookReaderApp.Resources.Themes.Dark;
using BookReaderApp.Resources.Themes.Light;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using System.Linq;

namespace BookReaderApp.Services;

/// <summary>Синхронизирует <see cref="InterfaceTheme"/> из БД с <see cref="Application.UserAppTheme"/> и смерженной палитрой (<see cref="LightPalette"/> / <see cref="DarkPalette"/>).</summary>
internal static class InterfaceThemeManager
{
  static Application? _app;
  static AppTheme? _lastEffectiveTheme;

  /// <summary>Последний выбранный пользователем режим (как в <see cref="InterfaceSettings.Theme"/>).</summary>
  internal static InterfaceTheme StoredThemePreference { get; private set; } = InterfaceTheme.Light;

  /// <summary>Совместимость со старым именем API.</summary>
  internal static InterfaceTheme StoredThemeKind => StoredThemePreference;

  /// <summary>Активная системная/приложенческая тема — <see cref="AppTheme.Dark"/>.</summary>
  internal static bool IsDarkPaletteActive =>
      Application.Current?.RequestedTheme == AppTheme.Dark;

  /// <summary>Вызывается после смены эффективной светлой/тёмной палитры в <see cref="Application.Resources"/>.</summary>
  internal static event Action? ResolvedThemeChanged;

  /// <summary>Сохраняет ссылку на <see cref="Application"/> для режима Auto вне окна.</summary>
  internal static void Initialize(Application app) =>
      _app = app;

  /// <summary>Подставляет нужный словарь цветов/иконок в <see cref="Application.Resources"/> (совпадает с <see cref="Application.RequestedTheme"/>).</summary>
  internal static void SynchronizeMergedPalette(Application application)
  {
    bool dark = StoredThemePreference switch
    {
      InterfaceTheme.Dark => true,
      InterfaceTheme.Light => false,
      InterfaceTheme.Auto => application.RequestedTheme == AppTheme.Dark,
      _ => false,
    };

    var merged = application.Resources.MergedDictionaries;
    foreach (var rd in merged.Where(static d => d is LightPalette or DarkPalette).ToList())
      merged.Remove(rd);

    merged.Add(dark ? new DarkPalette() : new LightPalette());
  }

  /// <summary>Сохраняет выбор темы, задаёт <see cref="Application.UserAppTheme"/> и обновляет палитру.</summary>
  internal static void ApplyThemePreference(InterfaceTheme theme, DateTime _)
  {
    StoredThemePreference = theme;
    var application = Application.Current ?? _app;
    if (application == null)
      return;

    application.UserAppTheme = theme switch
    {
      InterfaceTheme.Dark => AppTheme.Dark,
      InterfaceTheme.Light => AppTheme.Light,
      InterfaceTheme.Auto => AppTheme.Unspecified,
      _ => AppTheme.Light
    };

    _lastEffectiveTheme = application.RequestedTheme;
    SynchronizeMergedPalette(application);
    ResolvedThemeChanged?.Invoke();
  }

  /// <summary>При режиме «авто» переопрашивает системную тему (смена Windows/Android в течение сессии).</summary>
  internal static void RefreshAutoIfNeeded(DateTime _)
  {
    if (StoredThemePreference != InterfaceTheme.Auto)
      return;

    var application = Application.Current ?? _app;
    if (application == null)
      return;

    application.UserAppTheme = AppTheme.Unspecified;

    AppTheme effective = application.RequestedTheme;
    if (_lastEffectiveTheme != effective)
    {
      _lastEffectiveTheme = effective;
      SynchronizeMergedPalette(application);
      ResolvedThemeChanged?.Invoke();
    }
  }
}
