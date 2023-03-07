using EnvDTE;
using Microsoft;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System;
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
using VSIntelliSenseTweaks.Utilities;
using System.Collections;

namespace VSIntelliSenseTweaks
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

    internal partial class CompletionItemManager : IAsyncCompletionItemManager
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

        const int patternMaxLength = 256;

        CompletionItemWithHighlight[] filteredCompletions;
        CompletionItemKey[] completionKeys;
        WordScorer scorer;

        FilteredCompletionModel completionModel;

        int maxFilterLength;
        int matchCount;

        Task<FilteredCompletionModel> updateTask;

        private void PerformInitialAllocations(int n_completions, int maxCharCount)
        {
            Debug.Assert(filteredCompletions == null); // Check that we did not allocate already.
            this.filteredCompletions = new CompletionItemWithHighlight[n_completions];
            this.completionKeys      = new CompletionItemKey[n_completions];
            this.scorer = new WordScorer(maxCharCount);
        }

        //private void DetermineSubwords(ImmutableArray<CompletionItem> completions)
        //{
        //    Span<CharKind> charKinds = stackalloc CharKind[patternMaxLength];
        //    for (int i = 0; i < completions.Length; i++)
        //    {
        //        var word = completions[i].FilterText.AsSpan(0, patternMaxLength);
        //        subwords[i] = DetermineSubwords(word, charKinds, subwordBeginnings);
        //    }
        //}

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


            var builder = ImmutableArray.CreateBuilder<CompletionItem>(n_completions);
            int maxCharCount = 0;
            for (int i = 0; i < n_completions; i++)
            {
                var completion = completions[i];
                builder.Add(completion);
                maxCharCount = Math.Max(maxCharCount, completion.FilterText.Length);
            }
            builder.Sort(new InitialComparer());
            var initialSortedItems = builder.MoveToImmutable();

            PerformInitialAllocations(n_completions, maxCharCount);

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
            var patternText = session.ApplicableToSpan.GetText(data.Snapshot);
            bool hasPattern = patternText.Length > 0;
            if (hasPattern)
            {
                var pattern = patternText.AsSpan(0, Math.Min(patternText.Length, patternMaxLength));
                matchCount = 0;
                for (int i = 0; i < n_completions; i++)
                {
                    var completion = completions[i];
                    var word = completion.FilterText.AsSpan();
                    int wordScore = scorer.ScoreWord(pattern, word, out var matchedSpans);
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
    }
}