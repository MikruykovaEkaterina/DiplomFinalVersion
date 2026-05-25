using System.Globalization;

namespace TranslationProxy;

/// <summary>
/// Структурные границы перед прямой речью (без списков глаголов): : —, , — «…, , "…, , «…
/// </summary>
internal static class SentenceBoundaryCore
{
  static bool IsCloser(char c) =>
      c == '"' || c == '\'' || c == '\u201D' || c == '\u2019' || c == '»' || c == ')';

  static bool IsDirectSpeechLead(char c) =>
      c == '—' || c == '–' || c == '«' || c == '"' || c == '\u201C';

  static int SkipWhitespace(string text, int index)
  {
    while (index < text.Length && char.IsWhiteSpace(text[index]))
      index++;
    return index;
  }

  static bool IntroducesDirectSpeechAt(string block, int index)
  {
    int j = SkipWhitespace(block, index);
    return j < block.Length && IsDirectSpeechLead(block[j]);
  }

  /// <summary>«, —» + кавычка или заглавная (не «, — жил-был»).</summary>
  static bool IsCommaDashDialogue(string block, int commaIdx)
  {
    int n = block.Length;
    int j = SkipWhitespace(block, commaIdx + 1);
    if (j >= n || (block[j] != '—' && block[j] != '–'))
      return false;
    j = SkipWhitespace(block, j + 1);
    if (j >= n)
      return false;
    if (block[j] == '«' || block[j] == '"' || block[j] == '\u201C')
      return true;
    char ch = block[j];
    return char.IsUpper(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.UppercaseLetter;
  }

  /// <summary>Англ. и др.: «…дочерям, "What…» — запятая, затем открывающая кавычка.</summary>
  static bool IsCommaOpenQuoteDialogue(string block, int commaIdx)
  {
    int j = SkipWhitespace(block, commaIdx + 1);
    return j < block.Length && block[j] is '"' or '\u201C' or '«';
  }

  /// <summary>Индексы начала следующего предложения (прямая речь).</summary>
  public static IEnumerable<int> EnumerateDirectSpeechCutIndices(string block)
  {
    if (string.IsNullOrEmpty(block))
      yield break;

    int n = block.Length;
    for (int i = 0; i < n; i++)
    {
      if (block[i] != ':')
        continue;
      if (!IntroducesDirectSpeechAt(block, i + 1))
        continue;
      int j = SkipWhitespace(block, i + 1);
      yield return j;
      i = j;
    }

    for (int i = 0; i < n; i++)
    {
      if (block[i] != ',')
        continue;
      if (IsCommaOpenQuoteDialogue(block, i))
        yield return SkipWhitespace(block, i + 1);
      else if (IsCommaDashDialogue(block, i))
        yield return SkipWhitespace(block, i + 1);
    }
  }
}
