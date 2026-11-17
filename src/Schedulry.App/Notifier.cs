using System.Security.Cryptography;
using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;
using Schedulry.Core;

namespace Schedulry.App;

/// <summary>What a click on a notification asks for.</summary>
public enum NoticeAction
{
    /// <summary>Show the window (and the task, when there is one).</summary>
    Open,
    /// <summary>Stop the alerts for that task.</summary>
    Mute,
}

/// <summary>
/// Windows notifications. A click arrives through <see cref="Activated"/> on a background thread,
/// whether this copy was running or Windows started it for the click.
/// </summary>
public static class Notifier
{
    /// <summary>Action, computer, task path (null for a note about the app itself).</summary>
    public static event Action<NoticeAction, string?, string?>? Activated;

    /// <summary>Windows started this copy because a notification was clicked.</summary>
    public static bool StartedByClick => ToastNotificationManagerCompat.WasCurrentProcessToastActivated();

    /// <summary>Listens for clicks. Called once at start-up, before anything is shown.</summary>
    public static void Listen() => ToastNotificationManagerCompat.OnActivated += e =>
    {
        var args = ToastArguments.Parse(e.Argument);
        var action = args.TryGetValue("action", out var a) && a == "mute" ? NoticeAction.Mute : NoticeAction.Open;
        args.TryGetValue("computer", out var computer);
        args.TryGetValue("path", out var path);
        Activated?.Invoke(action, computer, path);
    };

    public static void Show(FailureNotice notice)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("action", "open")
                .AddArgument("computer", notice.Computer)
                .AddArgument("path", notice.Task.Path)
                .AddText(notice.Title)
                .AddText(notice.Body)
                .AddButton(new ToastButton().SetContent("Open")
                    .AddArgument("action", "open").AddArgument("computer", notice.Computer).AddArgument("path", notice.Task.Path))
                .AddButton(new ToastButton().SetContent("Stop These Alerts")
                    .AddArgument("action", "mute").AddArgument("computer", notice.Computer).AddArgument("path", notice.Task.Path)
                    .SetBackgroundActivation())
                // One notification per task: a newer failure replaces the older one in the notification centre.
                .Show(toast =>
                {
                    toast.Tag = Tag(notice.Task.Path);
                    toast.Group = Tag(notice.Computer);
                });
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Notifications turned off for the app in Windows, or the service unavailable: the list still shows it.
        }
    }

    /// <summary>A note about the app itself, such as "still running in the notification area".</summary>
    public static void ShowNote(string title, string text)
    {
        try
        {
            new ToastContentBuilder().AddArgument("action", "open").AddText(title).AddText(text).Show();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }
    }

    // Windows keeps tags short (64 characters); a task path can be longer.
    static string Tag(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToUpperInvariant())))[..32];
}
