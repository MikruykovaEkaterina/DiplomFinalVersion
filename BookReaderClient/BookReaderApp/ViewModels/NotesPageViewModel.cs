using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using BookReaderApp.Models;
using Microsoft.Maui.Controls;
using BookReaderApp.Resources;
using BookReaderApp.Services;

namespace BookReaderApp.ViewModels;

/// <summary>
/// Список заметок произведения: загрузка из БД, команды открытия, добавления и удаления; связь со страницей через делегаты.
/// </summary>
public sealed class NotesPageViewModel : INotifyPropertyChanged
{
  readonly IDatabaseService _db;
  int _workId;
  bool _isEmpty;

  /// <summary>Создаёт команды и сохраняет сервис данных.</summary>
  public NotesPageViewModel(IDatabaseService databaseService)
  {
    _db = databaseService;
    OpenRowCommand = new Command<NoteListItemViewModel>(async item =>
    {
      if (item == null || OnOpenNoteFromList == null)
        return;
      await OnOpenNoteFromList(item.Model);
    });
    AddNoteCommand = new Command(async () =>
    {
      if (OnAddNoteRequested == null)
        return;
      await OnAddNoteRequested();
    });
    DeleteNoteCommand = new Command<NoteListItemViewModel>(item => _ = TryDeleteNoteAsync(item));
  }

  /// <summary>Элементы списка заметок (новые сверху).</summary>
  public ObservableCollection<NoteListItemViewModel> Items { get; } = new();

  /// <summary>Выбор заметки: переход к месту в книге (после закрытия списка вызывает ReadingPage).</summary>
  public Func<Note, Task>? OnOpenNoteFromList { get; set; }

  /// <summary>«Добавить заметку»: закрыть список и показать диалог на экране чтения.</summary>
  public Func<Task>? OnAddNoteRequested { get; set; }

  /// <summary>Подтверждение удаления; страница показывает оверлей, возвращает true — удалять.</summary>
  public Func<Task<bool>>? ConfirmDeleteNoteAsync { get; set; }

  /// <summary>Открыть выбранную строку (переход к якорю в тексте).</summary>
  public Command<NoteListItemViewModel> OpenRowCommand { get; }

  /// <summary>Запросить добавление заметки на экране чтения.</summary>
  public Command AddNoteCommand { get; }

  /// <summary>Удалить заметку после подтверждения.</summary>
  public Command<NoteListItemViewModel> DeleteNoteCommand { get; }

  /// <summary>Нет ни одной заметки для произведения.</summary>
  public bool IsEmpty
  {
    get => _isEmpty;
    private set
    {
      if (_isEmpty == value)
        return;
      _isEmpty = value;
      OnPropertyChanged(nameof(IsEmpty));
    }
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  /// <summary>Загружает заметки по <paramref name="workId"/> и наполняет <see cref="Items"/>.</summary>
  public async Task LoadAsync(int workId)
  {
    _workId = workId;
    var notes = await _db.GetNotesByWorkIdAsync(workId).ConfigureAwait(true);
    var list = notes.OrderByDescending(n => n.CreatedDate).ToList();

    Items.Clear();
    foreach (var n in list)
    {
      string title = (n.Title ?? "").Trim();
      string titleDisplay = string.IsNullOrEmpty(title) ? Strings.Notes_Untitled : title;

      Items.Add(new NoteListItemViewModel(n, titleDisplay));
    }

    IsEmpty = Items.Count == 0;
  }

  /// <summary>Удаляет заметку после успешного <see cref="ConfirmDeleteNoteAsync"/> и перезагружает список.</summary>
  async Task TryDeleteNoteAsync(NoteListItemViewModel? item)
  {
    if (item == null || ConfirmDeleteNoteAsync == null)
      return;
    try
    {
      if (!await ConfirmDeleteNoteAsync().ConfigureAwait(true))
        return;
      await _db.DeleteNoteAsync(item.Model.Id).ConfigureAwait(true);
      await LoadAsync(_workId).ConfigureAwait(true);
      var notify = ServiceLocator.Get<IAppNotificationService>();
      notify?.Show(Strings.Notes_DeletedSuccess, AppNotificationSeverity.Success, TimeSpan.FromSeconds(3));
    }
    catch (Exception ex)
    {
      Debug.WriteLine($"[NotesPageViewModel] Delete note: {ex.Message}");
    }
  }

  void OnPropertyChanged([CallerMemberName] string? name = null) =>
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
