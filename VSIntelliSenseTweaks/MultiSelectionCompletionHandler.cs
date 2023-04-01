using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor.Commanding;

namespace VSIntelliSenseTweaks
{
    [Export(typeof(ICommandHandler))]
    [Name(nameof(MultiSelectionCompletionHandler))]
    [ContentType("CSharp")]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    public class MultiSelectionCompletionHandler : ICommandHandler<TypeCharCommandArgs>
    {
        IEditorCommandHandlerServiceFactory _factory;

        [Import]
        IAsyncCompletionBroker2 broker;

        [ImportingConstructor]
        public MultiSelectionCompletionHandler(IEditorCommandHandlerServiceFactory factory)
        {
            _factory = factory;
        }

        public string DisplayName => nameof(MultiSelectionCompletionHandler);

        public bool ExecuteCommand(TypeCharCommandArgs args, CommandExecutionContext executionContext)
        {
            var textView = args.TextView;
            textView.TextBuffer.Insert(0, "lol");
            return true;

            //if (args != 'j')
            //    return false;

            var selections = textView.Selection.SelectedSpans;
            int n_selections = selections.Count;
            if (n_selections < 1)
                return false;

            for (int i = 0; i < n_selections; i++)
            {
                var selection = selections[i];
                if (selection.IsEmpty)
                {
                    var trigger = new CompletionTrigger(CompletionTriggerReason.Invoke, textView.TextSnapshot, default);
                    broker.TriggerCompletion(textView, trigger, selection.Start, default);
                    return true;
                }
            }
            return false;
        }

        public CommandState GetCommandState(TypeCharCommandArgs args)
        {
            return CommandState.Available;
        }
    }
}
