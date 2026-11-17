using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Schedulry.App;

/// <summary>
/// One copy per person signed in. Starting it again brings the running copy's window forward
/// instead of opening a second one (which would watch, and notify, twice).
/// <para>
/// The lock and the "show" signal are made with a security descriptor that lets this person in at
/// any integrity level, so a copy started normally still finds one running as administrator. With
/// Windows' defaults, an elevated copy's lock can't be opened by a normal one.
/// </para>
/// </summary>
internal static class SingleInstance
{
    static WaitHandle? _mutex;
    static NativeHandle? _show;

    /// <summary>
    /// True when this copy should run. False when another is running: it has been asked to show
    /// its window, unless <paramref name="quiet"/> (started with Windows, nothing to show).
    /// <paramref name="waitForPrevious"/> is for a restart (as administrator, or to update) while the old copy closes.
    /// </summary>
    public static bool Claim(string name, bool quiet, bool waitForPrevious)
    {
        try
        {
            var (mutex, existed) = Create($@"Local\{name}", isMutex: true);
            _mutex = mutex;
            if (existed && !Wait(mutex, waitForPrevious ? TimeSpan.FromSeconds(20) : TimeSpan.Zero))
            {
                if (!quiet)
                {
                    var (show, _) = Create($@"Local\{name}.Show", isMutex: false);
                    // The running copy may bring its window to the front: this one has the right to, and passes it on.
                    _ = AllowSetForegroundWindow(AnyProcess);
                    _ = SetEvent(show.SafeWaitHandle);
                    show.Dispose();
                }
                mutex.Dispose();
                _mutex = null;
                return false;
            }
            _show = Create($@"Local\{name}.Show", isMutex: false).Handle;
        }
        catch (Win32Exception)
        {
            // Can't share a lock (an older copy made it with Windows' defaults): run alongside rather than do nothing.
            _mutex = null;
        }
        return true;
    }

    /// <summary>Calls <paramref name="show"/> (on a background thread) whenever another copy asks.</summary>
    public static void OnShowRequested(Action show)
    {
        if (_show is not { } handle)
            return;
        new Thread(() =>
        {
            while (handle.WaitOne())
                show();
        }) { IsBackground = true, Name = "Schedulry show requests" }.Start();
    }

    static bool Wait(WaitHandle mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;   // the previous copy ended without letting go: it's ours now
        }
    }

    /// <summary>A named mutex (owned when new) or auto-reset event, open to this person at any integrity level.</summary>
    static (NativeHandle Handle, bool Existed) Create(string name, bool isMutex)
    {
        var user = WindowsIdentity.GetCurrent().User!.Value;
        // This person and SYSTEM get full access; the Medium label lets a normal copy signal an elevated one.
        var sddl = $"D:(A;;GA;;;{user})(A;;GA;;;SY)S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, IntPtr.Zero))
            throw new Win32Exception();
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var raw = isMutex
                ? CreateMutexW(ref attributes, true, name)
                : CreateEventW(ref attributes, false, false, name);
            var error = Marshal.GetLastPInvokeError();
            if (raw == IntPtr.Zero)
                throw new Win32Exception(error);
            return (new NativeHandle(new SafeWaitHandle(raw, ownsHandle: true)), error == AlreadyExists);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    sealed class NativeHandle : WaitHandle
    {
        public NativeHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    const int AnyProcess = -1;
    const int AlreadyExists = 183;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateMutexW(ref SecurityAttributes attributes, bool initialOwner, string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateEventW(ref SecurityAttributes attributes, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetEvent(SafeWaitHandle handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AllowSetForegroundWindow(int processId);
}
