using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TranslationProxy;

/// <summary>
/// Перевод содержимого FB2 и EPUB через LibreTranslate с сохранением структуры XML:
/// вытягивает текст из выбранных элементов, режет на чанки (<see cref="BookTextChunker"/>), подставляет перевод обратно.
/// </summary>
public sealed class StructuredBookTranslator
{
  readonly LibreTranslateClient _lt;
  readonly int _maxChunkChars;

  public StructuredBookTranslator(LibreTranslateClient lt, int maxChunkChars)
  {
    _lt = lt;
    _maxChunkChars = Math.Clamp(maxChunkChars, 256, 12000);
  }

  public async Task TranslateFb2PathAsync(string pathIn, string pathOut, string srcIso, string tgtIso, IProgress<double>? progress, CancellationToken ct)
  {
    var xml = await File.ReadAllTextAsync(pathIn, ct).ConfigureAwait(false);
    xml = BookFormatDetector.SanitizeXmlText(xml);
    BookFormatDetector.EnsureXlinkNs(ref xml);

    var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
    var elements = CollectFb2TranslatableElements(doc)
        .Where(e => !string.IsNullOrWhiteSpace(e.Value))
        .ToList();
    int n = Math.Max(1, elements.Count);
    for (int i = 0; i < elements.Count; i++)
    {
      ct.ThrowIfCancellationRequested();
      var el = elements[i];
      var translated = await TranslateLongTextAsync(el.Value, srcIso, tgtIso, ct).ConfigureAwait(false);
      el.RemoveNodes();
      el.Add(new XText(translated));
      progress?.Report((i + 1.0) / n);
    }

    Directory.CreateDirectory(Path.GetDirectoryName(pathOut)!);
    var settings = new XmlWriterSettings { Async = true, Indent = false, OmitXmlDeclaration = false, CloseOutput = true };
    await using (var writer = XmlWriter.Create(pathOut, settings))
    {
      doc.Save(writer);
    }
  }

  public async Task RepackFb2ZipAsync(string zipIn, string zipOut, string srcIso, string tgtIso, IProgress<double>? progress, CancellationToken ct)
  {
    var work = Path.Combine(Path.GetTempPath(), "fb2z-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try
    {
      string? innerName = null;
      string? fb2Path = null;
      using (var read = ZipFile.OpenRead(zipIn))
      {
        foreach (var e in read.Entries)
        {
          if (e.FullName.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))
          {
            innerName = e.FullName.Replace('\\', '/');
            fb2Path = Path.Combine(work, "_book.fb2");
            e.ExtractToFile(fb2Path, true);
            break;
          }
        }
      }
      if (fb2Path == null || innerName == null)
        throw new InvalidOperationException("В архиве не найден файл .fb2.");

      var outFb2 = Path.Combine(work, "_out.fb2");
      await TranslateFb2PathAsync(fb2Path, outFb2, srcIso, tgtIso, progress, ct).ConfigureAwait(false);

      if (File.Exists(zipOut))
        File.Delete(zipOut);
      using (var write = new ZipArchive(File.Create(zipOut), ZipArchiveMode.Create))
      {
        write.CreateEntryFromFile(outFb2, innerName.Replace('\\', '/'), CompressionLevel.Optimal);
        using var read = ZipFile.OpenRead(zipIn);
        foreach (var e in read.Entries)
        {
          if (e.FullName.Equals(innerName, StringComparison.OrdinalIgnoreCase))
            continue;
          var dest = write.CreateEntry(e.FullName.Replace('\\', '/'));
          await using var o = dest.Open();
          await using var inp = e.Open();
          await inp.CopyToAsync(o, ct).ConfigureAwait(false);
        }
      }
    }
    finally
    {
      try { Directory.Delete(work, true); } catch { }
    }
  }

