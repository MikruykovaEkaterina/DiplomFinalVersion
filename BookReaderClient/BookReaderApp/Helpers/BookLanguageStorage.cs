using System.Globalization;
using BookReaderApp.Models;
using BookReaderApp.Resources;

namespace BookReaderApp.Helpers;

/// <summary>
/// Соответствие между <see cref="BookLanguage"/> карточки и строкой в БД (ISO <c>ru</c>/<c>en</c> или пусто).
/// Отображение в UI через <see cref="LocalizedEnumHelper.GetBookLanguageString"/>.
/// </summary>
public static class BookLanguageStorage
{
  /// <summary>Возвращает значение для поля языка при сохранении в БД.</summary>
  public static string ToStored(BookLanguage lang) =>
      lang switch
      {
        BookLanguage.Russian => "ru",
        BookLanguage.English => "en",
        _ => ""
      };

  /// <summary>Приводит значение из БД или устаревшую подпись к ISO для сохранения.</summary>
  public static string NormalizeToIso(string? raw) => ToStored(FromStored(raw));

  /// <summary>Сопоставляет строку из БД (ISO, локализованная подпись, устаревший текст) с <see cref="BookLanguage"/>.</summary>
  public static BookLanguage FromStored(string? s)
  {
    if (string.IsNullOrWhiteSpace(s))
      return BookLanguage.None;
    var t = s.Trim();
    if (t.Equals("ru", StringComparison.OrdinalIgnoreCase))
      return BookLanguage.Russian;
    if (t.Equals("en", StringComparison.OrdinalIgnoreCase))
      return BookLanguage.English;

    foreach (BookLanguage l in Enum.GetValues(typeof(BookLanguage)))
    {
      if (l == BookLanguage.None)
        continue;
      if (string.Equals(LocalizedEnumHelper.GetBookLanguageString(l), t, StringComparison.OrdinalIgnoreCase))
        return l;
    }

    foreach (var culture in new[] { "ru", "en" })
    {
      var c = CultureInfo.GetCultureInfo(culture);
      foreach (BookLanguage lang in Enum.GetValues(typeof(BookLanguage)))
      {
        if (lang == BookLanguage.None)
          continue;
        var key = lang switch
        {
          BookLanguage.Russian => "BookLanguage_Russian",
          BookLanguage.English => "BookLanguage_English",
          _ => null
        };
        if (key == null)
          continue;
        var localized = Strings.ResourceManager.GetString(key, c);
        if (!string.IsNullOrEmpty(localized) &&
            string.Equals(localized.Trim(), t, StringComparison.OrdinalIgnoreCase))
          return lang;
      }
    }

    if (t.Equals("Русский", StringComparison.OrdinalIgnoreCase))
      return BookLanguage.Russian;
    if (t.Equals("English", StringComparison.OrdinalIgnoreCase))
      return BookLanguage.English;

    return BookLanguage.None;
  }
}
