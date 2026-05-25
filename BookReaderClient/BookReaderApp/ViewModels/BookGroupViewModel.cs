using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BookReaderApp.Models;

namespace BookReaderApp.ViewModels
{
  /// <summary>
  /// Группа языковых <see cref="BookInfoViewModel"/> одного произведения (<see cref="WorkId"/>): переключение версии свайпом и подпись текущей карточки.
  /// </summary>
  public class BookGroupViewModel : INotifyPropertyChanged
  {
    /// <summary>Идентификатор произведения в БД (<see cref="Work.Id"/>).</summary>
    public int WorkId { get; set; }

    /// <summary>Все карточки-версии книги (языки), упорядоченные по дате добавления при сборке списка.</summary>
    public ObservableCollection<BookInfoViewModel> LanguageVersions { get; } = new();

    private int _currentIndex = 0;

    /// <summary>Индекс выбранной языковой версии в <see cref="LanguageVersions"/>.</summary>
    public int CurrentIndex
    {
      get => _currentIndex;
      set
      {
        if (_currentIndex != value && value >= 0 && value < LanguageVersions.Count)
        {
          _currentIndex = value;
          OnPropertyChanged();
          OnPropertyChanged(nameof(CurrentBook));
          OnPropertyChanged(nameof(HasMultipleVersions));
          OnPropertyChanged(nameof(CanSwipeLeft));
          OnPropertyChanged(nameof(CanSwipeRight));
        }
      }
    }

    /// <summary>Активная для отображения карточка (по <see cref="CurrentIndex"/> или первая при пустом индексе).</summary>
    public BookInfoViewModel CurrentBook
    {
      get
      {
        if (LanguageVersions.Count == 0)
          return null;

        if (_currentIndex >= 0 && _currentIndex < LanguageVersions.Count)
          return LanguageVersions[_currentIndex];

        return LanguageVersions[0];
      }
    }

    /// <summary>Создаёт группу и подписывается на изменение списка версий для согласованности индекса и подписей свайпа.</summary>
    public BookGroupViewModel()
    {
      LanguageVersions.CollectionChanged += (s, e) =>
      {
        OnPropertyChanged(nameof(CurrentBook));
        OnPropertyChanged(nameof(HasMultipleVersions));
        OnPropertyChanged(nameof(CanSwipeLeft));
        OnPropertyChanged(nameof(CanSwipeRight));

        if (_currentIndex >= LanguageVersions.Count)
        {
          _currentIndex = 0;
          OnPropertyChanged(nameof(CurrentIndex));
        }
      };
    }

    /// <summary>Доступно более одной языковой версии.</summary>
    public bool HasMultipleVersions => LanguageVersions.Count > 1;

    /// <summary>Можно перейти на предыдущую версию в списке.</summary>
    public bool CanSwipeLeft => HasMultipleVersions && _currentIndex > 0;

    /// <summary>Можно перейти на следующую версию в списке.</summary>
    public bool CanSwipeRight => HasMultipleVersions && _currentIndex < LanguageVersions.Count - 1;

    /// <summary>Уменьшает <see cref="CurrentIndex"/> на единицу, если возможно.</summary>
    public void SwipeLeft()
    {
      if (CanSwipeLeft)
        CurrentIndex--;
    }

    /// <summary>Увеличивает <see cref="CurrentIndex"/> на единицу, если возможно.</summary>
    public void SwipeRight()
    {
      if (CanSwipeRight)
        CurrentIndex++;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>Уведомляет подписчиков об изменении свойства.</summary>
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
  }
}
