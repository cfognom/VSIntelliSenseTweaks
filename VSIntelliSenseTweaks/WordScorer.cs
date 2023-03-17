using Microsoft.VisualStudio.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using VSIntelliSenseTweaks.Utilities;

namespace VSIntelliSenseTweaks
{
    public struct WordScorer
    {
        LightStack<MatchedSpan> workStack;
        PendingResult pendingResult;

        public WordScorer(int stackInitialCapacity = 4096)
        {
            this.workStack = new LightStack<MatchedSpan>(stackInitialCapacity);
            this.pendingResult = default;
        }

        public int ScoreWord(ReadOnlySpan<char> pattern, ReadOnlySpan<char> word, out ImmutableArray<Span> matchedSpans)
        {
            Debug.Assert(pattern.Length > 0);

            Span<int> subwordData = stackalloc int[BitSpan.GetRequiredIntCount(word.Length)];
            var isSubwordStart = new BitSpan(subwordData);
            int n_subwords = DetermineSubwords(word, isSubwordStart);

            var state = new State
            {
                pattern = pattern,
                word = word,
                isSubwordStart = isSubwordStart,
                wordPosition = 0,
                n_matchedSpans = 0
            };

            if (Recurse(state))
            {
                return pendingResult.Finalize(word.Length, pattern.Length, n_subwords, isSubwordStart, out matchedSpans);
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

                if (isSubwordBeginning)
                {
                    isSubwordStart.SetBit(i);
                    n_subwords++;
                }
            }
            return n_subwords;
        }

        private ref struct State
        {
            public ReadOnlySpan<char> pattern;
            public ReadOnlySpan<char> word;
            public BitSpan isSubwordStart;
            public int wordPosition;
            public int n_matchedSpans;
        }

        // For each position in word try to match pattern.
        // If pattern was (partially) matched push matched span to stack.
        // Sort the pushed spans so that the best span comes last.
        // Pop the recently pushed spans in sorted order.
        // If popped span covers all of pattern => success.
        // Else, reduce word and pattern and call method again (recursive).

        private bool Recurse(State state)
        {
            int i_final = state.word.Length - state.pattern.Length;
            int stackIndex = workStack.count;
            int pushCount = 0;
            for (int i = 0; i <= i_final; i++)
            {
                //TODO: Change to slide over approach.
                if (char.ToLower(state.pattern[0]) == char.ToLower(state.word[i]))
                {
                    var slice = state.word.Slice(i);
                    var matchedSpan = CreateMatchedSpan(state.pattern, slice, state.isSubwordStart, state.wordPosition + i);
                    workStack.Push(matchedSpan);
                    pushCount++;
                }
            }

            Array.Sort(workStack.array, stackIndex, pushCount, new BestSpanLast());

            while (workStack.count > stackIndex)
            {
                var bestSpan = workStack.Pop();

                if (bestSpan.Length == state.pattern.Length)
                {
                    // Whole pattern matched.
                    pendingResult = new PendingResult(state.n_matchedSpans + 1);
                    pendingResult.AddSpan(state.n_matchedSpans, bestSpan);
                    return true;
                }

                int nextWordPosition = bestSpan.End;
                int localNextWordPosition = nextWordPosition - state.wordPosition;
                var newState = new State
                {
                    pattern = state.pattern.Slice(bestSpan.Length),
                    word = state.word.Slice(localNextWordPosition),
                    isSubwordStart = state.isSubwordStart,
                    wordPosition = nextWordPosition,
                    n_matchedSpans = state.n_matchedSpans + 1,
                };
                if (Recurse(newState))
                {
                    pendingResult.AddSpan(state.n_matchedSpans, bestSpan);
                    return true;
                }
            }

            return false;
        }

        private MatchedSpan CreateMatchedSpan(ReadOnlySpan<char> pattern, ReadOnlySpan<char> word, BitSpan isSubwordStart, int wordPosition)
        {
            Debug.Assert(pattern.Length <= word.Length);

            int inexactness = pattern[0] == word[0] ? 0 : 1;
            int i = 1;
            while (i < pattern.Length && !isSubwordStart[wordPosition + i] && char.ToLower(pattern[i]) == char.ToLower(word[i]))
            {
                if (pattern[i] != word[i])
                {
                    inexactness++;
                }
                i++;
            }

            return new MatchedSpan(wordPosition, i, inexactness, isSubwordStart[wordPosition]);
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
                if (comp == 0)
                {
                    comp = y.Inexactness - x.Inexactness;
                }
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
                int effectiveLength = (4 * span.Length - 3 * span.Inexactness);
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

        private readonly struct MatchedSpan
        {
            readonly short start;
            readonly byte length;
            readonly byte inexactnessAndIsSubwordStart;

            const int isSubwordStartMask = 128; // last bit in byte.
            const int maxInexactness = isSubwordStartMask - 1;

            public int Start => start;
            public int End => start + length;
            public int Length => length;
            public int Inexactness => inexactnessAndIsSubwordStart & maxInexactness;
            public bool IsSubwordStart => (inexactnessAndIsSubwordStart & isSubwordStartMask) == isSubwordStartMask;
            public int IsSubwordStart_AsInt => (inexactnessAndIsSubwordStart >> 7);

            public MatchedSpan(int start, int length, int inexactness, bool isSubwordStart)
            {
                this.start = (short)start;
                this.length = (byte)length;
                Debug.Assert(inexactness <= maxInexactness);
                int _isSubwordStart = (isSubwordStart ? isSubwordStartMask : 0);
                this.inexactnessAndIsSubwordStart = (byte)(inexactness | _isSubwordStart);
            }

            public Span ToSpan()
            {
                return new Span(start, length);
            }
        }
    }
}