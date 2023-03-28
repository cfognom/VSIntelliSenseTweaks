using Microsoft.VisualStudio.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Windows.Markup;
using VSIntelliSenseTweaks.Utilities;

namespace VSIntelliSenseTweaks
{
    public struct WordScorer
    {
        UnmanagedStack<MatchedSpan> matchedSpans;

        public WordScorer(int stackInitialCapacity)
        {
            this.matchedSpans = new UnmanagedStack<MatchedSpan>(stackInitialCapacity);
        }

        public int ScoreWord(ReadOnlySpan<char> word, ReadOnlySpan<char> pattern, out ImmutableArray<Span> matchedSpans)
        {
            int wordLength = word.Length;
            int patternLength = pattern.Length;
            Debug.Assert(patternLength > 0);
            Debug.Assert(patternLength <= 256);

            Span<CharRange> charRanges = stackalloc CharRange[patternLength];

            if (!Prospect(word, pattern, charRanges))
            {
                matchedSpans = default;
                return int.MinValue;
            }

            int n_ints = BitSpan.GetRequiredIntCount(wordLength + 1);
            Span<int> ints = n_ints <= 256 ? stackalloc int[n_ints] : new int[n_ints];
            var isSubwordStart = new BitSpan(ints);
            int n_subwords = DetermineSubwords(word, isSubwordStart);

            Span<MatchedSpan> spans = stackalloc MatchedSpan[patternLength];
            Span<byte> charToSpan = stackalloc byte[patternLength];

            var data = new PatternMatchingData
            {
                word = word,
                pattern = pattern,
                charRanges = charRanges,
                charToSpan = charToSpan,
                spans = spans,
                isSubwordStart = isSubwordStart,
                n_subwords = n_subwords,
                n_spans = 0,
            };

            FindMatchingSpans(ref data);
            CombineMatchedSpans(ref data);

            //Span<Span> subwordSpans = n_subwords <= 128 ? stackalloc Span[n_subwords] : new Span[n_subwords];
            //PopulateSubwords(wordLength, isSubwordStart, subwordSpans);
            int score = CompileSpans(ref data, out matchedSpans);
            return score;
        }

        static int DetermineSubwords(ReadOnlySpan<char> word, BitSpan isSubwordStart)
        {
            Span<CharKind> charKinds = word.Length <= 1024 ? stackalloc CharKind[word.Length] : new CharKind[word.Length];
            int n_chars = word.Length;

            for (int i = 0; i < n_chars; i++)
            {
                charKinds[i] = new CharKind(word[i]);
            }

            int n_subwords = 0;
            for (int i = 0; i < n_chars; i++)
            {
                bool isSubwordBeginning = i == 0
                  || ((!charKinds[i - 1].IsUpper || word[i - 1] == 'I') && charKinds[i].IsUpper)
                  || (charKinds[i - 1].IsLetter != charKinds[i].IsLetter)
                  || (i + 1 < n_chars && charKinds[i].IsUpper && !charKinds[i + 1].IsUpper);

                // TODO: Set each bit to ensure initialized.
                if (isSubwordBeginning)
                {
                    isSubwordStart.SetBit(i);
                    n_subwords++;
                }
            }
            return n_subwords;
        }

        static void PopulateSubwords(int wordLength, BitSpan isSubwordStart, Span<Span> subwordSpans)
        {
            int j = 0;
            for (int i = 0; i < subwordSpans.Length; i++)
            {
                int start = j;
                do
                {
                    j++;
                } while (j != wordLength && !isSubwordStart[j]);
                int length = j - start;
                subwordSpans[i] = new Span(start, length);
            }
        }

        private ref struct PatternMatchingData
        {
            public ReadOnlySpan<char> word;
            public ReadOnlySpan<char> pattern;
            public Span<CharRange> charRanges;
            public Span<byte> charToSpan;
            public Span<MatchedSpan> spans;
            public BitSpan isSubwordStart;
            public int n_subwords;
            public byte n_spans;

            public byte GetSpanIndex(int charIndex) => charToSpan[charIndex];

            public void AddSpan(MatchedSpan span)
            {
                byte index = n_spans++;
                spans[index] = span;
                int i_end = span.EndInPattern;
                for (int i = span.StartInPattern; i < i_end; i++)
                {
                    charToSpan[i] = index;
                }
            }

            public bool IsInRange(int indexInWord, int indexInPattern)
            {
                return charRanges[indexInPattern].IsInRange(indexInWord);
            }
        }

