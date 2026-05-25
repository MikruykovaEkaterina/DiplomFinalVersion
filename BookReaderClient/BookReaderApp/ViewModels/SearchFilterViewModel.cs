using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using BookReaderApp.Localization;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using Microsoft.Maui.Storage;

namespace BookReaderApp.ViewModels
{
  /// <summary>
  /// Панель поиска: <b>черновик</b> (поля в UI) и <b>применённые</b> значения (каталог и сохранение).
  /// В список попадают только применённые; они же пишутся в Preferences по кнопке «Показать результаты».
  /// </summary>
  public class SearchFilterViewModel : INotifyPropertyChanged
  {
    /// <summary>Максимальная длина полей «Название» и «Автор» в символах.</summary>
    public const int MaxTextFieldLength = 300;

    const string PrefHasSaved = "catalog_search_applied_v1";
    const string PrefTitle = "catalog_search_applied_title";
    const string PrefAuthor = "catalog_search_applied_author";
    const string PrefPageFrom = "catalog_search_applied_pf";
    const string PrefPageTo = "catalog_search_applied_pt";
    const string PrefLang = "catalog_search_applied_lang";
    const string PrefStatus = "catalog_search_applied_status";
    const string PrefReaction = "catalog_search_applied_reaction";
    const string PrefSort = "catalog_search_applied_sort";

    /// <summary>Все значения языка книги для пикера.</summary>
    public List<BookLanguage> LanguageOptions { get; }

    /// <summary>Варианты фильтра по статусу чтения.</summary>
    public List<BookStatus> StatusOptions { get; }

    /// <summary>Варианты фильтра по реакции.</summary>
    public List<BookReaction> ReactionOptions { get; }

    /// <summary>Варианты сортировки каталога.</summary>
    public List<BookSort> SortOptions { get; }

    // —— Черновик (виджеты панели) ——

    private BookLanguage _selectedLanguage;

    /// <summary>Черновик: выбранный язык в панели поиска.</summary>
    public BookLanguage SelectedLanguage
    {
      get => _selectedLanguage;
      set { if (_selectedLanguage != value) { _selectedLanguage = value; OnPropertyChanged(); } }
    }

    private BookStatus _selectedStatus;

    /// <summary>Черновик: статус чтения в панели.</summary>
    public BookStatus SelectedStatus
    {
      get => _selectedStatus;
      set { if (_selectedStatus != value) { _selectedStatus = value; OnPropertyChanged(); } }
    }

    private BookReaction _selectedReaction;

    /// <summary>Черновик: реакция в панели.</summary>
    public BookReaction SelectedReaction
    {
      get => _selectedReaction;
      set { if (_selectedReaction != value) { _selectedReaction = value; OnPropertyChanged(); } }
    }

    private BookSort _selectedSort;

    /// <summary>Черновик: выбранная сортировка до нажатия «Показать результаты».</summary>
    public BookSort SelectedSort
    {
      get => _selectedSort;
      set { if (_selectedSort != value) { _selectedSort = value; OnPropertyChanged(); } }
    }

    private string _bookTitle = "";

    /// <summary>Черновик: подстрока названия (обрезается до <see cref="MaxTextFieldLength"/>).</summary>
    public string BookTitle
    {
      get => _bookTitle;
      set
      {
        value = ClampText(value);
        if (_bookTitle == value) return;
        _bookTitle = value;
        OnPropertyChanged();
      }
    }

    private string _bookAuthor = "";

    /// <summary>Черновик: подстрока автора.</summary>
    public string BookAuthor
    {
      get => _bookAuthor;
      set
      {
        value = ClampText(value);
        if (_bookAuthor == value) return;
        _bookAuthor = value;
        OnPropertyChanged();
      }
    }

    private string _bookPageFrom = "";