  public async Task TranslateEpubTreeAsync(string rootDir, string srcIso, string tgtIso, IProgress<double>? progress, CancellationToken ct)
  {
    var files = Directory.GetFiles(rootDir, "*.*", SearchOption.AllDirectories)
        .Where(f =>
        {
          var l = f.ToLowerInvariant();
          return l.EndsWith(".xhtml") || l.EndsWith(".html") || l.EndsWith(".htm");
        })
        .ToList();

    var batches = new List<(string Path, List<XElement> Els)>();
    int total = 0;
    foreach (var f in files)
    {
      try
      {
        var raw = await File.ReadAllTextAsync(f, ct).ConfigureAwait(false);
        if (raw.IndexOf('<') < 0)
          continue;
        var doc = XDocument.Parse(raw, LoadOptions.PreserveWhitespace);
        var els = doc.Descendants()
            .Where(e => e.Name.LocalName == "p" && !string.IsNullOrWhiteSpace(e.Value))
            .ToList();
        if (els.Count == 0)
          continue;
        batches.Add((f, els));
        total += els.Count;
      }
      catch
      {
        // пропускаем невалидные фрагменты
      }
    }

    int done = 0;
    int denom = Math.Max(1, total);
    foreach (var (path, _) in batches)
    {
      ct.ThrowIfCancellationRequested();
      var raw = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
      var doc = XDocument.Parse(raw, LoadOptions.PreserveWhitespace);
      var els = doc.Descendants()
          .Where(e => e.Name.LocalName == "p" && !string.IsNullOrWhiteSpace(e.Value))
          .ToList();
      foreach (var el in els)
      {
        ct.ThrowIfCancellationRequested();
        var translated = await TranslateLongTextAsync(el.Value, srcIso, tgtIso, ct).ConfigureAwait(false);
        el.RemoveNodes();
        el.Add(new XText(translated));
        done++;
        progress?.Report(done / (double)denom);
      }

      var settings = new XmlWriterSettings { Async = true, Indent = false, OmitXmlDeclaration = false };
      await using (var writer = XmlWriter.Create(path, settings))
        doc.Save(writer);
    }

    await TranslateEpubPackageMetadataAsync(rootDir, srcIso, tgtIso, ct).ConfigureAwait(false);
  }

  public async Task RepackEpubZipAsync(string zipIn, string zipOut, string srcIso, string tgtIso, IProgress<double>? progress, CancellationToken ct)
  {
    var work = Path.Combine(Path.GetTempPath(), "epub-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try
    {
      ZipFile.ExtractToDirectory(zipIn, work, overwriteFiles: true);
      await TranslateEpubTreeAsync(work, srcIso, tgtIso, progress, ct).ConfigureAwait(false);
      if (File.Exists(zipOut))
        File.Delete(zipOut);
      ZipFile.CreateFromDirectory(work, zipOut, CompressionLevel.Optimal, includeBaseDirectory: false);
    }
    finally
    {
      try { Directory.Delete(work, true); } catch { }
    }
  }

  async Task<string> TranslateLongTextAsync(string text, string srcIso, string tgtIso, CancellationToken ct)
  {
    if (string.Equals(srcIso, tgtIso, StringComparison.OrdinalIgnoreCase))
      return text;
    var chunks = BookTextChunker.ChunkByLength(text, _maxChunkChars).ToList();
    if (chunks.Count == 0)
      return text;

    var parts = new List<string>(chunks.Count);
    foreach (var ch in chunks)
    {
      var t = await _lt.TranslateAsync(ch, srcIso, tgtIso, ct).ConfigureAwait(false);
      if (string.IsNullOrEmpty(t))
        throw new InvalidOperationException("Пустой ответ переводчика.");
      parts.Add(TranslationClosingPunctuation.AlignTrailingWithSource(ch, t));
    }
    return string.Concat(parts);
  }

  public async Task TranslateEpubPackageMetadataAsync(string rootDir, string srcIso, string tgtIso, CancellationToken ct)
  {
    var containerPath = Path.Combine(rootDir, "META-INF", "container.xml");
    if (!File.Exists(containerPath))
      return;

    string? opfRel = null;
    try
    {
      var cdoc = XDocument.Load(containerPath, LoadOptions.None);
      opfRel = cdoc.Descendants()
          .FirstOrDefault(e => e.Name.LocalName.Equals("rootfile", StringComparison.OrdinalIgnoreCase))
          ?.Attribute("full-path")
          ?.Value;
    }
    catch
    {
      return;
    }

    if (string.IsNullOrWhiteSpace(opfRel))
      return;

    var opfPath = Path.GetFullPath(Path.Combine(rootDir, opfRel.Replace('/', Path.DirectorySeparatorChar)));
    var rootFull = Path.GetFullPath(rootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    var rel = Path.GetRelativePath(rootFull, opfPath);
    if (rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || rel == ".."
        || !File.Exists(opfPath))
      return;

    XDocument opf;
    try
    {
      opf = XDocument.Load(opfPath, LoadOptions.PreserveWhitespace);
    }
    catch
    {
      return;
    }

    var metadata = opf.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("metadata", StringComparison.OrdinalIgnoreCase));
    if (metadata == null)
      return;

    foreach (var title in metadata.Elements().Where(e => e.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase)).ToList())
    {
      var t = (title.Value ?? "").Trim();
      if (string.IsNullOrEmpty(t))
        continue;
      var tr = await TranslateLongTextAsync(t, srcIso, tgtIso, ct).ConfigureAwait(false);
      title.RemoveNodes();
      title.Add(new XText(tr));
    }

    foreach (var creator in metadata.Elements().Where(e => e.Name.LocalName.Equals("creator", StringComparison.OrdinalIgnoreCase)).ToList())
    {
      var t = DcCreatorOrTitleToPlain(creator);
      if (string.IsNullOrEmpty(t))
        continue;
      var tr = await TranslateLongTextAsync(t, srcIso, tgtIso, ct).ConfigureAwait(false);
      creator.RemoveNodes();
      creator.Add(new XText(tr));
    }

    foreach (var desc in metadata.Elements().Where(e => e.Name.LocalName.Equals("description", StringComparison.OrdinalIgnoreCase)).ToList())
    {
      var plain = DcDescriptionToPlain(desc);
      if (string.IsNullOrWhiteSpace(plain))
        continue;
      var tr = await TranslateLongTextAsync(plain, srcIso, tgtIso, ct).ConfigureAwait(false);
      desc.RemoveNodes();
      desc.Add(new XText(tr));
    }

    var ws = new XmlWriterSettings { Async = false, Indent = false, OmitXmlDeclaration = false };
    using (var writer = XmlWriter.Create(opfPath, ws))
      opf.Save(writer);
  }

