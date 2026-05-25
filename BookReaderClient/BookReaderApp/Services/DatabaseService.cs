using System.Linq;
using System.IO;
using Microsoft.Maui.Storage;
using SQLite;
using BookReaderApp.Helpers;
using BookReaderApp.Models;

namespace BookReaderApp.Services
{
  /// <summary>Реализация <see cref="IDatabaseService"/> через SQLite в каталоге приложения.</summary>
  public class DatabaseService : IDatabaseService
  {
    private readonly SQLiteAsyncConnection _database;
    private readonly Task _initTask;

    /// <summary>Открывает <c>bookreader.db3</c>, создаёт таблицы и выполняет миграции схемы.</summary>
    public DatabaseService()
    {
      try
      {
        System.Diagnostics.Debug.WriteLine("[DB] ctor start");
        string dbPath = Path.Combine(FileSystem.AppDataDirectory, "bookreader.db3");
        System.Diagnostics.Debug.WriteLine($"[DB] path = {dbPath}");
        _database = new SQLiteAsyncConnection(dbPath);
        _initTask = InitializeDatabaseAsync();
        System.Diagnostics.Debug.WriteLine("[DB] ctor end");
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[DB] ctor ERROR: {ex}");
        throw;
      }
    }

    private async Task InitializeDatabaseAsync()
    {
      try
      {
        System.Diagnostics.Debug.WriteLine("[DB] init start");
        await _database.CreateTableAsync<Work>();
        await _database.CreateTableAsync<Card>();
        await _database.CreateTableAsync<ReadingPosition>();
        await _database.CreateTableAsync<Note>();
        await _database.CreateTableAsync<TextSettings>();
        await _database.CreateTableAsync<InterfaceSettings>();
        await _database.CreateTableAsync<SearchFilter>();

        await TryAddCardsEstimatedPageCountColumnAsync();
        await TryAddCardsLastOpenedColumnAsync();
        await TryMigrateWorkLastOpenedToCardsAsync();
        await TryMigrateCardLanguagesToIsoAsync();
        await TryMigrateWorkStatusAndReactionToIntegersAsync();
        await TryMigrateInterfaceThemeToIntegerAsync();

        System.Diagnostics.Debug.WriteLine("[DB] init end");
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[DB] init ERROR: {ex}");
        throw;
      }
    }
    private async Task EnsureInitializedAsync() => await _initTask;

    // === WORK ===
    /// <inheritdoc />
    public async Task<int> SaveWorkAsync(Work work)
    {
      await EnsureInitializedAsync();
      if (work.Id == 0)
      {
        await _database.InsertAsync(work);
        System.Diagnostics.Debug.WriteLine($"[DB] Сохранён Work с ID: {work.Id}");
        if (work.Id == 0)
        {
          System.Diagnostics.Debug.WriteLine("[DB] ПРЕДУПРЕЖДЕНИЕ: Work.Id всё ещё равен 0 после InsertAsync!");
        }
        return work.Id; // SQLite-net устанавливает Id после вставки
      }
      else
      {
        await _database.UpdateAsync(work);
        return work.Id;
      }
    }