        struct MatchedSpanAndScore
        {
            public MatchedSpan span;
            public int score;
        }

        private struct CharRange
        {
            public short minPos;
            public short maxPos;

            public bool IsInRange(int index)
            {
                return minPos <= index && index <= maxPos;
            }
        }

        static bool Prospect(ReadOnlySpan<char> word, ReadOnlySpan<char> pattern, Span<CharRange> ranges)
        {
            int wordLength = word.Length;
            int patternLength = pattern.Length;
            Debug.Assert(patternLength == ranges.Length);
            int i = 0;
            int j = 0;
            while (i < wordLength && j < patternLength)
            {
                if (FuzzyCharEquals(word[i], pattern[j]))
                {
                    ranges[j].minPos = (short)i;
                    j++;
                }
                i++;
            }

            if (j != patternLength)
            {
                return false;
            }

            i = wordLength - 1;
            j--;

            while (j > -1)
            {
                if (FuzzyCharEquals(word[i], pattern[j]))
                {
                    ranges[j].maxPos = (short)i;
                    j--;
                }
                i--;
            }
            Debug.Assert(j == -1);
            return true;
        }

        void FindMatchingSpans(ref PatternMatchingData data)
        {
            this.matchedSpans.count = 0;

            int i_final = data.word.Length - data.pattern.Length;

            for (int k = 0; k <= i_final; k++)
            {
                DetermineApplicablePatternSpan(k, data.charRanges, out int j, out int j_final);
                int i = k + j;
                int length = 0;
                bool isEnd = j > j_final;
                while (!isEnd)
                {
                    bool eq = FuzzyCharEquals(data.word[i], data.pattern[j]);
                    if (eq) length++;

                    i++;
                    j++;

                    isEnd = j > j_final;

                    if ((!eq || isEnd || data.isSubwordStart[i]) && length > 0)
                    {
                        int startInWord = i - length - (!eq ? 1 : 0);
                        int startInPattern = j - length - (!eq ? 1 : 0);
                        var newSpan = new MatchedSpan(startInWord, startInPattern, length, data.isSubwordStart[startInWord]);
                        //int score = ScoreSpan(newSpan, length);
                        this.matchedSpans.Push(newSpan);
                        length = 0;
                    }
                }
            }
        }

        static void DetermineApplicablePatternSpan(int k, Span<CharRange> ranges, out int start, out int final)
        {
            start = 0;
            final = ranges.Length - 1;
            while (start <  final && k + start > ranges[start].maxPos) start++;
            while (final >= start && k + final < ranges[final].minPos) final--;
        }

