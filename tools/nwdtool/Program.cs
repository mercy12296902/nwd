// nwdtool：读 Navisworks（.nwd/.nwc），列清单、按保留清单导出几何。基于 NwcIO（本仓库修补版）。
//
//   nwdtool inventory <in.nwd> <out_dir>
//       inventory.json：长度单位、模型文件（第 1 层）、楼层（第 2 层，带该层楼板顶面标高的中位数）、类别（第 3 层）、
//       族（第 4 层，每个类别取面数最多的 40 个），各带网格数、三角面数、包围盒（米，世界坐标）
//
//   nwdtool export <in.nwd> <keep.json> <out_dir>
//       keep.json（模型按 skill 写）：
//         { "keep": [ {"file": 正则, "category": 正则, "family": 正则, "level": 正则}, ... ],   每项各字段可省，省略即任意
//           "drop": [ 同上 ],                       在 keep 之后再排除
//           "lights": [ 同上 ],                     只输出位置（灯位），不输出几何
//           "unit_m": 0.3048,                       文档长度单位换成米（省略时按 inventory 的判断）
//           "origin": [x, y],                       世界坐标原点（米）；省略时取保留部分包围盒的中心
//           "ground_z": 103.9 }                     地面标高（米），导出后 z = 原 z − ground_z
//       输出：groups.json（每组：楼层、类别、模型文件、Revit 材质名和颜色、顶点和三角形数、数据文件名）、
//             g_<n>.bin（float32 顶点 xyz 米，Z 向上，接 uint32 三角形索引）、lights.json、export_report.json
//       同一（模型文件、楼层、类别、材质）合成一组，减少物体数；每组顶点上限约 400 万，超出就拆成下一组。
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NwcIO;
using NwcIO.Model;

var inv = CultureInfo.InvariantCulture;
if (args.Length < 3) { Console.Error.WriteLine("用法见文件开头"); return 2; }
var doc = NwcDocument.Open(args[1]);
var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

static List<NwcNode> Chain(NwcNode? n) { var l = new List<NwcNode>(); for (var p = n; p != null; p = p.Parent) l.Add(p); l.Reverse(); return l; }
static string Lv(List<NwcNode> c, int i) => c.Count > i ? c[i].Name ?? "" : "";

// 长度单位：Revit 导出的文件是英尺；其他来源 NwcIO 读不出单位，按米并在报告里写明
double unitGuess = doc.MetresPerUnit ?? 1.0;
string unitNote = doc.MetresPerUnit is { } mpu ? $"{doc.LengthUnit}（NwcIO 判断，{mpu} m/单位）" : "未知，按米；请用已知尺寸核对后在 keep.json 写 unit_m";
if (doc.MetresPerUnit is null && doc.PropertyRecords.Any(r => (r.Class ?? "").StartsWith("LcRevit", StringComparison.Ordinal)))
{ unitGuess = 0.3048; unitNote = "英尺（属性里有 LcRevit 记录：Revit 导出的文件内部单位是英尺）"; }

if (args[0] == "inventory")
{
    Directory.CreateDirectory(args[2]);
    var files = new Dictionary<string, Acc>(); var levels = new Dictionary<string, Acc>(); var cats = new Dictionary<string, Acc>(); var fams = new Dictionary<string, Acc>();
    var floorTop = new Dictionary<string, List<double>>();
    int n = 0;
    foreach (var m in doc.Meshes)
    {
        var c = Chain(m.Node);
        string f = Lv(c, 1), l = Lv(c, 2), k = Lv(c, 3), fa = Lv(c, 4);
        var bb = m.GetWorldBoundingBox(); var t = m.TriangleCount;
        Acc.Add(files, f, t, bb, unitGuess); Acc.Add(levels, f + " | " + l, t, bb, unitGuess); Acc.Add(cats, k, t, bb, unitGuess); Acc.Add(fams, k + " | " + fa, t, bb, unitGuess);
        if (k == "Floors") { (floorTop.TryGetValue(l, out var z) ? z : floorTop[l] = []).Add(bb.Max.Z * unitGuess); }
        n++;
    }
    object Rows(Dictionary<string, Acc> d, int take = 1000) => d.OrderByDescending(x => x.Value.Tris).Take(take).Select(x => x.Value.Row(x.Key)).ToList();
    var famRows = fams.GroupBy(x => x.Key.Split(" | ")[0]).SelectMany(g => g.OrderByDescending(x => x.Value.Tris).Take(40)).Select(x => x.Value.Row(x.Key)).ToList();
    var floorLevels = floorTop.Select(kv => new { level = kv.Key, floor_top_median_m = Math.Round(Median(kv.Value), 3), floors = kv.Value.Count }).OrderBy(x => x.floor_top_median_m).ToList();
    var outj = new { unit_m = unitGuess, unit_note = unitNote, meshes = n, triangles = files.Values.Sum(a => a.Tris),
        files = Rows(files), levels = Rows(levels), categories = Rows(cats), families_top40_per_category = famRows, floor_levels = floorLevels };
    File.WriteAllText(Path.Combine(args[2], "inventory.json"), JsonSerializer.Serialize(outj, opts));
    Console.WriteLine($"网格 {n}，三角面 {files.Values.Sum(a => a.Tris) / 1e4:F1} 万，模型文件 {files.Count}，类别 {cats.Count}，单位 {unitNote}");
    return 0;
}