  static string DcDescriptionToPlain(XElement desc)
  {
    var fromNodes = string.Join(" ",
        desc.DescendantNodes().OfType<XText>()
            .Select(t => t.Value.Trim())
            .Where(s => s.Length > 0));
    var raw = string.IsNullOrWhiteSpace(fromNodes) ? (desc.Value ?? "") : fromNodes;
    return StripHtmlishToPlain(raw);
  }

  static string DcCreatorOrTitleToPlain(XElement el)
  {
    var fromNodes = string.Join(" ",
        el.DescendantNodes().OfType<XText>()
            .Select(t => t.Value.Trim())
            .Where(s => s.Length > 0));
    var raw = string.IsNullOrWhiteSpace(fromNodes) ? (el.Value ?? "") : fromNodes;
    return StripHtmlishToPlain(raw);
  }

  static string StripHtmlishToPlain(string? raw)
  {
    if (string.IsNullOrWhiteSpace(raw))
      return "";
    var s = WebUtility.HtmlDecode(raw.Trim());
    s = Regex.Replace(s, @"<[^>]+>", " ");
    s = Regex.Replace(s, @"\s+", " ").Trim();
    return s;
  }

  static IEnumerable<XElement> CollectFb2TranslatableElements(XDocument doc)
  {
    foreach (var el in doc.Descendants())
    {
      if (el.Name.LocalName.Equals("binary", StringComparison.OrdinalIgnoreCase))
        continue;
      var ln = el.Name.LocalName;
      if (IsUnderBody(el) && ln is "p" or "v" or "subtitle" or "text-author" or "cite")
        yield return el;
      if (IsUnderTitleInfo(el) && ln is "book-title" or "first-name" or "last-name" or "middle-name" or "nickname")
        yield return el;
      if (IsUnderAnnotation(el) && ln is "p")
        yield return el;
    }
  }

  static bool IsUnderBody(XElement el) =>
      el.Ancestors().Any(a => a.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));

  static bool IsUnderTitleInfo(XElement el) =>
      el.Ancestors().Any(a => a.Name.LocalName.Equals("title-info", StringComparison.OrdinalIgnoreCase));

  static bool IsUnderAnnotation(XElement el) =>
      el.Ancestors().Any(a => a.Name.LocalName.Equals("annotation", StringComparison.OrdinalIgnoreCase));
}
