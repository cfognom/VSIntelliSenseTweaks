using Microsoft.VisualStudio.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TextManager.Interop;
using System.Collections;
using Microsoft;
using System.Diagnostics;

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

        public static ReadOnlySpan<char> Slice(this ReadOnlySpan<char> word, Span span)
        {
            return word.Slice(span.Start, span.Length);
        }

        public ref struct BitSpan
        {
            private Span<int> data;

            public static int GetRequiredIntCount(int n_bits)
            {
                Debug.Assert(n_bits > 0);
                return (n_bits - 1) / 32 + 1;
            }

            public BitSpan(Span<int> data)
            {
                this.data = data;
            }

            public bool this[int index]
            {
                get
                {
                    var intIndex = Math.DivRem(index, 32, out var bitIndex);
                    var mask = 1 << bitIndex;
                    return (data[intIndex] & mask) == mask;
                }
                set
                {
                    var intIndex = Math.DivRem(index, 32, out var bitIndex);
                    var mask = 1 << bitIndex;
                    if (value)
                    {
                        data[intIndex] |= mask;
                    }
                    else
                    {
                        data[intIndex] &= ~mask;
                    }
                }
            }
        }
    }
}
