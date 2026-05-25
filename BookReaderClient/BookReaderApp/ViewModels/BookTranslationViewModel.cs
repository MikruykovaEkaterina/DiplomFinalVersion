using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BookReaderApp.Helpers;
using BookReaderApp.Localization;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using BookReaderApp.Services;

namespace BookReaderApp.ViewModels
{
  /// <summary>Экран перевода книги: выбор языка, прогресс, запуск фоновой задачи на сервере перевода.</summary>
  public class BookTranslationViewModel : INotifyPropertyChanged
  {
    int _cardId;
    int _workId;
    string _sourceBookPath = "";
    string _sourceIso = "en";

    private string _bookTitle = "";

    /// <summary>Заголовок книги для подписей прогресса.</summary>
    public string BookTitle
    {
      get => _bookTitle;
      set
      {
        if (_bookTitle != value)
        {
          _bookTitle = value;
          OnPropertyChanged();
          OnPropertyChanged(nameof(BookTitleLine));
          OnPropertyChanged(nameof(HasBookTitle));
          OnPropertyChanged(nameof(ProgressText));
        }
      }
    }

    /// <summary>Название в кавычках для отображения или пустая строка.</summary>
    public string BookTitleLine => string.IsNullOrWhiteSpace(BookTitle) ? "" : $"«{BookTitle}»";

    /// <summary>Задан непустой заголовок.</summary>
    public bool HasBookTitle => !string.IsNullOrWhiteSpace(_bookTitle);

    private List<BookLanguage> _languageOptions = new();

    /// <summary>Допустимые целевые языки: первая позиция «не выбран», далее языки без оригинала и уже добавленных версий.</summary>
    public List<BookLanguage> LanguageOptions
    {
      get => _languageOptions;
      private set { _languageOptions = value ?? new List<BookLanguage>(); OnPropertyChanged(); }
    }

    /// <summary>Подписывается на смену языка UI для обновления строк прогресса.</summary>
    public BookTranslationViewModel()
    {
      LocalizationResourceManager.Instance.PropertyChanged += OnLocalizationChanged;
    }

    /// <summary>Обновляет локализованные подписи при смене культуры.</summary>
    void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
      if (!string.IsNullOrEmpty(e.PropertyName) &&
          e.PropertyName != nameof(LocalizationResourceManager.CurrentCulture))
        return;
      OnPropertyChanged(nameof(SelectedTargetLanguageDisplay));
      OnPropertyChanged(nameof(ProgressText));
    }

    private BookLanguage _selectedTargetLanguage = BookLanguage.None;

    /// <summary>Выбранный язык перевода (None — не выбран).</summary>
    public BookLanguage SelectedTargetLanguage
    {
      get => _selectedTargetLanguage;
      set
      {
        if (_selectedTargetLanguage != value)
        {
          _selectedTargetLanguage = value;
          OnPropertyChanged();
          OnPropertyChanged(nameof(IsLanguageSelected));
          OnPropertyChanged(nameof(SelectedTargetLanguageDisplay));
          OnPropertyChanged(nameof(ProgressText));
        }
      }
    }

    /// <summary>Подпись языка или приглашение выбрать язык.</summary>
    public string SelectedTargetLanguageDisplay =>
        _selectedTargetLanguage == BookLanguage.None
            ? Strings.SelectLanguageTitle
            : LocalizedEnumHelper.GetBookLanguageString(_selectedTargetLanguage);

    /// <summary>Выбран конкретный язык из <see cref="LanguageOptions"/>.</summary>
    public bool IsLanguageSelected =>
        _selectedTargetLanguage != BookLanguage.None && _languageOptions.Contains(_selectedTargetLanguage);

    private bool _isTranslating;

    /// <summary>Идёт удалённый перевод и опрос статуса.</summary>
    public bool IsTranslating
    {
      get => _isTranslating;
      set
      {
        if (_isTranslating != value)
        {
          _isTranslating = value;
          OnPropertyChanged();
          OnPropertyChanged(nameof(ProgressText));
          OnPropertyChanged(nameof(HasProgressNetworkNote));
        }
      }
    }

    private string _progressNetworkNote = "";

    /// <summary>Подсказка при потере связи с прокси во время опроса перевода книги.</summary>
    public string ProgressNetworkNote
    {
      get => _progressNetworkNote;
      set
      {
        var v = value ?? "";
        if (_progressNetworkNote != v)
        {
          _progressNetworkNote = v;
          OnPropertyChanged();
          OnPropertyChanged(nameof(HasProgressNetworkNote));
        }
      }
    }

    /// <summary>Показать блок сетевой подсказки.</summary>
    public bool HasProgressNetworkNote => !string.IsNullOrWhiteSpace(_progressNetworkNote);

    private double _translationProgress;