        void CombineMatchedSpans(ref PatternMatchingData data)
        {
            Debug.Assert(matchedSpans.count > 0);

            int n_matchedInPattern = 0;
            for (int i = 0; i < matchedSpans.count; i++)
            {
                var newSpan = matchedSpans.array[i];

                if (newSpan.StartInPattern > n_matchedInPattern)
                {
                    continue;
                }

                if (newSpan.StartInPattern == n_matchedInPattern)
                {
                    data.AddSpan(newSpan);
                    n_matchedInPattern = newSpan.EndInPattern;
                    continue;
                }

                // newSpan.StartInPattern < n_matchedInPattern

                var existingSpanIndex = data.charToSpan[newSpan.StartInPattern];
                var existingSpan = data.spans[existingSpanIndex];

                var endSpanIndex = newSpan.EndInPattern < n_matchedInPattern ?
                    data.charToSpan[newSpan.EndInPattern - 1] + 1 : data.n_spans;

                int n_subs_before = 0;
                int n_spans_before = endSpanIndex - existingSpanIndex;
                for (int j = existingSpanIndex; j < endSpanIndex; j++)
                {
                    n_subs_before += data.spans[j].IsSubwordStart_AsInt;
                }
                int n_subs_after = newSpan.IsSubwordStart_AsInt;
                int n_spans_after;
                if (newSpan.StartInPattern != existingSpan.StartInPattern)
                {
                    n_spans_after = 2;
                    n_subs_after++;
                }
                else
                {
                    n_spans_after = 1;
                }

                bool merge = false;
                if (n_subs_after > n_subs_before)
                    merge = true;
                else if (n_subs_after == n_subs_before && n_spans_after < n_spans_before)
                    merge = true;
                else if (n_subs_after == n_subs_before && n_spans_after == n_spans_before && newSpan.EndInPattern > n_matchedInPattern)
                    merge = true;

                if (merge)
                {
                    data.n_spans = existingSpanIndex;
                    int overlap = existingSpan.EndInPattern - newSpan.StartInPattern;
                    Debug.Assert(overlap > 0);
                    if (overlap < existingSpan.Length)
                    {
                        data.AddSpan(existingSpan.TrimBack(overlap));
                    }
                    data.AddSpan(newSpan);
                    n_matchedInPattern = newSpan.EndInPattern;
                }
                else if (newSpan.EndInPattern > n_matchedInPattern)
                {
                    int trimCount = n_matchedInPattern - newSpan.StartInPattern;
                    Debug.Assert(trimCount > 0);
                    newSpan = newSpan.TrimFront(trimCount);
                    Debug.Assert(!data.isSubwordStart[newSpan.Start]);
                    data.AddSpan(newSpan);
                    n_matchedInPattern = newSpan.EndInPattern;
                }

                //if (newSpan.StartInPattern == existingSpan.StartInPattern)
                //{
                //    if (IsNewBetter())
                //    {
                //        data.n_spans = existingSpanIndex;
                //        data.AddSpan(newSpan);
                //        n_matchedInPattern = newSpan.EndInPattern;
                //    }

                //    bool IsNewBetter()
                //    {
                //        if (newSpan.IsSubwordStart_AsInt > existingSpan.IsSubwordStart_AsInt)
                //        //if (newSpan.IsSubwordStart)
                //            return true;

                //        //if (newSpan.Length > existingSpan.Length)
                //        //    return true;

                //        //if (newSpan.Start < existingSpan.Start)
                //        //    return true;

                //        return false;
                //    }
                //}
                //else
                //{
                //    if (IsNewBetter())
                //    {
                //        data.n_spans = existingSpanIndex;
                //        int overlap = existingSpan.EndInPattern - newSpan.StartInPattern;
                //        Debug.Assert(overlap > 0);
                //        data.AddSpan(existingSpan.TrimBack(overlap));
                //        data.AddSpan(newSpan);
                //        n_matchedInPattern = newSpan.EndInPattern;
                //    }
                //    else if (newSpan.EndInPattern > n_matchedInPattern)
                //    {
                //        int trimCount = n_matchedInPattern - newSpan.StartInPattern;
                //        Debug.Assert(trimCount > 0);
                //        newSpan = newSpan.TrimFront(trimCount);
                //        Debug.Assert(!data.isSubwordStart[newSpan.Start]);
                //        data.AddSpan(newSpan);
                //        n_matchedInPattern = newSpan.EndInPattern;
                //    }

                //    bool IsNewBetter()
                //    {
                //        //if (newSpan.IsSubwordStart_AsInt > existingSpan.IsSubwordStart_AsInt)
                //        if (newSpan.IsSubwordStart)
                //            return true;

                //        if (newSpan.Length > existingSpan.Length)
                //            return true;

                //        if (newSpan.Start < existingSpan.Start)
                //            return true;

                //        return false;
                //    }
                //}

                //if (comparer.Compare(existingSpan, newSpan) < 0)
                //{
                //    data.n_spans = existingSpanIndex;
                //    int overlap = existingSpan.EndInPattern - newSpan.StartInPattern;
                //    Debug.Assert(overlap > 0);
                //    if (overlap < existingSpan.Length)
                //    {
                //        data.AddSpan(existingSpan.TrimBack(overlap));
                //    }
                //    data.AddSpan(newSpan);
                //    n_matchedInPattern = newSpan.EndInPattern;
                //}
                //else if (newSpan.EndInPattern > n_matchedInPattern)
                //{
                //    int trimCount = n_matchedInPattern - newSpan.StartInPattern;
                //    Debug.Assert(trimCount > 0);
                //    newSpan = newSpan.TrimFront(trimCount);
                //    Debug.Assert(!data.isSubwordStart[newSpan.Start]);
                //    data.AddSpan(newSpan);
                //    n_matchedInPattern = newSpan.EndInPattern;
                //}
                //else if (newSpan.EndInPattern == n_matchedInPattern)
                //{
                //    //throw new Exception();
                //}
            }

            Debug.Assert(n_matchedInPattern == data.pattern.Length);
        }

