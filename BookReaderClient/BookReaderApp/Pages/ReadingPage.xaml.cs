using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using BookReaderApp.Helpers;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using BookReaderApp.Services;
using BookReaderApp.ViewModels;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
namespace BookReaderApp;

/// <summary>Элемент списка оглавления (текущая глава подсвечивается).</summary>
public sealed class TocChapterItem
{
  public string Title { get; init; } = "";
  public int ChapterIndex { get; init; }
  public Color RowBackground { get; init; } = Colors.Transparent;
}

/// <summary>
/// Экран чтения книги: загрузка FB2/EPUB, построение текста и оглавления,
/// режим вертикального текста через WebView и горизонтальное «листание столбцами» через тот же WebView,
/// сохранение позиции символа, заметки, перевод предложения по событиям из HTML, переключение панелей.
/// Совокупность приватных методов обслуживает вёрстку, пагинацию, оглавление и диалог перевода строки.
/// </summary>
public partial class ReadingPage : ContentPage
{
  /// <summary>Активная страница чтения — для сохранения позиции при уходе приложения в фон.</summary>
  private static ReadingPage? _activeReadingPage;

  private Card _currentBook;
  private readonly IDatabaseService _db = new DatabaseService();
  private CancellationTokenSource? _sentenceTranslateCts;
  private Work _currentWork;
  private ReadingPosition _currentReadingPosition;

  private string _fullText = "";
  private long _dbTotalChars = 0;

  /// <summary>Оценка символов на страницу из настроек (если нет размеров области чтения).</summary>
  private int _charsPerPage = 1800;
  private int _pageCount = 1;
  private int _currentPageIndex = 0;
  private long _currentPageStartOffset = 0;
  private long _currentPageTextLength = 0;
  private double _currentPageScrollRatio = 0;
  private bool _isRendering = false;
  private bool _panelsVisible = true;
  /// <summary>Режим из настроек текста (вертикальный скролл vs горизонтальное листание страниц). Не путать с поворотом физического устройства.</summary>
  private bool _isVerticalMode = true;
  private readonly List<(string Title, long Start, long End)> _chapters = new();
  private int _currentChapterIndex = 0;
  private readonly List<(long Start, long End)> _horizontalPages = new();
  private double _readingFontSize = 16;
  private Color _readingTextColor = Colors.Black;
  private Color _readingBgColor = Colors.White;
  private double _readingMarginPx = 16;
  private Microsoft.Maui.TextAlignment _paragraphTextAlignment = Microsoft.Maui.TextAlignment.Justify;
  private List<XElement>? _fb2OrderedSections;
  /// <summary>После смены карточки — открыть книгу сразу на сохранённой заметке.</summary>
  private Note? _pendingOpenNoteAfterLoad;
  /// <summary>Точная прокрутка WebView к символу плоского текста (после загрузки или при том же HTML).</summary>
  private long? _pendingWebScrollToBookOffset;
  /// <summary>Смещение в тексте на момент открытия диалога «новая заметка» (= сохранённый символ перехода и текста карточки).</summary>
  private long _noteEditorAnchorOffset;
  /// <summary>EPUB: HTML-фрагменты по главам (вертикальный WebView). Для FB2 — null.</summary>
  private List<string>? _epubChapterHtmlBodies;
  private bool _verticalFullBookLayoutValid;
  private bool _readingWithWebView;
  private Microsoft.Maui.Dispatching.IDispatcherTimer? _webScrollTimer;
  /// <summary>Горизонтальное листание: синхронизация номера страницы в меню с позицией колонок WebView.</summary>
  private Microsoft.Maui.Dispatching.IDispatcherTimer? _horizontalPageSyncTimer;
  private bool _tocOverlayVisible;
  /// <summary>Не обрабатывать выбор при программной установке SelectedItem (открытие оглавления).</summary>
  private bool _tocSuppressSelectionChanged;
  private bool _tocMarginListenersAttached;
  /// <summary>Завершена первичная загрузка книги (чтобы OnAppearing не дублировал рендер при первом открытии).</summary>
  private bool _initialLoadFinished;

  /// <summary>После SaveReadingStateAsync в OnBack/OnHome — не сохранять снова в OnDisappearing (иначе позиция портится после остановки WebView).</summary>
  private bool _skipReadingStateSaveOnDisappearing;

  private bool _horizontalPagesLayoutValid;
  /// <summary>Загрузка measure-shell для калибровки cap (не показывать как страницу чтения).</summary>
  private bool _paginationMeasureMode;
  private TaskCompletionSource<bool>? _measureShellTcs;
  /// <summary>Макс. размер хвоста для одного вызова __measureFitMaxLen (символов).</summary>
  private const int MaxMeasureFragmentChars = 262144;
  /// <summary>Последний выгруженный в WebView HTML — чтобы не перезагружать тот же документ без причины.</summary>
  private string? _lastCommittedReadingHtml;
  /// <summary>Последние применённые настройки (нормализованные), чтобы не пересчитывать страницы без причины.</summary>
  private TextSettings? _lastAppliedTextSettingsSnapshot;
  /// <summary>Игнорировать app://pagenext|pageprev сразу после загрузки HTML — WebView иногда шлёт ложные клики по краям.</summary>
  private DateTime _suppressWebViewPagingNavUntilUtc;
  /// <summary>Горизонтальный multicol: один HTML загружен, страницы режет движок + горизонтальный скролл.</summary>
  private bool _horizontalColumnDocLoaded;
  private long? _pendingHorizontalColumnRestoreOffset;
  private int? _pendingHorizontalChapterJumpAfterLoad;
  /// <summary>После поворота/resize: один отложенный пересчёт ширины и возврат на сохранённую страницу.</summary>
  private CancellationTokenSource? _horizontalLayoutRealignCts;
  /// <summary>Подавить realign колонок сразу после снятия оверлея — иначе второй sync+reflow даёт «слипшийся» текст.</summary>
  private DateTime _suppressHorizontalRealignUntilUtc;
  /// <summary>Вертикальный режим: последняя отрисованная в WebView глава (для кэша HTML).</summary>
  private int _lastRenderedVerticalChapterIndex = -1;
  /// <summary>Вертикальный режим: «экранная» страница внутри текущей главы (по высоте вьюпорта).</summary>
  private int _verticalViewportPageIndex;
  private int _verticalViewportPageCount = 1;
  /// <summary>Показан оверлей «книга открывается» до первого успешного Navigated WebView.</summary>
  private bool _pendingReadingLoadUi;
  /// <summary>Вертикальный WebView: подпись «N из M» только после синхронизации scroll (иначе мелькают чужие числа).</summary>
  private bool _verticalPageNumbersSynced = true;
  /// <summary>
  /// После перехода к заметке DOM ещё кратко на scrollY≈0; PollWeb по верхнему абзацу затирал <see cref="_currentPageScrollRatio"/> началом главы.
  /// </summary>
  private DateTime _suppressVerticalPollAnchorApplyUntilUtc;
  /// <summary>Горизонтальный multicol: последний «верхний видимый символ» из DOM (обновляется лёгим опросом; <see cref="ComputeCurrentFullTextOffset"/>).</summary>
  private long? _horizontalDomBookAnchorCache;

  /// <summary>Связывает разметку с обработчиком списка оглавления и событием <see cref="Loaded"/> области чтения.</summary>
  public ReadingPage()
  {
    InitializeComponent();
    SetPanelsVisibility(true);
    if (TocList != null)
      TocList.SelectionChanged += OnTocListSelectionChanged;
    Loaded += OnReadingPageLoaded;
  }

  /// <summary>После создания платформенного обработчика выравнивает системные области под режим чтения.</summary>
  protected override void OnHandlerChanged()
  {
    base.OnHandlerChanged();
    if (Handler != null)
      TryUpdateReadingSystemBars();
  }

  /// <summary>Вешает контролируемые обработчики размеров и отступы оглавления после первичной загрузки макета.</summary>
  private void OnReadingPageLoaded(object? sender, EventArgs e)
  {
    AttachTocOverlayMarginListeners();
    UpdateTocOverlayMargins();
    if (ReadingLayer != null)
      ReadingLayer.SizeChanged += OnReadingLayerSizeChanged;
    if (BookWebView != null)
      BookWebView.SizeChanged += OnBookWebViewSizeChanged;
    UpdateReadingChromeInsets();
    TryUpdateReadingSystemBars();
  }

  /// <summary>Ширина WebView в CSS px для JS: нативная ширина контрола, без visualViewport.</summary>
  private int GetHorizontalReaderNativeWidthPx()
  {
    if (BookWebView != null && BookWebView.Width > 0)
      return (int)Math.Round(BookWebView.Width);
    if (ReadingLayer != null && ReadingLayer.Width > 0)
      return (int)Math.Round(ReadingLayer.Width);
    return 0;
  }

  /// <summary>В горизонтальном WebView режиме планирует пересборку после изменения контроля размеров.</summary>
  private void OnBookWebViewSizeChanged(object? sender, EventArgs e)
  {
    if (_pendingReadingLoadUi)
      return;
    if (!_readingWithWebView || _isVerticalMode || !_horizontalColumnDocLoaded || BookWebView == null)
      return;
    ScheduleHorizontalReaderRealignAfterLayoutChange();
  }

  /// <summary>Откладывает realign горизонтального читателя после смены размеров (альбомная ориентация, первый размер).</summary>
  private void ScheduleHorizontalReaderRealignAfterLayoutChange()
  {
    if (_pendingReadingLoadUi)
      return;
    if (DateTime.UtcNow < _suppressHorizontalRealignUntilUtc)
      return;
    if (!_readingWithWebView || _isVerticalMode || !_horizontalColumnDocLoaded || BookWebView == null)
      return;
    _horizontalLayoutRealignCts?.Cancel();
    _horizontalLayoutRealignCts?.Dispose();
    var cts = new CancellationTokenSource();
    _horizontalLayoutRealignCts = cts;
    _ = HorizontalReaderRealignAfterLayoutAsync(cts.Token);
  }

  /// <summary>Выполняет отложенный realign после resize: повторная инъекция ширины для JS и навигация к сохранённому смещению книги.</summary>
  private async Task HorizontalReaderRealignAfterLayoutAsync(CancellationToken token)
  {
    try
    {
      await Task.Delay(60, token).ConfigureAwait(true);
      await InjectHorizontalReaderNativeWidthAsync().ConfigureAwait(true);
      await Task.Delay(100, token).ConfigureAwait(true);
      if (string.IsNullOrEmpty(_fullText) || _currentBook == null || BookWebView == null)
        return;
      long maxIx = Math.Max(0, _fullText.Length - 1);
      long offDom = Math.Clamp(ComputeCurrentFullTextOffset(), 0, maxIx);
      long off = offDom;
      try
      {
        var pos = await _db.GetReadingPositionByCardIdAsync(_currentBook.Id).ConfigureAwait(true);
        if (pos != null)
          off = Math.Clamp(pos.CharacterOffset, 0, maxIx);
      }
      catch { }
      await HorizontalNavigateToBookOffsetAsync(off).ConfigureAwait(true);
      await RefreshHorizontalColumnPageIndexFromScrollAsync().ConfigureAwait(true);
      await MainThread.InvokeOnMainThreadAsync(() =>
      {
        UpdatePageNumberLabel();
        UpdateBookHeaders();
      });
    }
    catch (OperationCanceledException)
    {
    }
    catch
    {
    }
  }

  /// <summary>Короткое ожидание ненулевой ширины (WebView или слоя). Нельзя отбрасывать ширину WebView из‑за ещё не измеренного ReadingLayer.</summary>
  private async Task WaitForHorizontalReaderWidthForLayoutAsync()
  {
    for (int i = 0; i < 48; i++)
    {
      int w = await MainThread.InvokeOnMainThreadAsync(() => GetHorizontalReaderNativeWidthPx());
      if (w >= 32)
        return;
      await Task.Delay(40).ConfigureAwait(true);
    }
  }

  /// <summary>Ждём, пока ReadingLayer перестанет менять размер (альбомная ориентация / первый кадр после открытия).</summary>
  private async Task WaitForStableReadingLayerBoxAsync()
  {
    double? lastW = null;
    double? lastH = null;
    int stableFrames = 0;
    for (int i = 0; i < 55; i++)
    {
      var (w, h) = await MainThread.InvokeOnMainThreadAsync(() =>
      {
        if (ReadingLayer == null)
          return (0.0, 0.0);
        return (ReadingLayer.Width, ReadingLayer.Height);
      });
      if (w > 1 && h > 1 && !double.IsNaN(w) && !double.IsNaN(h))
      {
        if (lastW.HasValue && Math.Abs(w - lastW.Value) < 0.75 && Math.Abs(h - lastH!.Value) < 0.75)
        {
          stableFrames++;
          if (stableFrames >= 3)
            return;
        }
        else
          stableFrames = 0;
        lastW = w;
        lastH = h;
      }
      await Task.Delay(40).ConfigureAwait(true);
    }
  }

  /// <summary>Возвращает true, когда область чтения шире, чем выше (ориентация «альбом» в логических пикселях слоя).</summary>
  private bool IsReadingLayerLandscapeLayout()
  {
    if (ReadingLayer == null) return false;
    return ReadingLayer.Width > ReadingLayer.Height + 8;
  }

  /// <summary>Пишет ширину нативного WebView в <c>window.__readerNativeWidthPx</c> и запускает JS-синхронизацию горизонтальной вёрстки.</summary>
  private async Task InjectHorizontalReaderNativeWidthAsync()
  {
    if (BookWebView == null || _isVerticalMode || !_horizontalColumnDocLoaded)
      return;
    int wPx = GetHorizontalReaderNativeWidthPx();
    if (wPx <= 0)
      return;
    string js =
        "(function(){window.__readerNativeWidthPx=" + wPx.ToString(CultureInfo.InvariantCulture)
        + ";if(window.__syncHorizontalReaderLayout)window.__syncHorizontalReaderLayout();"
        + "if(window.__hrReflowPagePos)window.__hrReflowPagePos();})()";
    try
    {
      await BookWebView.EvaluateJavaScriptAsync(js).ConfigureAwait(true);
    }
    catch { }
  }

  /// <summary>Подписки на изменение размеров корня и панелей, чтобы пересчитывать отступы выезжающего оглавления.</summary>
  private void AttachTocOverlayMarginListeners()
  {
    if (_tocMarginListenersAttached)
      return;
    _tocMarginListenersAttached = true;
    if (RootGrid != null)
      RootGrid.SizeChanged += (_, _) => UpdateTocOverlayMargins();
    if (TopChromeRoot != null)
      TopChromeRoot.SizeChanged += (_, _) => UpdateTocOverlayMargins();
    if (BottomPanel != null)
      BottomPanel.SizeChanged += (_, _) => UpdateTocOverlayMargins();
  }

  /// <summary>Отступы области чтения и оверлеев от верхней/нижней панели приложения и системной навигации.</summary>
  private void UpdateTocOverlayMargins() => UpdateReadingChromeInsets();

  /// <summary>Суммарная высота верхнего приложенческого хрома и дополнительного system safe inset (для области чтения под панели).</summary>
  private double GetChromeTopInset()
  {
    double top = 0;
    if (_panelsVisible)
    {
      if (TopChromeRoot != null && TopChromeRoot.Height > 0)
        top = TopChromeRoot.Height;
      else if (TopPanel != null && HeaderPanel != null)
        top = TopPanel.Height + HeaderPanel.Height;
    }
    return top + GetSystemSafeTopInsetExtra();
  }

  /// <summary>Суммарная высота нижней панели и нижнего safe area (галочная навигация и т. п.).</summary>
  private double GetChromeBottomInset()
  {
    double bottom = 0;
    if (_panelsVisible && BottomPanel != null && BottomPanel.Height > 0)
      bottom = BottomPanel.Height;
    return bottom + GetSystemSafeBottomInsetExtra();
  }

  /// <summary>Вертикальные отступы только под панели приложения (без system safe extra) — для оверлея оглавления, чтобы оно стыковалось с верхом/низом без зазора.</summary>
  private double GetTocChromeTopInset()
  {
    if (!_panelsVisible)
      return 0;
    if (TopChromeRoot != null && TopChromeRoot.Height > 0)
      return TopChromeRoot.Height;
    if (TopPanel != null && HeaderPanel != null)
      return TopPanel.Height + HeaderPanel.Height;
    return 0;
  }

  /// <summary>Высота нижней панели для отступов панели оглавления при видимых панелях.</summary>
  private double GetTocChromeBottomInset()
  {
    if (!_panelsVisible || BottomPanel == null || BottomPanel.Height <= 0)
      return 0;
    return BottomPanel.Height;
  }

  /// <summary>Дополнительный нижний отступ под системную навигацию (различается по платформе).</summary>
  private static double GetSystemSafeBottomInsetExtra()
  {
#if ANDROID
    try
    {
      double d = DeviceDisplay.MainDisplayInfo.Density;
      if (d > 0)
        return 28 / d;
    }
    catch { }
    return 16;
#elif IOS || MACCATALYST
    return 8;
#else
    return 12;
#endif
  }

  /// <summary>Дополнительный верхний отступ под вырезы/статус-бар (в основном iOS).</summary>
  private static double GetSystemSafeTopInsetExtra()
  {
#if IOS || MACCATALYST
    return 4;
#else
    return 0;
#endif
  }

  /// <summary>Обновляет margin слоёв чтения и оглавления согласно панелям и полям текста из настроек.</summary>
  private void UpdateReadingChromeInsets()
  {
    if (ReadingLayer != null)
      ReadingLayer.Margin = new Thickness(0);
    // Текст на весь экран; верх/низ — оверлеи поверх WebView (как в XAML). Не задаём Margin у WebView:
    // иначе при скрытии панелей меняется inset и текст «подпрыгивает». Отступы читаемости — в HTML (safe-area + поля в настройках).
    if (BookWebView != null)
      BookWebView.Margin = new Thickness(0);
    if (TocOverlay != null)
      TocOverlay.Margin = new Thickness(0);
    if (TocPanel != null)
      TocPanel.Margin = new Thickness(0, GetTocChromeTopInset(), 0, GetTocChromeBottomInset());
    if (HorizontalPageTurnLayer != null)
      HorizontalPageTurnLayer.Margin = new Thickness(0);
    UpdateSentencesLayoutWidth();
  }

  /// <summary>Скрывает нативный слой эмуляции перелистывания: жесты обрабатываются внутри WebView.</summary>
  private void UpdateHorizontalPageTurnLayerVisibility()
  {
    if (HorizontalPageTurnLayer == null)
      return;
    // Листание и тапы — в WebView (JS), без тяжёлого оверлея поверх WebView.
    HorizontalPageTurnLayer.IsVisible = false;
    UpdateReadingChromeInsets();
  }

  /// <summary>Приводит параметры текста из БД к допустимым границам (шрифт, поля и т. д.).</summary>
  private static TextSettings ComputeEffectiveTextSettings(TextSettings? ts)
  {
    if (ts == null) ts = new TextSettings();
    int fs = ts.FontSize > 0 ? ts.FontSize : TextReadingLayout.DefaultFontSize;
    fs = Math.Clamp(fs, TextReadingLayout.MinFontSize, TextReadingLayout.MaxFontSize);
    int m = ts.Margins > 0 ? ts.Margins : TextReadingLayout.DefaultMargins;
    m = Math.Clamp(m, TextReadingLayout.MinMargins, TextReadingLayout.MaxMargins);
    return new TextSettings
    {
      Id = ts.Id,
      FontSize = fs,
      Margins = m,
      TextAlignment = ts.TextAlignment?.Trim() ?? "",
      ScrollingMode = ts.ScrollingMode?.Trim() ?? "",
      TextColor = (ts.TextColor ?? "").Trim(),
      BackgroundColor = (ts.BackgroundColor ?? "").Trim(),
      TranslationLanguage = (ts.TranslationLanguage ?? "").Trim()
    };
  }

  /// <summary>Копирует объект настроек для снимка и сравнения без мутаций.</summary>
  private static TextSettings CloneTextSettings(TextSettings s) =>
      new()
      {
        Id = s.Id,
        FontSize = s.FontSize,
        Margins = s.Margins,
        TextAlignment = s.TextAlignment,
        ScrollingMode = s.ScrollingMode,
        TextColor = s.TextColor,
        BackgroundColor = s.BackgroundColor,
        TranslationLanguage = s.TranslationLanguage
      };

  /// <summary>Сравнивает поля влияющие на пагинацию: режим скролла, шрифт, поля, выравнивание.</summary>
  private static bool PaginationLayoutEqualEffective(TextSettings ea, TextSettings eb)
  {
    bool ha = TextReadingLayout.IsHorizontalScrollingMode(ea.ScrollingMode);
    bool hb = TextReadingLayout.IsHorizontalScrollingMode(eb.ScrollingMode);
    if (ha != hb) return false;
    if (ea.FontSize != eb.FontSize) return false;
    if (ea.Margins != eb.Margins) return false;
    if (!string.Equals(ea.TextAlignment ?? "", eb.TextAlignment ?? "", StringComparison.OrdinalIgnoreCase))
      return false;
    return true;
  }

