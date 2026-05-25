using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BookReaderApp.Helpers;
using BookReaderApp.Localization;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using BookReaderApp.Services;
using BookReaderApp.ViewModels;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace BookReaderApp;

/// <summary>
/// Экран настроек отображения текста при чтении: масштаб шрифта книги, поля, цвета фона и текста,
/// выравнивание абзацев, вертикальная или горизонтальная прокрутка, язык цели для перевода предложения.
/// Сохраняет <see cref="TextSettings"/> в БД и пересчитывает оценку числа страниц карточек.
/// </summary>
public partial class TextSettingsPage : ContentPage
{
  private readonly IDatabaseService _db = new DatabaseService();
  private TextSettings _model = new();

  static readonly List<BookLanguage> TranslationPickLanguages =
      new() { BookLanguage.Russian, BookLanguage.English };

  PropertyChangedEventHandler? _localizationChangedHandler;

  private static readonly string[] PaletteHex =
  {
    "#000000", "#1a1a1a", "#333333", "#666666", "#888888",
    "#FFFFFF", "#F5F5F5", "#E8E8E8", "#D0D0D0",
    "#F4E4BC", "#FFF8E7", "#E8DCC8",
    "#1a1a2e", "#16213e", "#0f3460", "#533483", "#e94560",
    "#1e3a2f", "#2d5016", "#4a6741",
    "#2c1810", "#5c4033", "#8b4513",
    "#1a237e", "#311b92", "#004d40", "#bf360c", "#b71c1c",
    "#01579b", "#0277bd", "#00695c"
  };

  private bool _colorDialogOpen;
  private string _colorDialogTarget = ""; // "bg" | "fg"
  private Color _pendingColor;
  private string _savedHexForDialog = "";
  private Border? _selectedSwatch;

  /// <summary>Строит палитру цветов и подписывается на смену культуры для подписей выравнивания и прокрутки.</summary>
  public TextSettingsPage()
  {
    InitializeComponent();
    BuildPalette();

    _localizationChangedHandler = (_, e) =>
    {
      if (!string.IsNullOrEmpty(e.PropertyName) &&
          e.PropertyName != nameof(LocalizationResourceManager.CurrentCulture))
        return;
      MainThread.BeginInvokeOnMainThread(() =>
      {
        UpdateTranslationLanguageDisplay();
        RefreshAlignmentScrollingPickers();
      });
    };
    LocalizationResourceManager.Instance.PropertyChanged += _localizationChangedHandler;
    Unloaded += (_, _) =>
    {
      if (_localizationChangedHandler != null)
        LocalizationResourceManager.Instance.PropertyChanged -= _localizationChangedHandler;
    };
  }

  /// <summary>Обновляет подпись выбранного языка перевода строк по <see cref="TextSettings.TranslationLanguage"/>.</summary>
  void UpdateTranslationLanguageDisplay()
  {
    var lang = BookLanguageStorage.FromStored(_model.TranslationLanguage);
    TranslationLanguageValueLabel.Text = lang == BookLanguage.None
        ? Strings.SelectLanguageTitle
        : LocalizedEnumHelper.GetBookLanguageString(lang);
  }

  /// <summary>Возвращает локализованную строку для ключа сохранённого выравнивания.</summary>
  static string AlignmentDisplayForKey(string key) =>
      key switch
      {
        TextSettingsStoredFormats.AlignmentStart => Strings.TextSettings_Align_Start,
        TextSettingsStoredFormats.AlignmentCenter => Strings.TextSettings_Align_Center,
        TextSettingsStoredFormats.AlignmentEnd => Strings.TextSettings_Align_End,
        _ => Strings.TextSettings_Align_Justify
      };

  /// <summary>Возвращает локализованную строку для ключа режима прокрутки.</summary>
  static string ScrollingDisplayForKey(string key) =>
      key == TextSettingsStoredFormats.ScrollingHorizontal
          ? Strings.TextSettings_Scroll_Horizontal
          : Strings.TextSettings_Scroll_Vertical;

  /// <summary>Синхронизирует подписи строк «выравнивание» и «прокрутка» с текущей моделью.</summary>
  void RefreshAlignmentScrollingPickers()
  {
    var alignKey = TextSettingsStoredFormats.NormalizeAlignment(_model.TextAlignment);
    if (AlignmentValueLabel != null)
      AlignmentValueLabel.Text = AlignmentDisplayForKey(alignKey);

    var scrollKey = TextSettingsStoredFormats.NormalizeScrolling(_model.ScrollingMode);
    if (ScrollingValueLabel != null)
      ScrollingValueLabel.Text = ScrollingDisplayForKey(scrollKey);
  }

