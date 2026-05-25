using BookReaderApp.Resources;

namespace BookReaderApp.Services;

/// <summary>Тексты для перевода предложения и ошибок связи с TranslationProxy (привязка к ресурсам).</summary>
public static class TranslationMessages
{
  /// <summary>Офлайн-сопоставление невозможно (нет пары книг или текста).</summary>
  public static string OfflineUnavailable => Strings.Translation_Msg_OfflineUnavailable;

  /// <summary>Сервис перевода в настройках указан, но ответ на запрос не получен.</summary>
  public static string TranslationProxyUnreachable => Strings.Translation_Msg_ProxyUnreachable;

  /// <summary>Сетевой запрос перевода выполнен с ошибкой тела или статуса.</summary>
  public static string OnlineFailure => Strings.Translation_Msg_OnlineFailure;

  /// <summary>Отмена пользователя или истечение времени запроса к прокси.</summary>
  public static string CancelledOrTimeout => Strings.Translation_Msg_CancelledOrTimeout;

  /// <summary>Не задан или пуст базовый URL TranslationProxy (<see cref="TranslationServerConfig"/>).</summary>
  public static string TranslationServerNotConfigured => Strings.Translation_Msg_ServerNotConfigured;
}
