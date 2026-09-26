namespace GameStream;

/// <summary>Configurações escolhidas na janela do app (nomes iguais aos campos do JSON).</summary>
public sealed record StreamSettings
{
    /// <summary>"w:&lt;hwnd&gt;" (janela) ou "m:&lt;hmonitor&gt;" (monitor). Ver <see cref="CaptureSources"/>.</summary>
    public string Source { get; init; } = "";

    /// <summary>Altura de saída (ex.: 720, 1080). 0 = resolução original.</summary>
    public int Height { get; init; } = 1080;

    public int Fps { get; init; } = 60;

    /// <summary>true = fps constante (repete o último quadro se o jogo não mandar um novo a tempo).</summary>
    public bool ConstantFps { get; init; } = true;

    /// <summary>auto | nvenc | amf | qsv | x264</summary>
    public string Encoder { get; init; } = "auto";

    /// <summary>cbr | vbr</summary>
    public string RateControl { get; init; } = "cbr";

    public int BitrateKbps { get; init; } = 8000;

    /// <summary>speed | balanced | quality</summary>
    public string Preset { get; init; } = "balanced";

    public double KeyframeSeconds { get; init; } = 1;

    /// <summary>high | main</summary>
    public string Profile { get; init; } = "high";

    public bool Cursor { get; init; } = true;

    /// <summary>window (só o programa da janela) | system (todo o som do PC) | none</summary>
    public string AudioMode { get; init; } = "window";

    public int AudioKbps { get; init; } = 160;

    /// <summary>Ganho aplicado ao áudio antes de codificar (dB), com limitador para não distorcer.</summary>
    public int AudioGainDb { get; init; } = 6;
}
