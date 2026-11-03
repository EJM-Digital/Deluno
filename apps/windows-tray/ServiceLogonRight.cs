using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Deluno.Tray;

/// <summary>
/// Gives a Windows account the right to log on as a service. A service set to run as a named person never starts
/// without it ("This service account does not have the required user right Log on as a service", seen on the golden VM
/// with the right password), and <c>sc config obj=</c> does not grant it.
/// </summary>
internal static class ServiceLogonRight
{
    private const string RightName = "SeServiceLogonRight";
    private const uint PolicyCreateAccount = 0x00000010;
    private const uint PolicyLookupNames = 0x00000800;

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes attributes, uint access, out IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern uint LsaAddAccountRights(IntPtr policy, byte[] accountSid, LsaUnicodeString[] userRights, uint count);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);

    /// <summary>Returns null when the account has the right now, or the reason in plain words when it could not be given.</summary>
    internal static string? Grant(string account)
    {
        try
        {
            var sid = SidOf(account);
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);

            var attributes = new LsaObjectAttributes { Length = Marshal.SizeOf<LsaObjectAttributes>() };
            var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyCreateAccount | PolicyLookupNames, out var policy);
            if (status != 0)
            {
                return $"Windows would not open its security policy ({new Win32Exception(LsaNtStatusToWinError(status)).Message}).";
            }

            var name = Marshal.StringToHGlobalUni(RightName);
            try
            {
                var right = new LsaUnicodeString { Buffer = name, Length = (ushort)(RightName.Length * 2), MaximumLength = (ushort)((RightName.Length + 1) * 2) };
                status = LsaAddAccountRights(policy, bytes, [right], 1);
                return status == 0 ? null : $"Windows would not give it the right ({new Win32Exception(LsaNtStatusToWinError(status)).Message}).";
            }
            finally
            {
                Marshal.FreeHGlobal(name);
                LsaClose(policy);
            }
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException or SystemException)
        {
            return $"Windows does not know an account called {account}.";
        }
    }

    private static SecurityIdentifier SidOf(string account)
    {
        var name = account.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + account[1..] : account;
        return (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
    }
}
