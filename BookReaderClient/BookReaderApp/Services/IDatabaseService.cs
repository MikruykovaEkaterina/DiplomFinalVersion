using BookReaderApp.Models;

namespace BookReaderApp.Services
{
  /// <summary>Слой данных: SQLite для книг, чтения, заметок, настроек и фильтров каталога.</summary>
  public interface IDatabaseService
  {
    // Work
    /// <summary>Вставка новой записи работы или обновление существующей (при <see cref="Work.Id"/> &gt; 0).</summary>
    Task<int> SaveWorkAsync(Work work);
    /// <summary>Работа по идентификатору или null.</summary>
    Task<Work> GetWorkByIdAsync(int id);
    /// <summary>Все сохранённые работы.</summary>
    Task<List<Work>> GetAllWorksAsync();
    /// <summary>Обновляет существующую работу целиком.</summary>
    Task UpdateWorkAsync(Work work);

    // Card
    /// <summary>Вставляет или обновляет языковую карточку книги.</summary>
    Task<int> SaveCardAsync(Card card);
    /// <summary>Карточка по первичному ключу.</summary>
    Task<Card> GetCardByIdAsync(int id);
    /// <summary>Все версии книги по <see cref="Card.WorkId"/>.</summary>
    Task<List<Card>> GetCardsByWorkIdAsync(int workId);
    /// <summary>Все карточки в базе.</summary>
    Task<List<Card>> GetAllCardsAsync();
    /// <summary>Обновляет запись карточки без смены ключа.</summary>
    Task UpdateCardAsync(Card card);
    /// <summary>Пересчитывает EstimatedPageCount у всех карточек: вертикальный скролл — stride из настроек; горизонтальное листание — вместимость страницы по viewport (как multicol).</summary>
    Task RecalculateAllCardsEstimatedPageCountAsync();
    /// <summary>Удаляет одну версию книги (Card), связанные позиции чтения и заметки; при последней версии удаляет Work. Возвращает false, если карточки не было.</summary>
    Task<bool> TryDeleteCardAsync(int cardId);

    // ReadingPosition
    /// <summary>Создание позиции (Id == 0) или обновление.</summary>
    Task<int> SaveReadingPositionAsync(ReadingPosition position);
    /// <summary>Позиция чтения для карточки.</summary>
    Task<ReadingPosition> GetReadingPositionByCardIdAsync(int cardId);
    /// <summary>Все сохранённые позиции чтения.</summary>
    Task<List<ReadingPosition>> GetAllReadingPositionsAsync();
    /// <summary>Самая свежая запись по LastUpdated с существующей карточкой и файлом (для «продолжить чтение»).</summary>
    Task<ReadingPosition?> GetLatestReadingPositionAsync();
    /// <summary>Обновляет оффсеты и счётчик обновления времени.</summary>
    Task UpdateReadingPositionAsync(ReadingPosition position);

    // Note
    /// <summary>Добавляет заметку к книге или позиции.</summary>
    Task<int> SaveNoteAsync(Note note);
    /// <summary>Заметки только по указанной карточке.</summary>
    Task<List<Note>> GetNotesByCardIdAsync(int cardId);
    /// <summary>Все заметки по любой версии одной работы.</summary>
    Task<List<Note>> GetNotesByWorkIdAsync(int workId);
    /// <summary>Удаление заметки по Id.</summary>
    Task DeleteNoteAsync(int noteId);

    // Settings
    /// <summary>Строка настроек страницы чтения (создаётся по умолчанию при отсутствии).</summary>
    Task<TextSettings> GetTextSettingsAsync();
    /// <summary>Сохраняет настройки текста всей читалки.</summary>
    Task SaveTextSettingsAsync(TextSettings settings);
    /// <summary>Язык UI, тема, размеры шрифтов и т.д.</summary>
    Task<InterfaceSettings> GetInterfaceSettingsAsync();
    /// <summary>Сохраняет настройки интерфейса.</summary>
    Task SaveInterfaceSettingsAsync(InterfaceSettings settings);

    // Search
    /// <summary>Последнее состояние фильтров главного каталога.</summary>
    Task<SearchFilter> GetSearchFilterAsync();
    /// <summary>Сохраняет фильтры главного экрана.</summary>
    Task SaveSearchFilterAsync(SearchFilter filter);
  }
}
