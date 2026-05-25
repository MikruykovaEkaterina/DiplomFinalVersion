using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using BookReaderApp.Helpers;
using BookReaderApp.Localization;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using BookReaderApp.Services;

namespace BookReaderApp.ViewModels
{
  /// <summary>
  /// Экран загрузки книги: выбор файла, языка издания, отображение прогресса загрузки; синхронизация с <see cref="BookUploadBackgroundCoordinator.SyncViewModel"/>.
  /// </summary>
  public class UploadViewModel : INotifyPropertyChanged
  {
    readonly IFilePickerService _filePickerService;
    readonly IBookParserService _bookParserService;
    readonly IBookUploadService _bookUploadService;

    /// <summary>Все значения <see cref="BookLanguage"/>, кроме None, для пикера языка.</summary>
    public List<BookLanguage> LanguageOptions { get; } =
        Enum.GetValues(typeof(BookLanguage)).Cast<BookLanguage>()
          .Where(l => l != BookLanguage.None)
          .ToList();

    BookLanguage? _selectedLanguage;

    /// <summary>Выбранный язык книги; null — язык ещё не выбран.</summary>
    public BookLanguage? SelectedLanguage
    {
      get => _selectedLanguage;
      set
      {
        if (_selectedLanguage != value)
        {
          _selectedLanguage = value;
          OnPropertyChanged();
          OnPropertyChanged(nameof(IsLanguageSelected));
          OnPropertyChanged(nameof(SelectedLanguageDisplay));
          OnPropertyChanged(nameof(ProgressText));
        }
      }
    }

    /// <summary>Подпись выбранного языка или заголовок «выберите язык».</summary>
    public string SelectedLanguageDisplay =>
        !_selectedLanguage.HasValue
            ? Strings.SelectLanguageTitle
            : LocalizedEnumHelper.GetBookLanguageString(_selectedLanguage.Value);

    /// <summary>Язык выбран и входит в список допустимых.</summary>
    public bool IsLanguageSelected => _selectedLanguage.HasValue && LanguageOptions.Contains(_selectedLanguage.Value);

    bool _isUploading;

    /// <summary>Идёт сохранение файла и разбор метаданных.</summary>
    public bool IsUploading
    {
      get => _isUploading;
      set
      {
        if (_isUploading != value)
        {
          _isUploading = value;
          OnPropertyChanged();
          OnPropertyChanged(nameof(ProgressText));
        }
      }
    }

    double _uploadProgress;

    /// <summary>Доля выполнения загрузки 0.0–1.0.</summary>
    public double UploadProgress
    {
      get => _uploadProgress;
      set
      {
        if (_uploadProgress != value)
        {
          _uploadProgress = value;
          OnPropertyChanged();
          OnPropertyChanged(nameof(UploadProgressPercent));
        }
      }
    }

    /// <summary>Текст процента для привязки к Label.</summary>
    public string UploadProgressPercent => $"{Math.Round(_uploadProgress * 100)}%";

    /// <summary>Сообщение о ходе загрузки с учётом выбранного языка.</summary>
    public string ProgressText
    {
      get
      {
        if (!_selectedLanguage.HasValue || !IsUploading)
          return "";
        return string.Format(
            Strings.Upload_Progress_Format,
            LocalizedEnumHelper.GetBookLanguageString(_selectedLanguage.Value));
      }
    }

    /// <summary>Вызывается координатором фоновой загрузки для обновления привязок без смены ссылок на сервисы.</summary>
    public void OnExternalProgressRefreshRequested()
    {
      OnPropertyChanged(nameof(IsUploading));
      OnPropertyChanged(nameof(UploadProgress));
      OnPropertyChanged(nameof(UploadProgressPercent));
      OnPropertyChanged(nameof(ProgressText));
      OnPropertyChanged(nameof(SelectedLanguageDisplay));
    }

    /// <summary>Переводит подписи прогресса при смене языка UI.</summary>
    void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
      if (!string.IsNullOrEmpty(e.PropertyName) &&
          e.PropertyName != nameof(LocalizationResourceManager.CurrentCulture))
        return;
      OnPropertyChanged(nameof(SelectedLanguageDisplay));
      OnPropertyChanged(nameof(ProgressText));
    }

    /// <summary>Подключение общих singleton-сервисов загрузки и парсинга из DI.</summary>
    public UploadViewModel(
        IFilePickerService filePickerService,
        IBookParserService bookParserService,
        IBookUploadService bookUploadService)
    {
      _filePickerService = filePickerService ?? throw new ArgumentNullException(nameof(filePickerService));
      _bookParserService = bookParserService ?? throw new ArgumentNullException(nameof(bookParserService));
      _bookUploadService = bookUploadService ?? throw new ArgumentNullException(nameof(bookUploadService));
      SelectedLanguage = null;
      LocalizationResourceManager.Instance.PropertyChanged += OnLocalizationChanged;
    }

    /// <summary>Открывает системный диалог выбора файла книги.</summary>
    public async Task<FilePickerResult> PickBookAsync() =>
        await _filePickerService.PickBookFileAsync();

    /// <summary>Извлекает метаданные книги из пути к файлу.</summary>
    public BookMetadata ParseBook(string filePath) =>
        _bookParserService.ParseBookMetadata(filePath);

    /// <summary>Сохраняет книгу в хранилище приложения и БД с отчётом о прогрессе.</summary>
    public async Task<UploadResult> SaveBookAsync(string filePath, IProgress<double>? progress = null)
    {
      if (!SelectedLanguage.HasValue)
        return new UploadResult { Success = false, ErrorMessage = Strings.Upload_Error_LanguageNotSelected };

      return await _bookUploadService.UploadAndSaveBookAsync(
          filePath,
          ParseBook(filePath),
          SelectedLanguage.Value,
          progress);
    }

    /// <summary>Событие изменения свойства для привязок.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Уведомляет об изменении свойства.</summary>
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
  }
}
