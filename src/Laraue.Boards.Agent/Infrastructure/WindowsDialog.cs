namespace Laraue.Boards.Agent.Infrastructure;

public static class WindowsDialog
{
    private const uint YesNo = 0x4;
    private const uint IconQuestion = 0x20;
    private const uint Topmost = 0x40000;
    private const uint SetForeground = 0x10000;
    private const int Yes = 6;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

    /// <summary>A Yes/No message box; the scheduled task runs with the person's interactive logon, so it shows on their screen.</summary>
    public static bool Ask(string message)
    {
        return MessageBox(IntPtr.Zero, message, Notifications.AppName, YesNo | IconQuestion | Topmost | SetForeground) == Yes;
    }
}
