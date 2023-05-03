using Microsoft.VisualStudio.Shell;
using System.ComponentModel;

namespace VSIntelliSenseTweaks
{
    public class GeneralSettings : DialogPage
    {
        public const string PageName = "General";

        private bool includeDebugSuffix = false;
        private bool disableSoftSelection = false;
        private bool boostEnumMemberScore = true;

        [Category(VSIntelliSenseTweaksPackage.PackageDisplayName)]
        [DisplayName(nameof(IncludeDebugSuffix))]
        [Description("Adds a suffix with debug information to the entries in the completion list.")]
        public bool IncludeDebugSuffix
        {
            get { return includeDebugSuffix; }
            set { includeDebugSuffix = value; }
        }

        [Category(VSIntelliSenseTweaksPackage.PackageDisplayName)]
        [DisplayName(nameof(DisableSoftSelection))]
        [Description("Disables initial soft-selection in the completion-list when completion was triggered manually (usually by ctrl + space).")]
        public bool DisableSoftSelection
        {
            get { return disableSoftSelection; }
            set { disableSoftSelection = value; }
        }

        [Category(VSIntelliSenseTweaksPackage.PackageDisplayName)]
        [DisplayName(nameof(BoostEnumMemberScore))]
        [Description("Boosts the score of enum members when the enum type was preselected by roslyn.")]
        public bool BoostEnumMemberScore
        {
            get { return boostEnumMemberScore; }
            set { boostEnumMemberScore = value; }
        }
    }
}
