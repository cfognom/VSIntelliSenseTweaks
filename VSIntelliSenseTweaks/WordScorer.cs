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
        MatchedSpan[] matchedSpans;
        int n_matchedSpans;
        //LightStack<MatchedSpan> workStack;
        PendingResult pendingResult;

        public WordScorer(int maxPatternLength)
        {
            //this.workStack = new LightStack<MatchedSpan>(stackInitialCapacity);
            this.matchedSpans = new MatchedSpan[maxPatternLength];
            this.n_matchedSpans = default;
            this.pendingResult = default;
        }

        public int ScoreWord(ReadOnlySpan<char> pattern, ReadOnlySpan<char> word, out ImmutableArray<Span> matchedSpans)
        {
            Debug.Assert(pattern.Length > 0);

            int n_ints = BitSpan.GetRequiredIntCount(word.Length);
            Span<int> ints = n_ints <= 256 ? stackalloc int[n_ints] : new int[n_ints];
            var isSubwordStart = new BitSpan(ints);
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

                // TODO: Set each bit to ensure initialized.
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
            //public ReadOnlySpan<char> antiPattern;
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
            n_matchedSpans = 0;
            int patternLength = state.pattern.Length;
            int iw_final = state.word.Length - patternLength;
            int ip_end = patternLength;
            //Span<short> patternToWord = stackalloc short[patternLength];
            //int stackIndex = workStack.count;
            //int pushCount = 0;
            int n_matchedInPattern = 0;
            for (int iw = 0; iw <= iw_final; iw++)
            {
                //TODO: Change to slide over approach.
                //TODO: make two spans, one original and one with case inverted.
                var potentialSpan = new MatchedSpan { start = -1 };
                int ip = 0;
                while (true)
                {
                    bool isCharMatch = FuzzyCharEquals(state.pattern[ip], state.word[iw + ip]);
                    if (isCharMatch)
                    {
                        if (!HasOpenSpan())
                        {
                            OpenSpan(state);
                        }
                        n_matchedInPattern = Math.Max(n_matchedInPattern, ip + 1);
                    }
                    else
                    {
                        if (HasOpenSpan())
                        {
                            CloseSpan(matchedSpans, ref n_matchedSpans, state);
                        }
                    }

                    ip++;

                    if (ip > n_matchedInPattern)
                    {
                        DiscardSpan();
                        break;
                    }
                    if (ip == ip_end)
                    {
                        if (HasOpenSpan())
                        {
                            CloseSpan(matchedSpans, ref n_matchedSpans, state);
                        }
                        break;
                    }
                    if (isCharMatch && state.isSubwordStart[iw + ip])
                    {
                        CloseSpan(matchedSpans, ref n_matchedSpans, state);
                    }

                    bool HasOpenSpan() => potentialSpan.start != -1;

                    void OpenSpan(State s)
                    {
                        int start = iw + ip;
                        potentialSpan.start = (short)start;
                        potentialSpan.isSubwordStart = s.isSubwordStart[start] ? (byte)1 : (byte)0;
                    }

                    void DiscardSpan()
                    {
                        potentialSpan.start = -1;
                    }

                    void CloseSpan(MatchedSpan[] spans, ref int n_spans, State s)
                    {
                        potentialSpan.length = (byte)(iw + ip - potentialSpan.start);
                        potentialSpan.startInPattern = (byte)(potentialSpan.start - iw);
                        if (potentialSpan.EndInPattern < n_matchedInPattern)
                        {
                            DiscardSpan();
                            return;
                        }
                        if (n_spans == 0)
                        {
                            spans[n_spans++] = potentialSpan;
                            return;
                        }

                        while (true)
                        {
                            // TODO: fix: word: [Se]rial[Rialsoap] pattern: serialsoap
                            ref var peeked = ref spans[n_spans - 1];
                            if (peeked.EndInPattern <= potentialSpan.StartInPattern)
                            {
                                spans[n_spans++] = potentialSpan;
                                break;
                            }
                            if (peeked.length >= potentialSpan.length)
                            {
                                var excess = peeked.EndInPattern - potentialSpan.StartInPattern;
                                potentialSpan.start += (short)excess;
                                potentialSpan.startInPattern += (byte)excess;
                                potentialSpan.length -= (byte)excess;
                                spans[n_spans++] = potentialSpan;
                                break;
                            }
                            int lengthDiff = potentialSpan.StartInPattern - peeked.StartInPattern;
                            if (lengthDiff >= 0)
                            {
                                if (lengthDiff == 0)
                                {
                                    n_spans--;
                                }
                                else
                                {
                                    peeked.length = (byte)lengthDiff;
                                }
                                spans[n_spans++] = potentialSpan;
                                break;
                            }
                            n_spans--;
                        };
                        if (n_spans == 0)
                        {
                            throw new Exception();
                        }
                    }
                }

                //if (FastCharEquals(state.pattern[0], state.word[i]))
                ////if (state.pattern[0].Equals(state.word[i]))
                //{
                //    var slice = state.word.Slice(i);
                //    var matchedSpan = CreateMatchedSpan(state.pattern, slice, state.isSubwordStart, state.wordPosition + i);
                //    workStack.Push(matchedSpan);
                //    pushCount++;
                //}
            }

            bool success = n_matchedInPattern == patternLength;

            if (success)
            {
                Debug.Assert(n_matchedSpans > 0);
                this.pendingResult = new PendingResult(n_matchedSpans);
                for (int i = 0; i < n_matchedSpans; i++)
                {
                    pendingResult.AddSpan(i, matchedSpans[i]);
                }
            }

            return success;

            //Array.Sort(workStack.array, stackIndex, pushCount, new BestSpanLast());

            //while (workStack.count > stackIndex)
            //{
            //    var bestSpan = workStack.Pop();

            //    if (bestSpan.Length == state.pattern.Length)
            //    {
            //        // Whole pattern matched.
            //        pendingResult = new PendingResult(state.n_matchedSpans + 1);
            //        pendingResult.AddSpan(state.n_matchedSpans, bestSpan);
            //        return true;
            //    }

            //    int nextWordPosition = bestSpan.End;
            //    int localNextWordPosition = nextWordPosition - state.wordPosition;
            //    var newState = new State
            //    {
            //        pattern = state.pattern.Slice(bestSpan.Length),
            //        antiPattern = state.antiPattern.Slice(bestSpan.Length),
            //        word = state.word.Slice(localNextWordPosition),
            //        isSubwordStart = state.isSubwordStart,
            //        wordPosition = nextWordPosition,
            //        n_matchedSpans = state.n_matchedSpans + 1,
            //    };
            //    if (Recurse(newState))
            //    {
            //        pendingResult.AddSpan(state.n_matchedSpans, bestSpan);
            //        return true;
            //    }
            //}

            //return false;
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
            public int Length => length;
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