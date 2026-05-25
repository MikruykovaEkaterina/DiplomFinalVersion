using System;
using BookReaderApp.Services;
var path = @"d:\project vs\BookReaderAppForAIAll2precedenta\T_Axakov_ALEN_KIJ_TsVETOChEK_-_royallib_com.epub";
if (EpubBookExtractor.TryExtract(path, out var plain, out _, out _))
{
  var blocks = SentenceAlignment.SplitParagraphBlocks(plain);
  for (int i = 0; i < Math.Min(6, blocks.Count); i++)
  {
    var s = SentenceAlignment.SplitBlockIntoSentences(blocks[i]);
    Console.WriteLine($"P{i} sents={s.Count} len={blocks[i].Length}");
    Console.WriteLine(blocks[i].Length > 120 ? blocks[i][..120] + "..." : blocks[i]);
    Console.WriteLine();
  }
}
else Console.WriteLine("extract failed");
