using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

// "Open file" dialog for picking a session CSV, per platform:
//   Editor  — EditorUtility.OpenFilePanel
//   Windows — the native GetOpenFileNameW dialog (comdlg32)
//   Linux   — zenity or kdialog, if one is installed (they ship with GNOME / KDE desktops)
// Returns Unavailable when no dialog can be shown, so the caller can fall back to something
// else (e.g. the newest CSV in the export folder).
//
// All variants are modal: the app window doesn't redraw while the dialog is open, the same
// as the existing folder picker.
public static class CsvFilePicker
{
    public enum Result { Selected, Cancelled, Unavailable }

    private const string Title = "Choose a session CSV file";

    public static Result Pick(string startDirectory, out string path)
    {
        path = null;
        if (!string.IsNullOrEmpty(startDirectory) && !Directory.Exists(startDirectory))
            startDirectory = null;

#if UNITY_EDITOR
        path = UnityEditor.EditorUtility.OpenFilePanel(Title, startDirectory ?? "", "csv");
        return string.IsNullOrEmpty(path) ? Result.Cancelled : Result.Selected;
#elif UNITY_STANDALONE_WIN
        return PickWindows(startDirectory, out path);
#elif UNITY_STANDALONE_LINUX
        return PickLinux(startDirectory, out path);
#else
        return Result.Unavailable;
#endif
    }

    // ------------------------------------------------------------------ Windows

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_EXPLORER = 0x00080000;
    private const int MaxPathChars = 4096;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    private static Result PickWindows(string startDirectory, out string path)
    {
        path = null;
        IntPtr buffer = Marshal.AllocCoTaskMem(MaxPathChars * 2);
        try
        {
            Marshal.Copy(new byte[MaxPathChars * 2], 0, buffer, MaxPathChars * 2); // zero-fill

            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf(typeof(OPENFILENAME)),
                hwndOwner = GetActiveWindow(),
                lpstrFilter = "CSV files (*.csv)\0*.csv\0All files (*.*)\0*.*\0\0",
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = MaxPathChars,
                lpstrInitialDir = startDirectory,
                lpstrTitle = Title,
                lpstrDefExt = "csv",
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            };

            if (!GetOpenFileNameW(ref ofn))
                return Result.Cancelled; // user cancelled (or the dialog failed — treated the same)

            path = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrEmpty(path) ? Result.Cancelled : Result.Selected;
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
        {
            return Result.Unavailable;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }
#endif

    // -------------------------------------------------------------------- Linux

#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
    private static Result PickLinux(string startDirectory, out string path)
    {
        string start = string.IsNullOrEmpty(startDirectory) ? "" : startDirectory.TrimEnd('/') + "/";

        Result result = RunDialog("zenity",
            $"--file-selection --title={Quote(Title)} --file-filter={Quote("CSV files | *.csv")} --file-filter={Quote("All files | *")}" +
            (start.Length > 0 ? $" --filename={Quote(start)}" : ""),
            out path);
        if (result != Result.Unavailable) return result;

        return RunDialog("kdialog",
            $"--title {Quote(Title)} --getopenfilename {Quote(start.Length > 0 ? start : ".")} {Quote("*.csv|CSV files")}",
            out path);
    }

    private static Result RunDialog(string program, string arguments, out string path)
    {
        path = null;
        try
        {
            var startInfo = new ProcessStartInfo(program, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using (Process process = Process.Start(startInfo))
            {
                if (process == null) return Result.Unavailable;
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();

                if (process.ExitCode == 0 && File.Exists(output))
                {
                    path = output;
                    return Result.Selected;
                }
                // zenity/kdialog exit with 1 when the user cancels.
                return process.ExitCode == 1 ? Result.Cancelled : Result.Unavailable;
            }
        }
        catch (Win32Exception)
        {
            return Result.Unavailable; // program not installed
        }
        catch (Exception)
        {
            return Result.Unavailable;
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
#endif
}
