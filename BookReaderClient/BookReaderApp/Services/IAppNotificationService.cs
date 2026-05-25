using BookReaderApp.Models;

namespace BookReaderApp.Services;

/// <summary>Очередь нижних баннеров: длительность, цвет по уровню, опционально открыть книгу по тапу.</summary>
public interface IAppNotificationService
{
  /// <summary>Всплывающее уведомление внизу экрана (единая очередь, крестик и таймер; тап по тексту только для баннеров с действием «открыть книгу»).</summary>
  void Show(string message, AppNotificationSeverity severity = AppNotificationSeverity.Info, TimeSpan? duration = null);

  /// <summary>Результат перевода на сервере недоставен (истёк срок, удалён файл); подавляет дублирующее «перевод готов».</summary>
  void NotifyTranslationFileNoLongerOnServer();

  /// <summary>Закрыть текущее всплывающее уведомление, если открыто.</summary>
  void DismissUploadBanner();

  /// <summary>Недопустимый формат файла при выборе.</summary>
  void ShowUploadInvalidFormat();

  /// <summary>Отмена выбора файла, отмена или ошибка загрузки.</summary>
  void ShowUploadCancelledOrFailed(bool cancelled, string? detail = null);

  /// <summary>Успешная загрузка; тап по тексту открывает книгу (ТЗ).</summary>
  void ShowUploadSuccessOpenBook(int cardId);

  /// <summary>Перевод готов: <paramref name="translatedCardId"/> — карточка перевода; <paramref name="sourceBookTitle"/> — заголовок исходной книги; тап открывает переведённую книгу.</summary>
  void ShowTranslationCompleteOpenBook(int translatedCardId, string sourceBookTitle, BookLanguage targetLanguage);

  /// <summary>Если баннер «перевод готов» не удалось показать, повторно поставить в очередь (редко).</summary>
  void TryReplayMissedTranslationCompleteBanner();
}
