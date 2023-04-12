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

using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System;
using System.Windows.Input;
using System.Threading;
using System.Linq;
using System.Collections.Generic;

namespace VSIntelliSenseTweaks
{
    [Export(typeof(ICommandHandler))]
    [Name(nameof(MultiSelectionCompletionHandler))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Interactive)]
    public class MultiSelectionCompletionHandler : ICommandHandler<TypeCharCommandArgs>
    {
        [Import]
        IAsyncCompletionBroker completionBroker;

        [Import]
        ITextBufferUndoManagerProvider undoManagerProvider;

        SessionController sessionController = new SessionController();

        [ImportingConstructor]
        public MultiSelectionCompletionHandler()
        {
            
        }

        public string DisplayName => nameof(MultiSelectionCompletionHandler);

        public CommandState GetCommandState(TypeCharCommandArgs args)
        {
            var textView = args.TextView;
            var textBuffer = textView.TextBuffer;

            if (!completionBroker.IsCompletionSupported(textBuffer.ContentType, textView.Roles))
                return CommandState.Unavailable;

            return CommandState.Available;
        }

        public bool ExecuteCommand(TypeCharCommandArgs args, CommandExecutionContext executionContext)
        {
            // TODO: Use binding of edit.completeWord instead of hardcoded keybinding.
            bool didAttemptCompleteWord = args.TypedChar == ' ' && (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (!didAttemptCompleteWord)
                return false;

            var textView = args.TextView;
            var textBuffer = textView.TextBuffer;

            var selectionBroker = textView.GetMultiSelectionBroker();
            if (!selectionBroker.HasMultipleSelections)
                return false;

            if (sessionController.HasSession) // We already have an active session, do not start a new one.
                return true;

            var selections = selectionBroker.AllSelections;
            var primarySelection = selectionBroker.PrimarySelection;
            int n_selections = selections.Count;
            var textVersion = textBuffer.CurrentSnapshot.Version;

            var cancellationToken = new CancellationToken();
            var trigger = new CompletionTrigger(CompletionTriggerReason.InvokeAndCommitIfUnique, textView.TextSnapshot);

            for (int i = 0; i < n_selections; i++)
            {
                var selection = selections[i];

                // Triggering a completion session only works when there is one selection.
                // So we have to make a hack where try each selection one at a time and then
                // patch up all other selections once an item was committed.
                selectionBroker.SetSelection(selection);
                var triggerPoint = selection.InsertionPoint.Position;
                var potentialSession = completionBroker.TriggerCompletion(textView, trigger, triggerPoint, cancellationToken);

                if (potentialSession == null)
                    continue;

                potentialSession.OpenOrUpdate(trigger, triggerPoint, cancellationToken);

                _ = potentialSession.GetComputedItems(cancellationToken); // This call dismisses the session if it is started in a bad location (in a string for example).

                if (textBuffer.CurrentSnapshot.Version != textVersion)
                {
                    // For some reason the text version changed due to starting a session.
                    selections = selectionBroker.AllSelections;
                    selection = selections[i];
                    Debug.WriteLine("Saved you.");
                }

                if (potentialSession.IsDismissed)
                    continue;

                // We have a good session.

                // Restore original selections, but use current selection as primary, otherwise session will terminate;
                selectionBroker.SetSelectionRange(selections, selection);

                this.sessionController.Initialize(textBuffer, undoManagerProvider, potentialSession, selectionBroker, primarySelection);

                return true;
            }

            // Restore original selections if we couldn't begin a session;
            selectionBroker.SetSelectionRange(selections, primarySelection);
            return true;
        }

        private void SelectionBroker_MultiSelectionSessionChanged(object sender, EventArgs e)
        {
            
            throw new NotImplementedException();
        }

        private class SessionController
        {
            ITextBuffer textBuffer;
            ITextBufferUndoManagerProvider undoManagerProvider;
            IAsyncCompletionSession session;
            IMultiSelectionBroker selectionBroker;

            IReadOnlyList<Selection> currentSelections;
            Selection currentPrimarySelection;
            Selection originalPrimarySelection;

            public void Initialize(
                ITextBuffer textBuffer,
                ITextBufferUndoManagerProvider undoManagerProvider,
                IAsyncCompletionSession session,
                IMultiSelectionBroker selectionBroker,
                Selection originalPrimarySelection)
            {
                Debug.Assert(!HasSession);

                this.textBuffer = textBuffer;
                this.undoManagerProvider = undoManagerProvider;
                this.session = session;
                this.selectionBroker = selectionBroker;
                this.currentSelections = selectionBroker.AllSelections;
                this.currentPrimarySelection = selectionBroker.PrimarySelection;
                this.originalPrimarySelection = originalPrimarySelection;

                session.ItemCommitted += AfterItemCommitted;
                session.Dismissed += SessionDismissed;

                selectionBroker.MultiSelectionSessionChanged += GetCurrentSelections;
            }

            public bool HasSession => session != null;
            int n_selections => currentSelections.Count;

            void GetCurrentSelections(object sender, EventArgs e)
            {
                Debug.Assert(sender == selectionBroker);
                if (!selectionBroker.HasMultipleSelections)
                {
                    // If we dont have multiSelections anymore it is likely the user committed an item and a bug set the selections to one.
                    return;
                }
                currentSelections = selectionBroker.AllSelections;
                currentPrimarySelection = selectionBroker.PrimarySelection;
            }

            void AfterItemCommitted(object sender, CompletionItemEventArgs e)
            {
                Debug.Assert(sender == session);

                int anchorOffset, activeOffset;
                PositionAffinity insertionPointAffinity;
                {
                    var afterCommitSelection = selectionBroker.PrimarySelection;

                    anchorOffset = afterCommitSelection.InsertionPoint.Position.Position
                        - afterCommitSelection.AnchorPoint.Position.Position;

                    activeOffset = afterCommitSelection.InsertionPoint.Position.Position
                        - afterCommitSelection.ActivePoint.Position.Position;

                    insertionPointAffinity = afterCommitSelection.InsertionPointAffinity;
                }

                var undoHistory = undoManagerProvider.GetTextBufferUndoManager(textBuffer).TextBufferUndoHistory;

                undoHistory.Undo(1); // Undo insertion of committed item, we will redo it for all selections under same transaction instead.

                var undoTransaction = undoHistory.CreateTransaction(nameof(MultiSelectionCompletionHandler));

                ITextSnapshot commitSnapshot = textBuffer.CurrentSnapshot;
                ITextSnapshot patchSnapshot;

                using (var edit = textBuffer.CreateEdit())
                {
                    for (int j = 0; j < n_selections; j++)
                    {
                        var selection = currentSelections[j];

                        var insertPoint = selection.InsertionPoint.TranslateTo(commitSnapshot).Position;
                        var replaceSpan = GetWordSpan(insertPoint.Position, commitSnapshot);
                        edit.Replace(replaceSpan, e.Item.InsertText);
                    }
                    patchSnapshot = edit.Apply();
                }

                var newSelections = currentSelections.Select(MakeNewSelection);
                var newPrimarySelection = MakeNewSelection(originalPrimarySelection);

                selectionBroker.SetSelectionRange(newSelections, newPrimarySelection);

                undoTransaction.Complete();

                Selection MakeNewSelection(Selection selection)
                {
                    var insertPoint = selection.InsertionPoint.TranslateTo(patchSnapshot).Position;
                    var anchorPoint = new SnapshotPoint(patchSnapshot, insertPoint.Position - anchorOffset);
                    var activePoint = new SnapshotPoint(patchSnapshot, insertPoint.Position - activeOffset);

                    return new Selection(insertPoint, anchorPoint, activePoint, insertionPointAffinity);
                }
            }

            void SessionDismissed(object sender, EventArgs e)
            {
                Debug.Assert(sender == session);

                session = null;
                selectionBroker.MultiSelectionSessionChanged -= GetCurrentSelections;
                selectionBroker.TrySetAsPrimarySelection(TranslateTo(originalPrimarySelection, selectionBroker.CurrentSnapshot,
                    PointTrackingMode.Positive, PointTrackingMode.Negative, PointTrackingMode.Positive));
            }
        }

        private static Span GetWordSpan(int position, ITextSnapshot snapshot)
        {
            int start = position;
            int length = 0;
            while (start > 0 && IsWordChar(snapshot[start - 1]))
            {
                start--;
                length++;
            }
            if (length > 0)
            {
                while (start + length < snapshot.Length && IsWordChar(snapshot[start + length]))
                {
                    length++;
                }
            }
            return new Span(start, length);

            bool IsWordChar(char c)
            {
                return char.IsLetterOrDigit(c) || c == '_';
            }
        }

        public static Selection TranslateTo(Selection selection, ITextSnapshot targetSnapshot, PointTrackingMode insertionPointTracking, PointTrackingMode anchorPointTracking, PointTrackingMode activePointTracking)
        {
            return new Selection
            (
                selection.InsertionPoint.TranslateTo(targetSnapshot, insertionPointTracking),
                selection.AnchorPoint.TranslateTo(targetSnapshot, anchorPointTracking),
                selection.ActivePoint.TranslateTo(targetSnapshot, activePointTracking),
                selection.InsertionPointAffinity
            );
        }
    }
}
