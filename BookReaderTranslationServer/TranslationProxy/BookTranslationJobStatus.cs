namespace TranslationProxy;

/// <summary>Статус фоновой задачи перевода книги (память, SQLite, JSON API). Имена совпадают с сериализацией.</summary>
public enum BookTranslationJobStatus
{
  Queued,
  InProgress,
  Completed,
  Failed,
  Canceled,
  Expired
}

/// <summary>Разбор значения столбца SQLite и совместимость со старыми строками.</summary>
public static class BookTranslationJobStatusParser
{
  public static bool TryParse(string? s, out BookTranslationJobStatus status) =>
      Enum.TryParse(s, ignoreCase: true, out status);

  public static BookTranslationJobStatus ParseOrDefault(string? s, BookTranslationJobStatus defaultStatus = BookTranslationJobStatus.Queued) =>
      TryParse(s, out var v) ? v : defaultStatus;
}
