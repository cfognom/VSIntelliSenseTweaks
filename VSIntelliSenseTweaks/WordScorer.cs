using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Text;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using VSIntelliSenseTweaks.Utilities;
using static System.Windows.Forms.AxHost;

namespace VSIntelliSenseTweaks
{
    public struct WordScorer
    {
        PendingResult pendingResult;

        public WordScorer(int maxPatternLength)
        {
            this.pendingResult = default;
        }

        public int ScoreWord(ReadOnlySpan<char> word, ReadOnlySpan<char> pattern, out ImmutableArray<Span> matchedSpans)
        {
            int wordLength = word.Length;
            int patternLength = pattern.Length;
            Debug.Assert(patternLength > 0);

            int n_ints = BitSpan.GetRequiredIntCount(wordLength + 1);
            Span<int> ints = n_ints <= 256 ? stackalloc int[n_ints] : new int[n_ints];
            var isSubwordStart = new BitSpan(ints);
            int n_subwords = DetermineSubwords(word, isSubwordStart);

            Debug.Assert(patternLength <= 256);
            Span<MatchedSpan> spans = stackalloc MatchedSpan[patternLength];
            Span<byte> charToSpan = stackalloc byte[patternLength];

            var data = new PatternMatchingData
            {
                word = word,
                pattern = pattern,
                charToSpan = charToSpan,
                spans = spans,
                isSubwordStart = isSubwordStart,
                spanCount = 0,
            };

            if (FindMatchingSpans(ref data))
            {
                return pendingResult.Finalize(wordLength, patternLength, n_subwords, isSubwordStart, out matchedSpans);
            }
            else
            {
                matchedSpans = default;
                return int.MinValue;
            }
        }

        private int DetermineSubwords(ReadOnlySpan<char> word, BitSpan isSubwordStart)
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
                  || (!charKinds[i - 1].IsUpper  && charKinds[i].IsUpper)
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

        private ref struct PatternMatchingData
        {
            public ReadOnlySpan<char> word;
            public ReadOnlySpan<char> pattern;
            public Span<byte> charToSpan;
            public Span<MatchedSpan> spans;
            public BitSpan isSubwordStart;
            public byte spanCount;

            public int GetSpanIndex(int charIndex) => charToSpan[charIndex];
            public void SetSpan(MatchedSpan span, byte index)
            {
                spans[index] = span;
                for (int i = span.startInPattern; i < span.EndInPattern; i++)
                {
                    charToSpan[i] = index;
                }
            }
        }

        // For each position in word try to match pattern.
        // If pattern was (partially) matched push matched span to stack.
        // Sort the pushed spans so that the best span comes last.
        // Pop the recently pushed spans in sorted order.
        // If popped span covers all of pattern => success.
        // Else, reduce word and pattern and call method again (recursive).

        private bool FindMatchingSpans(ref PatternMatchingData data)
        {
            int patternLength = data.pattern.Length;
            int n_matchedInPattern = 0;
            int i_final = data.word.Length - patternLength;
            for (int i = 0; i <= i_final;)
            {
                int wordPos = i + n_matchedInPattern;
                int bCount = MatchBackward(data, wordPos - 1, n_matchedInPattern - 1);
                int fCount = MatchForward(data, wordPos, n_matchedInPattern, out bool isSubwordStartAhead);

                bool isSplit = data.isSubwordStart[wordPos];
                int lengthBase = isSplit ? 0 : fCount;

                while (bCount > 0)
                {
                    int startInPattern = n_matchedInPattern - bCount;
                    int startInWord = wordPos - bCount;
                    int length = lengthBase + bCount;
                    var spanIndex = data.charToSpan[startInPattern];
                    ref var span = ref data.spans[spanIndex];
                    int stealCount = span.EndInPattern - startInPattern;
                    Debug.Assert(stealCount > 0);
                    if (stealCount < length)
                    {
                        span.Length -= stealCount;
                        spanIndex++;
                        WriteSpan(ref data, spanIndex);
                        break;
                    }
                    else if (stealCount == length)
                    {
                        bool isSubwordStart = data.isSubwordStart[startInWord];
                        if (!span.IsSubwordStart && isSubwordStart)
                        {
                            WriteSpan(ref data, spanIndex);
                            break;
                        }
                    }
                    bCount -= stealCount;
                    Debug.Assert(bCount >= 0);

                    void WriteSpan(ref PatternMatchingData s, byte index)
                    {
                        var newSpan = new MatchedSpan(startInWord, startInPattern, length, s.isSubwordStart[startInWord]);
                        s.SetSpan(newSpan, index);
                        s.spanCount = ++index;
                    }
                }

                if (fCount > 0 && (isSplit || bCount == 0))
                {
                    var newSpan = new MatchedSpan(wordPos, n_matchedInPattern, fCount, isSplit);
                    data.SetSpan(newSpan, data.spanCount);
                    data.spanCount++;
                }

                n_matchedInPattern += fCount;
                if (!isSubwordStartAhead) i++;
            }

            bool success = n_matchedInPattern == patternLength;

            if (success)
            {
                Debug.Assert(data.spanCount > 0);
                this.pendingResult = new PendingResult(data.spanCount);
                for (int i = 0; i < data.spanCount; i++)
                {
                    pendingResult.AddSpan(i, data.spans[i]);
                }
            }

            return success;
        }

