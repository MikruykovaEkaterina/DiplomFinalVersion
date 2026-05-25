using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BookReaderApp.Resources;
using Microsoft.Maui.Controls;

namespace BookReaderApp.Services;

/// <summary>
/// HTML/CSS для экрана чтения в WebView: выравнивание и переносы через движок браузера.
/// </summary>
public static class ReadingHtmlBuilder
{
  static string DefaultChapterTitle(int chapterNumberOneBased) =>
      string.Format(Strings.Reading_ChapterNumberFormat, chapterNumberOneBased);

  public const string AppNavBaseUrl = "https://app.bookreader.local/";

  /// <summary>Вызов из native: снять подсветку предложения (<c>mark.reader-sentence-hl</c>), если загружен скрипт чтения.</summary>
  public const string ClearReaderSentenceHighlightJavaScript =
      "(function(){try{if(typeof window.__readerClearSentenceHl==='function')window.__readerClearSentenceHl()}catch(e){}})();";

  /// <summary>Символьное смещение в плоском тексте книги у верхней видимой строки (абзацы с <c>data-bo</c>).</summary>
  public const string EvaluateGetTopVisibleBookOffsetJavaScript =
      "(function(){try{return(typeof window.__readerGetTopBookOffset==='function')?window.__readerGetTopBookOffset():-1}catch(e){return-1}})()";

  /// <summary>Как <see cref="EvaluateGetTopVisibleBookOffsetJavaScript"/>, но точка отсчёта — центр видимой области (где читают), не верх.</summary>
  public const string EvaluateGetViewportCenterBookOffsetJavaScript =
      "(function(){try{return(typeof window.__readerGetViewportCenterBookOffset==='function')?window.__readerGetViewportCenterBookOffset():-1}catch(e){return-1}})()";

  /// <summary>Оценка символа по <c>scrollY</c> и интервалу главы <c>window.__readerChapterStart/End</c> (fallback при отсутствии <c>data-bo</c>).</summary>
  public const string EvaluateApproxBookOffsetJavaScript =
      "(function(){try{return(typeof window.__readerApproxBookOffset==='function')?window.__readerApproxBookOffset():-1}catch(e){return-1}})()";

  /// <summary>Горизонтальный multicol: грубая оценка символа по <c>scrollLeft / (scrollWidth-clientWidth)</c> и <c>__hrTextLen</c>.</summary>
  public const string EvaluateHorizontalLinearBookApproxOffsetJavaScript =
      "(function(){try{return(typeof window.__readerHrLinearBookApproxOffset==='function')?window.__readerHrLinearBookApproxOffset():-1}catch(e){return-1}})()";

  /// <summary>Горизонтальный multicol: caret у левого края колонки, верх области — «первая видимая позиция на странице».</summary>
  public const string EvaluateHorizontalLeadBookOffsetJavaScript =
      "(function(){try{return(typeof window.__readerGetHorizontalLeadBookOffset==='function')?window.__readerGetHorizontalLeadBookOffset():-1}catch(e){return-1}})()";

  /// <summary>Прокрутить к абзацу с <c>data-bo</c> ≤ offset (вертикальный scroll или <c>#hscroll</c>).</summary>
  public static string MakeScrollToBookOffsetJavaScript(long offset) =>
      "(function(){try{if(typeof window.__readerScrollToBookOffset==='function')window.__readerScrollToBookOffset("
      + Math.Max(0L, offset).ToString(CultureInfo.InvariantCulture) + ");}catch(e){}})()";

  /// <summary>Горизонтальный multicol: грубый <c>scrollLeft ≈ символ/max · maxScroll</c> перед точным <see cref="MakeScrollToBookOffsetJavaScript"/>.</summary>
  public static string MakeHorizontalCoarseSnapToBookOffsetJavaScript(long offset) =>
      "(function(){try{if(typeof window.__readerHrCoarseSnapToBookOffset==='function')window.__readerHrCoarseSnapToBookOffset("
      + Math.Max(0L, offset).ToString(CultureInfo.InvariantCulture) + ");}catch(e){}})()";

  /// <summary>Вертикально: грубо по доле внутри интервала главы [<paramref name="chapterStart"/>, <paramref name="chapterEndExclusive"/>), затем несколько точных <c>__readerScrollToBookOffset</c> после перелома строк.</summary>
  public static string MakeVerticalRevealBookOffsetJavaScript(long bookOffset, long chapterStart, long chapterEndExclusive)
  {
    string n = Math.Max(0L, bookOffset).ToString(CultureInfo.InvariantCulture);
    string cs = Math.Max(0L, chapterStart).ToString(CultureInfo.InvariantCulture);
    string ce = Math.Max(chapterStart + 1, chapterEndExclusive).ToString(CultureInfo.InvariantCulture);
    return "(function(){var n="
        + n
        + ",cs="
        + cs
        + ",ce="
        + ce
        + ";function coarse(){try{var ce2=Math.max(ce,cs+1),sp=(ce2-cs)|0;if(!(sp>=1&&ce2>cs))return;var wi=n-cs;if(wi<0)wi=0;if(wi>sp-1)wi=Math.max(0,sp-1);var ratio=wi/Math.max(1,sp);var ih=Math.max(1,window.innerHeight||0);var sh=Math.max(document.documentElement.scrollHeight||0,document.body.scrollHeight||0);var mx=Math.max(0,sh-ih);window.scrollTo(0,Math.round(mx*Math.min(1,Math.max(0,ratio))));}catch(e){}}"
        + "function fine(){try{if(typeof window.__readerScrollToBookOffset==='function')window.__readerScrollToBookOffset(Math.max(0,Math.floor(n)));}catch(e){}}"
        + "coarse();fine();setTimeout(fine,80);setTimeout(fine,320);setTimeout(fine,760);})()";
  }

