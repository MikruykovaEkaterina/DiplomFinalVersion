namespace BookReaderApp.Models;

/// <summary>
/// Метаданные книги после разбора файла при добавлении (ещё без записей в SQLite). Передаются в службы сохранения.
/// </summary>
public class BookMetadata
{
  public string Title { get; set; } = "";

  public string Author { get; set; } = "";

  public string Description { get; set; } = "";

  /// <summary>Язык контента или ISO (до нормализации в <see cref="BookLanguageStorage"/>).</summary>
  public string Language { get; set; } = "";

  public DateTime? PublishDate { get; set; }
}
