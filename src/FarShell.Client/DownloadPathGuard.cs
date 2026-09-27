using System.ComponentModel;
using System.Runtime.InteropServices;
using FarShell.Protocol;
using Microsoft.Win32.SafeHandles;

namespace FarShell.Client;

// Keep directory handles open until commit/cleanup. Denying write and delete
// sharing prevents a checked directory from being retargeted or renamed.
internal sealed class DownloadPathGuard : IDisposable
{
    private readonly List<SafeFileHandle> _directories = [];

    internal static DownloadPathGuard Acquire(string root, string destination)
    {
        var guard = new DownloadPathGuard();
        try
        {
            var directory = Path.GetDirectoryName(destination)!;
            var volume = Path.GetPathRoot(directory)!;
            var current = volume;
            guard.LockDirectory(current);
            foreach (var segment in directory[volume.Length..].Split(
                Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current))
                {
                    // Only create directories inside the trusted download root.
                    var relative = Path.GetRelativePath(root, current);
                    if (relative == ".." || relative.StartsWith("..\\", StringComparison.Ordinal)
                        || Path.IsPathRooted(relative))
                    {
                        throw new DirectoryNotFoundException("Download root does not exist.");
                    }

                    Directory.CreateDirectory(current);
                }

                guard.LockDirectory(current);
            }

            RejectReparseFile(destination);
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    internal static void RejectReparseFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                throw new ProtocolException("Download destination must be an ordinary file.");
            }
        }
        catch (FileNotFoundException)
        {
            // A new destination is valid.
        }
    }

    private void LockDirectory(string path)
    {
        var handle = CreateFileW(path, 0x80, FileShare.Read, IntPtr.Zero, 3,
            0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("Cannot secure the download directory.", new Win32Exception(error));
        }

        _directories.Add(handle);
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception();
        }

        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new ProtocolException("Download path must not traverse a reparse point.");
        }
    }

    public void Dispose()
    {
        for (var index = _directories.Count - 1; index >= 0; index--)
        {
            _directories[index].Dispose();
        }

        _directories.Clear();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access,
        FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
}
