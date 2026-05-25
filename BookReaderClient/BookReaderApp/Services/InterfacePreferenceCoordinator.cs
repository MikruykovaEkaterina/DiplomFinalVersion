using System.Globalization;
using BookReaderApp.Localization;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using Microsoft.Maui.Controls;

namespace BookReaderApp.Services;

/// <summary>Загрузка <see cref="InterfaceSettings"/> из БД при старте и применение темы, языка и размера шрифта интерфейса.</summary>
public static class InterfacePreferenceCoordinator
{
  /// <summary>Минимальный и максимальный размер шрифта интерфейса (pt) из настроек.</summary>
  public const int InterfaceFontSizeMin = 14;

  /// <summary>Максимальный размер шрифта интерфейса (pt) из настроек.</summary>
  public const int InterfaceFontSizeMax = 19;

  /// <summary>
  /// «Средний» размер (pt) и база масштаба: при значении 16 производные размеры и иконки хрома совпадают с прежним фиксированным UI (иконки 18 pt и т.д.).
  /// Шкала выбора: 14–15 малый, 16–17 средний, 18–19 крупный.
  /// </summary>
  public const int InterfaceFontSizeNormal = 16;

  /// <summary>Текущие размеры UI (pt), синхронны с ключами Application.Resources после <see cref="ApplyInterfaceFontSizes"/>.</summary>
  public static double EffectiveMainFontSize { get; private set; } = InterfaceFontSizeNormal;

  /// <inheritdoc cref="EffectiveMainFontSize" />
  public static double EffectiveButtonFontSize { get; private set; } = InterfaceFontSizeNormal;

  /// <inheritdoc cref="EffectiveMainFontSize" />
  public static double EffectiveAppTitleFontSize { get; private set; } = 18;

  /// <inheritdoc cref="EffectiveMainFontSize" />
  public static double EffectiveChromeIconSize { get; private set; } = 18;

  /// <inheritdoc cref="EffectiveMainFontSize" />
  public static double EffectiveChromeTapMinSize { get; private set; } = 36;

  /// <summary>Стрелки переключения версии на карточке (Swipe).</summary>
  public static double EffectiveSwipeVersionArrowSize { get; private set; } = 28;

  /// <inheritdoc cref="EffectiveMainFontSize" />
  public static double EffectiveSplashActivitySize { get; private set; } = 36;

  /// <summary>Иконки в строках (карточка «Описание», развёрнуть заметку) — чуть меньше тулбара, база 20 pt при «среднем» кегле.</summary>
  public static double EffectiveInlineIconSize { get; private set; } = 20;

  /// <summary>Минимальная зона нажатия для <see cref="EffectiveInlineIconSize"/> (меньше, чем <see cref="EffectiveChromeTapMinSize"/>).</summary>
  public static double EffectiveInlineTapMinSize { get; private set; } = 32;

  /// <summary>Все «крестики» закрытия (модалки, заметки, тост и т.д.): один глиф, тот же расчёт, что <see cref="EffectiveInlineIconSize"/>, меньше основного хрома.</summary>
  public static double EffectiveDismissIconSize { get; private set; } = 20;

  /// <inheritdoc cref="EffectiveDismissIconSize"/>
  public static double EffectiveDismissTapMinSize { get; private set; } = 32;

  /// <summary>Мин. высота шапки expander на карточке книги (44 pt при кегле 16).</summary>
  public static double EffectiveBookCardExpanderHeaderMinHeight { get; private set; } = 44;

  static IDispatcherTimer? _autoThemeTimer;
  static bool _started;

  /// <summary>Вызывает <see cref="InterfaceThemeManager.Initialize"/>, задаёт ресурсы шрифтов и загружает настройки из БД; раз в минуту обновляет авто-тему.</summary>
  public static void Start(Application app)
  {
    ArgumentNullException.ThrowIfNull(app);
    if (_started)
      return;
    _started = true;

    InterfaceThemeManager.Initialize(app);
    ApplyInterfaceFontSizes(new InterfaceSettings().InterfaceFontSize);
    _ = ApplyFromDatabaseAsync();

    var dispatcher = app.Dispatcher;
    if (_autoThemeTimer == null && dispatcher != null)
    {
      _autoThemeTimer = dispatcher.CreateTimer();
      _autoThemeTimer.Interval = TimeSpan.FromMinutes(1);
      _autoThemeTimer.Tick += (_, _) =>
          InterfaceThemeManager.RefreshAutoIfNeeded(DateTime.Now);
      _autoThemeTimer.Start();
    }
  }