  /// <summary>JS после <c>var B='…'</c>: границы предложения по тем же правилам, что в C#; подсветка &lt;mark&gt; без системного selection.</summary>
  const string ReaderSentenceLongPressScriptTail =
      "';function go(p){window.location.href=B+p;}function paraFromNode(n){var p=n;while(p&&p.nodeType!==1)p=p.parentNode;while(p){var t=p.tagName;if(t==='P'||t==='LI'||t==='H2'||t==='H3'||t==='H4'||t==='TD'||t==='TH'||t==='DT'||t==='DD'||(t==='DIV'&&p.classList&&(p.classList.contains('verse')||p.classList.contains('para'))))return p;p=p.parentNode}return null}function scopeFromPointEl(startEl){if(startEl&&startEl.closest){var q0=startEl.closest('p,li,h2,h3,h4,td,th,dt,dd,div.para,div.verse');if(q0)return q0}var el=startEl;while(el){var t=el.tagName;if(t==='P'||t==='LI'||t==='H2'||t==='H3'||t==='H4'||t==='TD'||t==='TH'||t==='DT'||t==='DD'||(t==='DIV'&&el.classList&&(el.classList.contains('verse')||el.classList.contains('para'))))return el;el=el.parentNode}return null}function tightBlockFromHit(hit){var el=hit&&hit.nodeType===1?hit:(hit&&hit.parentElement);if(!el)return null;var q=el.closest('p,li,h2,h3,h4,td,th,dt,dd,div.para,div.verse');if(q)return q;var inner=null;if(el.closest)inner=el.closest('.epub-chapter');if(!inner&&el.closest)inner=el.closest('section.chapter-wrap');var innerP=inner?el.closest('p'):null;if(innerP&&inner&&inner.contains(innerP))return innerP;if(!inner)return scopeFromPointEl(el)||paraFromNode(el);var n=el;while(n&&n!==inner&&n!==document.body){if(n.parentElement===inner)return n;n=n.parentElement}return scopeFromPointEl(el)||paraFromNode(el)}function sentenceRanges(F){if(!F)return[{s:0,e:0}];var para=/\\r?\\n\\s*\\r?\\n/g,R=[],push=function(seg,base){if(!seg)return;var n=seg.length,cuts=[],i,j,k;function sk(p){while(p<n&&/\\s/.test(seg.charAt(p)))p++;return p;}function inQ(ix){var d=false,g=false;for(k=0;k<ix;k++){var c=seg.charCodeAt(k);if(c===34)d=!d;else if(c===0x201C)d=true;else if(c===0x201D)d=false;else if(c===0xAB)g=true;else if(c===0xBB)g=false;}return d||g;}function isCl(c){return c===34||c===39||c===0x201D||c===0x2019||c===0xBB||c===41;}function isLd(c){return c===0x2014||c===0x2013||c===0xAB||c===34||c===0x201C;}function intro(p){p=sk(p);return p<n&&isLd(seg.charCodeAt(p));}function cDash(ix){j=sk(ix+1);if(j>=n)return false;var c=seg.charCodeAt(j);if(c!==0x2014&&c!==0x2013)return false;j=sk(j+1);if(j>=n)return false;c=seg.charCodeAt(j);if(c===0xAB||c===34||c===0x201C)return true;var ch=seg.charAt(j);return ch===ch.toUpperCase()&&ch!==ch.toLowerCase();}for(i=0;i<n;i++){var ch=seg.charAt(i);if(ch==='.'||ch==='!'||ch==='?'||ch==='…'){j=i+1;if(ch==='.'){while(j<n&&seg.charAt(j)==='.')j++;}var hadC=false;while(j<n&&isCl(seg.charCodeAt(j))){hadC=true;j++;}if(inQ(i)&&!hadC&&ch==='.'){if(j>=n||!/\\s/.test(seg.charAt(j))){i++;continue;}var k=j;while(k<n&&/\\s/.test(seg.charAt(k)))k++;if(k<n){var nx=seg.charAt(k);if(nx===nx.toLowerCase()&&nx!==nx.toUpperCase()){i++;continue;}}}if(j<n&&!/\\s/.test(seg.charAt(j)))continue;while(j<n&&/\\s/.test(seg.charAt(j)))j++;cuts.push(j);i=j-1;}}for(i=0;i<n;i++){if(seg.charAt(i)!==':')continue;if(intro(i+1)){j=sk(i+1);cuts.push(j);}}for(i=0;i<n;i++){if(seg.charAt(i)!==',')continue;j=sk(i+1);if(j>=n)continue;if(seg.charCodeAt(j)===34||seg.charCodeAt(j)===0x201C||seg.charCodeAt(j)===0xAB){cuts.push(j);continue;}if(cDash(i)){cuts.push(sk(i+1));}}cuts.sort(function(a,b){return a-b});var last=0;for(i=0;i<cuts.length;i++){j=cuts[i];if(j>last){R.push({s:base+last,e:base+j});last=j;}}if(last<n)R.push({s:base+last,e:base+n})};var ps=0,pm;while((pm=para.exec(F))!==null){push(F.substring(ps,pm.index),ps);ps=pm.index+pm[0].length}push(F.substring(ps),ps);if(!R.length)R.push({s:0,e:F.length});return R}function clipSpanNoParagraphGap(full,sp){var a=sp.start,e=sp.end;if(a<0)a=0;if(e>full.length)e=full.length;if(a>=e)return sp;var sub=full.substring(a,e),g=/\\r?\\n\\s*\\r?\\n/.exec(sub);if(g)e=a+g.index;return{start:a,end:e}}function sentenceSpan(full,st,en){if(!full)return{start:0,end:0};st=Math.max(0,Math.min(st,full.length));en=Math.max(0,Math.min(en,full.length));if(en<st){var T=st;st=en;en=T}var rng=sentenceRanges(full),mid=((st+en)/2)|0,k,r;if(rng.length===0)return{start:0,end:full.length};for(k=0;k<rng.length;k++){r=rng[k];if(mid>=r.s&&mid<r.e)return clipSpanNoParagraphGap(full,{start:r.s,end:r.e})}for(k=0;k<rng.length;k++){r=rng[k];if(st<r.e&&en>r.s)return clipSpanNoParagraphGap(full,{start:r.s,end:r.e})}r=rng[0];return clipSpanNoParagraphGap(full,{start:r.s,end:r.e})}function textNodesUnder(el){var a=[],w=document.createTreeWalker(el,NodeFilter.SHOW_TEXT,null,false),n;while(n=w.nextNode())a.push(n);return a}function fullFromNodes(scope){var nodes=textNodesUnder(scope),s='',i;for(i=0;i<nodes.length;i++)s+=nodes[i].textContent;return s.replace(/\\r/g,'')}function caretOffsetInScope(scope,caret){if(!caret||!scope.contains(caret.startContainer))return 0;var sc=caret.startContainer,so=caret.startOffset;if(sc.nodeType===3){var nodes=textNodesUnder(scope),acc=0,i;for(i=0;i<nodes.length;i++){if(nodes[i]===sc)return acc+Math.min(so,nodes[i].textContent.length);acc+=nodes[i].textContent.length}}return (fullFromNodes(scope).length/2)|0}function setRangeFromOffsets(scope,st,en){st=Math.max(0,st|0);en=Math.max(st,en|0);var nodes=textNodesUnder(scope),i,L,acc=0,sn=null,so=0,enN=null,eo=0;for(i=0;i<nodes.length;i++){L=nodes[i].textContent.length;if(sn===null&&acc+L>=st){sn=nodes[i];so=st-acc}if(acc+L>=en){enN=nodes[i];eo=en-acc;break}acc+=L}if(!sn)return null;if(!enN){enN=sn;eo=sn.textContent.length}var r=document.createRange();r.setStart(sn,Math.max(0,Math.min(so,sn.textContent.length)));r.setEnd(enN,Math.max(0,Math.min(eo,enN.textContent.length)));return r}function clearReaderHl(){document.querySelectorAll('mark.reader-sentence-hl').forEach(function(m){var p=m.parentNode;if(!p)return;while(m.firstChild)p.insertBefore(m.firstChild,m);p.removeChild(m)})}function applyReaderHl(scope,st,en){clearReaderHl();var r=setRangeFromOffsets(scope,st,en);if(!r||r.collapsed)return;try{var mk=document.createElement('mark');mk.className='reader-sentence-hl';r.surroundContents(mk)}catch(e1){try{var mk2=document.createElement('mark');mk2.className='reader-sentence-hl';var f=r.extractContents();mk2.appendChild(f);r.insertNode(mk2)}catch(e2){}}}var lpT=null,lpX=0,lpY=0,lpArm=false;function clearLp(){if(lpT){clearTimeout(lpT);lpT=null}lpArm=false}function armLp(x,y){clearLp();lpX=x;lpY=y;lpArm=true;lpT=setTimeout(runLp,620)}function moveLp(x,y){if(!lpArm)return;if(Math.abs(x-lpX)>14||Math.abs(y-lpY)>14)clearLp()}function endLp(){clearLp()}function runLp(){lpT=null;if(!lpArm)return;lpArm=false;var hit=document.elementFromPoint(lpX,lpY);if(!hit)return;var scope=tightBlockFromHit(hit);if(!scope)return;var full=fullFromNodes(scope);var caret=document.caretRangeFromPoint?document.caretRangeFromPoint(lpX,lpY):null;var off=(caret&&scope.contains(caret.startContainer))?caretOffsetInScope(scope,caret):(full.length/2|0);var sp=sentenceSpan(full,off,off);var rng=sentenceRanges(full),mid=((sp.start+sp.end)/2)|0,si=-1,kj,r;for(kj=0;kj<rng.length;kj++){r=rng[kj];if(mid>=r.s&&mid<r.e){si=kj;break;}}var bo=-1;try{if(scope&&scope.getAttribute)bo=parseInt(scope.getAttribute('data-bo')||'-1',10);}catch(eBo){}var txt=full.substring(sp.start,sp.end).trim();if(!txt)return;try{applyReaderHl(scope,sp.start,sp.end)}catch(e){}try{window.getSelection().removeAllRanges()}catch(e){}setTimeout(function(){try{window.getSelection().removeAllRanges()}catch(e){}},0);go('translate?text='+encodeURIComponent(txt.substring(0,8000))+(si>=0?'&si='+si:'')+(bo>=0?'&bo='+bo:''))}document.addEventListener('dblclick',function(e){e.preventDefault();go('toggle-panels')},true);document.addEventListener('selectstart',function(e){e.preventDefault()},true);if(window.PointerEvent){document.addEventListener('pointerdown',function(e){if(e.isPrimary===false)return;armLp(e.clientX,e.clientY)},{passive:true});document.addEventListener('pointermove',function(e){if(e.isPrimary===false)return;moveLp(e.clientX,e.clientY)},{passive:true});document.addEventListener('pointerup',endLp,{passive:true});document.addEventListener('pointercancel',endLp,{passive:true})}else{document.addEventListener('touchstart',function(e){var x=e.touches&&e.touches[0]?e.touches[0].clientX:(e.changedTouches&&e.changedTouches[0]?e.changedTouches[0].clientX:0);var y=e.touches&&e.touches[0]?e.touches[0].clientY:(e.changedTouches&&e.changedTouches[0]?e.changedTouches[0].clientY:0);armLp(x,y)},{passive:true});document.addEventListener('touchmove',function(e){var t=(e.touches&&e.touches[0])||(e.changedTouches&&e.changedTouches[0]);if(!t)return;moveLp(t.clientX,t.clientY)},{passive:true});document.addEventListener('touchend',endLp,{passive:true});document.addEventListener('touchcancel',endLp,{passive:true})};window.__readerClearSentenceHl=clearReaderHl})();</script>";

  private static string NormalizeTextAlignCss(string? textAlignCss)
  {
    var t = (textAlignCss ?? "justify").Trim().ToLowerInvariant();
    return t is "left" or "center" or "right" or "justify" ? t : "justify";
  }

  /// <summary>Android WebView требует валидный BaseUrl (часто file:///android_asset/), иначе HTML не рисуется.</summary>
  public static string GetWebViewBaseUrl(bool useAndroidAssetScheme) =>
      useAndroidAssetScheme ? "file:///android_asset/" : AppNavBaseUrl;

