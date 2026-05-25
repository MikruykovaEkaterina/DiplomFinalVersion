using BookReaderApp.Helpers;
using BookReaderApp.Models;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Graphics;

namespace BookReaderApp.Services;

/// <summary>Параметры отображения текста книги: страницы, выравнивание, цвета.</summary>
public static class TextReadingLayout
{
  /// <summary>Кегль по умолчанию (pt).</summary>
  public const int DefaultFontSize = 16;
  /// <summary>Минимальный кегль текста (pt).</summary>
  public const int MinFontSize = 10;
  /// <summary>Максимальный кегль текста (pt).</summary>
  public const int MaxFontSize = 28;

  /// <summary>Отступ страницы по умолчанию (логические единицы MAUI).</summary>
  public const int DefaultMargins = 16;
  /// <summary>Минимальный отступ.</summary>
  public const int MinMargins = 8;
  /// <summary>Максимальный отступ.</summary>
  public const int MaxMargins = 48;
  /// <summary>Шаг изменения отступа по кнопкам настроек.</summary>
  public const int MarginStep = 4;

  /// <summary>Режим «Горизонтальное листание» в настройках (колонки WebView, не ориентация устройства).</summary>
  public static bool IsHorizontalScrollingMode(TextSettings? s) =>
      IsHorizontalScrollingMode(s?.ScrollingMode);

  public static bool IsHorizontalScrollingMode(string? scrollingMode) =>
      TextSettingsStoredFormats.NormalizeScrolling(scrollingMode) ==
      TextSettingsStoredFormats.ScrollingHorizontal;

  /// <summary>Логический размер области текста для оценки без WebView: экран минус запас под хром (ближе к ReadingLayer).</summary>
  public static (double Width, double Height) GetDefaultReadingViewportLogicalDp()
  {
    double w = 400, h = 700;
    try
    {
      var info = DeviceDisplay.MainDisplayInfo;
      double d = info.Density > 0 ? info.Density : 1.0;
      w = info.Width / d;
      h = info.Height / d;
    }
    catch
    {
      // оставляем запасные значения
    }
    w *= 0.92;
    h *= 0.86;
    return (Math.Max(120, w), Math.Max(200, h));
  }

  /// <summary>Оценка страниц по символам и настройкам (вертикальный скролл: stride = символов на страницу, как GetGlobalBookPageStride).</summary>
  public static int ComputeEstimatedPageCount(long totalChars, TextSettings? s)
  {
    if (totalChars <= 0) return 0;
    s ??= new TextSettings();
    // Те же нормализации шрифта/полей, что в ReadingPage.ApplyTextSettingsFromModel / GetGlobalBookPageDisplay
    int fs = s.FontSize > 0 ? s.FontSize : DefaultFontSize;
    fs = Math.Clamp(fs, MinFontSize, MaxFontSize);
    int m = s.Margins > 0 ? s.Margins : DefaultMargins;
    m = Math.Clamp(m, MinMargins, MaxMargins);
    var forStride = new TextSettings { FontSize = fs, Margins = m };
    int stride = Math.Max(1, GetCharsPerPage(forStride));
    return Math.Max(1, (int)Math.Ceiling(totalChars / (double)stride));
  }

  /// <summary>
  /// Оценка страниц для карточек и RecalculateAll: вертикальный режим — <see cref="ComputeEstimatedPageCount"/>;
  /// горизонтальное листание — вместимость страницы через <see cref="EstimateCharsPerPageFromViewport"/> (как multicol), без фиксированного GetCharsPerPage.
  /// </summary>
  public static int ComputeEstimatedPageCountForCard(long totalChars, TextSettings? s)
  {
    if (totalChars <= 0) return 0;
    s ??= new TextSettings();
    int fs = s.FontSize > 0 ? s.FontSize : DefaultFontSize;
    fs = Math.Clamp(fs, MinFontSize, MaxFontSize);
    int m = s.Margins > 0 ? s.Margins : DefaultMargins;
    m = Math.Clamp(m, MinMargins, MaxMargins);

    if (!IsHorizontalScrollingMode(s))
    {
      var forStride = new TextSettings { FontSize = fs, Margins = m };
      int stride = Math.Max(1, GetCharsPerPage(forStride));
      return Math.Max(1, (int)Math.Ceiling(totalChars / (double)stride));
    }

    var (vw, vh) = GetDefaultReadingViewportLogicalDp();
    int perPage = EstimateCharsPerPageFromViewport(vw, vh, fs, m);
    perPage = Math.Max(200, perPage);
    return Math.Max(1, (int)Math.Ceiling(totalChars / (double)perPage));
  }

  /// <summary>Оценка «символов на страницу» для вертикального режима по текущему кеглю и полям.</summary>
  public static int GetCharsPerPage(TextSettings s)
  {
    int fs = s.FontSize > 0 ? s.FontSize : DefaultFontSize;
    fs = Math.Clamp(fs, MinFontSize, MaxFontSize);
    int m = s.Margins > 0 ? s.Margins : DefaultMargins;
    m = Math.Clamp(m, MinMargins, MaxMargins);
    const double refFs = 16.0;
    const double refM = 16.0;
    double factor = (refFs / fs) * (refM / m);
    return (int)Math.Round(Math.Clamp(1800 * factor, 400, 8000));
  }

