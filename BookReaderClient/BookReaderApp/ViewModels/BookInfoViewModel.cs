using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BookReaderApp.Helpers;
using BookReaderApp.Models;
using BookReaderApp.Resources;

namespace BookReaderApp.ViewModels
{
  /// <summary>
  /// Данные одной карточки книги для каталога и свайп-карточки: метаданные, прогресс чтения, реакция, обложка и вычисляемые подписи для привязок.
  /// </summary>
  public class BookInfoViewModel : INotifyPropertyChanged
  {
    /// <summary>Идентификатор карточки в БД.</summary>
    public int CardId { get; set; }

    /// <summary>Идентификатор произведения (<see cref="Work.Id"/>), общий для всех языковых версий.</summary>
    public int WorkId { get; set; }

    /// <summary>Название книги.</summary>
    public string Title { get; set; }

    /// <summary>Автор (сырое поле из карточки).</summary>
    public string Author { get; set; }

    /// <summary>Размер полного текста в символах.</summary>
    public long TotalChars { get; set; }

    /// <summary>Символ, на котором остановилось чтение (ReadingPositions.CharacterOffset), не максимум за сессии.</summary>
    public long ReadingPositionOffset { get; set; }

    /// <summary>Дата добавления карточки в библиотеку.</summary>
    public DateTime DateAdded { get; set; }

    /// <summary>Дата последнего открытия этой языковой версии.</summary>
    public DateTime LastOpened { get; set; }

    /// <summary>Язык карточки в виде ISO-кода (ru/en) или пусто; локализованная подпись в UI считается отдельно.</summary>
    public string Language { get; set; }

    /// <summary>Текст описания для раскрывающегося блока на карточке.</summary>
    public string? Description { get; set; }

    /// <summary>Путь к файлу книги на устройстве.</summary>
    public string FilePath { get; set; }

    /// <summary>Формат файла (FB2, EPUB и т. д.).</summary>
    public string Format { get; set; }

    BookReaction _reaction = BookReaction.Unrated;
    BookStatus _readingStatus = BookStatus.New;

    /// <summary>Реакция пользователя на книгу (из <see cref="Work"/>).</summary>
    public BookReaction Reaction
    {
      get => _reaction;
      set
      {
        if (_reaction == value)
          return;
        _reaction = value;
        OnPropertyChanged();
        ChromeChanged();
      }
    }

    /// <summary>Статус чтения произведения (из <see cref="Work"/>).</summary>
    public BookStatus ReadingStatus
    {
      get => _readingStatus;
      set
      {
        if (_readingStatus == value)
          return;
        _readingStatus = value;
        OnPropertyChanged();
        ChromeChanged();
      }
    }

    /// <summary>«Прочитано» — вручную из меню карточки или при открытии последней страницы в чтении; снять — только из меню («Убрать прочитано»).</summary>
    public bool IsReadDone => ReadingStatus == BookStatus.Read;

    /// <summary>Избранная книга (в БД <see cref="BookReaction.Favorite"/>).</summary>
    public bool IsFavorite => Reaction == BookReaction.Favorite;

    /// <summary>Показывать ленту «Прочитано» на обложке.</summary>
    public bool ShowReadRibbon => IsReadDone;

    /// <summary>Показывать ленту «Избранное» на обложке.</summary>
    public bool ShowFavoriteRibbon => IsFavorite;

    /// <summary>Есть хотя бы одна статусная лента на обложке.</summary>
    public bool HasStatusRibbons => ShowReadRibbon || ShowFavoriteRibbon;

    /// <summary>Ключ цвета фона карточки в ResourceDictionary (избранное важнее «прочитано»).</summary>
    public string CardBackgroundResourceKey
    {
      get
      {
        if (IsFavorite) return "BookCardSurfaceFavorite";
        if (IsReadDone) return "BookCardSurfaceRead";
        return "BookCardBackground";
      }
    }

    /// <summary>Подсветка кнопки меню при активном статусе «прочитано» или «избранное».</summary>
    public bool MenuButtonHighlighted => IsReadDone || IsFavorite;

    /// <summary>Синхронизирует поля с записью Work после обновления БД (все языковые версии одной книги).</summary>
    public void ApplyWorkSnapshot(Work work)
    {
      if (work == null) return;
      ReadingStatus = work.ReadingStatus;
      Reaction = work.Reaction;
    }

