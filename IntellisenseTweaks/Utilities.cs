﻿using Microsoft.VisualStudio.Text;
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
            var startLine = startPoint.GetContainingLine();
            textSpan.iStartIndex = startPoint.Position - startLine.Start.Position;
            textSpan.iStartLine = startLine.LineNumber;

            var endPoint = span.End;
            var endLine = endPoint.GetContainingLine();
            textSpan.iEndIndex = endPoint.Position - endLine.Start.Position;
            textSpan.iEndLine = endLine.LineNumber;

            return textSpan;
        }
    }
}
