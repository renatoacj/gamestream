using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GameStream;

/// <summary>
/// Localiza (ou baixa na primeira execução) as ferramentas externas usadas pelo app.
/// Ordem de busca: pasta do GameStream.exe → %LOCALAPPDATA%\GameStream → download oficial.
/// </summary>
public static class Tools
{
    private const string FfmpegZipUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameStream");

    private static readonly SemaphoreSlim FfmpegLock = new(1, 1);

    /// <summary>Caminho do ffmpeg.exe, baixando se necessário. <paramref name="progress"/> recebe mensagens de status.</summary>
    public static async Task<string> EnsureFfmpegAsync(Action<string>? progress = null, CancellationToken token = default)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;

        var cached = Path.Combine(DataDir, "ffmpeg", "ffmpeg.exe");
        await FfmpegLock.WaitAsync(token);
        try
        {
            if (File.Exists(cached)) return cached;

            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            var zip = Path.Combine(DataDir, "ffmpeg.zip.download");
            await DownloadAsync(FfmpegZipUrl, zip, pct => progress?.Invoke($"Baixando FFmpeg (só na primeira vez)… {pct}%"), token);

            // Confere o SHA-256 publicado junto com o build: arquivo corrompido ou trocado no caminho é descartado.
            progress?.Invoke("Verificando FFmpeg…");
            await VerifyFfmpegChecksumAsync(zip, token);

            progress?.Invoke("Extraindo FFmpeg…");
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("ffmpeg.exe não encontrado no pacote baixado");
                entry.ExtractToFile(cached + ".tmp", overwrite: true);
            }
            File.Move(cached + ".tmp", cached, overwrite: true);
            File.Delete(zip);
            return cached;
        }
        finally
        {
            FfmpegLock.Release();
        }
    }

    private static async Task VerifyFfmpegChecksumAsync(string zip, CancellationToken token)
    {
        var fileName = Path.GetFileName(new Uri(FfmpegZipUrl).LocalPath);
        var checksumsUrl = FfmpegZipUrl[..FfmpegZipUrl.LastIndexOf('/')] + "/checksums.sha256";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
        var list = await http.GetStringAsync(checksumsUrl, token);
        var expected = list.Split('\n')
            .Select(l => l.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].Trim().TrimStart('*') == fileName)?[0];

        string actual;
        await using (var f = File.OpenRead(zip))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(f, token));

        if (expected == null || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new InvalidDataException("O FFmpeg baixado não confere com o checksum oficial. Tente de novo mais tarde.");
        }
    }

    /// <summary>
    /// Verifica a assinatura digital (Authenticode) de um executável e se foi emitida para
    /// <paramref name="organization"/>. Usado para só executar o cloudflared oficial da Cloudflare.
    /// </summary>
    public static bool IsSignedBy(string path, string organization)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,        // WTD_UI_NONE
                dwUnionChoice = 1,     // WTD_CHOICE_FILE
                pFile = pFile,
            };
            if (WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data) != 0) return false;
        }
        finally
        {
            Marshal.FreeHGlobal(pFile);
        }

#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        // Subject do tipo: CN="Cloudflare, Inc.", O="Cloudflare, Inc.", ...
        return cert.GetNameInfo(X509NameType.SimpleName, false) == organization;
    }

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    public static async Task DownloadAsync(string url, string path, Action<int>? percent, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;

        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(path);
        var buffer = new byte[256 * 1024];
        long done = 0;
        var lastPct = -1;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            done += read;
            var pct = total > 0 ? (int)(done * 100 / total) : 0;
            if (pct != lastPct) { lastPct = pct; percent?.Invoke(pct); }
        }
    }
}
