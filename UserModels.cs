using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace YuCap;

/// <summary>
/// 自分専用モデル: where the user's own training lives and which trained models exist. Everything is under one folder,
/// <c>%LOCALAPPDATA%\YuCapTrain</c> (or the folder named by the environment variable YUCAP_TRAIN_DIR): <c>data\&lt;profile&gt;</c> holds the
/// collected pictures, <c>models\&lt;name&gt;.hlsl</c> the trained networks tools/train_upscaler.py exports (same layout as the
/// Anime4K S, M or L network, so <see cref="D3DShaders.GetA4kCustom"/> compiles it and the existing passes run it), <c>venv</c> and <c>runs</c>
/// the training environment. Deleting the folder removes all of it.
/// </summary>
internal static class UserModels
{
    public static string Root
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("YUCAP_TRAIN_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuCapTrain");
        }
    }

    public static string ModelsDir => Path.Combine(Root, "models");
    public static string DataRoot => Path.Combine(Root, "data");

    /// <summary>The picture folder for a profile name (invalid file-name characters replaced; an empty name is "default").</summary>
    public static string DataDirFor(string? profile)
    {
        string n = string.IsNullOrWhiteSpace(profile) ? "default" : profile.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return Path.Combine(DataRoot, n);
    }

    public static string PathOf(string name) => Path.Combine(ModelsDir, name + ".hlsl");

    /// <summary>Names of the usable trained models (an .hlsl whose first line is the YUCAP-MODEL marker with arch=S, M or L), newest first.</summary>
    public static List<string> List()
    {
        var res = new List<(string Name, DateTime T)>();
        try
        {
            if (!Directory.Exists(ModelsDir)) return new List<string>();
            foreach (string f in Directory.EnumerateFiles(ModelsDir, "*.hlsl"))
                if (IsModelFile(f)) res.Add((Path.GetFileNameWithoutExtension(f), File.GetLastWriteTimeUtc(f)));
        }
        catch (Exception ex) { Log.Info("user models: listing failed - " + ex.Message.Split('\n')[0]); }
        return res.OrderByDescending(x => x.T).Select(x => x.Name).ToList();
    }

    public static bool IsModelFile(string path)
    {
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            string? first = r.ReadLine();
            return ArchOfLine(first) != '\0';
        }
        catch { return false; }
    }

    /// <summary>The layout letter ('S', 'M' or 'L') in the first line of a model file, '\0' when it is not a YuCap model file.</summary>
    public static char ArchOf(string path)
    {
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            return ArchOfLine(r.ReadLine());
        }
        catch { return '\0'; }
    }

    private static char ArchOfLine(string? first)
    {
        if (first == null || !first.StartsWith("// YUCAP-MODEL", StringComparison.Ordinal)) return '\0';
        foreach (char a in "SML")
            if (first.Contains("arch=" + a, StringComparison.Ordinal)) return a;
        return '\0';
    }

    /// <summary>The model file to use for the setting value <paramref name="name"/> (empty = the built-in network), or null when it does not exist.</summary>
    public static string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)) return null;     // a name, never a path
        string p = PathOf(name);
        return File.Exists(p) && IsModelFile(p) ? p : null;
    }
}