    /// <summary>Обновляет «последнее открытие» для этой карточки (только текущий язык).</summary>
    public void SetLastOpened(DateTime value)
    {
      if (LastOpened.Equals(value)) return;
      LastOpened = value;
      OnPropertyChanged(nameof(LastOpened));
    }

    /// <summary>Поднимает уведомления для лент, фона и кнопки меню после смены статуса или реакции.</summary>
    void ChromeChanged()
    {
      OnPropertyChanged(nameof(IsReadDone));
      OnPropertyChanged(nameof(IsFavorite));
      OnPropertyChanged(nameof(ShowReadRibbon));
      OnPropertyChanged(nameof(ShowFavoriteRibbon));
      OnPropertyChanged(nameof(HasStatusRibbons));
      OnPropertyChanged(nameof(CardBackgroundResourceKey));
      OnPropertyChanged(nameof(MenuButtonHighlighted));
    }

    private string _coverPath;

    /// <summary>Путь к файлу обложки на диске.</summary>
    public string CoverPath
    {
      get => _coverPath;
      set
      {
        _coverPath = value;
        OnPropertyChanged();
        OnPropertyChanged(nameof(CoverImage));
        OnPropertyChanged(nameof(HasCover));
      }
    }

    /// <summary>Источник изображения обложки или null, если файла нет.</summary>
    public ImageSource? CoverImage
    {
      get
      {
        if (string.IsNullOrEmpty(CoverPath))
          return null;

        try
        {
          if (File.Exists(CoverPath))
            return ImageSource.FromFile(CoverPath);
        }
        catch { }

        return null;
      }
    }

    /// <summary>Файл обложки существует и доступен.</summary>
    public bool HasCover => !string.IsNullOrEmpty(CoverPath) && File.Exists(CoverPath);

    /// <summary>Доля текста до текущей позиции чтения по <see cref="ReadingPositionOffset"/> и <see cref="TotalChars"/>, 0.0–1.0.</summary>
    public double Progress => TotalChars > 0 ? Math.Min(1.0, Math.Max(0, (double)ReadingPositionOffset / TotalChars)) : 0;

    /// <summary>Оценка символов на страницу (из настроек текста книги).</summary>
    public int CharsPerPageEstimate { get; set; } = 1800;

    /// <summary>Сохранённое в БД число страниц (актуализируется при смене настроек текста и TotalChars).</summary>
    public int StoredEstimatedPageCount { get; set; }

    /// <summary>Число страниц для подписи «Страниц»: из БД или оценка по <see cref="CharsPerPageEstimate"/>.</summary>
    public int Pages => TotalChars > 0
        ? (StoredEstimatedPageCount > 0
            ? StoredEstimatedPageCount
            : (int)Math.Ceiling(TotalChars / (double)Math.Max(1, CharsPerPageEstimate)))
        : 0;

    /// <summary>Собирает модель отображения из карточки и произведения; позиция чтения задаётся смещением символа.</summary>
    /// <param name="card">Карточка языковой версии.</param>
    /// <param name="work">Произведение (статус и реакция общие для языков).</param>
    /// <param name="charsPerPageEstimate">Символов на страницу из настроек текста.</param>
    /// <param name="readingPositionOffset">Смещение в полном тексте из таблицы позиций чтения.</param>
    public static BookInfoViewModel FromCardAndWork(Card card, Work? work, int charsPerPageEstimate = 1800, long readingPositionOffset = 0)
    {
      var vm = new BookInfoViewModel
      {
        CardId = card.Id,
        WorkId = card.WorkId,
        Title = card.Title ?? Strings.Book_NoTitle,
        Author = card.Author,
        TotalChars = card.TotalChars,
        ReadingPositionOffset = readingPositionOffset,
        DateAdded = card.AddedDate,
        LastOpened = card.LastOpened != default ? card.LastOpened : card.AddedDate,
        Language = BookLanguageStorage.NormalizeToIso(card.Language ?? ""),
        Description = card.Description,
        CoverPath = card.CoverPath,
        FilePath = card.FilePath,
        Format = card.Format,
        Reaction = work?.Reaction ?? BookReaction.Unrated,
        ReadingStatus = work?.ReadingStatus ?? BookStatus.New,
        CharsPerPageEstimate = Math.Max(1, charsPerPageEstimate),
        StoredEstimatedPageCount = Math.Max(0, card.EstimatedPageCount)
      };
      return vm;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>Уведомляет об изменении свойства.</summary>
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
  }
}
