using BookReaderApp.Models;

namespace BookReaderApp.Services;

/// <summary>Перевод выделенного предложения: сначала офлайн по параллельной карточке, при неудаче — HTTP к TranslationProxy.</summary>
public interface ISentenceTranslationService
{
  /// <summary>
  /// Сначала сопоставление с параллельной карточкой (если есть — без ожидания сети). Если офлайн не дал перевода и задан URL — запрос к TranslationProxy.
  /// Если URL пуст, только офлайн.
  /// </summary>
  Task<SentenceTranslationResult> TranslateSentenceAsync(
      string sentenceText,
      Card currentCard,
      string sourceDisplayLanguage,
      string targetDisplayLanguage,
      CancellationToken cancellationToken = default,
      int sourceParagraphBookOffset = -1,
      int sentenceIndexInParagraph = -1);
}

/// <summary>Результат перевода предложения: модель, тост или флаг «ничего показывать».</summary>
public sealed class SentenceTranslationResult
{
  /// <summary>Открыть полноэкранный диалог с переводом.</summary>
  public bool ShowTranslationModal { get; init; }
  /// <summary>Текст для модального окна перевода.</summary>
  public string? ModalText { get; init; }
  /// <summary>Snackbar; если задано, модальное окно перевода не показываем.</summary>
  public string? ToastMessage { get; init; }
}