  /// <param name="onlyChapterIndex">Если задан — в WebView только эта глава (одна section); иначе вся книга одним документом.</param>
  /// <param name="epubChapterHtmlBodies">Фрагменты HTML по главам EPUB (уже безопасные теги); иначе текст из <paramref name="fullText"/> по смещениям.</param>
  /// <param name="enableHorizontalSwipeNavigation">Горизонтальный режим: свайп влево/вправо — следующая/предыдущая страница (без нативных жестов).</param>
  /// <param name="horizontalColumnPaged">Один документ: CSS multi-column (колонка = viewport px) + горизонтальный скролл.</param>
  public static string BuildFullDocumentHtml(
      string fullText,
      IReadOnlyList<(string Title, long Start, long End)> chapters,
      IReadOnlyList<XElement>? fb2Sections,
      double fontSizePx,
      string textAlignCss,
      string foregroundHex,
      string backgroundHex,
      double paddingPx,
      int? onlyChapterIndex = null,
      IReadOnlyList<string>? epubChapterHtmlBodies = null,
      bool enableHorizontalSwipeNavigation = false,
      bool horizontalColumnPaged = false)
  {
    if (horizontalColumnPaged && onlyChapterIndex.HasValue)
      onlyChapterIndex = null;

    string ta = NormalizeTextAlignCss(textAlignCss);
    bool isJustify = string.Equals(ta, "justify", StringComparison.OrdinalIgnoreCase);
    string fs = fontSizePx.ToString(CultureInfo.InvariantCulture);
    string fsH2 = (fontSizePx + 4).ToString(CultureInfo.InvariantCulture);
    string fsH3 = (fontSizePx + 2).ToString(CultureInfo.InvariantCulture);
    string pad = paddingPx.ToString(CultureInfo.InvariantCulture);
    var sb = new StringBuilder();
    sb.Append("<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\"/>");
    sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\"/>");
    sb.Append("<style>");
    if (!horizontalColumnPaged)
    {
      // Вертикальное чтение: прокрутка по главе; overflow:hidden ломает scrollY/WebView на части платформ.
      sb.Append("html,body{margin:0;padding:0;min-height:100%;overflow-x:hidden;overflow-y:auto;-webkit-overflow-scrolling:touch;-webkit-user-select:none;user-select:none;-webkit-touch-callout:none;background:");
      sb.Append(backgroundHex);
      sb.Append(";color:");
      sb.Append(foregroundHex);
      sb.Append(";}body{font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;font-size:");
      sb.Append(fs);
      sb.Append("px;line-height:1.6;-webkit-user-select:none;user-select:none;-webkit-touch-callout:none;padding:0;padding-left:calc(");
      sb.Append(pad);
      sb.Append("px + env(safe-area-inset-left,0px));padding-right:calc(");
      sb.Append(pad);
      sb.Append("px + env(safe-area-inset-right,0px));padding-top:calc(");
      sb.Append(pad);
      sb.Append("px + max(env(safe-area-inset-top,0px),0px));padding-bottom:calc(");
      sb.Append(pad);
      sb.Append("px + max(28px,env(safe-area-inset-bottom,0px)));text-align:");
      sb.Append(ta);
      if (isJustify)
        sb.Append(";text-justify:inter-word;-webkit-text-align-last:auto;text-align-last:auto");
      sb.Append(";hyphens:auto;-webkit-hyphens:auto;word-wrap:break-word;overflow-wrap:break-word;-webkit-tap-highlight-color:transparent;}");
    }
    else
    {
      // Во встроенном WebView height:100% на body часто даёт 0 до измерения — multicol получает нулевую высоту → «слипшийся» текст полосками.
      sb.Append("html{height:100%;}body{margin:0;padding:0;width:100%;min-width:100%;box-sizing:border-box;overflow:hidden;background:");
      sb.Append(backgroundHex);
      sb.Append(";color:");
      sb.Append(foregroundHex);
      sb.Append(";min-height:100vh;min-height:100dvh;height:100%;height:-webkit-fill-available;");
      sb.Append(";}body{font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;font-size:");
      sb.Append(fs);
      sb.Append("px;line-height:1.6;-webkit-user-select:none;user-select:none;-webkit-touch-callout:none;padding:0;text-align:");
      sb.Append(ta);
      if (isJustify)
        sb.Append(";text-justify:inter-word;-webkit-text-align-last:auto;text-align-last:auto");
      sb.Append(";hyphens:auto;-webkit-hyphens:auto;word-wrap:break-word;overflow-wrap:break-word;-webkit-tap-highlight-color:transparent;}");
      sb.Append("#hscroll{--reader-side-pad:");
      sb.Append(pad);
      // Фиксируем область на весь видимый фрейм WebView — не зависим от цепочки % от родителя MAUI.
      sb.Append("px;direction:ltr;margin:0;border:0;position:fixed;top:0;left:0;right:0;bottom:0;width:100%;height:auto;min-height:100vh;min-height:100dvh;min-height:-webkit-fill-available;");
      sb.Append("visibility:hidden;overflow-x:hidden;overflow-y:hidden;box-sizing:border-box;");
      sb.Append("padding:0;");
      sb.Append("scrollbar-width:none;-ms-overflow-style:none;touch-action:none;scroll-behavior:auto;}");
      sb.Append("#hscroll::-webkit-scrollbar{display:none}");
      // width будет выставлен JS в sync() как N×sp (кратное viewport) — обход max-content бага WebKit
      sb.Append("#colflow{margin:0;border:0;height:100%;min-height:100%;max-height:none;");
      sb.Append("direction:ltr;position:relative;z-index:2;padding-left:0;padding-right:0;");
      sb.Append("padding-top:calc(var(--reader-side-pad) + max(env(safe-area-inset-top,0px),0px));");
      sb.Append("padding-bottom:calc(var(--reader-side-pad) + max(env(safe-area-inset-bottom,0px),0px));");
      sb.Append("column-fill:auto;box-sizing:border-box;overflow-x:hidden;overflow-y:hidden;");
      sb.Append("column-width:1px;-webkit-column-width:1px;column-gap:0;-webkit-column-gap:0;}");
      // sentinel: нулевой размер, но участвует в потоке колонок
      sb.Append("#hrViewport{display:block;width:0;height:0;overflow:hidden;line-height:0;font-size:0;pointer-events:none;}");
      // Явный разрыв колонки (WebView часто игнорирует break-before у section); + дублирующие правила для section/h2.
      sb.Append(".chapter-col-break{display:block;height:1px;line-height:0;margin:0;padding:0;border:0;width:100%;overflow:hidden;");
      sb.Append("break-before:column;break-after:avoid;-webkit-column-break-before:always;page-break-before:always;}");
      sb.Append(".chapter-col-break+section.chapter-wrap{break-before:column!important;-webkit-column-break-before:always!important;}");
    }
    sb.Append("h2.chapter{font-size:");
    sb.Append(fsH2);
    sb.Append("px;margin:1.4em 0 0.6em;font-weight:700;text-align:left;}");
    sb.Append("h3.subtitle{font-size:");
    sb.Append(fsH3);
    sb.Append("px;margin:0.6em 0;font-weight:700;text-align:left;}");
    sb.Append("p.para{display:block;margin:0 0 1.25em 0;padding:0;white-space:pre-line;text-align:");
    sb.Append(ta);
    if (isJustify)
      sb.Append(";text-justify:inter-word");
    sb.Append(";text-indent:0;}div.vspace{height:0.75em;}div.verse{text-align:center;font-style:italic;margin:0 0 0.5em;}");
    sb.Append("cite.fb2{margin:0.5em 0;display:block;}strong{font-weight:inherit;}em{font-style:italic;}s{text-decoration:line-through;}");
    sb.Append(".epub-chapter{overflow-wrap:anywhere;-webkit-hyphens:manual;hyphens:manual;}");
    sb.Append(".epub-chapter p.para{margin-bottom:0.62em;line-height:1.5;orphans:3;widows:3;box-decoration-break:clone;-webkit-box-decoration-break:clone;}");
    sb.Append(".epub-chapter>br{display:none;}");
    sb.Append(".epub-chapter img{max-width:100%;height:auto;}");
    sb.Append("mark.reader-sentence-hl{background:rgba(255,212,90,.38);color:inherit;border-radius:3px;padding:0 .04em;-webkit-user-select:none;user-select:none;-webkit-touch-callout:none;");
    sb.Append("box-decoration-break:clone;-webkit-box-decoration-break:clone;}");
    sb.Append("</style></head><body>");
    AppendVerticalReaderScrollGlobals(sb, fullText, chapters, onlyChapterIndex, horizontalColumnPaged);

    sb.Append("<script>(function(){var B='");
    sb.Append(AppNavBaseUrl);
    sb.Append(ReaderSentenceLongPressScriptTail);
    if (enableHorizontalSwipeNavigation)
    {
      if (horizontalColumnPaged)
      {
        sb.Append("<script>(function(){var B='");
        sb.Append(AppNavBaseUrl);
        sb.Append("';function go(p){window.location.href=B+p;}var sx=0,sy=0,st=0,armed=false,moved=false;var h=document.getElementById('hscroll');function onStart(e){var t=e.changedTouches&&e.changedTouches[0]?e.changedTouches[0]:e;sx=t.clientX;sy=t.clientY;st=Date.now();armed=true;moved=false;}function onMove(e){if(!armed)return;var t=e.changedTouches&&e.changedTouches[0]?e.changedTouches[0]:e;var dx=t.clientX-sx,dy=t.clientY-sy;if(Math.abs(dx)>14||Math.abs(dy)>14)moved=true;if(Math.abs(dx)>Math.abs(dy)&&Math.abs(dx)>8)e.preventDefault();}function onEnd(e){if(!armed)return;armed=false;var t=e.changedTouches&&e.changedTouches[0]?e.changedTouches[0]:e;var dx=t.clientX-sx,dy=t.clientY-sy;var dt=Date.now()-st;var w=window.innerWidth;if(dt<=520&&Math.abs(dx)>=48&&Math.abs(dx)>=Math.abs(dy)*1.6){if(dx<-48)go('pagenext');else if(dx>48)go('pageprev');return;}if(dt<340&&Math.abs(dx)<16&&Math.abs(dy)<18){var x=t.clientX;if(x<w*0.24)go('pageprev');else if(x>w*0.76)go('pagenext');}}document.addEventListener('touchstart',onStart,{passive:true});document.addEventListener('touchmove',onMove,{passive:false});document.addEventListener('touchend',onEnd,{passive:true});document.addEventListener('click',function(e){if(e.defaultPrevented)return;var t=e.target;if(t&&t.closest&&t.closest('a'))return;var w=window.innerWidth,x=e.clientX;if(x<w*0.24)go('pageprev');else if(x>w*0.76)go('pagenext');});})();</script>");
      }
      else
      {
        sb.Append("<script>(function(){var B='");
        sb.Append(AppNavBaseUrl);
        sb.Append("';function go(p){window.location.href=B+p;}var sx=0,sy=0,st=0,active=false;document.addEventListener('touchstart',function(e){var t=e.changedTouches&&e.changedTouches[0]?e.changedTouches[0]:e;sx=t.clientX;sy=t.clientY;st=Date.now();active=true;},{passive:true});document.addEventListener('touchend',function(e){if(!active)return;active=false;var t=e.changedTouches&&e.changedTouches[0]?e.changedTouches[0]:e;var dx=t.clientX-sx,dy=t.clientY-sy;var dt=Date.now()-st;var w=window.innerWidth;if(dt<=520&&Math.abs(dx)>=56&&Math.abs(dx)>=Math.abs(dy)*1.85){if(dx<-56)go('pagenext');else if(dx>56)go('pageprev');return;}if(dt<340&&Math.abs(dx)<16&&Math.abs(dy)<18){var x=t.clientX;if(x<w*0.24)go('pageprev');else if(x>w*0.76)go('pagenext');}},{passive:true});document.addEventListener('click',function(e){if(e.defaultPrevented)return;var t=e.target;if(t&&t.closest&&t.closest('a'))return;var w=window.innerWidth,x=e.clientX;if(x<w*0.24)go('pageprev');else if(x>w*0.76)go('pagenext');});})();</script>");
      }
    }

