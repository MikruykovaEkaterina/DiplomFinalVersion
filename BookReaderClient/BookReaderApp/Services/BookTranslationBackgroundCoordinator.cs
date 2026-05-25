using System.IO;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using BookReaderApp.ViewModels;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;

namespace BookReaderApp.Services;

/// <summary>
/// Фоновый опрос задачи перевода книги после ухода с экрана. Не отменяет job при OnDisappearing.
/// </summary>
public static class BookTranslationBackgroundCoordinator
{
  /// <summary>Интервал опроса прокси (секунды) при активном job.</summary>
  public const int PollIntervalSeconds = 35;

  /// <summary>Разумная верхняя граница ожидания завершения перевода книги перед отказом пользователю.</summary>
  public static readonly TimeSpan MaxWait = TimeSpan.FromHours(24);

  /// <summary>Прогресс/режим перевода изменились (привязка UI).</summary>
  public static event Action? StateChanged;

  static readonly object Gate = new();
  static CancellationTokenSource? _userCts;
  static string? _jobId;
  static int _cardId;
  static int _workId;
  static string _sourcePath = "";
  static BookLanguage _targetLang;
  static DateTime _startedUtc;
  static double _progress;
  static bool _isRunning;
  static bool _statusPollWaitingForNetwork;
  static bool _connectivityHookRegistered;
  static DateTime _lastOfflineUserNotifyUtc = DateTime.MinValue;

  /// <summary>Подписка на смену сети, чтобы обновить экран перевода после восстановления связи.</summary>
  public static void EnsureTranslationPollConnectivityHook()
  {
    if (_connectivityHookRegistered)
      return;
    try
    {
      Connectivity.ConnectivityChanged += (_, __) =>
      {
        try
        {
          if (HasActiveTranslation)
            Raise();
        }
        catch { }
      };
      _connectivityHookRegistered = true;
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[BookTranslationCoordinator] Connectivity hook: {ex.Message}");
    }
  }

  static bool IsStatusPollWaitingForNetworkLocked()
  {
    return _statusPollWaitingForNetwork && _isRunning && !string.IsNullOrEmpty(_jobId);
  }

  const string PrefJobId = "br_bt_job_id";
  const string PrefCardId = "br_bt_card_id";
  const string PrefWorkId = "br_bt_work_id";
  const string PrefTargetLang = "br_bt_target_lang";
  const string PrefSourcePath = "br_bt_source_path";
  const string PrefStartedUtc = "br_bt_started_utc";
  const string PrefProgressPct = "br_bt_progress_pct";

  /// <summary>Пытается возобновить опрос сохранённой задачи (после перезапуска приложения или с экрана перевода).</summary>
  public static void ScheduleResumePersistedJob(int delayMs = 300)
  {
    _ = Task.Run(() => ResumePersistedJobCoreAsync(delayMs));
  }

  static bool TryReadPersistedJobPreview(out int cardId, out int progressPct, out BookLanguage targetLang, out int workId)
  {
    cardId = 0;
    progressPct = 0;
    targetLang = BookLanguage.None;
    workId = 0;
    string jobId = Preferences.Get(PrefJobId, string.Empty);
    if (string.IsNullOrWhiteSpace(jobId))
      return false;
    cardId = Preferences.Get(PrefCardId, 0);
    if (cardId <= 0)
      return false;
    workId = Preferences.Get(PrefWorkId, 0);
    long startedBin = Preferences.Get(PrefStartedUtc, 0L);
    if (startedBin == 0)
      return false;
    try
    {
      var startedUtc = DateTime.FromBinary(startedBin);
      if ((DateTime.UtcNow - startedUtc) > MaxWait)
        return false;
    }
    catch
    {
      return false;
    }

    progressPct = Preferences.Get(PrefProgressPct, 0);
    int langInt = Preferences.Get(PrefTargetLang, (int)BookLanguage.None);
    if (Enum.IsDefined(typeof(BookLanguage), langInt))
      targetLang = (BookLanguage)langInt;
    return true;
  }

