using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BookReaderApp.Services;

/// <summary>Сопоставление предложения между параллельными версиями книги (офлайн): тот же абзац P и тот же номер предложения внутри абзаца.</summary>
public static class SentenceAlignment
{
  /// <summary>Как <see cref="ReadingHtmlBuilder"/> — границы абзацев для <c>data-bo</c> и <c>p.para</c>.</summary>
  static readonly Regex ParagraphBreakRegex = new(@"\r\n\s*\r\n|\n\s*\n", RegexOptions.Compiled);

  /// <summary>
  /// Делит текст на «предложения» (как WebView): абзацы по <c>\n\n</c>, внутри — по . ! ? … и пробелам;
  /// хвост абзаца без точки — отдельное предложение.
  /// </summary>
  public static List<string> SplitIntoSentences(string fullText)
  {
    var result = new List<string>();
    foreach (var (_, text) in EnumerateDisplayParagraphs(fullText))
      result.AddRange(SplitBlockIntoSentences(text));
    return result;
  }

  /// <summary>Предложения внутри одного абзаца (как <c>sentenceRanges</c> в WebView).</summary>
  public static List<string> SplitBlockIntoSentences(string blockText)
  {
    var result = new List<string>();
    foreach (var piece in SentenceBoundaryHelper.SplitBlock(blockText))
    {
      var n = NormalizeWhitespace(piece);
      if (n.Length > 0)
        result.Add(n);
    }
    return result;
  }

  public static string PrepareSelectionFromReader(string? raw) =>
      NormalizeWhitespace(raw);

  public static List<string> SplitParagraphBlocks(string fullText) =>
      EnumerateDisplayParagraphs(fullText).Select(p => p.Text).ToList();

  /// <summary>Абзацы как в WebView: <c>data-bo</c> = смещение начала текста абзаца в книге.</summary>
  static List<(int BookOffset, string Text)> EnumerateDisplayParagraphs(string fullText)
  {
    var list = new List<(int, string)>();
    if (string.IsNullOrEmpty(fullText))
      return list;

    foreach (var (a, b) in BuildParagraphIndexRanges(fullText))
    {
      if (a >= b)
        continue;
      int i = a;
      while (i < b && char.IsWhiteSpace(fullText[i]))
        i++;
      int j = b - 1;
      while (j >= i && char.IsWhiteSpace(fullText[j]))
        j--;
      if (i > j)
        continue;
      list.Add((i, NormalizeWhitespace(fullText.Substring(i, j - i + 1))));
    }
    return list;
  }

  /// <summary>Копия логики <c>ReadingHtmlBuilder</c> — совпадение номеров предложений с WebView.</summary>
  static List<(int A, int B)> BuildParagraphIndexRanges(string slice)
  {
    var ranges = new List<(int, int)>();
    if (string.IsNullOrEmpty(slice))
      return ranges;
    int start = 0;
    foreach (Match m in ParagraphBreakRegex.Matches(slice))
    {
      ranges.Add((start, m.Index));
      start = m.Index + m.Length;
    }
    ranges.Add((start, slice.Length));
    if (ranges.Count == 1)
    {
      var (a0, b0) = ranges[0];
      bool hasNl = false;
      for (int z = a0; z < b0; z++)
      {
        if (slice[z] == '\n' || slice[z] == '\r')
        {
          hasNl = true;
          break;
        }
      }
      if (hasNl && b0 > a0)
      {
        ranges.Clear();
        int lineStart = a0;
        for (int z = a0; z < b0; z++)
        {
          if (slice[z] == '\n')
          {
            if (z > lineStart)
              ranges.Add((lineStart, z));
            lineStart = z + 1;
          }
          else if (slice[z] == '\r')
          {
            int skip = (z + 1 < b0 && slice[z + 1] == '\n') ? 2 : 1;
            if (z > lineStart)
              ranges.Add((lineStart, z));
            lineStart = z + skip;
            if (skip == 2)
              z++;
          }
        }
        if (b0 > lineStart)
          ranges.Add((lineStart, b0));
      }
    }
    return ranges;
  }

