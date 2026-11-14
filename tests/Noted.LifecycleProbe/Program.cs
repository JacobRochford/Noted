using System.Windows.Threading;
using System.Reflection;
using System.IO;
using System.Windows.Controls;
using Noted;
using Noted.Services;
using Noted.Models;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // refuse to share the real application's identity with an existing instance.
        using (var guard = new Mutex(false, "Noted.SingleInstance", out var available))
        {
            if (!available)
            {
                Console.WriteLine("Noted is already running; lifecycle verification skipped.");
                return 2;
            }
        }
        if (Environment.GetEnvironmentVariable("NOTED_REMINDER_SMOKE") == "1" ||
            ChecklistReminderActivation.IsNotificationLaunch(Environment.GetCommandLineArgs().Skip(1)))
            return RunReminderSmoke();
        var result = 1;
        var app = new App();
        app.InitializeComponent();
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (WindowManager.Main is null) throw new InvalidOperationException("Main window is missing.");
                Console.WriteLine("actual application startup completed");
                if (typeof(App).GetField("_checklistReminderScheduler", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(app) is null)
                    throw new InvalidOperationException("The reminder scheduler did not start.");
                Console.WriteLine(WindowManager.Checklist is null
                    ? "reminder scheduler started before Checklist was opened"
                    : "reminder scheduler started with the restored Checklist window");
                var owner = app.GetOrCreateChecklistViewModel();
                WindowManager.ShowChecklist();
                var first = WindowManager.Checklist ?? throw new InvalidOperationException("Checklist is missing.");
                if (!ReferenceEquals(owner, first.DataContext)) throw new InvalidOperationException("Checklist owner differs.");
                first.Close();
                if (WindowManager.Checklist is not null) throw new InvalidOperationException("Checklist did not close.");
                if (!owner.TryFlushPendingItems(out var error)) throw new InvalidOperationException(error);
                WindowManager.ShowChecklist();
                var reopened = WindowManager.Checklist ?? throw new InvalidOperationException("Checklist did not reopen.");
                if (ReferenceEquals(first, reopened) || !ReferenceEquals(owner, reopened.DataContext))
                    throw new InvalidOperationException("Checklist lifetime is incorrect.");
                reopened.Close();
                Console.WriteLine("Checklist opened, closed, flushed while closed, and reopened with the same owner");
                var mainClosed = false;
                WindowManager.Main.Closed += (_, _) => mainClosed = true;
                WindowManager.Main.Close();
                if (!mainClosed)
                    throw new InvalidOperationException("Normal application shutdown was blocked.");
                result = 0;
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception);
                Console.WriteLine("The probe is left open for normal user-controlled shutdown.");
            }
        }, DispatcherPriority.ApplicationIdle);
        app.Exit += (_, _) => Console.WriteLine("actual application exit completed");
        app.Run();
        return result;
    }

    private static int RunReminderSmoke()
    {
        var activated = ChecklistReminderActivation.IsNotificationLaunch(Environment.GetCommandLineArgs().Skip(1));
        var profile = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "reminder-smoke-data");
        var log = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "reminder-smoke.log");
        void Record(string text)
        {
            var line = $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {text}";
            Console.WriteLine(line);
            File.AppendAllText(log, line + Environment.NewLine);
        }
        Directory.CreateDirectory(profile);
        if (!activated)
        {
            var settings = new AppSettingsService(profile);
            settings.SaveNotesDirectory(Path.Combine(profile, "notes"));
            var now = DateTimeOffset.UtcNow;
            ChecklistItemState[] reminders = [
                new() { Id = Guid.Parse("d4345c04-82c7-400e-a536-0e015ecac711"), Text = "[smoke] Individual A", ReminderAtUtc = now.AddSeconds(3) },
                new() { Id = Guid.Parse("e4345c04-82c7-400e-a536-0e015ecac722"), Text = "[smoke] Individual B", ReminderAtUtc = now.AddSeconds(7) },
                new() { Id = Guid.Parse("f4345c04-82c7-400e-a536-0e015ecac733"), Text = "[smoke] Group A", ReminderAtUtc = now.AddSeconds(11) },
                new() { Id = Guid.Parse("a4345c04-82c7-400e-a536-0e015ecac744"), Text = "[smoke] Group B", ReminderAtUtc = now.AddSeconds(11) }
            ];
            if (Environment.GetEnvironmentVariable("NOTED_REMINDER_GROUP_ONLY") == "1")
                reminders = reminders.Where(item => item.Text!.StartsWith("[smoke] Group", StringComparison.Ordinal))
                    .Select(item => item with { ReminderAtUtc = now.AddSeconds(3) }).ToArray();
            new ChecklistContentService(profile).SaveItems(reminders);
        }
        var app = new App(profile);
        app.InitializeComponent();
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(() =>
        {
            Record($"real App started; COM={activated}; isolated profile={profile}");
            Guid? selected = null;
            var observe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            observe.Tick += (_, _) =>
            {
                if (WindowManager.Checklist?.FindName("ItemsList") is ListBox list && list.SelectedItem is ChecklistItem item && item.Id != selected)
                {
                    selected = item.Id;
                    Record($"selected task={item.Id:D}");
                }
            };
            observe.Start();
            var exit = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            exit.Tick += (_, _) => { exit.Stop(); observe.Stop(); WindowManager.Main?.Close(); };
            exit.Start();
        }, DispatcherPriority.ApplicationIdle);
        app.Exit += (_, _) => Record("real App exited normally");
        return app.Run();
    }
}