  static async Task ResumePersistedJobCoreAsync(int delayMs)
  {
    try
    {
      if (delayMs > 0)
        await Task.Delay(delayMs).ConfigureAwait(false);

      lock (Gate)
      {
        if (_isRunning)
          return;
      }

      string jobId = Preferences.Get(PrefJobId, string.Empty);
      if (string.IsNullOrWhiteSpace(jobId))
        return;

      int cardId = Preferences.Get(PrefCardId, 0);
      int workId = Preferences.Get(PrefWorkId, 0);
      int langInt = Preferences.Get(PrefTargetLang, (int)BookLanguage.None);
      string sourcePath = Preferences.Get(PrefSourcePath, "") ?? "";
      long startedBin = Preferences.Get(PrefStartedUtc, DateTime.UtcNow.ToBinary());
      DateTime startedUtc = DateTime.FromBinary(startedBin);

      if (cardId <= 0 || (DateTime.UtcNow - startedUtc) > MaxWait)
      {
        ClearPersistedJobSnapshot();
        return;
      }

      try
      {
        var db = ServiceLocator.Get<IDatabaseService>();
        if (db != null)
        {
          var card = await db.GetCardByIdAsync(cardId).ConfigureAwait(false);
          if (card == null)
          {
            ClearPersistedJobSnapshot();
            return;
          }
        }
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[BookTranslationCoordinator] resume DB: {ex.Message}");
      }

      int pctHint = Preferences.Get(PrefProgressPct, 0);

      var cts = new CancellationTokenSource();
      lock (Gate)
      {
        if (_isRunning)
          return;
        _userCts?.Cancel();
        _userCts = cts;
        _jobId = jobId;
        _cardId = cardId;
        _workId = workId;
        _sourcePath = sourcePath;
        _targetLang = Enum.IsDefined(typeof(BookLanguage), langInt) ? (BookLanguage)langInt : BookLanguage.None;
        _startedUtc = startedUtc;
        _progress = Math.Clamp(pctHint, 0, 100) / 100.0;
        _isRunning = true;
        _statusPollWaitingForNetwork = false;
      }

      Raise();
      _ = Task.Run(() => RunPollLoopAsync(cts));
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[BookTranslationCoordinator] ResumePersistedJobCoreAsync: {ex.Message}");
    }
  }

  static void WritePersistedJobSnapshot(
      string jobId, int cardId, int workId, BookLanguage targetLang, string sourcePath, DateTime startedUtc)
  {
    try
    {
      Preferences.Set(PrefJobId, jobId);
      Preferences.Set(PrefCardId, cardId);
      Preferences.Set(PrefWorkId, workId);
      Preferences.Set(PrefTargetLang, (int)targetLang);
      Preferences.Set(PrefSourcePath, sourcePath ?? "");
      Preferences.Set(PrefStartedUtc, startedUtc.ToBinary());
      Preferences.Set(PrefProgressPct, 0);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[BookTranslationCoordinator] WritePersistedJobSnapshot: {ex.Message}");
    }
  }

  static void ClearPersistedJobSnapshot()
  {
    try
    {
      Preferences.Remove(PrefJobId);
      Preferences.Remove(PrefCardId);
      Preferences.Remove(PrefWorkId);
      Preferences.Remove(PrefTargetLang);
      Preferences.Remove(PrefSourcePath);
      Preferences.Remove(PrefStartedUtc);
      Preferences.Remove(PrefProgressPct);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[BookTranslationCoordinator] ClearPersistedJobSnapshot: {ex.Message}");
    }
  }

  static DateTime? _lastUserCancelUtc;

  /// <summary>Для данного произведения (Work) сейчас выполняется перевод или есть сохранённая задача.</summary>
  public static bool IsTranslationInProgressForWork(int workId)
  {
    if (workId <= 0)
      return false;
    lock (Gate)
    {
      if (_isRunning && _workId == workId)
        return true;
    }
    return TryReadPersistedJobPreview(out _, out _, out _, out int w) && w == workId;
  }

