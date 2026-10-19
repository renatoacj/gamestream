using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace GameStream;

/// <summary>Preferências do próprio app (não da transmissão), guardadas em app-settings.json na pasta de dados.</summary>
public sealed class AppSettings
{
    /// <summary>Fechar a janela deixa o app rodando na bandeja (perto do relógio) em vez de encerrá-lo.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Nome que aparece para os outros na sala ("Seu nome", no menu).</summary>
    public string Name { get; set; } = "";

    public static AppSettings Current { get; } = Load();

    private static string FilePath => Path.Combine(Tools.DataDir, "app-settings.json");

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Tools.Log("Configurações do app ilegíveis, usando o padrão: " + ex.Message);
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Tools.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex)
        {
            Tools.Log("Não foi possível salvar as configurações do app: " + ex.Message);
        }
    }

    // ---------- Iniciar com o Windows ----------
    // Uma entrada em "Executar" do usuário (HKCU), sem precisar de administrador. O app abre direto na bandeja.

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "GameStream";
    public const string StartupArgument = "--startup";

    private static string Command => $"\"{Environment.ProcessPath}\" {StartupArgument}";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string value && value.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, Command);
            else if (key.GetValue(RunValue) != null) key.DeleteValue(RunValue);
        }
    }
}
