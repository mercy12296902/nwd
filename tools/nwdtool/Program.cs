// nwdtool：读 Navisworks（.nwd/.nwc），列清单、按保留清单导出几何。基于 NwcIO（本仓库修补版）。
//
// 标签从哪来：每个网格属于层级树里的一个节点，往上的祖先节点名就是它的标签。"模型文件、楼层、类别、族"各在第几层，
// 取决于导出模型的软件（Revit 导出器是 文件 > 楼层 > 类别 > 族 > 类型 > 构件，即 1/2/3/4）。这里不假设，由 depth 声明：
//   depth = {"file": 1, "level": 2, "category": 3, "family": 4}（省略时用这组，对应 Revit；别的来源先看 inventory 的 tree_samples 再写）
//
//   nwdtool inventory <in.nwd> <out_dir> [depth 的 JSON]
//       inventory.json：长度单位；tree_samples（每个模型文件几条完整层级路径，用来核对 depth）；
//       按 depth 统计的模型文件、楼层（level_base_z：floor_top_m 是该层扁平大构件顶面标高的众数，约等于楼面标高；
//       base_z_mode_m 是全部构件底面标高的众数，结构模型里常落在板底；都不依赖类别名和语言）、
//       类别、族（每个类别取面数最多的 40 个），各带网格数、三角面数、包围盒（米，世界坐标）
//
//   nwdtool export <in.nwd> <keep.json> <out_dir>
//       keep.json（模型按 skill 写）：
//         { "depth":  {…},                             同上，省略即 Revit 的层级
//           "keep":   [ {"file": 正则, "level": 正则, "category": 正则, "family": 正则, "path": 正则}, … ]   字段可省，省略即任意
//           "drop":   [ 同上 ],                        在 keep 之后再排除
//           "lights": [ 同上 ],                        只输出位置（灯位），不输出几何
//           "ground": [ 同上 ],                        保留下来的这些归到 Ground 模块（场地、地形），其余归 Main
//           "unit_m": 0.3048,                          文档长度单位换成米（省略时按 inventory 的判断）
//           "origin": [x, y],                          世界坐标原点（米）；省略时取保留部分包围盒的中心
//           "ground_z": 0 }                            地面标高（米），导出后 z = 原 z − ground_z
//       正则整段匹配、不区分大小写；path 匹配用 " > " 连起来的整条祖先路径（不含根）。
//       输出：groups.json（每组：模块、楼层、类别、模型文件、材质名和颜色、顶点和三角形数、数据文件名）、
//             g_<n>.bin（float32 顶点 xyz 米，Z 向上，接 uint32 三角形索引）、lights.json、export_report.json
//       同一（模型文件、楼层、类别、材质）合成一组，减少物体数；每组顶点上限约 400 万，超出就拆成下一组。
using System.Text.Json;
using System.Text.RegularExpressions;
using NwcIO;
using NwcIO.Model;

if (args.Length < 3) { Console.Error.WriteLine("用法见文件开头"); return 2; }
var doc = NwcDocument.Open(args[1]);
var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

static List<NwcNode> Chain(NwcNode? n) { var l = new List<NwcNode>(); for (var p = n; p != null; p = p.Parent) l.Add(p); l.Reverse(); return l; }

// 长度单位：NwcIO 能判断的用它的；判断不了但属性里有 Revit 记录的，按 Revit 的内部单位英尺；都不行按米并要求核对
double unitGuess = doc.MetresPerUnit ?? 1.0;
string unitNote = doc.MetresPerUnit is { } mpu ? $"{doc.LengthUnit}（NwcIO 判断，{mpu} m/单位）" : "未知，按米；请用已知尺寸核对后在 keep.json 写 unit_m";
if (doc.MetresPerUnit is null && doc.PropertyRecords.Any(r => (r.Class ?? "").StartsWith("LcRevit", StringComparison.Ordinal)))
{ unitGuess = 0.3048; unitNote = "英尺（属性里有 LcRevit 记录：Revit 的内部长度单位是英尺）"; }