        private int MatchForward(PatternMatchingData state, int wordStartIndex, int patternStartIndex, out bool isSubwordStartAhead)
        {
            int i = wordStartIndex;
            int j = patternStartIndex;
            int j_stop = state.pattern.Length;
            isSubwordStartAhead = false;
            while (j != j_stop)
            {
                if (!FuzzyCharEquals(state.word[i], state.pattern[j]))
                {
                    break;
                }

                i++;
                j++;

                if (state.isSubwordStart[i])
                {
                    isSubwordStartAhead = true;
                    break;
                }
            }
            return j - patternStartIndex;
        }

        private int MatchBackward(PatternMatchingData state, int wordStartIndex, int patternStartIndex)
        {
            int i = wordStartIndex;
            int j = patternStartIndex;
            int j_stop = -1;
            while (j != j_stop)
            {
                if (!FuzzyCharEquals(state.word[i], state.pattern[j]))
                {
                    break;
                }

                j--;

                if (state.isSubwordStart[i])
                {
                    break;
                }

                i--;
            }
            return patternStartIndex - j;
        }

        private MatchedSpan CreateMatchedSpan(ReadOnlySpan<char> pattern, ReadOnlySpan<char> word, BitSpan isSubwordStart, int wordPosition)
        {
            Debug.Assert(pattern.Length <= word.Length);

            int inexactness = pattern[0] == word[0] ? 0 : 1;
            int i = 1;
            while (i < pattern.Length && !isSubwordStart[wordPosition + i] && char.ToUpperInvariant(pattern[i]) == char.ToUpperInvariant(word[i]))
            {
                if (pattern[i] != word[i])
                {
                    inexactness++;
                }
                i++;
            }

            return new MatchedSpan(wordPosition, 0, i, isSubwordStart[wordPosition]);
        }

        private static bool FuzzyCharEquals(char a, char b)
        {
            int comp = a - b;
            bool result = comp == 0;
            result |= comp == 32;
            result |= comp == -32;
            return result;
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

        struct PendingResult
        {
            ImmutableArray<Span>.Builder builder;
            int score;

            public PendingResult(int n_matchedSpans) : this()
            {
                Debug.Assert(n_matchedSpans > 0);

                this.builder = ImmutableArray.CreateBuilder<Span>(n_matchedSpans);
                builder.Count = n_matchedSpans;
            }

            public int Finalize(int wordLength, int patternLength, int n_subwords, BitSpan isSubwordStart, out ImmutableArray<Span> matchedSpans)
            {
                matchedSpans = builder.MoveToImmutable();
                score -= 4 * (wordLength - patternLength);
                return score;
                //return CalculateScore(wordLength, n_subwords, isSubwordStart, matchedSpans);
            }

            public void AddSpan(int index, MatchedSpan span)
            {
                this.score += ScoreSpan(span);
                builder[index] = span.ToSpan();
            }

            private int ScoreSpan(MatchedSpan span)
            {
                //int effectiveLength = (4 * span.Length - 3 * span.Inexactness);
                int effectiveLength = (4 * span.Length);
                int score = 4 * effectiveLength - 3;
                score += span.IsSubwordStart ? 32 : 0;
                score -= span.Start;
                return score;
            }

            private int CalculateScore(int wordLength, int n_subwords, BitSpan isSubwordStart, ImmutableArray<Span> matchedSpans)
            {
                int n_spans = matchedSpans.Length;
                Debug.Assert(n_spans > 0);

                int i_span = 0;
                int i_subword = 0;
                int i_char = 0;

                int totalScore = 0;
                for (; i_subword < n_subwords; i_subword++)
                {
                    Span subword;
                    int subwordLength;
                    { // GetSubword
                        int subwordStart = i_char;
                        do
                        {
                            i_char++;
                        } while (i_char < wordLength && !isSubwordStart[i_char]);
                        subwordLength = i_char - subwordStart;
                        subword = new Span(subwordStart, subwordLength);
                    }

                    int cohesion = 0;
                    bool isStartMatch = false;
                    while (i_span < n_spans)
                    {
                        Span span = matchedSpans[i_span++];
                        if (span.Start >= subword.End)
                        {
                            i_span--;
                            break;
                        }
                        isStartMatch |= span.Start == subword.Start;
                        cohesion += 4 * span.Length - 3;
                    }

                    if (cohesion > 0)
                    {
                        int subwordScore = (isStartMatch ? 16 : 8) * cohesion;
                        subwordScore -= subword.Start;

                        totalScore += subwordScore;
                    }
                    else
                    {
                        totalScore -= 2 * subword.Length;
                    }
                }

                //totalScore /= n_subwords;
                //totalScore -= wordLength;

                return totalScore;
            }
        }

        private struct MatchedSpan
        {
            internal short start;
            internal byte startInPattern;
            internal byte length;
            internal byte isSubwordStart;

            public bool IsValid => length > 0;
            public int Start => start;
            public int StartInPattern => startInPattern;
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
        }
    }
}