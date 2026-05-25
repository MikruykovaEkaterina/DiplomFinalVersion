using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BookReaderApp.Services;

/// <summary>
/// Разбор EPUB: порядок spine из OPF, границы глав по документам spine, плоский текст для смещений и безопасный HTML для WebView.
/// </summary>
public static class EpubBookExtractor
{
  /// <summary>Читает OPF spine, собирает плоский текст, оглавление и HTML-куски для показа в WebView.</summary>
  public static bool TryExtract(
      string epubPath,
      out string fullPlainText,
      out List<Fb2BookTextExtractor.ChapterSpan> chapters,
      out List<string> chapterHtmlFragments)
  {
    fullPlainText = "";
    chapters = new List<Fb2BookTextExtractor.ChapterSpan>();
    chapterHtmlFragments = new List<string>();

    string pathToRead = epubPath;
    string? tempInnerEpub = null;
    try
    {
      if (epubPath.EndsWith(".epub.zip", StringComparison.OrdinalIgnoreCase))
      {
        tempInnerEpub = MaterializeInnerEpubFromEpubZip(epubPath);
        if (string.IsNullOrEmpty(tempInnerEpub))
          return false;
        pathToRead = tempInnerEpub;
      }

      using var zipFs = File.OpenRead(pathToRead);
      using var zip = new ZipArchive(zipFs, ZipArchiveMode.Read, leaveOpen: false);
      var container = zip.GetEntry("META-INF/container.xml")
          ?? zip.Entries.FirstOrDefault(e =>
              e.FullName.Replace('\\', '/').Equals("META-INF/container.xml", StringComparison.OrdinalIgnoreCase));
      if (container == null)
        return false;

      string opfPath;
      using (var s = container.Open())
      {
        var doc = XDocument.Load(s);
        opfPath = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "rootfile")
            ?.Attribute("full-path")?.Value?.Trim() ?? "";
      }
      if (string.IsNullOrEmpty(opfPath))
        return false;

      opfPath = opfPath.Replace('\\', '/');
      var opfEntry = zip.GetEntry(opfPath)
          ?? zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/')
              .Equals(opfPath, StringComparison.OrdinalIgnoreCase));
      if (opfEntry == null)
        return false;

      string opfDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
      if (!string.IsNullOrEmpty(opfDir) && !opfDir.EndsWith('/'))
        opfDir += "/";

      XDocument opfDoc;
      using (var os = opfEntry.Open())
      using (var reader = System.Xml.XmlReader.Create(os, new System.Xml.XmlReaderSettings
             {
               DtdProcessing = System.Xml.DtdProcessing.Ignore,
               IgnoreComments = true
             }))
      {
        opfDoc = XDocument.Load(reader);
      }