  /// <summary>Считывает интерфейсные настройки из БД и применяет культуру, палитру и кегль на главном потоке.</summary>
  public static async Task ApplyFromDatabaseAsync()
  {
    try
    {
      var db = ServiceLocator.Get<IDatabaseService>();
      if (db == null)
        return;
      var s = await db.GetInterfaceSettingsAsync().ConfigureAwait(false);
      await MainThread.InvokeOnMainThreadAsync(() =>
      {
        ApplyCulture(s.InterfaceLanguage);
        InterfaceThemeManager.ApplyThemePreference(s.Theme, DateTime.Now);
        ApplyInterfaceFontSizes(s.InterfaceFontSize);
        Strings.Culture = LocalizationResourceManager.Instance.CurrentCulture;
      }).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[InterfacePreferenceCoordinator] {ex.Message}");
    }
  }

  /// <summary>Устанавливает культуры <see cref="LocalizationResourceManager"/> и строкового ресурсного менеджера.</summary>
  public static void ApplyCulture(string? languageCode)
  {
    var c = NormalizeCulture(languageCode);
    LocalizationResourceManager.Instance.CurrentCulture = c;
    Strings.Culture = c;
  }

  /// <summary>Нормализует ISO/подписи к <see cref="CultureInfo"/> ru или en.</summary>
  static CultureInfo NormalizeCulture(string? raw) =>
      raw switch
      {
        "en" or "English" => CultureInfo.GetCultureInfo("en"),
        _ => CultureInfo.GetCultureInfo("ru")
      };

  /// <summary>Базовый шаг — значение из БД (pt); производные размеры масштабируются относительно <see cref="InterfaceFontSizeNormal"/>.</summary>
  public static void ApplyInterfaceFontSizes(int fontSizePt)
  {
    var app = Application.Current;
    if (app == null)
      return;

    double baseline = InterfaceFontSizeNormal;
    double f = Math.Clamp(fontSizePt, InterfaceFontSizeMin, InterfaceFontSizeMax);
    double scale = f / baseline;

    EffectiveMainFontSize = f;
    EffectiveButtonFontSize = Math.Round(16 * scale);
    // Заголовки экранов и названия книг/заметок в списках — ровно на 2 pt крупнее основного UI (MainFontSize).
    double emphasisTitlePt = Math.Min(f + 2, 24);
    EffectiveAppTitleFontSize = emphasisTitlePt;
    EffectiveChromeIconSize = Math.Clamp(Math.Round(18 * scale), 13, 30);
    EffectiveChromeTapMinSize = Math.Max(26.0, Math.Round(36 * scale));
    EffectiveSwipeVersionArrowSize = Math.Clamp(Math.Round(28 * scale), 20, 40);
    EffectiveSplashActivitySize = Math.Clamp(Math.Round(36 * scale), 26, 58);
    double compactGlyphPx = Math.Clamp(Math.Round(20 * scale), 14, 30);
    double compactTapPx = Math.Max(26.0, Math.Round(32 * scale));
    EffectiveInlineIconSize = compactGlyphPx;
    EffectiveInlineTapMinSize = compactTapPx;
    EffectiveDismissIconSize = compactGlyphPx;
    EffectiveDismissTapMinSize = compactTapPx;
    EffectiveBookCardExpanderHeaderMinHeight = Math.Max(38.0, Math.Round(44 * scale));

    app.Resources["MainFontSize"] = f;
    app.Resources["LabelFontSize"] = f;
    app.Resources["ButtonFontSize"] = Math.Round(16 * scale);
    app.Resources["AppTitleFontSize"] = emphasisTitlePt;
    app.Resources["BookTitleFontSize"] = emphasisTitlePt;
    app.Resources["BookCoverFontSize"] = Math.Round(14 * scale);
    app.Resources["ReadingTitleFontSize"] = Math.Round(22 * scale);
    app.Resources["ReadingAuthorFontSize"] = Math.Round(14 * scale);
    app.Resources["ReadingPageInfoFontSize"] = Math.Round(12 * scale);
    app.Resources["ReadingTextFontSize"] = Math.Round(16 * scale);
    app.Resources["ReadingProgressFontSize"] = Math.Round(12 * scale);
    app.Resources["NoteTitleFontSize"] = emphasisTitlePt;
    app.Resources["NoteTextFontSize"] = Math.Round(14 * scale);
    app.Resources["UiChromeIconSize"] = EffectiveChromeIconSize;
    app.Resources["UiChromeTapMinSize"] = EffectiveChromeTapMinSize;
    app.Resources["UiSwipeVersionArrowSize"] = EffectiveSwipeVersionArrowSize;
    app.Resources["UiSplashActivitySize"] = EffectiveSplashActivitySize;
    app.Resources["UiInlineIconSize"] = EffectiveInlineIconSize;
    app.Resources["UiInlineTapMinSize"] = EffectiveInlineTapMinSize;
    app.Resources["UiDismissIconSize"] = EffectiveDismissIconSize;
    app.Resources["UiDismissTapMinSize"] = EffectiveDismissTapMinSize;
    app.Resources["UiBookCardExpanderHeaderMinHeight"] = EffectiveBookCardExpanderHeaderMinHeight;
  }
}
