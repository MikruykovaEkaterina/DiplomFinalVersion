using BookReaderApp.Helpers;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using BookReaderApp.Services;
using BookReaderApp.ViewModels;
using Microsoft.Maui.Controls;

namespace BookReaderApp;

/// <summary>
/// Экран полнокнижного перевода через прокси: выбор языка назначения, запуск задачи на сервере и отображение прогресса вместе с <see cref="BookTranslationBackgroundCoordinator"/>.
/// Реализует <see cref="IQueryAttributable"/> для получения <c>CardId</c> из маршрута Shell.
/// </summary>
public partial class TranslationPage : ContentPage, IQueryAttributable
{
  readonly BookTranslationViewModel _vm = new();
  readonly IDatabaseService _db;
  readonly BookTranslationApiClient _api = new();
  /// <summary>CardId из маршрута Shell; приоритет у глобально активной задачи перевода.</summary>
  int _navigationCardId;

  /// <summary>Создаёт страницу, задаёт локальный <see cref="BookTranslationViewModel"/> и сервис БД.</summary>
  public TranslationPage()
  {
    InitializeComponent();
    _db = new DatabaseService();
    BindingContext = _vm;
  }

  /// <summary>Открывает лист языка целевого перевода книги.</summary>
  async void OnTranslationTargetLanguageTapped(object sender, EventArgs e)
  {
    await ThemedEnumPickSheet.PickAsync(
        this,
        _vm.LanguageOptions,
        LocalizedEnumHelper.GetBookLanguageString,
        lang => _vm.SelectedTargetLanguage = lang,
        Strings.SelectLanguageTitle).ConfigureAwait(true);
  }

  /// <summary>Подписывается на фонового координатора перевода и пытается возобновить сохранённое задание.</summary>
  protected override void OnAppearing()
  {
    base.OnAppearing();
    BookTranslationBackgroundCoordinator.StateChanged += OnTranslationCoordinatorStateChanged;
    BookTranslationBackgroundCoordinator.ScheduleResumePersistedJob(120);
    _ = EnsureViewModelCardAsync();
  }

  /// <summary>Снимает подписку на события координатора при закрытии страницы.</summary>
  protected override void OnDisappearing()
  {
    BookTranslationBackgroundCoordinator.StateChanged -= OnTranslationCoordinatorStateChanged;
    base.OnDisappearing();
  }

  /// <summary>Переносит прогресс из фона в элементы управления после событий координатора.</summary>
  void OnTranslationCoordinatorStateChanged()
  {
    MainThread.BeginInvokeOnMainThread(() =>
    {
      BookTranslationBackgroundCoordinator.SyncViewModel(_vm);
      _vm.OnExternalProgressRefreshRequested();
    });
  }

  /// <summary>Разбирает параметры запроса Shell (<c>CardId</c>) и переинициализирует книгу для перевода.</summary>
  public void ApplyQueryAttributes(IDictionary<string, object> query)
  {
    if (!query.TryGetValue("CardId", out var raw) || raw == null)
    {
      _navigationCardId = 0;
      return;
    }

    int cardId = raw switch
    {
      int i => i,
      long l => (int)l,
      string s when int.TryParse(s, out var x) => x,
      _ => int.TryParse(raw.ToString(), out var p) ? p : 0
    };
    _navigationCardId = cardId > 0 ? cardId : 0;
    if (_navigationCardId > 0)
      _ = EnsureViewModelCardAsync();
  }

  /// <summary>Загружает карточку: при активном переводе всегда показывает ту книгу, для которой идёт задача.</summary>
  async Task EnsureViewModelCardAsync()
  {
    int activeId = BookTranslationBackgroundCoordinator.ActiveTranslationCardId;
    int cardIdToLoad = activeId > 0 ? activeId : _navigationCardId;
    if (cardIdToLoad <= 0)
    {
      await MainThread.InvokeOnMainThreadAsync(() =>
      {
        BookTranslationBackgroundCoordinator.SyncViewModel(_vm);
        _vm.OnExternalProgressRefreshRequested();
      }).ConfigureAwait(false);
      return;
    }

    try
    {
      var card = await _db.GetCardByIdAsync(cardIdToLoad).ConfigureAwait(false);
      await MainThread.InvokeOnMainThreadAsync(async () =>
      {
        if (card == null)
        {
          await ThemedOverlayPresenter.ShowAlertAsync(this, Strings.Common_Error, Strings.Alert_BookNotFound, Strings.Common_OK).ConfigureAwait(true);
          await Navigation.PopAsync().ConfigureAwait(true);
          return;
        }

        await _vm.ApplyCardAsync(card, _db).ConfigureAwait(true);
        BookTranslationBackgroundCoordinator.SyncViewModel(_vm);
        _vm.OnExternalProgressRefreshRequested();
      }).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[TranslationPage] EnsureViewModelCardAsync: {ex.Message}");
    }
  }

