using SQLite;

namespace BookReaderApp.Models;

/// <summary>
/// Заметка пользователя привязана к книге (<see cref="CardId"/>), дублирует <see cref="WorkId"/> для запросов по произведению.
/// Смещение — позиция в общем тексте карточки.
/// </summary>
[Table("Notes")]
public class Note
{
  [PrimaryKey, AutoIncrement]
  public int Id { get; set; }

  [Indexed]
  public int CardId { get; set; }

  [Indexed]
  public int WorkId { get; set; }

  /// <summary>Краткий заголовок заметки.</summary>
  public string Title { get; set; } = "";

  /// <summary>Развёрнутый комментарий.</summary>
  public string Comment { get; set; } = "";

  /// <summary>Якорь в тексте книги для возврата к месту создания заметки.</summary>
  public long CharacterOffset { get; set; }

  public DateTime CreatedDate { get; set; } = DateTime.Now;
}
