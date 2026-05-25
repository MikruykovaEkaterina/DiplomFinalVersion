using System.IO.Compression;
using System.Text.RegularExpressions;

namespace TranslationProxy;

/// <summary>
/// Определяет формат книги (FB2, EPUB, архивы) по имени загрузки и при необходимости по содержимому ZIP;
/// чинит типичные проблемы FB2 XML перед разбором (невалидные символы, неэкранированный <c>&amp;</c>, xmlns xlink).
/// </summary>
public static class BookFormatDetector
{
  static readonly Regex AmpFix = new(@"&(?!(?:amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

  public static BookFormatKind DetectFromFilePath(string path, string? uploadOriginalName = null)
  {
    var fromName = DetectFromUploadName(uploadOriginalName) != BookFormatKind.Unknown
        ? DetectFromUploadName(uploadOriginalName)
        : DetectFromUploadName(Path.GetFileName(path));
    if (fromName != BookFormatKind.Unknown)
      return fromName;

    try
    {
      using var zip = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
      foreach (var e in zip.Entries)
      {
        var en = e.FullName.ToLowerInvariant();
        if (en.EndsWith(".fb2", StringComparison.Ordinal))
          return BookFormatKind.Fb2Zip;
      }
      foreach (var e in zip.Entries)
      {
        if (e.FullName.Equals("mimetype", StringComparison.OrdinalIgnoreCase))
          return BookFormatKind.EpubZip;
      }
    }
    catch { }

    return BookFormatKind.Unknown;
  }

  public static BookFormatKind DetectFromUploadName(string? uploadName)
  {
    if (string.IsNullOrEmpty(uploadName))
      return BookFormatKind.Unknown;
    var n = uploadName.ToLowerInvariant();
    if (n.EndsWith(".fb2.zip", StringComparison.Ordinal))
      return BookFormatKind.Fb2Zip;
    if (n.EndsWith(".epub.zip", StringComparison.Ordinal))
      return BookFormatKind.EpubZip;
    if (n.EndsWith(".fb2", StringComparison.Ordinal))
      return BookFormatKind.Fb2;
    if (n.EndsWith(".epub", StringComparison.Ordinal))
      return BookFormatKind.Epub;
    return BookFormatKind.Unknown;
  }

  public static string SanitizeXmlText(string xmlContent)
  {
    if (string.IsNullOrEmpty(xmlContent))
      return xmlContent;
    var s = Regex.Replace(xmlContent, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");
    if (s.Length > 0 && s[0] == '\uFEFF')
      s = s[1..];
    return AmpFix.Replace(s, "&amp;");
  }

  public static void EnsureXlinkNs(ref string xml)
  {
    if (xml.Contains("xmlns:xlink", StringComparison.OrdinalIgnoreCase))
      return;
    var m = Regex.Match(xml, @"<FictionBook\s+", RegexOptions.IgnoreCase);
    if (m.Success)
      xml = xml.Insert(m.Index + m.Length, "xmlns:xlink=\"http://www.w3.org/1999/xlink\" ");
    else
      xml = xml.Replace("<FictionBook>", "<FictionBook xmlns:xlink=\"http://www.w3.org/1999/xlink\">", StringComparison.OrdinalIgnoreCase);
  }
}
