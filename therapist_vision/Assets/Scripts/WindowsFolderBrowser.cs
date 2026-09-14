#if UNITY_STANDALONE_WIN
using System;
using System.Runtime.InteropServices;
using System.Text;

// Minimal Win32 folder picker for standalone Windows builds, where
// UnityEditor.EditorUtility.OpenFolderPanel isn't available (it's an Editor-only API, stripped
// out of any real build). Uses the classic SHBrowseForFolder shell API rather than the newer
// IFileOpenDialog COM interface — IFileOpenDialog's COM vtable is easy to get subtly wrong (and
// a mistake there tends to crash the process rather than throw a catchable exception), whereas
// SHBrowseForFolder is a single flat P/Invoke call every Windows version has supported for
// decades.
public static class WindowsFolderBrowser
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public string pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_NEWDIALOGSTYLE = 0x0040; // resizable dialog with a "Make New Folder" button
    private const uint BIF_EDITBOX = 0x0010; // adds a text box so a path can be typed/pasted directly
    private const uint BIF_USENEWUI = BIF_NEWDIALOGSTYLE | BIF_EDITBOX;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    // Returns false if the user cancelled the dialog (or the picked item somehow wasn't a
    // filesystem path — shouldn't happen given BIF_RETURNONLYFSDIRS above).
    public static bool TryBrowseForFolder(string title, out string selectedPath)
    {
        selectedPath = null;

        var browseInfo = new BROWSEINFO
        {
            pszDisplayName = new string('\0', 260), // scratch buffer the dialog writes into while open; MAX_PATH-sized
            lpszTitle = title,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_USENEWUI,
        };

        IntPtr pidl = SHBrowseForFolder(ref browseInfo);
        if (pidl == IntPtr.Zero)
            return false; // user cancelled

        try
        {
            var pathBuffer = new StringBuilder(260);
            if (!SHGetPathFromIDList(pidl, pathBuffer))
                return false;

            selectedPath = pathBuffer.ToString();
            return !string.IsNullOrEmpty(selectedPath);
        }
        finally
        {
            CoTaskMemFree(pidl);
        }
    }
}
#endif
