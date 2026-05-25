using System.Globalization;
using System.Text;

namespace TranslationProxy;

/// <summary>
/// Делит длинный текст на части для вызова переводчика: сначала по границам предложений,
/// несколько коротких предложений объединяются в один чанк до лимита символов;
/// если одно предложение длиннее лимита — запасной вариант по пробелам (как раньше).
/// </summary>
public static class BookTextChunker
{
  /// <inheritdoc cref="ChunkByLength(string,int)"/>
  public static IEnumerable<string> ChunkByLength(string text, int maxLen)
  {
    if (string.IsNullOrEmpty(text))
      yield break;
    if (maxLen < 64)
      maxLen = 64;

    List<string>? sentences = null;
    foreach (var s in SplitIntoSentences(text))
    {
      var t = s.Trim();
      if (t.Length == 0)
        continue;
      sentences ??= new List<string>();
      sentences.Add(t);
    }

    if (sentences == null || sentences.Count == 0)
      yield break;

    // Один фрагмент без распознанной пунктуации — как раньше по словам
    if (sentences.Count == 1 && sentences[0].Length == text.Trim().Length)
    {
      foreach (var part in ChunkLongSegmentByWords(sentences[0], maxLen))
        yield return part;
      yield break;
    }

    var buf = new StringBuilder();
    foreach (var sent in sentences)
    {
      if (sent.Length > maxLen)
      {
        if (buf.Length > 0)
        {
          yield return buf.ToString();
          buf.Clear();
        }

        foreach (var part in ChunkLongSegmentByWords(sent, maxLen))
          yield return part;
        continue;
      }

      var needSep = buf.Length > 0 ? 1 : 0;
      if (buf.Length + needSep + sent.Length <= maxLen)
      {
        if (needSep > 0)
          buf.Append(' ');
        buf.Append(sent);
      }
      else
      {
        if (buf.Length > 0)
          yield return buf.ToString();
        buf.Clear();
        buf.Append(sent);
      }
    }

    if (buf.Length > 0)
      yield return buf.ToString();
  }

  /// <summary>
  /// Русский/английский художественный текст: <c>. ! ? …</c> (сокращения, кавычки ."),
  /// плюс структурная прямая речь (<see cref="SentenceBoundaryCore"/>): <c>: —</c>, <c>, — «</c>, <c>, "</c>.
  /// </summary>
  internal static IEnumerable<string> SplitIntoSentences(string text)
  {
    if (string.IsNullOrEmpty(text))
      yield break;

    var cuts = new SortedSet<int>();
    foreach (int c in EnumerateTerminalCutIndices(text))
      cuts.Add(c);
    foreach (int c in SentenceBoundaryCore.EnumerateDirectSpeechCutIndices(text))
      cuts.Add(c);

    int last = 0;
    foreach (int cut in cuts)
    {
      if (cut > last)
      {
        foreach (var x in YieldTrimmed(text, last, cut))
          yield return x;
      }
      last = cut;
    }

    if (last < text.Length)
    {
      foreach (var x in YieldTrimmed(text, last, text.Length))
        yield return x;
    }
  }

  static IEnumerable<int> EnumerateTerminalCutIndices(string text)
  {
    int n = text.Length;
    int i = 0;

    while (i < n)
    {
      char c = text[i];

      if (c == '!' || c == '?')
      {
        i++;
        i = SkipClosingQuotes(text, i);
        i = SkipLeadingWs(text, i);
        yield return i;
        continue;
      }

      if (c == '\u2026')
      {
        i++;
        i = SkipClosingQuotes(text, i);
        i = SkipLeadingWs(text, i);
        yield return i;
        continue;
      }

      if (c == '.')
      {
        if (StartsAsciiEllipsis(text, i))
        {
          i = ConsumeAsciiDots(text, i);
          i = SkipClosingQuotes(text, i);
          i = SkipLeadingWs(text, i);
          yield return i;
          continue;
        }

        if (IsSentenceEndingPeriod(text, i) || IsQuotedSentenceEndPeriod(text, i))
        {
          i++;
          i = ConsumeTrailingDots(text, i);
          i = SkipClosingQuotes(text, i);
          i = SkipLeadingWs(text, i);
          yield return i;
          continue;
        }
      }

      i++;
    }
  }