  /// <summary>Загружает настройки из БД, при необходимости мигрирует устаревшие значения выравнивания/прокрутки и обновляет UI.</summary>
  protected override async void OnAppearing()
  {
    base.OnAppearing();
    _model = await _db.GetTextSettingsAsync();
    var rawAlign = (_model.TextAlignment ?? "").Trim();
    var rawScroll = (_model.ScrollingMode ?? "").Trim();
    NormalizeModel(_model);
    var migratedLayout =
        !string.Equals(rawAlign, (_model.TextAlignment ?? "").Trim(), StringComparison.Ordinal) ||
        !string.Equals(rawScroll, (_model.ScrollingMode ?? "").Trim(), StringComparison.Ordinal);
    if (migratedLayout)
      await SaveAndRefreshAsync();

    FontSizeValueLabel.Text = $"{_model.FontSize} pt";
    MarginValueLabel.Text = $"{_model.Margins} px";
    SetPreviewColor(BgColorPreview, _model.BackgroundColor);
    SetPreviewColor(FgColorPreview, _model.TextColor);
    RefreshAlignmentScrollingPickers();
    UpdateTranslationLanguageDisplay();
    UpdateFontButtons();
    UpdateMarginButtons();
  }

  /// <summary>Приводит поля модели к поддерживаемым значениям (ISO языка перевода строк и т.д.).</summary>
  private static void NormalizeModel(TextSettings s)
  {
    s.TextAlignment = TextSettingsStoredFormats.NormalizeAlignment(s.TextAlignment);
    s.ScrollingMode = TextSettingsStoredFormats.NormalizeScrolling(s.ScrollingMode);
    if (string.IsNullOrWhiteSpace(s.TranslationLanguage))
      s.TranslationLanguage = "ru";
    else
    {
      var iso = BookLanguageStorage.NormalizeToIso(s.TranslationLanguage);
      s.TranslationLanguage = string.IsNullOrEmpty(iso) ? "ru" : iso;
    }
  }