    /// <inheritdoc />
    public async Task<Work> GetWorkByIdAsync(int id)
    {
      await EnsureInitializedAsync();
      return await _database.Table<Work>().Where(w => w.Id == id).FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<List<Work>> GetAllWorksAsync()
    {
      await EnsureInitializedAsync();
      return await _database.Table<Work>().ToListAsync();
    }

    /// <inheritdoc />
    public async Task UpdateWorkAsync(Work work)
    {
      await EnsureInitializedAsync();
      await _database.UpdateAsync(work);
    }

    // === CARD ===
    /// <inheritdoc />
    public async Task<int> SaveCardAsync(Card card)
    {
      await EnsureInitializedAsync();
      if (card.Id == 0)
      {
        await _database.InsertAsync(card);
        System.Diagnostics.Debug.WriteLine($"[DB] Сохранена Card с ID: {card.Id}, WorkId: {card.WorkId}");
        if (card.Id == 0)
        {
          System.Diagnostics.Debug.WriteLine("[DB] ПРЕДУПРЕЖДЕНИЕ: Card.Id всё ещё равен 0 после InsertAsync!");
        }
        return card.Id; // SQLite-net устанавливает Id после вставки
      }
      else
      {
        await _database.UpdateAsync(card);
        return card.Id;
      }
    }

    /// <inheritdoc />
    public async Task<Card> GetCardByIdAsync(int id)
    {
      await EnsureInitializedAsync();
      return await _database.Table<Card>().Where(c => c.Id == id).FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<List<Card>> GetCardsByWorkIdAsync(int workId)
    {
      await EnsureInitializedAsync();
      return await _database.Table<Card>().Where(c => c.WorkId == workId).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Card>> GetAllCardsAsync()
    {
      await EnsureInitializedAsync();
      return await _database.Table<Card>().ToListAsync();
    }

    /// <inheritdoc />
    public async Task UpdateCardAsync(Card card)
    {
      await EnsureInitializedAsync();
      await _database.UpdateAsync(card);
    }

    /// <inheritdoc />
    public async Task RecalculateAllCardsEstimatedPageCountAsync()
    {
      await EnsureInitializedAsync();
      var ts = await GetTextSettingsAsync();
      var cards = await GetAllCardsAsync();
      foreach (var c in cards)
      {
        int ep = TextReadingLayout.ComputeEstimatedPageCountForCard(c.TotalChars, ts);
        if (c.EstimatedPageCount == ep)
          continue;
        c.EstimatedPageCount = ep;
        await _database.UpdateAsync(c);
      }
    }

    /// <summary>Миграция: колонка EstimatedPageCount для оценки числа страниц на карточке.</summary>
    private async Task TryAddCardsEstimatedPageCountColumnAsync()
    {
      try
      {
        await _database.ExecuteAsync("ALTER TABLE Cards ADD COLUMN EstimatedPageCount INTEGER NOT NULL DEFAULT 0");
        System.Diagnostics.Debug.WriteLine("[DB] Добавлена колонка Cards.EstimatedPageCount");
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[DB] ALTER Cards.EstimatedPageCount: {ex.Message}");
      }
    }

    /// <summary>Миграция: время последнего открытия — на уровне языковой карточки.</summary>
    private async Task TryAddCardsLastOpenedColumnAsync()
    {
      try
      {
        await _database.ExecuteAsync("ALTER TABLE Cards ADD COLUMN LastOpened INTEGER NOT NULL DEFAULT 0");
        System.Diagnostics.Debug.WriteLine("[DB] Добавлена колонка Cards.LastOpened");
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[DB] ALTER Cards.LastOpened: {ex.Message}");
      }
    }

    /// <summary>Копирует LastOpened из Works (старое поле в БД) в Cards один раз для существующих строк.</summary>
    private async Task TryMigrateWorkLastOpenedToCardsAsync()
    {
      try
      {
        await _database.ExecuteAsync(
            "UPDATE Cards SET LastOpened = (SELECT LastOpened FROM Works WHERE Works.Id = Cards.WorkId) WHERE LastOpened = 0");
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[DB] Migrate LastOpened Works→Cards: {ex.Message}");
      }

      try
      {
        var cards = await _database.Table<Card>().ToListAsync();
        foreach (var c in cards)
        {
          if (c.LastOpened == default)
          {
            c.LastOpened = c.AddedDate;
            await _database.UpdateAsync(c);
          }
        }
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[DB] LastOpened fallback AddedDate: {ex.Message}");
      }
    }

    /// <summary>Приводит Cards.Language к ISO (ru/en); старые локализованные подписи распознаются один раз при старте.</summary>
    private async Task TryMigrateCardLanguagesToIsoAsync()
    {
      try
      {
        var cards = await _database.Table<Card>().ToListAsync();
        foreach (var c in cards)
        {
          var iso = BookLanguageStorage.NormalizeToIso(c.Language);
          if (!string.Equals((c.Language ?? "").Trim(), iso, StringComparison.Ordinal))
          {
            c.Language = iso;
            await _database.UpdateAsync(c);
          }
        }
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[DB] Migrate Card.Language → ISO: {ex.Message}");
      }
    }

    /// <summary>Старые версии хранили статус и реакцию строками на русском; приводим к INTEGER под перечисления.</summary>
    private async Task TryMigrateWorkStatusAndReactionToIntegersAsync()
    {
      try
      {
        await _database.ExecuteAsync(@"
UPDATE Works SET ReadingStatus = CASE CAST(ReadingStatus AS TEXT)
  WHEN 'Новое' THEN 1
  WHEN 'В процессе' THEN 2
  WHEN 'Прочитано' THEN 3
  WHEN 'None' THEN 0
  WHEN 'New' THEN 1
  WHEN 'InProgress' THEN 2
  WHEN 'Read' THEN 3
  ELSE CAST(ReadingStatus AS INTEGER)
END;");
        await _database.ExecuteAsync(@"
UPDATE Works SET Reaction = CASE CAST(Reaction AS TEXT)
  WHEN 'Не оценено' THEN 2
  WHEN 'Любимое' THEN 1
  WHEN 'Нравится' THEN 1
  WHEN 'None' THEN 0
  WHEN 'Favorite' THEN 1
  WHEN 'Unrated' THEN 2
  ELSE CAST(Reaction AS INTEGER)
END;");
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[DB] Migrate Works.ReadingStatus/Reaction → int: {ex.Message}");
      }
    }

    /// <summary>Тема интерфейса ранее могла быть TEXT (RU/EN); нормализуем к INTEGER (<see cref="InterfaceTheme"/>).</summary>
    private async Task TryMigrateInterfaceThemeToIntegerAsync()
    {
      try
      {
        await _database.ExecuteAsync(@"
UPDATE InterfaceSettings SET Theme = CASE CAST(Theme AS TEXT)
  WHEN 'Light' THEN 0
  WHEN 'Светлая' THEN 0
  WHEN 'Dark' THEN 1
  WHEN 'Тёмная' THEN 1
  WHEN 'Темная' THEN 1
  WHEN 'ТЁМАЯ' THEN 1
  WHEN 'ТЁМНАЯ' THEN 1
  WHEN 'AUTO' THEN 2
  WHEN 'Auto' THEN 2
  WHEN 'Авто' THEN 2
  ELSE CAST(Theme AS INTEGER)
END;");
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[DB] Migrate InterfaceSettings.Theme → int: {ex.Message}");
      }
    }

    /// <inheritdoc />
    public async Task<bool> TryDeleteCardAsync(int cardId)
    {
      await EnsureInitializedAsync();
      var card = await GetCardByIdAsync(cardId);
      if (card == null)
        return false;

      int workId = card.WorkId;

      TryDeleteLocalFile(card.FilePath);
      TryDeleteLocalFile(card.CoverPath);

      await _database.ExecuteAsync("DELETE FROM ReadingPositions WHERE CardId = ?", cardId);
      await _database.ExecuteAsync("DELETE FROM Notes WHERE CardId = ?", cardId);

      await _database.ExecuteAsync("DELETE FROM Cards WHERE Id = ?", cardId);

      int remaining = await _database.Table<Card>().Where(c => c.WorkId == workId).CountAsync();
      if (remaining == 0)
        await _database.ExecuteAsync("DELETE FROM Works WHERE Id = ?", workId);

      return true;
    }

    private static void TryDeleteLocalFile(string? relativeOrFullPath)
    {
      if (string.IsNullOrWhiteSpace(relativeOrFullPath))
        return;
      try
      {
        string path = relativeOrFullPath;
        if (!Path.IsPathRooted(path))
          path = Path.Combine(FileSystem.AppDataDirectory, relativeOrFullPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (File.Exists(path))
          File.Delete(path);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[DB] Не удалось удалить файл: {relativeOrFullPath} — {ex.Message}");
      }
    }

    // === READING POSITION ===
    /// <inheritdoc />
    public async Task<int> SaveReadingPositionAsync(ReadingPosition position)
    {
      await EnsureInitializedAsync();
      if (position.Id == 0)
        return await _database.InsertAsync(position);
      else
        return await _database.UpdateAsync(position);
    }

    /// <inheritdoc />
    public async Task<ReadingPosition> GetReadingPositionByCardIdAsync(int cardId)
    {
      await EnsureInitializedAsync();
      return await _database.Table<ReadingPosition>()
          .Where(rp => rp.CardId == cardId)
          .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<List<ReadingPosition>> GetAllReadingPositionsAsync()
    {
      await EnsureInitializedAsync();
      return await _database.Table<ReadingPosition>().ToListAsync();
    }

    /// <inheritdoc />
    public async Task<ReadingPosition?> GetLatestReadingPositionAsync()
    {
      await EnsureInitializedAsync();
      var all = await _database.Table<ReadingPosition>().ToListAsync();
      foreach (var rp in all.OrderByDescending(x => x.LastUpdated))
      {
        var card = await GetCardByIdAsync(rp.CardId);
        if (card != null && !string.IsNullOrWhiteSpace(card.FilePath))
          return rp;
      }
      return null;
    }

    /// <inheritdoc />
    public async Task UpdateReadingPositionAsync(ReadingPosition position)
    {
      await EnsureInitializedAsync();
      position.LastUpdated = DateTime.Now;
      await _database.UpdateAsync(position);
    }

    // === NOTE ===
    /// <inheritdoc />
    public async Task<int> SaveNoteAsync(Note note)
    {
      await EnsureInitializedAsync();
      return await _database.InsertAsync(note);
    }

    /// <inheritdoc />
    public async Task<List<Note>> GetNotesByCardIdAsync(int cardId)
    {
      await EnsureInitializedAsync();
      return await _database.Table<Note>().Where(n => n.CardId == cardId).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Note>> GetNotesByWorkIdAsync(int workId)
    {
      await EnsureInitializedAsync();
      return await _database.Table<Note>().Where(n => n.WorkId == workId).ToListAsync();
    }

    /// <inheritdoc />
    public async Task DeleteNoteAsync(int noteId)
    {
      await EnsureInitializedAsync();
      await _database.DeleteAsync<Note>(noteId);
    }

    // === SETTINGS ===
    /// <inheritdoc />
    public async Task<TextSettings> GetTextSettingsAsync()
    {
      await EnsureInitializedAsync();

      var settings = await _database.Table<TextSettings>()
          .Where(s => s.Id == 1)
          .FirstOrDefaultAsync();

      if (settings == null)
      {
        settings = new TextSettings { Id = 1 };
        await _database.InsertAsync(settings);
      }
      return settings;
    }

    /// <inheritdoc />
    public async Task SaveTextSettingsAsync(TextSettings settings)
    {
      await EnsureInitializedAsync();
      await _database.UpdateAsync(settings);
    }

    /// <inheritdoc />
    public async Task<InterfaceSettings> GetInterfaceSettingsAsync()
    {
      await EnsureInitializedAsync();

      var settings = await _database.Table<InterfaceSettings>()
          .Where(s => s.Id == 1)
          .FirstOrDefaultAsync();

      if (settings == null)
      {
        settings = new InterfaceSettings { Id = 1 };
        await _database.InsertAsync(settings);
      }

      NormalizeInterfaceSettings(settings);
      return settings;
    }

    static void NormalizeInterfaceSettings(InterfaceSettings settings)
    {
      settings.Theme = InterfaceThemeStored.Clamp(Convert.ToInt32(settings.Theme));
      settings.InterfaceLanguage = settings.InterfaceLanguage switch
      {
        "en" or "English" => "en",
        _ => "ru"
      };
      settings.InterfaceFontSize = Math.Clamp(
          settings.InterfaceFontSize,
          InterfacePreferenceCoordinator.InterfaceFontSizeMin,
          InterfacePreferenceCoordinator.InterfaceFontSizeMax);
    }

    /// <inheritdoc />
    public async Task SaveInterfaceSettingsAsync(InterfaceSettings settings)
    {
      await EnsureInitializedAsync();
      await _database.UpdateAsync(settings);
    }

    // === SEARCH FILTER ===
    /// <inheritdoc />
    public async Task<SearchFilter> GetSearchFilterAsync()
    {
      await EnsureInitializedAsync();

      var filter = await _database.Table<SearchFilter>()
          .Where(f => f.Id == 1)
          .FirstOrDefaultAsync();

      if (filter == null)
      {
        filter = new SearchFilter { Id = 1 };
        await _database.InsertAsync(filter);
      }
      return filter;
    }

    /// <inheritdoc />
    public async Task SaveSearchFilterAsync(SearchFilter filter)
    {
      await EnsureInitializedAsync();
      await _database.UpdateAsync(filter);
    }
  }
}
