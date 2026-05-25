using Microsoft.Maui.Graphics;

namespace BookReaderApp.Services;

/// <summary>Синхронизация цвета строки состояния и навигации с панелями чтения / фоном текста.</summary>
public static class ReadingSystemChrome
{
  /// <summary>
  /// Иконки/текст системных панелей: тёмные, если фон панели «светлее» опорного цвета текста (как контраст текста к фону).
  /// </summary>
  public static bool ShouldUseDarkSystemIcons(Color barBackground, Color referenceForeground)
  {
    double lb = RelativeLuminance(barBackground);
    double lt = RelativeLuminance(referenceForeground);
    if (Math.Abs(lb - lt) < 0.02)
      return lb >= 0.5;
    return lb > lt;
  }

  static Color OpaqueRgb(Color c) => new(c.Red, c.Green, c.Blue, 1f);

  /// <param name="menusVisible">Панели инструментов видны — фон как у меню (PrimaryBackground), иначе фон чтения.</param>
  public static void ApplyReadingPage(
    bool menusVisible,
    Color primaryBackground,
    Color mainTextColor,
    Color readingBackground,
    Color readingTextColor)
  {
    Color barTint = menusVisible ? primaryBackground : readingBackground;
    Color barForContrast = barTint;
    Color fgRef = menusVisible ? mainTextColor : readingTextColor;
    if (!menusVisible)
    {
      barForContrast = OpaqueRgb(readingBackground);
      fgRef = OpaqueRgb(readingTextColor);
    }
    var darkIcons = ShouldUseDarkSystemIcons(barForContrast, fgRef);
#if ANDROID
    global::BookReaderApp.ReadingSystemBarsHelper.Apply(barTint, darkIcons);
#endif
  }

  /// <summary>Восстанавливает системные полосы под цвет главного фона приложения (выход со страницы чтения).</summary>
  public static void RestoreAppChrome(Color primaryBackground, Color mainTextColor)
  {
    var darkIcons = ShouldUseDarkSystemIcons(primaryBackground, mainTextColor);
#if ANDROID
    global::BookReaderApp.ReadingSystemBarsHelper.Apply(primaryBackground, darkIcons);
#endif
  }

  /// <summary>Относительная светимость цвета sRGB для оценки контраста (WCAG).</summary>
  static double RelativeLuminance(Color c)
  {
    static double Srgb(double u) => u <= 0.03928 ? u / 12.92 : Math.Pow((u + 0.055) / 1.055, 2.4);
    double r = Srgb(c.Red);
    double g = Srgb(c.Green);
    double b = Srgb(c.Blue);
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
  }
}
