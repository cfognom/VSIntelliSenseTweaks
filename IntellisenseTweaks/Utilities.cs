using Microsoft.VisualStudio.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TextManager.Interop;

namespace IntellisenseTweaks
{
    internal static class Utilities
    {
        public static TextSpan GetTextSpan(ref this SnapshotSpan span)
        {
            TextSpan textSpan = new TextSpan();

            var startPoint = span.Start;
            textSpan.iStartIndex = startPoint.Position;
            textSpan.iStartLine = startPoint.GetContainingLineNumber();

            var endPoint = span.End;
            textSpan.iEndIndex = endPoint.Position;
            textSpan.iEndLine = endPoint.GetContainingLineNumber();

            return textSpan;
        }
    }
}
