using EnvDTE;
using Microsoft;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;

namespace IntellisenseTweaks
{
    [Export(typeof(IAsyncCompletionItemManagerProvider))]
    [ContentType("CSharp")]
    [Name("CustomCompletionItemManagerProvider")]
    internal class CompletionItemManagerProvider : IAsyncCompletionItemManagerProvider
    {
        public IAsyncCompletionItemManager GetOrCreate(ITextView textView)
        {
            return new CompletionItemManager();
        }
    }

    internal class CompletionItemManager : IAsyncCompletionItemManager
    {
        private struct CompletionItemKey : IComparable<CompletionItemKey>
        {
            public int score;
            public int index;
            //public Subwords subwords;

            public int CompareTo(CompletionItemKey other)
            {
                int comp = score - other.score;
                if (comp == 0) // If score is same, preserve initial ordering.
                {
                    comp = index - other.index;
                }
                return comp;
            }
        }

        private struct Subword
        {
            public int startIndex;
            public int length;

            public ReadOnlySpan<char> SliceOf(ReadOnlySpan<char> word)
            {
                return word.Slice(startIndex, length);
            }
        }

        private struct Subwords
        {
            public int startIndex; // Start index in subwordBeginnings array.
            public int length; // Length in subwordBeginnings array.

            public void GetSubwords(ReadOnlySpan<char> word, List<byte> subwordBeginnings, Span<Subword> subwords)
            {
                Debug.Assert(subwords.Length == length);
                for (int i = 0; i < length; i++)
                {
                    int subwordStart = subwordBeginnings[startIndex + i];
                    int subwordStop = i + 1 == length ? word.Length : subwordBeginnings[startIndex + i + 1];

                    subwords[i] = new Subword
                    {
                        startIndex = subwordStart,
                        length = subwordStop - subwordStart
                    };
                }
            }
        }

        const int filterMaxLength = 256;

        CompletionItemWithHighlight[] filteredCompletions;
        CompletionItemKey[] completionKeys;
        Subwords[] subwords;
        List<byte> subwordBeginnings;

        FilteredCompletionModel completionModel;

        int matchCount;

        Task<FilteredCompletionModel> updateTask;

        internal struct CharKind
        {
            private const byte isLetter = 1;
            private const byte isUpper  = 2;

            internal byte flags;

            public CharKind(char c)
            {
                this.flags = default;
                flags |= char.IsLetter(c) ? isLetter : default;
                flags |= char.IsUpper(c)  ? isUpper  : default;
            }

            public bool IsLetter => (flags & isLetter) != 0;
            public bool IsUpper  => (flags & isUpper)  != 0;

        }

        private void PerformInitialAllocations(int n_completions)
        {
            Debug.Assert(filteredCompletions == null); // Check that we did not allocate already.
            this.filteredCompletions = new CompletionItemWithHighlight[n_completions];
            this.completionKeys      = new CompletionItemKey[n_completions];
            //this.subwords            = new Subwords[n_completions];
            //this.subwordBeginnings   = new List<byte>(n_completions * 8); // Initial guess of 8 subwords per completion.
        }

        private void DetermineSubwords(ImmutableArray<CompletionItem> completions)
        {
            Span<CharKind> charKinds = stackalloc CharKind[filterMaxLength];
            for (int i = 0; i < completions.Length; i++)
            {
                var word = completions[i].FilterText.AsSpan(0, filterMaxLength);
                subwords[i] = DetermineSubwords(word, charKinds, subwordBeginnings);
            }
        }
        private static Subwords DetermineSubwords(ReadOnlySpan<char> word, Span<CharKind> charKinds, List<byte> subwordBeginnings)
        {
            var subwords = new Subwords { startIndex = subwordBeginnings.Count };
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
                    subwordBeginnings.Add((byte)i);
                    subwords.length += 1;
                }
            }

