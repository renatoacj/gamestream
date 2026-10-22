using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace GameStream;

/// <summary>Preferências do próprio app (não da transmissão), guardadas em app-settings.json na pasta de dados.</summary>
public sealed class AppSettings
{
    /// <summary>Fechar a janela deixa o app rodando na bandeja (perto do relógio) em vez de encerrá-lo.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Nome que aparece para os outros na sala ("Seu nome", no menu).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Dados da interface (salas salvas, salas recentes, qualidade, painéis). Ficavam só no armazenamento do
    /// navegador embutido, que muda com a porta do app (8790, 8791…) e pode perder o que foi gravado logo antes de
    /// o app fechar — as salas "sumiam". Aqui ficam no arquivo do app.
    /// </summary>
    public Dictionary<string, string> Store { get; set; } = [];

    /// <summary>Publicar no ntfy.sh o endereço das salas salvas (código fixo). null = o que foi escolhido na instalação.</summary>
    public bool? FixedRoomCodes { get; set; }

    /// <summary>Consultar no GitHub se há versão nova. null = o que foi escolhido na instalação.</summary>
    public bool? CheckUpdates { get; set; }

    [JsonIgnore] public bool FixedRoomCodesEnabled => FixedRoomCodes ?? InstallerChoice("FixedRoomCodes");
    [JsonIgnore] public bool CheckUpdatesEnabled => CheckUpdates ?? InstallerChoice("CheckUpdates");

    /// <summary>Opção marcada na instalação (HKCU\Software\GameStream); ligada se não houver.</summary>
    private static bool InstallerChoice(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\GameStream");
            return key?.GetValue(name) is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }

    public static AppSettings Current { get; } = Load();

#if DEBUG
    // Compilações de desenvolvimento usam um arquivo próprio: testes nunca mexem nas salas e preferências reais.
    private static string FilePath => Path.Combine(Tools.DataDir, "app-settings-dev.json");
#else
    private static string FilePath => Path.Combine(Tools.DataDir, "app-settings.json");
#endif
    private static readonly object FileLock = new();

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
            string json;
            lock (Store) json = JsonSerializer.Serialize(this);
            lock (FileLock) File.WriteAllText(FilePath, json);
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