    /// <summary>Ход перевода 0.0–1.0.</summary>
    public double TranslationProgress
    {
      get => _translationProgress;
      set
      {
        if (Math.Abs(_translationProgress - value) > 0.0001)
        {
          _translationProgress = value;
          OnPropertyChanged();
          OnPropertyChanged(nameof(TranslationProgressPercent));
        }
      }
    }

    /// <summary>Текст процента для индикатора.</summary>
    public string TranslationProgressPercent => $"{Math.Round(_translationProgress * 100)}%";

    /// <summary>Составная строка состояния перевода (название и целевой язык).</summary>
    public string ProgressText
    {
      get
      {
        if (!_isTranslating)
          return "";
        string lang = _selectedTargetLanguage == BookLanguage.None
            ? ""
            : LocalizedEnumHelper.GetBookLanguageString(_selectedTargetLanguage);
        bool hasTitle = !string.IsNullOrWhiteSpace(BookTitle);
        if (hasTitle && !string.IsNullOrEmpty(lang))
          return string.Format(Strings.Translation_Progress_WithTitleTarget, BookTitle, lang);
        if (hasTitle && string.IsNullOrEmpty(lang))
          return string.Format(Strings.Translation_Progress_TitleOnly, BookTitle);
        if (!hasTitle && !string.IsNullOrEmpty(lang))
          return string.Format(Strings.Translation_Progress_TargetOnly, lang);
        return "";
      }
    }

    /// <summary>Идентификатор исходной карточки после <see cref="ApplyCardAsync"/>.</summary>
    public int CardId => _cardId;

    /// <summary>Идентификатор произведения.</summary>
    public int WorkId => _workId;

    /// <summary>Путь к файлу оригинала на устройстве.</summary>
    public string SourceBookFilePath => _sourceBookPath;

    /// <summary>ISO код языка оригинала для API перевода.</summary>
    public string SourceIso => _sourceIso;

    /// <summary>Заполняет поля из карточки и строит список доступных целевых языков по соседним карточкам работы.</summary>
    public async Task ApplyCardAsync(Card? card, IDatabaseService db)
    {
      BookTitle = card?.Title?.Trim() ?? "";
      _cardId = card?.Id ?? 0;
      _workId = card?.WorkId ?? 0;
      _sourceBookPath = card?.FilePath?.Trim() ?? "";
      _sourceIso = TranslationLanguageCodes.TryGetIsoCode(card?.Language) ?? "en";

      var takenLangs = new HashSet<BookLanguage>();
      if (card != null && card.WorkId > 0)
      {
        var siblings = await db.GetCardsByWorkIdAsync(card.WorkId).ConfigureAwait(false);
        foreach (var c in siblings)
          MapDisplayToBookLanguage(c.Language, takenLangs);
      }

      var sourceLang = MapDisplayToBookLanguageSingle(card?.Language);

      var all = Enum.GetValues(typeof(BookLanguage)).Cast<BookLanguage>()
          .Where(l => l != BookLanguage.None)
          .ToList();

      var options = new List<BookLanguage> { BookLanguage.None };
      foreach (var l in all)
      {
        if (sourceLang == l)
          continue;
        if (takenLangs.Contains(l))
          continue;
        options.Add(l);
      }

      LanguageOptions = options;
      SelectedTargetLanguage = BookLanguage.None;
    }

    /// <summary>Добавляет язык карточки в множество занятых версий.</summary>
    static void MapDisplayToBookLanguage(string? language, HashSet<BookLanguage> into)
    {
      var x = MapDisplayToBookLanguageSingle(language);
      if (x.HasValue)
        into.Add(x.Value);
    }

    /// <summary>Преобразует строку языка карточки в enum или null.</summary>
    static BookLanguage? MapDisplayToBookLanguageSingle(string? language)
    {
      var lang = BookLanguageStorage.FromStored(language);
      return lang == BookLanguage.None ? null : lang;
    }

    /// <summary>ISO код выбранного целевого языка для запуска перевода или null.</summary>
    public string? GetTargetIso()
    {
      if (!IsLanguageSelected)
        return null;
      return TranslationLanguageCodes.TryGetIsoFromBookLanguage(_selectedTargetLanguage);
    }

    /// <summary>Синхронизация с фоновым координатором перевода без смены полей по ссылке.</summary>
    public void OnExternalProgressRefreshRequested()
    {
      OnPropertyChanged(nameof(TranslationProgressPercent));
      OnPropertyChanged(nameof(SelectedTargetLanguageDisplay));
      OnPropertyChanged(nameof(ProgressText));
      OnPropertyChanged(nameof(IsTranslating));
      OnPropertyChanged(nameof(ProgressNetworkNote));
      OnPropertyChanged(nameof(HasProgressNetworkNote));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Уведомляет об изменении свойства.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
      => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
  }
}
