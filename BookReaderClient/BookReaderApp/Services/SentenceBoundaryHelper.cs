namespace BookReaderApp.Services;

/// <summary>
/// Границы предложений для WebView и <see cref="SentenceAlignment"/> (как <c>BookTextChunker</c> на сервере):
/// . ! ? … (с кавычками ." / .»); структурная прямая речь — : —, , — «, , " без списков слов.
/// </summary>
internal static class SentenceBoundaryHelper
{
  static bool IsCloser(char c) =>
      c == '"' || c == '\'' || c == '\u201D' || c == '\u2019' || c == '»' || c == ')';

  static bool IsTerminal(char c) =>
      c == '.' || c == '!' || c == '?' || c == '…';

  static bool IsDirectSpeechLead(char c) =>
      c == '—' || c == '–' || c == '«' || c == '"' || c == '\u201C';

  static int SkipWhitespace(string text, int index)
  {
    while (index < text.Length && char.IsWhiteSpace(text[index]))
      index++;
    return index;
  }

  /// <summary>Находится ли позиция <paramref name="index"/> внутри парных кавычек (двойных / « »).</summary>
  public static bool IsInsideQuotes(string text, int index)
  {
    if (string.IsNullOrEmpty(text) || index <= 0)
      return false;
    bool inAscii = false;
    bool inGuillemet = false;
    int n = Math.Min(index, text.Length);
    for (int i = 0; i < n; i++)
    {
      switch (text[i])
      {
        case '"':
          inAscii = !inAscii;
          break;
        case '\u201C':
          inAscii = true;
          break;
        case '\u201D':
          inAscii = false;
          break;
        case '«':
          inGuillemet = true;
          break;
        case '»':
          inGuillemet = false;
          break;
      }
    }
    return inAscii || inGuillemet;
  }

  static bool IntroducesDirectSpeechAt(string block, int index)
  {
    int j = SkipWhitespace(block, index);
    return j < block.Length && IsDirectSpeechLead(block[j]);
  }

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
    return char.IsUpper(block[j]) || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(block[j]) == System.Globalization.UnicodeCategory.UppercaseLetter;
  }

  static bool IsCommaOpenQuoteDialogue(string block, int commaIdx)
  {
    int j = SkipWhitespace(block, commaIdx + 1);
    return j < block.Length && block[j] is '"' or '\u201C' or '«';
  }

  static IEnumerable<int> EnumerateTerminalCutIndices(string block)
  {
    int n = block.Length;
    for (int i = 0; i < n; i++)
    {
      if (!IsTerminal(block[i]))
        continue;

      int j = i + 1;
      if (block[i] == '.')
      {
        while (j < n && block[j] == '.')
          j++;
      }

      bool hadCloser = false;
      while (j < n && IsCloser(block[j]))
      {
        hadCloser = true;
        j++;
      }

      // Внутри кавычек ! ? … — режем; точка без » после — только если дальше не «. строчное слово».
      if (IsInsideQuotes(block, i) && !hadCloser && block[i] == '.')
      {
        if (j >= n || !char.IsWhiteSpace(block[j]))
          continue;
        int k = SkipWhitespace(block, j);
        if (k < n && char.IsLetter(block[k]) && char.IsLower(block[k]))
          continue;
      }

      if (j < n && !char.IsWhiteSpace(block[j]))
        continue;

      while (j < n && char.IsWhiteSpace(block[j]))
        j++;

      yield return j;
      i = j - 1;
    }
  }

  static IEnumerable<int> EnumerateDirectSpeechCutIndices(string block)
  {
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

  /// <summary>Индексы в <paramref name="block"/> с которых начинается следующее предложение.</summary>
  public static IEnumerable<int> EnumerateSentenceCutIndices(string block)
  {
    if (string.IsNullOrEmpty(block))
      yield break;

    var cuts = new SortedSet<int>();
    foreach (int c in EnumerateTerminalCutIndices(block))
      cuts.Add(c);
    foreach (int c in EnumerateDirectSpeechCutIndices(block))
      cuts.Add(c);

    foreach (int cut in cuts)
      yield return cut;
  }

  /// <summary>Тексты предложений внутри одного абзаца (без нормализации пробелов).</summary>
  public static List<string> SplitBlock(string blockText)
  {
    var result = new List<string>();
    if (string.IsNullOrEmpty(blockText))
      return result;
    var block = blockText.Trim();
    if (block.Length == 0)
      return result;

    int last = 0;
    foreach (int cut in EnumerateSentenceCutIndices(block))
    {
      if (cut > last)
      {
        var piece = block.Substring(last, cut - last).Trim();
        if (piece.Length > 0)
          result.Add(piece);
      }
      last = cut;
    }

    if (last < block.Length)
    {
      var tail = block.Substring(last).Trim();
      if (tail.Length > 0)
        result.Add(tail);
    }

    return result;
  }
}
