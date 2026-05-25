using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Threading.Tasks;
using BookReaderApp.Helpers;
using BookReaderApp.Services;
using BookReaderApp.Models;

namespace BookReaderApp.ViewModels
{
  /// <summary>
  /// Модель главного экрана: загрузка библиотеки из БД, группировка карточек по произведениям, фильтрация/сортировка и точечные обновления без полной перезагрузки.
  /// Экземпляр из XAML регистрируется как <see cref="Instance"/> для синхронизации с экраном чтения и карточкой.
  /// </summary>
  public class MainPageViewModel : INotifyPropertyChanged
  {
    private readonly IDatabaseService _databaseService;
    private bool _isLoading;
    private bool _hasBooks;
    private bool _isLoadingInProgress;

    /// <summary>Отображаемые группы книг с учётом поиска и сортировки.</summary>
    public ObservableCollection<BookGroupViewModel> BookGroups { get; } = new();

    /// <summary>Параметры панели поиска (привязка к SearchBarView).</summary>
    public SearchFilterViewModel SearchFilter { get; } = new SearchFilterViewModel();

    /// <summary>Полный список групп после загрузки из БД; <see cref="BookGroups"/> — отфильтрованное/отсортированное представление.</summary>
    private List<BookGroupViewModel> _masterBookGroups = new();

    /// <summary>Индикатор загрузки каталога (оверлей или спиннер).</summary>
    public bool IsLoading
    {
      get => _isLoading;
      set
      {
        if (_isLoading != value)
        {
          _isLoading = value;
          OnPropertyChanged();
        }
      }
    }

    /// <summary>True, если после фильтра есть хотя бы одна группа.</summary>
    public bool HasBooks
    {
      get => _hasBooks;
      set
      {
        if (_hasBooks != value)
        {
          _hasBooks = value;
          OnPropertyChanged();
        }
      }
    }

    private static MainPageViewModel _instance;

    /// <summary>Текущий ViewModel главной страницы (задаётся в конструкторе при создании из XAML).</summary>
    public static MainPageViewModel Instance => _instance;

    /// <summary>Создаёт сервис БД по умолчанию, регистрирует синглтон; загрузка списка выполняется в OnAppearing страницы.</summary>
    public MainPageViewModel()
    {
      _databaseService = new DatabaseService();
      _instance = this;
    }

    /// <summary>Загружает карточки и произведения, собирает группы по <see cref="WorkId"/> и перестраивает видимый список.</summary>
    public async Task LoadBooksAsync()
    {
      if (_isLoadingInProgress)
      {
        System.Diagnostics.Debug.WriteLine("[MainPage] Загрузка уже выполняется, пропускаем...");
        return;
      }

      try
      {
        _isLoadingInProgress = true;
        IsLoading = true;
        System.Diagnostics.Debug.WriteLine("[MainPage] Загрузка книг из БД...");

        var cardsTask = _databaseService.GetAllCardsAsync();
        var worksTask = _databaseService.GetAllWorksAsync();
        var textSettingsTask = _databaseService.GetTextSettingsAsync();
        var positionsTask = _databaseService.GetAllReadingPositionsAsync();
        await Task.WhenAll(cardsTask, worksTask, textSettingsTask, positionsTask).ConfigureAwait(false);

        var cards = await cardsTask.ConfigureAwait(false);
        var works = await worksTask.ConfigureAwait(false);
        var textSettings = await textSettingsTask.ConfigureAwait(false);
        var positions = await positionsTask.ConfigureAwait(false);
        var offsetByCardId = (positions ?? new List<ReadingPosition>())
            .GroupBy(p => p.CardId)
            .ToDictionary(g => g.Key, g => g.First().CharacterOffset);

        System.Diagnostics.Debug.WriteLine($"[MainPage] Найдено карточек: {cards?.Count ?? 0}");

        int charsPerPage = TextReadingLayout.GetCharsPerPage(textSettings);
        var workById = (works ?? new List<Work>()).Where(w => w != null).ToDictionary(w => w.Id);

        var bookGroupsToAdd = await Task.Run(() =>
        {
          var list = new List<BookGroupViewModel>();
          if (cards == null || cards.Count == 0)
            return list;
          foreach (var group in cards.GroupBy(c => c.WorkId))
          {
            var bookGroup = new BookGroupViewModel { WorkId = group.Key };
            foreach (var card in group.OrderBy(c => c.AddedDate))
            {
              workById.TryGetValue(card.WorkId, out var work);
              offsetByCardId.TryGetValue(card.Id, out var posOffset);
              bookGroup.LanguageVersions.Add(BookInfoViewModel.FromCardAndWork(card, work, charsPerPage, posOffset));
            }
            list.Add(bookGroup);
          }
          return list;
        }).ConfigureAwait(false);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
          _masterBookGroups = bookGroupsToAdd;
          RebuildVisibleBookGroups();
        });
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[MainPage] Ошибка загрузки книг: {ex.Message}");
        System.Diagnostics.Debug.WriteLine($"[MainPage] StackTrace: {ex.StackTrace}");
      }
      finally
      {
        IsLoading = false;
        _isLoadingInProgress = false;
      }
    }

    /// <summary>Полная перезагрузка каталога (после загрузки новой книги, смены настроек и т. д.).</summary>
    public async Task RefreshBooksAsync()
    {
      System.Diagnostics.Debug.WriteLine("[MainPage] Обновление списка книг...");
      await LoadBooksAsync();
    }

    /// <summary>Перестраивает <see cref="BookGroups"/> по текущим фильтрам и сортировке из <see cref="SearchFilter"/>.</summary>
    public void RebuildVisibleBookGroups()
    {
      var filtered = BookGroupListQuery.Filter(_masterBookGroups, SearchFilter);
      var sorted = BookGroupListQuery.Sort(filtered, SearchFilter.AppliedSort).ToList();
      BookGroups.Clear();
      foreach (var g in sorted)
        BookGroups.Add(g);
      HasBooks = BookGroups.Count > 0;
    }

    /// <summary>
    /// Обновляет статус/реакцию у всех языковых версий одной книги без полной перезагрузки списка
    /// (видимый список не пересобирается — см. прецедент 2.6).
    /// </summary>
    public void ApplyWorkToAllLanguageVersions(Work work)
    {
      if (work == null) return;
      void Apply()
      {
        foreach (var g in _masterBookGroups)
        {
          if (g.WorkId != work.Id) continue;
          foreach (var vm in g.LanguageVersions)
            vm.ApplyWorkSnapshot(work);
        }
      }

      if (MainThread.IsMainThread)
        Apply();
      else
        MainThread.BeginInvokeOnMainThread(Apply);
    }

    /// <summary>Обновляет дату последнего открытия для одной языковой карточки в кэше списка.</summary>
    public void ApplyLastOpenedToCard(int cardId, DateTime lastOpened)
    {
      void Apply()
      {
        foreach (var g in _masterBookGroups)
        {
          foreach (var vm in g.LanguageVersions)
          {
            if (vm.CardId != cardId) continue;
            vm.SetLastOpened(lastOpened);
          }
        }
      }

      if (MainThread.IsMainThread)
        Apply();
      else
        MainThread.BeginInvokeOnMainThread(Apply);
    }

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>Уведомляет об изменении свойства.</summary>
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
  }
}
