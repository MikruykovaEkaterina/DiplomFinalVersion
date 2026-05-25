using SQLite;

namespace BookReaderApp.Models;

/// <summary>
/// Сохранённая позиция чтения внутри книги по карточке (смещение в «плоском» тексте после извлечения).
/// </summary>
[Table("ReadingPositions")]
public class ReadingPosition
{
  [PrimaryKey, AutoIncrement]
  public int Id { get; set; }

  /// <summary>Языковая карточка, к которой относится позиция.</summary>
  [Indexed]
  public int CardId { get; set; }

  /// <summary>Символьное смещение от начала логической последовательности текста книги.</summary>
  public long CharacterOffset { get; set; } = 0;

  /// <summary>Время последнего сохранения позиции.</summary>
  public DateTime LastUpdated { get; set; } = DateTime.Now;
}
