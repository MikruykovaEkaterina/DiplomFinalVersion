using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BookReaderApp;
using BookReaderApp.Helpers;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace BookReaderApp.Services;

/// <summary>Единственная очередь нижних баннеров: длительности, переход по тапу к книге (<see cref="IAppNotificationService"/>).</summary>
public sealed class AppNotificationService : IAppNotificationService
{
  /// <summary>Ключ Preference: последняя карточка с подсказкой про перевод.</summary>
  public const string PrefFirstOpenTranslationTipCardIdKey = "br_first_open_translation_tip_card_id";

  const string PrefPendingTranslationCard = "br_pending_translation_card_id";
  const int MaxAnchorRetry = 12;

  enum QKind
  {
    General,
    UploadSuccessOpenBook,
    TranslationSuccessOpenBook,
    TranslationUnavailableOnServer,
  }

  sealed record QItem(QKind Kind, string Message, AppNotificationSeverity Severity, TimeSpan Duration, int? CardId);

  static readonly object QLock = new();
  static readonly Queue<QItem> Queue = new();
  static bool _draining;
  static bool _suppressTranslationSuccess;
  static int _consecutiveShowFailures;
  static DateTime _lastTranslationReplayAttemptUtc = DateTime.MinValue;

  static void RunOnMainThread(Action action)
  {
    if (MainThread.IsMainThread)
      action();
    else
      MainThread.BeginInvokeOnMainThread(action);
  }

  /// <summary>Разрешает снова показывать баннеры «перевод готов» после подавления.</summary>
  public static void ClearTranslationSuccessSuppression()
  {
    RunOnMainThread(() =>
    {
      lock (QLock)
        _suppressTranslationSuccess = false;
    });
  }

  /// <inheritdoc />
  public void Show(string message, AppNotificationSeverity severity = AppNotificationSeverity.Info, TimeSpan? duration = null)
  {
    if (string.IsNullOrWhiteSpace(message))
      return;
    RunOnMainThread(() =>
    {
      var d = duration ?? DefaultDuration(severity);
      Add(new QItem(QKind.General, message.Trim(), severity, d, null));
    });
  }

  /// <inheritdoc />
  public void NotifyTranslationFileNoLongerOnServer()
  {
    RunOnMainThread(NotifyTranslationFileNoLongerOnServerCore);
  }

  void NotifyTranslationFileNoLongerOnServerCore()
  {
    string msg = Strings.Notification_TranslationExpired;
    lock (QLock)
    {
      _suppressTranslationSuccess = true;
      var kept = Queue.Where(q => q.Kind != QKind.TranslationSuccessOpenBook).ToList();
      Queue.Clear();
      foreach (var x in kept)
        Queue.Enqueue(x);
    }
    Add(new QItem(QKind.TranslationUnavailableOnServer, msg, AppNotificationSeverity.Warning, TimeSpan.FromSeconds(12), null));
  }

  /// <inheritdoc />
  public void DismissUploadBanner()
  {
    InAppNotificationPresenter.DismissCurrent();
  }

  /// <inheritdoc />
  public void ShowUploadInvalidFormat()
  {
    Show(
        Strings.Notification_InvalidUploadFormat,
        AppNotificationSeverity.Error,
        TimeSpan.FromSeconds(7));
  }

  /// <inheritdoc />
  public void ShowUploadCancelledOrFailed(bool cancelled, string? detail = null)
  {
    string msg = cancelled
        ? Strings.Notification_UploadCancelled
        : string.IsNullOrWhiteSpace(detail)
            ? Strings.Notification_UploadFailedGeneric
            : SanitizeDetail(detail!.Trim());
    Show(msg, AppNotificationSeverity.Warning, TimeSpan.FromSeconds(6));
  }