            return subwords;
        }

        public Task<ImmutableArray<CompletionItem>> SortCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionInitialDataSnapshot data, CancellationToken token)
        {
            var sortTask = Task.Factory.StartNew(
                () => SortCompletionList(session, data, token),
                token,
                TaskCreationOptions.None,
                TaskScheduler.Current
            );
            return sortTask;
        }

        public ImmutableArray<CompletionItem> SortCompletionList(IAsyncCompletionSession session, AsyncCompletionSessionInitialDataSnapshot data, CancellationToken token)
        {
            var completions = data.InitialItemList;
            int n_completions = completions.Count;

            PerformInitialAllocations(n_completions);

            var builder = ImmutableArray.CreateBuilder<CompletionItem>(n_completions);
            for (int i = 0; i < n_completions; i++)
            {
                builder.Add(completions[i]);
            }
            builder.Sort(new InitialComparer());
            var initialSortedItems = builder.MoveToImmutable();

            //DetermineSubwords(initialSortedItems);
            
//#if !DEBUG
//            // Manual update to hard-select top entry.
//            session.OpenOrUpdate(data.Trigger, session.ApplicableToSpan.GetStartPoint(data.Snapshot), token);
//#endif
            
            return initialSortedItems;
        }

        public Task<FilteredCompletionModel> UpdateCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data, CancellationToken token)
        {
            Assumes.True(
                updateTask == null || updateTask.IsCompleted,
                "Tried to update completion list while previous update was not completed."
            );

            updateTask = Task.Factory.StartNew(
                () => UpdateCompletionList(session, data, token),
                token,
                TaskCreationOptions.None,
                TaskScheduler.Current
            );
            return updateTask;
        }

        public FilteredCompletionModel UpdateCompletionList(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data, CancellationToken token)
        {
            var completions = data.InitialSortedItemList;
            int n_completions = completions.Count;
            int preselectionIndex = -1;
            var pattern = session.ApplicableToSpan.GetText(data.Snapshot);
            bool hasPattern = pattern.Length > 0;
            if (hasPattern)
            {
                var scorer = new WordScorer(pattern.AsSpan(0, Math.Min(pattern.Length, filterMaxLength)));
                matchCount = 0;
                for (int i = 0; i < n_completions; i++)
                {
                    var completion = completions[i];
                    var word = completion.FilterText.AsSpan();
                    int wordScore = scorer.ScoreWord(word, out var matchedSpans);
                    if (wordScore > 0)
                    {
                        filteredCompletions[matchCount] = new CompletionItemWithHighlight(completion, matchedSpans);
                        completionKeys[matchCount] = new CompletionItemKey { score = wordScore, index = i };
                        matchCount++;
                    }
                }

                Array.Sort(completionKeys, filteredCompletions, 0, matchCount);
            }
            else
            {
                for (int i = 0; i < n_completions; i++)
                {
                    var completion = completions[i];
                    if (preselectionIndex == -1 && completion.IsPreselected) preselectionIndex = i;
                    filteredCompletions[i] = new CompletionItemWithHighlight(completion);
                }
                matchCount = n_completions;
            }

            int selectionIndex = Math.Max(preselectionIndex, 0);

            if (matchCount > 0)
            {
                completionModel = new FilteredCompletionModel
                (
                    ImmutableArray.Create(filteredCompletions, 0, matchCount),
                    selectionIndex,
                    data.SelectedFilters,
                    hasPattern ? UpdateSelectionHint.Selected : UpdateSelectionHint.SoftSelected,
                    centerSelection: false,
                    null
                );
            }
            token.ThrowIfCancellationRequested();
            return completionModel;
        }

        struct InitialComparer : IComparer<CompletionItem>
        {
            public int Compare(CompletionItem x, CompletionItem y)
            {
                int comp = string.Compare(x.SortText, y.SortText, ignoreCase: false);
                //if (comp == 0)
                //{
                //    comp = string.Compare(x, y, ignoreCase: false);
                //}
                return comp;
            }
        }

        public ref struct WordScorer
        {
            ReadOnlySpan<char> pattern;
            List<Span> stack;
            Span[] tempSpans;

            public WordScorer(ReadOnlySpan<char> pattern)
            {
                Debug.Assert(pattern != null && pattern.Length > 0);
                this.pattern = pattern;
                this.stack = new List<Span>(64);
                this.tempSpans = new Span[pattern.Length];
            }

            public int ScoreWord(ReadOnlySpan<char> word, out ImmutableArray<Span> matchedSpans)
            {
                matchedSpans = Recurse(new State
                {
                    pattern = this.pattern,
                    word = word,
                    wordPosition = 0,
                    tempSpansCount = 0
                });
                if (matchedSpans.IsDefault) return 0;

                return CalculateScore(matchedSpans);
            }

            private ref struct State
            {
                public ReadOnlySpan<char> pattern;
                public ReadOnlySpan<char> word;
                public int wordPosition;
                public int tempSpansCount;
            }

            // take first char in pattern
            // find that char in word and note position in list
            // for each position try to fit pattern, add length of fit to list
            // pick position that fits most of pattern
            // if no fit go back and pick next best item in list.
            // reduce pattern, repeat (recursive)
            private ImmutableArray<Span> Recurse(State state)
            {
                int i_final = state.word.Length - state.pattern.Length;
                int sortIndex = stack.Count;
                int sortCount = 0;
                for (int i = 0; i <= i_final; i++)
                {
                    var slice = state.word.Slice(i);
                    int matchCount = TryMatch(slice, state.pattern);
                    if (matchCount > 0)
                    {
                        var matchSpan = new Span(state.wordPosition + i, matchCount);
                        if (matchCount == state.pattern.Length)
                        {
                            tempSpans[state.tempSpansCount++] = matchSpan;
                            return ImmutableArray.Create(tempSpans, 0, state.tempSpansCount);
                        }
                        stack.Add(matchSpan);
                        sortCount++;
                    }
                }

                stack.Sort(sortIndex, sortCount, new SpanComparer());

                while (stack.Count > sortIndex)
                {
                    var popped = Pop(stack);

                    tempSpans[state.tempSpansCount] = popped;
                    int nextWordPosition = popped.End;
                    int localNextWordPosition = nextWordPosition - state.wordPosition;
                    var matchedSpans = Recurse(new State
                    {
                        pattern = state.pattern.Slice(popped.Length),
                        word = state.word.Slice(localNextWordPosition),
                        wordPosition = nextWordPosition,
                        tempSpansCount = state.tempSpansCount + 1
                    });
                    if (!matchedSpans.IsDefault) return matchedSpans;
                }

                return default;

                Span Pop(List<Span> stack)
                {
                    int lastIndex = stack.Count - 1;
                    var popped = stack[lastIndex];
                    stack.RemoveAt(lastIndex);
                    return popped;
                }
            }

            private int TryMatch(ReadOnlySpan<char> word, ReadOnlySpan<char> pattern, BitArray isSubWordStart = default)
            {
                Debug.Assert(word.Length >= pattern.Length);
                int i = 0;
                while (i < pattern.Length && char.ToLower(word[i]) == char.ToLower(pattern[i]))
                {
                    i++;
                }
                return i;
            }

            private struct SpanComparer : IComparer<Span>
            {
                public int Compare(Span x, Span y)
                {
                    int comp = y.Length - x.Length;
                    if (comp == 0)
                    {
                        comp = x.Start - y.Start;
                    }
                    return comp;
                }
            }

            private int CalculateScore(ImmutableArray<Span> spans)
            {
                int score = 0;
                for (int i = 0; i < spans.Length; i++)
                {
                    var span = spans[i];
                    score += span.Start;
                }
                const int factor = 1 << 15;
                Assumes.True(score < factor);
                score += factor * spans.Length;
                return score;
            }

            private static int GetCharScore(char completionChar, char patternChar)
            {
                if (completionChar == patternChar)
                    return 2;
                else if (char.ToLower(completionChar) == char.ToLower(patternChar))
                    return 1;
                else
                    return 0;
            }
        }
    }
}