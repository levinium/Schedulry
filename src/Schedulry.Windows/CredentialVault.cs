using System.ComponentModel;
using System.Runtime.InteropServices;
using Schedulry.Core;

namespace Schedulry.Windows;

/// <summary>
/// Remembered passwords in Windows Credential Manager, as generic credentials named
/// "Schedulry:COMPUTER". Windows encrypts them with the person's sign-in; they are kept on this
/// PC only (not roamed), and show in Control Panel, Credential Manager, Windows Credentials.
/// </summary>
public sealed class CredentialVault : IPasswordVault
{
    public const string Prefix = "Schedulry:";
    const int Generic = 1;              // CRED_TYPE_GENERIC
    const int LocalMachine = 2;         // CRED_PERSIST_LOCAL_MACHINE: this PC only
    const int NotFound = 1168;          // ERROR_NOT_FOUND

    public static string TargetFor(string computer) => Prefix + (ComputerNames.Normalise(computer) ?? "").ToUpperInvariant();

    public string? Read(string computer, string account)
    {
        if (!CredRead(TargetFor(computer), Generic, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            return error == NotFound ? null : throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (!Accounts.Same(credential.UserName, account) || credential.CredentialBlob == IntPtr.Zero)
                return null;   // saved for a different account: not this one's password
            return Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Save(string computer, string account, string password)
    {
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new Credential
            {
                Type = Generic,
                TargetName = TargetFor(computer),
                Comment = "Saved by Schedulry to connect to this computer's Task Scheduler.",
                CredentialBlob = blob,
                CredentialBlobSize = password.Length * 2,
                Persist = LocalMachine,
                UserName = account,
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            // Zero the copy before freeing it.
            for (var i = 0; i < password.Length * 2; i++)
                Marshal.WriteByte(blob, i, 0);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete(string computer)
    {
        if (!CredDelete(TargetFor(computer), Generic, 0) && Marshal.GetLastWin32Error() is var error && error != NotFound)
            throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
