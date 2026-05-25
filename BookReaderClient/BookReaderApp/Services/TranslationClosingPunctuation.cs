namespace BookReaderApp.Services;

/// <summary>Подгонка финального знака предложения в переводе под исходный фрагмент (модалка, офлайн, тот же алгоритм на прокси для записи книги).</summary>
public static class TranslationClosingPunctuation
{
  /// <summary>Согласует завершающий знак (. ! ? … и т.д.) перевода со знаком у исходного фрагмента перед скобками и кавычками.</summary>
  public static string AlignTrailingWithSource(string? sourceSnippet, string? translatedSnippet)
  {
    if (string.IsNullOrWhiteSpace(translatedSnippet))
      return translatedSnippet ?? "";
    var t = translatedSnippet.Trim();
    if (t.Length == 0)
      return translatedSnippet;
    var s = (sourceSnippet ?? "").TrimEnd();
    if (s.Length == 0)
      return translatedSnippet;

    char? want = ExtractTrailingSentencePunctuation(s);
    if (want == null)
      return translatedSnippet;

    char? have = ExtractTrailingSentencePunctuation(t);
    if (have == null)
      return InsertBeforeTrailingClosers(t, want.Value);
    if (have == want.Value)
      return translatedSnippet;
    return ReplaceTrailingSentencePunct(t, want.Value);
  }

  /// <summary>Возвращает знак завершения предложения в конце строки (игнорируя закрывающие пробелы, кавычки).</summary>
  static char? ExtractTrailingSentencePunctuation(string s)
  {
    int i = s.Length - 1;
    while (i >= 0 && IsClosesAfterSentence(s[i]))
      i--;
    if (i < 0)
      return null;
    char c = s[i];
    return IsSentenceTerminal(c) ? c : null;
  }

  /// <summary>Символы после основного знака предложения (NBSP, кавычки, скобки).</summary>
  static bool IsClosesAfterSentence(char c) =>
      char.IsWhiteSpace(c) || c == '\u00A0' ||
      c == '"' || c == '\'' || c == '\u201D' || c == '\u2019' || c == '»' || c == ')' || c == ']';

  /// <summary>Возможные терминаторы рус./англ. предложения.</summary>
  static bool IsSentenceTerminal(char c) =>
      c == '.' || c == '!' || c == '?' || c == '…' || c == ';' || c == ':';

  /// <summary>Вставляет знак перед хвостовыми пробелами/кавычками, если перевод без терминатора.</summary>
  static string InsertBeforeTrailingClosers(string t, char punct)
  {
    int i = t.Length - 1;
    while (i >= 0 && IsClosesAfterSentence(t[i]))
      i--;
    return t.Substring(0, i + 1) + punct + t.Substring(i + 1);
  }

  /// <summary>Заменяет существующий терминатор в конце перевода на <paramref name="newPunct"/>.</summary>
  static string ReplaceTrailingSentencePunct(string t, char newPunct)
  {
    int i = t.Length - 1;
    while (i >= 0 && IsClosesAfterSentence(t[i]))
      i--;
    if (i < 0)
      return t + newPunct;
    if (!IsSentenceTerminal(t[i]))
      return t.Substring(0, i + 1) + newPunct + t.Substring(i + 1);
    return t.Substring(0, i) + newPunct + t.Substring(i + 1);
  }

  /// <summary>
  /// Индекс последнего завершающего знака предложения в строке (после пропуска хвостовых «закрывателей»),
  /// для сохранения пунктуации при обрезке длинного фрагмента.
  /// </summary>
  public static bool TryGetTrailingSentenceTerminalStart(ReadOnlySpan<char> s, out int termStartIndex)
  {
    termStartIndex = 0;
    if (s.IsEmpty)
      return false;
    int i = s.Length - 1;
    while (i >= 0 && IsClosesAfterSentence(s[i]))
      i--;
    if (i < 0 || !IsSentenceTerminal(s[i]))
      return false;
    termStartIndex = i;
    return true;
  }
}
