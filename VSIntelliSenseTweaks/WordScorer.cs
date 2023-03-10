using VSIntelliSenseTweaks.Utilities;
using Microsoft;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;

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
            Span<int> subwordData = stackalloc int[BitSpan.GetRequiredIntCount(word.Length)];
            var isSubwordStart = new BitSpan(subwordData);
            DetermineSubwords(word, isSubwordStart);

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
                return pendingResult.Finalize(out matchedSpans);
            }
            else
            {
                matchedSpans = default;
                return 0;
            }
        }

        private void DetermineSubwords(ReadOnlySpan<char> word, BitSpan isSubwordStart)
        {
            Span<CharKind> charKinds = word.Length <= 1024 ? stackalloc CharKind[word.Length] : new CharKind[word.Length];
            int n_chars = word.Length;

            for (int i = 0; i < n_chars; i++)
            {
                charKinds[i] = new CharKind(word[i]);
            }

            for (int i = 0; i < n_chars; i++)
            {
                bool isSubwordBeginning = i == 0
                  || (!charKinds[i - 1].IsUpper  && charKinds[i].IsUpper)
                  || (!charKinds[i - 1].IsLetter && charKinds[i].IsLetter)
                  || (i + 1 < n_chars && charKinds[i].IsUpper && !charKinds[i + 1].IsUpper);

                if (isSubwordBeginning)
                {
                    isSubwordStart.SetBit(i);
                }
            }
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

        /// <returns> Length of the matched spans array, or 0 if fail. </returns>
        private bool Recurse(State state)
        {
            int i_final = state.word.Length - state.pattern.Length;
            int stackIndex = workStack.count;
            int pushCount = 0;
            for (int i = 0; i <= i_final; i++)
            {
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
            private ImmutableArray<Span>.Builder builder;
            private int farness; // Distance from start;
            private int inexactness; // Number of non-exact char matches.
            private int disjointness; // Number of spans.

            public PendingResult(int n_matchedSpans)
            {
                Debug.Assert(n_matchedSpans > 0);

                this.builder = ImmutableArray.CreateBuilder<Span>(n_matchedSpans);
                builder.Count = builder.Capacity;
                this.farness = 0;
                this.inexactness = 0;
                this.disjointness = 0;
            }

            public int Finalize(out ImmutableArray<Span> matchedSpans)
            {
                matchedSpans = builder.MoveToImmutable();
                return CalculateScore();
            }

            public void AddSpan(int index, MatchedSpan span)
            {
                builder[index] = span.ToSpan();
                farness      += span.Start;
                inexactness  += span.Inexactness;
                disjointness += span.IsSubwordStart ? 1 : 2;
            }

            public int CalculateScore()
            {
                farness = Math.Min(farness, (1 << 13) - 1);

                Debug.Assert(0 <= farness      && farness      <  (1 << 13)); // Needs 13 bits
                Debug.Assert(0 <= inexactness  && inexactness  <= (1 << 8 )); // Needs 9  bits, since it can be equal to 2^8
                Debug.Assert(1 <= disjointness && disjointness <  (1 << 9 )); // Needs 9  bits
                                                                              // Total 31 bits (cant use last sign bit)

                int score = (farness      <<  0      )
                          + (inexactness  <<  13     )
                          + (disjointness << (13 + 9));

                Debug.Assert(score > 0);

                return score;
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