using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PhotoMusicViewer.Services
{
    /// <summary>Kernel Transaction Manager / Transactional Registry. No nontransactional fallback.</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsRegistryMruTransaction : IRegistryMruTransaction
    {
        private readonly SafeTransactionHandle _transaction;
        private bool _committed, _disposed;

        internal WindowsRegistryMruTransaction()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Registry cleanup requires Windows TxR.");
            _transaction = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 5000, "PhotoMusicViewer MRU cleanup");
            if (_transaction.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                _transaction.Dispose();
                throw new Win32Exception(error, "Could not start atomic registry cleanup. No history was changed.");
            }
        }

        public IRegistryMruKey? OpenCurrentUser(string path)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Never transact a predefined root itself, and never create missing keys.
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('\\')) throw new ArgumentException("A registry subkey is required.");
            const uint QuerySetEnumerate = 0x0001 | 0x0002 | 0x0008;
            int result = RegOpenKeyTransactedW(new IntPtr(unchecked((int)0x80000001)), path, 0,
                QuerySetEnumerate, out var handle, _transaction, IntPtr.Zero);
            if (result != 0)
            {
                handle?.Dispose();
                if (result is 2 or 3) return null; // key absent: no mutation
                throw new Win32Exception(result, "Atomic registry cleanup could not open a key.");
            }
            try { return new Key(RegistryKey.FromHandle(handle)); }
            catch { handle.Dispose(); throw; }
        }

        public void Commit()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!CommitTransaction(_transaction))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Registry cleanup was not committed (conflict, timeout or access error).");
            _committed = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_committed && !_transaction.IsInvalid)
                    RollbackTransaction(_transaction);
            }
            finally { _transaction.Dispose(); } // close also aborts a never-committed transaction
        }

        private sealed class Key : IRegistryMruKey
        {
            private readonly RegistryKey _key;
            internal Key(RegistryKey key) => _key = key;
            public string[] ValueNames() => _key.GetValueNames();
            public string[] SubKeyNames() => _key.GetSubKeyNames();
            public byte[]? ReadBinary(string name)
            {
                var value = _key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null) return null;
                return _key.GetValueKind(name) == RegistryValueKind.Binary ? value as byte[] : null;
            }
            public void DeleteValue(string name) => _key.DeleteValue(name, throwOnMissingValue: true);
            public void WriteOrder(byte[] order) => _key.SetValue("MRUListEx", order, RegistryValueKind.Binary);
            public void Dispose() => _key.Dispose();
        }

        private sealed class SafeTransactionHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeTransactionHandle() : base(true) { }
            protected override bool ReleaseHandle() => CloseHandle(handle);
        }
        [DllImport("KtmW32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeTransactionHandle CreateTransaction(IntPtr attributes, IntPtr uow,
            uint options, uint isolationLevel, uint isolationFlags, uint timeout, string description);
        [DllImport("KtmW32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CommitTransaction(SafeTransactionHandle transaction);
        [DllImport("KtmW32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RollbackTransaction(SafeTransactionHandle transaction);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegOpenKeyTransactedW(IntPtr root, string subkey, uint options,
            uint access, out SafeRegistryHandle key, SafeTransactionHandle transaction, IntPtr extendedParameter);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