      var manifest = opfDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "manifest");
      var spine = opfDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "spine");
      if (manifest == null || spine == null)
        return false;

      var items = new Dictionary<string, (string Href, string Media)>(StringComparer.OrdinalIgnoreCase);
      foreach (var item in manifest.Elements().Where(e => e.Name.LocalName == "item"))
      {
        var id = item.Attribute("id")?.Value;
        var href = item.Attribute("href")?.Value;
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(href))
          continue;
        var media = item.Attribute("media-type")?.Value ?? "";
        var props = item.Attribute("properties")?.Value ?? "";
        items[id] = (href, media + "|" + props);
      }

      var spineIds = new List<string>();
      foreach (var ir in spine.Elements().Where(e => e.Name.LocalName == "itemref"))
      {
        var idref = ir.Attribute("idref")?.Value;
        if (string.IsNullOrEmpty(idref)) continue;
        var lin = ir.Attribute("linear");
        if (lin != null && string.Equals(lin.Value, "no", StringComparison.OrdinalIgnoreCase))
          continue;
        spineIds.Add(idref);
      }

      if (spineIds.Count == 0)
        return false;

      var fullSb = new StringBuilder();
      int chapterIndex = 0;

      foreach (var idref in spineIds)
      {
        if (!items.TryGetValue(idref, out var mi))
          continue;
        if (!IsContentDocument(mi.Href, mi.Media))
          continue;

        string entryPath = CombineZipPath(opfDir, mi.Href);
        var entry = zip.GetEntry(entryPath)
            ?? zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals(entryPath, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
          continue;

        string rawHtml;
        using (var es = entry.Open())
        using (var sr = new StreamReader(es, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
          rawHtml = sr.ReadToEnd();

        if (!TryGetBodyElement(rawHtml, out var body, out var headTitle))
          continue;

        RemoveDangerousElements(body);
        string title = DeriveChapterTitle(body, headTitle, chapterIndex + 1);
        string plain = PlainTextFromBody(body);
        int start = fullSb.Length;
        var bookOffsetQueue = BuildEpubParagraphOffsetQueue(body, plain, start);
        string htmlFragment = EpubBodyToReadingHtml(body, bookOffsetQueue);

        if (string.IsNullOrWhiteSpace(plain) && string.IsNullOrWhiteSpace(htmlFragment))
          continue;

        if (fullSb.Length > 0)
          fullSb.Append("\n\n");
        fullSb.Append(plain);
        int end = fullSb.Length;

        chapters.Add(new Fb2BookTextExtractor.ChapterSpan
        {
          Title = title,
          Start = start,
          End = end
        });
        chapterHtmlFragments.Add(htmlFragment);
        chapterIndex++;
      }

      if (chapters.Count == 0)
        return false;

      fullPlainText = fullSb.ToString();
      return true;
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[EpubBookExtractor] {ex.Message}");
      return false;
    }
    finally
    {
      if (!string.IsNullOrEmpty(tempInnerEpub))
      {
        try { File.Delete(tempInnerEpub); }
        catch { /* ignore */ }
      }
    }
  }

  /// <summary>Архив .epub.zip с вложенным .epub: распаковываем основной файл во временный путь для разбора как обычного EPUB.</summary>
  static string? MaterializeInnerEpubFromEpubZip(string epubZipPath)
  {
    try
    {
      using var zip = ZipFile.OpenRead(epubZipPath);
      var entry = BookZipEntryHelper.FindPrimaryEpubEntry(zip);
      if (entry == null)
        return null;
      var tmp = Path.Combine(Path.GetTempPath(), $"br-epub-read-{Guid.NewGuid():N}.epub");
      using (var fs = File.Create(tmp))
      using (var inp = entry.Open())
        inp.CopyTo(fs);
      return tmp;
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[EpubBookExtractor] MaterializeInnerEpubFromEpubZip: {ex.Message}");
      return null;
    }
  }

  private static bool IsContentDocument(string href, string mediaAndProps)
  {
    var h = href.ToLowerInvariant();
    int pipe = mediaAndProps.IndexOf('|');
    var props = pipe >= 0 ? mediaAndProps[(pipe + 1)..] : "";
    foreach (var p in props.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
      if (string.Equals(p, "nav", StringComparison.OrdinalIgnoreCase))
        return false;
    }
    var media = pipe >= 0 ? mediaAndProps[..pipe] : mediaAndProps;
    if (h.EndsWith(".ncx", StringComparison.Ordinal))
      return false;
    if (h.EndsWith(".xhtml", StringComparison.Ordinal) || h.EndsWith(".html", StringComparison.Ordinal) ||
        h.EndsWith(".htm", StringComparison.Ordinal))
      return true;
    if (media.Contains("xhtml", StringComparison.OrdinalIgnoreCase) ||
        media.Contains("html", StringComparison.OrdinalIgnoreCase))
      return true;
    return false;
  }

  private static string CombineZipPath(string baseDir, string relative)
  {
    var b = baseDir.Replace('\\', '/').TrimEnd('/');
    var r = relative.Replace('\\', '/');
    var full = string.IsNullOrEmpty(b) ? r : b + "/" + r;
    var parts = full.Split('/');
    var list = new List<string>();
    foreach (var p in parts)
    {
      if (p.Length == 0 || p == ".") continue;
      if (p == "..")
      {
        if (list.Count > 0) list.RemoveAt(list.Count - 1);
      }
      else list.Add(p);
    }
    return string.Join("/", list);
  }

  private static bool TryGetBodyElement(string rawHtml, out XElement body, out string? headTitle)
  {
    body = null!;
    headTitle = null;
    rawHtml ??= "";
    try
    {
      var settings = new System.Xml.XmlReaderSettings
      {
        DtdProcessing = System.Xml.DtdProcessing.Ignore,
        IgnoreComments = true
      };
      using var stringReader = new StringReader(rawHtml);
      using var reader = System.Xml.XmlReader.Create(stringReader, settings);
      var doc = XDocument.Load(reader);
      var b = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
      if (b != null)
      {
        body = b;
        var t = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "head")
            ?.Descendants().FirstOrDefault(e => e.Name.LocalName == "title");
        headTitle = t?.Value?.Trim();
        return true;
      }
    }
    catch { }

    var m = Regex.Match(rawHtml, @"<body[^>]*>(?<inner>[\s\S]*?)</body>", RegexOptions.IgnoreCase);
    if (!m.Success)
      return false;
    var inner = m.Groups["inner"].Value;
    inner = Regex.Replace(inner, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
    inner = Regex.Replace(inner, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
    try
    {
      var wrapped = "<root xmlns=\"http://www.w3.org/1999/xhtml\">" + inner + "</root>";
      var frag = XDocument.Parse(wrapped, LoadOptions.PreserveWhitespace);
      body = new XElement("body", frag.Root != null ? frag.Root.Nodes() : Array.Empty<XNode>());
    }
    catch
    {
      var plain = WebUtility.HtmlDecode(Regex.Replace(inner, @"<[^>]+>", " "));
      plain = CollapseWs(plain);
      body = new XElement("body", new XElement("p", plain));
    }

    var hm = Regex.Match(rawHtml, @"<title[^>]*>(?<t>[\s\S]*?)</title>", RegexOptions.IgnoreCase);
    if (hm.Success)
      headTitle = WebUtility.HtmlDecode(Regex.Replace(hm.Groups["t"].Value, @"<[^>]+>", "")).Trim();
    return true;
  }

  private static void RemoveDangerousElements(XElement root)
  {
    foreach (var el in root.Descendants().ToList())
    {
      var ln = el.Name.LocalName;
      if (ln is "script" or "style" or "iframe" or "object" or "embed" or "video" or "audio" or "form")
      {
        el.Remove();
        continue;
      }
      el.Attributes().Where(a =>
      {
        var n = a.Name.LocalName;
        return n.StartsWith("on", StringComparison.OrdinalIgnoreCase) || n == "style";
      }).ToList().ForEach(a => a.Remove());
    }
  }

  private static string DeriveChapterTitle(XElement body, string? headTitle, int fallbackNumber)
  {
    foreach (var tag in new[] { "h1", "h2", "h3" })
    {
      var h = body.Descendants().FirstOrDefault(e => e.Name.LocalName == tag);
      if (h != null)
      {
        var t = string.Concat(h.Nodes().OfType<XText>().Select(x => x.Value)).Trim();
        if (string.IsNullOrWhiteSpace(t))
          t = h.Value?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(t) && t.Length < 200)
          return CollapseWs(t);
      }
    }
    if (!string.IsNullOrWhiteSpace(headTitle))
    {
      var t = CollapseWs(headTitle);
      if (t.Length < 200)
        return t;
    }
    return $"Часть {fallbackNumber}";
  }

  private static string CollapseWs(string s)
  {
    return Regex.Replace(s.Trim(), @"\s+", " ");
  }

  /// <summary>
  /// Плоский текст главы: те же блоки, что <c>p.para</c> в WebView, через <c>\n\n</c> — иначе офлайн-сопоставление
  /// видит всю главу одним абзацем и подставляет лишние предложения (как у Royallib).
  /// </summary>
  private static string PlainTextFromBody(XElement body)
  {
    var keys = CollectEpubParaPlainKeys(body);
    var parts = new List<string>();
    foreach (var keyNullable in keys)
    {
      if (keyNullable == null)
        continue;
      var k = CollapseWs(keyNullable).Trim();
      if (k.Length > 0)
        parts.Add(k);
    }
    if (parts.Count > 0)
      return string.Join("\n\n", parts);

    var sb = new StringBuilder();
    AppendPlainLegacy(body, sb);
    return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
  }

  private static void AppendPlainLegacy(XNode node, StringBuilder sb)
  {
    switch (node)
    {
      case XText t:
        sb.Append(t.Value);
        break;
      case XElement e:
        var ln = e.Name.LocalName;
        if (ln is "p" or "div" or "br" or "h1" or "h2" or "h3" or "h4" or "li" or "tr")
          sb.Append(' ');
        foreach (var c in e.Nodes())
          AppendPlainLegacy(c, sb);
        if (ln is "p" or "div" or "h1" or "h2" or "h3" or "h4" or "li" or "blockquote" or "section" or "article")
          sb.Append(' ');
        break;
    }
  }

  /// <summary>
  /// Смещение в глобальном плоском тексте для каждого блока <c>p.para</c> в разметке главы.
  /// Всегда монотонно неубывающее — иначе в WebView у части абзацев нет <c>data-bo</c>, скрипт
  /// <c>__readerScrollToBookOffset</c> не находит узлы и заметка открывается только на «доле главы» (начало).
  /// </summary>
  private static Queue<int>? BuildEpubParagraphOffsetQueue(XElement body, string plainChapter, int chapterGlobalStart)
  {
    var keys = CollectEpubParaPlainKeys(body);
    if (keys.Count == 0)
      return null;
    var flat = (plainChapter ?? "").Replace("\r", "");
    int chEnd = chapterGlobalStart + Math.Max(0, flat.Length - 1);
    var raw = new List<int>(keys.Count);
    int searchCursor = 0;
    foreach (var keyNullable in keys)
    {
      if (keyNullable == null)
      {
        raw.Add(-1);
        continue;
      }
      var k = CollapseWs(keyNullable).Trim();
      if (k.Length == 0)
      {
        raw.Add(-1);
        continue;
      }
      while (searchCursor < flat.Length && char.IsWhiteSpace(flat[searchCursor]))
        searchCursor++;
      int idx = flat.IndexOf(k, searchCursor, StringComparison.Ordinal);
      if (idx < 0)
        idx = flat.IndexOf(k, searchCursor, StringComparison.OrdinalIgnoreCase);
      if (idx < 0)
        raw.Add(-1);
      else
      {
        raw.Add(chapterGlobalStart + idx);
        searchCursor = idx + Math.Max(1, k.Length);
      }
    }

    var q = new Queue<int>();
    int lastAssigned = chapterGlobalStart - 1;
    for (int i = 0; i < raw.Count; i++)
    {
      int bo = raw[i];
      if (bo < 0)
        bo = Math.Min(chEnd, lastAssigned + 1);
      else
      {
        if (bo <= lastAssigned)
          bo = Math.Min(chEnd, lastAssigned + 1);
      }
      if (bo < chapterGlobalStart)
        bo = chapterGlobalStart;
      q.Enqueue(bo);
      lastAssigned = bo;
    }
    return q;
  }

  static List<string?> CollectEpubParaPlainKeys(XElement body)
  {
    var keys = new List<string?>();
    foreach (var child in body.Nodes())
      CollectEpubParaKeysFromNode(child, keys);
    return keys;
  }

  static void CollectEpubParaKeysFromNode(XNode node, List<string?> keys)
  {
    switch (node)
    {
      case XText t:
      {
        var v = t.Value;
        if (string.IsNullOrWhiteSpace(v))
          return;
        keys.Add(CollapseWs(v.Trim()));
        return;
      }
      case XElement e:
        CollectEpubParaKeysFromElement(e, keys);
        return;
    }
  }

  static void CollectEpubParaKeysFromElement(XElement e, List<string?> keys)
  {
    var ln = e.Name.LocalName;
    switch (ln)
    {
      case "p":
        if (ShouldSkipEpubWhitespaceOnlyBlock(e))
          return;
        keys.Add(EpubBlockPlainOneLine(e));
        return;
      case "h1":
      case "h2":
      case "h3":
      case "h4":
      case "h5":
      case "h6":
      case "br":
      case "hr":
        return;
      case "blockquote":
        foreach (var c in e.Nodes())
        {
          if (c is XElement ce && ce.Name.LocalName == "p")
          {
            if (ShouldSkipEpubWhitespaceOnlyBlock(ce))
              continue;
            keys.Add(EpubBlockPlainOneLine(ce));
          }
          else
            CollectEpubParaKeysFromNode(c, keys);
        }
        return;
      case "ul":
      case "ol":
        foreach (var li in e.Elements().Where(x => x.Name.LocalName == "li"))
        {
          if (ShouldSkipEpubWhitespaceOnlyBlock(li))
            continue;
          keys.Add(EpubBlockPlainOneLine(li));
        }
        return;
      case "div":
      case "section":
      case "article":
      case "main":
      case "header":
      case "footer":
      case "aside":
      case "span":
        foreach (var c in e.Nodes())
          CollectEpubParaKeysFromNode(c, keys);
        return;
      case "pre":
        keys.Add(EpubBlockPlainOneLine(e));
        return;
      case "table":
        foreach (var row in e.Descendants().Where(x => x.Name.LocalName == "tr"))
        {
          if (ShouldSkipEpubWhitespaceOnlyBlock(row))
            continue;
          keys.Add(EpubBlockPlainOneLine(row));
        }
        return;
      case "img":
      {
        var alt = e.Attribute("alt")?.Value;
        if (!string.IsNullOrWhiteSpace(alt))
          keys.Add(null);
        return;
      }
      case "a":
        return;
      default:
        if (e.Elements().Any())
        {
          foreach (var c in e.Nodes())
            CollectEpubParaKeysFromNode(c, keys);
        }
        else
        {
          var t = e.Value?.Trim();
          if (!string.IsNullOrEmpty(t))
            keys.Add(CollapseWs(t));
        }
        return;
    }
  }

  static string EpubBlockPlainOneLine(XElement e) =>
      CollapseWs(string.Concat(e.DescendantNodes().OfType<XText>().Select(x => x.Value)));

  /// <summary>Пустой блок (только пробелы) без медиа даёт лишний вертикальный зазор между абзацами.</summary>
  private static bool ShouldSkipEpubWhitespaceOnlyBlock(XElement block)
  {
    if (block == null)
      return false;
    if (!string.IsNullOrWhiteSpace(EpubBlockPlainOneLine(block)))
      return false;
    foreach (var d in block.Descendants())
    {
      switch (d.Name.LocalName)
      {
        case "img":
        case "svg":
        case "canvas":
        case "math":
        case "video":
        case "audio":
        case "object":
        case "iframe":
          return false;
      }
    }
    return true;
  }

  static void AppendEpubParaOpen(StringBuilder sb, Queue<int>? bookOffsets, string? extraAttrsBeforeCloseAngle = null)
  {
    sb.Append("<p class=\"para\"");
    if (!string.IsNullOrEmpty(extraAttrsBeforeCloseAngle))
      sb.Append(extraAttrsBeforeCloseAngle);
    if (bookOffsets != null && bookOffsets.TryDequeue(out int v) && v >= 0)
    {
      sb.Append(" data-bo=\"");
      sb.Append(v.ToString(CultureInfo.InvariantCulture));
      sb.Append("\"");
    }
    sb.Append(">");
  }

  private static string EpubBodyToReadingHtml(XElement body, Queue<int>? bookOffsets = null)
  {
    var sb = new StringBuilder();
    foreach (var child in body.Nodes())
      AppendHtmlNode(child, sb, bookOffsets);
    return sb.ToString();
  }

  private static void AppendHtmlNode(XNode node, StringBuilder sb, Queue<int>? bookOffsets)
  {
    switch (node)
    {
      case XText t:
      {
        var v = t.Value;
        if (string.IsNullOrWhiteSpace(v)) return;
        AppendEpubParaOpen(sb, bookOffsets);
        sb.Append(WebUtility.HtmlEncode(v.Trim()));
        sb.Append("</p>");
        break;
      }
      case XElement e:
        EmitElement(e, sb, bookOffsets);
        break;
    }
  }

  private static void EmitElement(XElement e, StringBuilder sb, Queue<int>? bookOffsets)
  {
    var ln = e.Name.LocalName;
    switch (ln)
    {
      case "p":
        if (ShouldSkipEpubWhitespaceOnlyBlock(e))
          return;
        AppendEpubParaOpen(sb, bookOffsets);
        AppendInline(e, sb);
        sb.Append("</p>");
        break;
      case "h1":
      case "h2":
        sb.Append("<h2 class=\"chapter\">");
        AppendInlinePlainEncoded(e, sb);
        sb.Append("</h2>");
        break;
      case "h3":
      case "h4":
      case "h5":
      case "h6":
        sb.Append("<h3 class=\"subtitle\">");
        AppendInlinePlainEncoded(e, sb);
        sb.Append("</h3>");
        break;
      case "blockquote":
        sb.Append("<cite class=\"fb2\">");
        foreach (var c in e.Nodes())
        {
          if (c is XElement ce && ce.Name.LocalName == "p")
          {
            if (ShouldSkipEpubWhitespaceOnlyBlock(ce))
              continue;
            AppendEpubParaOpen(sb, bookOffsets);
            AppendInline(ce, sb);
            sb.Append("</p>");
          }
          else
            AppendHtmlNode(c, sb, bookOffsets);
        }
        sb.Append("</cite>");
        break;
      case "br":
        sb.Append("<br/>");
        break;
      case "hr":
        sb.Append("<div class=\"vspace\"></div>");
        break;
      case "ul":
      case "ol":
        foreach (var li in e.Elements().Where(x => x.Name.LocalName == "li"))
        {
          if (ShouldSkipEpubWhitespaceOnlyBlock(li))
            continue;
          AppendEpubParaOpen(sb, bookOffsets);
          sb.Append("• ");
          AppendInline(li, sb);
          sb.Append("</p>");
        }
        break;
      case "div":
      case "section":
      case "article":
      case "main":
      case "header":
      case "footer":
      case "aside":
      case "span":
        foreach (var c in e.Nodes())
          AppendHtmlNode(c, sb, bookOffsets);
        break;
      case "pre":
        AppendEpubParaOpen(sb, bookOffsets, " style=\"white-space:pre-wrap;font-family:system-ui,monospace\"");
        AppendInlinePlainEncoded(e, sb);
        sb.Append("</p>");
        break;
      case "table":
        foreach (var row in e.Descendants().Where(x => x.Name.LocalName == "tr"))
        {
          if (ShouldSkipEpubWhitespaceOnlyBlock(row))
            continue;
          AppendEpubParaOpen(sb, bookOffsets);
          AppendInlinePlainEncoded(row, sb);
          sb.Append("</p>");
        }
        break;
      case "img":
      {
        var alt = e.Attribute("alt")?.Value;
        if (!string.IsNullOrWhiteSpace(alt))
        {
          AppendEpubParaOpen(sb, bookOffsets);
          sb.Append("<em>[");
          sb.Append(WebUtility.HtmlEncode(alt.Trim()));
          sb.Append("]</em></p>");
        }
        break;
      }
      case "a":
        AppendInline(e, sb);
        break;
      default:
        if (e.Elements().Any())
        {
          foreach (var c in e.Nodes())
            AppendHtmlNode(c, sb, bookOffsets);
        }
        else
        {
          var t = e.Value?.Trim();
          if (!string.IsNullOrEmpty(t))
          {
            AppendEpubParaOpen(sb, bookOffsets);
            sb.Append(WebUtility.HtmlEncode(t));
            sb.Append("</p>");
          }
        }
        break;
    }
  }

  private static void AppendInline(XElement e, StringBuilder sb)
  {
    foreach (var n in e.Nodes())
    {
      switch (n)
      {
        case XText t:
          sb.Append(WebUtility.HtmlEncode(t.Value));
          break;
        case XElement c:
        {
          var ln = c.Name.LocalName;
          if (ln is "strong" or "b")
          {
            sb.Append("<strong>");
            AppendInline(c, sb);
            sb.Append("</strong>");
          }
          else if (ln is "em" or "i")
          {
            sb.Append("<em>");
            AppendInline(c, sb);
            sb.Append("</em>");
          }
          else if (ln == "br")
            sb.Append("<br/>");
          else if (ln is "span" or "small" or "sub" or "sup")
            AppendInline(c, sb);
          else
            AppendInlinePlainEncoded(c, sb);
          break;
        }
      }
    }
  }

  private static void AppendInlinePlainEncoded(XElement e, StringBuilder sb)
  {
    var plain = string.Concat(e.DescendantNodes().OfType<XText>().Select(x => x.Value));
    sb.Append(WebUtility.HtmlEncode(CollapseWs(plain)));
  }
}