  /// <summary>Полное сравнение настроек чтения, включая цвета и язык перевода.</summary>
  private static bool FullSettingsEqualEffective(TextSettings ea, TextSettings eb)
  {
    if (!PaginationLayoutEqualEffective(ea, eb)) return false;
    if (!string.Equals(ea.TextColor ?? "", eb.TextColor ?? "", StringComparison.OrdinalIgnoreCase)) return false;
    if (!string.Equals(ea.BackgroundColor ?? "", eb.BackgroundColor ?? "", StringComparison.OrdinalIgnoreCase)) return false;
    if (!string.Equals(ea.TranslationLanguage ?? "", eb.TranslationLanguage ?? "", StringComparison.OrdinalIgnoreCase))
      return false;
    return true;
  }

  /// <summary>При изменении размера слоя обновляет отступы и при необходимости планирует realign горизонтального WebView или подпись страницы.</summary>
  private void OnReadingLayerSizeChanged(object? sender, EventArgs e)
  {
    // Только отступы/хром — НЕ сбрасываем пагинацию: иначе после каждого кадра WebView снова грузится measure-shell + книга.
    UpdateReadingChromeInsets();
    if (!_pendingReadingLoadUi && !_isVerticalMode && _readingWithWebView && _horizontalColumnDocLoaded && BookWebView != null)
      ScheduleHorizontalReaderRealignAfterLayoutChange();
    if (ReadingLayer != null && ReadingLayer.Width > 1 && ReadingLayer.Height > 1 && !_pendingReadingLoadUi
        && !(_isVerticalMode && _readingWithWebView && !_verticalPageNumbersSynced))
      MainThread.BeginInvokeOnMainThread(UpdatePageNumberLabel);
  }

  /// <summary>Ожидание первичного прохода measure: у слоя есть ненулевая ширина и высота.</summary>
  private async Task WaitForReadingLayerLayoutAsync()
  {
    for (int i = 0; i < 80; i++)
    {
      bool ready = await MainThread.InvokeOnMainThreadAsync(() =>
          ReadingLayer != null
          && ReadingLayer.Width > 1
          && ReadingLayer.Height > 1
          && !double.IsNaN(ReadingLayer.Width));
      if (ready)
        return;
      await Task.Delay(40).ConfigureAwait(true);
    }
  }

  /// <summary>Показывает оверлей загрузки, сбрасывает строки заголовка и блокирует досрочное обновление подписей до готовности WebView.</summary>
  private void ShowReadingLoadingUi(bool applyingSettings = false, string? messageOverride = null)
  {
    _pendingReadingLoadUi = true;
    _initialLoadFinished = false;
    if (ReadingLoadingOverlay != null)
      ReadingLoadingOverlay.IsVisible = true;
    if (ReadingLoadIndicator != null)
      ReadingLoadIndicator.IsRunning = true;
    if (ReadingLoadingMessageLabel != null)
      ReadingLoadingMessageLabel.Text = messageOverride
          ?? (applyingSettings ? Strings.Reading_ApplyingTextSettings : Strings.Reading_OpeningBook);
    if (ChapterLabel != null)
      ChapterLabel.Text = "";
    if (BookTitleLabel != null)
      BookTitleLabel.Text = "";
    if (PageInfoLabel != null)
      PageInfoLabel.Text = "";
    // Не трогаем ReadingLayer.Opacity: скрытие слоя заставляет WebView переложить колонки дважды («слипшийся» текст).
    // Загрузку закрываем только ReadingLoadingOverlay (ZIndex выше текста).
  }

  /// <summary>Скрывает индикатор загрузки, помечает первичную загрузку завершённой и включает таймер синхронизации номера страницы для горизонтали.</summary>
  private void HideReadingLoadingUi()
  {
    _pendingReadingLoadUi = false;
    if (ReadingLoadingOverlay != null)
      ReadingLoadingOverlay.IsVisible = false;
    if (ReadingLoadIndicator != null)
      ReadingLoadIndicator.IsRunning = false;
    _initialLoadFinished = true;
    if (BookWebView != null && !_isVerticalMode && _readingWithWebView)
      BookWebView.Opacity = 1;
    // Первый кадр после показа WebView часто даёт OnBookWebViewSizeChanged → повторный reflow; откладываем на ~1.5 с.
    if (!_isVerticalMode && _readingWithWebView)
      _suppressHorizontalRealignUntilUtc = DateTime.UtcNow.AddMilliseconds(1800);
    UpdateBookHeaders();
    UpdatePageNumberLabel();
    if (!_isVerticalMode && _readingWithWebView && _horizontalColumnDocLoaded && BookWebView != null)
      StartHorizontalPageIndexSyncTimerIfNeeded();
  }

  /// <summary>
  /// Один финальный sync+scroll перед снятием оверлея. Много проходов inject/reflow отменено: они конфликтовали с ResizeObserver и realign при загрузке («слипшийся» текст).
  /// </summary>
  private async Task StabilizeHorizontalReaderAtRestoredPageBeforeDismissOverlayAsync()
  {
    if (BookWebView == null || !_horizontalColumnDocLoaded || !_readingWithWebView || _isVerticalMode)
      return;

    await WaitForStableReadingLayerBoxAsync().ConfigureAwait(true);
    await WaitForHorizontalReaderWidthForLayoutAsync().ConfigureAwait(true);
    bool landscape = await MainThread.InvokeOnMainThreadAsync(() => IsReadingLayerLandscapeLayout());
    int anchor = Math.Clamp(_currentPageIndex, 0, Math.Max(0, Math.Max(1, _pageCount) - 1));

    await MainThread.InvokeOnMainThreadAsync(async () =>
    {
      if (BookWebView == null) return;
      int pc = Math.Max(1, _pageCount);
      int goal = Math.Clamp(anchor, 0, Math.Max(0, pc - 1));
      _currentPageIndex = goal;
      await InjectHorizontalReaderNativeWidthAsync().ConfigureAwait(true);
      try
      {
        await BookWebView.EvaluateJavaScriptAsync(
            "(function(){if(window.__syncHorizontalReaderLayout)window.__syncHorizontalReaderLayout();" +
            "if(window.__hrReflowPagePos)window.__hrReflowPagePos();})()").ConfigureAwait(true);
      }
      catch { }
      await ScrollHorizontalColumnToPageAsync(goal, smooth: false).ConfigureAwait(true);
    }).ConfigureAwait(true);

    await Task.Delay(landscape ? 240 : 150).ConfigureAwait(true);
    await MainThread.InvokeOnMainThreadAsync(async () =>
    {
      await RefreshHorizontalColumnPageStateFromDomAsync(leadingLayoutDelay: true).ConfigureAwait(true);
    }).ConfigureAwait(true);
    await Task.Delay(landscape ? 120 : 80).ConfigureAwait(true);
    await WaitForHorizontalDomLayoutMetricsOkAsync().ConfigureAwait(true);
    await MainThread.InvokeOnMainThreadAsync(() =>
    {
      if (BookWebView != null)
        BookWebView.Opacity = 1;
    });
  }

  /// <summary>Ждём, пока во фрейме WebView появятся ненулевые ширина/высота области колонок (альбом часто даёт 0 до второго кадра).</summary>
  private async Task WaitForHorizontalDomLayoutMetricsOkAsync(int maxAttempts = 55)
  {
    if (BookWebView == null)
      return;
    for (int attempt = 0; attempt < maxAttempts; attempt++)
    {
      string? raw = await MainThread.InvokeOnMainThreadAsync(async () =>
      {
        try
        {
          return await BookWebView!.EvaluateJavaScriptAsync(
              "(function(){if(typeof window.__hrLayoutOk!=='function')return '0|0|0';return window.__hrLayoutOk();})()").ConfigureAwait(true);
        }
        catch
        {
          return null;
        }
      }).ConfigureAwait(true);
      if (!string.IsNullOrWhiteSpace(raw))
      {
        string t = raw.Trim().Trim('"');
        if (t.StartsWith("1|", StringComparison.Ordinal))
          return;
      }
      await Task.Delay(80).ConfigureAwait(true);
    }
  }

  /// <summary>Снимает оверлей только после стабилизации вёрстки WebView (иначе «слипшийся» текст и пустые подписи).</summary>
  private async Task ScheduleHideReadingLoadingUiWhenReadyAsync(bool quick = false)
  {
    if (!_pendingReadingLoadUi)
      return;
    if (!quick)
    {
      if (_readingWithWebView && _isVerticalMode && BookWebView != null)
      {
        await Task.Delay(320).ConfigureAwait(true);
        await MainThread.InvokeOnMainThreadAsync(async () => await PollWebScrollAsync().ConfigureAwait(true));
        await Task.Delay(180).ConfigureAwait(true);
        await MainThread.InvokeOnMainThreadAsync(async () => await PollWebScrollAsync().ConfigureAwait(true));
        await MainThread.InvokeOnMainThreadAsync(() => { _verticalPageNumbersSynced = true; });
      }
      else if (_readingWithWebView && !_isVerticalMode && BookWebView != null && _horizontalColumnDocLoaded)
      {
        try
        {
          await StabilizeHorizontalReaderAtRestoredPageBeforeDismissOverlayAsync().ConfigureAwait(true);
        }
        catch
        {
          // Не блокируем снятие оверлея при сбое стабилизации — иначе книга «не открывается».
        }
      }
    }
    else
      await Task.Delay(100).ConfigureAwait(true);

    await MainThread.InvokeOnMainThreadAsync(async () =>
    {
      if (!_pendingReadingLoadUi)
        return;
      if (_readingWithWebView && !_isVerticalMode && _horizontalColumnDocLoaded && BookWebView != null)
      {
        await RefreshHorizontalColumnPageCountAsync().ConfigureAwait(true);
        UpdatePageNumberLabel();
        UpdateBookHeaders();
      }
      else
      {
        UpdatePageNumberLabel();
        UpdateBookHeaders();
      }
      await PersistCurrentBookEstimatedPageCountToCardAsync().ConfigureAwait(true);
      if (_pendingReadingLoadUi)
        HideReadingLoadingUi();
    });
  }

  /// <summary>Если оверлей ещё висит после рендера — планирует его съём после стабилизации верстки (особенно важно для горизонтали).</summary>
  private void CompleteReadingLoadUiIfPending()
  {
    if (!_pendingReadingLoadUi)
      return;
    // Горизонталь: нельзя quick (100 ms) — перебивает полную стабилизацию из OnBookWebNavigated и оверлей исчезает до вёрстки.
    _ = ScheduleHideReadingLoadingUiWhenReadyAsync(quick: _isVerticalMode);
  }

  /// <summary>Запускает периодический лёгкий опрос DOM для актуальных индекса колонки и числа страниц в горизонтальном multicol.</summary>
  private void StartHorizontalPageIndexSyncTimerIfNeeded()
  {
    if (_isVerticalMode || !_readingWithWebView || !_horizontalColumnDocLoaded || BookWebView == null)
      return;
    if (_horizontalPageSyncTimer != null)
      return;
    _horizontalPageSyncTimer = Dispatcher.CreateTimer();
    _horizontalPageSyncTimer.Interval = TimeSpan.FromMilliseconds(480);
    _horizontalPageSyncTimer.Tick += OnHorizontalPageSyncTick;
    _horizontalPageSyncTimer.Start();
  }

  /// <summary>Тик таймера: обновление позиции в горизонтальном читателе без тяжёлого полного reflow.</summary>
  private async void OnHorizontalPageSyncTick(object? sender, EventArgs e)
  {
    if (_isRendering || _isVerticalMode || !_horizontalColumnDocLoaded || BookWebView == null || !_readingWithWebView)
      return;
    try
    {
      // Без reflowPagePos в __hrPageCount — иначе каждые N мс полный sync+reflow и листание после смены вертикаль↔горизонталь «тупит».
      await RefreshHorizontalColumnPageStateFromDomLightAsync().ConfigureAwait(true);
      await MainThread.InvokeOnMainThreadAsync(() =>
      {
        UpdatePageNumberLabel();
        UpdateBookHeaders();
      });
    }
    catch { }
  }

  /// <summary>Останавливает и освобождает таймер синхронизации горизонтальной страницы.</summary>
  private void StopHorizontalPageIndexSyncTimer()
  {
    if (_horizontalPageSyncTimer == null)
      return;
    _horizontalPageSyncTimer.Stop();
    _horizontalPageSyncTimer.Tick -= OnHorizontalPageSyncTick;
    _horizontalPageSyncTimer = null;
  }

  /// <summary>Сброс расчёта страниц только при смене параметров вёрстки в настройках или при новой книге/новом тексте (см. вызовы).</summary>
  private void InvalidatePaginationForLayout()
  {
    _horizontalPagesLayoutValid = false;
    _lastCommittedReadingHtml = null;
    _horizontalColumnDocLoaded = false;
    _horizontalDomBookAnchorCache = null;
    _pendingHorizontalChapterJumpAfterLoad = null;
    _verticalFullBookLayoutValid = false;
    _lastRenderedVerticalChapterIndex = -1;
  }

  /// <summary>Применяет настройки текста из модели БД к полям состояния и при существенном изменении сбрасывает раскладку пагинации.</summary>
  private void ApplyTextSettingsFromModel(TextSettings? ts)
  {
    if (ts == null) ts = new TextSettings();
    var effectiveNew = ComputeEffectiveTextSettings(ts);
    bool paginationChanged = _lastAppliedTextSettingsSnapshot == null
        || !PaginationLayoutEqualEffective(_lastAppliedTextSettingsSnapshot, effectiveNew);

    // TextSettings.ScrollingMode: «Горизонт» = листание страницами в приложении, не ориентация экрана.
    bool wasVertical = _isVerticalMode;
    _isVerticalMode = !TextReadingLayout.IsHorizontalScrollingMode(effectiveNew.ScrollingMode);
    if (wasVertical != _isVerticalMode)
    {
      StopHorizontalPageIndexSyncTimer();
      // Всегда сбрасываем при смене оси чтения (даже если сравнение снимка по ошибке не увидело изменение).
      InvalidatePaginationForLayout();
    }
    if (BookScrollView != null)
      BookScrollView.Orientation = _isVerticalMode ? ScrollOrientation.Vertical : ScrollOrientation.Neither;
    _readingFontSize = effectiveNew.FontSize;
    _paragraphTextAlignment = TextReadingLayout.ParseAlignment(effectiveNew.TextAlignment);
    _readingTextColor = TextReadingLayout.ParseColorHex(effectiveNew.TextColor, ResolveThemeColor("MainTextColor", Colors.Black));
    _readingBgColor = TextReadingLayout.ParseColorHex(effectiveNew.BackgroundColor, ResolveThemeColor("BookPageBackground", Colors.White));
    _readingMarginPx = effectiveNew.Margins;
    _charsPerPage = TextReadingLayout.GetCharsPerPage(effectiveNew);
    if (paginationChanged)
      InvalidatePaginationForLayout();
    ApplyReadingMargins();
    UpdateHorizontalPageTurnLayerVisibility();
    _lastAppliedTextSettingsSnapshot = CloneTextSettings(effectiveNew);
    TryUpdateReadingSystemBars();
  }

  private const double ScrollContentBottomExtraPadding = 14;

  /// <summary>Применяет поля текста к fallback-слою Label и цвет фона к скроллу и слою чтения.</summary>
  private void ApplyReadingMargins()
  {
    if (SentencesLayout != null)
    {
      SentencesLayout.Padding = new Thickness(
      _readingMarginPx,
      _readingMarginPx,
      _readingMarginPx,
      _readingMarginPx + ScrollContentBottomExtraPadding);
    }
    if (BookScrollView != null)
      BookScrollView.BackgroundColor = _readingBgColor;
    if (ReadingLayer != null)
      ReadingLayer.BackgroundColor = _readingBgColor;
    UpdateReadingChromeInsets();
  }

  /// <summary>Ширина текста = ширина области чтения минус поля (иначе на Windows подписи сжимаются по ширине контента).</summary>
  private void UpdateSentencesLayoutWidth()
  {
    if (SentencesLayout == null || ReadingLayer == null)
      return;
    double w = ReadingLayer.Width;
    if (w <= 0 || double.IsNaN(w) || double.IsInfinity(w))
      return;
    double inner = Math.Max(0, w - 2 * _readingMarginPx);
    if (inner > 0)
      SentencesLayout.WidthRequest = inner;
  }

  /// <summary>Первый смещённый символ внутри главы (не только пробелы/переносы), чтобы не терять начало главы при разбиении страниц.</summary>
  private long GetFirstContentOffsetInChapter(int chapterIndex)
  {
    if (_chapters.Count == 0 || chapterIndex < 0 || chapterIndex >= _chapters.Count)
      return 0;
    long st = _chapters[chapterIndex].Start;
    long en = _chapters[chapterIndex].End;
    while (st < en && st < _fullText.Length && char.IsWhiteSpace(_fullText[(int)st]))
      st++;
    return Math.Min(st, Math.Max(0, _fullText.Length - 1));
  }

  /// <summary>Показать или скрыть верхнюю шапку и нижнее меню; обновляет safe area оглавления и системные полосы.</summary>
  private void SetPanelsVisibility(bool visible)
  {
    _panelsVisible = visible;
    var opacity = visible ? 1.0 : 0.0;

    TopPanel.Opacity = opacity;
    HeaderPanel.Opacity = opacity;
    BottomPanel.Opacity = opacity;

    TopPanel.InputTransparent = !visible;
    HeaderPanel.InputTransparent = !visible;
    BottomPanel.InputTransparent = !visible;
    UpdateTocOverlayMargins();
    TryUpdateReadingSystemBars();
  }

  /// <summary>Строка состояния и панель навигации (Android): как меню или как фон чтения; контраст иконок по цвету текста.</summary>
  void TryUpdateReadingSystemBars()
  {
    if (Handler == null)
      return;
    try
    {
      var primaryBg = ResolveThemeColor("PrimaryBackground", Colors.White);
      var mainTxt = ResolveThemeColor("MainTextColor", Colors.Black);
      ReadingSystemChrome.ApplyReadingPage(_panelsVisible, primaryBg, mainTxt, _readingBgColor, _readingTextColor);
    }
    catch (Exception ex)
    {
      Debug.WriteLine($"[ReadingPage] TryUpdateReadingSystemBars: {ex.Message}");
    }
  }

  /// <summary>Возвращает системную строку состояния и навигации к палитре приложения после закрытия читалки.</summary>
  static void RestoreAppSystemChromeAfterReading()
  {
    try
    {
      var res = Application.Current?.Resources;
      Color primaryBg = Colors.White;
      Color mainTxt = Colors.Black;
      if (res != null && res.TryGetValue("PrimaryBackground", out var pb) && pb is Color p)
        primaryBg = p;
      if (res != null && res.TryGetValue("MainTextColor", out var mt) && mt is Color m)
        mainTxt = m;
      ReadingSystemChrome.RestoreAppChrome(primaryBg, mainTxt);
    }
    catch (Exception ex)
    {
      Debug.WriteLine($"[ReadingPage] RestoreAppSystemChrome: {ex.Message}");
    }
  }

  /// <summary>Устанавливает текущую карточку книги и запускает цепочку загрузки текста (<see cref="LoadBookContentWithOptionalFirstOpenTipAsync"/>).</summary>
  public void SetBookData(Card card)
  {
    _pendingOpenNoteAfterLoad = null;
    _pendingWebScrollToBookOffset = null;
    _suppressVerticalPollAnchorApplyUntilUtc = default;
    _horizontalDomBookAnchorCache = null;
    _currentBook = card;
    _ = LoadBookContentWithOptionalFirstOpenTipAsync();
  }

