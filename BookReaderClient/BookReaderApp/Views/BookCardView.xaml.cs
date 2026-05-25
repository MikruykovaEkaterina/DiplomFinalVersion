using Microsoft.Maui.Controls;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using BookReaderApp.Models;
using BookReaderApp.Services;
using BookReaderApp.ViewModels;
using BookReaderApp.Resources;
using Microsoft.Maui.Graphics;

namespace BookReaderApp.Views
{
  /// <summary>
  /// Одна карточка книги в списке: обложка, метаданные, раскрываемое описание, контекстное меню
  /// (прочитано/избранное, удаление, перевод). Привязка к <see cref="BookInfoViewModel"/>; тап по карточке
  /// отдаётся наружу через <see cref="BookTapped"/>, действия меню — через <see cref="CardDeleteRequested"/> и <see cref="CardTranslateRequested"/>.
  /// </summary>
  public partial class BookCardView : ContentView
  {
    /// <summary>Текущая страница для модальных диалогов (Shell, не устаревший MainPage).</summary>
    static Page? HostPageForDialogs()
    {
      if (Shell.Current?.CurrentPage is Page shellPage)
        return shellPage;
      return Application.Current?.Windows.FirstOrDefault()?.Page;
    }

    public event EventHandler<int> BookTapped;
    /// <summary>Запрошено удаление этой версии книги (CardId).</summary>
    public event EventHandler<int> CardDeleteRequested;
    /// <summary>Открыть экран перевода для этой версии (CardId).</summary>
    public event EventHandler<int> CardTranslateRequested;

    private BookInfoViewModel? _boundVm;

    public BookCardView()
    {
      InitializeComponent();
      DescriptionExpander.ExpandedChanged += (_, _) =>
      {
        UpdateDescriptionHeaderIcon();
        InvalidateMeasureUpToCollectionView();
        ApplyCardTouchExclusions();
      };
      UpdateDescriptionHeaderIcon();

      CardTouchBehavior.Command = new Command(OnCardBorderTouchCompleted);

      Loaded += (_, _) => ScheduleApplyCardTouchExclusions();
    }

    protected override void OnBindingContextChanged()
    {
      base.OnBindingContextChanged();
      DetachViewModel();
      if (BindingContext is BookInfoViewModel vm)
      {
        _boundVm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
      }
      ScheduleApplyCardTouchExclusions();
      ApplyChrome();
    }

    protected override void OnHandlerChanged()
    {
      base.OnHandlerChanged();
      if (Handler is null)
        DetachViewModel();
      else
        ScheduleApplyCardTouchExclusions();
    }

    void DetachViewModel()
    {
      if (_boundVm != null)
      {
        _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = null;
      }
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
      switch (e.PropertyName)
      {
        case nameof(BookInfoViewModel.CardBackgroundResourceKey):
        case nameof(BookInfoViewModel.ReadingStatus):
        case nameof(BookInfoViewModel.Reaction):
        case nameof(BookInfoViewModel.MenuButtonHighlighted):
          ApplyChrome();
          break;
      }
    }

    void ApplyChrome()
    {
      ApplyCardSurface();
      ApplyMenuButtonHighlight();
    }

    void ApplyCardSurface()
    {
      if (BindingContext is not BookInfoViewModel vm)
      {
        CardBorder.Background = ResolveBrush("BookCardBackground") ?? new SolidColorBrush(Colors.White);
        return;
      }

      CardBorder.Background = ResolveBrush(vm.CardBackgroundResourceKey)
        ?? ResolveBrush("BookCardBackground")
        ?? new SolidColorBrush(Colors.White);
    }

    void ApplyMenuButtonHighlight()
    {
      if (BindingContext is not BookInfoViewModel vm)
      {
        MenuButton.BackgroundColor = Colors.Transparent;
        return;
      }

      if (vm.MenuButtonHighlighted && TryResolveColor("BookCardMenuButtonActiveTint", out var tint))
        MenuButton.BackgroundColor = tint;
      else
        MenuButton.BackgroundColor = Colors.Transparent;
    }

