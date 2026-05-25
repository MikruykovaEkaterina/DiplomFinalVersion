using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BookReaderApp.Services;

/// <summary>
/// Разбор абзацев FB2 с сохранением начертания (strong, emphasis, strikethrough).
/// </summary>
public static class Fb2RichParagraphParser
{
  /// <summary>
  /// Главы для оглавления: прямые &lt;section&gt; у &lt;body&gt;, с разворачиванием одиночной обёртки,
  /// если у неё нет собственных абзацев до первой вложенной &lt;section&gt; (типичный FB2: одна внешняя секция на всю книгу).
  /// </summary>
  public static List<XElement> GetOrderedSectionElements(XElement body)
  {
    var sections = GetDirectChildSections(body);
    while (sections.Count == 1)
    {
      var outer = sections[0];
      if (HasOwnBlockContentBeforeFirstChildSection(outer))
        break;
      var inner = GetDirectChildSections(outer);
      if (inner.Count == 0)
        break;
      sections = inner;
    }
    return sections;
  }

  private static List<XElement> GetDirectChildSections(XElement parent)
  {
    var list = new List<XElement>();
    XNamespace ns = parent.GetDefaultNamespace();
    if (!string.IsNullOrEmpty(ns.NamespaceName))
    {
      foreach (var child in parent.Elements(ns + "section"))
        list.Add(child);
    }
    if (list.Count == 0)
    {
      foreach (var child in parent.Elements())
      {
        if (child.Name.LocalName == "section")
          list.Add(child);
      }
    }
    return list;
  }

  /// <summary>Есть ли у секции блок контента (абзац и т.д.) до первой вложенной &lt;section&gt;.</summary>
  private static bool HasOwnBlockContentBeforeFirstChildSection(XElement section)
  {
    foreach (var child in section.Elements())
    {
      if (child.Name.LocalName == "title")
        continue;
      if (child.Name.LocalName == "section")
        return false;
      if (child.Name.LocalName == "empty-line")
        continue;
      return true;
    }
    return false;
  }

  /// <summary>
  /// Прямые дочерние элементы секции (кроме title и вложенных section).
  /// </summary>
  public static IEnumerable<XElement> EnumerateContentElements(XElement section)
  {
    foreach (var child in section.Elements())
    {
      if (child.Name.LocalName == "title")
        continue;
      if (child.Name.LocalName == "section")
        continue;
      yield return child;
    }
  }

  /// <summary>Строит <see cref="FormattedString"/> по дочерним inline-элементам FB2-параграфа.</summary>
  public static FormattedString BuildFormattedParagraph(XElement pElement, double fontSize, Color textColor)
  {
    var parts = new List<(string Text, FontAttributes Fa, TextDecorations Td)>();
    foreach (var node in pElement.Nodes())
      CollectParts(node, FontAttributes.None, TextDecorations.None, parts);
    MergeAdjacent(parts);
    var fs = new FormattedString();
    foreach (var (text, fa, td) in parts)
    {
      if (string.IsNullOrEmpty(text))
        continue;
      fs.Spans.Add(new Span
      {
        Text = text,
        FontAttributes = fa,
        TextDecorations = td,
        FontSize = fontSize,
        TextColor = textColor
      });
    }
    return fs;
  }

  /// <summary>
  /// Плоский текст абзаца.
  /// </summary>
  /// <summary>Текстовое содержимое параграфа без разметки (для офлайна и оффсета).</summary>
  public static string ParagraphPlainText(XElement pElement)
  {
    var sb = new StringBuilder();
    foreach (var t in pElement.DescendantNodes().OfType<XText>())
      sb.Append(t.Value);
    return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
  }

  private static void CollectParts(XNode node, FontAttributes fa, TextDecorations td,
    List<(string Text, FontAttributes Fa, TextDecorations Td)> parts)
  {
    switch (node)
    {
      case XText t:
        if (t.Value.Length > 0)
          parts.Add((t.Value, fa, td));
        return;
      case XElement e:
        FontAttributes nfa = fa;
        TextDecorations ntd = td;
        switch (e.Name.LocalName)
        {
          case "emphasis":
            nfa |= FontAttributes.Italic;
            break;
          case "strikethrough":
            ntd |= TextDecorations.Strikethrough;
            break;
        }
        foreach (var child in e.Nodes())
          CollectParts(child, nfa, ntd, parts);
        return;
    }
  }

  private static void MergeAdjacent(List<(string Text, FontAttributes Fa, TextDecorations Td)> parts)
  {
    if (parts.Count <= 1) return;
    int i = 0;
    while (i < parts.Count - 1)
    {
      var (a, fa, td) = parts[i];
      var (b, fa2, td2) = parts[i + 1];
      if (fa == fa2 && td == td2)
      {
        parts[i] = (a + b, fa, td);
        parts.RemoveAt(i + 1);
      }
      else
        i++;
    }
  }
}
