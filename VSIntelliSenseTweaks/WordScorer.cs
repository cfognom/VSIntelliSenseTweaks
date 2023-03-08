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
    // TODO: Make score from unexpectedness.
    public struct WordScorer
    {
        private struct MatchedSpan
        {
            public Span span;
            public short inexactness;
            public bool isSubwordStart;
        }

        LightStack<MatchedSpan> workStack;
        MatchedSpan[] stagedSpans;
        //CharKind[] charKinds;
        //BitArray isSubwordStarts;

        public WordScorer(int maxWordLength)
        {
            this.workStack = new LightStack<MatchedSpan>(64);
            this.stagedSpans = new MatchedSpan[maxWordLength];
            //this.charKinds = new CharKind[maxWordLength];
            //this.isSubwordStarts = new BitArray(maxWordLength);
        }

        public int ScoreWord(ReadOnlySpan<char> pattern, ReadOnlySpan<char> word, out ImmutableArray<Span> matchedSpans)
        {
            Span<int> ints = stackalloc int[BitSpan.GetRequiredIntCount(word.Length)];
            var isSubwordStart = new BitSpan(ints);
            DetermineSubwords(word, isSubwordStart);

            int result = Recurse(new State
            {
                pattern = pattern,
                word = word,
                isSubwordStart = isSubwordStart,
                wordPosition = 0,
                stagedSpansCount = 0
            });


            if (result == 0)
            {
                matchedSpans = default;
                return 0;
            }
            else
            {
                int stagedSpansCount = result;
                var builder = ImmutableArray.CreateBuilder<Span>(stagedSpansCount);
                for (int i = 0; i < stagedSpansCount; i++)
                {
                    builder.Add(stagedSpans[i].span);
                }
                matchedSpans = builder.MoveToImmutable();
                return CalculateScore(stagedSpansCount);
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
            public int stagedSpansCount;
        }

        // For each position in word try to match pattern.
        // If pattern was (partially) matched push matched span to stack.
        // Sort the pushed spans so that the best span comes last.
        // Pop the recently pushed spans in sorted order.
        // If popped span covers all of pattern => success.
        // Else, reduce word and pattern and call method again (recursive).

        /// <returns> Length of the matched spans array, or 0 if fail. </returns>
        private int Recurse(State state)
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
                var popped = workStack.Pop();
                stagedSpans[state.stagedSpansCount] = popped;

                if (popped.span.Length == state.pattern.Length)
                {
                    return state.stagedSpansCount + 1;
                }

                int nextWordPosition = popped.span.End;
                int localNextWordPosition = nextWordPosition - state.wordPosition;
                var result = Recurse(new State
                {
                    pattern = state.pattern.Slice(popped.span.Length),
                    word = state.word.Slice(localNextWordPosition),
                    isSubwordStart = state.isSubwordStart,
                    wordPosition = nextWordPosition,
                    stagedSpansCount = state.stagedSpansCount + 1
                });
                if (result > 0) return result;
            }

            return 0;
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

            return new MatchedSpan
            {
                span = new Span(wordPosition, i),
                inexactness = (short)inexactness,
                isSubwordStart = isSubwordStart[wordPosition]
            };
        }

        private struct BestSpanLast : IComparer<MatchedSpan>
        {
            public int Compare(MatchedSpan x, MatchedSpan y)
            {
                int comp = (x.isSubwordStart ? 1 : 0) - (y.isSubwordStart ? 1 : 0);
                if (comp == 0)
                {
                    comp = x.span.Length - y.span.Length;
                }
                if (comp == 0)
                {
                    comp = y.inexactness - x.inexactness;
                }
                if (comp == 0)
                {
                    comp = y.span.Start - x.span.Start;
                }
                return comp;
            }
        }

        private int CalculateScore(int stagedSpansCount)
        {
            Debug.Assert(stagedSpansCount > 0);

            int farness; // Distance from start;
            int inexactness; // Number of non-exact char matches.
            int disjointness; // Number of spans.

            farness = -CalculateMinFarness();
            int CalculateMinFarness()
            {
                int v = stagedSpansCount - 1;
                return (v * v + v) / 2;
            }

            inexactness = 0;

            disjointness = stagedSpansCount;

            for (int i = 0; i < stagedSpansCount; i++)
            {
                var spanData = stagedSpans[i];
                farness     += spanData.span.Start;
                inexactness += spanData.inexactness;
                disjointness += spanData.isSubwordStart ? 0 : 1;
            }

            Assumes.True(0 <= farness && farness < (1 << 8));
            Assumes.True(0 <= inexactness && inexactness <= (1 << 8));
            Assumes.True(0 <= disjointness && disjointness <= (1 << 8));

            int score = (farness      <<  0)
                        + (inexactness  <<  8)
                        + (disjointness << (8 + 9));

            Debug.Assert(score > 0);

            return score;
        }
    }
}