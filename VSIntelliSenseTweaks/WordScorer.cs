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
        LightStack<MatchedSpan> workStack;
        MatchedSpan[] stagedSpans;

        public WordScorer(int maxWordLength)
        {
            this.workStack = new LightStack<MatchedSpan>(2048);
            this.stagedSpans = new MatchedSpan[maxWordLength];
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
                    builder.Add(stagedSpans[i].ToSpan());
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

                if (popped.Length == state.pattern.Length)
                {
                    return state.stagedSpansCount + 1;
                }

                int nextWordPosition = popped.End;
                int localNextWordPosition = nextWordPosition - state.wordPosition;
                var result = Recurse(new State
                {
                    pattern = state.pattern.Slice(popped.Length),
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
                farness     += spanData.Start;
                inexactness += spanData.Inexactness;
                disjointness += spanData.IsSubwordStart ? 0 : 1;
            }

            Assumes.True(0 <= farness && farness < (1 << 13));
            Assumes.True(0 <= inexactness && inexactness <= (1 << 8));
            Assumes.True(0 <= disjointness && disjointness <= (1 << 8));

            int score = (farness      <<  0)
                      + (inexactness  <<  13)
                      + (disjointness << (13 + 9));

            Debug.Assert(score > 0);

            return score;
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