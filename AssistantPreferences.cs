using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace LolMatchAssistant;

internal record ChampionChoice(int Id, string Name);

internal record AssistantPreferences(int Theme = 0, bool AutoAccept = false, bool AutoPick = false, int Champion = 0,
    bool LockIn = false, int AcceptDelay = 0)
{
    private static readonly string PathOnDisk = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LolMatchAssistant", "preferences.json");

    public static AssistantPreferences Load()
    {
        try { return JsonSerializer.Deserialize<AssistantPreferences>(File.ReadAllText(PathOnDisk)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
        var temporary = PathOnDisk + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, PathOnDisk, overwrite: true);
    }
}

internal static class AssistantTheme
{
    public const int LastChoice = 6;
    private static readonly string[] Keys =
    [
        "Ink", "Muted", "Paper", "Panel", "Stroke", "Accent", "Rail", "RailText", "AccentInk",
        "HoverSurface", "SelectedSurface", "SelectionStroke", "WinSurface", "WinStroke", "WinInk",
        "LossSurface", "LossStroke", "LossInk", "BlueSurface", "BlueInk", "ScrollThumb", "RadarPersonal"
    ];
    private static readonly string[][] Palettes =
    [
        ["E8EFEE", "ADBDBB", "161D21", "20292E", "354246", "75C4A7", "1A2327", "C6D8D1", "132823",
         "273639", "2B403E", "608D80", "202E30", "3B5450", "97D5BD", "2B2226", "50353D", "F1A49A", "253B47", "B7DFEC", "546968", "F1BC72"],
        ["1D3032", "4E6265", "EDF2F2", "FFFFFF", "C4D1D2", "23785D", "DEE9E7", "243F40", "FFFFFF",
         "DCE9E6", "CDE3DB", "679D8B", "E9F3EF", "AACDC0", "1E654E", "F9EDEE", "DFB7BC", "9A373D", "E4EEF6", "284E66", "91AAA8", "9E4E20"],
        ["E8EEF9", "A9B7CE", "171E2D", "202A3D", "374761", "97B7F8", "1B2537", "CDD9F4", "172442",
         "293754", "33486D", "688BB9", "23334C", "3C5271", "A1C5FE", "322734", "5C3D54", "F1ACB7", "293C5D", "C0D9FF", "536684", "F1BC72"],
        ["263831", "51655B", "EDF5F0", "F8FCFA", "C1D7CD", "246F58", "DFEDE5", "264237", "FFFFFF",
         "DBEBE2", "CBDDD4", "72A991", "DFEEF5", "B4D2E2", "1D5175", "F6E7EA", "E1BBC3", "952D48", "DCEAF4", "2D5474", "819E8F", "964B1D"],
        ["F4EEE3", "C3B6A2", "211F23", "2B2A30", "494348", "EBC182", "252329", "E5D9C7", "392715",
         "3E3634", "4A3E37", "AD895D", "29393A", "486668", "A7DDCA", "3C2A32", "66414F", "F4ACB6", "293748", "AFD3F2", "736A72", "F1BC72"],
        ["302B3C", "635B72", "F5F0FA", "FEFBFF", "DBCEEA", "684198", "EEE4F7", "423557", "FFFFFF",
         "EDE3F6", "E2D3F1", "9374B8", "E5EDF9", "BDCEE9", "2C517C", "F9E9EE", "E8BFCF", "9B3659", "E5E8FB", "434A86", "A397B7", "974C26"]
    ];

    public static void Apply(int theme)
    {
        var light = theme == 0 && Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is 1;
        var colors = theme == 0 ? Palettes[light ? 1 : 0] : Palettes[Math.Clamp(theme - 1, 0, Palettes.Length - 1)];
        for (var i = 0; i < Keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + colors[i]));
            brush.Freeze();
            Application.Current.Resources[Keys[i]] = brush;
        }
    }
}