  /// <summary>
  /// Быстрая оценка вместимости страницы по размеру области чтения (логические px, как в MAUI).
  /// Без WebView: сетка «средняя ширина символа × строки» с небольшим запасом. Горизонтальная пагинация.
  /// </summary>
  public static int EstimateCharsPerPageFromViewport(double widthPx, double heightPx, double fontSizePx, double marginPx, double lineHeight = 1.6)
  {
    if (widthPx <= 1 || heightPx <= 1)
      return 1800;
    double fs = Math.Clamp(fontSizePx > 0 ? fontSizePx : DefaultFontSize, MinFontSize, MaxFontSize);
    double margin = Math.Clamp(marginPx > 0 ? marginPx : DefaultMargins, MinMargins, MaxMargins);
    double innerW = Math.Max(0, widthPx - 2 * margin);
    double innerH = Math.Max(0, heightPx - 2 * margin);
    // Средняя ширина глифа для смеси кириллица/латиница в system-ui (грубая, но стабильная оценка).
    const double avgCharWidthFactor = 0.52;
    double avgCharW = fs * avgCharWidthFactor;
    double linePx = fs * lineHeight;
    // Запас под margin абзацев; используется только если калибровка WebView недоступна.
    innerH = Math.Max(linePx, innerH - fs * 1.1);
    int lines = Math.Max(1, (int)Math.Floor(innerH / linePx));
    int cols = Math.Max(1, (int)Math.Floor(innerW / avgCharW));
    int cap = cols * lines;
    cap = (int)Math.Round(cap * 0.84);
    return Math.Clamp(Math.Max(200, cap), 200, 120_000);
  }

  /// <summary>Приводит сохранённую строку выравнивания к значению MAUI.</summary>
  public static Microsoft.Maui.TextAlignment ParseAlignment(string? text)
  {
    return TextSettingsStoredFormats.NormalizeAlignment(text) switch
    {
      TextSettingsStoredFormats.AlignmentCenter => Microsoft.Maui.TextAlignment.Center,
      TextSettingsStoredFormats.AlignmentEnd => Microsoft.Maui.TextAlignment.End,
      TextSettingsStoredFormats.AlignmentStart => Microsoft.Maui.TextAlignment.Start,
      _ => Microsoft.Maui.TextAlignment.Justify
    };
  }

  /// <summary>Соответствует ли выравнивание режиму «по ширине».</summary>
  public static bool IsJustify(Microsoft.Maui.TextAlignment a) =>
      a == Microsoft.Maui.TextAlignment.Justify;

  /// <summary>Значение CSS text-align для встроенного HTML.</summary>
  public static string AlignmentToCss(Microsoft.Maui.TextAlignment a) =>
      a switch
      {
        Microsoft.Maui.TextAlignment.Center => "center",
        Microsoft.Maui.TextAlignment.End => "right",
        Microsoft.Maui.TextAlignment.Start => "left",
        Microsoft.Maui.TextAlignment.Justify => "justify",
        _ => "justify"
      };

  /// <summary>Разбирает <c>#RRGGBB</c> или <c>#RRGGBBAA</c>; при ошибке возвращает <paramref name="fallback"/>.</summary>
  public static Color ParseColorHex(string? hex, Color fallback)
  {
    if (string.IsNullOrWhiteSpace(hex))
      return fallback;
    try
    {
      var h = hex.Trim();
      if (h.StartsWith('#'))
        h = h[1..];
      if (h.Length == 6)
      {
        return Color.FromRgb(
            Convert.ToByte(h[..2], 16),
            Convert.ToByte(h[2..4], 16),
            Convert.ToByte(h[4..6], 16));
      }
      if (h.Length == 8)
      {
        return Color.FromRgba(
            Convert.ToByte(h[0..2], 16),
            Convert.ToByte(h[2..4], 16),
            Convert.ToByte(h[4..6], 16),
            Convert.ToByte(h[6..8], 16));
      }
    }
    catch { }
    return fallback;
  }

  /// <summary>RGB hex без альфы для сохранения в настройках.</summary>
  public static string ColorToHexRgb(Color c) =>
      $"#{(byte)(c.Red * 255):X2}{(byte)(c.Green * 255):X2}{(byte)(c.Blue * 255):X2}";

  /// <summary>Оценка номера «книжной» страницы по смещению (как подпись чтения без live WebView).</summary>
  public static int GetApproximateGlobalPageNumber(long offset, long totalChars, TextSettings? ts)
  {
    if (totalChars <= 0)
      return 1;
    ts ??= new TextSettings();
    offset = Math.Clamp(offset, 0, Math.Max(0L, totalChars - 1));
    if (!IsHorizontalScrollingMode(ts))
    {
      int stride = Math.Max(1, GetCharsPerPage(ts));
      int total = Math.Max(1, (int)Math.Ceiling(totalChars / (double)stride));
      int page = 1 + (int)(offset / stride);
      return Math.Clamp(page, 1, total);
    }

    var (vw, vh) = GetDefaultReadingViewportLogicalDp();
    int fs = ts.FontSize > 0 ? ts.FontSize : DefaultFontSize;
    fs = Math.Clamp(fs, MinFontSize, MaxFontSize);
    int m = ts.Margins > 0 ? ts.Margins : DefaultMargins;
    m = Math.Clamp(m, MinMargins, MaxMargins);
    int perPage = EstimateCharsPerPageFromViewport(vw, vh, fs, m);
    perPage = Math.Max(200, perPage);
    int totalH = Math.Max(1, (int)Math.Ceiling(totalChars / (double)perPage));
    int pageH = 1 + (int)(offset / perPage);
    return Math.Clamp(pageH, 1, totalH);
  }
}
