namespace BookReaderApp.Models;

/// <summary>Результат опроса статуса задачи перевода на сервере.</summary>
public enum BookTranslationPollOutcome
{
  Ok,
  /// <summary>Задачи нет (404) — снимать с опроса.</summary>
  NotFoundOnServer,
  /// <summary>Сеть, таймаут, 5xx и т.п. — не сбрасывать job, повторить позже.</summary>
  TransientFailure,
}
