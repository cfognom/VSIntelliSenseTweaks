using Microsoft.VisualStudio.Text;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using VSIntelliSenseTweaks.Utilities;

namespace VSIntelliSenseTweaks
{
    public static class WordScorer
    {
        public static int ScoreWord(ReadOnlySpan<char> word, ReadOnlySpan<char> pattern, out ImmutableArray<Span> matchedSpans)
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
                n_subwords = n_subwords,
                spanCount = 0,
            };

            if (FindMatchingSpans(ref data))
            {
                //Span<Span> subwordSpans = n_subwords <= 128 ? stackalloc Span[n_subwords] : new Span[n_subwords];
                //PopulateSubwords(wordLength, isSubwordStart, subwordSpans);
                int score = CompileSpans(ref data, out matchedSpans);
                return score;
            }
            else
            {
                matchedSpans = default;
                return int.MinValue;
            }
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
            public Span<byte> charToSpan;
            public Span<MatchedSpan> spans;
            public BitSpan isSubwordStart;
            public int n_subwords;
            public byte spanCount;

            public int GetSpanIndex(int charIndex) => charToSpan[charIndex];
            public void SetSpan(MatchedSpan span, byte index)
            {
                spans[index] = span;
                for (int i = span.StartInPattern; i < span.EndInPattern; i++)
                {
                    charToSpan[i] = index;
                }
            }
        }

        static bool FindMatchingSpans(ref PatternMatchingData data)
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
                    bool isSubwordStart = data.isSubwordStart[startInWord];
                    var spanIndex = data.charToSpan[startInPattern];
                    ref var span = ref data.spans[spanIndex];
                    int stealCount = span.EndInPattern - startInPattern;
                    Debug.Assert(stealCount > 0);
                    bool shouldSteal = span.IsSubwordStart ?
                        (isSubwordStart ? span.Length > stealCount || span.Length < length : false ) :
                        (isSubwordStart ? true : span.Length < length);
                    if (shouldSteal)
                    {
                        if (span.Length > stealCount)
                        {
                            span.Length -= stealCount;
                            spanIndex++;
                        }
                        var newSpan = new MatchedSpan(startInWord, startInPattern, length, isSubwordStart);
                        data.SetSpan(newSpan, spanIndex);
                        data.spanCount = ++spanIndex;
                        break;
                    }
                    bCount -= stealCount;
                    Debug.Assert(bCount >= 0);
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

            return success;
        }

        static int MatchForward(PatternMatchingData state, int wordStartIndex, int patternStartIndex, out bool isSubwordStartAhead)
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

        static int MatchBackward(PatternMatchingData state, int wordStartIndex, int patternStartIndex)
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

        static int CompileSpans(ref PatternMatchingData data, out ImmutableArray<Span> matchedSpans)
        {
            int n_spans = data.spanCount;
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

        private struct MatchedSpan
        {
            short start;
            byte startInPattern;
            byte length;
            byte isSubwordStart;

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