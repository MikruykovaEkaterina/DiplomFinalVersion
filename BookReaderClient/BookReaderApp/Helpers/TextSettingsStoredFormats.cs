namespace BookReaderApp.Helpers;

/// <summary>
/// Нормализованные ключи выравнивания и режима прокрутки текста для хранения в БД; подписи в настройках локализуются отдельно.
/// </summary>
public static class TextSettingsStoredFormats
{
  /// <summary>Ключ хранения: выравнивание по ширине (justify).</summary>
  public const string AlignmentJustify = "justify";

  /// <summary>Ключ хранения: выравнивание по левому краю.</summary>
  public const string AlignmentStart = "start";

  /// <summary>Ключ хранения: выравнивание по центру.</summary>
  public const string AlignmentCenter = "center";

  /// <summary>Ключ хранения: выравнивание по правому краю.</summary>
  public const string AlignmentEnd = "end";

  /// <summary>Ключ хранения: вертикальная прокрутка (несколько колонок не используются).</summary>
  public const string ScrollingVertical = "vertical";

  /// <summary>Ключ хранения: горизонтальное листание страниц (multicol/WebView).</summary>
  public const string ScrollingHorizontal = "horizontal";

  /// <summary>Порядок переключения выравнивания в UI (как цикл).</summary>
  public static readonly string[] AlignmentKeysOrdered =
      { AlignmentJustify, AlignmentStart, AlignmentCenter, AlignmentEnd };

  /// <summary>Порядок режимов прокрутки в UI.</summary>
  public static readonly string[] ScrollingKeysOrdered =
      { ScrollingVertical, ScrollingHorizontal };

  /// <summary>Приводит сырое значение из БД или устаревшую локализованную строку к одному из <see cref="AlignmentJustify"/> … <see cref="AlignmentEnd"/>.</summary>
  public static string NormalizeAlignment(string? raw)
  {
    if (string.IsNullOrWhiteSpace(raw))
      return AlignmentJustify;
    var t = raw.Trim();
    var lower = t.ToLowerInvariant();
    if (lower is AlignmentJustify or AlignmentStart or AlignmentCenter or AlignmentEnd)
      return lower;

    if (t.Contains("центр", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("center", StringComparison.OrdinalIgnoreCase))
      return AlignmentCenter;
    if (t.Contains("прав", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("right", StringComparison.OrdinalIgnoreCase))
      return AlignmentEnd;
    if (t.Contains("лев", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("left", StringComparison.OrdinalIgnoreCase))
      return AlignmentStart;
    if (t.Contains("ширин", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("justify", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("width", StringComparison.OrdinalIgnoreCase))
      return AlignmentJustify;
    return AlignmentJustify;
  }

  /// <summary>Приводит сырое значение к <see cref="ScrollingVertical"/> или <see cref="ScrollingHorizontal"/>.</summary>
  public static string NormalizeScrolling(string? raw)
  {
    if (string.IsNullOrWhiteSpace(raw))
      return ScrollingVertical;
    var t = raw.Trim();
    if (string.Equals(t, ScrollingVertical, StringComparison.OrdinalIgnoreCase) ||
        t.Contains("вертик", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("vertical", StringComparison.OrdinalIgnoreCase))
      return ScrollingVertical;
    if (string.Equals(t, ScrollingHorizontal, StringComparison.OrdinalIgnoreCase) ||
        t.Contains("горизонт", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("horizontal", StringComparison.OrdinalIgnoreCase))
      return ScrollingHorizontal;
    return ScrollingVertical;
  }
}