  public static string NormalizeWhitespace(string? s)
  {
    if (string.IsNullOrEmpty(s))
      return "";
    var t = s.Trim().Replace('\u00A0', ' ');
    var sb = new StringBuilder(t.Length);
    bool prevSpace = false;
    foreach (var c in t)
    {
      if (char.IsWhiteSpace(c))
      {
        if (!prevSpace)
          sb.Append(' ');
        prevSpace = true;
      }
      else
      {
        prevSpace = false;
        sb.Append(c);
      }
    }
    return sb.ToString().Trim();
  }

  public static string TruncatePreservingTrailingPunctuation(string? s, int maxLen)
  {
    if (string.IsNullOrEmpty(s) || maxLen < 1)
      return NormalizeWhitespace(s);
    var t = NormalizeWhitespace(s);
    if (t.Length <= maxLen)
      return t;

    if (!TranslationClosingPunctuation.TryGetTrailingSentenceTerminalStart(t.AsSpan(), out int termStart))
      return t.Substring(0, maxLen);

    int suffixLen = t.Length - termStart;
    if (termStart < maxLen)
    {
      int keepThroughSuffix = termStart + suffixLen;
      int keep = Math.Max(maxLen, keepThroughSuffix);
      keep = Math.Min(keep, t.Length);
      return t.Substring(0, keep);
    }

    return t.Substring(0, maxLen).TrimEnd() + t.Substring(termStart);
  }

  /// <summary>Абзац P и индекс предложения в нём по якорю WebView (<c>data-bo</c> + <c>si</c>).</summary>
  public static bool TryResolveReaderAnchor(
      string sourceFullText,
      int paragraphBookOffset,
      int sentenceIndexInParagraph,
      out int paragraphIndex,
      out int localSentenceIndex)
  {
    paragraphIndex = -1;
    localSentenceIndex = sentenceIndexInParagraph;
    if (paragraphBookOffset < 0 || sentenceIndexInParagraph < 0 || string.IsNullOrEmpty(sourceFullText))
      return false;

    var paragraphs = EnumerateDisplayParagraphs(sourceFullText);
    if (paragraphs.Count == 0)
      return false;

    for (int p = 0; p < paragraphs.Count; p++)
    {
      if (paragraphBookOffset != paragraphs[p].BookOffset)
        continue;
      var local = SplitBlockIntoSentences(paragraphs[p].Text);
      if (sentenceIndexInParagraph >= local.Count)
        return false;
      paragraphIndex = p;
      localSentenceIndex = sentenceIndexInParagraph;
      return true;
    }

    int bestP = -1;
    int bestDist = int.MaxValue;
    for (int p = 0; p < paragraphs.Count; p++)
    {
      int d = Math.Abs(paragraphs[p].BookOffset - paragraphBookOffset);
      if (d < bestDist)
      {
        bestDist = d;
        bestP = p;
      }
    }
    if (bestP < 0 || bestDist > 48)
      return false;

    var fallbackLocal = SplitBlockIntoSentences(paragraphs[bestP].Text);
    if (sentenceIndexInParagraph >= fallbackLocal.Count)
      return false;
    paragraphIndex = bestP;
    localSentenceIndex = sentenceIndexInParagraph;
    return true;
  }

  /// <summary>Глобальный номер (0-based) — только для заметок/якорей, не для офлайн-перевода между языками.</summary>
  public static int FindGlobalSentenceIndexByReaderAnchor(
      string sourceFullText,
      int paragraphBookOffset,
      int sentenceIndexInParagraph)
  {
    if (!TryResolveReaderAnchor(sourceFullText, paragraphBookOffset, sentenceIndexInParagraph, out int p, out int si))
      return -1;
    int global = 0;
    var paragraphs = EnumerateDisplayParagraphs(sourceFullText);
    for (int i = 0; i < p; i++)
      global += SplitBlockIntoSentences(paragraphs[i].Text).Count;
    return global + si;
  }