  /// <summary>
  /// Открыть заметку из списка. Позиция строго по сохранённому символу <see cref="Note.CharacterOffset"/>
  /// в тексте текущего файла (пересчёт через предложение не выполняется — он давал начало книги или другую страницу).
  /// </summary>
  public async Task NavigateToNoteFromListAsync(Note note)
  {
    Task HideNoteJumpLoadingAsync() =>
        MainThread.InvokeOnMainThreadAsync(HideReadingLoadingUi);

    if (_currentBook != null && note.CardId == _currentBook.Id)
    {
      await MainThread.InvokeOnMainThreadAsync(() =>
          ShowReadingLoadingUi(messageOverride: Strings.Reading_OpeningNotePosition)).ConfigureAwait(true);
      try
      {
        if (string.IsNullOrEmpty(_fullText))
        {
          await HideNoteJumpLoadingAsync().ConfigureAwait(true);
          return;
        }

        long maxIx = Math.Max(0, _fullText.Length - 1);
        long off = Math.Clamp(note.CharacterOffset, 0, maxIx);
        await ScrollToFullTextOffsetAsync(off, persistExactReadingOffset: true).ConfigureAwait(true);
      }
      finally
      {
        await HideNoteJumpLoadingAsync().ConfigureAwait(true);
      }
      return;
    }

    var card = await _db.GetCardByIdAsync(note.CardId).ConfigureAwait(true);
    if (card == null || string.IsNullOrWhiteSpace(card.FilePath))
    {
      await MainThread.InvokeOnMainThreadAsync(async () =>
          await ThemedOverlayPresenter.ShowAlertAsync(
              this,
              Strings.Reading_Chrome_Notes,
              Strings.Notes_VersionUnavailable,
              Strings.Common_OK)).ConfigureAwait(true);
      return;
    }

    _pendingOpenNoteAfterLoad = note;
    _currentBook = card;
    await LoadBookContentAsync().ConfigureAwait(true);
  }

  /// <summary>Диалог «Новая заметка» поверх текста (экран заметок при этом уже закрыт).</summary>
  public async Task ShowAddNoteEditorAsync()
  {
    if (_currentBook == null || string.IsNullOrEmpty(_fullText) || NoteEditorOverlay == null)
      return;
    long maxIx = Math.Max(0, _fullText.Length - 1);
    if (_readingWithWebView && !_isVerticalMode && _horizontalColumnDocLoaded && BookWebView != null)
      await RefreshHorizontalColumnPageStateFromDomLightAsync().ConfigureAwait(true);
    var anchors = await TryCollectWebViewAnchorOffsetsAsync().ConfigureAwait(true);
    long fb = await MainThread.InvokeOnMainThreadAsync(() => Math.Clamp(ComputeCurrentFullTextOffset(), 0, maxIx)).ConfigureAwait(true);
    // Заметка: сначала центр экрана (что реально читают), затем верх, затем «лид» колонки — hrLead давал сдвиг на прошлую колонку/страницу.
    long charOff = PickFirstValidBookOffset(
        maxIx, fb, anchors.center, anchors.top, anchors.hrLead, anchors.approx, anchors.hrScrollLinear);
    await MainThread.InvokeOnMainThreadAsync(() =>
    {
      _noteEditorAnchorOffset = charOff;
      if (NoteTitleEntry != null)
      {
        NoteTitleEntry.Text = "";
        NoteTitleEntry.Unfocus();
      }
      if (NoteCommentEditor != null)
      {
        NoteCommentEditor.Text = "";
        NoteCommentEditor.Unfocus();
      }
      NoteEditorOverlay.IsVisible = true;
      NoteEditorOverlay.InputTransparent = false;
    }).ConfigureAwait(true);
  }

