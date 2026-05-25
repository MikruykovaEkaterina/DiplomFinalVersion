using BookReaderApp.Models;
using BookReaderApp.Resources;
using BookReaderApp.Services;
using BookReaderApp.ViewModels;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

namespace BookReaderApp;

/// <summary>
/// Главный экран приложения: библиотека книг, поиск по каталогу, переход к загрузке, настройкам, чтению и переводу карточки.
/// </summary>
public partial class MainPage : ContentPage
{
  static bool _launchSplashDismissed;

  /// <summary>Инициализирует разметку, заставку первого запуска и колбэки панели поиска (<see cref="Views.SearchBarView"/>).</summary>
  public MainPage()
  {
    InitializeComponent();
    if (LaunchSplashOverlay != null)
    {
      LaunchSplashOverlay.IsVisible = !_launchSplashDismissed;
      LaunchSplashOverlay.InputTransparent = _launchSplashDismissed;
    }

    InterfaceThemeManager.ResolvedThemeChanged += () => ApplyMainSystemChrome();

    SearchBar.SearchApplied += (_, _) =>
    {
      if (BindingContext is not MainPageViewModel vm)
        return;
      vm.SearchFilter.CommitAppliedFilters();
      vm.RebuildVisibleBookGroups();
      var notify = ServiceLocator.Get<IAppNotificationService>();
      int n = vm.BookGroups.Count;
      if (n == 0)
        notify?.Show(Strings.Main_SearchNoBooksFound, AppNotificationSeverity.Warning);
      else
        notify?.Show(Strings.Main_SearchApplied, AppNotificationSeverity.Success);
    };
    SearchBar.SearchReset += (_, _) =>
    {
      if (BindingContext is not MainPageViewModel vm)
        return;
      vm.RebuildVisibleBookGroups();
      ServiceLocator.Get<IAppNotificationService>()
          ?.Show(Strings.Main_SearchResetDone, AppNotificationSeverity.Success);
    };
  }

  /// <summary>Отменяет несохранённые фильтры поиска на главной перед уходом со страницы.</summary>
  protected override void OnDisappearing()
  {
    if (BindingContext is MainPageViewModel vm)
      vm.SearchFilter.RevertUncommittedDraft();
    base.OnDisappearing();
  }

  /// <summary>При входе восстанавливает системный вид (Android), обновляет список книг, заставку, уведомления и при необходимости прокручивает каталог наверх.</summary>
  protected override async void OnAppearing()
  {
    base.OnAppearing();
    ApplyMainSystemChrome();

    bool showSplash = !_launchSplashDismissed;
    if (showSplash && LaunchSplashOverlay != null && LaunchSplashContent != null)
    {
      LaunchSplashOverlay.IsVisible = true;
      LaunchSplashOverlay.Opacity = 1;
      LaunchSplashOverlay.InputTransparent = false;
      LaunchSplashContent.Opacity = 0;
      _ = LaunchSplashContent.FadeTo(1, 320, Easing.CubicOut);
    }

    if (BindingContext is MainPageViewModel vm)
      await vm.RefreshBooksAsync();

    ServiceLocator.Get<IAppNotificationService>()?.TryReplayMissedTranslationCompleteBanner();

    SearchBar.RefreshSearchChrome();

    if (showSplash && LaunchSplashOverlay != null && LaunchSplashContent != null)
    {
      await Task.Delay(100).ConfigureAwait(true);
      await LaunchSplashOverlay.FadeTo(0, 340, Easing.CubicInOut).ConfigureAwait(true);
      LaunchSplashOverlay.IsVisible = false;
      LaunchSplashOverlay.Opacity = 1;
      LaunchSplashContent.Opacity = 1;
      LaunchSplashOverlay.InputTransparent = true;
      _launchSplashDismissed = true;
    }

    if (Preferences.Get("ScrollMainToTopRequested", false))
    {
      Preferences.Set("ScrollMainToTopRequested", false);
      if (BindingContext is MainPageViewModel mainVm && mainVm.BookGroups.Count > 0)
        MainBooksCollection.ScrollTo(mainVm.BookGroups[0], position: ScrollToPosition.Start, animate: false);
    }
  }

  /// <summary>Восстанавливает цвета системных баров под текущую светлую тему приложения (только Android).</summary>
  static void ApplyMainSystemChrome()
  {
#if ANDROID
    Color pb = ResolveUiColor("PrimaryBackground", Colors.White);
    Color mt = ResolveUiColor("MainTextColor", Colors.Black);
    ReadingSystemChrome.RestoreAppChrome(pb, mt);
#endif
  }

  /// <summary>Возвращает цвет из <see cref="Application.Resources"/> по ключу или запасное значение.</summary>
  static Color ResolveUiColor(string key, Color fallback)
  {
    if (Application.Current?.Resources.TryGetValue(key, out var o) == true && o is Color c)
      return c;
    return fallback;
  }

  /// <summary>Открывает экран загрузки книги (<see cref="UploadPage"/>).</summary>
  private async void OnUploadClicked(object sender, EventArgs e) =>
      await Shell.Current.GoToAsync(nameof(UploadPage));

