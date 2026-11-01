using System.Runtime.InteropServices;
using System.Text;

namespace Lumos.Core;

/// <summary>
/// Секрети (ключ Gemini API) у профілі користувача, %AppData%\Lumos. На Windows — зашифровані DPAPI:
/// розшифрувати файл може лише цей користувач Windows на цьому комп'ютері. У коді й файлах проєкту ключа немає.
/// </summary>
public static class SecretStore
{
    private static string PathFor(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumos", name + ".secret");

    public static string? Load(string name)
    {
        try
        {
            string path = PathFor(name);
            if (!File.Exists(path)) return null;
            byte[] data = File.ReadAllBytes(path);
            return Encoding.UTF8.GetString(OperatingSystem.IsWindows() ? Unprotect(data) : data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public static void Save(string name, string? value)
    {
        string path = PathFor(name);
        if (string.IsNullOrEmpty(value))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] data = Encoding.UTF8.GetBytes(value);
        File.WriteAllBytes(path, OperatingSystem.IsWindows() ? Protect(data) : data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private const int UiForbidden = 0x1;

    private static byte[] Protect(byte[] data) => Run(data, protect: true);

    private static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "Lumos", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new InvalidOperationException($"Не вдалося {(protect ? "зашифрувати" : "розшифрувати")} ключ (код {Marshal.GetLastWin32Error()}).");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
