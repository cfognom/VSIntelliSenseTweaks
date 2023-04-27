using Microsoft.VisualStudio.Shell;
using System.ComponentModel;

namespace VSIntelliSenseTweaks
{
    public class GeneralSettings : DialogPage
    {
        public const string PageName = "General";

        private bool includeDebugSuffix = false;
        private bool disableSoftSelectionOnManualTrigger = false;

        [Category(VSIntelliSenseTweaksPackage.PackageDisplayName)]
        [DisplayName(nameof(IncludeDebugSuffix))]
        [Description("Adds a suffix with debug information to the entries in the completion list.")]
        public bool IncludeDebugSuffix
        {
            get { return includeDebugSuffix; }
            set { includeDebugSuffix = value; }
        }

        [Category(VSIntelliSenseTweaksPackage.PackageDisplayName)]
        [DisplayName(nameof(DisableSoftSelectionOnManualTrigger))]
        [Description("Disables soft-selection in the completion list when completion was triggered manually (usually by ctrl + space).")]
        public bool DisableSoftSelectionOnManualTrigger
        {
            get { return disableSoftSelectionOnManualTrigger; }
            set { disableSoftSelectionOnManualTrigger = value; }
        }
    }
}
