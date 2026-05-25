using BookReaderApp.Models;
using BookReaderApp.Resources;
using BookReaderApp.Services;

namespace BookReaderApp.Views;

/// <summary>
/// Нижний баннер: тап по полосе (если есть действие) — открыть книгу; ✕, свайп вниз, внешний SignalDismiss — закрыть.
/// </summary>
public partial class InAppNotificationBar : Border
{
  readonly TaskCompletionSource<InAppNotificationCloseReason> _tcs = new();

  public InAppNotificationBar(
      string message,
      AppNotificationSeverity severity,
      bool tapOpensBook,
      int openCardId)
  {
    InitializeComponent();

    MessageLabel.Text = message;

    var (bg, fg) = NotificationColors.ForSeverity(severity);
    BackgroundColor = bg;
    Stroke = NotificationColors.BorderForSeverity(severity);
    MessageLabel.TextColor = fg;
    CloseGlyph.TextColor = fg;

    SemanticProperties.SetDescription(CloseGlyph, Strings.A11y_Notification_Close);

    if (tapOpensBook && openCardId > 0)
    {
      var tap = new TapGestureRecognizer();
      tap.Tapped += (_, _) => Complete(InAppNotificationCloseReason.OpenBookRequested);
      GestureRecognizers.Add(tap);
    }
  }

  public Task<InAppNotificationCloseReason> WaitForCloseAsync() => _tcs.Task;

  /// <summary>Закрытие снаружи (навигация, таймер, сервис).</summary>
  public void SignalDismiss() => Complete(InAppNotificationCloseReason.Dismissed);

  void OnCloseTapped(object? sender, EventArgs e) => Complete(InAppNotificationCloseReason.Dismissed);

  void OnSwipedDown(object? sender, SwipedEventArgs e) => Complete(InAppNotificationCloseReason.Dismissed);

  void Complete(InAppNotificationCloseReason reason) => _tcs.TrySetResult(reason);
}
