using Microsoft.Maui.Controls;
using BookReaderApp.ViewModels;
using System;
using System.ComponentModel;
using System.Linq;

namespace BookReaderApp.Views
{
  /// <summary>
  /// Обёртка над <see cref="BookCardView"/> для группы языковых версий одной работы (<see cref="BookGroupViewModel"/>):
  /// индикатор версии и переключение влево/вправо (стрелки и зоны тапа). Пробрасывает события открытия книги
  /// и запросов удаления/перевода с вложенной карточки наружу.
  /// </summary>
  public partial class BookCardSwipeView : ContentView
  {
    private BookGroupViewModel _bookGroup;
    private double _initialTotalX = 0;
    private const double SwipeThreshold = 80;

    public event EventHandler<int> BookTapped;
    public event EventHandler<int> CardDeleteRequested;
    public event EventHandler<int> CardTranslateRequested;

    public BookCardSwipeView()
    {
      InitializeComponent();
      CurrentCardView.CardDeleteRequested += (_, cardId) => CardDeleteRequested?.Invoke(this, cardId);
      CurrentCardView.CardTranslateRequested += (_, cardId) => CardTranslateRequested?.Invoke(this, cardId);
      this.BindingContextChanged += OnBindingContextChanged;
      
      // Ждём загрузки элементов перед добавлением жестов
      this.Loaded += OnLoaded;
    }

    private void AttachArrowTapGestures()
    {
      AttachTapToHost(LeftArrowHost, OnLeftArrowTapped);
      AttachTapToHost(RightArrowHost, OnRightArrowTapped);
      AttachTapToHost(VersionTapLeft, OnLeftArrowTapped);
      AttachTapToHost(VersionTapRight, OnRightArrowTapped);
    }

    static void AttachTapToHost(Grid? host, EventHandler<TappedEventArgs> handler)
    {
      if (host == null)
        return;
      if (host.GestureRecognizers.OfType<TapGestureRecognizer>().Any())
        return;
      var tap = new TapGestureRecognizer();
      tap.Tapped += handler;
      host.GestureRecognizers.Add(tap);
    }

    private void OnLeftArrowTapped(object sender, TappedEventArgs e)
    {
      if (_bookGroup?.CanSwipeLeft == true)
        _bookGroup.SwipeLeft();
    }

    private void OnRightArrowTapped(object sender, TappedEventArgs e)
    {
      if (_bookGroup?.CanSwipeRight == true)
        _bookGroup.SwipeRight();
    }

    private void OnLoaded(object sender, EventArgs e)
    {
      // Версии переключаем только по кнопкам, поэтому Pan-свайп не подключаем.
      // (Pan-обработчик может оставаться в коде, но жесты не добавляем.)
      AttachArrowTapGestures();
    }

    private void AddGestureToBorder(ContentView cardView)
    {
      try
      {
        // Ищем Border внутри BookCardView
        if (cardView.Content is Border border)
        {
          var borderPanGesture = new PanGestureRecognizer();
          borderPanGesture.PanUpdated += OnPanUpdated;
          borderPanGesture.TouchPoints = 1;
          
          // Очищаем существующие жесты пана на Border (если есть)
          var panGesturesToRemove = border.GestureRecognizers
            .OfType<PanGestureRecognizer>()
            .ToList();
          
          foreach (var gesture in panGesturesToRemove)
          {
            border.GestureRecognizers.Remove(gesture);
          }
          
          border.GestureRecognizers.Add(borderPanGesture);
          System.Diagnostics.Debug.WriteLine("[BookCardSwipeView] Жесты также добавлены к Border внутри BookCardView");
        }
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Ошибка добавления жестов к Border: {ex.Message}");
      }
    }

    private void OnBindingContextChanged(object sender, EventArgs e)
    {
      System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] BindingContextChanged: {BindingContext?.GetType().Name ?? "null"}");
      
