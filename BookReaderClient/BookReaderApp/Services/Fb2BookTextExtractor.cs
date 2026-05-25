using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using BookReaderApp.Resources;

namespace BookReaderApp.Services;

/// <summary>
/// Извлекает текст тела FB2 и оглавление по дереву &lt;section&gt; (без поиска заголовков в «сплющенной» строке).
/// </summary>
public static class Fb2BookTextExtractor
{
  /// <summary>Исправляет типичные ошибки FB2 (незакодированный &amp;, управляющие символы), из‑за которых падает XmlReader.</summary>
  public static string SanitizeFb2XmlBeforeParse(string xmlContent)
  {
    if (string.IsNullOrEmpty(xmlContent))
      return xmlContent;
    var s = Regex.Replace(xmlContent, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");
    s = EnsureXlinkNamespaceDeclaration(s);
    return Regex.Replace(s, @"&(?!(?:amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)", "&amp;", RegexOptions.IgnoreCase);
  }

  /// <summary>
  /// Убирает &lt;binary&gt;…&lt;/binary&gt; перед разбором: обложки и иллюстрации в base64 раздувают файл и на слабых устройствах
  /// дают сбой/таймаут XmlReader, после чего извлечение уходит в fallback с урезанным текстом. Для чтения текста бинарники не нужны.
  /// </summary>
  private static string StripFb2BinaryBlocks(string xml)
  {
    if (string.IsNullOrEmpty(xml))
      return xml;
    return Regex.Replace(
        xml,
        @"<binary\b[^>]*>.*?</binary>",
        string.Empty,
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
  }

  /// <summary>
  /// Многие FB2 (ficbook и др.) ставят &lt;a xlink:href="..."&gt; без xmlns:xlink на корне — XmlReader падает с «необъявленный префикс xlink».
  /// </summary>
  private static string EnsureXlinkNamespaceDeclaration(string xml)
  {
    if (xml.Contains("xmlns:xlink", StringComparison.OrdinalIgnoreCase))
      return xml;
    var m = Regex.Match(xml, @"<FictionBook\s+", RegexOptions.IgnoreCase);
    if (m.Success)
      return xml.Insert(m.Index + m.Length, "xmlns:xlink=\"http://www.w3.org/1999/xlink\" ");
    return xml.Replace("<FictionBook>", "<FictionBook xmlns:xlink=\"http://www.w3.org/1999/xlink\">", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>Разбор FB2 без падения на редких символах в тексте и DTD.</summary>
  public static XDocument LoadFictionBookDocument(string xmlContent)
  {
    if (string.IsNullOrEmpty(xmlContent))
      throw new ArgumentException("Empty XML", nameof(xmlContent));
    var s = xmlContent.TrimStart();
    if (s.Length > 0 && s[0] == '\uFEFF')
      s = s.Substring(1);
    s = SanitizeFb2XmlBeforeParse(s);
    s = StripFb2BinaryBlocks(s);
    // Сразу правим типичный битый ficbook-XML (emphasis на несколько &lt;p&gt;), иначе первый же успешный catch
    // после repair снова бросал XmlException на втором Load — исключение уходило наружу и ExtractFromXml уходил в fallback.
    s = ApplyFb2StructuralRepairs(s);
    const int maxAttempts = 24;
    XmlException? lastXml = null;
    for (int attempt = 0; attempt < maxAttempts; attempt++)
    {
      try
      {
        return LoadFictionBookDocumentCore(s);
      }
      catch (XmlException ex)
      {
        lastXml = ex;
        var next = ApplyFb2StructuralRepairs(s);
        if (string.Equals(next, s, StringComparison.Ordinal))
          break;
        s = next;
      }
    }
    throw lastXml ?? new XmlException(Strings.Fb2_Error_XmlParseFailed);
  }

  private static XDocument LoadFictionBookDocumentCore(string s)
  {
    var settings = new XmlReaderSettings
    {
      DtdProcessing = DtdProcessing.Ignore,
      IgnoreComments = true,
      IgnoreWhitespace = false,
      CheckCharacters = false
    };
    using var sr = new StringReader(s);
    using var reader = XmlReader.Create(sr, settings);
    return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
  }

  /// <summary>
  /// Ficbook: один &lt;emphasis&gt; на несколько &lt;p&gt; (невалидный XML). Приводим к &lt;emphasis&gt;&lt;p&gt;…&lt;/p&gt;…&lt;/p&gt;&lt;/emphasis&gt;.
  /// </summary>
  private static string RepairMalformedEmphasisAcrossParagraphs(string xml)
  {
    var lines = xml.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
    var openPemph = new Regex(@"<p>\s*<emphasis([^>]*)>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10));
    for (int i = 0; i < lines.Length; i++)
    {
      if (!IsMalformedEmphasisOpeningLine(lines[i]))
        continue;
      int k = FindClosingEmphasisParagraphLine(lines, i);
      if (k > i)
      {
        lines[i] = openPemph.Replace(lines[i], "<emphasis$1><p>", 1);
        lines[k] = Regex.Replace(lines[k], @"</emphasis>\s*</p>", "</p></emphasis>", RegexOptions.IgnoreCase);
      }
      else
      {
        lines[i] = Regex.Replace(
            lines[i],
            @"<p>\s*<emphasis([^>]*)>((?:(?!</emphasis>).)*?)</p>",
            "<p><emphasis$1>$2</emphasis></p>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(10));
      }
    }
    return string.Join("\n", lines);
  }

  private static bool IsMalformedEmphasisOpeningLine(string line)
  {
    if (!Regex.IsMatch(line, @"<p>\s*<emphasis\b", RegexOptions.IgnoreCase))
      return false;
    if (!line.Contains("</p>", StringComparison.OrdinalIgnoreCase))
      return false;
    int pClose = line.IndexOf("</p>", StringComparison.OrdinalIgnoreCase);
    int emClose = line.IndexOf("</emphasis>", StringComparison.OrdinalIgnoreCase);
    return pClose >= 0 && (emClose < 0 || emClose > pClose);
  }

  private static int FindClosingEmphasisParagraphLine(string[] lines, int i)
  {
    for (int k = i + 1; k < lines.Length; k++)
    {
      if (lines[k].IndexOf("</emphasis>", StringComparison.OrdinalIgnoreCase) < 0)
        continue;
      if (!lines[k].Contains("</emphasis></p>", StringComparison.OrdinalIgnoreCase)
          && !Regex.IsMatch(lines[k], @"</emphasis>\s*</p>", RegexOptions.IgnoreCase))
        continue;
      bool hasIntermediateP = false;
      for (int j = i + 1; j < k; j++)
      {
        if (lines[j].Contains("<p>", StringComparison.OrdinalIgnoreCase))
        {
          hasIntermediateP = true;
          break;
        }
      }
      // Два подряд: первая <p><emphasis>…</p> без </emphasis>, вторая <p>…</emphasis></p> (между ними нет строк с <p>)
      bool adjacentClosing =
          k == i + 1
          && !Regex.IsMatch(lines[k], @"^\s*<p>\s*<emphasis\b", RegexOptions.IgnoreCase);
      if (hasIntermediateP || adjacentClosing)
        return k;
    }
    return -1;
  }

  /// <summary>
  /// Одна проходка: «emphasis на несколько &lt;p&gt;», баланс инлайна в &lt;p&gt;/&lt;v&gt;, затем баланс инлайна внутри
  /// «листовых» &lt;section&gt; (без вложенного &lt;section&gt;) — иначе &lt;emphasis&gt; на уровне секции ломает XmlReader.
  /// </summary>
  private static string ApplyFb2StructuralRepairs(string xml)
  {
    var     s = RepairMalformedEmphasisAcrossParagraphs(xml);
    s = RepairParagraphInlineTagBalance(s);
    return RepairFb2XmlWithFullStack(s);
  }

  /// <summary>
  /// Типичные ошибки FB2: &lt;p&gt;&lt;strong&gt;…&lt;/p&gt; без &lt;/strong&gt;, &lt;strong&gt;&lt;emphasis&gt;…&lt;/strong&gt;&lt;/emphasis&gt; и т.п.
  /// Восстанавливает порядок закрывающих тегов и дописывает недостающие в конце абзаца.
  /// </summary>
  private static string RepairParagraphInlineTagBalance(string xml)
  {
    if (string.IsNullOrEmpty(xml))
      return xml;
    try
    {
      xml = ParagraphInnerRegex.Replace(
          xml,
          m => m.Groups["open"].Value + BalanceInlineXmlFragment(m.Groups["inner"].Value) + "</p>");
      xml = StanzaLineInnerRegex.Replace(
          xml,
          m => m.Groups["open"].Value + BalanceInlineXmlFragment(m.Groups["inner"].Value) + "</v>");
      return xml;
    }
    catch (RegexMatchTimeoutException)
    {
      return xml;
    }
  }

  /// <summary>
  /// Один проход по всему FB2: стек открытых тегов. Перед каждым &lt;/section&gt; из файла
  /// дописываются недостающие &lt;/emphasis&gt;, &lt;/p&gt; и т.д., пока на вершине стека не окажется
  /// сама эта &lt;section&gt;. Иначе при вложенных &lt;section&gt; и &lt;emphasis&gt; до внешнего &lt;/section&gt;
  /// листовой regex никогда не срабатывал.
  /// </summary>
  private static string RepairFb2XmlWithFullStack(string xml)
  {
    if (string.IsNullOrEmpty(xml))
      return xml;
    if (xml.IndexOf('<', StringComparison.Ordinal) < 0)
      return xml;

    var sb = new StringBuilder(xml.Length + 128);
    var stack = new Stack<string>();
    int i = 0;
    while (i < xml.Length)
    {
      if (xml[i] != '<')
      {
        int next = xml.IndexOf('<', i);
        if (next < 0)
        {
          sb.Append(xml, i, xml.Length - i);
          break;
        }
        sb.Append(xml, i, next - i);
        i = next;
        continue;
      }

      int gt = xml.IndexOf('>', i);
      if (gt < 0)
      {
        sb.Append(xml, i, xml.Length - i);
        break;
      }

      string segment = xml.Substring(i, gt - i + 1);

      if (segment.Length >= 4 && segment[1] == '/' && char.IsLetter(segment[2]))
      {
        string? closeName = ParseClosingTagName(segment);
        if (closeName != null)
        {
          if (string.Equals(closeName, "section", StringComparison.OrdinalIgnoreCase))
          {
            while (stack.Count > 0 && !string.Equals(stack.Peek(), "section", StringComparison.OrdinalIgnoreCase))
              sb.Append("</").Append(stack.Pop()).Append('>');
            if (stack.Count > 0 && string.Equals(stack.Peek(), "section", StringComparison.OrdinalIgnoreCase))
            {
              stack.Pop();
              sb.Append(segment);
            }
            else
              sb.Append(segment);
            i = gt + 1;
            continue;
          }

          if (!StackContains(stack, closeName))
          {
            i = gt + 1;
            continue;
          }
          while (stack.Count > 0 && !string.Equals(stack.Peek(), closeName, StringComparison.OrdinalIgnoreCase))
            sb.Append("</").Append(stack.Pop()).Append('>');
          if (stack.Count > 0)
            stack.Pop();
          sb.Append(segment);
          i = gt + 1;
          continue;
        }
      }

      if (segment.Length >= 3 && segment[1] == '!')
      {
        sb.Append(segment);
        i = gt + 1;
        continue;
      }

      if (segment.Length >= 2 && segment[1] == '?')
      {
        sb.Append(segment);
        i = gt + 1;
        continue;
      }

      string? openName = ParseOpenTagName(segment);
      bool selfClosing = IsSelfClosingTagSegment(segment);
      if (openName != null && !selfClosing && !IsVoidInlineTag(openName))
        stack.Push(openName);
      sb.Append(segment);
      i = gt + 1;
    }

    while (stack.Count > 0)
      sb.Append("</").Append(stack.Pop()).Append('>');
    return sb.ToString();
  }

  private static readonly Regex ParagraphInnerRegex = new(
      @"(?<open><p\b[^>]*>)(?<inner>[\s\S]*?)</p>",
      RegexOptions.IgnoreCase | RegexOptions.Compiled,
      TimeSpan.FromSeconds(120));

  private static readonly Regex StanzaLineInnerRegex = new(
      @"(?<open><v\b[^>]*>)(?<inner>[\s\S]*?)</v>",
      RegexOptions.IgnoreCase | RegexOptions.Compiled,
      TimeSpan.FromSeconds(120));

  private static string BalanceInlineXmlFragment(string inner)
  {
    if (string.IsNullOrEmpty(inner) || inner.IndexOf('<', StringComparison.Ordinal) < 0)
      return inner;

    var sb = new StringBuilder(inner.Length + 32);
    var stack = new Stack<string>();
    int i = 0;
    while (i < inner.Length)
    {
      if (inner[i] != '<')
      {
        int next = inner.IndexOf('<', i);
        if (next < 0)
        {
          sb.Append(inner, i, inner.Length - i);
          break;
        }
        sb.Append(inner, i, next - i);
        i = next;
        continue;
      }

      int gt = inner.IndexOf('>', i);
      if (gt < 0)
      {
        sb.Append(inner, i, inner.Length - i);
        break;
      }

      string segment = inner.Substring(i, gt - i + 1);

      if (segment.Length >= 4 && segment[1] == '/' && char.IsLetter(segment[2]))
      {
        string? closeName = ParseClosingTagName(segment);
        if (closeName != null)
        {
          if (!StackContains(stack, closeName))
          {
            i = gt + 1;
            continue;
          }
          while (stack.Count > 0 && !string.Equals(stack.Peek(), closeName, StringComparison.OrdinalIgnoreCase))
          {
            sb.Append("</").Append(stack.Pop()).Append('>');
          }
          if (stack.Count > 0)
            stack.Pop();
          sb.Append(segment);
          i = gt + 1;
          continue;
        }
      }

      if (segment.Length >= 3 && segment[1] == '!')
      {
        sb.Append(segment);
        i = gt + 1;
        continue;
      }

      if (segment.Length >= 2 && segment[1] == '?')
      {
        sb.Append(segment);
        i = gt + 1;
        continue;
      }

      string? openName = ParseOpenTagName(segment);
      bool selfClosing = IsSelfClosingTagSegment(segment);
      if (openName != null && !selfClosing && !IsVoidInlineTag(openName))
        stack.Push(openName);
      sb.Append(segment);
      i = gt + 1;
    }

    while (stack.Count > 0)
      sb.Append("</").Append(stack.Pop()).Append('>');
    return sb.ToString();
  }

  private static bool StackContains(Stack<string> stack, string name)
  {
    foreach (var x in stack)
    {
      if (string.Equals(x, name, StringComparison.OrdinalIgnoreCase))
        return true;
    }
    return false;
  }

  private static string? ParseClosingTagName(string segment)
  {
    int start = 2;
    while (start < segment.Length && char.IsWhiteSpace(segment[start]))
      start++;
    int end = start;
    while (end < segment.Length && segment[end] != '>' && !char.IsWhiteSpace(segment[end]))
      end++;
    if (end <= start)
      return null;
    return segment.Substring(start, end - start).ToLowerInvariant();
  }

  private static string? ParseOpenTagName(string segment)
  {
    if (segment.Length < 2 || segment[0] != '<')
      return null;
    int start = 1;
    if (segment[start] == '/')
      return null;
    while (start < segment.Length && char.IsWhiteSpace(segment[start]))
      start++;
    int end = start;
    while (end < segment.Length && segment[end] != '>' && segment[end] != '/' && !char.IsWhiteSpace(segment[end]))
      end++;
    if (end <= start)
      return null;
    return segment.Substring(start, end - start).ToLowerInvariant();
  }

  private static bool IsSelfClosingTagSegment(string segment)
  {
    var t = segment.AsSpan().Trim();
    return t.Length >= 2 && t[^2] == '/' && t[^1] == '>';
  }

  private static bool IsVoidInlineTag(string localName)
  {
    return localName switch
    {
      "br" or "hr" or "img" or "image" or "col" or "meta" or "link" or "input" or "empty-line" => true,
      _ => false
    };
  }

  public sealed class ChapterSpan
  {
    public string Title { get; init; } = "";
    public int Start { get; init; }
    public int End { get; init; }
  }

  public static (string NormalizedText, List<ChapterSpan> Chapters) ExtractFromFile(string filePath)
  {
    var xml = File.ReadAllText(filePath);
    return ExtractFromXml(xml);
  }

  /// <summary>
  /// Только основное тело текста: до первого &lt;/body&gt; (для запасного разбора строкой).
  /// </summary>
  public static string? ExtractMainBodyXmlFragment(string xmlContent)
  {
    int bodyStart = xmlContent.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
    if (bodyStart < 0)
      return null;
    int bodyEnd = xmlContent.IndexOf("</body>", bodyStart, StringComparison.OrdinalIgnoreCase);
    if (bodyEnd < 0)
      return null;
    return xmlContent.Substring(bodyStart, bodyEnd - bodyStart + "</body>".Length);
  }

  /// <summary>
  /// Основной &lt;body&gt; с текстом: не &lt;body name="notes"&gt;. Разбор только целого FictionBook, иначе теряются xmlns (xlink и т.д.) и парсер падает в fallback с одной «главой».
  /// </summary>
  public static XElement? GetMainBodyElement(XDocument doc)
  {
    var root = doc.Root;
    if (root == null)
      return null;
    XNamespace ns = root.GetDefaultNamespace();
    if (!string.IsNullOrEmpty(ns.NamespaceName))
    {
      foreach (var el in root.Elements(ns + "body"))
      {
        var n = el.Attribute("name")?.Value;
        if (string.Equals(n, "notes", StringComparison.OrdinalIgnoreCase))
          continue;
        return el;
      }
    }
    foreach (var el in root.Elements())
    {
      if (el.Name.LocalName != "body")
        continue;
      var n = el.Attribute("name")?.Value;
      if (string.Equals(n, "notes", StringComparison.OrdinalIgnoreCase))
        continue;
      return el;
    }
    return null;
  }

  /// <summary>Текст и оглавление по уже отобранному списку секций (совпадает с деревом для HTML).</summary>
  public static (string NormalizedText, List<ChapterSpan> Chapters) ExtractFromOrderedSections(
      IReadOnlyList<XElement> orderedSections)
  {
    var fullSb = new StringBuilder();
    var chapters = new List<ChapterSpan>();
    int chapterIndex = 0;

    foreach (var sec in orderedSections)
    {
      chapterIndex++;
      var title = GetSectionTitle(sec);
      var body = PlainTextFromSectionBody(sec);
      if (string.IsNullOrEmpty(body))
        continue;

      int start = fullSb.Length;
      if (fullSb.Length > 0)
        fullSb.Append("\n\n");
      fullSb.Append(body);
      int end = fullSb.Length;

      var displayTitle = string.IsNullOrWhiteSpace(title)
          ? string.Format(Strings.Reading_ChapterNumberFormat, chapterIndex)
          : title.Trim();
      chapters.Add(new ChapterSpan { Title = displayTitle, Start = start, End = end });
    }

    string fullText = fullSb.ToString().Trim();
    return (fullText, chapters);
  }

  public static (string NormalizedText, List<ChapterSpan> Chapters) ExtractFromXml(string xmlContent)
  {
    try
    {
      var doc = LoadFictionBookDocument(xmlContent);
      var bodyRoot = GetMainBodyElement(doc);
      if (bodyRoot == null)
        return (Strings.Fb2_Error_NoBookText, new List<ChapterSpan>());

      var orderedSections = Fb2RichParagraphParser.GetOrderedSectionElements(bodyRoot);
      string bodyXmlFallback = ExtractMainBodyXmlFragment(xmlContent) ?? "";
      if (orderedSections.Count == 0)
        return ExtractFromBodyRoot(bodyRoot, bodyXmlFallback);

      var (fullText, chapters) = ExtractFromOrderedSections(orderedSections);
      if (string.IsNullOrEmpty(fullText))
        return FallbackFromStrippedBody(bodyXmlFallback);

      if (chapters.Count == 0)
        chapters.Add(new ChapterSpan { Title = Strings.Reading_TocFallbackBodyTitle, Start = 0, End = fullText.Length });

      return (fullText, chapters);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[Fb2BookTextExtractor] ExtractFromXml: {ex.Message}");
      var frag = ExtractMainBodyXmlFragment(xmlContent) ?? xmlContent;
      return FallbackFromStrippedBody(frag);
    }
  }

  /// <summary>
  /// Совпадает с правилом включения секции в оглавление (непустой текст после нормализации).
  /// </summary>
  public static bool SectionHasVisibleBody(XElement section)
  {
    var body = PlainTextFromSectionBody(section);
    return !string.IsNullOrEmpty(body);
  }

  private static (string NormalizedText, List<ChapterSpan> Chapters) ExtractFromBodyRoot(XElement bodyRoot, string bodyXmlFallback)
  {
    var body = PlainTextFromSectionBody(bodyRoot);
    if (string.IsNullOrEmpty(body))
      return FallbackFromStrippedBody(bodyXmlFallback);

    var title = GetSectionTitle(bodyRoot);
    var displayTitle = string.IsNullOrWhiteSpace(title) ? Strings.Reading_TocFallbackBodyTitle : title.Trim();
    string fullText = body;
    var chapters = new List<ChapterSpan>
    {
      new ChapterSpan { Title = displayTitle, Start = 0, End = fullText.Length }
    };
    return (fullText, chapters);
  }

  /// <summary>Пустой &lt;p&gt; (только пробелы) без иллюстраций — как <see cref="EpubBookExtractor"/> для EPUB.</summary>
  public static bool ShouldSkipFb2WhitespaceOnlyParagraph(XElement? p)
  {
    if (p == null || p.Name.LocalName != "p")
      return false;
    if (!string.IsNullOrEmpty(Fb2RichParagraphParser.ParagraphPlainText(p)))
      return false;
    foreach (var d in p.Descendants())
    {
      if (d.Name.LocalName == "image")
        return false;
    }
    return true;
  }

  /// <summary>Порядок &lt;p&gt; как у HTML-рендера чтения (совпадает с <c>ReadingHtmlBuilder.AppendFb2SectionHtml</c>).</summary>
  public static List<XElement> EnumerateFb2ParagraphsInHtmlOrder(XElement section)
  {
    var list = new List<XElement>();
    foreach (var el in section.Elements())
    {
      if (el.Name.LocalName == "title")
        continue;
      if (el.Name.LocalName == "section")
      {
        list.AddRange(EnumerateFb2ParagraphsInHtmlOrder(el));
        continue;
      }
      AddFb2ContentParagraphs(el, list);
    }
    return list;
  }

  static void AddFb2ContentParagraphs(XElement el, List<XElement> list)
  {
    switch (el.Name.LocalName)
    {
      case "p":
        if (!ShouldSkipFb2WhitespaceOnlyParagraph(el))
          list.Add(el);
        return;
      case "empty-line":
      case "subtitle":
        return;
      case "epigraph":
      case "cite":
        foreach (var p in el.Elements().Where(e => e.Name.LocalName == "p"))
        {
          if (!ShouldSkipFb2WhitespaceOnlyParagraph(p))
            list.Add(p);
        }
        return;
      case "poem":
        return;
      default:
        if (el.Descendants().Any(e => e.Name.LocalName == "p"))
        {
          foreach (var p in el.Descendants().Where(e => e.Name.LocalName == "p"))
          {
            if (!ShouldSkipFb2WhitespaceOnlyParagraph(p))
              list.Add(p);
          }
        }
        return;
    }
  }

  /// <summary>
  /// Глобальные смещения в полном <paramref name="fullText"/> для каждого &lt;p&gt; секции:
  /// поиск по тексту абзаца в фрагменте главы (как в <see cref="PlainTextFromSectionBody"/>).
  /// </summary>
  public static Dictionary<XElement, int> TryBuildFb2ParagraphBookOffsets(
      XElement section, string fullText, long chapterStart, long chapterEnd)
  {
    var map = new Dictionary<XElement, int>();
    var flat = (fullText ?? "").Replace("\r", "");
    if (flat.Length == 0)
      return map;
    int cs = (int)Math.Clamp(chapterStart, 0, flat.Length);
    int ce = (int)Math.Clamp(chapterEnd, cs, flat.Length);
    if (ce <= cs)
      return map;
    var body = flat.Substring(cs, ce - cs);
    var paras = EnumerateFb2ParagraphsInHtmlOrder(section);
    int cursor = 0;
    int lastMapped = cs - 1;
    foreach (var p in paras)
    {
      var plain = Fb2RichParagraphParser.ParagraphPlainText(p);
      if (string.IsNullOrEmpty(plain))
      {
        int tentative = cs + Math.Min(cursor, Math.Max(0, body.Length - 1));
        int pos = tentative;
        if (lastMapped >= 0 && pos <= lastMapped)
          pos = (int)Math.Min((long)lastMapped + 1, ce - 1);
        map[p] = Math.Clamp(pos, cs, Math.Max(cs, ce - 1));
        lastMapped = map[p];
        continue;
      }
      while (cursor < body.Length && char.IsWhiteSpace(body[cursor]))
        cursor++;
      // ParagraphPlainText сжимает любые пробелы; в сохранённой главе внутри кусков остаются \n из FB2 —
      // прямой IndexOf часто не находит текст и давал бы неверный data-bo (сдвиг «на прошлую страницу»).
      int idx = -1;
      int advanceFromMatch = plain.Length;
      var flex = TryIndexOfFlexibleWhitespace(body, plain, cursor);
      if (flex.idx >= 0)
      {
        idx = flex.idx;
        advanceFromMatch = flex.matchLen > 0 ? flex.matchLen : plain.Length;
      }
      else
      {
        idx = body.IndexOf(plain, cursor, StringComparison.Ordinal);
        if (idx < 0)
          idx = body.IndexOf(plain, cursor, StringComparison.OrdinalIgnoreCase);
      }
      if (idx < 0)
      {
        int tentative = cs + Math.Min(cursor, Math.Max(0, body.Length - plain.Length));
        int bo = tentative;
        if (lastMapped >= 0 && bo <= lastMapped)
          bo = (int)Math.Min((long)lastMapped + 1, ce - 1);
        bo = Math.Clamp(bo, cs, Math.Max(cs, ce - 1));
        map[p] = bo;
        lastMapped = bo;
        cursor = Math.Min(body.Length, Math.Max(cursor, bo - cs));
        continue;
      }
      map[p] = cs + idx;
      cursor = idx + advanceFromMatch;
      lastMapped = map[p];
    }

    return map;
  }

  /// <summary>Сопоставляет нормализованный текст абзаца с куском главы, где между словами могут быть \n/\r/пробелы.</summary>
  /// <remarks>Возвращает индекс в <paramref name="body"/> и длину совпадения в «сыром» теле для корректного продвижения курсора.</remarks>
  static (int idx, int matchLen) TryIndexOfFlexibleWhitespace(string body, string normalizedParagraph, int minIndex)
  {
    if (string.IsNullOrEmpty(body) || minIndex >= body.Length || string.IsNullOrEmpty(normalizedParagraph))
      return (-1, 0);
    var words = Regex.Split(normalizedParagraph.Trim(), @"\s+", RegexOptions.None)
        .Where(w => w.Length > 0)
        .Select(Regex.Escape)
        .ToArray();
    if (words.Length == 0)
      return (-1, 0);
    try
    {
      int startBase = Math.Clamp(minIndex, 0, body.Length);
      var tail = body.Substring(startBase);
      var pattern = string.Join(@"\s+", words);
      var m = Regex.Match(tail, pattern,
          RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
      if (!m.Success)
        return (-1, 0);
      return (startBase + m.Index, m.Length);
    }
    catch (RegexMatchTimeoutException)
    {
      return (-1, 0);
    }
  }

  private static string PlainTextFromSectionBody(XElement section)
  {
    var sb = new StringBuilder();
    bool firstBlock = true;
    foreach (var child in section.Elements())
    {
      if (child.Name.LocalName == "title")
        continue;
      if (child.Name.LocalName == "section")
      {
        var nested = PlainTextFromSectionBody(child);
        if (string.IsNullOrEmpty(nested))
          continue;
        if (!firstBlock)
          sb.Append("\n\n");
        firstBlock = false;
        sb.Append(nested);
        continue;
      }
      if (child.Name.LocalName == "empty-line")
      {
        if (!firstBlock)
          sb.Append("\n\n");
        firstBlock = false;
        continue;
      }
      if (child.Name.LocalName == "p" && ShouldSkipFb2WhitespaceOnlyParagraph(child))
        continue;
      if (!firstBlock)
        sb.Append("\n\n");
      firstBlock = false;
      AppendPlainFromElement(child, sb);
    }
    return NormalizeWhitespaceBlocks(sb.ToString());
  }

  private static string NormalizeWhitespaceBlocks(string s)
  {
    if (string.IsNullOrEmpty(s))
      return "";
    var parts = s.Split(new[] { "\n\n" }, StringSplitOptions.None);
    var sb = new StringBuilder();
    for (int i = 0; i < parts.Length; i++)
    {
      if (i > 0)
        sb.Append("\n\n");
      var p = Regex.Replace(parts[i].Trim(), @"[ \t\r]+", " ");
      sb.Append(p);
    }
    return sb.ToString().Trim();
  }

  private static void AppendPlainFromElement(XElement el, StringBuilder sb)
  {
    foreach (var node in el.Nodes())
    {
      switch (node)
      {
        case XText t:
          sb.Append(t.Value);
          break;
        case XElement e:
          if (e.Name.LocalName == "section")
            continue;
          AppendPlainFromElement(e, sb);
          break;
      }
    }
  }

  private static (string, List<ChapterSpan>) FallbackFromStrippedBody(string bodyXml)
  {
    string textOnly = Regex.Replace(bodyXml, "<[^>]+>", "");
    textOnly = Regex.Replace(textOnly, @"\s+", " ").Trim();
    textOnly = textOnly.Replace("&nbsp;", " ").Replace("&quot;", "\"").Replace("&amp;", "&")
        .Replace("&lt;", "<").Replace("&gt;", ">");
    if (string.IsNullOrEmpty(textOnly))
      return (textOnly, new List<ChapterSpan>());
    return (textOnly, new List<ChapterSpan> { new ChapterSpan { Title = Strings.Reading_TocFallbackBodyTitle, Start = 0, End = textOnly.Length } });
  }

  /// <summary>Заголовок секции: &lt;title&gt;&lt;p&gt;Имя&lt;/p&gt;&lt;/title&gt; — типичный FB2.</summary>
  public static string GetSectionTitle(XElement section)
  {
    var titleEl = section.Elements().FirstOrDefault(e => e.Name.LocalName == "title");
    if (titleEl == null)
      return "";
    var pInTitle = titleEl.Elements().FirstOrDefault(e => e.Name.LocalName == "p");
    if (pInTitle != null)
    {
      var sb = new StringBuilder();
      foreach (var t in pInTitle.DescendantNodes().OfType<XText>())
        sb.Append(t.Value);
      return Regex.Replace(sb.ToString().Trim(), @"\s+", " ");
    }
    var sbAll = new StringBuilder();
    foreach (var t in titleEl.DescendantNodes().OfType<XText>())
      sbAll.Append(t.Value);
    return Regex.Replace(sbAll.ToString().Trim(), @"\s+", " ");
  }
}
