/*
    Copyright 2023 Carl Foghammar Nömtak

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

        http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.
*/

using Microsoft.VisualStudio.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;

namespace VSIntelliSenseTweaks.Utilities
{
    public struct WordScorer
    {
        UnmanagedStack<MatchedSpan> matchedSpans;

        public WordScorer(int stackInitialCapacity)
        {
            this.matchedSpans = new UnmanagedStack<MatchedSpan>(stackInitialCapacity);
        }

        public int ScoreWord(ReadOnlySpan<char> word, ReadOnlySpan<char> pattern, int displayTextOffset, out ImmutableArray<Span> matchedSpans)
        {
            int wordLength = word.Length;
            int patternLength = pattern.Length;
            Debug.Assert(patternLength > 0);
            Debug.Assert(patternLength <= 256);

            if (wordLength < patternLength)
            {
                matchedSpans = default;
                return int.MinValue;
            }

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

            int score = CompileSpans(ref data, displayTextOffset, out matchedSpans);
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
                  || (!charKinds[i - 1].IsLetter && charKinds[i].IsLetter)
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
            public int n_spans;

            public int GetSpanIndex(int charIndex) => charToSpan[charIndex];

            public void AddSpan(MatchedSpan span)
            {
                var index = n_spans++;
                spans[index] = span;
                int i_end = span.EndInPattern;
                for (int i = span.StartInPattern; i < i_end; i++)
                {
                    charToSpan[i] = (byte)index;
                }
            }

            public bool IsInRange(int indexInWord, int indexInPattern)
            {
                return charRanges[indexInPattern].IsInRange(indexInWord);
            }
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
            while (j < patternLength)
            {
                if (patternLength - j > wordLength - i)
                {
                    return false;
                }

                if (FuzzyCharEquals(word[i], pattern[j]))
                {
                    ranges[j].minPos = (short)i;
                    j++;
                }
                i++;
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
                    if (FuzzyCharEquals(data.word[i], data.pattern[j]))
                    {
                        length++;
                    }
                    else if (length > 0)
                    {
                        MakeSpan(ref data, this.matchedSpans);
                    }

                    i++;
                    j++;

                    isEnd = j > j_final;

                    if (length > 0 && (isEnd || data.isSubwordStart[i]))
                    {
                        MakeSpan(ref data, this.matchedSpans);
                    }

                    void MakeSpan(ref PatternMatchingData _data, UnmanagedStack<MatchedSpan> matchedSpans)
                    {
                        int startInWord = i - length;
                        int startInPattern = j - length;
                        var newSpan = new MatchedSpan(startInWord, startInPattern, length, _data.isSubwordStart[startInWord]);
                        matchedSpans.Push(newSpan);
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

                ConsiderSpan(ref data, ref n_matchedInPattern, newSpan);
            }

            Debug.Assert(n_matchedInPattern == data.pattern.Length);
        }

        private static void ConsiderSpan(ref PatternMatchingData data, ref int n_matchedInPattern, MatchedSpan newSpan)
        {
            if (newSpan.StartInPattern > n_matchedInPattern)
            {
                return;
            }

            if (newSpan.StartInPattern == n_matchedInPattern)
            {
                data.AddSpan(newSpan);
                n_matchedInPattern = newSpan.EndInPattern;
                return;
            }

            // newSpan.StartInPattern < n_matchedInPattern

            int existingSpanIndex = data.charToSpan[newSpan.StartInPattern];
            MatchedSpan existingSpan = data.spans[existingSpanIndex];

            if (ShouldMerge(ref data, ref n_matchedInPattern))
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

            bool ShouldMerge(ref PatternMatchingData data_, ref int n_matchedInPattern_)
            {
                if (newSpan.StartInPattern == existingSpan.StartInPattern)
                {
                    if (newSpan.IsSubwordStart_AsInt > existingSpan.IsSubwordStart_AsInt)
                        return true;

                    if (newSpan.IsSubwordStart_AsInt < existingSpan.IsSubwordStart_AsInt)
                        return false;

                    if (newSpan.Length > existingSpan.Length)
                        return true;

                    return false;
                }

                if (newSpan.IsSubwordStart && newSpan.EndInPattern > existingSpan.EndInPattern)
                    return true;

                return false;
            }
        }

        static int CompileSpans(ref PatternMatchingData data, int displayTextOffset, out ImmutableArray<Span> matchedSpans)
        {
            int n_spans = data.n_spans;
            Debug.Assert(n_spans > 0);
            var builder = ImmutableArray.CreateBuilder<Span>(n_spans);
            builder.Count = n_spans;
            int score = 0;
            int n_subwordHits = 0;
            int n_upperMatchedAsLower = 0;
            int n_lowerMatchedAsUpper = 0;
            for (int i = 0; i < n_spans; i++)
            {
                var span = data.spans[i];
                builder[i] = new Span(span.Start + displayTextOffset, span.Length);
                if (span.IsSubwordStart) n_subwordHits++;
                for (int j = 0; j < span.Length; j++)
                {
                    int comp = data.word[span.Start + j] - data.pattern[span.StartInPattern + j];
                    if (comp < 0)
                    {
                        n_lowerMatchedAsUpper++;
                    }
                    else if (comp > 0)
                    {
                        n_upperMatchedAsLower++;
                    }
                }
                score += ScoreSpan(span); 
            }

            int n_unmatchedChars = data.word.Length - data.pattern.Length;
            int n_unmatchedTrailingChars = data.word.Length - data.spans[n_spans - 1].End;
            int n_unmatchedPassedChars = n_unmatchedChars - n_unmatchedTrailingChars;
            int n_unmatchedSubwords = data.n_subwords - n_subwordHits;

            score -= 4 * n_unmatchedPassedChars;
            score -= 1 * n_unmatchedTrailingChars;
            score -= 64 * n_unmatchedSubwords;
            score -= 16 * n_spans;
            score -= 32 * n_upperMatchedAsLower;
            score -= 16 * n_lowerMatchedAsUpper;

            if (n_unmatchedChars == 0 && n_upperMatchedAsLower == 0 && n_lowerMatchedAsUpper == 0)
            {
                // Perfect match gets a bonus.
                score *= 2;
            }

            matchedSpans = builder.MoveToImmutable();

            return score;
        }

        static int ScoreSpan(MatchedSpan span)
        {
            int effectiveLength = span.Length;
            int score = 32 * effectiveLength;
            score *= span.IsSubwordStart ? 4 : 1;
            //score -= span.Start;
            return score;
        }

        static bool FuzzyCharEquals(char a, char b)
        {
            // May need to be improved if non-ascii chars are used.
            int comp = a - b;
            bool result = comp == 0;
            result |= comp ==  32 && a >= 'a' && b <= 'Z';
            result |= comp == -32 && a <= 'Z' && b >= 'a';
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

        private struct MatchedSpan
        {
            // Kept small to decrease allocation size.
            ushort isSubwordStart_start;
            byte startInPattern;
            byte length;

            public bool IsValid => length != 0;
            public int Start => isSubwordStart_start & ((1 << 15) - 1);
            public int StartInPattern => startInPattern;
            public int End => Start + length;
            public int EndInPattern => startInPattern + length;
            public int Length => length;
            public bool IsSubwordStart => IsSubwordStart_AsInt == 1;
            public int IsSubwordStart_AsInt => isSubwordStart_start >> 15;

            public MatchedSpan(int start, int startInPattern, int length, bool isSubwordStart)
            {
                Debug.Assert(start >= 0);
                Debug.Assert(start < 1 << 15);
                Debug.Assert(startInPattern >= 0);
                Debug.Assert(startInPattern <= byte.MaxValue);
                Debug.Assert(length >= 0);
                Debug.Assert(length <= byte.MaxValue);
                this.isSubwordStart_start = (ushort)start;
                this.startInPattern = (byte)startInPattern;
                this.length = (byte)length;
                if (isSubwordStart)
                {
                    this.isSubwordStart_start |= 1 << 15;
                }
            }

            public Span ToSpan()
            {
                return new Span(Start, Length);
            }

            public MatchedSpan TrimFront(int count)
            {
                Debug.Assert(count < length);
                return new MatchedSpan(Start + count, StartInPattern + count, Length - count, false);
            }

            public MatchedSpan TrimBack(int count)
            {
                Debug.Assert(count < length);
                return new MatchedSpan(Start, StartInPattern, Length - count, IsSubwordStart);
            }
        }
    }
}