using BookReaderApp.Helpers;
using BookReaderApp.Models;

namespace BookReaderApp.Services;

/// <summary>
/// Соответствие языка контента приложения ISO 639-1 для API перевода.
/// Каталог языков — только <see cref="BookLanguage"/> (сейчас «Русский» и «English»).
/// </summary>
public static class TranslationLanguageCodes
{
  /// <summary>ISO 639-1 из перечисления языка книги.</summary>
  public static string? TryGetIsoFromBookLanguage(BookLanguage lang) =>
      lang switch
      {
        BookLanguage.Russian => "ru",
        BookLanguage.English => "en",
        _ => null
      };

  /// <summary>
  /// ISO из поля карточки (ru/en), из локализованной подписи или устаревших значений БД.
  /// </summary>
  public static string? TryGetIsoCode(string? storedOrDisplay) =>
      TryGetIsoFromBookLanguage(BookLanguageStorage.FromStored(storedOrDisplay));
}