  /// <summary>Карточка книги, для которой сейчас идёт перевод; 0 если задачи нет.</summary>
  public static int ActiveTranslationCardId
  {
    get
    {
      lock (Gate)
      {
        if (_isRunning && _cardId > 0)
          return _cardId;
      }

      return TryReadPersistedJobPreview(out int persistedCard, out _, out _, out _) ? persistedCard : 0;
    }
  }

  /// <summary>Есть ли активное задание в памяти или сохранённый снимок в Preferences после перезапуска.</summary>
  public static bool HasActiveTranslation
  {
    get
    {
      lock (Gate)
      {
        if (_isRunning && _cardId > 0)
          return true;
      }

      return TryReadPersistedJobPreview(out _, out _, out _, out _);
    }
  }

  /// <summary>Обновляет <see cref="BookTranslationViewModel"/> из активного или персистентного job.</summary>
  public static void SyncViewModel(BookTranslationViewModel vm)
  {
    lock (Gate)
    {
      if (_isRunning && _cardId > 0)
      {
        if (vm.CardId > 0 && vm.CardId != _cardId)
        {
          vm.IsTranslating = false;
          vm.TranslationProgress = 0;
          vm.ProgressNetworkNote = "";
          return;
        }

        vm.IsTranslating = true;
        vm.TranslationProgress = _progress;
        if (_targetLang != BookLanguage.None)
          vm.SelectedTargetLanguage = _targetLang;
        vm.ProgressNetworkNote = IsStatusPollWaitingForNetworkLocked()
            ? Strings.TranslationBookPollNetworkNote
            : "";
        return;
      }
    }

    if (TryReadPersistedJobPreview(out int pCard, out int pct, out BookLanguage pLang, out _))
    {
      if (vm.CardId > 0 && vm.CardId != pCard)
      {
        vm.IsTranslating = false;
        vm.TranslationProgress = 0;
        vm.ProgressNetworkNote = "";
        return;
      }

      vm.IsTranslating = true;
      vm.TranslationProgress = Math.Clamp(pct, 0, 100) / 100.0;
      if (pLang != BookLanguage.None)
        vm.SelectedTargetLanguage = pLang;
      vm.ProgressNetworkNote = "";
      return;
    }

    if (vm.CardId > 0)
    {
      vm.IsTranslating = false;
      vm.TranslationProgress = 0;
      vm.ProgressNetworkNote = "";
    }
  }

