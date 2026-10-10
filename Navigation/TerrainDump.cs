using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace WhatsARoute.Navigation;

/// <summary>
/// An area's raw terrain on disk, for the offline benchmark (tools\route-bench in the scaffolding): the exact
/// RawPathfindingData and RawTerrainHeightData the HUD gave on entry, so FlowField / EdgeMap / PathShaper can be
/// replayed against real layouts without the game. gzip: "WART1", int W, int H, then per row W pathfinding bytes and
/// W float heights. Written on a worker, once per area instance.
/// </summary>
public static class TerrainDump
{
    private const string Magic = "WART1";

    public static void Write(string file, int[][] pf, float[][]? height)
    {
        if (File.Exists(file)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        int h = pf.Length, w = pf[0].Length;
        var tmp = file + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var bw = new BinaryWriter(gz))
        {
            bw.Write(Magic.ToCharArray());
            bw.Write(w); bw.Write(h);
            var row = new byte[w];
            for (var y = 0; y < h; y++)
            {
                var src = pf[y];
                for (var x = 0; x < w; x++) row[x] = x < src.Length ? (byte)Math.Clamp(src[x], 0, 255) : (byte)0;
                bw.Write(row);
                var hr = height != null && y < height.Length ? height[y] : null;
                for (var x = 0; x < w; x++) bw.Write(hr != null && x < hr.Length ? hr[x] : 0f);
            }
        }
        File.Move(tmp, file, true);
    }

    public static (int[][] pf, float[][] height) Read(string file)
    {
        using var fs = File.OpenRead(file);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var br = new BinaryReader(gz);
        if (new string(br.ReadChars(Magic.Length)) != Magic) throw new InvalidDataException($"{file}: not a {Magic} terrain dump");
        int w = br.ReadInt32(), h = br.ReadInt32();
        var pf = new int[h][];
        var height = new float[h][];
        for (var y = 0; y < h; y++)
        {
            var row = br.ReadBytes(w);
            pf[y] = new int[w];
            for (var x = 0; x < w; x++) pf[y][x] = row[x];
            height[y] = new float[w];
            for (var x = 0; x < w; x++) height[y][x] = br.ReadSingle();
        }
        return (pf, height);
    }
}

/// <summary>One objective the route resolved in a dumped area: the goal cells and where the player stood (benchmark replay).</summary>
public sealed class GoalSampleDto
{
    public string Spec { get; set; } = "";
    public string Label { get; set; } = "";
    public List<int[]> Goals { get; set; } = new();
    public int[] Player { get; set; } = [];
    public double FieldBuildMs { get; set; }
}
