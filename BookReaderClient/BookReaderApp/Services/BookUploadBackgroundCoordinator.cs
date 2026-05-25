using BookReaderApp;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using BookReaderApp.ViewModels;
using Microsoft.Maui.ApplicationModel;

namespace BookReaderApp.Services;

/// <summary>
/// Фоновая загрузка книги в библиотеку: можно уйти с экрана; прогресс восстанавливается при возврате в том же сеансе приложения.
/// Автоотмена через 2 минуты.
/// </summary>
public static class BookUploadBackgroundCoordinator
{
  /// <summary>Мягкий лимит длительности импорта (с отменой и уведомлением).</summary>
  public static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(2);

  /// <summary>Вызывается при изменении <see cref="IsRunning"/> или <see cref="Progress"/> (для синхронизации UI).</summary>
  public static event Action? StateChanged;

  /// <summary>Событие после успешного сохранения книги — сброс UI загрузки (язык и т.д.).</summary>
  public static event Action? UploadCompletedSuccessfully;

  static readonly object Gate = new();
  static CancellationTokenSource? _cts;
  static BookLanguage _language;
  static double _progress;
  static bool _isRunning;

  /// <summary>Выполняется ли сейчас фоновая загрузка в этом процессе.</summary>
  public static bool IsRunning
  {
    get
    {
      lock (Gate)
        return _isRunning;
    }
  }

  /// <summary>Прогресс загрузки 0.0–1.0 последней активной операции.</summary>
  public static double Progress
  {
    get
    {
      lock (Gate)
        return _progress;
    }
  }

  /// <summary>Язык издания, выбранный для последнего или текущего импорта.</summary>
  public static BookLanguage ActiveLanguage
  {
    get
    {
      lock (Gate)
        return _language;
    }
  }

  /// <summary>Копирует состояние координатора в свойства привязки <see cref="UploadViewModel"/>.</summary>
  public static void SyncViewModel(UploadViewModel vm)
  {
    lock (Gate)
    {
      vm.IsUploading = _isRunning;
      vm.UploadProgress = _progress;
      if (_isRunning)
        vm.SelectedLanguage = _language;
      else if (vm.SelectedLanguage == _language && _progress <= 0)
      {
        // Язык может остаться от прошлого выбора; сброс делает UploadPage по UploadCompletedSuccessfully.
      }
    }
    vm.OnExternalProgressRefreshRequested();
  }

  /// <summary>Запускает импорт в фоновом потоке с общим сервисом <see cref="IBookUploadService"/> из контейнера.</summary>
  public static void BeginUpload(string filePath, BookMetadata metadata, BookLanguage language)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
    if (language == BookLanguage.None)
      throw new ArgumentException("Language required", nameof(language));

    CancellationTokenSource? cts;
    lock (Gate)
    {
      _cts?.Cancel();
      _cts?.Dispose();
      cts = new CancellationTokenSource();
      _cts = cts;
      _language = language;
      _progress = 0;
      _isRunning = true;
    }

    Raise();
    _ = Task.Run(() => RunUploadCoreAsync(filePath, metadata, language, cts));
  }

  /// <summary>Основной цикл: <see cref="IBookUploadService.UploadAndSaveBookAsync"/>, уведомления и обновление каталога.</summary>
  static async Task RunUploadCoreAsync(
      string filePath,
      BookMetadata metadata,
      BookLanguage language,
      CancellationTokenSource outerCts)
  {
    var notify = ServiceLocator.Get<IAppNotificationService>();
    var upload = ServiceLocator.GetRequired<IBookUploadService>();
    var parser = ServiceLocator.GetRequired<IBookParserService>();

    using var timeout = new CancellationTokenSource(UploadTimeout);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token, timeout.Token);

    try
    {
      BookMetadata effMeta = metadata;
      try
      {
        var p = parser.ParseBookMetadata(filePath);
        if (p != null)
          effMeta = p;
      }
      catch { }

      if (upload == null)
      {
        lock (Gate)
        {
          _isRunning = false;
          _progress = 0;
        }
        Raise();
        MainThread.BeginInvokeOnMainThread(() =>
            notify?.Show(Strings.Notification_Upload_ServiceUnavailable, AppNotificationSeverity.Warning));
        return;
      }

      var progress = new Progress<double>(p =>
      {
        lock (Gate)
          _progress = Math.Clamp(p, 0, 1);
        Raise();
      });

      var result = await upload.UploadAndSaveBookAsync(filePath, effMeta, language, progress, linked.Token)
          .ConfigureAwait(false);

      lock (Gate)
      {
        _isRunning = false;
        _progress = 0;
      }
      Raise();

      await MainThread.InvokeOnMainThreadAsync(async () =>
      {
        if (result.Success && result.CardId > 0)
        {
          try
          {
            UploadCompletedSuccessfully?.Invoke();
          }
          catch { }
          if (MainPageViewModel.Instance != null)
            await MainPageViewModel.Instance.RefreshBooksAsync().ConfigureAwait(true);
          notify?.ShowUploadSuccessOpenBook(result.CardId);
        }
        else
          notify?.ShowUploadCancelledOrFailed(false, result.ErrorMessage);
      }).ConfigureAwait(true);
    }
    catch (OperationCanceledException)
    {
      lock (Gate)
      {
        _isRunning = false;
        _progress = 0;
      }
      Raise();
      bool timedOut = timeout.IsCancellationRequested && !outerCts.IsCancellationRequested;
      await MainThread.InvokeOnMainThreadAsync(() =>
      {
        if (timedOut)
          notify?.Show(
              Strings.Notification_Upload_TimeoutTwoMinutes,
              AppNotificationSeverity.Warning,
              TimeSpan.FromSeconds(8));
        else
          notify?.Show(Strings.Notification_UploadCancelled, AppNotificationSeverity.Info, TimeSpan.FromSeconds(5));
      }).ConfigureAwait(true);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[BookUploadCoordinator] {ex}");
      lock (Gate)
      {
        _isRunning = false;
        _progress = 0;
      }
      Raise();
      await MainThread.InvokeOnMainThreadAsync(() =>
          notify?.ShowUploadCancelledOrFailed(false, Strings.Notification_Upload_UnexpectedFailure)).ConfigureAwait(true);
    }
    finally
    {
      try
      {
        outerCts.Dispose();
      }
      catch { }
      lock (Gate)
      {
        if (ReferenceEquals(_cts, outerCts))
          _cts = null;
      }
    }
  }

  /// <summary>Безопасно вызывает подписчиков <see cref="StateChanged"/>.</summary>
  static void Raise()
  {
    try
    {
      StateChanged?.Invoke();
    }
    catch { }
  }
}
