using SQLite;

namespace BookReaderApp.Models
{
  /// <summary>
  /// Версия книги (Таблица 3) - информация для конкретной языковой версии книги.
  /// Каждая книга может иметь несколько карточек (по одной для каждого поддерживаемого языка).
  /// </summary>
  [Table("Cards")]
  public class Card
  {
    /// <summary>Уникальный идентификатор карточки (>= 1)</summary>
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Идентификатор произведения - связь с родительским Work (внешний ключ)</summary>
    [Indexed]
    public int WorkId { get; set; }

    /// <summary>Название книги на языке этой версии (до 300 символов, UTF-8)</summary>
    [MaxLength(300)]
    public string Title { get; set; }

    /// <summary>Имя автора книги на языке этой версии (UTF-8, может быть пустым)</summary>
    public string Author { get; set; }

    /// <summary>Язык текста этой версии: ISO-код (ru/en) или пусто — локализованная подпись только при показе.</summary>
    [MaxLength(50)]
    public string Language { get; set; }

    /// <summary>Общее количество символов в текстовом контенте (>= 0)</summary>
    public long TotalChars { get; set; }

    /// <summary>Переведённое описание произведения (UTF-8, может быть пустым)</summary>
    public string Description { get; set; }

    /// <summary>Физическое расположение файла книги (относительный путь внутри директории приложения)</summary>
    [MaxLength(500)]
    public string FilePath { get; set; }

    /// <summary>Формат текстового файла ("FB2", "EPUB")</summary>
    [MaxLength(10)]
    public string Format { get; set; }

    /// <summary>Дата загрузки/завершения перевода (для оригинала - дата загрузки, для переведённой версии - дата завершения перевода)</summary>
    public DateTime AddedDate { get; set; } = DateTime.Now;

    /// <summary>Момент последнего открытия именно этой языковой версии в читалке.</summary>
    public DateTime LastOpened { get; set; }

    /// <summary>Физическое расположение изображения обложки книги (относительный путь, может быть пустым)</summary>
    [MaxLength(500)]
    public string CoverPath { get; set; }

    /// <summary>Количество символов текста, которое пользователь прочитал (>= 0, до общего количества символов)</summary>
    public long ReadChars { get; set; } = 0;

    /// <summary>Оценка числа страниц при текущих настройках текста (шрифт, поля). Пересчитывается при смене настроек и при обновлении TotalChars.</summary>
    public int EstimatedPageCount { get; set; }
  }
}
