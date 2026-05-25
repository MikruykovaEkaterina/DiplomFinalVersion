using BookReaderApp.Models;
using BookReaderApp.Views;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace BookReaderApp.Services;

/// <summary>
/// Нижний баннер поверх контента страницы (без строки в Grid — экран не «подпрыгивает»);
/// таймер, ✕, свайп вниз — закрытие; тап по полосе (при openCardId) — открыть книгу.
/// </summary>
static class InAppNotificationPresenter
{
  const string HostAutomationId = "AppNotificationPageHost";
  static CancellationTokenSource? _timerCts;
  static InAppNotificationBar? _activeBar;
  static EventHandler<ShellNavigatedEventArgs>? _navHandler;

  /// <summary>Закрывает активный нижний баннер программно (Shell, загрузчик и т.д.).</summary>
  public static void DismissCurrent()
  {
    RunOnMainThread(() => _activeBar?.SignalDismiss());
  }

  static void RunOnMainThread(Action action)
  {
    if (MainThread.IsMainThread)
      action();
    else
      MainThread.BeginInvokeOnMainThread(action);
  }

  /// <summary>
  /// Показывает баннер на текущей <see cref="ContentPage"/>; по таймеру/жесту закрывается; при тапе и <paramref name="openCardId"/> вызывает <paramref name="openBookAsync"/>.
  /// </summary>
  public static async Task<bool> ShowAsync(
      string message,
      AppNotificationSeverity severity,
      TimeSpan duration,
      int? openCardId,
      Func<int, Task> openBookAsync)
  {
    Grid? host = null;
    InAppNotificationBar? bar = null;

    var mounted = await MainThread.InvokeOnMainThreadAsync(() =>
    {
      var page = ResolveCurrentContentPage();
      if (page == null)
        return false;

      host = EnsureHostGrid(page);
      RemoveExistingBanner(host);

      bool tapOpens = openCardId is > 0;
      bar = new InAppNotificationBar(message, severity, tapOpens, openCardId ?? 0);
      Grid.SetRow(bar, 0);
      bar.VerticalOptions = LayoutOptions.End;
      bar.HorizontalOptions = LayoutOptions.Fill;
      host.Children.Add(bar);

      _activeBar = bar;
      AttachNavDismiss();

      _timerCts?.Cancel();
      _timerCts = new CancellationTokenSource();
      _ = RunDismissTimerAsync(duration, _timerCts.Token, bar);

      return true;
    }).ConfigureAwait(true);

    if (!mounted || bar == null || host == null)
      return false;

    try
    {
      var reason = await bar.WaitForCloseAsync().ConfigureAwait(true);

      if (reason == InAppNotificationCloseReason.OpenBookRequested && openCardId is int cid && cid > 0)
        await openBookAsync(cid).ConfigureAwait(true);

      return true;
    }
    finally
    {
      _timerCts?.Cancel();
      _timerCts = null;
      if (_activeBar == bar)
        _activeBar = null;
      DetachNavDismiss();

      await MainThread.InvokeOnMainThreadAsync(() =>
      {
        if (bar != null)
        {
          for (int i = 0; i < host.Children.Count; i++)
          {
            if (ReferenceEquals(host.Children[i], bar))
            {
              host.Children.Remove(bar);
              break;
            }
          }
        }
      }).ConfigureAwait(true);
    }
  }

  static async Task RunDismissTimerAsync(TimeSpan duration, CancellationToken token, InAppNotificationBar bar)
  {
    try
    {
      await Task.Delay(duration, token).ConfigureAwait(false);
      await MainThread.InvokeOnMainThreadAsync(() => bar.SignalDismiss()).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
    }
  }

  static void AttachNavDismiss()
  {
    if (Shell.Current == null || _navHandler != null)
      return;
    _navHandler = (_, _) => DismissCurrent();
    Shell.Current.Navigated += _navHandler;
  }

  static void DetachNavDismiss()
  {
    if (Shell.Current == null || _navHandler == null)
      return;
    Shell.Current.Navigated -= _navHandler;
    _navHandler = null;
  }

  static ContentPage? ResolveCurrentContentPage()
  {
    if (Shell.Current?.CurrentPage is ContentPage cp)
      return cp;
    return Application.Current?.Windows.FirstOrDefault()?.Page as ContentPage;
  }

  /// <summary>Старый вариант: две строки (* + Auto) — контент прыгал. Перестраиваем в один ряд с оверлеем.</summary>
  static void MigrateLegacyTwoRowHostToOverlay(Grid host)
  {
    View? main = null;
    var banners = new List<InAppNotificationBar>();
    foreach (var ch in host.Children.ToArray())
    {
      if (ch is InAppNotificationBar b)
        banners.Add(b);
      else if (ch is View v)
        main = v;
    }

    host.Children.Clear();
    host.RowDefinitions.Clear();
    host.RowDefinitions.Add(new RowDefinition(GridLength.Star));

    if (main != null)
    {
      Grid.SetRow(main, 0);
      main.VerticalOptions = LayoutOptions.Fill;
      main.HorizontalOptions = LayoutOptions.Fill;
      host.Children.Add(main);
    }

    foreach (var b in banners)
    {
      Grid.SetRow(b, 0);
      b.VerticalOptions = LayoutOptions.End;
      b.HorizontalOptions = LayoutOptions.Fill;
      host.Children.Add(b);
    }
  }

  static Grid EnsureHostGrid(ContentPage page)
  {
    if (page.Content is Grid g && g.AutomationId == HostAutomationId)
    {
      if (g.RowDefinitions.Count != 1)
        MigrateLegacyTwoRowHostToOverlay(g);
      return g;
    }

    var oldContent = page.Content;
    page.Content = null;

    var host = new Grid
    {
      AutomationId = HostAutomationId,
      BackgroundColor = Colors.Transparent,
      RowDefinitions = new RowDefinitionCollection { new RowDefinition(GridLength.Star) }
    };

    if (oldContent != null && oldContent is View v)
    {
      Grid.SetRow(v, 0);
      v.VerticalOptions = LayoutOptions.Fill;
      v.HorizontalOptions = LayoutOptions.Fill;
      host.Children.Add((IView)oldContent);
    }

    page.Content = host;
    return host;
  }

  static void RemoveExistingBanner(Grid host)
  {
    foreach (var ch in host.Children.ToArray())
    {
      if (ch is InAppNotificationBar b)
        host.Children.Remove(b);
    }
  }

  /// <summary>Возвращает однострочный хост-Grid с оверлеем для модалок и баннеров на той же странице.</summary>
  internal static Grid ObtainOverlayHost(ContentPage page) => EnsureHostGrid(page);
}