  /// <summary>Не показывает сырой URL и служебные фрагменты в тексте ошибки загрузки.</summary>
  static string SanitizeDetail(string raw)
  {
    if (raw.Contains("config", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("ApiBase", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("http://", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("https://", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
      return Strings.Notification_UploadFailedGenericLater;
    if (raw.Length > 160)
      raw = raw[..160].Trim() + "…";
    return string.Format(Strings.Notification_UploadFailedWithDetail, raw);
  }

  /// <inheritdoc />
  public void ShowUploadSuccessOpenBook(int cardId)
  {
    if (cardId <= 0)
      return;
    RunOnMainThread(() =>
        Add(new QItem(
            QKind.UploadSuccessOpenBook,
            Strings.Notification_UploadSuccessTapOpen,
            AppNotificationSeverity.Success,
            TimeSpan.FromSeconds(8),
            cardId)));
  }

  /// <inheritdoc />
  public void ShowTranslationCompleteOpenBook(int translatedCardId, string sourceBookTitle, BookLanguage targetLanguage)
  {
    if (translatedCardId <= 0)
      return;
    RunOnMainThread(() =>
    {
      lock (QLock)
      {
        if (_suppressTranslationSuccess)
          return;
      }

      try
      {
        Microsoft.Maui.Storage.Preferences.Set(PrefPendingTranslationCard, translatedCardId);
      }
      catch { }

      string t = string.IsNullOrWhiteSpace(sourceBookTitle) ? "…" : sourceBookTitle.Trim();
      string lang = LocalizedEnumHelper.GetBookLanguageString(targetLanguage);
      if (string.IsNullOrWhiteSpace(lang) || targetLanguage == BookLanguage.None)
        lang = "…";
      string body = string.Format(Strings.Notification_TranslationCompleteFormat, t, lang);

      Add(new QItem(
          QKind.TranslationSuccessOpenBook,
          body,
          AppNotificationSeverity.Success,
          TimeSpan.FromSeconds(10),
          translatedCardId));
    });
  }

  /// <inheritdoc />
  public void TryReplayMissedTranslationCompleteBanner()
  {
    var now = DateTime.UtcNow;
    if (now - _lastTranslationReplayAttemptUtc < TimeSpan.FromSeconds(50))
      return;
    _lastTranslationReplayAttemptUtc = now;

    RunOnMainThread(() => { _ = ReplayMissedTranslationBannerWhenPrefStillPendingAsync(); });
  }

  async System.Threading.Tasks.Task ReplayMissedTranslationBannerWhenPrefStillPendingAsync()
  {
    try
    {
      await System.Threading.Tasks.Task.Delay(700).ConfigureAwait(true);
      int id;
      try { id = Microsoft.Maui.Storage.Preferences.Get(PrefPendingTranslationCard, 0); }
      catch { return; }
      if (id <= 0) return;

      var db = ServiceLocator.Get<IDatabaseService>();
      if (db == null) return;
      var card = await db.GetCardByIdAsync(id).ConfigureAwait(true);
      if (card == null)
      {
        TryClearPendingPref(id);
        return;
      }

      string title = string.IsNullOrWhiteSpace(card.Title) ? "…" : card.Title.Trim();
      ShowTranslationCompleteOpenBook(id, title, BookLanguage.None);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[AppNotificationService] ReplayMissedTranslation: {ex.Message}");
    }
  }

  void Add(QItem item)
  {
    bool startDrain;
    lock (QLock)
    {
      if (item.Kind == QKind.TranslationSuccessOpenBook && _suppressTranslationSuccess)
        return;
      Queue.Enqueue(item);
      if (_draining)
      {
        startDrain = false;
        return;
      }
      _draining = true;
      startDrain = true;
    }

    if (startDrain)
      _ = DrainAsync();
  }

  async Task DrainAsync()
  {
    try
    {
      while (true)
      {
        QItem? next = null;
        lock (QLock)
        {
          while (Queue.Count > 0)
          {
            var cand = Queue.Dequeue();
            if (cand.Kind == QKind.TranslationSuccessOpenBook && _suppressTranslationSuccess)
              continue;
            next = cand;
            break;
          }
          if (next == null)
          {
            _draining = false;
            return;
          }
        }

        bool shown = false;
        try
        {
          shown = await ShowInAppBannerAsync(next).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
          System.Diagnostics.Debug.WriteLine($"[AppNotificationService] ShowInAppBannerAsync: {ex.Message}");
        }

        if (!shown)
        {
          int fail = Interlocked.Increment(ref _consecutiveShowFailures);
          if (fail < 40)
          {
            lock (QLock)
              Queue.Enqueue(next);
            await Task.Delay(Math.Min(150 + fail * 25, 900)).ConfigureAwait(true);
          }
          else
          {
            System.Diagnostics.Debug.WriteLine(
                $"[AppNotificationService] Пропуск уведомления после {fail} неудачных показов: {next.Message[..Math.Min(80, next.Message.Length)]}…");
            Interlocked.Exchange(ref _consecutiveShowFailures, 0);
          }
        }
        else
          Interlocked.Exchange(ref _consecutiveShowFailures, 0);
      }
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[AppNotificationService] Drain: {ex.Message}");
      lock (QLock)
        _draining = false;
    }
  }

  async Task<bool> ShowInAppBannerAsync(QItem n)
  {
    bool withOpen = n.CardId is > 0
        && (n.Kind == QKind.UploadSuccessOpenBook || n.Kind == QKind.TranslationSuccessOpenBook);
    int? openId = withOpen ? n.CardId : null;

    for (int attempt = 0; attempt < MaxAnchorRetry; attempt++)
    {
      if (ResolveCurrentContentPageForBanner() == null)
      {
        await Task.Delay(100).ConfigureAwait(true);
        continue;
      }

      try
      {
        bool ok = await InAppNotificationPresenter.ShowAsync(
            n.Message,
            n.Severity,
            n.Duration,
            openId,
            OpenBookFromNotificationAsync).ConfigureAwait(true);

        if (ok && n.Kind == QKind.TranslationSuccessOpenBook && n.CardId is int tid && tid > 0)
          TryClearPendingPref(tid);

        if (ok)
          return true;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[AppNotificationService] Баннер: {ex.Message}");
        await Task.Delay(120).ConfigureAwait(true);
      }
    }

    System.Diagnostics.Debug.WriteLine("[AppNotificationService] Не удалось показать баннер после нескольких попыток.");
    return false;
  }

  static ContentPage? ResolveCurrentContentPageForBanner()
  {
    if (Shell.Current?.CurrentPage is ContentPage cp)
      return cp;
    return Application.Current?.Windows.FirstOrDefault()?.Page as ContentPage;
  }

  static void TryClearPendingPref(int cardId)
  {
    try
    {
      int v = Microsoft.Maui.Storage.Preferences.Get(PrefPendingTranslationCard, 0);
      if (v == cardId)
        Microsoft.Maui.Storage.Preferences.Remove(PrefPendingTranslationCard);
    }
    catch { }
  }

  static async Task OpenBookFromNotificationAsync(int cardId)
  {
    try
    {
      var db = ServiceLocator.Get<IDatabaseService>();
      if (db == null)
        return;
      var card = await db.GetCardByIdAsync(cardId).ConfigureAwait(true);
      if (card == null)
        return;

      var reading = new ReadingPage();
      reading.SetBookData(card);
      if (Shell.Current != null)
        await Shell.Current.Navigation.PushAsync(reading).ConfigureAwait(true);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[AppNotificationService] OpenBook: {ex.Message}");
    }
  }

  static TimeSpan DefaultDuration(AppNotificationSeverity s) =>
      s switch
      {
        AppNotificationSeverity.Error => TimeSpan.FromSeconds(5.5),
        AppNotificationSeverity.Warning => TimeSpan.FromSeconds(4.5),
        AppNotificationSeverity.Success => TimeSpan.FromSeconds(4),
        _ => TimeSpan.FromSeconds(4)
      };
}