        static int CompileSpans(ref PatternMatchingData data, out ImmutableArray<Span> matchedSpans)
        {
            int n_spans = data.n_spans;
            var builder = ImmutableArray.CreateBuilder<Span>(n_spans);
            builder.Count = n_spans;
            int score = 0;
            int n_subwordHits = 0;
            for (int i = 0; i < n_spans; i++)
            {
                var span = data.spans[i];
                builder[i] = span.ToSpan();
                if (span.IsSubwordStart) n_subwordHits++;
                int exactCount = 0;
                for (int j = 0; j < span.Length; j++)
                {
                    if (data.word[span.Start + j] == data.pattern[span.StartInPattern + j])
                    {
                        exactCount++;
                    }
                }
                score += ScoreSpan(span, exactCount); 
            }

            score -= (data.word.Length - data.pattern.Length);
            score -= 16 * (data.n_subwords - n_subwordHits);
            score -= 16 * n_spans;

            matchedSpans = builder.MoveToImmutable();

            return score;
        }

        static int ScoreSpan(MatchedSpan span, int exactCount)
        {
            int effectiveLength = span.Length + exactCount;
            int score = 32 * effectiveLength;
            score *= span.IsSubwordStart ? 4 : 1;
            score -= span.Start;
            return score;
        }

        static bool FuzzyCharEquals(char a, char b)
        {
            int comp = a - b;
            bool result = comp == 0;
            result |= comp == 32;
            result |= comp == -32;
            return result;
        }

        private struct FirstSpanFirst : IComparer<MatchedSpan>
        {
            public int Compare(MatchedSpan x, MatchedSpan y)
            {
                int comp = x.Start - y.Start;
                return comp;
            }
        }

        private struct BestSpanLast : IComparer<MatchedSpan>
        {
            public int Compare(MatchedSpan x, MatchedSpan y)
            {
                int comp = x.IsSubwordStart_AsInt - y.IsSubwordStart_AsInt;
                if (comp == 0)
                {
                    comp = x.Length - y.Length;
                }
                //if (comp == 0)
                //{
                //    comp = y.Inexactness - x.Inexactness;
                //}
                if (comp == 0)
                {
                    comp = y.Start - x.Start;
                }
                return comp;
            }
        }

        private struct MatchedSpan
        {
            short start;
            byte startInPattern;
            byte length;
            byte isSubwordStart;

            public bool IsValid => length > 0;
            public int Start
            {
                get => start;
                set => start = (short)value;
            }
            public int StartInPattern
            {
                get => startInPattern;
                set => startInPattern = (byte)value;
            }
            public int End => start + length;
            public int EndInPattern => startInPattern + length;
            public int Length
            {
                get => length;
                set => length = (byte)value;
            }
            public bool IsSubwordStart => isSubwordStart == 1;
            public int IsSubwordStart_AsInt => isSubwordStart;

            public MatchedSpan(int start, int startInPattern, int length, bool isSubwordStart)
            {
                this.start = (short)start;
                this.startInPattern = (byte)startInPattern;
                this.length = (byte)length;
                this.isSubwordStart = isSubwordStart ? (byte)1 : (byte)0;
            }

            public Span ToSpan()
            {
                return new Span(start, length);
            }

            public MatchedSpan TrimFront(int count)
            {
                Debug.Assert(count < length);
                return new MatchedSpan(start + count, startInPattern + count, length - count, false);
            }

            public MatchedSpan TrimBack(int count)
            {
                Debug.Assert(count < length);
                return new MatchedSpan(start, startInPattern, length - count, IsSubwordStart);
            }
        }
    }
}