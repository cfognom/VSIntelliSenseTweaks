using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using System;

namespace VSIntelliSenseTweaks.Utilities
{
    static class Helpers
    {
        public static TextSpan GetTextSpan(ref this SnapshotSpan span)
        {
            TextSpan textSpan = new TextSpan();

            var startPoint = span.Start;
            var startLine = startPoint.GetContainingLine();
            textSpan.iStartIndex = startPoint.Position - startLine.Start.Position;
            textSpan.iStartLine = startLine.LineNumber;

            var endPoint = span.End;
            var endLine = endPoint.GetContainingLine();
            textSpan.iEndIndex = endPoint.Position - endLine.Start.Position;
            textSpan.iEndLine = endLine.LineNumber;

            return textSpan;
        }

        public static ReadOnlySpan<char> Slice(this ReadOnlySpan<char> word, Span span)
        {
            return word.Slice(span.Start, span.Length);
        }
    }
}