  /// <summary>Глобальный номер предложения по тексту выделения (запасной путь без якоря WebView).</summary>
  public static int FindGlobalSentenceIndex(string sourceFullText, string selectedSentence)
  {
    var sel = NormalizeWhitespace(selectedSentence);
    if (sel.Length == 0 || string.IsNullOrEmpty(sourceFullText))
      return -1;

    var paragraphs = EnumerateDisplayParagraphs(sourceFullText);
    int global = 0;
    for (int p = 0; p < paragraphs.Count; p++)
    {
      var para = paragraphs[p].Text;
      var sents = SplitBlockIntoSentences(para);
      for (int i = 0; i < sents.Count; i++)
      {
        if (string.Equals(sents[i], sel, StringComparison.Ordinal))
          return global + i;
      }
      global += sents.Count;
    }

    global = 0;
    int bestGlobal = -1;
    int bestQ = -1;
    for (int p = 0; p < paragraphs.Count; p++)
    {
      var para = paragraphs[p].Text;
      var sents = SplitBlockIntoSentences(para);
      int local = FindSentenceIndexInParagraph(para, sel, sents);
      if (local >= 0)
      {
        int q = ScoreSelectionMatch(para, sel, sents, local);
        if (q > bestQ)
        {
          bestQ = q;
          bestGlobal = global + local;
        }
      }
      global += sents.Count;
    }

    return bestGlobal;
  }

  public static int FindSentenceIndexInSource(string sourceFullText, string selectedSentence) =>
      FindGlobalSentenceIndex(sourceFullText, selectedSentence);

  /// <summary>Номер предложения из <see cref="SplitIntoSentences"/> (-1 при неудаче).</summary>
  static int FindSentenceIndexInParagraph(string paragraph, string selectedSentence, IReadOnlyList<string> sentences)
  {
    if (string.IsNullOrWhiteSpace(selectedSentence) || sentences.Count == 0)
      return -1;

    var para = NormalizeWhitespace(paragraph);
    var sel = NormalizeWhitespace(selectedSentence);
    if (sel.Length == 0)
      return -1;

    for (int i = 0; i < sentences.Count; i++)
    {
      if (string.Equals(sentences[i], sel, StringComparison.Ordinal))
        return i;
    }

    var selLoose = TrimTrailingForLooseSentenceMatch(sel);
    if (selLoose.Length >= 3)
    {
      for (int i = 0; i < sentences.Count; i++)
      {
        var cur = TrimTrailingForLooseSentenceMatch(sentences[i]);
        if (string.Equals(cur, selLoose, StringComparison.Ordinal)
            || string.Equals(cur, selLoose, StringComparison.OrdinalIgnoreCase))
          return i;
      }
    }

    if (!HasTrailingSentenceTerminal(sel) && IsParagraphSuffix(para, sel))
      return sentences.Count - 1;

    int pos = para.LastIndexOf(sel, StringComparison.Ordinal);
    if (pos < 0)
      pos = para.LastIndexOf(sel, StringComparison.OrdinalIgnoreCase);
    if (pos >= 0)
      return FindSentenceIndexByCharOffset(sentences, para, pos);

    return -1;
  }

  static int ScoreSelectionMatch(string paragraph, string sel, IReadOnlyList<string> sentences, int sentenceIndex)
  {
    if (sentenceIndex < 0 || sentenceIndex >= sentences.Count)
      return 0;
    if (string.Equals(sentences[sentenceIndex], sel, StringComparison.Ordinal))
      return 100;
    var looseSel = TrimTrailingForLooseSentenceMatch(sel);
    var looseSent = TrimTrailingForLooseSentenceMatch(sentences[sentenceIndex]);
    if (looseSel.Length >= 3
        && (string.Equals(looseSent, looseSel, StringComparison.Ordinal)
            || string.Equals(looseSent, looseSel, StringComparison.OrdinalIgnoreCase)))
      return 95;
    return 50;
  }

  static bool HasTrailingSentenceTerminal(string text) =>
      TranslationClosingPunctuation.TryGetTrailingSentenceTerminalStart(
          NormalizeWhitespace(text).AsSpan(), out _);

