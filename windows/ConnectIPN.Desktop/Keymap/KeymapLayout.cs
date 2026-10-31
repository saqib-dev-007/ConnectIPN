using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectIPN;

internal sealed class KeymapLayout
{
    public string Name { get; set; } = "ConnectIPN layout";

    public List<KeymapControl> Controls { get; set; } = new();

    private static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectIPN");
            return Path.Combine(dir, "keymap.json");
        }
    }

    public static KeymapLayout CreateDefault()
    {
        var layout = new KeymapLayout();
        layout.Controls.Add(new KeymapControl
        {
            Type = "stick", Label = "WASD", X = 0.06, Y = 0.60, Size = 0.28, Cx = 0.20, Cy = 0.80,
        });
        layout.Controls.Add(new KeymapControl
        {
            Type = "button", Label = "Jump", Key = "Space", X = 0.82, Y = 0.78, Size = 0.13, Cx = 0.88, Cy = 0.86,
        });
        layout.Controls.Add(new KeymapControl
        {
            Type = "button", Label = "Fire", Key = "Ctrl", X = 0.66, Y = 0.66, Size = 0.13, Cx = 0.72, Cy = 0.78,
        });
        layout.Controls.Add(new KeymapControl
        {
            Type = "look", Label = "Look", X = 0.42, Y = 0.16, W = 0.54, H = 0.42,
        });
        return layout;
    }

    public static KeymapLayout Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                KeymapLayout? loaded = JsonSerializer.Deserialize<KeymapLayout>(
                    File.ReadAllText(FilePath), Json.Options);
                if (loaded is not null && loaded.Controls.Count > 0)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // a corrupt editor file must not block the window
        }
        return CreateDefault();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json.Options));
        }
        catch
        {
            // best effort
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json.Options);
}

internal sealed class KeymapControl
{
    public string Type { get; set; } = "button";
    public string Label { get; set; } = "";
    public string Key { get; set; } = "";
    public double X { get; set; } = 0.74;
    public double Y { get; set; } = 0.72;
    public double Size { get; set; } = 0.13;
    public double W { get; set; } = 0.4;
    public double H { get; set; } = 0.4;
    public double Cx { get; set; } = 0.82;
    public double Cy { get; set; } = 0.84;
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };
}