    /// <summary>Черновик: нижняя граница диапазона страниц (ввод пользователя).</summary>
    public string BookPageFrom
    {
      get => _bookPageFrom;
      set
      {
        if (_bookPageFrom == value) return;
        _bookPageFrom = value ?? "";
        OnPropertyChanged();
        ValidatePageFields();
      }
    }

    private string _bookPageTo = "";

    /// <summary>Черновик: верхняя граница диапазона страниц.</summary>
    public string BookPageTo
    {
      get => _bookPageTo;
      set
      {
        if (_bookPageTo == value) return;
        _bookPageTo = value ?? "";
        OnPropertyChanged();
        ValidatePageFields();
      }
    }

    private string _pageFromError = "";

    /// <summary>Сообщение валидации для поля «от страницы».</summary>
    public string PageFromError
    {
      get => _pageFromError;
      private set { if (_pageFromError == value) return; _pageFromError = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(HasPageFromError)); }
    }

    private string _pageToError = "";

    /// <summary>Сообщение валидации для поля «до страницы».</summary>
    public string PageToError
    {
      get => _pageToError;
      private set { if (_pageToError == value) return; _pageToError = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(HasPageToError)); }
    }

    /// <summary>Есть ошибка в поле «от страницы».</summary>
    public bool HasPageFromError => !string.IsNullOrEmpty(PageFromError);

    /// <summary>Есть ошибка в поле «до страницы».</summary>
    public bool HasPageToError => !string.IsNullOrEmpty(PageToError);

    /// <summary>Можно применить поиск (ошибок валидации номеров страниц нет).</summary>
    public bool CanApplySearch => !HasPageFromError && !HasPageToError;

    // —— Применённые (каталог <see cref="BookGroupListQuery"/>) ——

    BookLanguage _appliedLanguage = BookLanguage.None;
    BookStatus _appliedStatus = BookStatus.None;
    BookReaction _appliedReaction = BookReaction.None;
    BookSort _appliedSort = BookSort.DateNew;
    string _appliedBookTitle = "";
    string _appliedBookAuthor = "";
    string _appliedBookPageFrom = "";
    string _appliedBookPageTo = "";

    /// <summary>Применённый фильтр языка (используется <see cref="BookGroupListQuery"/>).</summary>
    public BookLanguage AppliedLanguage => _appliedLanguage;

    /// <summary>Применённый фильтр статуса.</summary>
    public BookStatus AppliedStatus => _appliedStatus;

    /// <summary>Применённый фильтр реакции.</summary>
    public BookReaction AppliedReaction => _appliedReaction;

    /// <summary>Применённая сортировка каталога.</summary>
    public BookSort AppliedSort => _appliedSort;

    /// <summary>Применённая подстрока названия.</summary>
    public string AppliedBookTitle => _appliedBookTitle;

    /// <summary>Применённая подстрока автора.</summary>
    public string AppliedBookAuthor => _appliedBookAuthor;

    /// <summary>Применённое нижнее значение страницы (строка из Preferences).</summary>
    public string AppliedBookPageFrom => _appliedBookPageFrom;

    /// <summary>Применённое верхнее значение страницы.</summary>
    public string AppliedBookPageTo => _appliedBookPageTo;

    /// <summary>Заполняет списки enum, восстанавливает сохранённые применённые фильтры и копирует их в черновик.</summary>
    public SearchFilterViewModel()
    {
      LanguageOptions = Enum.GetValues(typeof(BookLanguage)).Cast<BookLanguage>().ToList();
      StatusOptions = Enum.GetValues(typeof(BookStatus)).Cast<BookStatus>().ToList();
      ReactionOptions = Enum.GetValues(typeof(BookReaction)).Cast<BookReaction>().ToList();
      SortOptions = Enum.GetValues(typeof(BookSort)).Cast<BookSort>().ToList();

      InitializeDefaultsDraftAndApplied();
      TryRestoreAppliedFromPreferences();
      CopyAppliedToDraft();
      LocalizationResourceManager.Instance.PropertyChanged += OnLocalizationChanged;
      ValidatePageFields();
    }

    /// <summary>Сбрасывает черновик и применённые значения к значению по умолчанию.</summary>
    void InitializeDefaultsDraftAndApplied()
    {
      _selectedLanguage = BookLanguage.None;
      _appliedLanguage = BookLanguage.None;
      _selectedStatus = BookStatus.None;
      _appliedStatus = BookStatus.None;
      _selectedReaction = BookReaction.None;
      _appliedReaction = BookReaction.None;
      _selectedSort = BookSort.DateNew;
      _appliedSort = BookSort.DateNew;
      _bookTitle = _appliedBookTitle = "";
      _bookAuthor = _appliedBookAuthor = "";
      _bookPageFrom = _appliedBookPageFrom = "";
      _bookPageTo = _appliedBookPageTo = "";
    }

    /// <summary>«Показать результаты»: перенести черновик в применённые и сохранить в Preferences.</summary>
    public void CommitAppliedFilters()
    {
      if (!CanApplySearch)
        return;
      _appliedBookTitle = _bookTitle ?? "";
      _appliedBookAuthor = _bookAuthor ?? "";
      _appliedBookPageFrom = _bookPageFrom ?? "";
      _appliedBookPageTo = _bookPageTo ?? "";
      _appliedLanguage = _selectedLanguage;
      _appliedStatus = _selectedStatus;
      _appliedReaction = _selectedReaction;
      _appliedSort = _selectedSort;
      PersistAppliedToPreferences();
    }

    /// <summary>Уход с главной без «Показать результаты»: выкинуть незакреплённый черновик.</summary>
    public void RevertUncommittedDraft()
    {
      CopyAppliedToDraft();
    }

    /// <summary>Копирует применённые фильтры в поля черновика и перепроверяет страницы.</summary>
    void CopyAppliedToDraft()
    {
      _bookTitle = _appliedBookTitle ?? "";
      _bookAuthor = _appliedBookAuthor ?? "";
      _bookPageFrom = _appliedBookPageFrom ?? "";
      _bookPageTo = _appliedBookPageTo ?? "";
      _selectedLanguage = _appliedLanguage;
      _selectedStatus = _appliedStatus;
      _selectedReaction = _appliedReaction;
      _selectedSort = _appliedSort;
      OnPropertyChanged(nameof(BookTitle));
      OnPropertyChanged(nameof(BookAuthor));
      OnPropertyChanged(nameof(BookPageFrom));
      OnPropertyChanged(nameof(BookPageTo));
      OnPropertyChanged(nameof(SelectedLanguage));
      OnPropertyChanged(nameof(SelectedStatus));
      OnPropertyChanged(nameof(SelectedReaction));
      OnPropertyChanged(nameof(SelectedSort));
      ValidatePageFields();
    }

    /// <summary>Читает применённые фильтры из <see cref="Preferences"/> при наличии сохранения.</summary>
    void TryRestoreAppliedFromPreferences()
    {
      if (!Preferences.Get(PrefHasSaved, false))
        return;

      _appliedBookTitle = Preferences.Get(PrefTitle, "") ?? "";
      _appliedBookAuthor = Preferences.Get(PrefAuthor, "") ?? "";
      _appliedBookPageFrom = Preferences.Get(PrefPageFrom, "") ?? "";
      _appliedBookPageTo = Preferences.Get(PrefPageTo, "") ?? "";
      ReadEnumPref(PrefLang, ref _appliedLanguage, BookLanguage.None);
      ReadEnumPref(PrefStatus, ref _appliedStatus, BookStatus.None);
      ReadEnumPref(PrefReaction, ref _appliedReaction, BookReaction.None);
      ReadEnumPref(PrefSort, ref _appliedSort, BookSort.DateNew);
    }

    /// <summary>Читает целочисленный enum из Preferences с откатом к значению по умолчанию.</summary>
    static void ReadEnumPref<T>(string key, ref T field, T fallback) where T : struct, Enum
    {
      int fallbackInt = (int)(object)fallback;
      int v = Preferences.Get(key, fallbackInt);
      if (Enum.IsDefined(typeof(T), v))
        field = (T)Enum.ToObject(typeof(T), v);
      else
        field = fallback;
    }

    /// <summary>Сохраняет текущие применённые значения в Preferences.</summary>
    void PersistAppliedToPreferences()
    {
      Preferences.Set(PrefHasSaved, true);
      Preferences.Set(PrefTitle, _appliedBookTitle ?? "");
      Preferences.Set(PrefAuthor, _appliedBookAuthor ?? "");
      Preferences.Set(PrefPageFrom, _appliedBookPageFrom ?? "");
      Preferences.Set(PrefPageTo, _appliedBookPageTo ?? "");
      Preferences.Set(PrefLang, (int)(object)_appliedLanguage);
      Preferences.Set(PrefStatus, (int)(object)_appliedStatus);
      Preferences.Set(PrefReaction, (int)(object)_appliedReaction);
      Preferences.Set(PrefSort, (int)(object)_appliedSort);
    }

    /// <summary>Удаляет ключи сохранённого поиска из Preferences.</summary>
    static void ClearAppliedPreferences()
    {
      Preferences.Remove(PrefHasSaved);
      Preferences.Remove(PrefTitle);
      Preferences.Remove(PrefAuthor);
      Preferences.Remove(PrefPageFrom);
      Preferences.Remove(PrefPageTo);
      Preferences.Remove(PrefLang);
      Preferences.Remove(PrefStatus);
      Preferences.Remove(PrefReaction);
      Preferences.Remove(PrefSort);
    }

    /// <summary>При смене языка UI перепроверяет тексты ошибок валидации страниц.</summary>
    void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
      if (!string.IsNullOrEmpty(e.PropertyName) &&
          e.PropertyName != nameof(LocalizationResourceManager.CurrentCulture))
        return;
      ValidatePageFields();
    }

    /// <summary>Проверяет поля диапазона страниц и обновляет <see cref="CanApplySearch"/>.</summary>
    void ValidatePageFields()
    {
      var fromT = (BookPageFrom ?? "").Trim();
      var toT = (BookPageTo ?? "").Trim();

      PageFromError = "";
      PageToError = "";

      int? fromVal = null;
      int? toVal = null;

      if (fromT.Length > 0)
      {
        if (!int.TryParse(fromT, NumberStyles.Integer, CultureInfo.CurrentCulture, out var fp) || fp < 1)
          PageFromError = Strings.Search_Validation_PagePositive;
        else
          fromVal = fp;
      }

      if (toT.Length > 0)
      {
        if (!int.TryParse(toT, NumberStyles.Integer, CultureInfo.CurrentCulture, out var tp) || tp < 1)
          PageToError = Strings.Search_Validation_PagePositive;
        else
          toVal = tp;
      }

      if (fromVal.HasValue && toVal.HasValue && toVal.Value < fromVal.Value)
        PageToError = Strings.Search_Validation_PageToLessThanFrom;

      OnPropertyChanged(nameof(CanApplySearch));
    }

    /// <summary>Обрезает строку до <see cref="MaxTextFieldLength"/> символов.</summary>
    static string ClampText(string? s)
    {
      if (string.IsNullOrEmpty(s)) return "";
      return s.Length <= MaxTextFieldLength ? s : s.Substring(0, MaxTextFieldLength);
    }

    /// <summary>Сброс панели, применённые фильтры и сохранённые Preferences.</summary>
    public void Reset()
    {
      InitializeDefaultsDraftAndApplied();
      ClearAppliedPreferences();
      CopyAppliedToDraft();
    }

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>Уведомляет подписчиков об изменении свойства.</summary>
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
      => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
  }
}
