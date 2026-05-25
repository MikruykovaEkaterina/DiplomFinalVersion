using SQLite;

namespace BookReaderApp.Models;

/// <summary>Снимок сохранённых полей фильтра каталога (таблица <c>SearchFilters</c>).</summary>
[Table("SearchFilters")]
public class SearchFilter
{
  [PrimaryKey]
  public int Id { get; set; }

  public string Title { get; set; } = "";

  public string Author { get; set; } = "";

  public string Language { get; set; } = "";

  public long MinChars { get; set; } = 0;

  public long MaxChars { get; set; } = long.MaxValue;

  /// <summary>Фильтр по статусу чтения; <see cref="BookStatus.None"/> — фильтр не задан.</summary>
  public BookStatus ReadingStatus { get; set; } = BookStatus.None;

  /// <summary>Фильтр по реакции; <see cref="BookReaction.None"/> — фильтр не задан.</summary>
  public BookReaction Reaction { get; set; } = BookReaction.None;

  /// <summary>Сортировка в каталоге.</summary>
  public BookSort SortBy { get; set; } = BookSort.TitleAsc;
}