    static Brush? ResolveBrush(string key)
    {
      if (Application.Current == null)
        return null;
      foreach (var dict in Application.Current.Resources.MergedDictionaries.Reverse())
      {
        if (dict.TryGetValue(key, out var v))
          return ToBrush(v);
      }
      if (Application.Current.Resources.TryGetValue(key, out var root))
        return ToBrush(root);
      return null;
    }

    static Brush? ToBrush(object v) =>
      v switch
      {
        Color c => new SolidColorBrush(c),
        Brush b => b,
        _ => null
      };

    static bool TryResolveColor(string key, out Color color)
    {
      color = default;
      if (ResolveBrush(key) is not SolidColorBrush scb)
        return false;
      color = scb.Color;
      return true;
    }

    /// <summary>
    /// После прокрутки CollectionView переиспользует карточку и меняет BindingContext; TouchBehavior снова
    /// помечает детей — без повторного вызова зона «Описание» временно ведёт себя как остальная карточка (тап открывает книгу).
    /// Отложенный повтор — после того как toolkit/layout применятся к ячейке.
    /// </summary>
    private void ScheduleApplyCardTouchExclusions()
    {
      ApplyCardTouchExclusions();
      _ = DeferredApplyCardTouchExclusionsAsync();
    }

    private async Task DeferredApplyCardTouchExclusionsAsync()
    {
      try
      {
        await Task.Delay(50);
        await MainThread.InvokeOnMainThreadAsync(ApplyCardTouchExclusions);
      }
      catch
      {
        // отмена задачи / отсоединение страницы
      }
    }

    /// <summary>
    /// При ShouldMakeChildrenInputTransparent=True касания идут на Border (анимация + тап «открыть книгу»).
    /// Кнопка меню вынесена из Border (см. разметку). Здесь только блок «Описание».
    /// </summary>
    private void ApplyCardTouchExclusions()
    {
      MainThread.BeginInvokeOnMainThread(() =>
      {
        SetSubtreeInputTransparent(DescriptionExpander, false);
        // После рекурсии Label/Image снова «видят» касания и забирают тап до Grid с жестом.
        if (DescriptionHeaderText != null)
          DescriptionHeaderText.InputTransparent = true;
        if (DescriptionHeaderIcon != null)
          DescriptionHeaderIcon.InputTransparent = true;
      });
    }

    private static void SetSubtreeInputTransparent(VisualElement? root, bool inputTransparent)
    {
      if (root == null)
        return;
      root.InputTransparent = inputTransparent;
      switch (root)
      {
        case Layout layout:
          foreach (var child in layout.Children)
          {
            if (child is VisualElement ve)
              SetSubtreeInputTransparent(ve, inputTransparent);
          }
          break;
        case ContentView { Content: VisualElement content }:
          SetSubtreeInputTransparent(content, inputTransparent);
          break;
        case Border { Content: VisualElement b }:
          SetSubtreeInputTransparent(b, inputTransparent);
          break;
      }
    }

    /// <summary>Пересчёт высоты строки после раскрытия описания (иначе нижние карточки не сдвигаются).</summary>
    private void InvalidateMeasureUpToCollectionView()
    {
      for (VisualElement? v = this; v != null; v = v.Parent as VisualElement)
        v.InvalidateMeasure();
    }

    void UpdateDescriptionHeaderIcon()
    {
      var key = DescriptionExpander.IsExpanded ? "UiIconArrowUp" : "UiIconArrowDown";
      if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is ImageSource img)
        DescriptionHeaderIcon.Source = img;
      else
        DescriptionHeaderIcon.Source =
            DescriptionExpander.IsExpanded ? ImageSource.FromFile("arrow_up.svg") : ImageSource.FromFile("arrow_down.svg");
    }