if (args[0] == "export")
{
    var keep = JsonDocument.Parse(File.ReadAllText(args[2])).RootElement;
    string outDir = args[3]; Directory.CreateDirectory(outDir);
    List<Dictionary<string, Regex>> Rules(string name) => keep.TryGetProperty(name, out var a)
        ? a.EnumerateArray().Select(r => r.EnumerateObject().ToDictionary(p => p.Name, p => new Regex("^(?:" + p.Value.GetString() + ")$", RegexOptions.IgnoreCase))).ToList() : [];
    var kRules = Rules("keep"); var dRules = Rules("drop"); var lRules = Rules("lights");
    static bool Hit(List<Dictionary<string, Regex>> rules, string f, string l, string k, string fa) => rules.Any(r =>
        (!r.TryGetValue("file", out var a) || a.IsMatch(f)) && (!r.TryGetValue("level", out var b) || b.IsMatch(l)) &&
        (!r.TryGetValue("category", out var c) || c.IsMatch(k)) && (!r.TryGetValue("family", out var d) || d.IsMatch(fa)));
    double u = keep.TryGetProperty("unit_m", out var um) ? um.GetDouble() : unitGuess;
    double gz = keep.TryGetProperty("ground_z", out var g) ? g.GetDouble() : 0;

    // 第一遍：分类、算保留部分的范围
    var kept = new List<(NwcMesh M, string F, string L, string K, string Fa)>(); var lights = new List<object>();
    double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
    long dropTris = 0, keepTris = 0; int dropN = 0;
    var lightSeen = new HashSet<NwcNode>();
    foreach (var m in doc.Meshes)
    {
        var c = Chain(m.Node); string f = Lv(c, 1), l = Lv(c, 2), k = Lv(c, 3), fa = Lv(c, 4);
        if (Hit(lRules, f, l, k, fa))
        {
            var el = m.Node?.Element ?? m.Node; if (el is not null && lightSeen.Add(el)) lights.Add(new { node = el.Id, level = l, family = fa, box = m.GetWorldBoundingBox() });
            dropTris += m.TriangleCount; dropN++; continue;
        }
        if (!Hit(kRules, f, l, k, fa) || Hit(dRules, f, l, k, fa)) { dropTris += m.TriangleCount; dropN++; continue; }
        kept.Add((m, f, l, k, fa)); keepTris += m.TriangleCount;
        var bb = m.GetWorldBoundingBox();
        x0 = Math.Min(x0, bb.Min.X * u); y0 = Math.Min(y0, bb.Min.Y * u); x1 = Math.Max(x1, bb.Max.X * u); y1 = Math.Max(y1, bb.Max.Y * u);
    }
    if (kept.Count == 0) { Console.Error.WriteLine("保留清单没有选中任何网格"); return 3; }
    double ox = (x0 + x1) / 2, oy = (y0 + y1) / 2;
    if (keep.TryGetProperty("origin", out var o)) { ox = o[0].GetDouble(); oy = o[1].GetDouble(); }

    // 第二遍：按（文件、楼层、类别、材质）分组写二进制
    var groups = new List<object>(); var cur = new Dictionary<string, Grp>(); int gi = 0;
    void Flush(string key)
    {
        var gr = cur[key]; if (gr.V.Count == 0) return;
        string fn = $"g_{gi}.bin";
        using (var bw = new BinaryWriter(File.Create(Path.Combine(outDir, fn))))
        { foreach (var v in gr.V) bw.Write(v); foreach (var i in gr.I) bw.Write(i); }
        groups.Add(new { id = gi, file = fn, model = gr.F, level = gr.L, category = gr.K, material = gr.Mat, color = gr.Col, verts = gr.V.Count / 3, tris = gr.I.Count / 3, elements = gr.Elements.Count });
        gi++; cur.Remove(key);
    }
    foreach (var (m, f, l, k, fa) in kept)
    {
        string mat = m.Material?.Name ?? "";
        string key = f + "\u0001" + l + "\u0001" + k + "\u0001" + mat;
        if (!cur.TryGetValue(key, out var gr)) cur[key] = gr = new Grp(f, l, k, mat, m.Material);
        var vs = m.GetWorldVertices(); int b = gr.V.Count / 3;
        foreach (var v in vs) { gr.V.Add((float)(v.X * u - ox)); gr.V.Add((float)(v.Y * u - oy)); gr.V.Add((float)(v.Z * u - gz)); }
        foreach (var i in m.Indices) gr.I.Add((uint)(i + b));
        if (m.Node is not null) gr.Elements.Add((m.Node.Element ?? m.Node).Id);
        if (gr.V.Count / 3 > 4_000_000) Flush(key);
    }
    foreach (var key in cur.Keys.ToList()) Flush(key);
    var lightOut = lights.Select(x => { var j = JsonSerializer.SerializeToElement(x); var bx = j.GetProperty("box");
        double cx = (bx.GetProperty("Min").GetProperty("X").GetDouble() + bx.GetProperty("Max").GetProperty("X").GetDouble()) / 2 * u - ox;
        double cy = (bx.GetProperty("Min").GetProperty("Y").GetDouble() + bx.GetProperty("Max").GetProperty("Y").GetDouble()) / 2 * u - oy;
        double cz = bx.GetProperty("Min").GetProperty("Z").GetDouble() * u - gz;
        return new { level = j.GetProperty("level").GetString(), family = j.GetProperty("family").GetString(), pos = new[] { Math.Round(cx, 3), Math.Round(cy, 3), Math.Round(cz, 3) } }; }).ToList();
    File.WriteAllText(Path.Combine(outDir, "groups.json"), JsonSerializer.Serialize(new { unit_m = u, origin_m = new[] { ox, oy }, ground_z_m = gz, groups }, opts));
    File.WriteAllText(Path.Combine(outDir, "lights.json"), JsonSerializer.Serialize(lightOut, opts));
    var rep = new { kept_meshes = kept.Count, kept_triangles = keepTris, dropped_meshes = dropN, dropped_triangles = dropTris, groups = gi, lights = lightOut.Count,
        extent_m = new[] { Math.Round(x1 - x0, 2), Math.Round(y1 - y0, 2) } };
    File.WriteAllText(Path.Combine(outDir, "export_report.json"), JsonSerializer.Serialize(rep, opts));
    Console.WriteLine($"保留 {kept.Count} 个网格 {keepTris / 1e4:F1} 万面，删 {dropN} 个 {dropTris / 1e4:F1} 万面，分 {gi} 组，灯位 {lightOut.Count}，范围 {x1 - x0:F1}×{y1 - y0:F1} m");
    return 0;
}
Console.Error.WriteLine("命令只有 inventory / export"); return 2;

