using System.ComponentModel;
using System.Runtime.CompilerServices;
using BookReaderApp.Models;
using Microsoft.Maui.Controls;

namespace BookReaderApp.ViewModels;

/// <summary>Одна строка в списке заметок: заголовок для отображения, разворачивание комментария, команда раскрытия.</summary>
public sealed class NoteListItemViewModel : INotifyPropertyChanged
{
  bool _isExpanded;

  /// <summary>Связывает модель заметки и подпись заголовка в списке.</summary>
  /// <param name="model">Сущность из БД.</param>
  /// <param name="titleDisplay">Текст заголовка или «Без названия».</param>
  public NoteListItemViewModel(Note model, string titleDisplay)
  {
    Model = model;
    TitleDisplay = titleDisplay;
    ToggleExpandCommand = new Command(() =>
    {
      if (!HasComment)
        return;
      IsExpanded = !IsExpanded;
    });
  }

  /// <summary>Данные заметки.</summary>
  public Note Model { get; }

  /// <summary>Заголовок строки (локализованный при отсутствии текста).</summary>
  public string TitleDisplay { get; }

  /// <summary>Текст комментария или пустая строка.</summary>
  public string CommentText => Model.Comment ?? "";

  /// <summary>Есть непустой комментарий для раскрытия.</summary>
  public bool HasComment => !string.IsNullOrWhiteSpace(Model.Comment);

  /// <summary>Показывать тело комментария под заголовком.</summary>
  public bool IsExpandedCommentVisible => HasComment && IsExpanded;

  /// <summary>Развёрнут ли блок комментария.</summary>
  public bool IsExpanded
  {
    get => _isExpanded;
    set
    {
      if (_isExpanded == value)
        return;
      _isExpanded = value;
      OnPropertyChanged(nameof(IsExpanded));
      OnPropertyChanged(nameof(ExpandChevronRotation));
      OnPropertyChanged(nameof(IsExpandedCommentVisible));
    }
  }

  /// <summary>0° — свернуто (стрелка вниз), 180° — развёрнуто.</summary>
  public double ExpandChevronRotation => IsExpanded ? 180 : 0;

  /// <summary>Переключает <see cref="IsExpanded"/>, если есть комментарий.</summary>
  public Command ToggleExpandCommand { get; }

  public event PropertyChangedEventHandler? PropertyChanged;

  void OnPropertyChanged([CallerMemberName] string? name = null) =>
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
