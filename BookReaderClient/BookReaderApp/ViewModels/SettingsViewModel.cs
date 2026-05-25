using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BookReaderApp.Localization;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using BookReaderApp.Services;

namespace BookReaderApp.ViewModels;

/// <summary>
/// Настройки интерфейса приложения: тема, шаг размера шрифта UI, язык строк;
/// загрузка и сохранение <see cref="InterfaceSettings"/> в БД с применением к текущей сессии.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
  readonly IDatabaseService _db;

  InterfaceTheme _theme = InterfaceTheme.Light;
  int _fontSize = InterfacePreferenceCoordinator.InterfaceFontSizeNormal;
  string _languageCode = "ru";

  /// <summary>Подписывается на локализацию и сохраняет сервис БД.</summary>
  public SettingsViewModel(IDatabaseService databaseService)
  {
    _db = databaseService;
    LocalizationResourceManager.Instance.PropertyChanged += OnLocalizationChanged;
  }

  /// <summary>При смене культуры обновляет текстовые сводки для экрана.</summary>
  void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) =>
      RaiseDisplayStrings();

  /// <summary>Локализованное название текущей темы.</summary>
  public string ThemeDisplayText => ThemeToDisplay(_theme);

  /// <summary>Строка «размер шрифта: малый/средний/крупный (N pt)».</summary>
  public string FontSummaryText => string.Format(Strings.Interface_FontSize_Format, FontBandLabel(_fontSize), _fontSize);

  /// <summary>Отображаемое имя выбранного языка интерфейса.</summary>
  public string LanguageSummaryText => LanguageToDisplay(_languageCode);

  /// <summary>Можно уменьшить шрифт UI (не на нижней границе).</summary>
  public bool CanDecreaseFont => _fontSize > InterfacePreferenceCoordinator.InterfaceFontSizeMin;

  /// <summary>Можно увеличить шрифт UI (не на верхней границе).</summary>
  public bool CanIncreaseFont => _fontSize < InterfacePreferenceCoordinator.InterfaceFontSizeMax;

  public event PropertyChangedEventHandler? PropertyChanged;

  /// <summary>Читает настройки из БД и обновляет привязки.</summary>
  public async Task LoadAsync()
  {
    var s = await _db.GetInterfaceSettingsAsync().ConfigureAwait(true);
    _theme = InterfaceThemeStored.Clamp(s.Theme);
    _fontSize = Math.Clamp(s.InterfaceFontSize,
        InterfacePreferenceCoordinator.InterfaceFontSizeMin,
        InterfacePreferenceCoordinator.InterfaceFontSizeMax);
    _languageCode = s.InterfaceLanguage switch
    {
      "en" => "en",
      _ => "ru"
    };
    RaiseDisplayStrings();
    RaiseFontCommands();
  }

  /// <summary>Локализованная подпись темы для списка.</summary>
  static string ThemeToDisplay(InterfaceTheme kind) =>
      kind switch
      {
        InterfaceTheme.Dark => Strings.Theme_Name_Dark,
        InterfaceTheme.Auto => Strings.Theme_Name_Auto,
        _ => Strings.Theme_Name_Light
      };

  /// <summary>Короткое имя языка для сводки.</summary>
  static string LanguageToDisplay(string code) =>
      code == "en" ? Strings.Interface_Language_Display_En : Strings.Interface_Language_Display_Ru;

  /// <summary>Категория размера шрифта (малый/средний/крупный).</summary>
  static string FontBandLabel(int size) =>
      size <= 15 ? Strings.Interface_FontSize_Small :
      size <= 17 ? Strings.Interface_FontSize_Medium :
      Strings.Interface_FontSize_Large;

  /// <summary>Переключает тему по кругу Light → Dark → Auto на <paramref name="delta"/> шагов (+1 вперёд, −1 назад).</summary>
  public void CycleTheme(int delta)
  {
    InterfaceTheme[] order = [InterfaceTheme.Light, InterfaceTheme.Dark, InterfaceTheme.Auto];
    int idx = Array.IndexOf(order, _theme);
    if (idx < 0)
      idx = 0;
    int len = order.Length;
    idx = ((idx + delta) % len + len) % len;
    _theme = order[idx];
    OnPropertyChanged(nameof(ThemeDisplayText));
    _ = PersistAsync();
  }

  /// <summary>Изменяет размер шрифта UI на один шаг в пределах допустимого диапазона.</summary>
  public void ChangeFont(int delta)
  {
    int n = Math.Clamp(_fontSize + delta,
        InterfacePreferenceCoordinator.InterfaceFontSizeMin,
        InterfacePreferenceCoordinator.InterfaceFontSizeMax);
    if (n == _fontSize)
      return;
    _fontSize = n;
    OnPropertyChanged(nameof(FontSummaryText));
    RaiseFontCommands();
    _ = PersistAsync();
  }

  /// <summary>Устанавливает язык интерфейса по коду «en» или «ru» и сохраняет.</summary>
  public async Task PickLanguageAsync(string code)
  {
    _languageCode = code == "en" ? "en" : "ru";
    OnPropertyChanged(nameof(LanguageSummaryText));
    await PersistAsync().ConfigureAwait(true);
  }

  /// <summary>Пишет настройки в БД и применяет культуру, тему и динамические размеры шрифтов.</summary>
  async Task PersistAsync()
  {
    var s = await _db.GetInterfaceSettingsAsync().ConfigureAwait(true);
    s.Theme = _theme;
    s.InterfaceFontSize = _fontSize;
    s.InterfaceLanguage = _languageCode;
    await _db.SaveInterfaceSettingsAsync(s).ConfigureAwait(true);

    InterfacePreferenceCoordinator.ApplyCulture(_languageCode);
    InterfaceThemeManager.ApplyThemePreference(_theme, DateTime.Now);
    InterfacePreferenceCoordinator.ApplyInterfaceFontSizes(_fontSize);
    Strings.Culture = LocalizationResourceManager.Instance.CurrentCulture;
    RaiseDisplayStrings();
  }

  /// <summary>Обновляет все текстовые сводки после смены культуры или настроек.</summary>
  void RaiseDisplayStrings()
  {
    OnPropertyChanged(nameof(ThemeDisplayText));
    OnPropertyChanged(nameof(FontSummaryText));
    OnPropertyChanged(nameof(LanguageSummaryText));
  }

  /// <summary>Пересчитывает доступность кнопок уменьшения/увеличения шрифта.</summary>
  void RaiseFontCommands()
  {
    OnPropertyChanged(nameof(CanDecreaseFont));
    OnPropertyChanged(nameof(CanIncreaseFont));
  }

  void OnPropertyChanged([CallerMemberName] string? name = null) =>
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

  /// <summary>Отписывается от менеджера локализации при закрытии страницы.</summary>
  public void Dispose() =>
      LocalizationResourceManager.Instance.PropertyChanged -= OnLocalizationChanged;
}
