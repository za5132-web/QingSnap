using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class TranslationCredentialStore(string dataDirectory)
{
    private const int MaximumFileBytes = 16 * 1024;
    private string CredentialPath => Path.Combine(dataDirectory, "baidu-translation.credentials");

    public TranslationCredentials? Load()
    {
        if (!File.Exists(CredentialPath)) return null;
        try
        {
            if (new FileInfo(CredentialPath).Length > MaximumFileBytes) throw new CryptographicException();
            var plain = Protect(File.ReadAllBytes(CredentialPath), decrypt: true);
            try
            {
                var values = JsonSerializer.Deserialize<string[]>(plain);
                if (values is not { Length: 2 }) throw new CryptographicException();
                var credentials = new TranslationCredentials(values[0], values[1]);
                if (!credentials.IsValid) throw new CryptographicException();
                return credentials;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          CryptographicException or JsonException)
        {
            // Do not attach the original exception: it may contain decrypted data.
            DiagnosticLog.Warning("Translation", $"Credential load failed ({exception.GetType().Name}).");
            throw new TranslationException("credentials", "无法读取已保存的密钥，请在此电脑重新填写并保存。");
        }
    }

    public void Save(TranslationCredentials credentials)
    {
        if (!credentials.IsValid) throw new TranslationException("credentials", "请检查 APPID 和密钥是否填写完整。");
        var plain = JsonSerializer.SerializeToUtf8Bytes(new[] { credentials.AppId, credentials.Secret });
        try
        {
            var encrypted = Protect(plain, decrypt: false);
            Directory.CreateDirectory(dataDirectory);
            var temporary = CredentialPath + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, CredentialPath, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Delete() => File.Delete(CredentialPath);

    private static byte[] Protect(byte[] input, bool decrypt)
    {
        var inputBlob = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(input, 0, inputBlob.Data, input.Length);
            var success = decrypt
                ? CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Windows credential protection failed.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            // Clear both unmanaged buffers before releasing them, including decrypted output.
            for (var i = 0; i < inputBlob.Length; i++) Marshal.WriteByte(inputBlob.Data, i, 0);
            Marshal.FreeHGlobal(inputBlob.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                _ = LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
