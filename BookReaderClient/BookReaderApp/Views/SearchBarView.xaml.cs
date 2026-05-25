using System.ComponentModel;
using BookReaderApp.Helpers;
using BookReaderApp.Localization;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using BookReaderApp.ViewModels;
using Microsoft.Maui.Controls;

namespace BookReaderApp.Views
{
  /// <summary>
  /// Раскрываемая панель поиска на главной: поля и пикеры фильтров (язык, статус, реакция, сортировка),
  /// сброс и «Показать результаты». Работает с <see cref="SearchFilterViewModel"/>; события
  /// <see cref="SearchReset"/> и <see cref="SearchApplied"/> сигнализируют странице перестроить список книг.
  /// </summary>
  public partial class SearchBarView : ContentView
  {
    PropertyChangedEventHandler? _localizationChangedHandler;

    /// <summary>Список на главной нужно перестроить после сброса фильтров.</summary>
    public event EventHandler? SearchReset;

    /// <summary>Применены фильтры — перестроить список (после нажатия «Показать результаты»).</summary>
    public event EventHandler? SearchApplied;

    public SearchBarView()
    {
      InitializeComponent();

      SearchExpander.ExpandedChanged += (_, _) => UpdateSearchButton();
      UpdateSearchButton();

      _localizationChangedHandler = (_, e) =>
      {
        if (!string.IsNullOrEmpty(e.PropertyName) &&
            e.PropertyName != nameof(LocalizationResourceManager.CurrentCulture))
          return;
        MainThread.BeginInvokeOnMainThread(UpdateSearchButton);
      };
      LocalizationResourceManager.Instance.PropertyChanged += _localizationChangedHandler;
      Unloaded += (_, _) =>
      {
        if (_localizationChangedHandler != null)
          LocalizationResourceManager.Instance.PropertyChanged -= _localizationChangedHandler;
      };
    }

    private void OnFilterButtonClicked(object sender, EventArgs e)
    {
      UnfocusAllEntries();
      SearchExpander.IsExpanded = !SearchExpander.IsExpanded;
      UpdateSearchButton();
    }

    async void OnSearchLanguageTapped(object sender, EventArgs e) =>
        await PickSearchEnumAsync(
            vm => vm.LanguageOptions,
            LocalizedEnumHelper.GetBookLanguageString,
            (vm, v) => vm.SelectedLanguage = v,
            Strings.SelectLanguageTitle).ConfigureAwait(true);

    async void OnSearchStatusTapped(object sender, EventArgs e) =>
        await PickSearchEnumAsync(
            vm => vm.StatusOptions,
            LocalizedEnumHelper.GetBookStatusString,
            (vm, v) => vm.SelectedStatus = v,
            Strings.Picker_SelectStatus).ConfigureAwait(true);

    async void OnSearchReactionTapped(object sender, EventArgs e) =>
        await PickSearchEnumAsync(
            vm => vm.ReactionOptions,
            LocalizedEnumHelper.GetBookReactionString,
            (vm, v) => vm.SelectedReaction = v,
            Strings.Picker_SelectReaction).ConfigureAwait(true);

    async void OnSearchSortTapped(object sender, EventArgs e) =>
        await PickSearchEnumAsync(
            vm => vm.SortOptions,
            LocalizedEnumHelper.GetBookSortString,
            (vm, v) => vm.SelectedSort = v,
            Strings.Picker_SelectSort).ConfigureAwait(true);

    async Task PickSearchEnumAsync<T>(
        Func<SearchFilterViewModel, List<T>> getOptions,
        Func<T, string> toLabel,
        Action<SearchFilterViewModel, T> apply,
        string sheetTitle)
    {
      if (BindingContext is not SearchFilterViewModel vm)
        return;
      UnfocusAllEntries();
      var items = getOptions(vm);
      await ThemedEnumPickSheet.PickAsync(
          this,
          items,
          toLabel,
          picked => apply(vm, picked),
          sheetTitle).ConfigureAwait(true);
    }

    /// <summary>Обновляет подпись кнопки поиска после смены языка или состояния раскрытия.</summary>
    public void RefreshSearchChrome() =>
        MainThread.BeginInvokeOnMainThread(UpdateSearchButton);

    private void UpdateSearchButton()
    {
      SearchButtonText.Text = SearchExpander.IsExpanded
        ? Strings.Search_CollapseSearch
        : Strings.SearchButtonLabel;
      var arrowKey = SearchExpander.IsExpanded ? "UiIconArrowUpLight" : "UiIconArrowDownLight";
      if (Application.Current?.Resources.TryGetValue(arrowKey, out var av) == true && av is ImageSource aimg)
        SearchButtonArrow.Source = aimg;
      else
        SearchButtonArrow.Source = SearchExpander.IsExpanded
          ? ImageSource.FromFile("arrow_up_light.svg")
          : ImageSource.FromFile("arrow_down_light.svg");
    }

    private void OnResetClicked(object sender, EventArgs e)
    {
      UnfocusAllEntries();
      if (BindingContext is SearchFilterViewModel vm)
        vm.Reset();
      SearchReset?.Invoke(this, EventArgs.Empty);
    }

    private void OnShowResultsClicked(object sender, EventArgs e)
    {
      UnfocusAllEntries();
      if (BindingContext is SearchFilterViewModel vm && !vm.CanApplySearch)
        return;
      SearchExpander.IsExpanded = false;
      UpdateSearchButton();
      SearchApplied?.Invoke(this, EventArgs.Empty);
    }

    public void UnfocusAllEntries()
    {
      TitleEntry?.Unfocus();
      AuthorEntry?.Unfocus();
      PageFromEntry?.Unfocus();
      PageToEntry?.Unfocus();
    }
  }
}
