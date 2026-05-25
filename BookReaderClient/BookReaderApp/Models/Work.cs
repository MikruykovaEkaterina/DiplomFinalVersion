using SQLite;

namespace BookReaderApp.Models;

/// <summary>
/// Произведение (таблица <c>Works</c>): один <c>Work</c> объединяет языковые <see cref="Card"/>.
/// Реакция и статус чтения задаются только enum’ами и в БД хранятся как целые.
/// </summary>
[Table("Works")]
public class Work
{
  [PrimaryKey, AutoIncrement]
  public int Id { get; set; }

  /// <summary>Вариант для UI — локализуется через <see cref="BookReaderApp.Helpers.LocalizedEnumHelper.GetBookReactionString"/>.</summary>
  public BookReaction Reaction { get; set; } = BookReaction.Unrated;

  /// <summary>Статус чтения — локализуется через <see cref="BookReaderApp.Helpers.LocalizedEnumHelper.GetBookStatusString"/>.</summary>
  public BookStatus ReadingStatus { get; set; } = BookStatus.New;
}