      if (BindingContext is BookGroupViewModel bookGroup)
      {
        System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Установлен BookGroupViewModel с WorkId={bookGroup.WorkId}, Versions={bookGroup.LanguageVersions.Count}");
        SetBookGroup(bookGroup);
      }
      else if (BindingContext != null)
      {
        System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] BindingContext не является BookGroupViewModel: {BindingContext.GetType().FullName}");
      }
    }

    public void SetBookGroup(BookGroupViewModel bookGroup)
    {
      // Отписываемся от старой группы
      if (_bookGroup != null)
      {
        _bookGroup.PropertyChanged -= OnBookGroupPropertyChanged;
      }

      _bookGroup = bookGroup;
      UpdateCurrentCard();
      UpdateVersionIndicator();
      
      // Подписываемся на изменения индекса и других свойств
      if (_bookGroup != null)
      {
        _bookGroup.PropertyChanged += OnBookGroupPropertyChanged;
      }
    }

    private void OnBookGroupPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
      if (e.PropertyName == nameof(BookGroupViewModel.CurrentIndex) ||
          e.PropertyName == nameof(BookGroupViewModel.CurrentBook) ||
          e.PropertyName == nameof(BookGroupViewModel.HasMultipleVersions) ||
          e.PropertyName == nameof(BookGroupViewModel.CanSwipeLeft) ||
          e.PropertyName == nameof(BookGroupViewModel.CanSwipeRight))
      {
        UpdateCurrentCard();
        UpdateVersionIndicator();
      }
    }

    private void UpdateCurrentCard()
    {
      if (_bookGroup != null)
      {
        var currentBook = _bookGroup.CurrentBook;
        if (currentBook != null)
        {
          // Явно устанавливаем BindingContext, чтобы переопределить наследование от BookGroupViewModel
          CurrentCardView.BindingContext = currentBook;
          System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Обновлена карточка: {currentBook.Title} (Index: {_bookGroup.CurrentIndex}/{_bookGroup.LanguageVersions.Count - 1}, Versions: {_bookGroup.LanguageVersions.Count}, BindingContext={CurrentCardView.BindingContext?.GetType().Name})");
        }
        else
        {
          System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] CurrentBook is null (Index: {_bookGroup.CurrentIndex}, Versions: {_bookGroup.LanguageVersions.Count})");
        }
      }
      else
      {
        System.Diagnostics.Debug.WriteLine("[BookCardSwipeView] _bookGroup is null");
      }
    }

    private void UpdateVersionIndicator()
    {
      if (_bookGroup == null)
      {
        VersionIndicator.IsVisible = false;
        return;
      }

      bool hasMultipleVersions = _bookGroup.HasMultipleVersions;
      VersionIndicator.IsVisible = hasMultipleVersions;

      if (!hasMultipleVersions)
        return;

      // Обновляем стрелки
      if (LeftArrow != null)
      {
        LeftArrow.Opacity = _bookGroup.CanSwipeLeft ? 1.0 : 0.3;
      }

      if (RightArrow != null)
      {
        RightArrow.Opacity = _bookGroup.CanSwipeRight ? 1.0 : 0.3;
      }

      // Обновляем точки индикатора
      if (DotsContainer != null)
      {
        DotsContainer.Children.Clear();
        
        int versionCount = _bookGroup.LanguageVersions.Count;
        int currentIndex = _bookGroup.CurrentIndex;

        // Получаем цвет из ресурсов или используем серый по умолчанию
        Color dotColor = Colors.Gray;
        try
        {
          if (Application.Current?.Resources.TryGetValue("PrimaryColor", out var colorObj) == true && colorObj is Color primaryColor)
          {
            dotColor = primaryColor;
          }
        }
        catch { }

        for (int i = 0; i < versionCount; i++)
        {
          var dot = new BoxView
          {
            WidthRequest = 8,
            HeightRequest = 8,
            CornerRadius = 4,
            BackgroundColor = dotColor,
            Opacity = i == currentIndex ? 1.0 : 0.3
          };
          DotsContainer.Children.Add(dot);
        }
      }
    }

    private bool _isHorizontalSwipe = false;
    private bool _swipeDirectionDetermined = false;
    private bool _swipePerformed = false; // Флаг, чтобы переключение происходило только один раз за жест

    private void OnPanUpdated(object sender, PanUpdatedEventArgs e)
    {
      // Всегда логируем для отладки
      System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] OnPanUpdated вызван! Status={e.StatusType}, TotalX={e.TotalX}, TotalY={e.TotalY}, Sender={sender?.GetType().Name}");
      
      if (_bookGroup == null || !_bookGroup.HasMultipleVersions)
      {
        System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Свайп заблокирован: _bookGroup={_bookGroup != null}, HasMultipleVersions={_bookGroup?.HasMultipleVersions}");
        return;
      }

      System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] OnPanUpdated: Status={e.StatusType}, TotalX={e.TotalX}, TotalY={e.TotalY}, InitialTotalX={_initialTotalX}, CurrentIndex={_bookGroup.CurrentIndex}");

      switch (e.StatusType)
      {
        case GestureStatus.Started:
          _initialTotalX = e.TotalX;
          _isHorizontalSwipe = false;
          _swipeDirectionDetermined = false;
          _swipePerformed = false;
          System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Свайп начат: InitialTotalX={_initialTotalX}");
          break;

        //case GestureStatus.Running:
        //  // Если InitialTotalX еще не установлен, устанавливаем его сейчас
        //  if (_initialTotalX == 0)
        //  {
        //    _initialTotalX = e.TotalX;
        //  }
          
        //  double deltaX = e.TotalX - _initialTotalX;
        //  double deltaY = e.TotalY;
        //  double absDeltaX = Math.Abs(deltaX);
        //  double absDeltaY = Math.Abs(deltaY);
          
        //  // Определяем направление жеста при первом значительном движении
        //  if (!_swipeDirectionDetermined && (absDeltaX > 10 || absDeltaY > 10))
        //  {
        //    _isHorizontalSwipe = absDeltaX > absDeltaY;
        //    _swipeDirectionDetermined = true;
        //    System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Направление определено: Horizontal={_isHorizontalSwipe}, absDeltaX={absDeltaX}, absDeltaY={absDeltaY}");
        //  }
          
        //  // Игнорируем вертикальные жесты (скролл списка)
        //  if (!_isHorizontalSwipe)
        //  {
        //    System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Игнорируем вертикальный жест: deltaY={deltaY}, deltaX={deltaX}");
        //    return;
        //  }
          
        //  System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Свайп выполняется: deltaX={deltaX}, CanSwipeLeft={_bookGroup.CanSwipeLeft}, CanSwipeRight={_bookGroup.CanSwipeRight}, CurrentIndex={_bookGroup.CurrentIndex}");
          
        //  // НЕ применяем визуальное смещение - просто отслеживаем движение для определения свайпа
        //  // Карточка будет меняться мгновенно при достижении порога
        //  break;

        case GestureStatus.Running:
          // Если InitialTotalX еще не установлен, устанавливаем его сейчас
          if (_initialTotalX == 0)
          {
            _initialTotalX = e.TotalX;
          }
          
          double deltaX = e.TotalX - _initialTotalX;
          double deltaY = e.TotalY;
          double absDeltaX = Math.Abs(deltaX);
          double absDeltaY = Math.Abs(deltaY);
          
          // Определяем направление жеста при первом значительном движении
          if (!_swipeDirectionDetermined && (absDeltaX > 10 || absDeltaY > 10))
          {
            _isHorizontalSwipe = absDeltaX > absDeltaY;
            _swipeDirectionDetermined = true;
            System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Направление определено: Horizontal={_isHorizontalSwipe}, absDeltaX={absDeltaX}, absDeltaY={absDeltaY}");
          }
          
          // Игнорируем вертикальные жесты (скролл списка)
          if (!_isHorizontalSwipe)
          {
            return;
          }
          
          System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Свайп выполняется: deltaX={deltaX}, CanSwipeLeft={_bookGroup.CanSwipeLeft}, CanSwipeRight={_bookGroup.CanSwipeRight}, CurrentIndex={_bookGroup.CurrentIndex}, SwipePerformed={_swipePerformed}");
          
          // Переключаем карточку сразу при достижении порога (только один раз за жест)
          if (!_swipePerformed && Math.Abs(deltaX) > SwipeThreshold)
          {
            if (deltaX > 0 && _bookGroup.CanSwipeRight)
            {
              System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Переключаем вправо: {_bookGroup.CurrentIndex} -> {_bookGroup.CurrentIndex + 1}");
              _bookGroup.SwipeRight();
              _swipePerformed = true;
              _initialTotalX = e.TotalX; // Сбрасываем начальную точку
            }
            else if (deltaX < 0 && _bookGroup.CanSwipeLeft)
            {
              System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Переключаем влево: {_bookGroup.CurrentIndex} -> {_bookGroup.CurrentIndex - 1}");
              _bookGroup.SwipeLeft();
              _swipePerformed = true;
              _initialTotalX = e.TotalX; // Сбрасываем начальную точку
            }
          }
          break;

        case GestureStatus.Completed:
        case GestureStatus.Canceled:
          // Определяем, был ли это свайп (на случай, если переключение не произошло во время движения)
          double finalDeltaX = e.TotalX - _initialTotalX;
          
          System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Свайп завершён: finalDeltaX={finalDeltaX}, Threshold={SwipeThreshold}, CurrentIndex={_bookGroup.CurrentIndex}, IsHorizontal={_isHorizontalSwipe}, SwipePerformed={_swipePerformed}");
          
          // Обрабатываем только горизонтальные свайпы, если переключение еще не произошло
          if (!_swipePerformed && _isHorizontalSwipe && Math.Abs(finalDeltaX) > SwipeThreshold)
          {
            if (finalDeltaX > 0 && _bookGroup.CanSwipeRight)
            {
              System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Выполняем свайп вправо при завершении: {_bookGroup.CurrentIndex} -> {_bookGroup.CurrentIndex + 1}");
              _bookGroup.SwipeRight();
            }
            else if (finalDeltaX < 0 && _bookGroup.CanSwipeLeft)
            {
              System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Выполняем свайп влево при завершении: {_bookGroup.CurrentIndex} -> {_bookGroup.CurrentIndex - 1}");
              _bookGroup.SwipeLeft();
            }
            else
            {
              System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Свайп заблокирован: finalDeltaX={finalDeltaX}, CanSwipeLeft={_bookGroup.CanSwipeLeft}, CanSwipeRight={_bookGroup.CanSwipeRight}");
            }
          }
          else if (!_isHorizontalSwipe)
          {
            System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Игнорируем завершение вертикального жеста");
          }
          else if (_swipePerformed)
          {
            System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Свайп уже выполнен во время движения");
          }
          else
          {
            System.Diagnostics.Debug.WriteLine($"[BookCardSwipeView] Свайп недостаточно сильный: {Math.Abs(finalDeltaX)} < {SwipeThreshold}");
          }

          // Сбрасываем состояние
          _initialTotalX = 0;
          _isHorizontalSwipe = false;
          _swipeDirectionDetermined = false;
          _swipePerformed = false;
          break;
      }
    }


    private async void OnBookTapped(object sender, int cardId)
    {
      BookTapped?.Invoke(this, cardId);
    }
  }
}