  /// <summary>Закрывает страницу, обновляя каталог главной перед возвратом.</summary>
  async void OnBackClicked(object sender, EventArgs e)
  {
    if (MainPageViewModel.Instance != null)
      await MainPageViewModel.Instance.RefreshBooksAsync().ConfigureAwait(true);
    await Navigation.PopAsync().ConfigureAwait(true);
  }

  /// <summary>Отправляет на прокси старт задачи перевода книги или показывает причину отказа (сервер, файл, активная задача).</summary>
  async void OnStartTranslationClicked(object sender, EventArgs e)
  {
    if (!_vm.IsLanguageSelected)
      return;

    if (BookTranslationBackgroundCoordinator.HasActiveTranslation)
    {
      int active = BookTranslationBackgroundCoordinator.ActiveTranslationCardId;
      if (active > 0 && active != _vm.CardId)
      {
        await ThemedOverlayPresenter.ShowAlertAsync(this, Strings.TranslationTitle, Strings.TranslationActiveJobRunningMessage, Strings.Common_OK)
            .ConfigureAwait(true);
        _ = EnsureViewModelCardAsync();
        return;
      }

      if (active > 0 && active == _vm.CardId)
        return;
    }

    if (string.IsNullOrWhiteSpace(TranslationServerConfig.ApiBaseUrl))
    {
      ServiceLocator.Get<IAppNotificationService>()?.Show(
          TranslationMessages.TranslationServerNotConfigured,
          AppNotificationSeverity.Error,
          TimeSpan.FromSeconds(8));
      return;
    }

    if (string.IsNullOrEmpty(_vm.SourceBookFilePath) || !File.Exists(_vm.SourceBookFilePath))
    {
      await ThemedOverlayPresenter.ShowAlertAsync(this, Strings.TranslationTitle, Strings.Alert_LocalBookFileMissing, Strings.Common_OK)
          .ConfigureAwait(true);
      return;
    }

    var targetIso = _vm.GetTargetIso();
    if (string.IsNullOrEmpty(targetIso))
      return;

    try
    {
      using var startCts = new CancellationTokenSource();
      var startResult = await _api
          .StartBookTranslationAsync(_vm.SourceBookFilePath, _vm.SourceIso, targetIso, startCts.Token)
          .ConfigureAwait(true);
      if (!startResult.Success || string.IsNullOrEmpty(startResult.JobId))
      {
        var detail = string.IsNullOrWhiteSpace(startResult.ErrorMessage)
            ? TranslationMessages.TranslationProxyUnreachable
            : startResult.ErrorMessage;
        ServiceLocator.Get<IAppNotificationService>()?.Show(
            detail,
            AppNotificationSeverity.Error,
            TimeSpan.FromSeconds(10));
        return;
      }

      var accepted = BookTranslationBackgroundCoordinator.BeginPollingAfterStart(_vm, startResult.JobId);
      if (!accepted)
      {
        await ThemedOverlayPresenter.ShowAlertAsync(this, Strings.TranslationTitle, Strings.TranslationActiveJobRunningMessage, Strings.Common_OK)
            .ConfigureAwait(true);
        _ = EnsureViewModelCardAsync();
        return;
      }

      _vm.IsTranslating = true;
      _vm.TranslationProgress = 0;
      _vm.OnExternalProgressRefreshRequested();
    }
    catch (Exception ex)
    {
      _vm.IsTranslating = false;
      System.Diagnostics.Debug.WriteLine($"[TranslationPage] OnStartTranslationClicked: {ex}");
      ServiceLocator.Get<IAppNotificationService>()?.Show(
          TranslationMessages.TranslationProxyUnreachable,
          AppNotificationSeverity.Error,
          TimeSpan.FromSeconds(10));
    }
  }

  /// <summary>Запрашивает отмену задачи перевода у фонового координатора и обновляет отображение.</summary>
  void OnCancelTranslationClicked(object sender, EventArgs e)
  {
    BookTranslationBackgroundCoordinator.RequestUserCancel();
    _vm.IsTranslating = false;
    _vm.TranslationProgress = 0;
    _ = EnsureViewModelCardAsync();
  }
}