    if (horizontalColumnPaged)
    {
      // Длина текста — для оценки числа страниц в JS (без бинарного поиска)
      sb.Append("<script>window.__hrTextLen=");
      sb.Append(fullText.Length.ToString(CultureInfo.InvariantCulture));
      sb.Append(";window.__hrFontSizePx=");
      sb.Append(fontSizePx.ToString(CultureInfo.InvariantCulture));
      sb.Append(";window.__hrPaddingPx=");
      sb.Append(paddingPx.ToString(CultureInfo.InvariantCulture));
      sb.Append(";</script>");
      sb.Append("<div id=\"hscroll\"><div id=\"colflow\">");
    }

    bool useFb2 = fb2Sections != null && fb2Sections.Count == chapters.Count && chapters.Count > 0;
    bool useEpub = epubChapterHtmlBodies != null && epubChapterHtmlBodies.Count == chapters.Count && chapters.Count > 0;

    if (onlyChapterIndex.HasValue && chapters.Count > 0)
    {
      int ci = Math.Clamp(onlyChapterIndex.Value, 0, chapters.Count - 1);
      if (useFb2 && ci < fb2Sections!.Count)
      {
        var rawTitle = chapters[ci].Title?.Trim() ?? "";
        var title = string.IsNullOrWhiteSpace(rawTitle)
            ? DefaultChapterTitle(ci + 1)
            : rawTitle;
        sb.Append("<section class=\"chapter-wrap\" id=\"ch-0\"><h2 class=\"chapter\">");
        sb.Append(WebUtility.HtmlEncode(title));
        sb.Append("</h2>");
        var (_, chStart, chEnd) = chapters[ci];
        var fb2ParaOffsets = Fb2BookTextExtractor.TryBuildFb2ParagraphBookOffsets(fb2Sections[ci], fullText, chStart, chEnd);
        AppendFb2SectionHtml(sb, fb2Sections[ci], fb2ParaOffsets);
        sb.Append("</section>");
      }
      else if (useEpub && ci < epubChapterHtmlBodies!.Count && !string.IsNullOrWhiteSpace(epubChapterHtmlBodies[ci]))
      {
        var (title, _, _) = chapters[ci];
        var titleTrim = title?.Trim() ?? "";
        var t = string.IsNullOrWhiteSpace(titleTrim) ? DefaultChapterTitle(ci + 1) : titleTrim;
        sb.Append("<section class=\"chapter-wrap\" id=\"ch-0\"><h2 class=\"chapter\">");
        sb.Append(WebUtility.HtmlEncode(t));
        sb.Append("</h2><div class=\"epub-chapter\">");
        sb.Append(epubChapterHtmlBodies[ci]);
        sb.Append("</div></section>");
      }
      else
      {
        var (title, st, en) = chapters[ci];
        var titleTrim = title?.Trim() ?? "";
        var t = string.IsNullOrWhiteSpace(titleTrim) ? DefaultChapterTitle(ci + 1) : titleTrim;
        int len = (int)Math.Max(0, en - st);
        string slice = len > 0 && st + len <= fullText.Length
            ? fullText.Substring((int)st, len)
            : "";
        sb.Append("<section class=\"chapter-wrap\" id=\"ch-0\"><h2 class=\"chapter\">");
        sb.Append(WebUtility.HtmlEncode(t));
        sb.Append("</h2>");
        AppendPlainBlocksHtml(sb, slice, st);
        sb.Append("</section>");
      }
      AppendBookAnchorNavigationScript(sb);
      sb.Append("</body></html>");
      return sb.ToString();
    }

    if (useFb2)
    {
      for (int ci = 0; ci < fb2Sections!.Count; ci++)
      {
        if (horizontalColumnPaged && ci > 0)
          sb.Append("<div class=\"chapter-col-break\" aria-hidden=\"true\"></div>");
        var rawTitle = chapters[ci].Title?.Trim() ?? "";
        var title = string.IsNullOrWhiteSpace(rawTitle)
            ? DefaultChapterTitle(ci + 1)
            : rawTitle;
        sb.Append("<section class=\"chapter-wrap\" id=\"ch-");
        sb.Append(ci);
        sb.Append("\"><h2 class=\"chapter\">");
        sb.Append(WebUtility.HtmlEncode(title));
        sb.Append("</h2>");
        var (_, chStart, chEnd) = chapters[ci];
        var fb2ParaOffsets = Fb2BookTextExtractor.TryBuildFb2ParagraphBookOffsets(fb2Sections[ci], fullText, chStart, chEnd);
        AppendFb2SectionHtml(sb, fb2Sections[ci], fb2ParaOffsets);
        sb.Append("</section>");
      }
    }
    else if (useEpub)
    {
      for (int ci = 0; ci < chapters.Count; ci++)
      {
        if (horizontalColumnPaged && ci > 0)
          sb.Append("<div class=\"chapter-col-break\" aria-hidden=\"true\"></div>");
        var (title, _, _) = chapters[ci];
        var titleTrim = title?.Trim() ?? "";
        var t = string.IsNullOrWhiteSpace(titleTrim) ? DefaultChapterTitle(ci + 1) : titleTrim;
        sb.Append("<section class=\"chapter-wrap\" id=\"ch-");
        sb.Append(ci);
        sb.Append("\"><h2 class=\"chapter\">");
        sb.Append(WebUtility.HtmlEncode(t));
        sb.Append("</h2><div class=\"epub-chapter\">");
        if (ci < epubChapterHtmlBodies!.Count && !string.IsNullOrWhiteSpace(epubChapterHtmlBodies[ci]))
          sb.Append(epubChapterHtmlBodies[ci]);
        sb.Append("</div></section>");
      }
    }
    else if (chapters.Count > 0)
    {
      for (int ci = 0; ci < chapters.Count; ci++)
      {
        if (horizontalColumnPaged && ci > 0)
          sb.Append("<div class=\"chapter-col-break\" aria-hidden=\"true\"></div>");
        var (title, st, en) = chapters[ci];
        var titleTrim = title?.Trim() ?? "";
        var t = string.IsNullOrWhiteSpace(titleTrim) ? DefaultChapterTitle(ci + 1) : titleTrim;
        int len = (int)Math.Max(0, en - st);
        string slice = len > 0 && st + len <= fullText.Length
            ? fullText.Substring((int)st, len)
            : "";
        sb.Append("<section class=\"chapter-wrap\" id=\"ch-");
        sb.Append(ci);
        sb.Append("\"><h2 class=\"chapter\">");
        sb.Append(WebUtility.HtmlEncode(t));
        sb.Append("</h2>");
        AppendPlainBlocksHtml(sb, slice, st);
        sb.Append("</section>");
      }
    }
    else
      AppendPlainBlocksHtml(sb, fullText, 0);

