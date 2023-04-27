using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using System.Diagnostics;
using Task = System.Threading.Tasks.Task;
using Microsoft;

namespace VSIntelliSenseTweaks
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(VSIntelliSenseTweaksPackage.PackageGuidString)]
    [ProvideOptionPage(pageType: typeof(GeneralSettings), categoryName: PackageDisplayName, pageName: GeneralSettings.PageName, 0, 0, true)]
    public sealed class VSIntelliSenseTweaksPackage : AsyncPackage
    {
        /// <summary>
        /// VSIntelliSenseTweaksPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "8e0ec3d8-0561-477a-ade4-77d8826fc290";

        public const string PackageDisplayName = "IntelliSense Tweaks";

        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
        /// <param name="progress">A provider for progress updates.</param>
        /// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Instance = this;
            // When initialized asynchronously, the current thread may be a background thread at this point.
            // Do any initialization that requires the UI thread after switching to the UI thread.
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        }

        public static VSIntelliSenseTweaksPackage Instance;

        public static GeneralSettings Settings
        {
            get
            {
                Debug.Assert(Instance != null);
                return (GeneralSettings)Instance.GetDialogPage(typeof(GeneralSettings));
            }
        }

        public static void EnsurePackageLoaded()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Instance == null)
            {
                var vsShell = (IVsShell)ServiceProvider.GlobalProvider.GetService(typeof(IVsShell));
                Assumes.Present(vsShell);
                var guid = new Guid(VSIntelliSenseTweaksPackage.PackageGuidString);
                vsShell.LoadPackage(ref guid, out var package);
                Debug.Assert(Instance != null);
            }
        }

        #endregion
    }
}