  /// <summary>Парсит целое из строки результата JavaScript WebView с учётом кавычек и строки «null».</summary>
  static bool TryParseWebViewLong(string? raw, out long v)
  {
    v = -1;
    if (string.IsNullOrWhiteSpace(raw))
      return false;
    var t = raw.Trim().Trim('"');
    if (string.Equals(t, "null", StringComparison.OrdinalIgnoreCase))
      return false;
    if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= 0)
      return true;
    if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d))
    {
      v = (long)Math.Round(d);
      return v >= 0;
    }
    return false;
  }

  /// <summary>Возвращает первое смещение в книге из списка кандидатов, входящее в диапазон, иначе <paramref name="fallback"/>.</summary>
  static long PickFirstValidBookOffset(long maxIx, long fallback, params long[] ordered)
  {
    foreach (long x in ordered)
    {
      if (x >= 0 && x <= maxIx)
        return x;
    }
    return Math.Clamp(fallback, 0, maxIx);
  }

  /// <summary>
  /// Центр экрана, верх, аппроксимация, hrLinear, и для горизонтали — «лид» левой колонки.
  /// </summary>
  async Task<(long center, long top, long approx, long hrScrollLinear, long hrLead)> TryCollectWebViewAnchorOffsetsAsync()
  {
    long maxIx = Math.Max(0, _fullText.Length - 1);
    if (!(_readingWithWebView && BookWebView != null))
    {
      long fbOnly =
          await MainThread.InvokeOnMainThreadAsync(() => Math.Clamp(ComputeCurrentFullTextOffset(), 0, maxIx)).ConfigureAwait(true);
      return (fbOnly, fbOnly, fbOnly, -1, -1);
    }

    return await MainThread.InvokeOnMainThreadAsync(async () =>
    {
      await TryRefreshVerticalScrollRatioFromWebGeometryAsync().ConfigureAwait(true);
      if (!_isVerticalMode && _horizontalColumnDocLoaded && BookWebView != null)
      {
        int wPx = GetHorizontalReaderNativeWidthPx();
        if (wPx > 0)
        {
          string injectSync =
              "(function(){window.__readerNativeWidthPx="
              + wPx.ToString(CultureInfo.InvariantCulture)
              + ";if(window.__syncHorizontalReaderLayout)window.__syncHorizontalReaderLayout();"
              + "if(window.__hrReflowPagePos)window.__hrReflowPagePos();})()";
          try
          {
            await BookWebView.EvaluateJavaScriptAsync(injectSync).ConfigureAwait(true);
            await Task.Delay(40).ConfigureAwait(true);
          }
          catch (Exception syncEx)
          {
            Debug.WriteLine($"[ReadingPage] Horizontal anchor sync: {syncEx.Message}");
          }
        }
      }
      long fb = Math.Clamp(ComputeCurrentFullTextOffset(), 0, maxIx);
      long c = -1, t = -1, a = -1, hrLin = -1, hrLead = -1;
      try
      {
        string? rawCenter =
            await BookWebView!.EvaluateJavaScriptAsync(ReadingHtmlBuilder.EvaluateGetViewportCenterBookOffsetJavaScript)
                .ConfigureAwait(true);
        TryParseWebViewLong(rawCenter, out c);
        string? rawTop =
            await BookWebView!.EvaluateJavaScriptAsync(ReadingHtmlBuilder.EvaluateGetTopVisibleBookOffsetJavaScript)
                .ConfigureAwait(true);
        TryParseWebViewLong(rawTop, out t);
        string? rawApprox =
            await BookWebView!.EvaluateJavaScriptAsync(ReadingHtmlBuilder.EvaluateApproxBookOffsetJavaScript)
                .ConfigureAwait(true);
        TryParseWebViewLong(rawApprox, out a);

        if (!_isVerticalMode && _horizontalColumnDocLoaded)
        {
          string? rawHr =
              await BookWebView!.EvaluateJavaScriptAsync(ReadingHtmlBuilder.EvaluateHorizontalLinearBookApproxOffsetJavaScript)
                  .ConfigureAwait(true);
          TryParseWebViewLong(rawHr, out hrLin);
          string? rawLead =
              await BookWebView!.EvaluateJavaScriptAsync(ReadingHtmlBuilder.EvaluateHorizontalLeadBookOffsetJavaScript)
                  .ConfigureAwait(true);
          TryParseWebViewLong(rawLead, out hrLead);
        }
      }
      catch (Exception ex)
      {
        Debug.WriteLine($"[ReadingPage] TryCollectWebViewAnchorOffsetsAsync: {ex.Message}");
      }

      static long validOrNeg1(long v, long maxI) => (v >= 0 && v <= maxI) ? v : -1;
      c = validOrNeg1(c, maxIx);
      t = validOrNeg1(t, maxIx);
      a = validOrNeg1(a, maxIx);
      hrLin = validOrNeg1(hrLin, maxIx);
      hrLead = validOrNeg1(hrLead, maxIx);

      try
      {
        if (!string.IsNullOrEmpty(_fullText))
        {
          // Центр области чтения ближе к тому, что видит пользователь, чем «лид» с caret у края (multicol).
          if (c >= 0)
            _horizontalDomBookAnchorCache = c;
          else if (t >= 0)
            _horizontalDomBookAnchorCache = t;
          else if (hrLead >= 0)
            _horizontalDomBookAnchorCache = hrLead;
          else if (hrLin >= 0)
            _horizontalDomBookAnchorCache = hrLin;
        }
      }
      catch { }

      if (c < 0 && t < 0 && a < 0 && hrLin < 0 && hrLead < 0)
        return (fb, fb, fb, fb, fb);
      return (c, t, a, hrLin, hrLead);
    }).ConfigureAwait(true);
  }

  /// <summary>Синхронизировать <see cref="_currentPageScrollRatio"/> с y/(scrollHeight-viewport) — до Read DOM data-bo.</summary>
  async Task TryRefreshVerticalScrollRatioFromWebGeometryAsync()
  {
    if (BookWebView == null || !_isVerticalMode || !_readingWithWebView)
      return;
    try
    {
      const string js =
          "(function(){var ih=Math.max(1,window.innerHeight||0);var sh=Math.max(document.documentElement.scrollHeight||0,document.body.scrollHeight||0);"
          + "var y=+(window.scrollY||window.pageYOffset||(document.documentElement&&document.documentElement.scrollTop)||0);"
          + "var h=Math.max(0,sh-ih);return h<=0?0:Math.min(1,Math.max(0,y/h));})()";
      string? s = await BookWebView.EvaluateJavaScriptAsync(js).ConfigureAwait(true);
      if (TryParseJsNumber(s, out double r01))
        ApplyVerticalReadingStateFromScrollGeometryRatio(r01);
    }
    catch { }
  }

  /// <summary>Границы главы по полному тексту для вертикального «грубого» скролла перед точным якорём.</summary>
  void GetVerticalRevealChapterBounds(long bookOffset, out long chapterStart, out long chapterEndExclusive)
  {
    chapterStart = 0;
    chapterEndExclusive = Math.Max(1, _fullText.Length);
    if (string.IsNullOrEmpty(_fullText))
      return;
    bookOffset = Math.Clamp(bookOffset, 0, Math.Max(0, _fullText.Length - 1));
    if (_chapters.Count <= 0)
    {
      chapterStart = 0;
      chapterEndExclusive = Math.Max(1, _fullText.Length);
      return;
    }
    int ix = Math.Clamp(FindChapterIndexByOffset(bookOffset), 0, _chapters.Count - 1);
    var ch = _chapters[ix];
    chapterStart = ch.Start;
    chapterEndExclusive = Math.Max(ch.Start + 1, ch.End);
  }

  /// <summary>Вертикаль после jump (заметка): coarse scroll по главе + несколько точных __readerScrollToBookOffset.</summary>
  Task EvaluateVerticalRevealToBookOffsetAsync(long bookOffset) =>
      MainThread.InvokeOnMainThreadAsync(async () =>
      {
        if (BookWebView == null || string.IsNullOrEmpty(_fullText))
          return;
        GetVerticalRevealChapterBounds(bookOffset, out long cs, out long ce);
        string js = ReadingHtmlBuilder.MakeVerticalRevealBookOffsetJavaScript(bookOffset, cs, ce);
        try
        {
          await BookWebView.EvaluateJavaScriptAsync(js).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
          Debug.WriteLine($"[ReadingPage] EvaluateVerticalRevealToBookOffset: {ex.Message}");
          await ScrollWebToRatioAsync(_currentPageScrollRatio).ConfigureAwait(true);
          try
          {
            await BookWebView.EvaluateJavaScriptAsync(ReadingHtmlBuilder.MakeScrollToBookOffsetJavaScript(bookOffset)).ConfigureAwait(true);
          }
          catch { }
        }
      });

  /// <summary>Переход книги на абсолютное смещение символа: обновляет индекс главы/страницы, перерисовывает и при необходимости сохраняет точный offset без повторной выборки позиции из DOM.</summary>
  async Task ScrollToFullTextOffsetAsync(long startFullOffset, bool persistExactReadingOffset = false)
  {
    if (_currentBook == null || string.IsNullOrEmpty(_fullText))
      return;
    startFullOffset = Math.Clamp(startFullOffset, 0, Math.Max(0, _fullText.Length - 1));
    await MainThread.InvokeOnMainThreadAsync(async () =>
    {
      // Якорь в DOM общий для обоих режимов; pending обязателен до Navigated, даже если флаг WebView ещё не обновлён.
      _pendingWebScrollToBookOffset = startFullOffset;
      if (_isVerticalMode && persistExactReadingOffset)
        ArmVerticalPollAnchorSuppressionForPreciseScroll();
      if (_isVerticalMode)
        _pendingHorizontalColumnRestoreOffset = null;
      else
        _pendingHorizontalColumnRestoreOffset = startFullOffset;
      await EnsureHorizontalPagesMeasuredAsync().ConfigureAwait(true);
      _currentChapterIndex = FindChapterIndexByOffset(startFullOffset);
      if (_isVerticalMode)
      {
        int ci = Math.Clamp(_currentChapterIndex, 0, Math.Max(0, _chapters.Count - 1));
        _currentChapterIndex = ci;
        if (_chapters.Count > 0)
        {
          var ch = _chapters[ci];
          long span = ch.End - ch.Start;
          if (span <= 0)
            _currentPageScrollRatio = 0;
          else
          {
            long within = Math.Clamp(startFullOffset - ch.Start, 0, span - 1);
            _currentPageScrollRatio = (double)within / span;
          }
        }
        else
          _currentPageScrollRatio = _fullText.Length > 1
              ? (double)startFullOffset / (_fullText.Length - 1)
              : 0;
        _pageCount = 1;
        _verticalViewportPageIndex = 0;
        _verticalViewportPageCount = 1;
        if (_chapters.Count > 0
            && _lastRenderedVerticalChapterIndex >= 0
            && _lastRenderedVerticalChapterIndex != ci)
          _lastRenderedVerticalChapterIndex = -1;
      }
      else
      {
        _currentPageIndex = FindHorizontalPageIndexByOffset(startFullOffset);
        int pi = Math.Clamp(_currentPageIndex, 0, Math.Max(0, _horizontalPages.Count - 1));
        _currentPageIndex = pi;
        _currentPageStartOffset = _horizontalPages[pi].Start;
        long pageEnd = _horizontalPages[pi].End;
        _currentPageTextLength = Math.Max(1, pageEnd - _currentPageStartOffset);
        long withinOffset = startFullOffset - _currentPageStartOffset;
        _currentPageScrollRatio = _currentPageTextLength > 0
            ? Math.Clamp((double)withinOffset / _currentPageTextLength, 0, 1)
            : 0;
        _currentChapterIndex = FindChapterIndexByOffset(startFullOffset);
      }

      await RenderCurrentPageAsync(scrollToRatio: _currentPageScrollRatio).ConfigureAwait(true);
      if (!_isVerticalMode && _readingWithWebView && BookWebView != null && _horizontalColumnDocLoaded
          && _pendingWebScrollToBookOffset is long remScroll)
      {
        _pendingWebScrollToBookOffset = null;
        _pendingHorizontalColumnRestoreOffset = null;
        await HorizontalNavigateToBookOffsetAsync(remScroll).ConfigureAwait(true);
      }
      await SaveReadingStateAsync(
          syncHorizontalDomFromScroll: !_isVerticalMode && !persistExactReadingOffset,
          exactCharacterOffset: persistExactReadingOffset ? startFullOffset : null).ConfigureAwait(true);
    }).ConfigureAwait(true);
  }

  /// <summary>Обработчик кнопки «Закрыть» в редакторе заметки: скрывает оверлей и снимает фокус с полей ввода.</summary>
  async void OnNoteEditorCloseClicked(object sender, EventArgs e) => await CloseNoteEditorAsync();

  /// <summary>Закрывает оверлей заметки и при вертикальном WebView сразу синхронизирует подпись прогресса.</summary>
  async Task CloseNoteEditorAsync()
  {
    if (NoteEditorOverlay == null)
      return;
    NoteEditorOverlay.IsVisible = false;
    NoteEditorOverlay.InputTransparent = true;
    if (NoteTitleEntry != null)
      NoteTitleEntry.Unfocus();
    if (NoteCommentEditor != null)
      NoteCommentEditor.Unfocus();
    if (_isVerticalMode && _readingWithWebView && BookWebView != null)
      await RefreshVerticalReadingPageLabelAfterNoteOverlayDismissAsync().ConfigureAwait(true);
  }

  /// <summary>Валидирует ввод и сохраняет новую заметку в БД при нажатии «Сохранить» оверлея.</summary>
  async void OnNoteEditorSaveClicked(object sender, EventArgs e)
  {
    if (_currentBook == null || string.IsNullOrEmpty(_fullText))
      return;
    string title = NoteTitleEntry?.Text?.Trim() ?? "";
    if (string.IsNullOrEmpty(title))
    {
      var notify = ServiceLocator.Get<IAppNotificationService>();
      notify?.Show(
          Strings.Notes_TitleRequiredMessage,
          AppNotificationSeverity.Error,
          TimeSpan.FromSeconds(5));
      return;
    }
    string comment = NoteCommentEditor?.Text?.Trim() ?? "";
    if (title.Length > 200)
      title = title[..200];
    if (comment.Length > 8000)
      comment = comment[..8000];

    long off = Math.Clamp(_noteEditorAnchorOffset, 0, Math.Max(0, _fullText.Length - 1));

    var note = new Note
    {
      CardId = _currentBook.Id,
      WorkId = _currentBook.WorkId,
      Title = title ?? "",
      Comment = comment ?? "",
      CharacterOffset = off,
      CreatedDate = DateTime.Now
    };

    try
    {
      await _db.SaveNoteAsync(note).ConfigureAwait(true);
      await CloseNoteEditorAsync().ConfigureAwait(true);
      var notify = ServiceLocator.Get<IAppNotificationService>();
      notify?.Show(Strings.Notes_SavedSuccess, AppNotificationSeverity.Success, TimeSpan.FromSeconds(3));
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[ReadingPage] Save note: {ex.Message}");
      await MainThread.InvokeOnMainThreadAsync(async () =>
          await ThemedOverlayPresenter.ShowAlertAsync(this, Strings.Common_Error, ex.Message, Strings.Common_OK)).ConfigureAwait(true);
    }
  }

  /// <summary>Вход в загрузку книги после выбора карточки: опционально показывает тост о завершённом переводе, затем полная загрузка файла.</summary>
  async Task LoadBookContentWithOptionalFirstOpenTipAsync()
  {
    await TryConsumeFirstOpenTranslationTipAsync(_currentBook).ConfigureAwait(false);
    await LoadBookContentAsync().ConfigureAwait(false);
  }

  /// <summary>Считывает сохранённый в Preferences флаг первого открытия перевода и показывает уведомление с контекстом исходного издания.</summary>
  async Task TryConsumeFirstOpenTranslationTipAsync(Card? card)
  {
    if (card == null || card.Id <= 0)
      return;
    try
    {
      string raw = Preferences.Get(AppNotificationService.PrefFirstOpenTranslationTipCardIdKey, "");
      if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tipId) || tipId != card.Id)
        return;

      Preferences.Remove(AppNotificationService.PrefFirstOpenTranslationTipCardIdKey);

      var siblings = await _db.GetCardsByWorkIdAsync(card.WorkId).ConfigureAwait(false);
      string sourceTitle = siblings
          .Where(c => c.Id != card.Id)
          .OrderBy(c => c.Id)
          .FirstOrDefault()?.Title?.Trim() ?? "";
      var targetLang = BookLanguageStorage.FromStored(card.Language);

      await MainThread.InvokeOnMainThreadAsync(() =>
          ServiceLocator.Get<IAppNotificationService>()?.ShowTranslationCompleteOpenBook(card.Id, sourceTitle, targetLang));
    }
    catch (Exception ex)
    {
      Debug.WriteLine($"[ReadingPage] FirstOpenTranslationTip: {ex.Message}");
    }
  }

  /// <summary>Полный пайплайн открытия: работа/карточка, позиция чтения, извлечение текста, настройки, рендер и обработка пустого текста.</summary>
  private async Task LoadBookContentAsync()
  {
    if (_currentBook == null || string.IsNullOrEmpty(_currentBook.FilePath))
      return;

    try
    {
      _isRendering = true;
      StopHorizontalPageIndexSyncTimer();
      _verticalFullBookLayoutValid = false;
      _lastAppliedTextSettingsSnapshot = null;
      InvalidatePaginationForLayout();
      await MainThread.InvokeOnMainThreadAsync(() =>
          ShowReadingLoadingUi(messageOverride: _pendingOpenNoteAfterLoad != null ? Strings.Reading_OpeningNotePosition : null));

      _currentWork = await _db.GetWorkByIdAsync(_currentBook.WorkId);
      if (_currentWork != null)
      {
        if (_currentWork.ReadingStatus == BookStatus.New)
          _currentWork.ReadingStatus = BookStatus.InProgress;
        await _db.UpdateWorkAsync(_currentWork);
        MainPageViewModel.Instance?.ApplyWorkToAllLanguageVersions(_currentWork);
      }

      _currentBook.LastOpened = DateTime.Now;
      await _db.UpdateCardAsync(_currentBook);
      MainPageViewModel.Instance?.ApplyLastOpenedToCard(_currentBook.Id, _currentBook.LastOpened);

      _currentReadingPosition = await _db.GetReadingPositionByCardIdAsync(_currentBook.Id);
      if (_currentReadingPosition == null)
      {
        _currentReadingPosition = new ReadingPosition
        {
          CardId = _currentBook.Id,
          CharacterOffset = 0,
          LastUpdated = DateTime.Now
        };
        await _db.SaveReadingPositionAsync(_currentReadingPosition);
      }

      await LoadFullTextAndStructureAsync(_currentBook.FilePath, _currentBook.Format);
      if (string.IsNullOrWhiteSpace(_fullText))
        _fullText = string.Empty;

      var textSettings = await _db.GetTextSettingsAsync();
      ApplyTextSettingsFromModel(textSettings);

      long extractedTotalChars = _fullText.Length;
      if (extractedTotalChars <= 0)
      {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
          HideReadingLoadingUi();
          StopWebScrollTimer();
          _readingWithWebView = false;
          if (BookWebView != null) BookWebView.IsVisible = false;
          if (BookScrollView != null)
          {
            BookScrollView.IsVisible = true;
            BookScrollView.InputTransparent = false;
          }
          SentencesLayout.Children.Clear();
          SentencesLayout.Children.Add(new Label
          {
            Text = Strings.Reading_Error_LoadBookText,
            TextColor = ResolveThemeColor("MainTextColor", Colors.Black),
            FontSize = 16,
            LineHeight = 1.6
          });
        });
        return;
      }

      // Обновляем базовую длину в карточке и в локальной переменной для корректного восстановления прогресса
      long previousCardTotalChars = Math.Max(0, _currentBook.TotalChars);
      if (_currentBook.TotalChars != extractedTotalChars)
      {
        _currentBook.TotalChars = extractedTotalChars;
        var tsForPages = await _db.GetTextSettingsAsync();
        _currentBook.EstimatedPageCount = TextReadingLayout.ComputeEstimatedPageCountForCard(extractedTotalChars, tsForPages);
        await _db.UpdateCardAsync(_currentBook);
      }
      else if (_currentBook.EstimatedPageCount <= 0 && extractedTotalChars > 0)
      {
        var tsForPages = await _db.GetTextSettingsAsync();
        _currentBook.EstimatedPageCount = TextReadingLayout.ComputeEstimatedPageCountForCard(extractedTotalChars, tsForPages);
        await _db.UpdateCardAsync(_currentBook);
      }
      _dbTotalChars = extractedTotalChars;

      // Восстанавливаем позицию: абсолютное смещение; при смене длины текста — пропорция к previousCardTotalChars.
      long startFullOffset = 0;
      bool openedFromSavedNote = false;
      if (_pendingOpenNoteAfterLoad != null && _pendingOpenNoteAfterLoad.CardId == _currentBook.Id)
      {
        long maxIdxPending = Math.Max(0, extractedTotalChars - 1);
        startFullOffset = Math.Clamp(_pendingOpenNoteAfterLoad.CharacterOffset, 0, maxIdxPending);
        _pendingOpenNoteAfterLoad = null;
        openedFromSavedNote = true;
      }
      else if (_currentReadingPosition != null && extractedTotalChars > 0)
      {
        long co = _currentReadingPosition.CharacterOffset;
        if (co > 0 && co <= extractedTotalChars)
          startFullOffset = co;
        else if (co > 0 && previousCardTotalChars > 0 && previousCardTotalChars != extractedTotalChars)
        {
          long coClamped = Math.Min(co, previousCardTotalChars);
          double ratio = Math.Clamp((double)coClamped / previousCardTotalChars, 0, 1);
          long maxIdx = Math.Max(0, extractedTotalChars - 1);
          startFullOffset = maxIdx == 0 ? 0 : (long)Math.Round(ratio * maxIdx);
        }
        else if (co > extractedTotalChars)
          startFullOffset = 0;
      }
      startFullOffset = Math.Clamp(startFullOffset, 0, Math.Max(0, extractedTotalChars - 1));

      if (openedFromSavedNote)
      {
        _pendingWebScrollToBookOffset = startFullOffset;
        if (_isVerticalMode)
          ArmVerticalPollAnchorSuppressionForPreciseScroll();
      }

      if (_chapters.Count == 0)
        BuildChapterIndex();
      // Горизонтальный multicol: резерв на случай, если _pendingWebScrollToBookOffset обнулится до Navigated (вертикаль↔горизонталь).
      _pendingHorizontalColumnRestoreOffset = !_isVerticalMode ? startFullOffset : null;
      await EnsureHorizontalPagesMeasuredAsync();
      _currentChapterIndex = FindChapterIndexByOffset(startFullOffset);
      if (_isVerticalMode)
      {
        int ci = Math.Clamp(_currentChapterIndex, 0, Math.Max(0, _chapters.Count - 1));
        _currentChapterIndex = ci;
        if (_chapters.Count > 0)
        {
          var ch = _chapters[ci];
          long span = ch.End - ch.Start;
          if (span <= 0)
            _currentPageScrollRatio = 0;
          else
          {
            long within = Math.Clamp(startFullOffset - ch.Start, 0, span - 1);
            _currentPageScrollRatio = (double)within / span;
          }
        }
        else
          _currentPageScrollRatio = 0;
        _pageCount = 1;
        _verticalViewportPageIndex = 0;
        _verticalViewportPageCount = 1;
      }
      else
      {
        _pageCount = 1;
        _currentPageIndex = FindHorizontalPageIndexByOffset(startFullOffset);
        int pi = Math.Clamp(_currentPageIndex, 0, Math.Max(0, _horizontalPages.Count - 1));
        _currentPageIndex = pi;
        _currentPageStartOffset = _horizontalPages[pi].Start;
        long pageEnd = _horizontalPages[pi].End;
        _currentPageTextLength = Math.Max(1, pageEnd - _currentPageStartOffset);
        long withinOffset = startFullOffset - _currentPageStartOffset;
        _currentPageScrollRatio = _currentPageTextLength > 0
            ? Math.Clamp((double)withinOffset / _currentPageTextLength, 0, 1)
            : 0;
        _currentChapterIndex = FindChapterIndexByOffset(startFullOffset);
      }

      await MainThread.InvokeOnMainThreadAsync(async () =>
      {
        await WaitForReadingLayerLayoutAsync();
        await RenderCurrentPageAsync(scrollToRatio: _currentPageScrollRatio);
        if (openedFromSavedNote)
          await SaveReadingStateAsync(
              syncHorizontalDomFromScroll: !_isVerticalMode,
              exactCharacterOffset: startFullOffset);
      });
      _ = Task.Run(async () =>
      {
        await Task.Delay(4500).ConfigureAwait(false);
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
          if (_pendingReadingLoadUi)
            await ScheduleHideReadingLoadingUiWhenReadyAsync(quick: false).ConfigureAwait(true);
        }).ConfigureAwait(false);
      });
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[ReadingPage] Ошибка загрузки текста: {ex}");
      await MainThread.InvokeOnMainThreadAsync(() =>
      {
        try
        {
          HideReadingLoadingUi();
          StopWebScrollTimer();
          _readingWithWebView = false;
          if (BookWebView != null) BookWebView.IsVisible = false;
          if (BookScrollView != null)
          {
            BookScrollView.IsVisible = true;
            BookScrollView.InputTransparent = false;
          }
          SentencesLayout.Children.Clear();
          SentencesLayout.Children.Add(new Label
          {
            Text = Strings.Reading_Error_OpenBookDetail,
            TextColor = ResolveThemeColor("MainTextColor", Colors.Black),
            FontSize = 16,
            LineHeight = 1.5
          });
        }
        catch { }
      });
    }
    finally
    {
      _isRendering = false;
    }
  }

  /// <summary>Обновляет подпись главы и заголовка книги в верхней панели (в том числе по DOM для горизонтального multicol).</summary>
  private void UpdateBookHeaders()
  {
    if (_currentBook != null)
    {
      if (ChapterLabel != null)
      {
        if (!_isVerticalMode && _horizontalColumnDocLoaded && _chapters.Count > 0)
          _ = ApplyHorizontalChapterHeaderFromDomAsync();
        else
        {
          int chIdx = Math.Clamp(_currentChapterIndex, 0, Math.Max(0, _chapters.Count - 1));
          if (_chapters.Count > 0 && chIdx >= 0 && chIdx < _chapters.Count)
          {
            var raw = _chapters[chIdx].Title?.Trim() ?? "";
            ChapterLabel.Text = string.IsNullOrWhiteSpace(raw) ? string.Format(Strings.Reading_ChapterNumberFormat, chIdx + 1) : raw;
          }
          else
            ChapterLabel.Text = string.Format(Strings.Reading_ChapterNumberFormat, Math.Max(1, _currentPageIndex + 1));
        }
      }

      if (BookTitleLabel != null)
        BookTitleLabel.Text = _currentBook.Title;
    }
  }

  /// <summary>По элементу под центром экрана WebView находит активную секцию главы для подписи в горизонтальном режиме.</summary>
  private async Task ApplyHorizontalChapterHeaderFromDomAsync()
  {
    if (ChapterLabel == null || BookWebView == null || !_horizontalColumnDocLoaded || _chapters.Count == 0)
      return;
    await Task.Delay(48).ConfigureAwait(true);
    const string js = @"(function(){var h=document.getElementById('hscroll');if(!h)return'-1';var cx=window.innerWidth*0.5;var cy=window.innerHeight*0.42;var el=document.elementFromPoint(cx,cy);if(!el)return'-1';var sec=el.closest('section.chapter-wrap');if(!sec||!sec.id)return'-1';var m=/^ch-(\d+)$/.exec(sec.id);return m?m[1]:'-1';})()";
    int chIdx = -1;
    try
    {
      string? r = await BookWebView.EvaluateJavaScriptAsync(js).ConfigureAwait(true);
      if (!string.IsNullOrWhiteSpace(r))
      {
        string t = r.Trim().Trim('"');
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
          chIdx = v;
      }
    }
    catch { }
    if (chIdx < 0 || chIdx >= _chapters.Count)
    {
      long off = ComputeCurrentFullTextOffset();
      chIdx = Math.Clamp(FindChapterIndexByOffset(off), 0, _chapters.Count - 1);
    }
    _currentChapterIndex = chIdx;
    await MainThread.InvokeOnMainThreadAsync(() =>
    {
      if (ChapterLabel == null) return;
      var raw = _chapters[chIdx].Title?.Trim() ?? "";
      ChapterLabel.Text = string.IsNullOrWhiteSpace(raw) ? string.Format(Strings.Reading_ChapterNumberFormat, chIdx + 1) : raw;
    });
  }

  /// <summary>Строит список глав по тексту книги через эвристику «chapter N» или целиком одну главу как fallback.</summary>
  private void BuildChapterIndex()
  {
    _chapters.Clear();
    if (string.IsNullOrWhiteSpace(_fullText))
    {
      _chapters.Add((string.Format(Strings.Reading_ChapterNumberFormat, 1), 0, 0));
      return;
    }

    var matches = Regex.Matches(_fullText, @"(?i)\b(?:глава|chapter)\s+\d+\b").Cast<Match>().ToList();
    if (matches.Count == 0)
    {
      _chapters.Add((string.Format(Strings.Reading_ChapterNumberFormat, 1), 0, _fullText.Length));
      return;
    }

    for (int i = 0; i < matches.Count; i++)
    {
      long start = matches[i].Index;
      long end = (i + 1 < matches.Count) ? matches[i + 1].Index : _fullText.Length;
      string title = matches[i].Value.Trim();
      if (string.IsNullOrWhiteSpace(title))
        title = string.Format(Strings.Reading_ChapterNumberFormat, i + 1);
      _chapters.Add((title, start, end));
    }
  }

  /// <summary>Возвращает индекс главы, содержащей символ <paramref name="offset"/> по полузакрытым интервалам <c>[Start, End)</c>.</summary>
  private int FindChapterIndexByOffset(long offset)
  {
    if (_chapters.Count == 0) return 0;
    if (offset < _chapters[0].Start)
      return 0;
    for (int i = 0; i < _chapters.Count; i++)
    {
      long st = _chapters[i].Start;
      long en = _chapters[i].End;
      // [Start, End) для всех глав; последний символ главы — End-1 (граница Start следующей главы — следующая глава).
      if (offset >= st && offset < en)
        return i;
    }
    return Math.Clamp(_chapters.Count - 1, 0, _chapters.Count - 1);
  }

  /// <summary>Пересоздаёт сегменты горизонтальных страниц в UI-потоке, если они ещё не рассчитаны.</summary>
  private async Task EnsureHorizontalPagesMeasuredAsync()
  {
    if (_horizontalPagesLayoutValid && _horizontalPages.Count > 0 && !string.IsNullOrEmpty(_fullText))
      return;
    await MainThread.InvokeOnMainThreadAsync(RebuildHorizontalPagesAsync);
  }

  /// <summary>Горизонтальный multicol: одна запись на всю книгу. Вертикальный режим: глава в WebView со скроллом — без measure-shell и нарезки текста.</summary>
  private async Task RebuildHorizontalPagesAsync()
  {
    _horizontalPages.Clear();
    if (string.IsNullOrEmpty(_fullText))
    {
      _horizontalPages.Add((0, 0));
      _horizontalPagesLayoutValid = true;
      return;
    }

    if (!_isVerticalMode)
    {
      _horizontalPages.Add((0, _fullText.Length));
      _horizontalPagesLayoutValid = true;
      return;
    }

    _horizontalPages.Add((0, Math.Max(1, _fullText.Length)));
    _horizontalPagesLayoutValid = true;
    await Task.CompletedTask.ConfigureAwait(true);
  }

  /// <summary>Вызов скрипта измерения: сколько символов текста помещается в одну страницу WebView с текущими стилями.</summary>
  private async Task<int> MeasureFittingLengthFromAsync(int start)
  {
    int remaining = _fullText.Length - start;
    if (remaining <= 0)
      return 0;
    if (BookWebView == null)
      return 1;
    int cap = Math.Min(remaining, MaxMeasureFragmentChars);
    string chunk = _fullText.Substring(start, cap);
    string json = chunk.Length > 4096
        ? await Task.Run(() => JsonSerializer.Serialize(chunk))
        : JsonSerializer.Serialize(chunk);
    string script = $"__measureFitMaxLen({json})";
    try
    {
      string? r = await BookWebView.EvaluateJavaScriptAsync(script).ConfigureAwait(true);
      if (string.IsNullOrWhiteSpace(r))
        return Math.Min(Math.Max(200, _charsPerPage), cap);
      string t = r.Trim().Trim('"');
      if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
        return Math.Clamp(v, 1, cap);
      if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        return Math.Clamp((int)Math.Round(d), 1, cap);
    }
    catch
    {
      // ignore
    }
    return Math.Min(Math.Max(200, _charsPerPage), cap);
  }

  /// <summary>Простое разбиение текста при недоступности JS-измерителя через <see cref="_charsPerPage"/>.</summary>
  private void FallbackSplitPagesByCharCount()
  {
    SplitHorizontalPagesByCharCap(Math.Max(400, _charsPerPage));
  }

  /// <summary>Нарезает плоский текст на страницы с ограничением по символам и границам слов.</summary>
  private void SplitHorizontalPagesByCharCap(int cap)
  {
    _horizontalPages.Clear();
    if (string.IsNullOrEmpty(_fullText))
    {
      _horizontalPages.Add((0, 0));
      return;
    }
    cap = Math.Max(200, cap);
    int start = 0;
    while (start < _fullText.Length)
    {
      int rawEnd = Math.Min(start + cap, _fullText.Length);
      int end = rawEnd;
      if (rawEnd < _fullText.Length && !char.IsWhiteSpace(_fullText[rawEnd - 1]))
      {
        int safe = _fullText.LastIndexOf(' ', rawEnd - 1, rawEnd - start);
        if (safe > start + 50)
          end = safe;
      }
      if (end <= start) end = rawEnd;
      end = TrimPageEndToWordBoundary(start, Math.Min(end, _fullText.Length));
      if (end <= start)
        end = Math.Min(start + 1, _fullText.Length);
      _horizontalPages.Add((start, end));
      start = end;
    }
  }

  /// <summary>Сдвигает конец отрезка страницы к пробелу перед обрезанным местом для аккуратного переноса.</summary>
  private int TrimPageEndToWordBoundary(int start, int endExclusive)
  {
    if (endExclusive <= start || start >= _fullText.Length)
      return endExclusive;
    endExclusive = Math.Min(endExclusive, _fullText.Length);
    if (endExclusive <= start)
      return start;
    int search = endExclusive - 1;
    while (search > start && char.IsWhiteSpace(_fullText[search]))
      search--;
    if (search <= start)
      return Math.Min(start + 1, _fullText.Length);
    int space = _fullText.LastIndexOfAny(new[] { ' ', '\t', '\n', '\r', '\u00A0' }, search - 1, search - start);
    if (space < start)
      return Math.Min(start + 1, _fullText.Length);
    int nb = space + 1;
    while (nb < endExclusive && nb < _fullText.Length && char.IsWhiteSpace(_fullText[nb]))
      nb++;
    return nb;
  }

  /// <summary>Находит индекс сегмента страницы (или аппроксимацию для multicol через <see cref="_pageCount"/>).</summary>
  private int FindHorizontalPageIndexByOffset(long offset)
  {
    if (_horizontalPages.Count == 0) return 0;
    if (string.IsNullOrEmpty(_fullText)) return 0;
    offset = Math.Clamp(offset, 0, _fullText.Length);
    if (!_isVerticalMode && _pageCount > 1)
    {
      long denom = Math.Max(1, _fullText.Length - 1);
      long o = Math.Clamp(offset, 0, denom);
      double ratio = o / (double)denom;
      return (int)Math.Clamp(Math.Round(ratio * (_pageCount - 1)), 0, _pageCount - 1);
    }
    for (int i = 0; i < _horizontalPages.Count; i++)
    {
      if (offset >= _horizontalPages[i].Start && offset < _horizontalPages[i].End)
        return i;
    }
    if (offset >= _horizontalPages[^1].End)
      return _horizontalPages.Count - 1;
    for (int i = 0; i < _horizontalPages.Count - 1; i++)
    {
      if (offset >= _horizontalPages[i].End && offset < _horizontalPages[i + 1].Start)
        return i + 1;
    }
    return Math.Clamp(_horizontalPages.Count - 1, 0, _horizontalPages.Count - 1);
  }

  /// <summary>Синхронизирует <see cref="_currentChapterIndex"/> и <see cref="_currentPageScrollRatio"/> с абсолютным смещением (вертикальный WebView / DOM).</summary>
  void ApplyVerticalReadingStateFromFullTextOffset(long startFullOffset)
  {
    if (string.IsNullOrEmpty(_fullText))
      return;
    startFullOffset = Math.Clamp(startFullOffset, 0, Math.Max(0, _fullText.Length - 1));
    _currentChapterIndex = FindChapterIndexByOffset(startFullOffset);
    if (_chapters.Count > 0)
    {
      int ci = Math.Clamp(_currentChapterIndex, 0, _chapters.Count - 1);
      _currentChapterIndex = ci;
      var ch = _chapters[ci];
      long span = ch.End - ch.Start;
      if (span <= 0)
        _currentPageScrollRatio = 0;
      else
      {
        long within = Math.Clamp(startFullOffset - ch.Start, 0, span - 1);
        _currentPageScrollRatio = (double)within / span;
      }
    }
    else
      _currentPageScrollRatio = _fullText.Length > 1
          ? (double)startFullOffset / (_fullText.Length - 1)
          : 0;
  }

  /// <summary>
  /// Вертикальный WebView: доля скролла y/(doc-h) для видимого документа (одна глава или целиком книга без оглавления).
  /// Выравниваем её с главой через <see cref="_currentChapterIndex"/> — когда DOM-<c>data-bo</c> недоступен, приложение всё же знает позицию.
  /// </summary>
  void ApplyVerticalReadingStateFromScrollGeometryRatio(double ratio01)
  {
    if (string.IsNullOrEmpty(_fullText))
      return;
    ratio01 = Math.Clamp(ratio01, 0, 1);
    if (_chapters.Count == 0)
    {
      long len = _fullText.Length;
      long within = len <= 1 ? 0 : (long)Math.Round(ratio01 * (len - 1));
      _currentPageScrollRatio = len > 1 ? (double)within / (len - 1) : 0;
      return;
    }
    int ci = Math.Clamp(_currentChapterIndex, 0, _chapters.Count - 1);
    var ch = _chapters[ci];
    long span = Math.Max(1L, ch.End - ch.Start);
    long withinCh = (long)Math.Round(ratio01 * Math.Max(0, span - 1));
    withinCh = Math.Clamp(withinCh, 0, span - 1);
    _currentPageScrollRatio = (double)withinCh / span;
  }

  /// <summary>Текущая позиция в полном тексте книги (для страниц и % прочтения).</summary>
  private long ComputeCurrentFullTextOffset()
  {
    if (string.IsNullOrEmpty(_fullText))
      return 0;
    if (!_isVerticalMode)
    {
      long maxIx = Math.Max(0, _fullText.Length - 1);
      if (_readingWithWebView && _horizontalColumnDocLoaded && _horizontalDomBookAnchorCache is long hx)
        return Math.Clamp(hx, 0, maxIx);
      double p = (_currentPageIndex + _currentPageScrollRatio) / Math.Max(1, _pageCount);
      return (long)Math.Clamp(
          Math.Round(p * Math.Max(0, _fullText.Length - 1)),
          0,
          _fullText.Length - 1);
    }
    if (_chapters.Count == 0)
    {
      long len = Math.Max(1, _currentPageTextLength);
      long within = (long)Math.Round(len * _currentPageScrollRatio);
      return Math.Clamp(_currentPageStartOffset + within, 0, _fullText.Length);
    }
    int ci = Math.Clamp(_currentChapterIndex, 0, _chapters.Count - 1);
    var ch = _chapters[ci];
    long span = ch.End - ch.Start;
    if (span <= 0)
      return Math.Clamp(ch.Start, 0, Math.Max(0, _fullText.Length - 1));
    long withinCh = (long)Math.Round(span * _currentPageScrollRatio);
    withinCh = Math.Clamp(withinCh, 0, span - 1);
    return Math.Clamp(ch.Start + withinCh, ch.Start, Math.Max(ch.Start, ch.End - 1));
  }

  /// <summary>Пока WebView догоняет scroll после программного jump (заметка), не затирать долю главы из «верхней строки» DOM.</summary>
  void ArmVerticalPollAnchorSuppressionForPreciseScroll()
  {
    _suppressVerticalPollAnchorApplyUntilUtc = DateTime.UtcNow.AddMilliseconds(5600);
  }

  /// <summary>Символов на одну «книжную страницу» для подписи «N из M» и перехода по номеру — из настроек текста, без скачков при перерисовке WebView.</summary>
  private int GetGlobalBookPageStride() => Math.Max(1, _charsPerPage);

  /// <summary>Общее число глобальных страниц книги как ceil(len / stride).</summary>
  private static int ComputeTotalGlobalBookPages(long len, int stride)
  {
    stride = Math.Max(1, stride);
    return len <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(len / (double)stride));
  }

  /// <summary>Смещение в тексте, с которого начинается глобальная страница с номером page1Based (1-based).</summary>
  private static long OffsetForGlobalBookPage(int page1Based, int stride, long len)
  {
    if (len <= 0)
      return 0;
    stride = Math.Max(1, stride);
    int total = ComputeTotalGlobalBookPages(len, stride);
    page1Based = Math.Clamp(page1Based, 1, total);
    if (page1Based <= 1)
      return 0;
    long off = (long)(page1Based - 1) * stride;
    return Math.Clamp(off, 0, len - 1);
  }

  /// <summary>Глобальный номер страницы и всего страниц в книге (пропорционально смещению в символах).</summary>
  private void GetGlobalBookPageDisplay(out int page1Based, out int totalBookPages)
  {
    // Горизонтальное листание: номер страницы = индекс колонки в WebView (CSS columns), не «шаг в символах» из настроек.
    if (!_isVerticalMode && _readingWithWebView && _horizontalColumnDocLoaded && _pageCount > 0)
    {
      totalBookPages = Math.Max(1, _pageCount);
      page1Based = Math.Clamp(_currentPageIndex + 1, 1, totalBookPages);
      return;
    }

    long len = _fullText.Length;
    int stride = GetGlobalBookPageStride();
    totalBookPages = ComputeTotalGlobalBookPages(len, stride);
    long off = ComputeCurrentFullTextOffset();
    off = Math.Clamp(off, 0, Math.Max(0, len - 1));
    page1Based = len <= 0 ? 1 : Math.Clamp(1 + (int)(off / stride), 1, totalBookPages);
  }

  /// <summary>Та же метрика, что подпись «N из M»: последняя страница книги.</summary>
  private bool IsOnLastBookPage()
  {
    if (string.IsNullOrEmpty(_fullText))
      return false;
    GetGlobalBookPageDisplay(out int page1Based, out int totalBookPages);
    return totalBookPages > 0 && page1Based >= totalBookPages;
  }

  /// <summary>То же число страниц, что в подписи «N из M», записываем в карточку для главного экрана.</summary>
  private async Task PersistCurrentBookEstimatedPageCountToCardAsync()
  {
    if (_currentBook == null || string.IsNullOrEmpty(_fullText))
      return;
    try
    {
      GetGlobalBookPageDisplay(out _, out int totalBookPages);
      totalBookPages = Math.Max(1, totalBookPages);
      if (_currentBook.EstimatedPageCount == totalBookPages)
        return;
      _currentBook.EstimatedPageCount = totalBookPages;
      await _db.UpdateCardAsync(_currentBook);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[ReadingPage] PersistEstimatedPageCount: {ex.Message}");
    }
  }

  /// <summary>Восстанавливает смещения начала/длины «текущей страницы» по индексу и режиму (вертикаль по главам, горизонталь по книге).</summary>
  private void SyncCurrentPageBoundsFromPageIndex()
  {
    if (string.IsNullOrEmpty(_fullText) || _horizontalPages.Count == 0)
    {
      _currentPageStartOffset = 0;
      _currentPageTextLength = Math.Max(1, _fullText?.Length ?? 1);
      _currentChapterIndex = 0;
      return;
    }
    if (!_isVerticalMode)
    {
      _currentPageStartOffset = 0;
      _currentPageTextLength = Math.Max(1, _fullText.Length);
      long approx = _fullText.Length <= 1
          ? 0
          : (long)Math.Clamp(
              Math.Round(_fullText.Length * (_currentPageIndex / (double)Math.Max(1, _pageCount))),
              0,
              _fullText.Length - 1);
      _currentChapterIndex = FindChapterIndexByOffset(approx);
      return;
    }
    if (_chapters.Count == 0)
    {
      _currentPageStartOffset = 0;
      _currentPageTextLength = Math.Max(1, _fullText?.Length ?? 1);
      return;
    }
    int ci = Math.Clamp(_currentChapterIndex, 0, _chapters.Count - 1);
    var ch = _chapters[ci];
    _currentPageStartOffset = ch.Start;
    _currentPageTextLength = Math.Max(1, (int)(ch.End - ch.Start));
  }

  /// <summary>Записывает в UI строку прогресса «N из M» и включает/выключает кнопки листания в зависимости от режима.</summary>
  private void UpdatePageNumberLabel()
  {
    if (PageInfoLabel == null || string.IsNullOrEmpty(_fullText))
      return;
    if (_pendingReadingLoadUi)
      return;
    if (_isVerticalMode && _readingWithWebView && !_verticalPageNumbersSynced)
    {
      PageInfoLabel.Text = "";
      return;
    }
    if (!_isVerticalMode && _readingWithWebView && !_horizontalColumnDocLoaded)
    {
      PageInfoLabel.Text = "";
      return;
    }
    if (ReadingLayer == null || ReadingLayer.Width <= 1 || ReadingLayer.Height <= 1 || double.IsNaN(ReadingLayer.Width))
    {
      PageInfoLabel.Text = "";
      return;
    }
    GetGlobalBookPageDisplay(out int globalPg, out int globalTotal);
    PageInfoLabel.Text = string.Format(Strings.Reading_PageProgressFormat, globalPg, globalTotal);
    if (_isVerticalMode)
    {
      if (PrevPageButton != null)
      {
        PrevPageButton.IsEnabled = _chapters.Count > 0 && _currentChapterIndex > 0;
        PrevPageButton.Opacity = PrevPageButton.IsEnabled ? 1.0 : 0.35;
      }
      if (NextPageButton != null)
      {
        NextPageButton.IsEnabled = _chapters.Count > 0 && _currentChapterIndex < _chapters.Count - 1;
        NextPageButton.Opacity = NextPageButton.IsEnabled ? 1.0 : 0.35;
      }
      return;
    }
    if (!_horizontalColumnDocLoaded)
      _pageCount = Math.Max(1, _pageCount);

    // Горизонтальное листание: страница задаётся только жестами/кнопками, не опросом scrollY WebView
    int pageIdx = Math.Clamp(_currentPageIndex, 0, Math.Max(0, _pageCount - 1));

    if (PrevPageButton != null)
    {
      PrevPageButton.IsEnabled = pageIdx > 0;
      PrevPageButton.Opacity = PrevPageButton.IsEnabled ? 1.0 : 0.35;
    }
    if (NextPageButton != null)
    {
      NextPageButton.IsEnabled = _pageCount > 0 && pageIdx < _pageCount - 1;
      NextPageButton.Opacity = NextPageButton.IsEnabled ? 1.0 : 0.35;
    }
  }

  /// <summary>Собирает HTML читателя, назначает <see cref="BookWebView"/>, восстанавливает прокрутку того же документа или полную перезагрузку при смене режима/контента.</summary>
  /// <param name="syncBoundsFromPageIndex">false при переходе по оглавлению на середину «логической» страницы (срез с якоря главы).</param>
  private async Task RenderCurrentPageAsync(double scrollToRatio, bool syncBoundsFromPageIndex = true)
  {
    if (_currentBook == null || string.IsNullOrEmpty(_fullText))
      return;

    _isRendering = true;
    try
    {
      if (_chapters.Count == 0)
        BuildChapterIndex();
      await EnsureHorizontalPagesMeasuredAsync();
      if (_isVerticalMode)
        _currentChapterIndex = Math.Clamp(_currentChapterIndex, 0, Math.Max(0, _chapters.Count - 1));
      else if (_horizontalColumnDocLoaded && _pageCount > 0)
        _currentPageIndex = Math.Clamp(_currentPageIndex, 0, Math.Max(0, _pageCount - 1));
      else
        _currentPageIndex = Math.Clamp(_currentPageIndex, 0, Math.Max(0, _horizontalPages.Count - 1));
      if (syncBoundsFromPageIndex)
        SyncCurrentPageBoundsFromPageIndex();
      _currentPageScrollRatio = Math.Clamp(scrollToRatio, 0, 1);

      var fg = ColorToHex(_readingTextColor);
      var bg = ColorToHex(_readingBgColor);
      double pad = _readingMarginPx;
      string cssAlign = TextReadingLayout.AlignmentToCss(_paragraphTextAlignment);

      string? verticalHtmlBase = null;
      if (_isVerticalMode && _fullText.Length > 0 && _currentPageTextLength > 0)
      {
        int? onlyCh = _chapters.Count == 0 ? null : Math.Clamp(_currentChapterIndex, 0, _chapters.Count - 1);
        verticalHtmlBase = ReadingHtmlBuilder.BuildFullDocumentHtml(
            _fullText,
            _chapters,
            _fb2OrderedSections,
            _readingFontSize, cssAlign, fg, bg, pad,
            onlyChapterIndex: onlyCh,
            epubChapterHtmlBodies: _epubChapterHtmlBodies,
            enableHorizontalSwipeNavigation: false,
            horizontalColumnPaged: false);
        bool verticalSameDoc = _verticalFullBookLayoutValid
            && _lastRenderedVerticalChapterIndex == _currentChapterIndex
            && string.Equals(_lastCommittedReadingHtml, verticalHtmlBase, StringComparison.Ordinal);
        if (_readingWithWebView && !verticalSameDoc)
          _verticalPageNumbersSynced = false;
      }

      // Горизонталь: тот же HTML — только прокрутка колонок; до Clear/заголовков (листание остаётся отзывчивым).
      if (!_isVerticalMode
          && _fullText.Length > 0
          && _currentPageTextLength > 0
          && BookWebView != null
          && _horizontalColumnDocLoaded
          && string.Equals(
              _lastCommittedReadingHtml,
              ReadingHtmlBuilder.BuildFullDocumentHtml(
                  _fullText,
                  _chapters,
                  _fb2OrderedSections,
                  _readingFontSize,
                  cssAlign,
                  fg,
                  bg,
                  pad,
                  onlyChapterIndex: null,
                  epubChapterHtmlBodies: _epubChapterHtmlBodies,
                  enableHorizontalSwipeNavigation: true,
                  horizontalColumnPaged: true),
              StringComparison.Ordinal))
      {
        _readingWithWebView = true;
        if (BookScrollView != null)
        {
          BookScrollView.IsVisible = false;
          BookScrollView.InputTransparent = true;
        }
        BookWebView.IsVisible = true;
        BookWebView.Opacity = 1;
        if (_pendingWebScrollToBookOffset is long phSame)
        {
          _pendingWebScrollToBookOffset = null;
          _pendingHorizontalColumnRestoreOffset = null;
          await HorizontalNavigateToBookOffsetAsync(phSame).ConfigureAwait(true);
        }
        else
          await ScrollHorizontalColumnToPageAsync(_currentPageIndex, smooth: false).ConfigureAwait(true);
        _verticalFullBookLayoutValid = false;
        UpdatePageNumberLabel();
        UpdateBookHeaders();
        UpdateHorizontalPageTurnLayerVisibility();
        CompleteReadingLoadUiIfPending();
        return;
      }

      UpdateBookHeaders();
      UpdatePageNumberLabel();

      SentencesLayout.Children.Clear();

      if (_fullText.Length == 0 || _currentPageTextLength <= 0)
      {
        StopWebScrollTimer();
        _readingWithWebView = false;
        if (BookWebView != null) BookWebView.IsVisible = false;
        if (BookScrollView != null)
        {
          BookScrollView.IsVisible = true;
          BookScrollView.InputTransparent = false;
        }
        SentencesLayout.Children.Add(new Label
        {
          Text = Strings.Reading_EndOfBook,
          FontSize = 16,
          LineHeight = 1.6,
          TextColor = ResolveThemeColor("MainTextColor", Colors.Black)
        });
        UpdateHorizontalPageTurnLayerVisibility();
        return;
      }

      _readingWithWebView = true;
      if (BookScrollView != null)
      {
        BookScrollView.IsVisible = false;
        BookScrollView.InputTransparent = true;
      }
      if (BookWebView != null) BookWebView.IsVisible = true;

      string html;
      if (!_isVerticalMode)
      {
        html = ReadingHtmlBuilder.BuildFullDocumentHtml(
            _fullText,
            _chapters,
            _fb2OrderedSections,
            _readingFontSize, cssAlign, fg, bg, pad,
            onlyChapterIndex: null,
            epubChapterHtmlBodies: _epubChapterHtmlBodies,
            enableHorizontalSwipeNavigation: true,
            horizontalColumnPaged: true);
      }
      else
      {
        int? onlyCh = _chapters.Count == 0 ? null : Math.Clamp(_currentChapterIndex, 0, _chapters.Count - 1);
        html = verticalHtmlBase ?? ReadingHtmlBuilder.BuildFullDocumentHtml(
            _fullText,
            _chapters,
            _fb2OrderedSections,
            _readingFontSize, cssAlign, fg, bg, pad,
            onlyChapterIndex: onlyCh,
            epubChapterHtmlBodies: _epubChapterHtmlBodies,
            enableHorizontalSwipeNavigation: false,
            horizontalColumnPaged: false);
      }
      if (BookWebView != null)
      {
        if (_isVerticalMode
            && _verticalFullBookLayoutValid
            && _lastRenderedVerticalChapterIndex == _currentChapterIndex
            && string.Equals(_lastCommittedReadingHtml, html, StringComparison.Ordinal))
        {
          if (_pendingWebScrollToBookOffset is long pb)
          {
            _pendingWebScrollToBookOffset = null;
            _pendingHorizontalColumnRestoreOffset = null;
            try
            {
              await Task.Delay(50).ConfigureAwait(true);
              await EvaluateVerticalRevealToBookOffsetAsync(pb).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
              Debug.WriteLine($"[ReadingPage] same-doc scroll to book offset: {ex.Message}");
              await ScrollWebToRatioAsync(_currentPageScrollRatio).ConfigureAwait(true);
            }
          }
          else
            await ScrollWebToRatioAsync(_currentPageScrollRatio).ConfigureAwait(true);
          StartWebScrollTimerIfNeeded();
          await PollWebScrollAsync().ConfigureAwait(true);
          _verticalPageNumbersSynced = true;
          UpdatePageNumberLabel();
          UpdateBookHeaders();
          UpdateHorizontalPageTurnLayerVisibility();
          CompleteReadingLoadUiIfPending();
          return;
        }
        _horizontalColumnDocLoaded = false;
        _lastCommittedReadingHtml = html;
        _suppressWebViewPagingNavUntilUtc = DateTime.UtcNow.AddMilliseconds(520);
        bool android = DeviceInfo.Platform == DevicePlatform.Android;
        // Горизонталь: не показываем первый кадр WebView до готовности multicol (иначе «слипшийся» текст под оверлеем).
        if (!_isVerticalMode)
          BookWebView.Opacity = 0;
        // Уникальный хвост — WebView2 иногда не перерисовывает при уменьшении шрифта, если строка совпадает по кэшу.
        html += "\n<!-- reader-rev:"
            + _readingFontSize.ToString(CultureInfo.InvariantCulture) + ":"
            + _readingMarginPx.ToString(CultureInfo.InvariantCulture) + ":"
            + Environment.TickCount64.ToString(CultureInfo.InvariantCulture) + " -->\n";
        BookWebView.Source = new HtmlWebViewSource
        {
          Html = html,
          BaseUrl = ReadingHtmlBuilder.GetWebViewBaseUrl(android)
        };
        if (_isVerticalMode)
          _lastRenderedVerticalChapterIndex = _currentChapterIndex;
      }
      _verticalFullBookLayoutValid = false;
      UpdateHorizontalPageTurnLayerVisibility();
    }
    finally
    {
      _isRendering = false;
    }
  }

  /// <summary>Преобразует <see cref="Color"/> MAUI в строку <c>#RRGGBB</c> для HTML/CSS.</summary>
  private static string ColorToHex(Color c)
  {
    return $"#{(byte)(c.Red * 255):X2}{(byte)(c.Green * 255):X2}{(byte)(c.Blue * 255):X2}";
  }

  /// <summary>Считывает цвет из словаря ресурсов приложения по ключу (тема/палитра).</summary>
  private static Color ResolveThemeColor(string key, Color fallback)
  {
    try
    {
      var res = Application.Current?.Resources;
      if (res != null && res.TryGetValue(key, out var val) && val is Color c)
        return c;
    }
    catch { }
    return fallback;
  }

  /// <summary>Обрабатывает SPA-переходы из читателя: листание, скрытие панелей, запрос перевода предложения, отладочные URL.</summary>
  private void OnBookWebNavigating(object? sender, WebNavigatingEventArgs e)
  {
    if (e.Url == null) return;
    var u = e.Url;
    if (!u.StartsWith(ReadingHtmlBuilder.AppNavBaseUrl, StringComparison.OrdinalIgnoreCase))
      return;
    e.Cancel = true;
    if (u.Contains("hr-debug", StringComparison.OrdinalIgnoreCase))
    {
      var qi = u.IndexOf('?', StringComparison.Ordinal);
      if (qi >= 0 && qi + 1 < u.Length)
      {
        try
        {
          var json = Uri.UnescapeDataString(u.Substring(qi + 1));
          Debug.WriteLine("[HR layout] " + json);
        }
        catch (Exception ex)
        {
          Debug.WriteLine("[HR layout] decode error: " + ex.Message);
        }
      }
      return;
    }
    if (u.Contains("toggle-panels", StringComparison.OrdinalIgnoreCase))
    {
      MainThread.BeginInvokeOnMainThread(() => SetPanelsVisibility(!_panelsVisible));
      return;
    }
    bool pagingTap = u.Contains("pageprev", StringComparison.OrdinalIgnoreCase)
        || u.Contains("pagenext", StringComparison.OrdinalIgnoreCase);
    if (pagingTap && DateTime.UtcNow < _suppressWebViewPagingNavUntilUtc)
      return;
    // Suppress immediately so any duplicate URL navigation (synthetic click after touch-end)
    // is blocked before GoToPageAsync is even queued on the main thread.
    if (pagingTap)
      _suppressWebViewPagingNavUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
    if (u.Contains("pageprev", StringComparison.OrdinalIgnoreCase))
    {
      MainThread.BeginInvokeOnMainThread(() =>
      {
        if (_isVerticalMode)
          _ = GoToAdjacentVerticalChapterAsync(-1);
        else
          _ = GoToPageAsync(_currentPageIndex - 1);
      });
      return;
    }
    if (u.Contains("pagenext", StringComparison.OrdinalIgnoreCase))
    {
      MainThread.BeginInvokeOnMainThread(() =>
      {
        if (_isVerticalMode)
          _ = GoToAdjacentVerticalChapterAsync(1);
        else
          _ = GoToPageAsync(_currentPageIndex + 1);
      });
      return;
    }
    if (u.Contains("translate", StringComparison.OrdinalIgnoreCase))
    {
      ParseTranslateNavUrl(u, out var text, out int bookOffset, out int sentenceIndex);
      MainThread.BeginInvokeOnMainThread(() => RequestSentenceTranslation(text, bookOffset, sentenceIndex));
    }
  }

  /// <summary>Разбор <c>translate?text=…&amp;bo=…&amp;si=…</c> из WebView (bo — data-bo абзаца, si — номер предложения в абзаце).</summary>
  static void ParseTranslateNavUrl(string url, out string? text, out int bookOffset, out int sentenceIndex)
  {
    text = null;
    bookOffset = -1;
    sentenceIndex = -1;
    if (string.IsNullOrEmpty(url))
      return;
    int q = url.IndexOf('?');
    if (q < 0)
      return;
    foreach (var part in url.Substring(q + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
      int eq = part.IndexOf('=');
      if (eq <= 0)
        continue;
      var key = part[..eq];
      var val = part[(eq + 1)..];
      try
      {
        if (key.Equals("text", StringComparison.OrdinalIgnoreCase))
          text = Uri.UnescapeDataString(val);
        else if (key.Equals("bo", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(val, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int bo))
          bookOffset = bo;
        else if (key.Equals("si", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(val, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int si))
          sentenceIndex = si;
      }
      catch
      {
        // ignore malformed query parts
      }
    }
  }

  /// <summary>Запускает асинхронный перевод выбранного в WebView предложения.</summary>
  void RequestSentenceTranslation(string? sentence, int paragraphBookOffset = -1, int sentenceIndexInParagraph = -1)
  {
    _ = RunSentenceTranslationAsync(sentence, paragraphBookOffset, sentenceIndexInParagraph);
  }

  /// <summary>Вызывает <see cref="ISentenceTranslationService"/>, показывает оверлей результата или тост при ошибке/отмене.</summary>
  async Task RunSentenceTranslationAsync(string? sentence, int paragraphBookOffset = -1, int sentenceIndexInParagraph = -1)
  {
    if (string.IsNullOrWhiteSpace(sentence) || _currentBook == null)
      return;
    var svc = ServiceLocator.Get<ISentenceTranslationService>();
    var notify = ServiceLocator.Get<IAppNotificationService>();
    if (svc == null)
      return;

    await MainThread.InvokeOnMainThreadAsync(() =>
    {
      if (TranslationBusyIndicator != null)
      {
        TranslationBusyIndicator.IsVisible = true;
        TranslationBusyIndicator.IsRunning = true;
      }
      if (TranslationTextLabel != null)
        TranslationTextLabel.Text = "";
      if (TranslationOverlay != null)
      {
        TranslationOverlay.IsVisible = true;
        TranslationOverlay.InputTransparent = false;
      }
    });

    _sentenceTranslateCts?.Cancel();
    _sentenceTranslateCts?.Dispose();
    _sentenceTranslateCts = new CancellationTokenSource();
    var translateCt = _sentenceTranslateCts.Token;

    try
    {
      TranslationDiagnostics.Log(
          $"UI: запрос перевода, предпросмотр текста: {(sentence ?? "").Length} симв.");
      var ts = await _db.GetTextSettingsAsync();
      var eff = ComputeEffectiveTextSettings(ts);
      string targetLang = string.IsNullOrWhiteSpace(eff.TranslationLanguage) ? "ru" : eff.TranslationLanguage.Trim();
      string sourceLang = string.IsNullOrWhiteSpace(_currentBook.Language) ? "ru" : _currentBook.Language.Trim();
      // WebView задаёт точные границы предложения; не режем по пунктуации на C# — иначе остаётся только «до первой точки».
      sentence = SentenceAlignment.PrepareSelectionFromReader(sentence);
      var result = await svc.TranslateSentenceAsync(
          SentenceAlignment.TruncatePreservingTrailingPunctuation(sentence.Trim(), 8000),
          _currentBook,
          sourceLang,
          targetLang,
          translateCt,
          paragraphBookOffset,
          sentenceIndexInParagraph).ConfigureAwait(true);
      TranslationDiagnostics.Log(
          $"UI: готово — модалка={result.ShowTranslationModal}, тост={(result.ToastMessage != null ? "да" : "нет")}");

      await MainThread.InvokeOnMainThreadAsync(() =>
      {
        if (TranslationBusyIndicator != null)
        {
          TranslationBusyIndicator.IsRunning = false;
          TranslationBusyIndicator.IsVisible = false;
        }
        if (result.ShowTranslationModal && !string.IsNullOrEmpty(result.ModalText))
        {
          if (TranslationTextLabel != null)
            TranslationTextLabel.Text = result.ModalText;
        }
        else
        {
          if (TranslationOverlay != null)
          {
            TranslationOverlay.IsVisible = false;
            TranslationOverlay.InputTransparent = true;
          }
          ClearWebViewSentenceHighlightIfNeeded();
          if (!string.IsNullOrEmpty(result.ToastMessage))
            notify?.Show(result.ToastMessage, AppNotificationSeverity.Warning, TimeSpan.FromSeconds(6));
        }
      });
    }
    catch (OperationCanceledException)
    {
      await MainThread.InvokeOnMainThreadAsync(() =>
      {
        if (TranslationBusyIndicator != null)
        {
          TranslationBusyIndicator.IsRunning = false;
          TranslationBusyIndicator.IsVisible = false;
        }
        if (TranslationOverlay != null)
        {
          TranslationOverlay.IsVisible = false;
          TranslationOverlay.InputTransparent = true;
        }
        ClearWebViewSentenceHighlightIfNeeded();
        notify?.Show(TranslationMessages.CancelledOrTimeout, AppNotificationSeverity.Info, TimeSpan.FromSeconds(4));
      });
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[ReadingPage] RunSentenceTranslationAsync: {ex.Message}");
      await MainThread.InvokeOnMainThreadAsync(() =>
      {
        if (TranslationBusyIndicator != null)
        {
          TranslationBusyIndicator.IsRunning = false;
          TranslationBusyIndicator.IsVisible = false;
        }
        if (TranslationOverlay != null)
        {
          TranslationOverlay.IsVisible = false;
          TranslationOverlay.InputTransparent = true;
        }
        ClearWebViewSentenceHighlightIfNeeded();
        notify?.Show(TranslationMessages.OnlineFailure, AppNotificationSeverity.Warning, TimeSpan.FromSeconds(6));
      });
    }
  }

  /// <summary>После загрузки HTML в WebView: вертикальная прокрутка к якорю, горизонтальный multicol, снятие оверлея загрузки, запуск таймеров опроса.</summary>
  private async void OnBookWebNavigated(object? sender, WebNavigatedEventArgs e)
  {
    if (e.Result != WebNavigationResult.Success) return;
    if (_paginationMeasureMode)
    {
      _measureShellTcs?.TrySetResult(true);
      return;
    }
    if (!_readingWithWebView) return;
    if (_isVerticalMode)
    {
      _verticalFullBookLayoutValid = true;
      StopHorizontalPageIndexSyncTimer();
      if (_pendingWebScrollToBookOffset is long pv)
      {
        _pendingWebScrollToBookOffset = null;
        _pendingHorizontalColumnRestoreOffset = null;
        try
        {
          await Task.Delay(130).ConfigureAwait(true);
          await EvaluateVerticalRevealToBookOffsetAsync(pv).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
          Debug.WriteLine($"[ReadingPage] scroll to book offset: {ex.Message}");
          await ScrollWebToRatioAsync(_currentPageScrollRatio);
        }
      }
      else
        await ScrollWebToRatioAsync(_currentPageScrollRatio);
      StartWebScrollTimerIfNeeded();
      await Task.Delay(150).ConfigureAwait(true);
      await PollWebScrollAsync();
    }
    else
    {
      StopWebScrollTimer();
      if (BookWebView != null)
      {
        // clientWidth=0 до измерения — ждём ширину до первого __hrPageCount; флаг «док загружен» — после первого reflow (realign при resize до этого отключён).
        await MainThread.InvokeOnMainThreadAsync(async () => await WaitForHorizontalReaderWidthForLayoutAsync().ConfigureAwait(true)).ConfigureAwait(true);
        await RefreshHorizontalColumnPageCountAsync().ConfigureAwait(true);
        _horizontalColumnDocLoaded = true;
        long? preciseBookScroll = null;
        if (_pendingWebScrollToBookOffset is long pwt)
        {
          preciseBookScroll = pwt;
          _pendingWebScrollToBookOffset = null;
          _pendingHorizontalColumnRestoreOffset = null;
        }
        else if (_pendingHorizontalColumnRestoreOffset is long pbak)
        {
          preciseBookScroll = pbak;
          _pendingHorizontalColumnRestoreOffset = null;
        }

        if (_pendingHorizontalChapterJumpAfterLoad is int chJump)
        {
          _pendingHorizontalChapterJumpAfterLoad = null;
          await JumpHorizontalColumnToChapterAsync(chJump).ConfigureAwait(true);
          await RefreshHorizontalColumnPageIndexFromScrollAsync().ConfigureAwait(true);
        }
        else if (preciseBookScroll is long pScroll)
        {
          await Task.Delay(95).ConfigureAwait(true);
          await HorizontalNavigateToBookOffsetAsync(pScroll).ConfigureAwait(true);
        }
        else
          await ScrollHorizontalColumnToPageAsync(_currentPageIndex, smooth: false).ConfigureAwait(true);

        UpdatePageNumberLabel();
        UpdateBookHeaders();
      }
    }
    await MainThread.InvokeOnMainThreadAsync(() => TryUpdateReadingSystemBars()).ConfigureAwait(true);
    await ScheduleHideReadingLoadingUiWhenReadyAsync(quick: false);
  }

  /// <summary>Вызывает JS-хелпер перемотки горизонтального читателя к индексу колонки-страницы с опцией smooth.</summary>
  private async Task ScrollHorizontalColumnToPageAsync(int pageIndex, bool smooth = false)
  {
    if (BookWebView == null) return;
    string sm = smooth ? "true" : "false";
    string js = $"(function(){{if(window.__hrSetPageIndex)window.__hrSetPageIndex({pageIndex},{sm});}})()";
    try
    {
      await BookWebView.EvaluateJavaScriptAsync(js).ConfigureAwait(true);
    }
    catch { }
  }

  /// <summary>Горизонталь: приблизить scrollLeft по доле символа в книге, затем точный <c>__readerScrollToBookOffset</c>.</summary>
  async Task HorizontalNavigateToBookOffsetAsync(long bookOffset)
  {
    if (BookWebView == null || !_horizontalColumnDocLoaded || _isVerticalMode)
      return;
    bookOffset = Math.Clamp(bookOffset, 0, Math.Max(0, _fullText.Length - 1));
    try
    {
      await BookWebView.EvaluateJavaScriptAsync(
              ReadingHtmlBuilder.MakeHorizontalCoarseSnapToBookOffsetJavaScript(bookOffset))
          .ConfigureAwait(true);
    }
    catch (Exception ex)
    {
      Debug.WriteLine($"[ReadingPage] HorizontalCoarseSnap: {ex.Message}");
    }
    await Task.Delay(155).ConfigureAwait(true);
    await ApplyHorizontalScrollToBookOffsetWithRetriesAsync(bookOffset).ConfigureAwait(true);
  }

  /// <summary>Горизонтальный multicol: после смены вертикаль↔горизонталь или первого reflow точная прокрутка к символу иногда нужна повторно.</summary>
  async Task ApplyHorizontalScrollToBookOffsetWithRetriesAsync(long bookOffset)
  {
    if (BookWebView == null || !_horizontalColumnDocLoaded || _isVerticalMode)
      return;
    bookOffset = Math.Clamp(bookOffset, 0, Math.Max(0, _fullText.Length - 1));
    string js = ReadingHtmlBuilder.MakeScrollToBookOffsetJavaScript(bookOffset);
    for (int attempt = 0; attempt < 6; attempt++)
    {
      if (attempt > 0)
        await Task.Delay(90 + 85 * attempt).ConfigureAwait(true);
      try
      {
        int wPx = GetHorizontalReaderNativeWidthPx();
        if (attempt == 0 && wPx > 0)
        {
          string injectSync =
              "(function(){window.__readerNativeWidthPx="
              + wPx.ToString(CultureInfo.InvariantCulture)
              + ";if(window.__syncHorizontalReaderLayout)window.__syncHorizontalReaderLayout();"
              + "if(window.__hrReflowPagePos)window.__hrReflowPagePos();})()";
          await BookWebView.EvaluateJavaScriptAsync(injectSync).ConfigureAwait(true);
          await Task.Delay(45).ConfigureAwait(true);
        }
        await BookWebView.EvaluateJavaScriptAsync(js).ConfigureAwait(true);
        await Task.Delay(115).ConfigureAwait(true);
        await RefreshHorizontalColumnPageIndexFromScrollAsync().ConfigureAwait(true);
      }
      catch (Exception ex)
      {
        Debug.WriteLine($"[ReadingPage] ApplyHorizontalScrollToBookOffset retry {attempt}: {ex.Message}");
      }
    }
  }

  /// <summary>Снять подсветку предложения в WebView (mark), когда модалка перевода закрыта или запрос отменён.</summary>
  private void ClearWebViewSentenceHighlightIfNeeded()
  {
    if (!_readingWithWebView || BookWebView == null) return;
    try
    {
      _ = BookWebView.EvaluateJavaScriptAsync(ReadingHtmlBuilder.ClearReaderSentenceHighlightJavaScript);
    }
    catch (Exception ex)
    {
      Debug.WriteLine("[ReadingPage] ClearWebViewSentenceHighlight: " + ex.Message);
    }
  }

  /// <summary>Один запрос к DOM: число колонок-страниц и текущий индекс по scrollLeft (без рассинхрона count/index).</summary>
  private async Task RefreshHorizontalColumnPageStateFromDomAsync(bool leadingLayoutDelay = true)
  {
    if (BookWebView == null) return;
    if (leadingLayoutDelay)
      await Task.Delay(120).ConfigureAwait(true);
    await MainThread.InvokeOnMainThreadAsync(async () =>
    {
      if (BookWebView == null) return;
      int wPx = GetHorizontalReaderNativeWidthPx();
      if (wPx > 0)
      {
        string inject =
            "(function(){window.__readerNativeWidthPx=" + wPx.ToString(CultureInfo.InvariantCulture)
            + ";if(window.__syncHorizontalReaderLayout)window.__syncHorizontalReaderLayout();"
            + "if(window.__hrReflowPagePos)window.__hrReflowPagePos();})()";
        try
        {
          await BookWebView.EvaluateJavaScriptAsync(inject).ConfigureAwait(true);
        }
        catch { }
      }

      const string js =
          "(function(){"
          + "if(typeof window.__hrPageCount!=='function'||typeof window.__hrGetPageIndex!=='function')return '0|1';"
          + "var cnt=window.__hrPageCount();var idx=window.__hrGetPageIndex();"
          + "return String(idx)+'|'+String(cnt);})()";
      try
      {
        string? r = await BookWebView.EvaluateJavaScriptAsync(js).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(r)) return;
        string t = r.Trim().Trim('"');
        int pipe = t.IndexOf('|', StringComparison.Ordinal);
        if (pipe < 0) return;
        if (!int.TryParse(t.AsSpan(0, pipe), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
          return;
        if (!int.TryParse(t.AsSpan(pipe + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cnt))
          return;
        _pageCount = Math.Max(1, cnt);
        _currentPageIndex = Math.Clamp(idx, 0, Math.Max(0, _pageCount - 1));
      }
      catch { }
    }).ConfigureAwait(true);
  }

  /// <summary>Совмещённые refresh count+index после inject (обёртка над <see cref="RefreshHorizontalColumnPageStateFromDomAsync"/>).</summary>
  private async Task RefreshHorizontalColumnPageCountAsync()
  {
    await RefreshHorizontalColumnPageStateFromDomAsync().ConfigureAwait(true);
  }

  /// <summary>Синхронизирует индекс текущей колонки с <c>scrollLeft</c> без лишней задержки перед измерением.</summary>
  private async Task RefreshHorizontalColumnPageIndexFromScrollAsync()
  {
    await RefreshHorizontalColumnPageStateFromDomAsync(leadingLayoutDelay: false).ConfigureAwait(true);
  }

  /// <summary>Синхронизация индекса/числа страниц по текущему скроллу без reflowPagePos (для таймера и лёгкого опроса).</summary>
  private async Task RefreshHorizontalColumnPageStateFromDomLightAsync()
  {
    if (BookWebView == null) return;
    await MainThread.InvokeOnMainThreadAsync(async () =>
    {
      if (BookWebView == null) return;
      const string js =
          "(function(){"
          + "if(typeof window.__hrPeekIdxCnt!=='function')return '0|1';"
          + "return window.__hrPeekIdxCnt();})()";
      try
      {
        string? r = await BookWebView.EvaluateJavaScriptAsync(js).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(r)) return;
        string t = r.Trim().Trim('"');
        int pipe = t.IndexOf('|', StringComparison.Ordinal);
        if (pipe < 0) return;
        if (!int.TryParse(t.AsSpan(0, pipe), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
          return;
        if (!int.TryParse(t.AsSpan(pipe + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cnt))
          return;
        _pageCount = Math.Max(1, cnt);
        _currentPageIndex = Math.Clamp(idx, 0, Math.Max(0, _pageCount - 1));

        try
        {
          if (BookWebView != null && !string.IsNullOrEmpty(_fullText))
          {
            string? topRaw =
                await BookWebView.EvaluateJavaScriptAsync(ReadingHtmlBuilder.EvaluateGetTopVisibleBookOffsetJavaScript)
                    .ConfigureAwait(true);
            if (TryParseWebViewLong(topRaw, out long domTop) && domTop >= 0)
              _horizontalDomBookAnchorCache = Math.Clamp(domTop, 0, Math.Max(0, _fullText.Length - 1));
          }
        }
        catch { }
      }
      catch { }
    }).ConfigureAwait(true);
  }

  /// <summary>Скроллит горизонтальный multicol так, чтобы вверх оказался начальный абзац заданной главы.</summary>
  private async Task JumpHorizontalColumnToChapterAsync(int chapterIndex)
  {
    if (BookWebView == null) return;
    string js = $@"(function(){{if(window.__hrJumpToChapter)window.__hrJumpToChapter({chapterIndex});}})()";
    try
    {
      await BookWebView.EvaluateJavaScriptAsync(js).ConfigureAwait(true);
    }
    catch { }
  }

  /// <summary>Вертикальный WebView: устанавливает <c>scrollTo</c> по доле <paramref name="ratio"/> высоты документа (после задержки на готовность DOM).</summary>
  private async Task ScrollWebToRatioAsync(double ratio)
  {
    ratio = Math.Clamp(ratio, 0, 1);
    string r = ratio.ToString(CultureInfo.InvariantCulture);
    string script = $"(function(){{var h=Math.max(0,document.documentElement.scrollHeight-window.innerHeight);window.scrollTo(0,h*{r});}})();";
    // Раньше было 8 подряд scrollTo — визуально страница «сама прокручивалась» при открытии.
    await Task.Delay(100).ConfigureAwait(true);
    for (int attempt = 0; attempt < 2; attempt++)
    {
      if (attempt > 0)
        await Task.Delay(180).ConfigureAwait(true);
      try
      {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
          if (BookWebView != null)
            await BookWebView.EvaluateJavaScriptAsync(script);
        });
      }
      catch
      {
        // WebView может быть ещё не готов к JS
      }
    }
  }

  /// <summary>Запускает таймер опроса прокрутки вертикального WebView для обновления прогресса и позиции в тексте.</summary>
  private void StartWebScrollTimerIfNeeded()
  {
    if (!_isVerticalMode) return;
    if (_webScrollTimer != null) return;
    _webScrollTimer = Dispatcher.CreateTimer();
    _webScrollTimer.Interval = TimeSpan.FromMilliseconds(280);
    _webScrollTimer.Tick += OnWebScrollPoll;
    _webScrollTimer.Start();
  }

  /// <summary>Останавливает таймер <see cref="OnWebScrollPoll"/>.</summary>
  private void StopWebScrollTimer()
  {
    if (_webScrollTimer == null) return;
    _webScrollTimer.Stop();
    _webScrollTimer.Tick -= OnWebScrollPoll;
    _webScrollTimer = null;
  }

  /// <summary>Периодически триггерит <see cref="PollWebScrollAsync"/> в вертикальном режиме.</summary>
  private void OnWebScrollPoll(object? sender, EventArgs e)
  {
    if (_isRendering || !_readingWithWebView || string.IsNullOrEmpty(_fullText) || !_isVerticalMode) return;
    _ = PollWebScrollAsync();
  }

  /// <summary>После оверлея заметки — сразу обновить «N из M» у вертикального WebView (опрос каждые ~280 мс может отличаться заметную паузу).</summary>
  Task RefreshVerticalReadingPageLabelAfterNoteOverlayDismissAsync()
  {
    if (!_isVerticalMode || !_readingWithWebView || BookWebView == null)
      return Task.CompletedTask;
    StartWebScrollTimerIfNeeded();
    return MainThread.InvokeOnMainThreadAsync(async () =>
    {
      try
      {
        _verticalPageNumbersSynced = true;
        await PollWebScrollAsync().ConfigureAwait(true);
        UpdatePageNumberLabel();
        await Task.Delay(90).ConfigureAwait(true);
        await PollWebScrollAsync().ConfigureAwait(true);
        UpdatePageNumberLabel();
      }
      catch
      {
        // только обновление UI
      }
    });
  }

  /// <summary>Читает геометрию вертикального скролла из WebView, обновляет метки глав и подпись «N из M» на главном потоке.</summary>
  private async Task PollWebScrollAsync()
  {
    if (BookWebView == null || !_isVerticalMode) return;
    try
    {
      const string js =
          "(function(){var ih=Math.max(1,window.innerHeight||0);var sh=document.documentElement.scrollHeight||0;"
          + "var y=window.scrollY||0;var h=Math.max(0,sh-ih);var total=Math.max(1,Math.ceil(sh/ih));"
          + "var cur=h<=0?0:Math.min(total-1,Math.floor(y/ih));var r=h<=0?0:Math.min(1,y/h);return cur+'|'+total+'|'+r;})()";
      string? s = await BookWebView.EvaluateJavaScriptAsync(js);
      if (string.IsNullOrWhiteSpace(s)) return;
      var t = s.Trim().Trim('"');
      int pipe = t.IndexOf('|', StringComparison.Ordinal);
      int pipe2 = t.IndexOf('|', pipe + 1);
      if (pipe <= 0 || pipe2 <= pipe) return;
      if (!int.TryParse(t.AsSpan(0, pipe), NumberStyles.Integer, CultureInfo.InvariantCulture, out int curVp))
        return;
      if (!int.TryParse(t.AsSpan(pipe + 1, pipe2 - pipe - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int totVp))
        return;
      if (!double.TryParse(t.AsSpan(pipe2 + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double scrollGeomRatio))
        return;
      _verticalViewportPageIndex = Math.Max(0, curVp);
      _verticalViewportPageCount = Math.Max(1, totVp);
      if (DateTime.UtcNow >= _suppressVerticalPollAnchorApplyUntilUtc)
      {
        string? anchorRaw =
            await BookWebView.EvaluateJavaScriptAsync(ReadingHtmlBuilder.EvaluateGetTopVisibleBookOffsetJavaScript).ConfigureAwait(true);
        if (TryParseWebViewLong(anchorRaw, out long domOff) && domOff >= 0)
          ApplyVerticalReadingStateFromFullTextOffset(domOff);
        else if (_readingWithWebView && _isVerticalMode)
          ApplyVerticalReadingStateFromScrollGeometryRatio(scrollGeomRatio);
      }
      // Пиксельная доля y/h не соответствует доле символов внутри главы; после навигации y≈0 давала сброс к началу главы и ломала переход по заметке.
      _verticalPageNumbersSynced = true;
      await MainThread.InvokeOnMainThreadAsync(() =>
      {
        UpdateBookHeaders();
        if (_tocOverlayVisible)
          RefreshTocHighlight();
        UpdatePageNumberLabel();
      });
    }
    catch { }
  }

  /// <summary>Вспомогательный парсер числа с плавающей точкой из JS-ответа.</summary>
  private static bool TryParseJsNumber(string? s, out double value)
  {
    value = 0;
    if (string.IsNullOrWhiteSpace(s)) return false;
    var t = s.Trim().Trim('"');
    return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
  }

  private void OnTranslationBackdropTapped(object? sender, TappedEventArgs e)
  {
    if (TranslationBusyIndicator != null && TranslationBusyIndicator.IsRunning)
    {
      _sentenceTranslateCts?.Cancel();
      if (TranslationBusyIndicator != null)
      {
        TranslationBusyIndicator.IsRunning = false;
        TranslationBusyIndicator.IsVisible = false;
      }
      if (TranslationOverlay != null)
      {
        TranslationOverlay.IsVisible = false;
        TranslationOverlay.InputTransparent = true;
      }
      ClearWebViewSentenceHighlightIfNeeded();
      return;
    }
    if (TranslationBusyIndicator != null)
    {
      TranslationBusyIndicator.IsRunning = false;
      TranslationBusyIndicator.IsVisible = false;
    }
    if (TranslationOverlay != null)
    {
      TranslationOverlay.IsVisible = false;
      TranslationOverlay.InputTransparent = true;
    }
    ClearWebViewSentenceHighlightIfNeeded();
  }

  /// <summary>Поглощает тап по «пузырю» результата, чтобы не считался тап по фону.</summary>
  private void OnTranslationBubbleTapped(object? sender, TappedEventArgs e)
  {
    // поглощаем тап по баллону, чтобы не закрывать по спецификации только «вне области»
  }

  /// <summary>Скрывает или показывает панели интерфейса по двойному касанию области чтения.</summary>
  private void OnDoubleTapped(object sender, TappedEventArgs e)
  {
    SetPanelsVisibility(!_panelsVisible);
  }

  /// <summary>Обновляет долю скролла и заголовки при использовании старого вертикального <see cref="ScrollView"/> вместо WebView.</summary>
  private void OnBookScrollViewScrolled(object sender, ScrolledEventArgs e)
  {
    if (_readingWithWebView) return;
    if (_isRendering) return;
    if (string.IsNullOrEmpty(_fullText)) return;
    try
    {
      double maxScrollY = BookScrollView.ContentSize.Height - BookScrollView.Height;
      if (maxScrollY <= 0) return;
      _currentPageScrollRatio = Math.Clamp(e.ScrollY / maxScrollY, 0, 1);
      long off = ComputeCurrentFullTextOffset();
      int ch = FindChapterIndexByOffset(off);
      if (ch != _currentChapterIndex)
      {
        _currentChapterIndex = ch;
        UpdateBookHeaders();
        if (_tocOverlayVisible)
          RefreshTocHighlight();
      }
      UpdatePageNumberLabel();
    }
    catch { }
  }

  /// <summary>Сохраняет смещение символа, прогресс карточки и статус произведения; при необходимости синхронизирует позицию с DOM WebView перед вычислением offset.</summary>
  /// <param name="syncHorizontalDomFromScroll">
  /// false сразу после программного <see cref="GoToPageAsync"/> — позиция уже задана индексом, опрос DOM только замедляет листание.
  /// </param>
  private async Task SaveReadingStateAsync(bool syncHorizontalDomFromScroll = true, long? exactCharacterOffset = null)
  {
    if (_currentBook == null || string.IsNullOrEmpty(_fullText))
      return;

    try
    {
      if (exactCharacterOffset == null
          && _readingWithWebView && BookWebView != null)
      {
        if (_isVerticalMode)
          await PollWebScrollAsync();
        else if (_horizontalColumnDocLoaded && syncHorizontalDomFromScroll)
        {
          await RefreshHorizontalColumnPageIndexFromScrollAsync();
          await MainThread.InvokeOnMainThreadAsync(UpdateBookHeaders);
        }
      }
    }
    catch { }

    long saveOffset = exactCharacterOffset ?? ComputeCurrentFullTextOffset();
    saveOffset = Math.Clamp(saveOffset, 0, _fullText.Length);

    if (_currentReadingPosition == null)
    {
      _currentReadingPosition = new ReadingPosition
      {
        CardId = _currentBook.Id,
        CharacterOffset = saveOffset,
        LastUpdated = DateTime.Now
      };
      await _db.SaveReadingPositionAsync(_currentReadingPosition);
    }
    else
    {
      _currentReadingPosition.CharacterOffset = saveOffset;
      await _db.UpdateReadingPositionAsync(_currentReadingPosition);
    }

    _currentBook.ReadChars = Math.Max(_currentBook.ReadChars, saveOffset);
    if (_fullText.Length > 0 && _currentBook.TotalChars != _fullText.Length)
      _currentBook.TotalChars = _fullText.Length;
    GetGlobalBookPageDisplay(out _, out int totalForCard);
    _currentBook.EstimatedPageCount = Math.Max(1, totalForCard);
    _currentBook.LastOpened = DateTime.Now;
    await _db.UpdateCardAsync(_currentBook);
    MainPageViewModel.Instance?.ApplyLastOpenedToCard(_currentBook.Id, _currentBook.LastOpened);

    if (_currentWork != null)
    {
      // «Прочитано» при открытии последней страницы (как в «N из M»). Снять статус можно только из меню карточки — здесь не сбрасываем.
      if (IsOnLastBookPage())
        _currentWork.ReadingStatus = BookStatus.Read;
      else if (_currentWork.ReadingStatus == BookStatus.New)
        _currentWork.ReadingStatus = BookStatus.InProgress;
      await _db.UpdateWorkAsync(_currentWork);
      MainPageViewModel.Instance?.ApplyWorkToAllLanguageVersions(_currentWork);
    }
  }

  /// <summary>Переход к главе из оглавления: текст с первого значимого символа главы (не хвост предыдущей на той же странице).</summary>
  private async Task GoToChapterAsync(int chapterIndex)
  {
    if (string.IsNullOrEmpty(_fullText))
      return;
    if (_chapters.Count == 0)
      BuildChapterIndex();
    if (_chapters.Count == 0)
      return;
    chapterIndex = Math.Clamp(chapterIndex, 0, _chapters.Count - 1);

    await SaveReadingStateAsync();

    await EnsureHorizontalPagesMeasuredAsync();
    _currentChapterIndex = chapterIndex;
    long anchor = GetFirstContentOffsetInChapter(chapterIndex);
    if (_fullText.Length > 0)
      anchor = Math.Clamp(anchor, 0, _fullText.Length - 1);

    if (!_isVerticalMode)
    {
      _currentPageScrollRatio = 0;
      if (_horizontalColumnDocLoaded)
      {
        await JumpHorizontalColumnToChapterAsync(chapterIndex).ConfigureAwait(true);
        await RefreshHorizontalColumnPageIndexFromScrollAsync().ConfigureAwait(true);
        SyncCurrentPageBoundsFromPageIndex();
        UpdateBookHeaders();
        UpdatePageNumberLabel();
        return;
      }
      _pendingHorizontalChapterJumpAfterLoad = chapterIndex;
      _currentPageIndex = Math.Clamp(FindHorizontalPageIndexByOffset(anchor), 0, Math.Max(0, _pageCount - 1));
      await RenderCurrentPageAsync(0, syncBoundsFromPageIndex: true);
      return;
    }

    var vch = _chapters[chapterIndex];
    long chLen = Math.Max(1, vch.End - vch.Start);
    long within = Math.Clamp(anchor - vch.Start, 0, chLen - 1);
    _currentPageScrollRatio = chLen > 0 ? (double)within / chLen : 0;
    _lastRenderedVerticalChapterIndex = -1;
    await RenderCurrentPageAsync(_currentPageScrollRatio, syncBoundsFromPageIndex: true);
  }

  /// <summary>Переключает соседнюю главу в вертикальном оглавлении WebView после жестов зоны листания.</summary>
  private async Task GoToAdjacentVerticalChapterAsync(int delta)
  {
    if (!_isVerticalMode || _chapters.Count == 0) return;
    int nc = Math.Clamp(_currentChapterIndex + delta, 0, _chapters.Count - 1);
    if (nc == _currentChapterIndex) return;
    await SaveReadingStateAsync();
    _suppressWebViewPagingNavUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
    _currentChapterIndex = nc;
    _currentPageScrollRatio = 0;
    _verticalViewportPageIndex = 0;
    _verticalViewportPageCount = 1;
    _lastRenderedVerticalChapterIndex = -1;
    await RenderCurrentPageAsync(scrollToRatio: 0);
  }

  /// <summary>Переход между страницами горизонтального листания (вертикальный режим кнопками переключает главы — см. <see cref="GoToAdjacentVerticalChapterAsync"/>).</summary>
  private async Task GoToPageAsync(int horizontalPageIndex)
  {
    if (_fullText == null || _fullText.Length == 0) return;
    if (_isVerticalMode) return;
    // Блокируем WebView URL-навигации на время выполнения этого перехода:
    // на Android прозрачный MAUI-оверлей пропускает тачи в WebView, из-за чего JS может
    // дублировать go('pagenext') после того, как MAUI-хендлер уже вызвал GoToPageAsync.
    _suppressWebViewPagingNavUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
    await EnsureHorizontalPagesMeasuredAsync();
    int maxPg = !_isVerticalMode && _horizontalColumnDocLoaded
        ? Math.Max(0, _pageCount - 1)
        : Math.Max(0, _horizontalPages.Count - 1);
    horizontalPageIndex = Math.Clamp(horizontalPageIndex, 0, maxPg);

    _horizontalDomBookAnchorCache = null;
    _currentPageIndex = horizontalPageIndex;
    _currentPageScrollRatio = 0;
    SyncCurrentPageBoundsFromPageIndex();

    await RenderCurrentPageAsync(scrollToRatio: 0);
    _ = SaveReadingStateAsync(syncHorizontalDomFromScroll: false);
  }

  /// <summary>Краткий доступ к централизованным тостам/баннерам приложения.</summary>
  private static void ShowAppNotification(string message, AppNotificationSeverity severity)
  {
    var svc = ServiceLocator.Get<IAppNotificationService>();
    svc?.Show(message, severity);
  }

  /// <summary>По нажатию на подпись прогресса открывает диалог перехода к странице по номеру.</summary>
  private async void OnPageInfoLabelTapped(object? sender, TappedEventArgs e)
  {
    await PromptAndGoToGlobalPageAsync();
  }

  /// <summary>Валидация ввода и вызов <see cref="GoToGlobalBookPageAsync"/> после подтверждения промпта.</summary>
  private async Task PromptAndGoToGlobalPageAsync()
  {
    if (string.IsNullOrEmpty(_fullText) || _currentBook == null)
      return;
    if (_pendingReadingLoadUi)
      return;
    if (_isVerticalMode && _readingWithWebView && !_verticalPageNumbersSynced)
      return;
    if (!_isVerticalMode && _readingWithWebView && !_horizontalColumnDocLoaded)
    {
      await ThemedOverlayPresenter.ShowAlertAsync(this, Strings.Common_Wait, Strings.Reading_WaitPages, Strings.Common_OK);
      return;
    }

    GetGlobalBookPageDisplay(out int curPg, out int totalPg);
    totalPg = Math.Max(1, totalPg);
    curPg = Math.Clamp(curPg, 1, totalPg);

    string? r = await ThemedOverlayPresenter.ShowPromptAsync(
        this,
        Strings.Reading_JumpPageTitle,
        string.Format(Strings.Reading_JumpPagePrompt, totalPg),
        Strings.Reading_JumpPageConfirm,
        Strings.Common_Cancel,
        initialValue: curPg.ToString(CultureInfo.InvariantCulture),
        keyboard: Keyboard.Numeric);

    if (r == null)
      return;
    if (!int.TryParse(r.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int typed))
    {
      ShowAppNotification(
          Strings.Reading_InvalidPageNumberInput,
          AppNotificationSeverity.Error);
      return;
    }

    if (typed < 1 || typed > totalPg)
    {
      ShowAppNotification(
          typed < 1
              ? string.Format(CultureInfo.CurrentUICulture, Strings.Reading_GotoPage_ErrorBelowMinFormat, totalPg)
              : string.Format(CultureInfo.CurrentUICulture, Strings.Reading_GotoPage_ErrorNoSuchPageFormat, typed, totalPg),
          AppNotificationSeverity.Error);
      return;
    }

    await GoToGlobalBookPageAsync(typed);
  }

  /// <summary>Переход по глобальному номеру страницы (как в подписи «N из M»).</summary>
  private async Task GoToGlobalBookPageAsync(int page1Based)
  {
    if (string.IsNullOrEmpty(_fullText))
      return;

    if (!_isVerticalMode && _horizontalColumnDocLoaded && _pageCount > 0)
    {
      int total = Math.Max(1, _pageCount);
      int horizPageIdx = Math.Clamp(page1Based, 1, total) - 1;
      _suppressWebViewPagingNavUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
      await GoToPageAsync(horizPageIdx);
      return;
    }

    int stride = GetGlobalBookPageStride();
    long len = _fullText.Length;
    int totalPg = ComputeTotalGlobalBookPages(len, stride);
    page1Based = Math.Clamp(page1Based, 1, totalPg);
    long off = OffsetForGlobalBookPage(page1Based, stride, len);

    await SaveReadingStateAsync();

    if (_isVerticalMode)
    {
      if (_chapters.Count == 0)
        BuildChapterIndex();
      _currentChapterIndex = FindChapterIndexByOffset(off);
      if (_chapters.Count > 0)
      {
        int ci = Math.Clamp(_currentChapterIndex, 0, _chapters.Count - 1);
        _currentChapterIndex = ci;
        var ch = _chapters[ci];
        long span = Math.Max(1, ch.End - ch.Start);
        long within = Math.Clamp(off - ch.Start, 0, span - 1);
        _currentPageScrollRatio = (double)within / span;
      }
      else
        _currentPageScrollRatio = 0;
      _suppressWebViewPagingNavUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
      _lastRenderedVerticalChapterIndex = -1;
      _verticalPageNumbersSynced = false;
      await RenderCurrentPageAsync(_currentPageScrollRatio, syncBoundsFromPageIndex: true);
      return;
    }

    await EnsureHorizontalPagesMeasuredAsync();
    int targetIdx = FindHorizontalPageIndexByOffset(off);
    await GoToPageAsync(targetIdx);
  }

  /// <summary>В фоне извлекает плоский текст и структуру глав из FB2 / EPUB / zip по расширению и формату карточки.</summary>
  private async Task LoadFullTextAndStructureAsync(string filePath, string format)
  {
    await Task.Run(() =>
    {
      _fb2OrderedSections = null;
      _epubChapterHtmlBodies = null;
      try
      {
        var fmt = format?.ToUpperInvariant() ?? string.Empty;
        var lowerPath = filePath?.ToLowerInvariant() ?? string.Empty;

        if (lowerPath.EndsWith(".fb2.zip"))
        {
          using var zip = new System.IO.Compression.ZipArchive(File.OpenRead(filePath));
          var fb2Entry = BookZipEntryHelper.FindPrimaryFb2Entry(zip);
          if (fb2Entry == null)
          {
            _fullText = Strings.Reading_Fallback_Fb2ZipMissing;
            ApplyParsedChapters(new List<Fb2BookTextExtractor.ChapterSpan>());
            return;
          }
          using var stream = fb2Entry.Open();
          using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
          var xml = reader.ReadToEnd();
          var (text, chapters) = Fb2BookTextExtractor.ExtractFromXml(xml);
          _fullText = text;
          ApplyParsedChapters(chapters);
          TryLoadFb2SectionTree(xml);
          ReconcileFb2ChaptersIfNeeded();
          return;
        }

        if (fmt == "FB2" || lowerPath.EndsWith(".fb2"))
        {
          var xml = File.ReadAllText(filePath);
          var (text, chapters) = Fb2BookTextExtractor.ExtractFromXml(xml);
          _fullText = text;
          ApplyParsedChapters(chapters);
          TryLoadFb2SectionTree(xml);
          ReconcileFb2ChaptersIfNeeded();
          return;
        }

        if (fmt == "EPUB" || lowerPath.EndsWith(".epub") || lowerPath.EndsWith(".epub.zip"))
        {
          if (EpubBookExtractor.TryExtract(filePath, out var epubPlain, out var epubChapters, out var epubHtmlBodies))
          {
            _fullText = epubPlain;
            _epubChapterHtmlBodies = epubHtmlBodies;
            ApplyParsedChapters(epubChapters);
            return;
          }
          _fullText = ExtractEPUBText(filePath);
          _epubChapterHtmlBodies = null;
          ApplyParsedChapters(new List<Fb2BookTextExtractor.ChapterSpan>());
          return;
        }

        _fullText = Strings.Reading_Fallback_UnsupportedFormat;
        ApplyParsedChapters(new List<Fb2BookTextExtractor.ChapterSpan>());
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[ReadingPage] Ошибка извлечения текста: {ex.Message}");
        _fullText = Strings.Reading_Fallback_LoadTextFailed;
        ApplyParsedChapters(new List<Fb2BookTextExtractor.ChapterSpan>());
      }
    });
  }

  /// <summary>
  /// Если разбор текста упал в fallback («Текст»), а дерево секций из того же XML успешно — пересобираем главы и текст по секциям.
  /// В БД хранится только путь к файлу; при открытии снова читается полный FB2.
  /// </summary>
  private void ReconcileFb2ChaptersIfNeeded()
  {
    if (_fb2OrderedSections == null || _fb2OrderedSections.Count <= 1)
      return;
    if (_chapters.Count != 1)
      return;
    var t0 = _chapters[0].Title?.Trim() ?? "";
    if (!string.Equals(t0, Strings.Reading_TocFallbackBodyTitle, StringComparison.Ordinal))
      return;

    var (text, spans) = Fb2BookTextExtractor.ExtractFromOrderedSections(_fb2OrderedSections);
    if (spans.Count <= 1 || string.IsNullOrEmpty(text))
      return;

    _fullText = text;
    ApplyParsedChapters(spans);
    InvalidatePaginationForLayout();
  }

  /// <summary>Сохраняет упорядоченное дерево секций FictionBook для богаче оглавления и последующей сверки с fallback-текстом.</summary>
  private void TryLoadFb2SectionTree(string xmlContent)
  {
    try
    {
      var doc = Fb2BookTextExtractor.LoadFictionBookDocument(xmlContent);
      var bodyRoot = Fb2BookTextExtractor.GetMainBodyElement(doc);
      if (bodyRoot == null)
      {
        _fb2OrderedSections = null;
        return;
      }
      var ordered = Fb2RichParagraphParser.GetOrderedSectionElements(bodyRoot);
      if (ordered.Count == 0)
      {
        _fb2OrderedSections = Fb2BookTextExtractor.SectionHasVisibleBody(bodyRoot)
            ? new List<XElement> { bodyRoot }
            : null;
        return;
      }
      _fb2OrderedSections = ordered.Where(Fb2BookTextExtractor.SectionHasVisibleBody).ToList();
      if (_fb2OrderedSections.Count == 0)
        _fb2OrderedSections = null;
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[ReadingPage] TryLoadFb2SectionTree: {ex.Message}");
      _fb2OrderedSections = null;
    }
  }

  /// <summary>Преобразует список spanов экстрактора в внутренний список <see cref="_chapters"/> или одну главу «Весь текст».</summary>
  private void ApplyParsedChapters(List<Fb2BookTextExtractor.ChapterSpan> spans)
  {
    _chapters.Clear();
    foreach (var c in spans)
      _chapters.Add((c.Title, c.Start, c.End));
    if (_chapters.Count == 0 && !string.IsNullOrEmpty(_fullText))
      _chapters.Add((Strings.Reading_TocFallbackBodyTitle, 0, _fullText.Length));
  }

  /// <summary>Простое извлечение строк из HTML-файлов внутри EPUB при недоступности структурированного <see cref="EpubBookExtractor"/>.</summary>
  private string ExtractEPUBText(string filePath)
  {
    try
    {
      StringBuilder text = new StringBuilder();
      using (var zip = new System.IO.Compression.ZipArchive(File.OpenRead(filePath)))
      {
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName))
        {
          if (entry.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
              entry.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase))
          {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            var textOnly = Regex.Replace(content, "<[^>]*>", "");
            text.Append(textOnly).Append(' ');
          }
        }
      }
      return text.ToString().Trim();
    }
    catch { return Strings.Reading_Error_ReadEpub; }
  }

  /// <summary>Переход на предыдущую главу или колонку-страницу из нижней панели инструментов.</summary>
  private async void OnPrevPageClicked(object sender, EventArgs e)
  {
    if (_isVerticalMode)
      await GoToAdjacentVerticalChapterAsync(-1);
    else
      await GoToPageAsync(_currentPageIndex - 1);
  }

  /// <summary>Переход на следующую главу или колонку-страницу из нижней панели инструментов.</summary>
  private async void OnNextPageClicked(object sender, EventArgs e)
  {
    if (_isVerticalMode)
      await GoToAdjacentVerticalChapterAsync(1);
    else
      await GoToPageAsync(_currentPageIndex + 1);
  }

  /// <summary>Тап по левому краю в горизонтальном режиме — предыдущая страница.</summary>
  private async void OnTurnPrevPageTapped(object sender, TappedEventArgs e)
  {
    if (_isVerticalMode) return;
    await GoToPageAsync(_currentPageIndex - 1);
  }

  /// <summary>Тап по правому краю в горизонтальном режиме — следующая страница.</summary>
  private async void OnTurnNextPageTapped(object sender, TappedEventArgs e)
  {
    if (_isVerticalMode) return;
    await GoToPageAsync(_currentPageIndex + 1);
  }

  /// <summary>Свайп справа налево (жест влево) — следующая страница.</summary>
  private async void OnSwipeLeftForPage(object sender, SwipedEventArgs e)
  {
    if (_isVerticalMode) return;
    await GoToPageAsync(_currentPageIndex + 1);
  }

  /// <summary>Свайп слева направо (жест вправо) — предыдущая страница.</summary>
  private async void OnSwipeRightForPage(object sender, SwipedEventArgs e)
  {
    if (_isVerticalMode) return;
    await GoToPageAsync(_currentPageIndex - 1);
  }

  /// <summary>При возврате на страницу применяет изменившиеся настройки текста и перерисовывает книгу с позиции из БД.</summary>
  protected override async void OnAppearing()
  {
    base.OnAppearing();
    _skipReadingStateSaveOnDisappearing = false;
    _activeReadingPage = this;
    if (!_initialLoadFinished || string.IsNullOrEmpty(_fullText) || _currentBook == null)
      return;
    try
    {
      var ts = await _db.GetTextSettingsAsync();
      var eff = ComputeEffectiveTextSettings(ts);
      // Только если настройки в БД реально изменились (например, после экрана настроек текста).
      // Не трогаем раскладку из-за «испорченного» layoutOk — иначе при каждом возврате на страницу снова грузится WebView.
      bool settingsEqual = _lastAppliedTextSettingsSnapshot != null && FullSettingsEqualEffective(_lastAppliedTextSettingsSnapshot, eff);
      if (settingsEqual)
        return;
      // Карточка уже пересчитана в БД (RecalculateAllCardsEstimatedPageCountAsync); подтянуть актуальный EstimatedPageCount.
      var freshCard = await _db.GetCardByIdAsync(_currentBook.Id);
      if (freshCard != null)
        _currentBook = freshCard;
      long off = 0;
      await MainThread.InvokeOnMainThreadAsync(async () =>
      {
        try
        {
          if (_isVerticalMode && _readingWithWebView)
            await PollWebScrollAsync();
          else if (!_isVerticalMode && _horizontalColumnDocLoaded && BookWebView != null)
            await RefreshHorizontalColumnPageIndexFromScrollAsync();
          off = ComputeCurrentFullTextOffset();
        }
        catch { }
      });
      // После смены шрифта/отступов DOM до перезагрузки ненадёжен — берём сохранённый символ из БД (оба режима).
      if (_currentBook != null && _fullText.Length > 0)
      {
        try
        {
          var pos = await _db.GetReadingPositionByCardIdAsync(_currentBook.Id);
          if (pos != null)
            off = Math.Clamp(pos.CharacterOffset, 0, Math.Max(0, _fullText.Length - 1));
        }
        catch { }
      }
      await MainThread.InvokeOnMainThreadAsync(async () =>
      {
        ShowReadingLoadingUi(applyingSettings: true);
        ApplyTextSettingsFromModel(ts);
        await WaitForReadingLayerLayoutAsync();
        await EnsureHorizontalPagesMeasuredAsync();
        if (!_isVerticalMode)
        {
          _currentPageScrollRatio = 0;
          _currentPageIndex = FindHorizontalPageIndexByOffset(off);
          _pendingWebScrollToBookOffset = off;
          _pendingHorizontalColumnRestoreOffset = null;
          _currentChapterIndex = FindChapterIndexByOffset(off);
          UpdateBookHeaders();
          await RenderCurrentPageAsync(scrollToRatio: 0);
          // Горизонталь: фактическое M — из DOM после Navigated (Persist в ScheduleHide / SaveReadingState).
          return;
        }
        _currentChapterIndex = FindChapterIndexByOffset(off);
        if (_chapters.Count > 0)
        {
          int ci = Math.Clamp(_currentChapterIndex, 0, _chapters.Count - 1);
          _currentChapterIndex = ci;
          var ch = _chapters[ci];
          long span = ch.End - ch.Start;
          if (span > 0)
            _currentPageScrollRatio = Math.Clamp((double)(off - ch.Start) / span, 0, 1);
          else
            _currentPageScrollRatio = 0;
        }
        else if (_fullText.Length > 1)
          _currentPageScrollRatio = Math.Clamp((double)off / (_fullText.Length - 1), 0, 1);
        else
          _currentPageScrollRatio = 0;
        _pendingWebScrollToBookOffset = off;
        ArmVerticalPollAnchorSuppressionForPreciseScroll();
        _pageCount = 1;
        _verticalViewportPageIndex = 0;
        _verticalViewportPageCount = 1;
        _lastRenderedVerticalChapterIndex = -1;
        UpdateBookHeaders();
        await RenderCurrentPageAsync(scrollToRatio: _currentPageScrollRatio);
        await PersistCurrentBookEstimatedPageCountToCardAsync();
      });
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[ReadingPage] OnAppearing: {ex.Message}");
    }
  }

  /// <summary>Сохраняет прогресс, останавливает таймеры и восстанавливает системный хром перед уходом со страницы.</summary>
  protected override async void OnDisappearing()
  {
    base.OnDisappearing();
    // Сохраняем позицию до остановки таймеров/WebView — иначе системная «Назад» (без OnBackClicked) даёт неверный offset.
    if (_tocOverlayVisible)
      await HideTocAsync();
    if (!_skipReadingStateSaveOnDisappearing)
    {
      try { await SaveReadingStateAsync(); }
      catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ReadingPage] Ошибка сохранения состояния: {ex.Message}"); }
    }
    else
      _skipReadingStateSaveOnDisappearing = false;

    _horizontalLayoutRealignCts?.Cancel();
    _horizontalLayoutRealignCts?.Dispose();
    _horizontalLayoutRealignCts = null;
    StopWebScrollTimer();
    StopHorizontalPageIndexSyncTimer();

    if (ReferenceEquals(_activeReadingPage, this))
      _activeReadingPage = null;

    RestoreAppSystemChromeAfterReading();
  }

  /// <summary>Аппаратная кнопка «Назад» (Android) — тот же сценарий, что и по кнопке в UI.</summary>
  protected override bool OnBackButtonPressed()
  {
    MainThread.BeginInvokeOnMainThread(() => _ = NavigateBackFromReadingAsync());
    return true;
  }

  /// <summary>Сохраняет позицию чтения, выходит из стека навигации и обновляет список книг на главной.</summary>
  private async Task NavigateBackFromReadingAsync()
  {
    try
    {
      if (_isVerticalMode && _readingWithWebView)
        await SaveReadingStateAsync();
      else
        await SaveReadingStateAsync(syncHorizontalDomFromScroll: false);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[ReadingPage] Ошибка перед возвратом: {ex.Message}");
    }
    _skipReadingStateSaveOnDisappearing = true;
    await Navigation.PopAsync();
    if (MainPageViewModel.Instance != null)
      await MainPageViewModel.Instance.RefreshBooksAsync();
  }

  /// <summary>Сохранить позицию, если открыт экран чтения (вызывается из App при уходе в фон).</summary>
  public static Task TrySaveActiveReadingStateAsync()
  {
    var p = _activeReadingPage;
    if (p == null)
      return Task.CompletedTask;
    return MainThread.InvokeOnMainThreadAsync(async () =>
    {
      try
      {
        await p.SaveReadingStateAsync();
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[ReadingPage] TrySaveActiveReadingStateAsync: {ex.Message}");
      }
    });
  }
  /// <summary>Возврат на корневую страницу приложения с сохранением прогресса и флагом прокрутки списка книг вверх.</summary>
  private async void OnHomeClicked(object sender, EventArgs e)
  {
    try
    {
      if (_isVerticalMode && _readingWithWebView)
        await SaveReadingStateAsync();
      else
        await SaveReadingStateAsync(syncHorizontalDomFromScroll: false);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[ReadingPage] Ошибка перед выходом на главный: {ex.Message}");
    }
    _skipReadingStateSaveOnDisappearing = true;
    Preferences.Set("ScrollMainToTopRequested", true);
    await Navigation.PopToRootAsync();
    if (MainPageViewModel.Instance != null)
      await MainPageViewModel.Instance.RefreshBooksAsync();
  }

  /// <summary>Возврат к предыдущему экрану с тем же сценарием сохранения, что и системная кнопка «Назад».</summary>
  private async void OnBackClicked(object sender, EventArgs e) => await NavigateBackFromReadingAsync();

  /// <summary>Открывает или закрывает боковое оглавление глав.</summary>
  private async void OnTableOfContentsClicked(object sender, EventArgs e)
  {
    if (_chapters.Count == 0)
    {
      await ThemedOverlayPresenter.ShowAlertAsync(this, Strings.Reading_TocTitle, Strings.Reading_TocUnavailable, Strings.Common_OK);
      return;
    }
    if (_tocOverlayVisible)
      _ = HideTocAsync();
    else
      _ = ShowTocAsync();
  }

  /// <summary>Показ выезжающей панели оглавления с подсветкой текущей главы и анимацией.</summary>
  private async Task ShowTocAsync()
  {
    if (TocOverlay == null || TocPanel == null || TocList == null) return;
    UpdateTocOverlayMargins();
    RefreshTocItems();
    TocOverlay.IsVisible = true;
    _tocOverlayVisible = true;
    double w = Width > 0 ? Width : DeviceDisplay.MainDisplayInfo.Width / DeviceDisplay.MainDisplayInfo.Density;
    if (TocDimBackdrop != null)
      TocDimBackdrop.Opacity = 0;
    TocPanel.TranslationX = -(float)(w * 0.5 + 8);
    await Task.Delay(20);
    var fadeIn = TocDimBackdrop != null
        ? TocDimBackdrop.FadeTo(1, 180)
        : Task.CompletedTask;
    await Task.WhenAll(fadeIn, TocPanel.TranslateTo(0, 0, 220, Easing.CubicOut));
    if (TocList.ItemsSource is IEnumerable<TocChapterItem> list)
    {
      int cur = _chapters.Count == 0
          ? 0
          : Math.Clamp(_currentChapterIndex, 0, _chapters.Count - 1);
      _tocSuppressSelectionChanged = true;
      try
      {
        TocList.SelectedItem = list.FirstOrDefault(x => x.ChapterIndex == cur);
      }
      finally
      {
        _tocSuppressSelectionChanged = false;
      }
    }
    await ScrollTocToCurrentChapterAsync();
  }

  /// <summary>Прокрутить список оглавления так, чтобы текущая глава была по центру (если возможно).</summary>
  private async Task ScrollTocToCurrentChapterAsync()
  {
    if (TocList == null || _chapters.Count == 0)
      return;
    int cur = Math.Clamp(_currentChapterIndex, 0, _chapters.Count - 1);
    if (TocList.ItemsSource is not IEnumerable<TocChapterItem> enumerable)
      return;
    var sel = enumerable.FirstOrDefault(x => x.ChapterIndex == cur);
    if (sel == null)
      return;
    await Task.Delay(48);
    try
    {
      await MainThread.InvokeOnMainThreadAsync(() =>
      {
        TocList.ScrollTo(sel, position: ScrollToPosition.Center, animate: false);
      });
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[ReadingPage] ScrollTocToCurrentChapterAsync: {ex.Message}");
    }
  }

  /// <summary>Скрывает оверлей оглавления и сбрасывает выделение в списке.</summary>
  private async Task HideTocAsync()
  {
    if (TocOverlay == null || TocPanel == null) return;
    double w = Width > 0 ? Width : DeviceDisplay.MainDisplayInfo.Width / DeviceDisplay.MainDisplayInfo.Density;
    if (TocDimBackdrop != null)
      await TocDimBackdrop.FadeTo(0, 120);
    await TocPanel.TranslateTo(-(float)(w * 0.5 + 8), 0, 200, Easing.CubicIn);
    TocOverlay.IsVisible = false;
    _tocOverlayVisible = false;
    if (TocDimBackdrop != null)
      TocDimBackdrop.Opacity = 1;
    if (TocList != null)
      TocList.SelectedItem = null;
  }

  /// <summary>Заполняет элементы оглавления с учётом тёмной/светлой темы и текущего индекса главы.</summary>
  private void RefreshTocItems()
  {
    if (TocList == null) return;
    bool dark = InterfaceThemeManager.IsDarkPaletteActive;
    var normalBg = ResolveThemeColor("PrimaryBackground", dark ? Color.FromArgb("#2C2C2E") : Color.FromArgb("#F5F5F5"));
    var selectedBg = dark ? Color.FromArgb("#48484A") : Color.FromArgb("#DCDCDC");
    var items = new List<TocChapterItem>();
    for (int i = 0; i < _chapters.Count; i++)
    {
      var raw = _chapters[i].Title?.Trim() ?? "";
      var title = string.IsNullOrWhiteSpace(raw) ? string.Format(Strings.Reading_ChapterNumberFormat, i + 1) : raw;
      bool isCur = i == _currentChapterIndex;
      items.Add(new TocChapterItem
      {
        Title = title,
        ChapterIndex = i,
        RowBackground = isCur ? selectedBg : normalBg
      });
    }
    TocList.ItemsSource = items;
  }

  /// <summary>В горизонтальном режиме заново красит строки оглавления под текущую главу без смены главы пользователем.</summary>
  private void RefreshTocHighlight()
  {
    if (!_tocOverlayVisible || TocList?.ItemsSource == null) return;
    // Вертикаль: RefreshTocItems пересоздаёт ItemsSource — CollectionView сбрасывает прокрутку оглавления.
    if (_isVerticalMode) return;
    RefreshTocItems();
  }

  /// <summary>Закрытие оглавления по тапу на затемнённый фон.</summary>
  private void OnTocBackdropTapped(object? sender, TappedEventArgs e)
  {
      _ = HideTocAsync();
  }

  /// <summary>Выбор главы из списка: переход текста и закрытие панели.</summary>
  private async void OnTocListSelectionChanged(object? sender, SelectionChangedEventArgs e)
  {
    if (_tocSuppressSelectionChanged)
      return;
    var item = e.CurrentSelection?.OfType<TocChapterItem>().FirstOrDefault();
    if (item == null)
      return;
    int idx = item.ChapterIndex;
    if (idx < 0 || idx >= _chapters.Count) return;

    await GoToChapterAsync(idx);
    _tocSuppressSelectionChanged = true;
    try
    {
      RefreshTocItems();
      if (TocList?.ItemsSource is IEnumerable<TocChapterItem> list)
        TocList.SelectedItem = list.FirstOrDefault(x => x.ChapterIndex == idx);
    }
    finally
    {
      _tocSuppressSelectionChanged = false;
    }
    await HideTocAsync();
  }
  /// <summary>Открывает экран настроек текста текущей книги.</summary>
  private async void OnTextSettingsClicked(object sender, EventArgs e)
  {
    await Navigation.PushAsync(new TextSettingsPage());
  }

  /// <summary>Переходит в общие настройки приложения через Shell.</summary>
  private async void OnSettingsClicked(object sender, EventArgs e) => await Shell.Current.GoToAsync(nameof(SettingsPage));

  /// <summary>Список заметок произведения с колбэками открытия заметки и добавления новой.</summary>
  private async void OnNotesClicked(object sender, EventArgs e)
  {
    if (_currentBook == null || _currentWork == null)
      return;
    var db = ServiceLocator.Get<IDatabaseService>() ?? new DatabaseService();
    var vm = new NotesPageViewModel(db);
    vm.OnOpenNoteFromList = async note =>
    {
      await Navigation.PopAsync().ConfigureAwait(true);
      await NavigateToNoteFromListAsync(note).ConfigureAwait(true);
    };
    vm.OnAddNoteRequested = async () =>
    {
      await Navigation.PopAsync().ConfigureAwait(true);
      await ShowAddNoteEditorAsync().ConfigureAwait(true);
    };
    var notesPage = new NotesPage(_currentWork.Id, vm);
    await Navigation.PushAsync(notesPage).ConfigureAwait(true);
  }
}