  static bool IsParagraphSuffix(string paragraph, string selection)
  {
    var p = NormalizeWhitespace(paragraph);
    var s = NormalizeWhitespace(selection);
    return s.Length > 0 && p.Length >= s.Length
        && (p.EndsWith(s, StringComparison.Ordinal) || p.EndsWith(s, StringComparison.OrdinalIgnoreCase));
  }

  static string TrimTrailingForLooseSentenceMatch(string s)
  {
    var t = NormalizeWhitespace(s);
    if (t.Length == 0)
      return t;
    while (t.Length > 0)
    {
      char c = t[^1];
      if (c == '.' || c == '!' || c == '?' || c == '…' || c == ';' || c == ':')
      {
        t = t[..^1].TrimEnd();
        continue;
      }
      if (c == '"' || c == '\'' || c == '\u201D' || c == '\u2019' || c == '»' || c == ')' || c == ']')
      {
        t = t[..^1].TrimEnd();
        continue;
      }
      break;
    }
    return t;
  }

  /// <summary>Предложение с тем же глобальным номером N (только если разбиение книг совпадает).</summary>
  public static bool TryGetParallelBySentenceIndex(string sourceFullText, string targetFullText, int sentenceIndex, out string? parallel)
  {
    parallel = null;
    if (sentenceIndex < 0)
      return false;
    var tgt = SplitIntoSentences(targetFullText);
    if (tgt.Count == 0 || sentenceIndex >= tgt.Count)
      return false;
    parallel = tgt[sentenceIndex];
    return !string.IsNullOrWhiteSpace(parallel);
  }

  static int MapParagraphIndex(int paragraphIndexInSource, int srcParagraphCount, int tgtParagraphCount)
  {
    if (tgtParagraphCount <= 0)
      return 0;
    if (srcParagraphCount <= 1)
      return 0;
    if (paragraphIndexInSource < tgtParagraphCount)
      return paragraphIndexInSource;
    int x = (int)Math.Round((double)paragraphIndexInSource * (tgtParagraphCount - 1) / (srcParagraphCount - 1));
    return Math.Clamp(x, 0, tgtParagraphCount - 1);
  }

  static int MapLocalSentenceIndex(int indexInSource, int srcSentenceCount, int tgtSentenceCount)
  {
    if (tgtSentenceCount <= 0)
      return -1;
    if (indexInSource < 0)
      return -1;
    if (indexInSource < tgtSentenceCount)
      return indexInSource;
    if (srcSentenceCount <= 1)
      return Math.Clamp(indexInSource, 0, tgtSentenceCount - 1);
    int x = (int)Math.Round((double)indexInSource * (tgtSentenceCount - 1) / (srcSentenceCount - 1));
    return Math.Clamp(x, 0, tgtSentenceCount - 1);
  }

  static int FindSentenceStartInParagraph(string paragraph, IReadOnlyList<string> sentences, int index)
  {
    if (index < 0 || index >= sentences.Count)
      return -1;
    int searchFrom = 0;
    for (int i = 0; i < index; i++)
    {
      int ix = paragraph.IndexOf(sentences[i], searchFrom, StringComparison.Ordinal);
      if (ix < 0)
        ix = paragraph.IndexOf(sentences[i], searchFrom, StringComparison.OrdinalIgnoreCase);
      if (ix < 0)
        return -1;
      searchFrom = ix + Math.Max(1, sentences[i].Length);
    }
    int p = paragraph.IndexOf(sentences[index], searchFrom, StringComparison.Ordinal);
    if (p < 0)
      p = paragraph.IndexOf(sentences[index], searchFrom, StringComparison.OrdinalIgnoreCase);
    return p;
  }

  static int ResolveLocalSentenceIndex(
      string sourceParagraph,
      IReadOnlyList<string> sourceSentences,
      string selectedSentence,
      int sentenceIndexHint)
  {
    if (sourceSentences.Count == 0)
      return -1;

    var sel = NormalizeWhitespace(selectedSentence);
    if (sel.Length > 0)
    {
      int byText = FindSentenceIndexInParagraph(sourceParagraph, sel, sourceSentences);
      if (byText >= 0)
        return byText;
    }

    if (sentenceIndexHint >= 0 && sentenceIndexHint < sourceSentences.Count)
      return sentenceIndexHint;

    return -1;
  }

