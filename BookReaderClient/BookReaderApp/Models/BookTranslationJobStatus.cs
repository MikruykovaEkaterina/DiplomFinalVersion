namespace BookReaderApp.Models;

/// <summary>Статус задачи перевода книги на прокси (поле JSON <c>status</c>), имена совпадают с сервером <c>TranslationProxy</c>.</summary>
public enum BookTranslationJobStatus
{
  Queued,
  InProgress,
  Completed,
  Failed,
  Canceled,
  Expired
}