if (args[0] == "inventory")
{
    var depth = Depth.From(args.Length > 3 ? JsonDocument.Parse(args[3]).RootElement : (JsonElement?)null);
    Directory.CreateDirectory(args[2]);
    var files = new Dictionary<string, Acc>(); var levels = new Dictionary<string, Acc>(); var cats = new Dictionary<string, Acc>(); var fams = new Dictionary<string, Acc>();
    var baseZ = new Dictionary<string, Dictionary<double, int>>(); var flatTop = new Dictionary<string, Dictionary<double, int>>();
    var samples = new Dictionary<string, List<string[]>>();
    int n = 0, maxDepth = 0;
    foreach (var m in doc.Meshes)
    {
        var c = Chain(m.Node); var lb = depth.Labels(c);
        var bb = m.GetWorldBoundingBox(); var t = m.TriangleCount;
        Acc.Add(files, lb.File, t, bb, unitGuess); Acc.Add(levels, lb.File + " | " + lb.Level, t, bb, unitGuess); Acc.Add(cats, lb.Category, t, bb, unitGuess); Acc.Add(fams, lb.Category + " | " + lb.Family, t, bb, unitGuess);
        // 楼面标高，不看类别名和语言：扁平的大构件（高 < 0.5 m、面积 > 1 m²：楼板、地面一类）顶面标高的众数。
        // 只取"构件底面"的众数会落在板底（结构模型里板最多），低一个板厚，所以两个都列出来对照
        var key = lb.File + " | " + lb.Level;
        double hz = (bb.Max.Z - bb.Min.Z) * unitGuess, area = (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y) * unitGuess * unitGuess;
        if (!baseZ.TryGetValue(key, out var h)) baseZ[key] = h = [];
        var zb = Math.Round(bb.Min.Z * unitGuess, 2);
        h[zb] = h.GetValueOrDefault(zb) + 1;
        if (hz < 0.5 && area > 1)
        {
            if (!flatTop.TryGetValue(key, out var ft)) flatTop[key] = ft = [];
            var zt = Math.Round(bb.Max.Z * unitGuess, 2);
            ft[zt] = ft.GetValueOrDefault(zt) + 1;
        }
        var top = c.Count > 1 ? c[1].Name ?? "" : "";
        if (!samples.TryGetValue(top, out var s)) samples[top] = s = [];
        if (s.Count < 3) s.Add(c.Skip(1).Select(x => x.Name ?? "").ToArray());
        maxDepth = Math.Max(maxDepth, c.Count - 1);
        n++;
    }
    object Rows(Dictionary<string, Acc> d) => d.OrderByDescending(x => x.Value.Tris).Select(x => x.Value.Row(x.Key)).ToList();
    var famRows = fams.GroupBy(x => x.Key.Split(" | ")[0]).SelectMany(g => g.OrderByDescending(x => x.Value.Tris).Take(40)).Select(x => x.Value.Row(x.Key)).ToList();
    var levelZ = baseZ.Select(kv =>
    {
        var best = kv.Value.MaxBy(x => x.Value);
        var flat = flatTop.TryGetValue(kv.Key, out var ft) && ft.Count > 0 ? ft.MaxBy(x => x.Value) : default;
        return new { level = kv.Key, floor_top_m = ft is { Count: > 0 } ? flat.Key : (double?)null, flat_at_mode = flat.Value, flat_meshes = ft?.Values.Sum() ?? 0,
            base_z_mode_m = best.Key, at_mode = best.Value, meshes = kv.Value.Values.Sum() };
    }).OrderBy(x => x.floor_top_m ?? x.base_z_mode_m).ToList();
    var outj = new { unit_m = unitGuess, unit_note = unitNote, depth = depth.ToJson(), max_depth = maxDepth, meshes = n, triangles = files.Values.Sum(a => a.Tris),
        tree_samples = samples, files = Rows(files), levels = Rows(levels), level_base_z = levelZ, categories = Rows(cats), families_top40_per_category = famRows };
    File.WriteAllText(Path.Combine(args[2], "inventory.json"), JsonSerializer.Serialize(outj, opts));
    Console.WriteLine($"网格 {n}，三角面 {files.Values.Sum(a => a.Tris) / 1e4:F1} 万，模型文件 {files.Count}，类别 {cats.Count}，层级最深 {maxDepth}，单位 {unitNote}");
    return 0;
}