  /// <summary>Строит сетку выбора цвета (<see cref="PaletteHex"/>) во flex-контейнере оверлея.</summary>
  private void BuildPalette()
  {
    ColorPaletteFlex.Children.Clear();
    foreach (var hex in PaletteHex)
    {
      var c = TextReadingLayout.ParseColorHex(hex, Colors.Gray);
      var border = new Border
      {
        WidthRequest = 40,
        HeightRequest = 40,
        BackgroundColor = c,
        StrokeThickness = 2,
        Stroke = Colors.Transparent,
        StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(6) },
        Margin = new Thickness(0, 0, 8, 8)
      };
      var tap = new TapGestureRecognizer();
      tap.Tapped += (_, _) => OnPaletteSwatchTapped(border, c);
      border.GestureRecognizers.Add(tap);
      ColorPaletteFlex.Children.Add(border);
    }
  }

  /// <summary>Помечает выбранный образец палитры и запоминает цвет как кандидат для подтверждения.</summary>
  private void OnPaletteSwatchTapped(Border border, Color c)
  {
    if (_selectedSwatch != null)
    {
      _selectedSwatch.Stroke = Colors.Transparent;
      _selectedSwatch = null;
    }

    _pendingColor = c;
    _selectedSwatch = border;
    border.Stroke = Colors.DeepPink;
  }

  /// <summary>Заливает образец-квадрат цветом по hex строки модели.</summary>
  private static void SetPreviewColor(Border? b, string? hex)
  {
    if (b == null) return;
    var c = TextReadingLayout.ParseColorHex(hex, Colors.White);
    b.BackgroundColor = c;
  }

  /// <summary>Включает или приглушает кнопки шага размера шрифта по границам допустимого диапазона.</summary>
  private void UpdateFontButtons()
  {
    FontDecreaseBtn.IsEnabled = _model.FontSize > TextReadingLayout.MinFontSize;
    FontIncreaseBtn.IsEnabled = _model.FontSize < TextReadingLayout.MaxFontSize;
    FontDecreaseBtn.Opacity = FontDecreaseBtn.IsEnabled ? 1 : 0.35;
    FontIncreaseBtn.Opacity = FontIncreaseBtn.IsEnabled ? 1 : 0.35;
  }

  /// <summary>Включает или приглушает кнопки шага полей по допустимому диапазону.</summary>
  private void UpdateMarginButtons()
  {
    MarginDecreaseBtn.IsEnabled = _model.Margins > TextReadingLayout.MinMargins;
    MarginIncreaseBtn.IsEnabled = _model.Margins < TextReadingLayout.MaxMargins;
    MarginDecreaseBtn.Opacity = MarginDecreaseBtn.IsEnabled ? 1 : 0.35;
    MarginIncreaseBtn.Opacity = MarginIncreaseBtn.IsEnabled ? 1 : 0.35;
  }

  /// <summary>Сохраняет настройки текста и обновляет оценочное число страниц у всех карточек каталога.</summary>
  private async Task SaveAndRefreshAsync()
  {
    await _db.SaveTextSettingsAsync(_model);
    // Карточки: вертикаль — stride из настроек; горизонтальное листание — оценка по viewport (без WebView). Открытая книга дополняет фактом из DOM при Persist.
    await _db.RecalculateAllCardsEstimatedPageCountAsync();
    if (MainPageViewModel.Instance != null)
      await MainPageViewModel.Instance.RefreshBooksAsync();
  }

  /// <summary>Обработчик кнопки уменьшения размера шрифта книги.</summary>
  private async void OnFontDecreaseClicked(object sender, EventArgs e)
  {
    if (_model.FontSize <= TextReadingLayout.MinFontSize) return;
    _model.FontSize--;
    FontSizeValueLabel.Text = $"{_model.FontSize} pt";
    UpdateFontButtons();
    await SaveAndRefreshAsync();
  }

  /// <summary>Обработчик кнопки увеличения размера шрифта книги.</summary>
  private async void OnFontIncreaseClicked(object sender, EventArgs e)
  {
    if (_model.FontSize >= TextReadingLayout.MaxFontSize) return;
    _model.FontSize++;
    FontSizeValueLabel.Text = $"{_model.FontSize} pt";
    UpdateFontButtons();
    await SaveAndRefreshAsync();
  }

  /// <summary>Обработчик кнопки уменьшения полей текста книги.</summary>
  private async void OnMarginDecreaseClicked(object sender, EventArgs e)
  {
    if (_model.Margins <= TextReadingLayout.MinMargins) return;
    _model.Margins -= TextReadingLayout.MarginStep;
    if (_model.Margins < TextReadingLayout.MinMargins)
      _model.Margins = TextReadingLayout.MinMargins;
    MarginValueLabel.Text = $"{_model.Margins} px";
    UpdateMarginButtons();
    await SaveAndRefreshAsync();
  }

  /// <summary>Обработчик кнопки увеличения полей текста книги.</summary>
  private async void OnMarginIncreaseClicked(object sender, EventArgs e)
  {
    if (_model.Margins >= TextReadingLayout.MaxMargins) return;
    _model.Margins += TextReadingLayout.MarginStep;
    if (_model.Margins > TextReadingLayout.MaxMargins)
      _model.Margins = TextReadingLayout.MaxMargins;
    MarginValueLabel.Text = $"{_model.Margins} px";
    UpdateMarginButtons();
    await SaveAndRefreshAsync();
  }

  /// <summary>Открывает диалог выбора цвета для фона чтения.</summary>
  private void OnBgColorTapped(object sender, EventArgs e) => OpenColorDialog("bg");

  /// <summary>Открывает диалог выбора цвета для текста чтения.</summary>
  private void OnFgColorTapped(object sender, EventArgs e) => OpenColorDialog("fg");

  /// <summary>Показывает оверлей палитры для фона (<c>bg</c>) или текста (<c>fg</c>).</summary>
  private void OpenColorDialog(string target)
  {
    _colorDialogTarget = target;
    _colorDialogOpen = true;
    ColorDialogTitle.Text = target == "bg" ? Strings.TextSettings_BgColor : Strings.TextSettings_FgColor;
    var hex = target == "bg" ? _model.BackgroundColor : _model.TextColor;
    _savedHexForDialog = hex ?? (target == "bg" ? "#FFFFFF" : "#000000");
    _pendingColor = TextReadingLayout.ParseColorHex(_savedHexForDialog, Colors.Gray);
    if (_selectedSwatch != null)
    {
      _selectedSwatch.Stroke = Colors.Transparent;
      _selectedSwatch = null;
    }

    foreach (var child in ColorPaletteFlex.Children)
    {
      if (child is Border b && b.BackgroundColor != null)
      {
        var bh = TextReadingLayout.ColorToHexRgb(b.BackgroundColor);
        var ph = TextReadingLayout.ColorToHexRgb(_pendingColor);
        if (string.Equals(NormalizeHex(bh), NormalizeHex(ph), StringComparison.OrdinalIgnoreCase))
        {
          b.Stroke = Colors.DeepPink;
          _selectedSwatch = b;
          break;
        }
      }
    }

    ColorOverlay.IsVisible = true;
  }

  /// <summary>Нормализует строку RGB для сравнения образцов (без <c>#</c>, первые шесть hex).</summary>
  private static string NormalizeHex(string h)
  {
    h = h.Trim();
    if (h.StartsWith('#')) h = h[1..];
    return h.Length >= 6 ? h[..6] : h;
  }

  /// <summary>Снимает палитру по тапу вне модального блока без сохранения.</summary>
  private void OnColorOverlayBackdropTapped(object sender, TappedEventArgs e)
  {
    CloseColorDialog(save: false);
  }

  /// <summary>Поглощает тап по карточке диалога, чтобы не закрывать палитру.</summary>
  private void OnColorDialogBubbleTapped(object sender, TappedEventArgs e)
  {
    // intentional no-op
  }

  /// <summary>Отменяет выбор цвета в палитре.</summary>
  private void OnColorDialogCancelClicked(object sender, EventArgs e)
  {
    CloseColorDialog(save: false);
  }

  /// <summary>Применяет выбранный в палитре цвет фона или текста и сохраняет модель.</summary>
  private async void OnColorDialogOkClicked(object sender, EventArgs e)
  {
    await CloseColorDialogAsync(save: true);
  }

  /// <summary>Запускает закрытие диалога цвета (синхронная обёртка над задачей).</summary>
  private void CloseColorDialog(bool save)
  {
    _ = CloseColorDialogAsync(save);
  }

  /// <summary>Восстанавливает или фиксирует превью и скрывает оверлей палитры.</summary>
  private async Task CloseColorDialogAsync(bool save)
  {
    if (!_colorDialogOpen) return;
    if (!save)
    {
      if (_colorDialogTarget == "bg")
        SetPreviewColor(BgColorPreview, _savedHexForDialog);
      else if (_colorDialogTarget == "fg")
        SetPreviewColor(FgColorPreview, _savedHexForDialog);
    }
    else
    {
      var hex = TextReadingLayout.ColorToHexRgb(_pendingColor);
      if (_colorDialogTarget == "bg")
      {
        _model.BackgroundColor = hex;
        SetPreviewColor(BgColorPreview, hex);
      }
      else if (_colorDialogTarget == "fg")
      {
        _model.TextColor = hex;
        SetPreviewColor(FgColorPreview, hex);
      }

      await SaveAndRefreshAsync();
    }

    _colorDialogOpen = false;
    ColorOverlay.IsVisible = false;
    if (_selectedSwatch != null)
    {
      _selectedSwatch.Stroke = Colors.Transparent;
      _selectedSwatch = null;
    }

    _colorDialogTarget = "";
  }

  /// <summary>Показывает action sheet для выбора режима выравнивания абзацев книги.</summary>
  async void OnAlignmentPickTapped(object sender, EventArgs e)
  {
    var keys = TextSettingsStoredFormats.AlignmentKeysOrdered;
    var labels = keys.Select(AlignmentDisplayForKey).ToArray();
    string? pick = await ThemedOverlayPresenter.ShowActionSheetAsync(
        this,
        Strings.TextSettings_Picker_Alignment,
        Strings.Common_Cancel,
        labels).ConfigureAwait(true);
    if (pick == null)
      return;
    int idx = Array.IndexOf(labels, pick);
    if (idx < 0 || idx >= keys.Length)
      return;
    _model.TextAlignment = keys[idx];
    RefreshAlignmentScrollingPickers();
    await SaveAndRefreshAsync().ConfigureAwait(true);
  }

  /// <summary>Показывает action sheet для выбора вертикальной или горизонтальной прокрутки.</summary>
  async void OnScrollingPickTapped(object sender, EventArgs e)
  {
    var keys = TextSettingsStoredFormats.ScrollingKeysOrdered;
    var labels = keys.Select(ScrollingDisplayForKey).ToArray();
    string? pick = await ThemedOverlayPresenter.ShowActionSheetAsync(
        this,
        Strings.TextSettings_Picker_Scrolling,
        Strings.Common_Cancel,
        labels).ConfigureAwait(true);
    if (pick == null)
      return;
    int idx = Array.IndexOf(labels, pick);
    if (idx < 0 || idx >= keys.Length)
      return;
    _model.ScrollingMode = keys[idx];
    RefreshAlignmentScrollingPickers();
    await SaveAndRefreshAsync().ConfigureAwait(true);
  }

  /// <summary>Позволяет выбрать язык перевода строки для сценария перевода предложений.</summary>
  async void OnTranslationLanguageTapped(object sender, EventArgs e)
  {
    await ThemedEnumPickSheet.PickAsync(
        this,
        TranslationPickLanguages,
        LocalizedEnumHelper.GetBookLanguageString,
        lang =>
        {
          _model.TranslationLanguage = BookLanguageStorage.ToStored(lang);
          UpdateTranslationLanguageDisplay();
          _ = SaveAndRefreshAsync();
        },
        Strings.SelectLanguageTitle).ConfigureAwait(true);
  }

  /// <summary>Возврат из экрана к читалке или родительской навигации.</summary>
  private async void OnBackClicked(object sender, EventArgs e)
  {
    await Navigation.PopAsync();
  }
}
