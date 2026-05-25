namespace BookReaderApp.Models;

/// <summary>Причина закрытия in-app баннера: отмена или переход к книге.</summary>
public enum InAppNotificationCloseReason
{
  /// <summary>Закрыто жестом, кнопкой, таймером или внешним вызовом.</summary>
  Dismissed,
  /// <summary>Пользователь тапнул по баннеру, чтобы открыть связанную книгу.</summary>
  OpenBookRequested,
}