  /// <summary>Точка внутри кавычек с закрывающей кавычкой сразу после (." / .»).</summary>
  static bool IsQuotedSentenceEndPeriod(string text, int dotIdx)
  {
    if (!IsInsideDoubleOrGuillemetQuotes(text, dotIdx))
      return false;

    int j = dotIdx + 1;
    j = ConsumeTrailingDots(text, j);
    if (j >= text.Length || !IsClosingQuoteChar(text[j]))
      return false;
    j++;
    while (j < text.Length && IsClosingQuoteChar(text[j]))
      j++;
    return j >= text.Length || char.IsWhiteSpace(text[j]);
  }

  static bool IsClosingQuoteChar(char c) =>
      c is '"' or '\'' or '\u201D' or '\u2019' or '»' or ')';

  static bool IsInsideDoubleOrGuillemetQuotes(string text, int index)
  {
    bool inAscii = false;
    bool inGuillemet = false;
    for (int i = 0; i < index && i < text.Length; i++)
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

  static IEnumerable<string> YieldTrimmed(string text, int start, int endExclusive)
  {
    ReadOnlySpan<char> span = text.AsSpan(start, endExclusive - start).Trim();
    if (span.Length > 0)
      yield return span.ToString();
  }

  static int SkipLeadingWs(string text, int i)
  {
    while (i < text.Length && char.IsWhiteSpace(text[i]))
      i++;
    return i;
  }

  static int SkipClosingQuotes(string text, int i)
  {
    while (i < text.Length)
    {
      char c = text[i];
      if (c is '"' or '\'' or '\u00BB' or '\u201D' or ')')
      {
        i++;
        continue;
      }

      break;
    }

    return i;
  }

  static bool StartsAsciiEllipsis(string text, int i) =>
      i + 2 < text.Length && text[i] == '.' && text[i + 1] == '.' && text[i + 2] == '.';

  static int ConsumeAsciiDots(string text, int i)
  {
    while (i < text.Length && text[i] == '.')
      i++;
    return i;
  }

  static int ConsumeTrailingDots(string text, int i)
  {
    while (i < text.Length && text[i] == '.')
      i++;
    return i;
  }

  /// <summary>
  /// Точка считается концом предложения, если после неё (с учётом кавычек) идёт пробел/перенос и дальше типичное начало нового предложения,
  /// либо конец строки. Иначе — часть номера, инициалов, многосимвольного сокращения (Mr., стр.) или цепочки «т. д.».
  /// </summary>
  static bool IsSentenceEndingPeriod(string text, int dotIdx)
  {
    if (LooksLikeDecimalSeparator(text, dotIdx))
      return false;

    // И. И. … или J. K. Rowling — точка после инициала не режет предложение
    if (AbbreviatedInitialChainContinues(text, dotIdx))
      return false;

    if (InitialThenLongCapitalWordFollows(text, dotIdx))
      return false;

    if (IsMultiLetterAbbrevBeforeDot(text, dotIdx))
      return false;

    // «т. д.», «и т. п.» — после точки следующее слово начинается со строчной (продолжение сокращения или связки)
    if (LowercaseLetterStartsWordAfterPeriod(text, dotIdx))
      return false;

    int j = dotIdx + 1;
    j = ConsumeTrailingDots(text, j);
    j = SkipClosingQuotes(text, j);

    if (j >= text.Length)
      return true;

    if (!char.IsWhiteSpace(text[j]))
      return false;

    bool hadNewline = false;
    while (j < text.Length && char.IsWhiteSpace(text[j]))
    {
      if (text[j] is '\n' or '\r')
        hadNewline = true;
      j++;
    }

    if (j >= text.Length)
      return true;

    // Новый абзац чаще всего начало нового предложения даже со строчной буквы
    if (hadNewline)
      return true;

    char next = text[j];
    if (char.IsUpper(next))
      return true;

    UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(next);
    if (cat == UnicodeCategory.UppercaseLetter)
      return true;

    return next is '\u00AB' or '\u201C' or '"' or '\'' or '(' or '—' or '–';
  }

  static bool LooksLikeDecimalSeparator(string text, int dotIdx)
  {
    bool digitBefore = dotIdx > 0 && char.IsDigit(text[dotIdx - 1]);
    bool digitAfter = dotIdx + 1 < text.Length && char.IsDigit(text[dotIdx + 1]);
    return digitBefore && digitAfter;
  }

  /// <summary>Одна заглавная буква непосредственно перед точкой (инициал).</summary>
  static bool SingleLetterCapitalBeforeDot(string text, int dotIdx)
  {
    if (dotIdx < 1 || !char.IsLetter(text[dotIdx - 1]))
      return false;

    char letter = text[dotIdx - 1];
    UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(letter);
    bool upper = cat == UnicodeCategory.UppercaseLetter || char.IsUpper(letter);
    if (!upper)
      return false;

    return dotIdx < 2 || !char.IsLetter(text[dotIdx - 2]);
  }

  /// <summary>После точки идёт следующий инициал: «И. И.».</summary>
  static bool AbbreviatedInitialChainContinues(string text, int dotIdx)
  {
    if (!SingleLetterCapitalBeforeDot(text, dotIdx))
      return false;

    int j = dotIdx + 1;
    j = ConsumeTrailingDots(text, j);
    j = SkipClosingQuotes(text, j);
    j = SkipLeadingWs(text, j);

    if (j >= text.Length || !char.IsLetter(text[j]))
      return false;

    UnicodeCategory cat0 = CharUnicodeInfo.GetUnicodeCategory(text[j]);
    if (cat0 != UnicodeCategory.UppercaseLetter && !char.IsUpper(text[j]))
      return false;

    int wordStart = j;
    while (j < text.Length && char.IsLetter(text[j]))
      j++;

    if (j - wordStart != 1)
      return false;

    return j < text.Length && text[j] == '.';
  }

  /// <summary>После инициала идёт фамилия/имя одним словом с заглавной: «И. И. Иванов», «J. K. Rowling».</summary>
  static bool InitialThenLongCapitalWordFollows(string text, int dotIdx)
  {
    if (!SingleLetterCapitalBeforeDot(text, dotIdx))
      return false;

    int j = dotIdx + 1;
    j = ConsumeTrailingDots(text, j);
    j = SkipClosingQuotes(text, j);
    j = SkipLeadingWs(text, j);

    if (j >= text.Length || !char.IsLetter(text[j]))
      return false;

    UnicodeCategory cat0 = CharUnicodeInfo.GetUnicodeCategory(text[j]);
    if (cat0 != UnicodeCategory.UppercaseLetter && !char.IsUpper(text[j]))
      return false;

    int wordStart = j;
    while (j < text.Length && char.IsLetter(text[j]))
      j++;

    return j - wordStart >= 2;
  }

  /// <summary>Слово из букв непосредственно перед точкой (без самой точки), в нижнем регистре.</summary>
  static bool TryGetWordBeforeDot(string text, int dotIdx, out string wordLower)
  {
    wordLower = "";
    if (dotIdx < 1)
      return false;

    int endIncl = dotIdx - 1;
    if (!char.IsLetter(text[endIncl]))
      return false;

    int s = endIncl;
    while (s >= 0 && char.IsLetter(text[s]))
      s--;

    ReadOnlySpan<char> w = text.AsSpan(s + 1, endIncl - s);
    if (w.Length == 0)
      return false;

    wordLower = w.ToString().ToLowerInvariant();
    return true;
  }

  static bool IsMultiLetterAbbrevBeforeDot(string text, int dotIdx)
  {
    if (!TryGetWordBeforeDot(text, dotIdx, out var w) || w.Length < 2)
      return false;
    return SentenceAbbreviationLexicon.Words.Contains(w);
  }

  /// <summary>После точки и пробелов первый символ «следующего слова» — строчная буква (частый признак «т. д.», «и т. п.»).</summary>
  static bool LowercaseLetterStartsWordAfterPeriod(string text, int dotIdx)
  {
    int j = dotIdx + 1;
    j = ConsumeTrailingDots(text, j);
    j = SkipClosingQuotes(text, j);
    j = SkipLeadingWs(text, j);
    if (j >= text.Length)
      return false;

    char c = text[j];
    return char.IsLetter(c) && char.IsLower(c);
  }

  /// <summary>Прежний алгоритм: порции по длине с переносом на последнем пробеле в окне.</summary>
  static IEnumerable<string> ChunkLongSegmentByWords(string text, int maxLen)
  {
    int i = 0;
    while (i < text.Length)
    {
      int remaining = text.Length - i;
      int len = Math.Min(maxLen, remaining);
      if (len < remaining)
      {
        int searchStart = i;
        int lastSpace = text.LastIndexOf(' ', i + len - 1, len);
        if (lastSpace > searchStart + maxLen / 5)
          len = lastSpace - i + 1;
      }

      yield return text.Substring(i, len);
      i += len;
      while (i < text.Length && char.IsWhiteSpace(text[i]))
        i++;
    }
  }
}
