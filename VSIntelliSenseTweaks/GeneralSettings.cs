using Microsoft.VisualStudio.Shell;
using System.ComponentModel;

namespace VSIntelliSenseTweaks
{
    public class GeneralSettings : DialogPage
    {
        public const string PageName = "General";

        private bool includeDebugSuffix = false;
        private bool disableSoftSelection = false;

        [Category(nameof(VSIntelliSenseTweaks))]
        [DisplayName(nameof(IncludeDebugSuffix))]
        [Description("Adds a suffix with debug information to the entries in the completion list.")]
        public bool IncludeDebugSuffix
        {
            get { return includeDebugSuffix; }
            set { includeDebugSuffix = value; }
        }

        [Category(nameof(VSIntelliSenseTweaks))]
        [DisplayName(nameof(DisableSoftSelection))]
        [Description("Disables soft-selection in the completion list.")]
        public bool DisableSoftSelection
        {
            get { return disableSoftSelection; }
            set { disableSoftSelection = value; }
        }
    }
}