  /// <summary>Открывает настройки приложения (<see cref="SettingsPage"/>).</summary>
  private async void OnSettingsClicked(object sender, EventArgs e) =>
      await Shell.Current.GoToAsync(nameof(SettingsPage));

  /// <summary>Открывает <see cref="ReadingPage"/> для выбранной карточки по идентификатору.</summary>
  private async void OnBookCardTapped(object sender, int cardId)
  {
    try
    {
      var databaseService = new DatabaseService();
      var card = await databaseService.GetCardByIdAsync(cardId);

      if (card == null)
      {
        await ThemedOverlayPresenter.ShowAlertAsync(
            this,
            Strings.Reading_Title,
            Strings.Main_BookNotFoundMaybeRemoved,
            Strings.Common_OK);
        if (BindingContext is MainPageViewModel vm)
          await vm.RefreshBooksAsync();
        return;
      }

      var readingPage = new ReadingPage();
      readingPage.SetBookData(card);
      await Navigation.PushAsync(readingPage);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[MainPage] Ошибка открытия книги: {ex.Message}");
    }
  }

  /// <summary>Открывает экран перевода книги (<see cref="TranslationPage"/>) для указанной карточки.</summary>
  private async void OnCardTranslateRequested(object sender, int cardId)
  {
    try
    {
      await Shell.Current.GoToAsync($"{nameof(TranslationPage)}?CardId={cardId}");
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[MainPage] OnCardTranslateRequested: {ex.Message}");
    }
  }

  /// <summary>Удаляет карточку из базы после проверки (нет активного перевода по произведению).</summary>
  private async void OnCardDeleteRequested(object sender, int cardId)
  {
    var notify = ServiceLocator.Get<IAppNotificationService>();
    try
    {
      var db = new DatabaseService();
      var card = await db.GetCardByIdAsync(cardId);
      if (card == null)
      {
        notify?.Show(
            Strings.Alert_BookNotFound,
            AppNotificationSeverity.Warning,
            TimeSpan.FromSeconds(6));
        return;
      }

      if (BookTranslationBackgroundCoordinator.IsTranslationInProgressForWork(card.WorkId))
      {
        notify?.Show(
            Strings.Main_DeleteBlockedTranslationRunning,
            AppNotificationSeverity.Warning,
            TimeSpan.FromSeconds(10));
        return;
      }

      bool ok = await db.TryDeleteCardAsync(cardId);
      if (!ok)
      {
        notify?.Show(
            Strings.Main_DeleteFailed,
            AppNotificationSeverity.Error,
            TimeSpan.FromSeconds(7));
        return;
      }

      if (BindingContext is MainPageViewModel vm)
        await vm.RefreshBooksAsync();

      notify?.Show(
          Strings.Main_CardDeletedSuccess,
          AppNotificationSeverity.Success,
          TimeSpan.FromSeconds(5));
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[MainPage] OnCardDeleteRequested: {ex.Message}");
      notify?.Show(
          Strings.Main_DeleteFailed,
          AppNotificationSeverity.Error,
          TimeSpan.FromSeconds(7));
    }
  }

  /// <summary>Восстанавливает последнюю сохранённую позицию чтения и открывает соответствующую книгу.</summary>
  private async void OnContinueLastBookClicked(object sender, EventArgs e)
  {
    try
    {
      var db = new DatabaseService();
      var allCards = await db.GetAllCardsAsync();
      if (allCards == null || allCards.Count == 0)
      {
        await ThemedOverlayPresenter.ShowAlertAsync(
            this,
            Strings.Reading_Title,
            Strings.Main_LibraryEmptyHint,
            Strings.Common_OK);
        return;
      }

      var pos = await db.GetLatestReadingPositionAsync();
      if (pos == null)
      {
        await ThemedOverlayPresenter.ShowAlertAsync(
            this,
            Strings.Reading_Title,
            Strings.Main_NoSavedReadingHint,
            Strings.Common_OK);
        return;
      }

      var card = await db.GetCardByIdAsync(pos.CardId);
      if (card == null)
      {
        await ThemedOverlayPresenter.ShowAlertAsync(
            this,
            Strings.Reading_Title,
            Strings.Alert_LastBookMissing,
            Strings.Common_OK);
        return;
      }

      if (string.IsNullOrWhiteSpace(card.FilePath))
      {
        await ThemedOverlayPresenter.ShowAlertAsync(
            this,
            Strings.Reading_Title,
            Strings.Main_FileMissingForSavedPosition,
            Strings.Common_OK);
        return;
      }

      var readingPage = new ReadingPage();
      readingPage.SetBookData(card);
      await Navigation.PushAsync(readingPage);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[MainPage] OnContinueLastBookClicked: {ex.Message}");
      await ThemedOverlayPresenter.ShowAlertAsync(this, Strings.Reading_Title, Strings.Alert_OpenBookFailed, Strings.Common_OK);
    }
  }
}
