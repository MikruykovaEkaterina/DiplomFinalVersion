using BookReaderApp.Models;
using BookReaderApp.Resources;

namespace BookReaderApp.Helpers;

/// <summary>Локализованные подписи enum-моделей каталога для UI (<see cref="Strings"/>).</summary>
public static class LocalizedEnumHelper
{
  /// <summary>Возвращает строку статуса чтения для текущей культуры.</summary>
  public static string GetBookStatusString(BookStatus status)
  {
    switch (status)
    {
      case BookStatus.None: return Strings.BookStatus_None;
      case BookStatus.New: return Strings.BookStatus_New;
      case BookStatus.InProgress: return Strings.BookStatus_InProgress;
      case BookStatus.Read: return Strings.BookStatus_Read;
      default: return status.ToString();
    }
  }

  /// <summary>Возвращает строку реакции/оценки для текущей культуры.</summary>
  public static string GetBookReactionString(BookReaction reaction)
  {
    switch (reaction)
    {
      case BookReaction.None: return Strings.BookReaction_None;
      case BookReaction.Favorite: return Strings.BookReaction_Favorite;
      case BookReaction.Unrated: return Strings.BookReaction_Unrated;
      default: return reaction.ToString();
    }
  }

  /// <summary>Возвращает подпись варианта сортировки каталога.</summary>
  public static string GetBookSortString(BookSort sort)
  {
    switch (sort)
    {
      case BookSort.TitleAsc: return Strings.BookSort_TitleAsc;
      case BookSort.TitleDesc: return Strings.BookSort_TitleDesc;
      case BookSort.DateNew: return Strings.BookSort_DateNew;
      case BookSort.DateOld: return Strings.BookSort_DateOld;
      case BookSort.SizeAsc: return Strings.BookSort_SizeAsc;
      case BookSort.SizeDesc: return Strings.BookSort_SizeDesc;
      default: return sort.ToString();
    }
  }

  /// <summary>Возвращает подпись языка книги (не путать с языком интерфейса приложения).</summary>
  public static string GetBookLanguageString(BookLanguage lang) =>
      lang switch
      {
        BookLanguage.None => Strings.BookLanguage_None,
        BookLanguage.Russian => Strings.BookLanguage_Russian,
        BookLanguage.English => Strings.BookLanguage_English,
        _ => lang.ToString()
      };
}
