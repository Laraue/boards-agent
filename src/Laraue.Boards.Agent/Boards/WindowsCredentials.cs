namespace Laraue.Boards.Agent.Boards;

/// <summary>A generic credential in the Windows Credential Manager, through advapi32's CredWrite/CredRead.</summary>
public static class WindowsCredentials
{
    private const int GenericType = 1;

    private const int PersistLocalMachine = 2;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public int LastWrittenLow;
        public int LastWrittenHigh;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static void Write(string target, string label, string secret)
    {
        var blob = System.Text.Encoding.UTF8.GetBytes(secret);
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(blob.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(blob, 0, buffer, blob.Length);
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = target,
                Comment = label,
                CredentialBlobSize = blob.Length,
                CredentialBlob = buffer,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException(
                    $"Couldn't save the API key in the Credential Manager (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    public static string? Read(string target)
    {
        if (!CredRead(target, GenericType, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = System.Runtime.InteropServices.Marshal.PtrToStructure<Credential>(pointer);
            var blob = new byte[credential.CredentialBlobSize];
            System.Runtime.InteropServices.Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return System.Text.Encoding.UTF8.GetString(blob);
        }
        finally
        {
            CredFree(pointer);
        }
    }
}
