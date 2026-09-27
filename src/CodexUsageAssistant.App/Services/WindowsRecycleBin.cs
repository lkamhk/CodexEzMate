using System.IO;
using System.Runtime.InteropServices;

namespace CodexUsageAssistant.Services;

internal static class WindowsRecycleBin
{
    internal static void Move(IReadOnlyList<string> paths)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            IFileOperation? operation = null;
            var items = new List<IShellItem>();
            try
            {
                operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
                // Recycle explicitly; never fall back to File.Delete. Stop on shell errors.
                operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00100000 | 0x0400 | 0x0004 | 0x0010 | 0x4000);
                foreach (var path in paths)
                {
                    var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                    Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item));
                    items.Add(item); operation.DeleteItem(item, IntPtr.Zero);
                }
                operation.PerformOperations();
                operation.GetAnyOperationsAborted(out var aborted);
                if (aborted || paths.Any(File.Exists)) throw new IOException("Windows did not recycle all selected files.");
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                foreach (var item in items) Marshal.FinalReleaseComObject(item);
                if (operation is not null) Marshal.FinalReleaseComObject(operation);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new IOException("Could not move the selected files to the Windows Recycle Bin.", error);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] object item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object items);
        void RenameItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.Interface)] object folder);
        void CopyItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.Interface)] object folder);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems([MarshalAs(UnmanagedType.Interface)] object items);
        void NewItem([MarshalAs(UnmanagedType.Interface)] object folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
