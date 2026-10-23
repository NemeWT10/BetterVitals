using System.Text;

namespace BetterVitals.Core.Sensors;

/// <summary>Builds the stable ids described on <see cref="HardwareDescriptor.Id"/> and <see cref="SensorDescriptor.Id"/>.</summary>
public static class SensorIds
{
    /// <summary>"NVIDIA GeForce RTX 5080" → "nvidia-geforce-rtx-5080"; "Core (Tctl/Tdie)" → "core-tctl-tdie"; "+3.3V" → "plus3-3v".</summary>
    public static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length);
        var dash = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                dash = false;
            }
            else if (ch == '+' && sb.Length == 0)
            {
                sb.Append("plus");
            }
            else if (ch == '-' && sb.Length == 0)
            {
                sb.Append("minus");
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }
        }
        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        return sb.Length == 0 ? "x" : sb.ToString();
    }

    /// <summary>"cpu/amd-ryzen-7-9800x3d/0" — kind + name slug + ordinal among hardware with the same kind and name.</summary>
    public static string Hardware(HardwareKind kind, string name, int ordinal, string? parentId = null)
    {
        var own = $"{Slug(kind.ToString())}/{Slug(name)}/{ordinal}";
        return parentId is null ? own : $"{parentId}/{own}";
    }

    /// <summary>"&lt;hardwareId&gt;/temperature/gpu-hot-spot" (+ "#k" for the k-th repeat of the same name, k ≥ 2).</summary>
    public static string Sensor(string hardwareId, SensorKind kind, string name, int repeat = 1)
        => repeat <= 1 ? $"{hardwareId}/{Slug(kind.ToString())}/{Slug(name)}" : $"{hardwareId}/{Slug(kind.ToString())}/{Slug(name)}#{repeat}";
}