    void OnDescriptionHeaderTapped(object sender, EventArgs e)
    {
      DescriptionExpander.IsExpanded = !DescriptionExpander.IsExpanded;
      UpdateDescriptionHeaderIcon();
    }

    /// <summary>Срабатывает по завершении касания у TouchBehavior на рамке карточки (не конфликтует с поведением как TapGestureRecognizer).</summary>
    void OnCardBorderTouchCompleted()
    {
      if (BindingContext is BookInfoViewModel bookInfo)
        BookTapped?.Invoke(this, bookInfo.CardId);
    }

    private async void OnMenuClicked(object sender, EventArgs e)
    {
      if (BindingContext is not BookInfoViewModel bookInfo)
        return;

      var page = HostPageForDialogs();
      if (page is not ContentPage cp)
        return;

      var readAction = bookInfo.IsReadDone ? Strings.Card_Action_ReadUnmark : Strings.Card_Action_ReadMark;
      var favAction = bookInfo.IsFavorite ? Strings.Card_Action_FavUnmark : Strings.Card_Action_FavMark;
      var deleteLabel = Strings.Card_Action_Delete;
      var translateLabel = Strings.Card_Action_Translate;

      var action = await ThemedOverlayPresenter.ShowActionSheetAsync(
        cp,
        Strings.Card_ContextMenuTitle,
        Strings.Common_Cancel,
        readAction,
        favAction,
        deleteLabel,
        translateLabel
      );

      if (action == deleteLabel)
      {
        bool confirm = await ThemedOverlayPresenter.ShowConfirmAsync(
          cp,
          Strings.Card_DeleteConfirmTitle,
          Strings.Card_DeleteConfirmMessage,
          Strings.Card_DeleteConfirmYes,
          Strings.Common_Cancel);
        if (confirm)
          CardDeleteRequested?.Invoke(this, bookInfo.CardId);
        return;
      }

      if (action == translateLabel)
      {
        CardTranslateRequested?.Invoke(this, bookInfo.CardId);
        return;
      }

      if (action == readAction)
      {
        await ToggleReadingStatusAsync(bookInfo);
        return;
      }

      if (action == favAction)
      {
        await ToggleFavoriteAsync(bookInfo);
      }
    }

    static void ApplyReadingStatusToggle(Work work, BookInfoViewModel vm)
    {
      if (work.ReadingStatus == BookStatus.Read)
      {
        if (vm.TotalChars <= 0 || (vm.ReadingPositionOffset <= 0 && vm.Progress <= 0.0001))
          work.ReadingStatus = BookStatus.New;
        else
          work.ReadingStatus = BookStatus.InProgress;
      }
      else
      {
        work.ReadingStatus = BookStatus.Read;
      }
    }

    static void ApplyFavoriteToggle(Work work)
    {
      work.Reaction = work.Reaction == BookReaction.Favorite
          ? BookReaction.Unrated
          : BookReaction.Favorite;
    }

    async Task ToggleReadingStatusAsync(BookInfoViewModel vm)
    {
      try
      {
        var db = new DatabaseService();
        var work = await db.GetWorkByIdAsync(vm.WorkId).ConfigureAwait(true);
        if (work == null)
          return;
        ApplyReadingStatusToggle(work, vm);
        await db.UpdateWorkAsync(work).ConfigureAwait(true);
        MainPageViewModel.Instance?.ApplyWorkToAllLanguageVersions(work);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[BookCard] Toggle read: {ex.Message}");
      }
    }

    async Task ToggleFavoriteAsync(BookInfoViewModel vm)
    {
      try
      {
        var db = new DatabaseService();
        var work = await db.GetWorkByIdAsync(vm.WorkId).ConfigureAwait(true);
        if (work == null)
          return;
        ApplyFavoriteToggle(work);
        await db.UpdateWorkAsync(work).ConfigureAwait(true);
        MainPageViewModel.Instance?.ApplyWorkToAllLanguageVersions(work);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[BookCard] Toggle favorite: {ex.Message}");
      }
    }
  }
}
