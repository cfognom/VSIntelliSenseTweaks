using Microsoft.VisualStudio.Shell;
using System.ComponentModel;

namespace VSIntelliSenseTweaks
{
    public class GeneralSettings : DialogPage
    {
        public const string PageName = "General";

        private bool enableDebugSuffix = false;

        [Category(nameof(VSIntelliSenseTweaks))]
        [DisplayName(nameof(EnableDebugSuffix))]
        [Description("Adds a suffix with debug information to the entries in the completion list.")]
        public bool EnableDebugSuffix
        {
            get { return enableDebugSuffix; }
            set { enableDebugSuffix = value; }
        }
    }
}
