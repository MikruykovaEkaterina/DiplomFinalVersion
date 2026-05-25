using BookReaderApp.Helpers;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using BookReaderApp.Services;
using BookReaderApp.ViewModels;
using Microsoft.Maui.Controls;

namespace BookReaderApp
{
  /// <summary>
  /// Экран выбора файла книги, языка и фоновой загрузки в библиотеку (<see cref="BookUploadBackgroundCoordinator"/>).
  /// </summary>
  public partial class UploadPage : ContentPage
  {
    /// <summary>Создаёт страницу, привязывает <see cref="UploadViewModel"/> из контейнера DI.</summary>
    public UploadPage()
    {
      InitializeComponent();
      BindingContext = ServiceLocator.GetRequired<UploadViewModel>();
    }

    /// <summary>Синхронизирует UI с состоянием фонового загрузчика и подписывается на его события.</summary>
    protected override void OnAppearing()
    {
      base.OnAppearing();
      BookUploadBackgroundCoordinator.StateChanged += OnUploadCoordinatorStateChanged;
      BookUploadBackgroundCoordinator.UploadCompletedSuccessfully += OnUploadSucceededResetLanguage;
      if (BindingContext is UploadViewModel vm)
        BookUploadBackgroundCoordinator.SyncViewModel(vm);
    }

    /// <summary>Отписывается от событий координатора загрузки при уходе со страницы.</summary>
    protected override void OnDisappearing()
    {
      BookUploadBackgroundCoordinator.StateChanged -= OnUploadCoordinatorStateChanged;
      BookUploadBackgroundCoordinator.UploadCompletedSuccessfully -= OnUploadSucceededResetLanguage;
      base.OnDisappearing();
    }

    /// <summary>Сбрасывает выбранный язык в VM после успешной загрузки книги на сервер/в БД.</summary>
    void OnUploadSucceededResetLanguage()
    {
      MainThread.BeginInvokeOnMainThread(() =>
      {
        if (BindingContext is UploadViewModel vm)
          vm.SelectedLanguage = null;
      });
    }

    /// <summary>Обновляет привязанную модель из событий прогресса фонового загрузчика.</summary>
    void OnUploadCoordinatorStateChanged()
    {
      MainThread.BeginInvokeOnMainThread(() =>
      {
        if (BindingContext is UploadViewModel u)
          BookUploadBackgroundCoordinator.SyncViewModel(u);
      });
    }

    /// <summary>Возвращает на главную, предварительно обновляя список книг на <see cref="MainPage"/>.</summary>
    private async void OnBackClicked(object sender, EventArgs e)
    {
      if (MainPageViewModel.Instance != null)
        await MainPageViewModel.Instance.RefreshBooksAsync();
      await Navigation.PopAsync();
    }

    /// <summary>Открывает лист выбора языка книги для загрузки.</summary>
    async void OnUploadLanguageTapped(object sender, EventArgs e)
    {
      if (BindingContext is not UploadViewModel vm)
        return;
      await ThemedEnumPickSheet.PickAsync(
          this,
          vm.LanguageOptions,
          LocalizedEnumHelper.GetBookLanguageString,
          lang => vm.SelectedLanguage = lang,
          Strings.SelectLanguageTitle).ConfigureAwait(true);
    }

    /// <summary>Запускает выбор файла, разбор метаданных и постановку задачи фонового импорта.</summary>
    private async void OnUploadClicked(object sender, EventArgs e)
    {
      var notifications = ServiceLocator.Get<IAppNotificationService>();
      if (notifications == null)
        return;

      var vm = BindingContext as UploadViewModel;
      if (vm?.SelectedLanguage == null) return;

      var pickResult = await vm.PickBookAsync();

      if (pickResult.Cancelled)
      {
        notifications.ShowUploadCancelledOrFailed(cancelled: true);
        return;
      }

      if (!pickResult.Success)
      {
        if (pickResult.IsInvalidFormat)
          notifications.ShowUploadInvalidFormat();
        else
          notifications.ShowUploadCancelledOrFailed(cancelled: false, pickResult.ErrorMessage);
        return;
      }

      string filePath = pickResult.FilePath;

      var metadata = vm.ParseBook(filePath);
      if (metadata == null)
      {
        notifications.ShowUploadCancelledOrFailed(cancelled: false, Strings.Notification_Upload_ReadBookFileFailed);
        return;
      }

      var lang = vm.SelectedLanguage!.Value;
      BookUploadBackgroundCoordinator.BeginUpload(filePath, metadata, lang);
      BookUploadBackgroundCoordinator.SyncViewModel(vm);
    }
  }
}
