namespace TranslationProxy;

/// <summary>
/// Многосимвольные сокращения перед точкой (нижний регистр для сравнения): при таком слове до <c>.</c>
/// граница предложения для чанкинга не ставится. Однобуквенные случаи («т. д.») обрабатываются в <see cref="BookTextChunker"/> отдельно.
/// </summary>
public static class SentenceAbbreviationLexicon
{
  /// <summary>Слова из двух и более букв; избегаются двусимвольные совпадения с обычными словами (<c>no</c>, <c>al</c>).</summary>
  public static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
  {
    // English
    "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "vs", "vol", "st",
    "ave", "blvd", "dept", "fig", "approx", "symb",
    "ltd", "inc", "corp", "co", "tel", "fax", "min", "sec", "hr",
    "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    "ed", "eds", "ch", "pp",
    "gen", "rep", "sen", "hon", "esq", "etc",
    // Russian
    "стр", "табл", "рис", "гл", "см", "им", "науч", "сокр", "инж", "гос", "акад", "кв", "ул", "пер", "обл", "гг", "увм", "тт", "тыс", "млн", "млрд"
  };
}