    if (horizontalColumnPaged)
    {
      sb.Append("<div id=\"hrViewport\"></div></div></div>");
      AppendHorizontalReaderLayoutScript(sb);
    }
    AppendBookAnchorNavigationScript(sb);
    sb.Append("</body></html>");
    return sb.ToString();
  }

  /// <summary>Границы текущего документа WebView (вся книга или одна глава) — для аппроксимации якоря по scroll.</summary>
  static void AppendVerticalReaderScrollGlobals(StringBuilder sb,
      string fullText,
      IReadOnlyList<(string Title, long Start, long End)> chapters,
      int? onlyChapterIndex,
      bool horizontalColumnPaged)
  {
    if (horizontalColumnPaged || string.IsNullOrEmpty(fullText))
      return;
    long cs = 0;
    long ce = Math.Max(1, fullText.Length);
    if (onlyChapterIndex.HasValue && chapters.Count > 0)
    {
      int cix = Math.Clamp(onlyChapterIndex.Value, 0, chapters.Count - 1);
      cs = chapters[cix].Start;
      ce = Math.Max(cs + 1, chapters[cix].End);
    }
    sb.Append("<script>window.__readerFullTextLen=");
    sb.Append(fullText.Length.ToString(CultureInfo.InvariantCulture));
    sb.Append(";window.__readerChapterStart=");
    sb.Append(cs.ToString(CultureInfo.InvariantCulture));
    sb.Append(";window.__readerChapterEnd=");
    sb.Append(ce.ToString(CultureInfo.InvariantCulture));
    sb.Append(";</script>");
  }

  static void AppendBookAnchorNavigationScript(StringBuilder sb)
  {
    sb.Append(
        "<script>(function(){function R(){return document.getElementById('hscroll');}"
        + "function offInPara(p,r){if(!p||!r)return 0;try{if(!p.contains(r.startContainer))return 0;var pre=document.createRange();pre.setStart(p,0);pre.setEnd(r.startContainer,r.startOffset);return(pre.toString()||'').length;}catch(e){return 0;}}"
        + "function paraFromPoint(x,y){var e=document.elementFromPoint(x,y);if(!e)return null;var q=e.closest&&e.closest('p.para');if(!q)return null;var bo=parseInt(q.getAttribute('data-bo')||'-1',10);if(bo>=0)return q;var a=[].slice.call(document.body.querySelectorAll('p.para[data-bo]'));if(!a.length)return null;var i,r,b=null,v=1e18;for(i=0;i<a.length;i++){r=a[i].getBoundingClientRect();if(!(r.width>1&&r.height>1))continue;var vy=r.top+r.height*.42;var dy=y>=r.top&&y<=r.bottom?Math.abs(vy-y):Math.min(Math.abs(y-r.top)+48,Math.abs(y-r.bottom)+48);var dx=x<r.left?r.left-x:x>r.right?x-r.right:0;var d=dx*dx+dy*dy;if(d<v){v=d;b=a[i];}}return b;}"
        + "window.__readerApproxBookOffset=function(){try{var cs=+window.__readerChapterStart;var ce=+window.__readerChapterEnd;if(!(ce>cs)){cs=0;ce=+window.__readerFullTextLen|0;}var sp=(ce-cs)|0,maxIn=Math.max(0,sp-1);if(!(sp>=1))return -1;var sy=+(window.scrollY||window.pageYOffset||(document.documentElement&&document.documentElement.scrollTop)||(document.body&&document.body.scrollTop)||0);var ih=Math.floor((window.visualViewport&&window.visualViewport.height)||window.innerHeight||400);var docH=Math.max(document.documentElement.scrollHeight||0,document.body.scrollHeight||0);var mx=Math.max(0,docH-ih);var r=(mx<=0)?0:Math.min(1,Math.max(0,sy/mx));var wi=Math.round(r*maxIn);if(wi<0)wi=0;if(wi>maxIn)wi=maxIn;return (cs+wi)|0;}catch(e){return-1;}}"
        + "function lineMidYForProbeInPara(p,yProbe){var r=document.createRange();try{r.selectNodeContents(p);var rects=[].slice.call(r.getClientRects()),i,q,bd=1e9,yy=NaN;"
        + "for(i=0;i<rects.length;i++){q=rects[i];if(q.width<2||q.height<2)continue;var ins=yProbe>=q.top&&yProbe<=q.bottom;var d=ins?0:Math.min(Math.abs(yProbe-q.top),Math.abs(yProbe-q.bottom));if(d<bd){bd=d;yy=q.top+q.height*0.5;}}"
        + "return yy}catch(e){}return NaN;}"
        + "function caretRangeForParaAtXY(pr,x,y){var rg=null;"
        + "try{if(document.caretRangeFromPoint)rg=document.caretRangeFromPoint(x,y)}catch(e){}"
        + "if(rg&&pr.contains(rg.startContainer))return rg;"
        + "var yM=lineMidYForProbeInPara(pr,y),yt=[y],j,i,y2,x2;"
        + "if(typeof yM==='number'&&isFinite(yM))yt.push(yM);yt.push(Math.max(1,y-1),y+1,Math.max(1,y-4),y+4,Math.max(1,y-10),y+10);"
        + "var xi=[x,Math.max(4,x-32),Math.min((window.innerWidth||400)-4,x+32)];"
        + "for(i=0;i<xi.length;i++){x2=xi[i];for(j=0;j<yt.length;j++){y2=Math.max(1,Math.min((window.innerHeight||900)-2,Math.floor(yt[j])));"
        + "try{if(document.caretRangeFromPoint){rg=document.caretRangeFromPoint(x2,y2);if(rg&&pr.contains(rg.startContainer))return rg}}catch(e2){}}}"
        + "return null;}"
        + "function findTopParaHit(){var h=R();if(!h)return null;try{h.style.visibility='visible'}catch(eH){};var rr=h.getBoundingClientRect();"
        + "if(!rr||rr.width<10)return null;var eps=2.75;var a=[].slice.call(h.querySelectorAll('p.para[data-bo]')),padHit=0;try{var cf=document.getElementById('colflow');if(cf)padHit=parseInt(getComputedStyle(cf).paddingLeft)||0;}catch(ep){};"
        + "var wv=Math.max(1,Math.floor(rr.width));var x0=rr.left+Math.min(padHit+6,Math.max(8,wv*.06));var i,br,best=null,bestTop=9e17,pr=null;"
        + "for(i=0;i<a.length;i++){pr=a[i];try{br=pr.getBoundingClientRect()}catch(z){continue;}"
        + "if(br.bottom<=rr.top+eps||br.top>=rr.bottom-eps||br.right<=rr.left+3||br.left>=rr.right-3)continue;if(!(br.width>3&&br.height>3))continue;"
        + "if(br.top<bestTop){bestTop=br.top;best=pr;} }pr=best;if(!pr)return null;var brPr;try{brPr=pr.getBoundingClientRect()}catch(z2){return null;}"
        + "var xx=Math.max(rr.left+6,Math.min(rr.right-8,Math.max(brPr.left+Math.min(26,Math.max(brPr.width*.08,8)),Math.min(brPr.right-8,x0))));"
        + "var yy=Math.min(brPr.bottom-4,Math.max(brPr.top+5,Math.min(brPr.bottom-12,brPr.top+Math.max(14,Math.min(brPr.height*0.34,Math.max(brPr.height*0.13,22)))))));"
        + "return{p:pr,x:xx,y:yy}}"
        + "function findVpCenterParaHit(){var h=R(),ih,iy=0;if(h)try{h.style.visibility='visible'}catch(eHc){};if(window.visualViewport){ih=Math.floor(window.visualViewport.height||0);iy=Math.floor(window.visualViewport.offsetTop||0);}else ih=0;if(ih<=0)ih=Math.floor(window.innerHeight||400);var x=Math.max(4,Math.floor((window.innerWidth||400)*0.5));var yBase=iy+Math.floor(ih*0.42);var step=[0,12,24,40,60,88,120,160,-14,-30,-52,-80];var si,pr=null,yy,xx,xi,xc;if(h){var rr=h.getBoundingClientRect();var wv=Math.max(1,Math.floor(rr.width));xc=[18,52,Math.min(96,Math.floor(wv*.12)),Math.floor(wv*.24),Math.floor(wv*.4)];"
        + "var yCol=rr.top+Math.max(8,Math.floor((rr.bottom-rr.top)*0.42));for(xi=0;xi<xc.length;xi++){xx=Math.max(rr.left+8,Math.min(rr.right-12,rr.left+xc[xi]));"
        + "for(si=0;si<step.length;si++){yy=Math.min(rr.bottom-4,Math.max(rr.top+4,yCol+step[si]));pr=paraFromPoint(xx,yy);if(pr)return{p:pr,x:xx,y:yy};}}}"
        + "for(si=0;si<step.length;si++){yy=Math.min(iy+ih-2,Math.max(iy+2,yBase+step[si]));pr=paraFromPoint(x,yy);if(pr)return{p:pr,x:x,y:yy};}return null;}"
        + "window.__readerGetViewportCenterBookOffset=function(){var hit=findVpCenterParaHit();if(!hit)return -1;var pr=hit.p;var b=parseInt(pr.getAttribute('data-bo')||'-1',10);if(!(b>=0))return -1;var rg=caretRangeForParaAtXY(pr,hit.x,hit.y);if(!rg||!pr.contains(rg.startContainer))return b;return b+offInPara(pr,rg);};"
        + "window.__readerGetTopBookOffset=function(){var hit=findTopParaHit();if(!hit)return -1;var pr=hit.p;"
        + "var b=parseInt(pr.getAttribute('data-bo')||'-1',10);if(!(b>=0))return -1;"
        + "var rg=caretRangeForParaAtXY(pr,hit.x,hit.y);if(!rg||!pr.contains(rg.startContainer))return b;"
        + "return b+offInPara(pr,rg);};"
        + "function findHorizontalLeadBookOffset(){var h=R();if(!h)return -1;"
        + "try{h.style.visibility='visible'}catch(z0){};var pad=0;"
        + "try{var cf=document.getElementById('colflow');if(cf)pad=parseInt(getComputedStyle(cf).paddingLeft)||0;}catch(zp){};"
        + "var rr=h.getBoundingClientRect(),wv=Math.max(1,Math.floor(rr.width));"
        + "var x0=Math.max(rr.left+2,Math.min(rr.right-10,rr.left+pad+Math.min(10,Math.floor(wv*.04))));"
        + "var st=[8,22,38,54,74,94,126,164,206,268],si,y,rg,pr,b,jj,gY=1e12,gX=1e12,best=-1,dx;"
        + "function tryHL(x,y){rg=null;"
        + "try{if(document.caretRangeFromPoint)rg=document.caretRangeFromPoint(Math.round(x),Math.round(y));}catch(ea){};"
        + "if(!rg||!rg.startContainer)return false;"
        + "pr=rg.startContainer.nodeType===3?rg.startContainer.parentElement:rg.startContainer;"
        + "while(pr&&!pr.closest)pr=pr.parentElement;pr=pr&&pr.closest?pr.closest('p.para'):null;"
        + "if(!pr)return false;b=parseInt(pr.getAttribute('data-bo')||'-1',10);if(!(b>=0))return false;"
        + "if(!pr.contains(rg.startContainer))return false;jj=b+offInPara(pr,rg);"
        + "var ry,rx;try{var brg=rg.getBoundingClientRect();rx=brg.left;ry=brg.top;}catch(eb){rx=x;ry=y;}"
        + "if(ry>rr.bottom+6||ry<rr.top-48)return false;"
        + "if(ry<gY-2||(ry<gY+3&&rx<=gX)){gY=ry;gX=rx;best=jj;}return true;}"
        + "for(si=0;si<st.length;si++){y=Math.min(rr.bottom-8,Math.max(rr.top+6,rr.top+st[si]));tryHL(x0,y);}"
        + "dx=[0,-12,14,-26,34];var dj,dj2,y2;"
        + "for(dj=0;dj<dx.length;dj++){var xi=Math.max(rr.left+2,Math.min(rr.right-12,x0+dx[dj]));"
        + "for(dj2=0;dj2<st.length;dj2++){y2=Math.min(rr.bottom-8,Math.max(rr.top+6,rr.top+st[dj2]));tryHL(xi,y2);}}"
        + "if(best>=0)return best;return(typeof window.__readerGetTopBookOffset==='function')?window.__readerGetTopBookOffset():-1;}"
        + "window.__readerGetHorizontalLeadBookOffset=function(){try{return findHorizontalLeadBookOffset();}catch(eh){return -1}};"
        + "window.__readerScrollToBookOffset=function(n){n=Math.max(0,Math.floor(n));var h=R(),root=h||document.body;"
        + "var a=[].slice.call(root.querySelectorAll('p.para[data-bo]'));if(!a.length)return;var best=null,i,b,b0,rel,jx=-1;"
        + "for(i=0;i<a.length;i++){b=parseInt(a[i].getAttribute('data-bo')||'-1',10);if(!(b>=0))continue;if(b<=n&&b>=jx){jx=b;best=a[i];}}if(!best)return;"
        + "b0=parseInt(best.getAttribute('data-bo')||'0',10)||0;rel=Math.max(0,n-b0);"
        + "if(h){try{h.style.visibility='visible'}catch(eV){};var pad=0;"
        + "try{var cf=document.getElementById('colflow');if(cf)pad=parseInt(getComputedStyle(cf).paddingLeft)||0;}catch(e){}"
        + "var hr=h.getBoundingClientRect(),sn=false;"
        + "if(typeof window.__hrSnapScrollLeftFromGeometry==='function'){try{var tw=document.createTreeWalker(best,NodeFilter.SHOW_TEXT,null,false),node,acc=0,L;"
        + "while(node=tw.nextNode()){L=(node.textContent||'').length;if(L<=0)continue;if(acc+L>=rel){try{var rg=document.createRange();rg.setStart(node,Math.max(0,Math.min(L,rel-acc)));rg.collapse(true);var br=rg.getBoundingClientRect();if(br&&(br.width>0||br.height>0||(br.left||br.right))){var gx=br.left-h.getBoundingClientRect().left+h.scrollLeft-pad;window.__hrSnapScrollLeftFromGeometry(gx);sn=true;}}catch(eF){}break;}acc+=L;}}catch(eT){}}"
        + "if(!sn){var er=best.getBoundingClientRect(),g=er.left-hr.left+h.scrollLeft-pad;"
        + "if(typeof window.__hrSnapScrollLeftFromGeometry==='function')window.__hrSnapScrollLeftFromGeometry(g);"
        + "else{var mxScr=Math.max(0,h.scrollWidth-h.clientWidth);h.scrollLeft=Math.min(mxScr,Math.max(0,Math.round(g)));}}"
        + "return;}"
        + "else{if(rel<=0){var tv=(window.scrollY||0)+best.getBoundingClientRect().top-8;try{window.scrollTo(0,Math.max(0,tv));}catch(e){}"
        + "try{best.scrollIntoView({block:'start',behavior:'auto'});}catch(e2){try{best.scrollIntoView(true);}catch(e3){}}}}}"
        + "if(rel<=0)return;"
        + "var tw=document.createTreeWalker(best,NodeFilter.SHOW_TEXT,null,false),node,acc=0,L,fine=false;"
        + "while(node=tw.nextNode()){L=(node.textContent||'').length;if(acc+L>=rel){"
        + "try{var rg=document.createRange();rg.setStart(node,Math.max(0,Math.min(L,rel-acc)));rg.collapse(true);"
        + "var br=rg.getBoundingClientRect();var vv=window.visualViewport,margin=Math.min(88,Math.max(44,Math.floor(((vv&&vv.height)||window.innerHeight||400)*0.11)));"
        + "window.scrollTo(0,window.scrollY+br.top-margin);fine=true;}catch(e4){fine=true;}break;}acc+=L;}"
        + "if(!fine&&!h&&rel>0){try{var r0=best.getBoundingClientRect();window.scrollTo(0,window.scrollY+r0.top-72);}catch(e5){}}}"
        + "};})();</script>");
  }

  static readonly Regex ParagraphBreakRegex = new(@"\r\n\s*\r\n|\n\s*\n", RegexOptions.Compiled);

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

  private static void AppendPlainBlocksHtml(StringBuilder sb, string slice, long sliceGlobalStart)
  {
    var ranges = BuildParagraphIndexRanges(slice);
    if (ranges.Count == 0)
    {
      sb.Append("<p class=\"para\" data-bo=\"");
      sb.Append(sliceGlobalStart.ToString(CultureInfo.InvariantCulture));
      sb.Append("\"> </p>");
      return;
    }

    bool any = false;
    foreach (var (a, b) in ranges)
    {
      if (a >= b)
        continue;
      int i = a;
      while (i < b && char.IsWhiteSpace(slice[i]))
        i++;
      int j = b - 1;
      while (j >= i && char.IsWhiteSpace(slice[j]))
        j--;
      if (i > j)
        continue;
      string t = slice.Substring(i, j - i + 1);
      long globalOff = sliceGlobalStart + i;
      sb.Append("<p class=\"para\" data-bo=\"");
      sb.Append(globalOff.ToString(CultureInfo.InvariantCulture));
      sb.Append("\">");
      sb.Append(WebUtility.HtmlEncode(t));
      sb.Append("</p>");
      any = true;
    }

    if (!any)
    {
      sb.Append("<p class=\"para\" data-bo=\"");
      sb.Append(sliceGlobalStart.ToString(CultureInfo.InvariantCulture));
      sb.Append("\"> </p>");
    }
  }

  /// <summary>
  /// Горизонтальная вёрстка:
  /// — #colflow.width = N×sp (кратное целому px ширины вьюпорта) — обходит WebKit-баг max-content + ограничение ~22 кол.
  /// — column-width = sp (ровно ширина вьюпорта, без дробей) → нет drift при пролистывании.
  /// — sentinel #hrViewport определяет реальный конец контента → width обрезается до usedCols×sp.
  /// </summary>
  private static void AppendHorizontalReaderLayoutScript(StringBuilder sb)
  {
    sb.Append("<script>(function(){");
    sb.Append("var hrPageIdx=0,hrTx=0,hrPageStep=0,hrViewW=0;");

    // ── вспомогательные ──────────────────────────────────────────────
    sb.Append("function fbW(){");
    sb.Append("var n=window.__readerNativeWidthPx;if(typeof n==='number'&&n>0&&isFinite(n))return Math.floor(n);");
    sb.Append("var a=[],v;");
    sb.Append("v=Math.floor(document.documentElement.clientWidth||0);if(v>0)a.push(v);");
    sb.Append("var vv=window.visualViewport;v=vv&&vv.width>0?Math.floor(vv.width):0;if(v>0)a.push(v);");
    sb.Append("v=Math.floor(window.innerWidth||0);if(v>0)a.push(v);");
    sb.Append("return a.length?Math.min.apply(null,a):1;}");

    sb.Append("function scrollport(){");
    sb.Append("var h=document.getElementById('hscroll');if(h){var hw=Math.floor(h.clientWidth||0);if(hw>0)return hw;}");
    sb.Append("var nw=typeof window.__readerNativeWidthPx==='number'&&isFinite(window.__readerNativeWidthPx)?Math.floor(window.__readerNativeWidthPx):0;");
    sb.Append("return nw>0?nw:fbW();}");

    sb.Append("function W(){return hrPageStep>0?hrPageStep:Math.max(1,scrollport());}");

    sb.Append("function maxTx(){");
    sb.Append("var h=document.getElementById('hscroll');if(!h)return 0;");
    sb.Append("void h.offsetWidth;");
    sb.Append("var vw=hrViewW>0?hrViewW:(h.clientWidth||1);");
    sb.Append("return Math.max(0,(h.scrollWidth||0)-vw);}");

    sb.Append("function maxIdx(){var st=W();if(st<=0)return 0;var mx=maxTx();if(mx<=0)return 0;");
    sb.Append("return Math.floor((mx+st-1e-6)/st);}");

    sb.Append("function applyTx(){");
    sb.Append("var h=document.getElementById('hscroll');if(!h)return;");
    sb.Append("var mx=maxTx(),st=W();");
    sb.Append("var x=Math.min(Math.max(0,hrTx),mx);");
    sb.Append("if(st>0){var i=Math.round(x/st);var nx=i*st;if(nx>mx)nx=Math.max(0,Math.floor(mx/st)*st);x=nx;}");
    sb.Append("h.scrollLeft=x;hrTx=h.scrollLeft;tryRevealH();}");

    // ── sync() ───────────────────────────────────────────────────────
    // sp   = viewport width = ширина одной «страницы» / шаг прокрутки
    // pad  = отступ слева и справа (из настроек, __hrPaddingPx)
    // colW = sp - 2*pad  — ширина текстового столбца (CSS column-width)
    // gap  = 2*pad       — зазор между столбцами (CSS column-gap); даёт pad справа + pad слева
    // Геометрия страницы N (scrollLeft = N*sp):
    //   left:  N*sp + pad  (начало текста)
    //   right: (N+1)*sp - pad  (конец текста)
    // allocW = estCols*sp, box-sizing:border-box → содержимое = allocW - 2*pad
    // trimW  = usedCols*sp (не меняется) → hrPageStep = sp (не меняется)
    sb.Append("function tryRevealH(){var h=document.getElementById('hscroll');if(!h)return;var cw=Math.floor(h.clientWidth||0),ch=Math.floor(h.clientHeight||0);if(cw>=32&&ch>=40)h.style.visibility='visible';}");
    sb.Append("function sync(){");
    sb.Append("var h=document.getElementById('hscroll'),c=document.getElementById('colflow');if(!h||!c)return;");
    sb.Append("void h.offsetWidth;");
    sb.Append("var sp=Math.floor(h.clientWidth||0);");
    sb.Append("var ch0=Math.floor(h.clientHeight||0);");
    sb.Append("hrViewW=sp;");
    sb.Append("if(sp<=0||ch0<40){setTimeout(function(){sync();requestAnimationFrame(reflowPagePos);},ch0<40?96:32);return;}");
    // Отступ: берём из настроек; colW — ширина текста в колонке
    sb.Append("var pad=Math.max(0,Math.round(window.__hrPaddingPx||0));");
    sb.Append("var colW=sp>2*pad+8?sp-2*pad:sp;"); // если pad слишком большой — fallback без отступов
    // Оценка числа страниц (используем colW — реальная ширина строки текста)
    sb.Append("var fs=window.__hrFontSizePx||16;var vH=h.clientHeight||sp;");
    sb.Append("var chPerCol=Math.max(50,Math.floor((colW/(fs*0.52))*(vH/(fs*1.6))));");
    sb.Append("var textLen=window.__hrTextLen||50000;");
    sb.Append("var estCols=Math.max(100,Math.ceil(textLen/chPerCol)*3+50);");
    // allocW кратно sp — общая ширина элемента (border-box, включает padding)
    sb.Append("var allocW=estCols*sp;");
    sb.Append("c.style.width=allocW+'px';");
    // Горизонтальный отступ: padding-left/right на #colflow (border-box — ширина не меняется)
    sb.Append("c.style.paddingLeft=pad+'px';c.style.paddingRight=pad+'px';");
    // column-width = colW, column-gap = 2*pad → текст занимает colW, зазор 2*pad между колонками
    sb.Append("c.style.columnWidth=colW+'px';c.style.webkitColumnWidth=colW+'px';");
    sb.Append("c.style.columnGap=(2*pad)+'px';c.style.webkitColumnGap=(2*pad)+'px';");
    sb.Append("void c.offsetWidth;void h.offsetWidth;");
    // Sentinel: найти реальный конец контента
    sb.Append("var sv=document.getElementById('hrViewport');");
    sb.Append("var usedCols=estCols;");
    sb.Append("if(sv){");
    sb.Append("var svL=sv.getBoundingClientRect().left-h.getBoundingClientRect().left+h.scrollLeft;");
    sb.Append("usedCols=Math.max(1,Math.ceil((svL+1)/sp));");
    sb.Append("var trimW=usedCols*sp;");
    sb.Append("if(trimW>0&&trimW!==allocW){c.style.width=trimW+'px';void c.offsetWidth;void h.offsetWidth;}}");
    sb.Append("var cOw=Math.round(c.offsetWidth||0);");
    sb.Append("hrPageStep=sp;");
    sb.Append("try{window.__hrLastSync={sp:sp,pad:pad,colW:colW,hrPageStep:hrPageStep,");
    sb.Append("hCw:Math.round(h.clientWidth||0),hSw:Math.round(h.scrollWidth||0),");
    sb.Append("cOw:cOw,usedCols:usedCols,allocW:allocW,");
    sb.Append("dpr:window.devicePixelRatio||1};}catch(e){}");
    sb.Append("var mx=maxTx();if(h.scrollLeft>mx)h.scrollLeft=mx;hrTx=h.scrollLeft;tryRevealH();}");

    // ── навигация ─────────────────────────────────────────────────────
    sb.Append("function reflowPagePos(){sync();var st=W(),mx=maxTx(),mi=maxIdx();");
    sb.Append("if(mi<hrPageIdx)hrPageIdx=mi;hrTx=Math.min(hrPageIdx*st,mx);applyTx();tryRevealH();}");

    sb.Append("function setPage(idx,smooth){");
    sb.Append("if(hrPageStep<=0)sync();"); // первый вызов до boot — инициализируем
    sb.Append("var st=W(),mx=maxTx(),mi=maxIdx();");
    sb.Append("var i=Math.max(0,Math.min(Math.round(idx),mi));hrPageIdx=i;hrTx=Math.min(i*st,mx);");
    sb.Append("var h=document.getElementById('hscroll');if(!h)return;");
    sb.Append("if(smooth){h.scrollTo({left:hrTx,behavior:'smooth'});setTimeout(applyTx,450);}else{applyTx();}}");

    sb.Append("function jumpCh(n){reflowPagePos();");
    sb.Append("var h=document.getElementById('hscroll'),c=document.getElementById('colflow');");
    sb.Append("var el=document.getElementById('ch-'+n);if(!h||!c||!el)return;");
    sb.Append("var st=W(),mx=maxTx(),mi=maxIdx();");
    sb.Append("var target=h.scrollLeft+(el.getBoundingClientRect().left-h.getBoundingClientRect().left);");
    sb.Append("target=Math.max(0,Math.min(target,mx));");
    sb.Append("var i=Math.round(target/st);i=Math.max(0,Math.min(i,mi));");
    sb.Append("hrPageIdx=i;hrTx=Math.min(i*st,mx);applyTx();}");

    // ── публичный API ─────────────────────────────────────────────────
    sb.Append("window.__syncHorizontalReaderLayout=sync;");
    sb.Append("window.__hrPageW=W;");
    sb.Append("window.__hrSetPageIndex=function(idx,smooth){setPage(idx,!!smooth);};");
    sb.Append("window.__hrGetPageIndex=function(){");
    sb.Append("var h=document.getElementById('hscroll');if(!h)return hrPageIdx;");
    sb.Append("var st=W();if(st<=0)return hrPageIdx;");
    sb.Append("var gi=Math.round((h.scrollLeft||0)/st);return Math.max(0,Math.min(gi,maxIdx()));};");
    sb.Append("window.__hrPageCount=function(){reflowPagePos();return maxIdx()+1;};");
    sb.Append("window.__hrPeekIdxCnt=function(){");
    sb.Append("var st=W();if(st<=0)return '0|1';var mi=maxIdx();");
    sb.Append("var h=document.getElementById('hscroll');");
    sb.Append("var gi=h?Math.round((h.scrollLeft||0)/st):0;");
    sb.Append("gi=Math.max(0,Math.min(gi,mi));return String(gi)+'|'+String(mi+1);};");
    sb.Append("window.__hrJumpToChapter=function(n){jumpCh(n);};");
    sb.Append("window.__hrReflowPagePos=reflowPagePos;");
    sb.Append("window.__hrLayoutOk=function(){var h=document.getElementById('hscroll');if(!h)return'0|0|0';");
    sb.Append("var cw=Math.floor(h.clientWidth||0),ch=Math.floor(h.clientHeight||0);");
    sb.Append("var ok=(cw>=32&&ch>=40)?'1':'0';return ok+'|'+cw+'|'+ch;};");
    // Прямой scrollLeft без обновления hrPageIdx/reflow даёт следующий sync() со сбросом на страницу 0.
    sb.Append("window.__hrSnapScrollLeftFromGeometry=function(px){");
    sb.Append("var h=document.getElementById('hscroll');if(!h)return;");
    sb.Append("sync();");
    sb.Append("var st=W(),mx=maxTx();if(st<=0)return;");
    sb.Append("var tgt=Math.min(mx,Math.max(0,Math.round(px)));var mi=maxIdx();");
    sb.Append("var i=Math.round(tgt/st);i=Math.max(0,Math.min(i,mi));hrPageIdx=i;hrTx=Math.min(i*st,mx);applyTx();};");

    // ── boot + observers ──────────────────────────────────────────────
    sb.Append("function boot(){sync();requestAnimationFrame(function(){sync();reflowPagePos();});");
    sb.Append("setTimeout(function(){sync();reflowPagePos();},80);");
    sb.Append("setTimeout(function(){sync();reflowPagePos();},300);");
    sb.Append("setTimeout(function(){var h=document.getElementById('hscroll');if(h)h.style.visibility='visible';},2200);}");
    sb.Append("if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',boot);else boot();");
    sb.Append("var h0=document.getElementById('hscroll');");
    sb.Append("if(h0&&window.ResizeObserver)(new ResizeObserver(function(){sync();requestAnimationFrame(reflowPagePos);})).observe(h0);");
    sb.Append("window.addEventListener('resize',function(){sync();requestAnimationFrame(reflowPagePos);});");
    sb.Append("if(window.visualViewport)window.visualViewport.addEventListener('resize',function(){sync();requestAnimationFrame(reflowPagePos);});");
    sb.Append("window.__readerHrLinearBookApproxOffset=function(){try{var h=document.getElementById('hscroll');var len=+window.__hrTextLen|0;if(!h||!(len>=1))return -1;try{if(h.style.visibility!=='visible')h.style.visibility='visible'}catch(eV){};var vw=Math.floor(h.clientWidth||0),sw=Math.floor(h.scrollWidth||0);if(vw<8)return -1;var mx=sw-vw;if(mx<64)return -1;var sl=+h.scrollLeft||0;var r=Math.min(1,Math.max(0,sl/mx));var wi=Math.round(r*(len-1));if(wi<0)wi=0;if(wi>=len)wi=len-1;return wi|0;}catch(e){return -1;}};");
    sb.Append("window.__readerHrCoarseSnapToBookOffset=function(n){try{n=Math.max(0,Math.floor(+n|0));var h=document.getElementById('hscroll');var len=+window.__hrTextLen|0;if(!h||len<1)return;try{if(h.style.visibility!=='visible')h.style.visibility='visible'}catch(eVi){}");
    sb.Append("if(window.__syncHorizontalReaderLayout)window.__syncHorizontalReaderLayout();void h.offsetWidth;var vw=Math.floor(h.clientWidth||0),sw=Math.floor(h.scrollWidth||0);if(vw<8)return;var mx=Math.max(0,sw-vw);if(mx<8)return;");
    sb.Append("var r=len<=1?0:Math.min(1,Math.max(0,n/Math.max(1,len-1)));var sl=Math.round(r*mx);if(typeof window.__hrSnapScrollLeftFromGeometry==='function')window.__hrSnapScrollLeftFromGeometry(sl);else h.scrollLeft=sl;}catch(eCs){}};");
    sb.Append("})();</script>");
  }

  /// <summary>HTML-фрагмент абзацев (как на странице чтения).</summary>
  public static string BuildPlainBlocksHtmlFragment(string pageText)
  {
    var sb = new StringBuilder();
    AppendPlainBlocksHtml(sb, pageText, 0);
    return sb.ToString();
  }

  /// <summary>Оболочка для калибровки: один JS <c>__measureFitMaxLen(plain)</c> — сколько символов помещается по высоте (как страница чтения).</summary>
  public static string BuildViewportMeasureShellHtml(
      double fontSizePx,
      string textAlignCss,
      string foregroundHex,
      string backgroundHex,
      double paddingPx)
  {
    string ta = NormalizeTextAlignCss(textAlignCss);
    bool isJustify = string.Equals(ta, "justify", StringComparison.OrdinalIgnoreCase);
    string fs = fontSizePx.ToString(CultureInfo.InvariantCulture);
    string pad = paddingPx.ToString(CultureInfo.InvariantCulture);
    var sb = new StringBuilder();
    sb.Append("<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\"/>");
    sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\"/>");
    sb.Append("<style>");
    sb.Append("html,body{margin:0;padding:0;overflow:hidden;background:");
    sb.Append(backgroundHex);
    sb.Append(";color:");
    sb.Append(foregroundHex);
    sb.Append(";}body{font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;font-size:");
    sb.Append(fs);
    sb.Append("px;line-height:1.6;padding:0;padding-left:calc(");
    sb.Append(pad);
    sb.Append("px + env(safe-area-inset-left,0px));padding-right:calc(");
    sb.Append(pad);
    sb.Append("px + env(safe-area-inset-right,0px));padding-top:calc(");
    sb.Append(pad);
    sb.Append("px + max(env(safe-area-inset-top,0px),0px));padding-bottom:calc(");
    sb.Append(pad);
    sb.Append("px + max(28px,env(safe-area-inset-bottom,0px)));text-align:");
    sb.Append(ta);
    if (isJustify)
      sb.Append(";text-justify:inter-word;-webkit-text-align-last:auto;text-align-last:auto");
    sb.Append(";hyphens:auto;-webkit-hyphens:auto;word-wrap:break-word;overflow-wrap:break-word;}");
    sb.Append("p.para{display:block;margin:0 0 1.25em 0;padding:0;white-space:pre-line;text-align:");
    sb.Append(ta);
    if (isJustify)
      sb.Append(";text-justify:inter-word");
    sb.Append(";text-indent:0;}#m{opacity:0;pointer-events:none;}</style></head><body>");
    sb.Append("<div id=\"m\"></div><script>");
    sb.Append(ViewportMeasureShellScript);
    sb.Append("</script></body></html>");
    return sb.ToString();
  }

  private static readonly string ViewportMeasureShellScript =
      """
      (function(){
      function H(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
      function P(t){
      var m=t.trim();
      if(!m.length)return'<p class="para"> </p>';
      var b=m.split(/\r\n\s*\r\n|\n\s*\n/g);
      if(b.length<=1&&/[\r\n]/.test(m)){
      b=m.split(/[\r\n]+/).filter(function(x){return x.length>0;});
      }
      if(b.length<=1)return'<p class="para">'+H(m)+'</p>';
      var r='',i;
      for(i=0;i<b.length;i++){
      var z=b[i].trim();
      if(z.length)r+='<p class="para">'+H(z)+'</p>';
      }
      return r;
      }
      function fit(html){
      var el=document.getElementById('m');
      el.innerHTML=html;
      var h=Math.max(document.documentElement.scrollHeight,document.body.scrollHeight);
      return h<=window.innerHeight+10;
      }
      function snapWord(plain,lo){
      if(lo<=1)return lo;
      for(var j=lo-1;j>0;j--){
      if(/\s/.test(plain[j])&&fit(P(plain.substring(0,j))))return j;
      }
      return lo;
      }
      window.__measureFitMaxLen=function(plain){
      if(!plain||!plain.length)return 0;
      if(!fit(P(plain.substring(0,1))))return 1;
      if(fit(P(plain)))return plain.length;
      var lo=1,hi=plain.length;
      while(lo<hi-1){
      var mid=(lo+hi+1)>>1;
      if(fit(P(plain.substring(0,mid))))lo=mid;else hi=mid;
      }
      return snapWord(plain,lo);
      };
      })();
      """;

  static void AppendFb2ParagraphOpenTag(StringBuilder sb, IReadOnlyDictionary<XElement, int>? bookOffsets, XElement pEl)
  {
    sb.Append("<p class=\"para\"");
    if (bookOffsets != null && bookOffsets.TryGetValue(pEl, out int bo))
    {
      sb.Append(" data-bo=\"");
      sb.Append(bo.ToString(CultureInfo.InvariantCulture));
      sb.Append("\"");
    }
    sb.Append(">");
  }

  /// <summary>Порядок как в XML: абзацы и вложенные &lt;section&gt; чередуются.</summary>
  private static void AppendFb2SectionHtml(StringBuilder sb, XElement section, IReadOnlyDictionary<XElement, int>? bookOffsets = null)
  {
    foreach (var el in section.Elements())
    {
      if (el.Name.LocalName == "title")
        continue;
      if (el.Name.LocalName == "section")
      {
        var sub = Fb2BookTextExtractor.GetSectionTitle(el);
        if (!string.IsNullOrWhiteSpace(sub))
        {
          sb.Append("<h3 class=\"subtitle\">");
          sb.Append(WebUtility.HtmlEncode(sub.Trim()));
          sb.Append("</h3>");
        }
        AppendFb2SectionHtml(sb, el, bookOffsets);
        continue;
      }
      AppendFb2ContentElement(sb, el, bookOffsets);
    }
  }

  private static void AppendFb2ContentElement(StringBuilder sb, XElement el, IReadOnlyDictionary<XElement, int>? bookOffsets)
  {
    switch (el.Name.LocalName)
    {
      case "p":
        if (Fb2BookTextExtractor.ShouldSkipFb2WhitespaceOnlyParagraph(el))
          return;
        AppendFb2ParagraphOpenTag(sb, bookOffsets, el);
        AppendParagraphInnerHtml(sb, el);
        sb.Append("</p>");
        break;
      case "empty-line":
        sb.Append("<div class=\"vspace\"></div>");
        break;
      case "subtitle":
        sb.Append("<h3 class=\"subtitle\">");
        sb.Append(WebUtility.HtmlEncode(Fb2RichParagraphParser.ParagraphPlainText(el)));
        sb.Append("</h3>");
        break;
      case "epigraph":
      case "cite":
        sb.Append("<cite class=\"fb2\">");
        foreach (var p in el.Elements().Where(e => e.Name.LocalName == "p"))
        {
          if (Fb2BookTextExtractor.ShouldSkipFb2WhitespaceOnlyParagraph(p))
            continue;
          AppendFb2ParagraphOpenTag(sb, bookOffsets, p);
          AppendParagraphInnerHtml(sb, p);
          sb.Append("</p>");
        }
        sb.Append("</cite>");
        break;
      case "poem":
        foreach (var v in el.Descendants().Where(e => e.Name.LocalName == "v"))
        {
          sb.Append("<div class=\"verse\">");
          sb.Append(WebUtility.HtmlEncode(Fb2RichParagraphParser.ParagraphPlainText(v)));
          sb.Append("</div>");
        }
        break;
      default:
        if (el.Descendants().Any(e => e.Name.LocalName == "p"))
        {
          foreach (var p in el.Descendants().Where(e => e.Name.LocalName == "p"))
          {
            if (Fb2BookTextExtractor.ShouldSkipFb2WhitespaceOnlyParagraph(p))
              continue;
            AppendFb2ParagraphOpenTag(sb, bookOffsets, p);
            AppendParagraphInnerHtml(sb, p);
            sb.Append("</p>");
          }
        }
        break;
    }
  }

  private static void AppendParagraphInnerHtml(StringBuilder sb, XElement pElement)
  {
    var parts = new List<(string Text, FontAttributes Fa, TextDecorations Td)>();
    foreach (var node in pElement.Nodes())
      CollectParts(node, FontAttributes.None, TextDecorations.None, parts);
    MergeAdjacent(parts);
    foreach (var (text, fa, td) in parts)
    {
      if (string.IsNullOrEmpty(text)) continue;
      var t = WebUtility.HtmlEncode(text);
      if ((td & TextDecorations.Strikethrough) != 0)
        t = "<s>" + t + "</s>";
      if ((fa & FontAttributes.Italic) != 0)
        t = "<em>" + t + "</em>";
      sb.Append(t);
    }
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
