namespace TranslationProxy;

/// <summary>
/// Подгоняет завершающую пунктуацию перевода к исходному фрагменту после склейки чанков
/// (согласовано с клиентским <c>BookReaderApp.Services.TranslationClosingPunctuation</c>).
/// </summary>
public static class TranslationClosingPunctuation
{
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

  static bool IsClosesAfterSentence(char c) =>
      char.IsWhiteSpace(c) || c == '\u00A0' ||
      c == '"' || c == '\'' || c == '\u201D' || c == '\u2019' || c == '»' || c == ')' || c == ']';

  static bool IsSentenceTerminal(char c) =>
      c == '.' || c == '!' || c == '?' || c == '…' || c == ';' || c == ':';

  static string InsertBeforeTrailingClosers(string t, char punct)
  {
    int i = t.Length - 1;
    while (i >= 0 && IsClosesAfterSentence(t[i]))
      i--;
    return t.Substring(0, i + 1) + punct + t.Substring(i + 1);
  }

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
}