if (args[0] == "export")
{
    var keep = JsonDocument.Parse(File.ReadAllText(args[2])).RootElement;
    var depth = Depth.From(keep.TryGetProperty("depth", out var dj) ? dj : null);
    string outDir = args[3]; Directory.CreateDirectory(outDir);
    List<Dictionary<string, Regex>> Rules(string name) => keep.TryGetProperty(name, out var a)
        ? a.EnumerateArray().Select(r => r.EnumerateObject().Where(p => !p.Name.StartsWith('_'))
            .ToDictionary(p => p.Name, p => new Regex("^(?:" + p.Value.GetString() + ")$", RegexOptions.IgnoreCase))).ToList() : [];
    var kRules = Rules("keep"); var dRules = Rules("drop"); var lRules = Rules("lights"); var gRules = Rules("ground");
    static bool Hit(List<Dictionary<string, Regex>> rules, Lb x) => rules.Any(r =>
        (!r.TryGetValue("file", out var a) || a.IsMatch(x.File)) && (!r.TryGetValue("level", out var b) || b.IsMatch(x.Level)) &&
        (!r.TryGetValue("category", out var c) || c.IsMatch(x.Category)) && (!r.TryGetValue("family", out var d) || d.IsMatch(x.Family)) &&
        (!r.TryGetValue("path", out var e) || e.IsMatch(x.Path)));
    double u = keep.TryGetProperty("unit_m", out var um) ? um.GetDouble() : unitGuess;
    double gz = keep.TryGetProperty("ground_z", out var g) ? g.GetDouble() : 0;

    // 第一遍：分类、算保留部分的范围
    var kept = new List<(NwcMesh M, Lb L)>(); var lights = new List<(Lb L, BoundingBox3d B)>();
    double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
    long dropTris = 0, keepTris = 0; int dropN = 0;
    var lightSeen = new HashSet<NwcNode>();
    foreach (var m in doc.Meshes)
    {
        var lb = depth.Labels(Chain(m.Node));
        if (Hit(lRules, lb))
        {
            var el = m.Node?.Element ?? m.Node; if (el is not null && lightSeen.Add(el)) lights.Add((lb, m.GetWorldBoundingBox()));
            dropTris += m.TriangleCount; dropN++; continue;
        }
        if (!Hit(kRules, lb) || Hit(dRules, lb)) { dropTris += m.TriangleCount; dropN++; continue; }
        kept.Add((m, lb)); keepTris += m.TriangleCount;
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
        groups.Add(new { id = gi, file = fn, module = gr.Module, model = gr.L.File, level = gr.L.Level, category = gr.L.Category, material = gr.Mat, color = gr.Col,
            verts = gr.V.Count / 3, tris = gr.I.Count / 3, elements = gr.Elements.Count });
        gi++; cur.Remove(key);
    }
    foreach (var (m, lb) in kept)
    {
        string mat = m.Material?.Name ?? "", module = Hit(gRules, lb) ? "Ground" : "Main";
        string key = module + "\u0001" + lb.File + "\u0001" + lb.Level + "\u0001" + lb.Category + "\u0001" + mat;
        if (!cur.TryGetValue(key, out var gr)) cur[key] = gr = new Grp(module, lb, mat, m.Material);
        var vs = m.GetWorldVertices(); int b = gr.V.Count / 3;
        foreach (var v in vs) { gr.V.Add((float)(v.X * u - ox)); gr.V.Add((float)(v.Y * u - oy)); gr.V.Add((float)(v.Z * u - gz)); }
        foreach (var i in m.Indices) gr.I.Add((uint)(i + b));
        if (m.Node is not null) gr.Elements.Add((m.Node.Element ?? m.Node).Id);
        if (gr.V.Count / 3 > 4_000_000) Flush(key);
    }
    foreach (var key in cur.Keys.ToList()) Flush(key);
    // 灯位：构件包围盒的水平中心、底面高度
    var lightOut = lights.Select(x => new { level = x.L.Level, family = x.L.Family,
        pos = new[] { Math.Round((x.B.Min.X + x.B.Max.X) / 2 * u - ox, 3), Math.Round((x.B.Min.Y + x.B.Max.Y) / 2 * u - oy, 3), Math.Round(x.B.Min.Z * u - gz, 3) } }).ToList();
    File.WriteAllText(Path.Combine(outDir, "groups.json"), JsonSerializer.Serialize(new { unit_m = u, origin_m = new[] { ox, oy }, ground_z_m = gz, depth = depth.ToJson(), groups }, opts));
    File.WriteAllText(Path.Combine(outDir, "lights.json"), JsonSerializer.Serialize(lightOut, opts));
    var rep = new { kept_meshes = kept.Count, kept_triangles = keepTris, dropped_meshes = dropN, dropped_triangles = dropTris, groups = gi, lights = lightOut.Count,
        extent_m = new[] { Math.Round(x1 - x0, 2), Math.Round(y1 - y0, 2) } };
    File.WriteAllText(Path.Combine(outDir, "export_report.json"), JsonSerializer.Serialize(rep, opts));
    Console.WriteLine($"保留 {kept.Count} 个网格 {keepTris / 1e4:F1} 万面，删 {dropN} 个 {dropTris / 1e4:F1} 万面，分 {gi} 组，灯位 {lightOut.Count}，范围 {x1 - x0:F1}×{y1 - y0:F1} m");
    return 0;
}
Console.Error.WriteLine("命令只有 inventory / export"); return 2;

// 网格的标签：按声明的层取祖先节点名
readonly record struct Lb(string File, string Level, string Category, string Family, string Path);

sealed class Depth
{
    public int File = 1, Level = 2, Category = 3, Family = 4;
    public static Depth From(JsonElement? j)
    {
        var d = new Depth();
        if (j is { } e && e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("file", out var a)) d.File = a.GetInt32();
            if (e.TryGetProperty("level", out var b)) d.Level = b.GetInt32();
            if (e.TryGetProperty("category", out var c)) d.Category = c.GetInt32();
            if (e.TryGetProperty("family", out var f)) d.Family = f.GetInt32();
        }
        return d;
    }
    public Lb Labels(List<NwcNode> c)
    {
        string At(int i) => i > 0 && c.Count > i ? c[i].Name ?? "" : "";
        return new Lb(At(File), At(Level), At(Category), At(Family), string.Join(" > ", c.Skip(1).Select(x => x.Name ?? "")));
    }
    public object ToJson() => new { file = File, level = Level, category = Category, family = Family };
}

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

sealed class Grp(string module, Lb l, string mat, NwcMaterial? m)
{
    public string Module = module, Mat = mat; public Lb L = l;
    public float[] Col = m is null ? [0.8f, 0.8f, 0.8f, 1f] : [m.Color.R, m.Color.G, m.Color.B, m.Color.A];
    public List<float> V = []; public List<uint> I = []; public HashSet<int> Elements = [];
}
