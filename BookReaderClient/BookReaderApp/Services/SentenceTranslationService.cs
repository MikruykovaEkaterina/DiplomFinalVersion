using BookReaderApp.Models;
using Microsoft.Extensions.Logging;

namespace BookReaderApp.Services;

/// <inheritdoc cref="ISentenceTranslationService" />
/// <summary>Стратегия: офлайн по параллельной книге через <see cref="SentenceAlignment"/>, затем HTTP к TranslationProxy.</summary>
public sealed class SentenceTranslationService : ISentenceTranslationService
{
  /// <inheritdoc cref="BookTranslationApiClient.SentenceRequestTimeoutSeconds" />
  public const int OnlineRequestTimeoutSeconds = BookTranslationApiClient.SentenceRequestTimeoutSeconds;

  readonly IDatabaseService _db;
  readonly BookTranslationApiClient _translationApi;
  readonly ILogger<SentenceTranslationService> _logger;

  /// <summary>Сохраняет зависимости из DI.</summary>
  public SentenceTranslationService(
      IDatabaseService db,
      BookTranslationApiClient translationApi,
      ILogger<SentenceTranslationService> logger)
  {
    _db = db;
    _translationApi = translationApi;
    _logger = logger;
  }

  /// <inheritdoc />
  public async Task<SentenceTranslationResult> TranslateSentenceAsync(
      string sentenceText,
      Card currentCard,
      string sourceDisplayLanguage,
      string targetDisplayLanguage,
      CancellationToken cancellationToken = default,
      int sourceParagraphBookOffset = -1,
      int sentenceIndexInParagraph = -1)
  {
    if (string.IsNullOrWhiteSpace(sentenceText))
      return ToastOnly(TranslationMessages.OnlineFailure);

    var sourceIso = TranslationLanguageCodes.TryGetIsoCode(sourceDisplayLanguage);
    var targetIso = TranslationLanguageCodes.TryGetIsoCode(targetDisplayLanguage);
    if (sourceIso == null || targetIso == null)
      return ToastOnly(TranslationMessages.OnlineFailure);

    var trimmed = SentenceAlignment.TruncatePreservingTrailingPunctuation(sentenceText.Trim(), 8000);

    if (string.Equals(sourceIso, targetIso, StringComparison.OrdinalIgnoreCase))
      return Modal(trimmed);

    TranslationDiagnostics.Log($"start: сначала офлайн (параллельная карточка), book={currentCard.Id}");
    var offline = await TryOfflineParallelAsync(
        trimmed, currentCard, targetDisplayLanguage, sourceParagraphBookOffset, sentenceIndexInParagraph, cancellationToken)
        .ConfigureAwait(false);
    if (offline.ShowTranslationModal)
      return offline;

    if (string.IsNullOrWhiteSpace(TranslationServerConfig.ApiBaseUrl))
    {
      TranslationDiagnostics.Log($"офлайн без результата, URL прокси пуст — без сетевого запроса, book={currentCard.Id}");
      return offline;
    }

    TranslationDiagnostics.Log(
        $"офлайн не дал перевода — запрос к прокси, len={trimmed.Length}, {sourceIso}->{targetIso}, book={currentCard.Id}");
    _logger.LogInformation("Translate sentence via translation API (after offline miss): {Source}->{Target}, len={Len}", sourceIso, targetIso, trimmed.Length);

    var translated = await _translationApi.TranslateSentenceViaProxyAsync(trimmed, sourceIso, targetIso, cancellationToken)
        .ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    if (!string.IsNullOrWhiteSpace(translated))
      return Modal(TranslationClosingPunctuation.AlignTrailingWithSource(trimmed, translated.Trim()));

    return ToastOnly(TranslationMessages.TranslationProxyUnreachable);
  }

  /// <summary>Сопоставление с другой версией той же <see cref="Work"/> на целевом языке через плоские тексты.</summary>
  async Task<SentenceTranslationResult> TryOfflineParallelAsync(
      string trimmed,
      Card currentCard,
      string targetDisplayLanguage,
      int sourceParagraphBookOffset,
      int sentenceIndexInParagraph,
      CancellationToken cancellationToken)
  {
    try
    {
      var parallel = await FindParallelCardAsync(currentCard, targetDisplayLanguage).ConfigureAwait(false);
      if (parallel == null)
      {
        TranslationDiagnostics.Log("офлайн: нет карточки с языком цели");
        return ToastOnly(TranslationMessages.OfflineUnavailable);
      }

      var targetPlain = await Task.Run(() => BookPlainTextLoader.TryLoadPlainText(parallel.FilePath, parallel.Format), cancellationToken).ConfigureAwait(false);
      var sourcePlain = await Task.Run(() => BookPlainTextLoader.TryLoadPlainText(currentCard.FilePath, currentCard.Format), cancellationToken).ConfigureAwait(false);
      if (string.IsNullOrEmpty(targetPlain) || string.IsNullOrEmpty(sourcePlain))
      {
        TranslationDiagnostics.Log("офлайн: пустой текст книги (source/target)");
        return ToastOnly(TranslationMessages.OfflineUnavailable);
      }

      if (SentenceAlignment.TryGetParallelSentence(
              sourcePlain, targetPlain, trimmed, out var parallelSentence,
              sourceParagraphBookOffset, sentenceIndexInParagraph)
          && !string.IsNullOrWhiteSpace(parallelSentence))
      {
        TranslationDiagnostics.Log(
            $"офлайн: абзац+предложение в паре (data-bo={sourceParagraphBookOffset}, si={sentenceIndexInParagraph})");
        var aligned = TranslationClosingPunctuation.AlignTrailingWithSource(trimmed, parallelSentence.Trim());
        return Modal(aligned);
      }

      TranslationDiagnostics.Log("офлайн: не удалось сопоставить предложение");
    }
    catch (Exception ex)
    {
      TranslationDiagnostics.Log($"офлайн: исключение {ex.GetType().Name}: {ex.Message}");
    }

    return ToastOnly(TranslationMessages.OfflineUnavailable);
  }

  /// <summary>Ищет карточку с тем же WorkId и ISO языка совпадающим с <paramref name="targetDisplayLanguage"/>.</summary>
  async Task<Card?> FindParallelCardAsync(Card current, string targetDisplayLanguage)
  {
    var cards = await _db.GetCardsByWorkIdAsync(current.WorkId).ConfigureAwait(false);
    var targetIso = TranslationLanguageCodes.TryGetIsoCode(targetDisplayLanguage);
    if (targetIso == null)
      return null;
    foreach (var c in cards)
    {
      if (c.Id == current.Id)
        continue;
      var cardIso = TranslationLanguageCodes.TryGetIsoCode(c.Language);
      if (string.Equals(cardIso, targetIso, StringComparison.OrdinalIgnoreCase))
        return c;
    }
    return null;
  }

  /// <summary>Результат с показом модального окна перевода.</summary>
  static SentenceTranslationResult Modal(string text) =>
      new() { ShowTranslationModal = true, ModalText = text };

  /// <summary>Результат только с уведомлением (toast), без модалки.</summary>
  static SentenceTranslationResult ToastOnly(string msg) =>
      new() { ShowTranslationModal = false, ToastMessage = msg };
}
