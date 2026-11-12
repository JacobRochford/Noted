using System.Windows;
using System.Windows.Threading;

namespace Noted.Tests.Services;

[TestClass]
[TestCategory("DesktopInteraction")]
public sealed class AppDialogTests
{
    [TestMethod]
    public void ModalDialogStaysTopmostWhenUtilityWindowIsTopmost() => WpfTestHost.Invoke(() =>
    {
        var owner = new Window { Width = 300, Height = 200 };
        var utility = new Window { Width = 200, Height = 150, Topmost = true };
        Window? dialogOwner = null;
        bool? dialogTopmost = null;
        try
        {
            owner.Show();
            utility.Show();
            var inspect = owner.Dispatcher.BeginInvoke(() =>
            {
                var dialog = Application.Current.Windows.OfType<AppDialog>().Single();
                dialogOwner = dialog.Owner;
                dialogTopmost = dialog.Topmost;
                dialog.Close();
            }, DispatcherPriority.ApplicationIdle);
            try
            {
                AppDialog.Show(owner, "Save changes?", "Unsaved Changes", MessageBoxButton.YesNoCancel);
            }
            finally { inspect.Abort(); }

            Assert.AreSame(owner, dialogOwner);
            Assert.AreEqual(true, dialogTopmost);
        }
        finally
        {
            utility.Close();
            owner.Close();
        }
    });
}