static double Median(List<double> v) { v.Sort(); return v.Count == 0 ? 0 : v[v.Count / 2]; }

sealed class Acc
{
    public long N, Tris; public double X0 = double.MaxValue, Y0 = double.MaxValue, Z0 = double.MaxValue, X1 = double.MinValue, Y1 = double.MinValue, Z1 = double.MinValue;
    public static void Add(Dictionary<string, Acc> d, string k, int t, BoundingBox3d b, double u)
    {
        if (!d.TryGetValue(k, out var a)) d[k] = a = new Acc();
        a.N++; a.Tris += t;
        a.X0 = Math.Min(a.X0, b.Min.X * u); a.Y0 = Math.Min(a.Y0, b.Min.Y * u); a.Z0 = Math.Min(a.Z0, b.Min.Z * u);
        a.X1 = Math.Max(a.X1, b.Max.X * u); a.Y1 = Math.Max(a.Y1, b.Max.Y * u); a.Z1 = Math.Max(a.Z1, b.Max.Z * u);
    }
    public object Row(string name) => new { name, meshes = N, tris = Tris, bbox_m = new[] { Math.Round(X0, 2), Math.Round(Y0, 2), Math.Round(Z0, 2), Math.Round(X1, 2), Math.Round(Y1, 2), Math.Round(Z1, 2) } };
}

sealed class Grp(string f, string l, string k, string mat, NwcMaterial? m)
{
    public string F = f, L = l, K = k, Mat = mat;
    public float[] Col = ColorOf(m);
    public List<float> V = []; public List<uint> I = []; public HashSet<int> Elements = [];
    static float[] ColorOf(NwcMaterial? m) => m is null ? [0.8f, 0.8f, 0.8f, 1f] : [m.Color.R, m.Color.G, m.Color.B, m.Color.A];
}
