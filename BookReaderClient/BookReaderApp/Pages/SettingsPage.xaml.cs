using BookReaderApp.Resources;
using BookReaderApp.Services;
using BookReaderApp.ViewModels;
using Microsoft.Maui.Controls;

namespace BookReaderApp;

/// <summary>
/// Настройки приложения: тема оформления, базовый размер шрифта интерфейса, язык UI. Модель <see cref="SettingsViewModel"/> пересоздаётся при каждом появлении страницы.
/// </summary>
public partial class SettingsPage : ContentPage
{
  SettingsViewModel? _vm;

  /// <summary>Инициализирует разметку страницы.</summary>
  public SettingsPage()
  {
    InitializeComponent();
  }

  /// <summary>Создаёт и загружает <see cref="SettingsViewModel"/> из зарегистрированного <see cref="IDatabaseService"/>.</summary>
  protected override async void OnAppearing()
  {
    base.OnAppearing();
    var db = ServiceLocator.Get<IDatabaseService>();
    if (db == null)
      return;
    _vm?.Dispose();
    _vm = new SettingsViewModel(db);
    BindingContext = _vm;
    await _vm.LoadAsync();
  }

  /// <summary>Освобождает VM и снимает контекст при закрытии страницы.</summary>
  protected override void OnDisappearing()
  {
    base.OnDisappearing();
    _vm?.Dispose();
    _vm = null;
    BindingContext = null;
  }

  /// <summary>Возврат к предыдущему экрану по стеку навигации.</summary>
  async void OnBackClicked(object sender, EventArgs e) =>
      await Navigation.PopAsync();

  /// <summary>Возврат к корню навигации (главный экран).</summary>
  async void OnHomeClicked(object sender, EventArgs e) =>
      await Navigation.PopToRootAsync();

  /// <summary>Переключает тему на предыдущую в списке вариантов.</summary>
  void OnThemePreviousClicked(object sender, EventArgs e) =>
      _vm?.CycleTheme(-1);

  /// <summary>Переключает тему на следующую в списке вариантов.</summary>
  void OnThemeNextClicked(object sender, EventArgs e) =>
      _vm?.CycleTheme(1);

  /// <summary>Уменьшает базовый размер шрифта интерфейса.</summary>
  void OnDecreaseFontClicked(object sender, EventArgs e) =>
      _vm?.ChangeFont(-1);

  /// <summary>Увеличивает базовый размер шрифта интерфейса.</summary>
  void OnIncreaseFontClicked(object sender, EventArgs e) =>
      _vm?.ChangeFont(1);

  /// <summary>Показывает выбор языка интерфейса (русский/английский) через action sheet.</summary>
  async void OnLanguageRowTapped(object sender, TappedEventArgs e)
  {
    if (_vm == null)
      return;
    var ru = Strings.Interface_Language_Display_Ru;
    var en = Strings.Interface_Language_Display_En;
    var pick = await ThemedOverlayPresenter.ShowActionSheetAsync(
        this,
        Strings.Interface_Language_Title,
        Strings.Common_Cancel,
        ru,
        en);
    if (pick == ru)
      await _vm.PickLanguageAsync("ru");
    else if (pick == en)
      await _vm.PickLanguageAsync("en");
  }
}