  /// <summary>
  /// Назначает задачу опроса. Возвращает false, если уже выполняется другая задача (переданный job при этом отменяется на сервере).
  /// </summary>
  public static bool BeginPollingAfterStart(BookTranslationViewModel vm, string jobId)
  {
    ArgumentNullException.ThrowIfNull(jobId);
    AppNotificationService.ClearTranslationSuccessSuppression();
    CancellationTokenSource? cts = null;
    DateTime startedUtcForPersist = default;
    lock (Gate)
    {
      if (_isRunning)
      {
        if (string.Equals(_jobId, jobId, StringComparison.Ordinal))
          return true;
      }
      else
      {
        _userCts?.Cancel();
        cts = new CancellationTokenSource();
        _userCts = cts;
        _jobId = jobId;
        _cardId = vm.CardId;
        _workId = vm.WorkId;
        _sourcePath = vm.SourceBookFilePath ?? "";
        _targetLang = vm.SelectedTargetLanguage;
        startedUtcForPersist = DateTime.UtcNow;
        _startedUtc = startedUtcForPersist;
        _progress = 0;
        _isRunning = true;
        _statusPollWaitingForNetwork = false;
      }
    }

    if (cts == null)
    {
      _ = Task.Run(async () =>
      {
        try
        {
          await new BookTranslationApiClient().CancelAsync(jobId, default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
          System.Diagnostics.Debug.WriteLine($"[BookTranslationCoordinator] отмена лишнего job: {ex.Message}");
        }
      });
      return false;
    }

    WritePersistedJobSnapshot(
        jobId,
        vm.CardId,
        vm.WorkId,
        vm.SelectedTargetLanguage,
        vm.SourceBookFilePath ?? "",
        startedUtcForPersist);

    Raise();
    var runCts = cts;
    _ = Task.Run(() => RunPollLoopAsync(runCts));
    return true;
  }

  static async Task RunPollLoopAsync(CancellationTokenSource cts)
  {
    var api = new BookTranslationApiClient();
    try
    {
      await PollLoopAsync(api, cts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      lock (Gate)
      {
        _isRunning = false;
        _jobId = null;
        _cardId = 0;
        _statusPollWaitingForNetwork = false;
      }
      ClearPersistedJobSnapshot();
      Raise();
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[BookTranslationCoordinator] PollLoop outer: {ex.Message}");
      lock (Gate)
        _statusPollWaitingForNetwork = true;
      Raise();
    }
  }

  static async Task PollLoopAsync(BookTranslationApiClient api, CancellationToken token)
  {
    while (!token.IsCancellationRequested)
    {
      string? jid;
      lock (Gate)
        jid = _jobId;
      if (string.IsNullOrEmpty(jid))
        return;

      if ((DateTime.UtcNow - _startedUtc) > MaxWait)
      {
        await api.CancelAsync(jid, CancellationToken.None).ConfigureAwait(false);
        lock (Gate)
        {
          _isRunning = false;
          _jobId = null;
          _cardId = 0;
          _statusPollWaitingForNetwork = false;
        }
        ClearPersistedJobSnapshot();
        Raise();
        await MainThread.InvokeOnMainThreadAsync(() =>
            ServiceLocator.Get<IAppNotificationService>()?.Show(
                Strings.Notification_Translation_PollTimeout24Hours,
                AppNotificationSeverity.Warning,
                TimeSpan.FromSeconds(10)));
        return;
      }

      BookTranslationPollOutcome outcome;
      BookTranslationStatus? st;
      try
      {
        (outcome, st) = await api.PollJobStatusAsync(jid, token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }

      if (outcome == BookTranslationPollOutcome.NotFoundOnServer)
      {
        lock (Gate)
        {
          _isRunning = false;
          _jobId = null;
          _cardId = 0;
          _statusPollWaitingForNetwork = false;
        }
        ClearPersistedJobSnapshot();
        Raise();
        await MainThread.InvokeOnMainThreadAsync(() =>
            ServiceLocator.Get<IAppNotificationService>()?.NotifyTranslationFileNoLongerOnServer());
        return;
      }

      if (outcome == BookTranslationPollOutcome.TransientFailure)
      {
        lock (Gate)
          _statusPollWaitingForNetwork = true;
        Raise();
        MaybeNotifyNoInternetUser();
        try
        {
          await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
          return;
        }
        continue;
      }

      lock (Gate)
        _statusPollWaitingForNetwork = false;

      if (st == null)
      {
        lock (Gate)
          _statusPollWaitingForNetwork = true;
        Raise();
        try
        {
          await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
          return;
        }
        continue;
      }

      int pct = Math.Clamp(st.Progress, 0, 100);
      lock (Gate)
        _progress = pct / 100.0;
      try
      {
        Preferences.Set(PrefProgressPct, pct);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[BookTranslationCoordinator] прогресс prefs: {ex.Message}");
      }
      Raise();

      switch (st.Status)
      {
        case BookTranslationJobStatus.Completed:
          await FinishDownloadAndImportAsync(api, jid, token).ConfigureAwait(false);
          return;
        case BookTranslationJobStatus.Failed:
        {
          bool skipFailToast = false;
          lock (Gate)
          {
            if (_lastUserCancelUtc.HasValue &&
                (DateTime.UtcNow - _lastUserCancelUtc.Value) < TimeSpan.FromSeconds(12))
              skipFailToast = true;
          }
          lock (Gate)
          {
            _isRunning = false;
            _jobId = null;
            _cardId = 0;
            _statusPollWaitingForNetwork = false;
          }
          ClearPersistedJobSnapshot();
          Raise();
          if (!skipFailToast)
          {
            string userMsg = FriendlyBookTranslationFailureMessage(st.Message);
            await MainThread.InvokeOnMainThreadAsync(() =>
                ServiceLocator.Get<IAppNotificationService>()?.Show(
                    userMsg,
                    AppNotificationSeverity.Warning,
                    TimeSpan.FromSeconds(10)));
          }
          return;
        }
        case BookTranslationJobStatus.Canceled:
          lock (Gate)
          {
            _isRunning = false;
            _jobId = null;
            _cardId = 0;
            _statusPollWaitingForNetwork = false;
          }
          ClearPersistedJobSnapshot();
          Raise();
          await MainThread.InvokeOnMainThreadAsync(() =>
          {
            ServiceLocator.Get<IAppNotificationService>()?.Show(
                Strings.Notification_Translation_UserStopped,
                AppNotificationSeverity.Info,
                TimeSpan.FromSeconds(6));
            return Task.CompletedTask;
          }).ConfigureAwait(false);
          return;
        case BookTranslationJobStatus.Expired:
          lock (Gate)
          {
            _isRunning = false;
            _jobId = null;
            _cardId = 0;
            _statusPollWaitingForNetwork = false;
          }
          ClearPersistedJobSnapshot();
          Raise();
          await MainThread.InvokeOnMainThreadAsync(() =>
              ServiceLocator.Get<IAppNotificationService>()?.NotifyTranslationFileNoLongerOnServer());
          return;
      }

      try
      {
        await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }
    }
  }

  static void MaybeNotifyNoInternetUser()
  {
    try
    {
      if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet)
        return;
      var now = DateTime.UtcNow;
      lock (Gate)
      {
        if ((now - _lastOfflineUserNotifyUtc).TotalSeconds < 45)
          return;
        _lastOfflineUserNotifyUtc = now;
      }

      MainThread.BeginInvokeOnMainThread(() =>
          ServiceLocator.Get<IAppNotificationService>()?.Show(
              Strings.Notification_Translation_OfflineNoInternet,
              AppNotificationSeverity.Warning,
              TimeSpan.FromSeconds(8)));
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[BookTranslationCoordinator] MaybeNotifyNoInternetUser: {ex.Message}");
    }
  }

  static async Task FinishDownloadAndImportAsync(BookTranslationApiClient api, string jobId, CancellationToken token)
  {
    int workId;
    BookLanguage lang;
    string sourcePath;
    int sourceCardId;
    lock (Gate)
    {
      workId = _workId;
      lang = _targetLang;
      sourcePath = _sourcePath;
      sourceCardId = _cardId;
    }

    var tmp = Path.Combine(Path.GetTempPath(), $"br-tr-{jobId}{InferExtension(sourcePath)}");
    try
    {
      var ok = await api.DownloadResultToFileAsync(jobId, tmp, token).ConfigureAwait(false);
      if (!ok)
      {
        lock (Gate)
        {
          _isRunning = false;
          _jobId = null;
          _cardId = 0;
          _statusPollWaitingForNetwork = false;
        }
        ClearPersistedJobSnapshot();
        Raise();
        await MainThread.InvokeOnMainThreadAsync(() =>
            ServiceLocator.Get<IAppNotificationService>()?.NotifyTranslationFileNoLongerOnServer());
        return;
      }

      var upload = ServiceLocator.Get<IBookUploadService>();
      if (upload == null)
      {
        lock (Gate)
        {
          _isRunning = false;
          _jobId = null;
          _cardId = 0;
          _statusPollWaitingForNetwork = false;
        }
        ClearPersistedJobSnapshot();
        Raise();
        return;
      }

      UploadResult? importResult = null;
      string sourceTitleForNotify = "";
      await MainThread.InvokeOnMainThreadAsync(async () =>
      {
        try
        {
          var db = ServiceLocator.Get<IDatabaseService>();
          if (db != null && sourceCardId > 0)
          {
            var src = await db.GetCardByIdAsync(sourceCardId).ConfigureAwait(true);
            sourceTitleForNotify = src?.Title?.Trim() ?? "";
          }
        }
        catch (Exception ex)
        {
          System.Diagnostics.Debug.WriteLine($"[BookTranslationCoordinator] source title: {ex.Message}");
        }

        importResult = await upload.ImportTranslatedBookVersionAsync(workId, tmp, lang, null, CancellationToken.None).ConfigureAwait(true);
      }).ConfigureAwait(false);

      lock (Gate)
      {
        _isRunning = false;
        _jobId = null;
        _cardId = 0;
        _statusPollWaitingForNetwork = false;
      }
      ClearPersistedJobSnapshot();
      Raise();

      await MainThread.InvokeOnMainThreadAsync(async () =>
      {
        if (importResult == null || !importResult.Success || importResult.CardId <= 0)
        {
          ServiceLocator.Get<IAppNotificationService>()?.Show(
              FriendlyBookTranslationFailureMessage(importResult?.ErrorMessage),
              AppNotificationSeverity.Warning,
              TimeSpan.FromSeconds(8));
          return;
        }
        Preferences.Set(AppNotificationService.PrefFirstOpenTranslationTipCardIdKey, importResult.CardId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ServiceLocator.Get<IAppNotificationService>()?.ShowTranslationCompleteOpenBook(importResult.CardId, sourceTitleForNotify, lang);
      }).ConfigureAwait(false);
    }
    finally
    {
      try
      {
        if (File.Exists(tmp))
          File.Delete(tmp);
      }
      catch { }
    }
  }

  static string InferExtension(string path)
  {
    var p = (path ?? "").ToLowerInvariant();
    if (p.EndsWith(".fb2.zip", StringComparison.Ordinal))
      return ".fb2.zip";
    if (p.EndsWith(".epub.zip", StringComparison.Ordinal))
      return ".epub.zip";
    return Path.GetExtension(path);
  }

  /// <summary>Отмена пользователем: останавливаем опрос и просим сервер отменить задачу.</summary>
  public static void RequestUserCancel()
  {
    lock (Gate)
      _lastUserCancelUtc = DateTime.UtcNow;

    string? jid;
    CancellationTokenSource? cts;
    lock (Gate)
    {
      jid = _jobId;
      cts = _userCts;
      _userCts = null;
      _isRunning = false;
      _jobId = null;
      _cardId = 0;
      _statusPollWaitingForNetwork = false;
    }
    try
    {
      cts?.Cancel();
    }
    catch { }

    if (!string.IsNullOrEmpty(jid))
      _ = new BookTranslationApiClient().CancelAsync(jid, default);

    ClearPersistedJobSnapshot();
    Raise();
    MainThread.BeginInvokeOnMainThread(() =>
        ServiceLocator.Get<IAppNotificationService>()?.Show(
            Strings.Notification_Translation_UserStopped,
            AppNotificationSeverity.Info,
            TimeSpan.FromSeconds(5)));
  }

  static string FriendlyBookTranslationFailureMessage(string? serverMsg)
  {
    if (string.IsNullOrWhiteSpace(serverMsg))
      return Strings.Notification_Translation_FailedGeneric;
    var t = serverMsg.Trim();
    if (t.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("API", StringComparison.OrdinalIgnoreCase) ||
        t.Length > 180)
      return Strings.Notification_Translation_FailedGeneric;
    if (t.StartsWith("Ошибка перевода:", StringComparison.Ordinal))
      return Strings.Notification_Translation_FailedGeneric;
    return t;
  }

  static void Raise()
  {
    try
    {
      StateChanged?.Invoke();
    }
    catch { }
  }
}