  static bool SelectionMatchesAt(string paragraph, int start, string normalizedSelection)
  {
    if (start < 0 || start >= paragraph.Length || normalizedSelection.Length == 0)
      return false;
    if (start + normalizedSelection.Length > paragraph.Length)
      return false;
    return paragraph.AsSpan(start, normalizedSelection.Length)
        .Equals(normalizedSelection.AsSpan(), StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>Границы как в WebView: ровно выделенный текст, не «следующее предложение» из C#-разбиения (иначе обрыв на ;).</summary>
  static (int Start, int End) GetSelectionSpanInParagraph(
      string normalizedParagraph,
      string normalizedSelection,
      IReadOnlyList<string> sentences,
      int sentenceIndexHint)
  {
    var sel = NormalizeWhitespace(normalizedSelection);
    if (sel.Length == 0)
      return (-1, -1);

    if (sentenceIndexHint >= 0 && sentenceIndexHint < sentences.Count)
    {
      int hintStart = FindSentenceStartInParagraph(normalizedParagraph, sentences, sentenceIndexHint);
      if (hintStart >= 0)
      {
        int at = normalizedParagraph.IndexOf(sel, hintStart, StringComparison.Ordinal);
        if (at < 0)
          at = normalizedParagraph.IndexOf(sel, hintStart, StringComparison.OrdinalIgnoreCase);
        if (at >= 0 && SelectionMatchesAt(normalizedParagraph, at, sel))
          return (at, at + sel.Length);
      }
    }

    int pos = normalizedParagraph.LastIndexOf(sel, StringComparison.Ordinal);
    if (pos < 0)
      pos = normalizedParagraph.LastIndexOf(sel, StringComparison.OrdinalIgnoreCase);
    if (pos >= 0 && SelectionMatchesAt(normalizedParagraph, pos, sel))
      return (pos, pos + sel.Length);

    return (-1, -1);
  }

  static (int Start, int End) GetSentenceSpanInParagraph(
      string paragraph,
      IReadOnlyList<string> sentences,
      int sentenceIndex,
      string selectedSentence)
  {
    var normPara = NormalizeWhitespace(paragraph);
    var sel = NormalizeWhitespace(selectedSentence);
    if (sel.Length > 0)
    {
      var fromSelection = GetSelectionSpanInParagraph(normPara, sel, sentences, sentenceIndex);
      if (fromSelection.Start >= 0)
        return fromSelection;
    }

    if (sentenceIndex < 0 || sentenceIndex >= sentences.Count)
      return (-1, -1);

    int start = FindSentenceStartInParagraph(normPara, sentences, sentenceIndex);
    if (start < 0)
      return (-1, -1);

    int end = normPara.Length;
    if (sentenceIndex + 1 < sentences.Count)
    {
      int next = FindSentenceStartInParagraph(normPara, sentences, sentenceIndex + 1);
      if (next > start)
        end = next;
    }
    else if (sel.Length > 0)
      end = Math.Min(normPara.Length, start + sel.Length);

    return (start, Math.Max(start + 1, end));
  }

  static bool IsStrongSentenceTerminalChar(char c) =>
      c == '.' || c == '!' || c == '?' || c == '…';

  static bool IsTrailingCloserChar(char c) =>
      char.IsWhiteSpace(c) || c == '\u00A0' ||
      c == '"' || c == '\'' || c == '\u201D' || c == '\u2019' || c == '»' || c == ')' || c == ']';

  /// <summary>Не обрезать перевод на ; или : , если выделение в оригинале заканчивается на . ! ? …</summary>
  static int ExtendTargetEndForSelectionTerminal(string target, int tStart, int tEnd, string normalizedSelection)
  {
    if (tStart < 0 || tEnd <= tStart || normalizedSelection.Length == 0)
      return tEnd;
    if (!TranslationClosingPunctuation.TryGetTrailingSentenceTerminalStart(
            normalizedSelection.AsSpan(), out int termStart))
      return tEnd;

    char term = normalizedSelection[termStart];
    if (!IsStrongSentenceTerminalChar(term))
      return tEnd;

    int maxEnd = Math.Min(target.Length, tStart + Math.Max(normalizedSelection.Length + 48, normalizedSelection.Length * 2));
    int end = Math.Max(tEnd, tStart + 1);
    for (int i = end; i < maxEnd; i++)
    {
      if (!IsStrongSentenceTerminalChar(target[i]))
        continue;
      end = i + 1;
      while (end < target.Length && IsTrailingCloserChar(target[end]))
        end++;
      return end;
    }

    return tEnd;
  }

  /// <summary>
  /// Параллельное предложение: тот же номер <paramref name="sentenceIndexHint"/> в абзаце перевода,
  /// при разном числе предложений — <see cref="MapLocalSentenceIndex"/>; доля символов абзаца только запасной путь.
  /// </summary>
  static string? ExtractParallelSentenceInParagraph(
      string sourceParagraph,
      string selectedSentence,
      int sentenceIndexHint,
      string targetParagraph)
  {
    var normSrc = NormalizeWhitespace(sourceParagraph);
    var normTgt = NormalizeWhitespace(targetParagraph);
    var sel = NormalizeWhitespace(selectedSentence);
    if (normSrc.Length == 0 || normTgt.Length == 0)
      return null;

    var srcSents = SplitBlockIntoSentences(normSrc);
    if (srcSents.Count == 0)
      return null;

    int si = ResolveLocalSentenceIndex(normSrc, srcSents, sel, sentenceIndexHint);
    if (si < 0)
      return null;

    var (srcStart, srcEnd) = GetSentenceSpanInParagraph(normSrc, srcSents, si, sel);
    if (srcStart < 0)
      return null;

    var tgtSents = SplitBlockIntoSentences(normTgt);
    if (tgtSents.Count == 0)
      return null;

    if (normSrc.Length < 2)
    {
      if (si < tgtSents.Count)
        return tgtSents[si].Trim();
      return tgtSents[0].Trim();
    }

    // Одинаковая нарезка абзаца — тот же si (тексты RU/EN не сравниваем).
    if (srcSents.Count == tgtSents.Count && si < tgtSents.Count)
      return tgtSents[si].Trim();

    // Разное число предложений — пропорция по индексу, не по длине абзаца в символах.
    int tgtSi = MapLocalSentenceIndex(si, srcSents.Count, tgtSents.Count);
    if (tgtSi >= 0 && tgtSi < tgtSents.Count)
      return tgtSents[tgtSi].Trim();

    // Запасной: начало выделения в абзаце (не середина — иначе уезжает в следующую реплику).
    int tgtChar = (int)Math.Round((double)srcStart / normSrc.Length * normTgt.Length);
    tgtChar = Math.Clamp(tgtChar, 0, Math.Max(0, normTgt.Length - 1));
    int byOffset = FindSentenceIndexByCharOffset(tgtSents, normTgt, tgtChar);
    if (byOffset >= 0)
      return tgtSents[byOffset].Trim();

    double r0 = (double)srcStart / normSrc.Length;
    double r1 = (double)srcEnd / normSrc.Length;
    int tStart = (int)Math.Floor(r0 * normTgt.Length);
    int tEnd = (int)Math.Ceiling(r1 * normTgt.Length);
    tStart = Math.Clamp(tStart, 0, Math.Max(0, normTgt.Length - 1));
    tEnd = Math.Clamp(tEnd, tStart + 1, normTgt.Length);
    tEnd = ExtendTargetEndForSelectionTerminal(normTgt, tStart, tEnd, sel);

    var slice = normTgt.Substring(tStart, tEnd - tStart).Trim();
    if (slice.Length == 0)
      return null;

    var sliceSents = SplitBlockIntoSentences(slice);
    if (sliceSents.Count == 1)
      return sliceSents[0].Trim();

    int pickInSlice = MapLocalSentenceIndex(si, srcSents.Count, sliceSents.Count);
    return sliceSents[Math.Clamp(pickInSlice, 0, sliceSents.Count - 1)].Trim();
  }

  static bool TryResolveParagraphBySelection(string sourceFullText, string selectedSentence, out int paragraphIndex, out int localSentenceIndex)
  {
    paragraphIndex = -1;
    localSentenceIndex = -1;
    var sel = NormalizeWhitespace(selectedSentence);
    if (sel.Length == 0)
      return false;

    var paragraphs = EnumerateDisplayParagraphs(sourceFullText);
    int bestP = -1;
    int bestSi = -1;
    int bestQ = -1;
    for (int p = 0; p < paragraphs.Count; p++)
    {
      var para = paragraphs[p].Text;
      var sents = SplitBlockIntoSentences(para);
      int si = FindSentenceIndexInParagraph(para, sel, sents);
      if (si < 0)
        continue;
      int q = ScoreSelectionMatch(para, sel, sents, si);
      if (q > bestQ)
      {
        bestQ = q;
        bestP = p;
        bestSi = si;
      }
    }

    if (bestP < 0)
      return false;
    paragraphIndex = bestP;
    localSentenceIndex = bestSi;
    return true;
  }

  /// <summary>
  /// Офлайн: абзац P (по <paramref name="paragraphBookOffset"/> / тексту) и предложение SI внутри абзаца → то же SI в абзаце P перевода.
  /// Глобальный номер по всей книге не используется — в другом языке другое число предложений и символов.
  /// </summary>
  public static bool TryGetParallelSentence(
      string sourceFullText,
      string targetFullText,
      string selectedSentence,
      out string? parallel,
      int paragraphBookOffset = -1,
      int sentenceIndexInParagraph = -1)
  {
    parallel = null;
    if (string.IsNullOrWhiteSpace(selectedSentence) || string.IsNullOrEmpty(sourceFullText) || string.IsNullOrEmpty(targetFullText))
      return false;

    selectedSentence = NormalizeWhitespace(selectedSentence);

    int pSrc = -1;
    int siHint = sentenceIndexInParagraph;
    if (paragraphBookOffset >= 0 && sentenceIndexInParagraph >= 0)
      TryResolveReaderAnchor(sourceFullText, paragraphBookOffset, sentenceIndexInParagraph, out pSrc, out siHint);

    if (pSrc < 0)
    {
      if (!TryResolveParagraphBySelection(sourceFullText, selectedSentence, out pSrc, out siHint))
        return false;
    }

    var srcParas = EnumerateDisplayParagraphs(sourceFullText);
    var tgtParas = EnumerateDisplayParagraphs(targetFullText);
    if (pSrc < 0 || pSrc >= srcParas.Count || tgtParas.Count == 0)
      return false;

    int pTgt = MapParagraphIndex(pSrc, srcParas.Count, tgtParas.Count);
    parallel = ExtractParallelSentenceInParagraph(
        srcParas[pSrc].Text,
        selectedSentence,
        siHint,
        tgtParas[pTgt].Text);
    return !string.IsNullOrWhiteSpace(parallel);
  }

  static int FindSentenceIndexByCharOffset(IReadOnlyList<string> sentences, string full, int charOffset)
  {
    if (sentences.Count == 0)
      return -1;
    int searchFrom = 0;
    for (int i = 0; i < sentences.Count; i++)
    {
      int ix = full.IndexOf(sentences[i], searchFrom, StringComparison.Ordinal);
      if (ix < 0)
        ix = full.IndexOf(sentences[i], searchFrom, StringComparison.OrdinalIgnoreCase);
      if (ix < 0)
        return -1;
      int end = ix + sentences[i].Length;
      if (charOffset >= ix && charOffset < end + 1)
        return i;
      searchFrom = ix + Math.Max(1, sentences[i].Length);
    }
    return -1;
  }
}
