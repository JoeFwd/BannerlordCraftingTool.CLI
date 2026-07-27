using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Xml.Linq;
using System.Xml.Xsl;
using Assimp;
using Vector3D = System.Windows.Media.Media3D.Vector3D;

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
    Console.WriteLine("Usage:\n  BannerlordCraftingTool.Cli extract --pieces <crafting_pieces.xml> [--out <report.json>]\n  BannerlordCraftingTool.Cli inspect --submodule <SubModule.xml> --weapon <CraftedItem-id> [--weapon <CraftedItem-id> ...] [--out <report.json>]\n  BannerlordCraftingTool.Cli audit --submodule <SubModule.xml> --fbx-folder <folder> (--all-templates | --template <id> ...) [--tolerance <xml-units>] [--out <report.json>]\n  BannerlordCraftingTool.Cli render --submodule <SubModule.xml> --weapon <CraftedItem-id> --fbx-folder <folder> --out <dir>\n\n  The module dependency stack is resolved from SubModule.xml; use --modules-root <Modules> to override the selected module's parent directory.\n  audit measures visual mesh bounds from FBX source geometry using fixed-reference adjacent-piece sweeps; it never edits XML.\n  render assembles the weapon as the GUI does, renders three orthographic views to <dir>\\render.png, and emits 3D measurements to <dir>\\render.json.\n  One --weapon returns a flat report; repeated --weapon returns { weapons, source_files }.\n  layout is a compatibility alias for inspect.");
        return;
    }

    var options = ReadOptions(args[1..]);
    switch (args[0])
    {
        case "extract":
            Extract(options);
            break;
        case "inspect":
        case "layout":
            Layout(options);
            break;
        case "audit":
            Audit(options);
            break;
        case "render":
            Render(options);
            break;
        default:
            throw new ArgumentException($"Unknown command '{args[0]}'.");
    }
}
catch (Exception exception) when (exception is ArgumentException or IOException or System.Xml.XmlException or XsltException)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 2;
}

static void Extract(IReadOnlyDictionary<string, List<string>> options)
{
    var pieces = LoadPieces(RequiredOption(options, "pieces"));
    WriteJson(new { pieces }, options);
}

static void Layout(IReadOnlyDictionary<string, List<string>> options)
{
    if (options.ContainsKey("weapons"))
        throw new ArgumentException("--weapons is no longer supported. Repeat --weapon <CraftedItem-id> instead.");
    if (!options.TryGetValue("weapon", out var craftedItemIds))
        throw new ArgumentException("inspect requires at least one --weapon <CraftedItem-id>.");
    if (!options.TryGetValue("submodule", out var subModulePaths))
        throw new ArgumentException("--weapon requires --submodule.");

    var craftedItemData = LoadSubModuleCraftingData(SingleOption(subModulePaths, "submodule"), options, loadItems: true);
    var piecesById = craftedItemData.Pieces.ToDictionary(piece => piece.Id, StringComparer.OrdinalIgnoreCase);
    var reports = craftedItemIds
        .Select(craftedItemId => CalculateLayout(LoadCraftedItem(craftedItemData.Items!, craftedItemId), piecesById, craftedItemData.BuildOrders))
        .ToList();
    var sourceFiles = new
    {
        crafting_pieces = craftedItemData.SourceFiles.CraftingPieces,
        crafting_templates = craftedItemData.SourceFiles.CraftingTemplates,
        items = craftedItemData.ItemPaths,
    };
    if (reports.Count == 1)
    {
        var report = reports[0];
        WriteJson(new
        {
            report.Id,
            report.Template,
            report.BuildOrder,
            report.Pieces,
            report.Joints,
            report.WeaponLength,
            report.HandToBottom,
            source_files = sourceFiles,
        }, options);
        return;
    }

    WriteJson(new { weapons = reports, source_files = sourceFiles }, options);
}

static void Audit(IReadOnlyDictionary<string, List<string>> options)
{
    var subModule = RequiredOption(options, "submodule");
    var fbxFolder = RequiredOption(options, "fbx-folder");
    if (!Directory.Exists(fbxFolder)) throw new ArgumentException($"FBX folder does not exist: '{fbxFolder}'.");
    var allTemplates = options.ContainsKey("all-templates");
    var requestedTemplates = options.TryGetValue("template", out var templateIds) ? templateIds : [];
    if (allTemplates == (requestedTemplates.Count > 0))
        throw new ArgumentException("audit requires exactly one of --all-templates or --template <id>.");
    var tolerance = options.TryGetValue("tolerance", out var toleranceValues)
        ? ParseNonNegativeFloat(SingleOption(toleranceValues, "tolerance"), "tolerance")
        : 0.01f;

    var data = LoadSubModuleCraftingData(subModule, options);
    var piecesById = data.Pieces.ToDictionary(piece => piece.Id, StringComparer.OrdinalIgnoreCase);
    var templates = allTemplates
        ? data.Templates.Values.OrderBy(template => template.Id, StringComparer.Ordinal).ToList()
        : requestedTemplates.Distinct(StringComparer.OrdinalIgnoreCase).Select(templateId => data.Templates.TryGetValue(templateId, out var template)
            ? template
            : throw new ArgumentException($"No CraftingTemplate with id '{templateId}' was found in resolved CraftingTemplates XML.")).ToList();
    var meshBounds = new FbxBoundsIndex(fbxFolder);
    var records = new List<VisualAuditRecord>();
    var skipped = new List<AuditSkip>();

    foreach (var template in templates)
    {
        if (template.BuildOrder.GroupBy(item => item.Type, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            skipped.Add(new AuditSkip(template.Id, "duplicate_piece_type", null));
            continue;
        }
        foreach (var unresolvedId in template.UsablePieceIds.Where(id => !piecesById.ContainsKey(id)).Distinct(StringComparer.OrdinalIgnoreCase))
            skipped.Add(new AuditSkip(template.Id, "unresolved_usable_piece", unresolvedId));
        var usableByType = template.UsablePieceIds
            .Select(id => piecesById.TryGetValue(id, out var piece) ? piece : null)
            .Where(piece => piece is not null && template.BuildOrder.Any(order => order.Type.Equals(piece.PieceType, StringComparison.OrdinalIgnoreCase)))
            .Cast<CraftingPiece>()
            .GroupBy(piece => piece.PieceType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var ordered = template.BuildOrder.OrderBy(item => item.Order).ToArray();
        var missingTypes = template.BuildOrder
            .Where(order => !usableByType.TryGetValue(order.Type, out var usable) || usable.Count == 0)
            .Select(order => order.Type)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingTypes.Count > 0)
        {
            foreach (var missingType in missingTypes) skipped.Add(new AuditSkip(template.Id, "missing_usable_piece_type", missingType));
            continue;
        }
        foreach (var pair in ordered.Zip(ordered.Skip(1)))
        {
            var gripPieces = usableByType[pair.First.Type];
            var tipPieces = usableByType[pair.Second.Type];

            var gripReference = gripPieces[0];
            var tipReference = tipPieces[0];
            var baseline = CreateVisualAuditRecord(template, pair.First, pair.Second, gripReference, tipReference, "grip", usableByType, piecesById, meshBounds, tolerance);
            foreach (var candidate in gripPieces)
                records.Add(WithCorrectionPlan(CreateVisualAuditRecord(template, pair.First, pair.Second, candidate, tipReference, "grip", usableByType, piecesById, meshBounds, tolerance), baseline));
            foreach (var candidate in tipPieces.Skip(1))
                records.Add(WithCorrectionPlan(CreateVisualAuditRecord(template, pair.First, pair.Second, gripReference, candidate, "tip", usableByType, piecesById, meshBounds, tolerance), baseline));
        }
    }

    WriteJson(new
    {
        audit_strategy = "fixed_reference_adjacent_piece_sweep",
        tolerance,
        fbx_folder = Path.GetFullPath(fbxFolder),
        records,
        skipped,
        summary = new
        {
            combinations = records.Count,
            visual_failures = records.Count(record => record.VisualGap is float gap && MathF.Abs(gap) > tolerance),
            records_with_missing_meshes = records.Count(record => record.MissingMeshes.Count > 0),
        },
        source_files = new { crafting_pieces = data.SourceFiles.CraftingPieces, crafting_templates = data.SourceFiles.CraftingTemplates },
    }, options);
}

static void Render(IReadOnlyDictionary<string, List<string>> options)
{
    var subModule = RequiredOption(options, "submodule");
    var weaponId = RequiredOption(options, "weapon");
    var fbxFolder = RequiredOption(options, "fbx-folder");
    if (!Directory.Exists(fbxFolder)) throw new ArgumentException($"FBX folder does not exist: '{fbxFolder}'.");
    var outDir = RequiredOption(options, "out");
    Directory.CreateDirectory(outDir);

    var data = LoadSubModuleCraftingData(subModule, options, loadItems: true);
    var piecesById = data.Pieces.ToDictionary(piece => piece.Id, StringComparer.OrdinalIgnoreCase);
    var layout = CalculateLayout(LoadCraftedItem(data.Items!, weaponId), piecesById, data.BuildOrders);

    var meshIndex = new FbxMeshIndex(fbxFolder);
    var orderedPieces = layout.Pieces.OrderBy(piece => piece.BuildOrder).ToList();

    var assembled = new List<RenderPiece>();
    var missing = new List<string>();
    foreach (var piece in orderedPieces)
    {
        var geo = meshIndex.LoadMesh(piece.Mesh);
        var scale = piece.ScaleFactor * 0.01f;
        var pivot = piece.Pivot;
        Bounds? worldBounds = null;
        if (geo is not null)
        {
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
            foreach (var p in geo.Positions)
            {
                var x = (float)p.X * scale;
                var y = (float)p.Y * scale + pivot;
                var z = (float)p.Z * scale;
                minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
                minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y);
                minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z);
            }
            worldBounds = new Bounds(minX, minY, minZ, maxX, maxY, maxZ);
        }
        else
        {
            missing.Add(piece.Mesh);
        }
        assembled.Add(new RenderPiece(piece.Type, piece.Id, piece.Mesh, piece.ScaleFactor, piece.Pivot, geo, scale, pivot, worldBounds));
    }

    var found = assembled.Where(piece => piece.Bounds is not null).ToList();
    var imagePath = Path.GetFullPath(Path.Combine(outDir, "render.png"));

    BoundsReport? assembledBounds = null;
    if (found.Count > 0)
    {
        var min = new[] {
            found.Min(p => p.Bounds!.MinX), found.Min(p => p.Bounds!.MinY), found.Min(p => p.Bounds!.MinZ) };
        var max = new[] {
            found.Max(p => p.Bounds!.MaxX), found.Max(p => p.Bounds!.MaxY), found.Max(p => p.Bounds!.MaxZ) };
        assembledBounds = new BoundsReport(min, max);
        RenderImage(found, min, max, imagePath);
    }

    var pieceReports = assembled.Select(piece =>
    {
        var b = piece.Bounds;
        return new RenderPieceReport(
            piece.Type,
            piece.Id,
            piece.Mesh,
            piece.ScaleFactor,
            piece.PivotXml,
            b is not null,
            b is null ? null : new BoundsReport([b.MinX, b.MinY, b.MinZ], [b.MaxX, b.MaxY, b.MaxZ]),
            b is null ? null : [ (b.MinX + b.MaxX) / 2f, (b.MinY + b.MaxY) / 2f, (b.MinZ + b.MaxZ) / 2f ],
            b is null ? null : new LateralOffset((b.MinX + b.MaxX) / 2f, (b.MinZ + b.MaxZ) / 2f),
            b is null ? null : new AxisExtent(b.MinY, b.MaxY));
    }).ToList();

    var seams = new List<RenderSeam>();
    for (var i = 0; i + 1 < assembled.Count; i++)
    {
        var grip = assembled[i];
        var tip = assembled[i + 1];
        if (grip.Bounds is null || tip.Bounds is null) continue;
        seams.Add(new RenderSeam(grip.Type, tip.Type, tip.Bounds.MinY - grip.Bounds.MaxY));
    }

    var report = new
    {
        weapon = weaponId,
        template = layout.Template,
        image = imagePath,
        views = new[] { "side", "front", "top" },
        weapon_length = layout.WeaponLength,
        assembled_bounds = assembledBounds,
        pieces = pieceReports,
        seams,
        missing_meshes = missing,
    };
    var json = JsonSerializer.Serialize(report, CliJson.Options);
    File.WriteAllText(Path.Combine(outDir, "render.json"), json);
    Console.WriteLine(json);
}

static void RenderImage(List<RenderPiece> pieces, float[] min, float[] max, string imagePath)
{
    Exception? err = null;
    var thread = new Thread(() =>
    {
        try
        {
            var group = new Model3DGroup();
            foreach (var piece in pieces)
            {
                var color = PieceColor(piece.Type);
                var material = new DiffuseMaterial(new SolidColorBrush(color));
                var transform = new Transform3DGroup();
                transform.Children.Add(new ScaleTransform3D(piece.Scale, piece.Scale, piece.Scale));
                transform.Children.Add(new TranslateTransform3D(0, piece.Pivot, 0));
                group.Children.Add(new GeometryModel3D(piece.Geometry, material)
                {
                    BackMaterial = material,
                    Transform = transform,
                });
            }

            var center = new Point3D((min[0] + max[0]) / 2.0, (min[1] + max[1]) / 2.0, (min[2] + max[2]) / 2.0);
            var span = new[] { max[0] - min[0], max[1] - min[1], max[2] - min[2] };
            var maxSpan = Math.Max(span[0], Math.Max(span[1], span[2]));
            var distance = Math.Max(maxSpan * 3.0, 1.0);

            const int tallW = 360, tallH = 1000, topSize = 360;
            var background = (Color)ColorConverter.ConvertFromString("#202020")!;

            // side: look down -Z, up +Y; horizontal axis = X, vertical axis = Y
            var side = RenderView(group, center, new Vector3D(0, 0, -1), new Vector3D(0, 1, 0),
                span[0], span[1], distance, tallW, tallH, background);
            // front: look down -X, up +Y; horizontal axis = Z, vertical axis = Y
            var front = RenderView(group, center, new Vector3D(-1, 0, 0), new Vector3D(0, 1, 0),
                span[2], span[1], distance, tallW, tallH, background);
            // top: look down -Y, up +Z; horizontal axis = X, vertical axis = Z
            var top = RenderView(group, center, new Vector3D(0, -1, 0), new Vector3D(0, 0, 1),
                span[0], span[2], distance, topSize, topSize, background);

            var composite = Composite(new[] { ("side", side), ("front", front), ("top", top) }, background);
            using var stream = File.Create(imagePath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(composite));
            encoder.Save(stream);
        }
        catch (Exception e)
        {
            err = e;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (err != null) throw err;
}

static RenderTargetBitmap RenderView(Model3DGroup group, Point3D center, Vector3D viewDir, Vector3D up,
    double horizontalSpan, double verticalSpan, double distance, int pw, int ph, Color background)
{
    var camera = new OrthographicCamera
    {
        Position = center - viewDir * distance,
        LookDirection = viewDir,
        UpDirection = up,
        Width = Math.Max(Math.Max(horizontalSpan, verticalSpan * (pw / (double)ph)), 1.0) * 1.15,
        NearPlaneDistance = 0.01,
        FarPlaneDistance = distance * 4,
    };

    var visual = new ModelVisual3D { Content = group };
    var lightGroup = new Model3DGroup();
    lightGroup.Children.Add(new DirectionalLight(Colors.White, viewDir));
    lightGroup.Children.Add(new DirectionalLight(Colors.White, -viewDir));
    lightGroup.Children.Add(new AmbientLight((Color)ColorConverter.ConvertFromString("#606060")!));
    var lightVisual = new ModelVisual3D { Content = lightGroup };

    var viewport = new Viewport3D { Camera = camera, Width = pw, Height = ph };
    viewport.Children.Add(visual);
    viewport.Children.Add(lightVisual);
    viewport.Measure(new Size(pw, ph));
    viewport.Arrange(new Rect(0, 0, pw, ph));
    viewport.UpdateLayout();

    var scene = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
    scene.Render(viewport);

    // Composite the 3D over a dark background so faces read against empty space.
    var drawing = new DrawingVisual();
    using (var dc = drawing.RenderOpen())
    {
        dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, pw, ph));
        dc.DrawImage(scene, new Rect(0, 0, pw, ph));
    }
    var result = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
    result.Render(drawing);
    return result;
}

static RenderTargetBitmap Composite((string Label, RenderTargetBitmap Bitmap)[] panels, Color background)
{
    var totalWidth = panels.Sum(panel => panel.Bitmap.PixelWidth);
    var height = panels.Max(panel => panel.Bitmap.PixelHeight);
    var drawing = new DrawingVisual();
    using (var dc = drawing.RenderOpen())
    {
        dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, totalWidth, height));
        double x = 0;
        foreach (var panel in panels)
        {
            dc.DrawImage(panel.Bitmap, new Rect(x, 0, panel.Bitmap.PixelWidth, panel.Bitmap.PixelHeight));
            var label = new FormattedText(panel.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 20, Brushes.White, 1.0);
            dc.DrawText(label, new Point(x + 8, 8));
            x += panel.Bitmap.PixelWidth;
        }
    }
    var result = new RenderTargetBitmap(totalWidth, height, 96, 96, PixelFormats.Pbgra32);
    result.Render(drawing);
    return result;
}

static Color PieceColor(string type) => type.ToLowerInvariant() switch
{
    "blade" => (Color)ColorConverter.ConvertFromString("#4FA3E0")!,
    "guard" => (Color)ColorConverter.ConvertFromString("#E0A64F")!,
    "handle" => (Color)ColorConverter.ConvertFromString("#5FBF6A")!,
    "pommel" => (Color)ColorConverter.ConvertFromString("#C05FBF")!,
    _ => (Color)ColorConverter.ConvertFromString("#B0B0B0")!,
};

static VisualAuditRecord CreateVisualAuditRecord(
    TemplateData template,
    BuildOrder gripOrder,
    BuildOrder tipOrder,
    CraftingPiece grip,
    CraftingPiece tip,
    string candidateSide,
    IReadOnlyDictionary<string, List<CraftingPiece>> usableByType,
    IReadOnlyDictionary<string, CraftingPiece> piecesById,
    FbxBoundsIndex meshBounds,
    float tolerance)
{
    var selected = template.BuildOrder.Select(order =>
    {
        var piece = order.Type.Equals(gripOrder.Type, StringComparison.OrdinalIgnoreCase) ? grip
            : order.Type.Equals(tipOrder.Type, StringComparison.OrdinalIgnoreCase) ? tip
            : usableByType[order.Type][0];
        return new PieceRequest(order.Type, piece.Id, 100);
    }).ToList();
    var layout = CalculateLayout(new WeaponRequest("audit", template.Id, selected), piecesById,
        new Dictionary<string, BuildOrder[]>(StringComparer.OrdinalIgnoreCase) { [template.Id] = template.BuildOrder });
    var gripReport = layout.Pieces.Single(piece => piece.Type.Equals(gripOrder.Type, StringComparison.OrdinalIgnoreCase));
    var tipReport = layout.Pieces.Single(piece => piece.Type.Equals(tipOrder.Type, StringComparison.OrdinalIgnoreCase));
    var missing = new List<string>();
    var gripBounds = meshBounds.Get(grip.Mesh);
    if (gripBounds is null) missing.Add(grip.Mesh);
    var tipBounds = meshBounds.Get(tip.Mesh);
    if (tipBounds is null && !missing.Contains(tip.Mesh, StringComparer.OrdinalIgnoreCase)) missing.Add(tip.Mesh);
    float? visualGap = gripBounds is null || tipBounds is null
        ? null
        : (tipBounds.MinY * tipReport.ScaleFactor * 0.01f + tipReport.Pivot) -
          (gripBounds.MaxY * gripReport.ScaleFactor * 0.01f + gripReport.Pivot);
    var candidate = candidateSide.Equals("grip", StringComparison.OrdinalIgnoreCase) ? gripReport : tipReport;
    return new VisualAuditRecord(template.Id, grip.Id, grip.PieceType, tip.Id, tip.PieceType, candidateSide,
        visualGap, visualGap is float gap && MathF.Abs(gap) <= tolerance, missing,
        new MeshExtent(grip.Mesh, gripBounds?.MinY, gripBounds?.MaxY, gripReport.Pivot),
        new MeshExtent(tip.Mesh, tipBounds?.MinY, tipBounds?.MaxY, tipReport.Pivot),
        new AuditCandidate(candidate.Id, candidate.Type, candidate.Mesh, candidate.ScaleFactor,
            new BuildDataValues(candidate.PieceOffset, candidate.PreviousPieceOffset, candidate.NextPieceOffset)));
}

static VisualAuditRecord WithCorrectionPlan(VisualAuditRecord record, VisualAuditRecord baseline)
{
    var isBaseline = record.GripId.Equals(baseline.GripId, StringComparison.OrdinalIgnoreCase) &&
                     record.TipId.Equals(baseline.TipId, StringComparison.OrdinalIgnoreCase);
    var baselineInfo = new AuditBaseline(baseline.GripId, baseline.TipId, baseline.VisualGap, baseline.Aligned,
        baseline.VisualGap is not null && baseline.MissingMeshes.Count == 0);
    var targetAttribute = record.CandidateSide.Equals("grip", StringComparison.OrdinalIgnoreCase)
        ? "next_piece_offset"
        : "previous_piece_offset";
    var currentValue = record.CandidateSide.Equals("grip", StringComparison.OrdinalIgnoreCase)
        ? record.Candidate.BuildData.NextPieceOffset
        : record.Candidate.BuildData.PreviousPieceOffset;
    float? delta = record.VisualGap is float candidateGap && baseline.VisualGap is float baselineGap
        ? (candidateGap - baselineGap) / (record.Candidate.ScalePercent * 0.01f)
        : null;
    var reason = isBaseline ? "reference_baseline" :
        !baselineInfo.Complete ? "reference_baseline_incomplete" :
        !baselineInfo.Aligned ? "reference_baseline_is_misaligned" :
        record.VisualGap is null ? "missing_mesh" :
        null;
    return record with
    {
        IsBaseline = isBaseline,
        Baseline = baselineInfo,
        CorrectionPlan = new CorrectionPlan(targetAttribute, currentValue, delta,
            delta is float value ? currentValue + value : null, reason is null, reason),
    };
}

static WeaponLayoutReport CalculateLayout(
    WeaponRequest request,
    IReadOnlyDictionary<string, CraftingPiece> piecesById,
    IReadOnlyDictionary<string, BuildOrder[]> loadedBuildOrders)
{
    if (string.IsNullOrWhiteSpace(request.Id)) throw new ArgumentException("Every weapon needs an id.");
    if (string.IsNullOrWhiteSpace(request.Template)) throw new ArgumentException($"Weapon '{request.Id}' needs a template.");

    var selected = new Dictionary<string, SelectedPiece>(StringComparer.OrdinalIgnoreCase);
    foreach (var input in request.Pieces)
    {
        if (string.IsNullOrWhiteSpace(input.Type) || string.IsNullOrWhiteSpace(input.Id))
            throw new ArgumentException($"Weapon '{request.Id}' contains a piece without type or id.");
        if (!piecesById.TryGetValue(input.Id, out var piece))
            throw new ArgumentException($"Weapon '{request.Id}' references unknown piece '{input.Id}'.");
        if (!string.Equals(piece.PieceType, input.Type, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Weapon '{request.Id}' declares '{input.Id}' as {input.Type}, but XML defines it as {piece.PieceType}.");
        if (!selected.TryAdd(input.Type, new SelectedPiece(piece, input.ScaleFactor ?? 100)))
            throw new ArgumentException($"Weapon '{request.Id}' contains more than one {input.Type} piece.");
    }

    var buildOrder = BuildOrderFor(request.Template, loadedBuildOrders);
    if (buildOrder.GroupBy(item => item.Type, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        throw new ArgumentException($"Template '{request.Template}' defines a piece type more than once.");
    foreach (var type in selected.Keys)
    {
        if (!buildOrder.Any(item => item.Type.Equals(type, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Template '{request.Template}' does not use piece type '{type}'.");
    }
    var pivots = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    var orderByType = buildOrder.ToDictionary(item => item.Type, item => item.Order, StringComparer.OrdinalIgnoreCase);
    float bottom = 0;
    float top = 0;

    foreach (var item in buildOrder)
    {
        if (!selected.TryGetValue(item.Type, out var selectedPiece)) continue;
        var scale = selectedPiece.ScaleFactor * 0.01f;
        var sign = Math.Sign(item.Order);
        if (sign == 0)
        {
            top += selectedPiece.Piece.PieceOffset * scale;
            bottom -= selectedPiece.Piece.PieceOffset * scale;
        }
        else if (sign < 0)
        {
            bottom += selectedPiece.Piece.DistanceToNextPiece * scale;
            bottom += selectedPiece.Piece.PieceOffset * scale;
            bottom -= selectedPiece.Piece.NextPieceOffset * scale;
        }
        else
        {
            top += selectedPiece.Piece.DistanceToPreviousPiece * scale;
            top += selectedPiece.Piece.PieceOffset * scale;
            top -= selectedPiece.Piece.PreviousPieceOffset * scale;
        }

        pivots[item.Type] = sign * (sign < 0 ? bottom : top) + (sign == 0 ? selectedPiece.Piece.PieceOffset * scale : 0);

        if (sign == 0)
        {
            bottom += selectedPiece.Piece.DistanceToPreviousPiece * scale - selectedPiece.Piece.PreviousPieceOffset * scale;
            top += selectedPiece.Piece.DistanceToNextPiece * scale - selectedPiece.Piece.NextPieceOffset * scale;
        }
        else if (sign < 0)
        {
            bottom += selectedPiece.Piece.DistanceToPreviousPiece * scale - selectedPiece.Piece.PreviousPieceOffset * scale;
        }
        else
        {
            top += selectedPiece.Piece.DistanceToNextPiece * scale - selectedPiece.Piece.NextPieceOffset * scale;
        }
    }

    var reportPieces = selected
        .Where(item => pivots.ContainsKey(item.Key))
        .Select(item => PieceReport.Create(item.Key, item.Value, pivots[item.Key], orderByType[item.Key]))
        .OrderBy(item => item.BuildOrder)
        .ToList();
    var joints = reportPieces.Zip(reportPieces.Skip(1), (gripSide, tipSide) => new JointReport(
        gripSide.Type,
        tipSide.Type,
        tipSide.Start - gripSide.End))
        .ToList();

    float? weaponLength = null;
    var blade = reportPieces.SingleOrDefault(piece => piece.Type.Equals("Blade", StringComparison.OrdinalIgnoreCase));
    if (blade is not null)
    {
        PieceReport? longestPiece = null;
        foreach (var type in new[] { "Blade", "Guard", "Handle", "Pommel" })
        {
            var piece = reportPieces.SingleOrDefault(piece => piece.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
            if (piece is not null && (longestPiece is null || piece.ScaledDistanceToNextPiece > longestPiece.ScaledDistanceToNextPiece))
                longestPiece = piece;
        }
        var longestExtent = longestPiece is null ? 0 : longestPiece.ScaledDistanceToNextPiece + longestPiece.ScaledPieceOffset;
        weaponLength = MathF.Max(blade.Pivot + blade.ScaledDistanceToNextPiece, longestExtent);
    }

    return new WeaponLayoutReport(request.Id, request.Template, buildOrder, reportPieces, joints, weaponLength, bottom);
}

static List<CraftingPiece> LoadPieces(string path) => ParsePieces(XDocument.Load(path));

static List<CraftingPiece> ParsePieces(XDocument document) =>
    document
        .Descendants("CraftingPiece")
        .Select(ParsePiece)
        .Where(piece => piece is not null)
        .Cast<CraftingPiece>()
        .OrderBy(piece => piece.Id, StringComparer.Ordinal)
        .ToList();

static Dictionary<string, BuildOrder[]> ParseBuildOrders(XDocument document)
{
    var buildOrders = new Dictionary<string, BuildOrder[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var template in document.Descendants("CraftingTemplate"))
    {
        var id = template.Attribute("id")?.Value;
        var pieces = template.Element("PieceDatas")?.Elements("PieceData")
            .Select(piece => int.TryParse(piece.Attribute("build_order")?.Value, out var order)
                ? new BuildOrder(piece.Attribute("piece_type")?.Value ?? "", order)
                : null)
            .Where(piece => piece is not null && piece.Type.Length > 0)
            .Cast<BuildOrder>()
            .ToArray();
        if (!string.IsNullOrWhiteSpace(id) && pieces is { Length: > 0 }) buildOrders[id] = pieces;
    }
    return buildOrders;
}

static Dictionary<string, TemplateData> ParseTemplates(XDocument document)
{
    var templates = new Dictionary<string, TemplateData>(StringComparer.OrdinalIgnoreCase);
    foreach (var template in document.Descendants("CraftingTemplate"))
    {
        var id = template.Attribute("id")?.Value;
        var buildOrder = template.Element("PieceDatas")?.Elements("PieceData")
            .Select(piece => int.TryParse(piece.Attribute("build_order")?.Value, out var order)
                ? new BuildOrder(piece.Attribute("piece_type")?.Value ?? "", order)
                : null)
            .Where(piece => piece is not null && piece.Type.Length > 0)
            .Cast<BuildOrder>()
            .ToArray();
        var usable = template.Element("UsablePieces")?.Elements("UsablePiece")
            .Select(piece => piece.Attribute("piece_id")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList() ?? [];
        if (!string.IsNullOrWhiteSpace(id) && buildOrder is { Length: > 0 })
            templates[id] = new TemplateData(id, buildOrder, usable);
    }
    return templates;
}

static BuildOrder[] BuildOrderFor(string template, IReadOnlyDictionary<string, BuildOrder[]> loadedBuildOrders)
{
    if (loadedBuildOrders.TryGetValue(template, out var loaded)) return loaded;
    throw new ArgumentException($"No build order is known for template '{template}' in resolved CraftingTemplates XML.");
}

static ResolvedCraftingData LoadSubModuleCraftingData(string subModulePath, IReadOnlyDictionary<string, List<string>> options, bool loadItems = false)
{
    if (options.ContainsKey("pieces") || options.ContainsKey("templates"))
        throw new ArgumentException("--submodule cannot be combined with --pieces or --templates.");

    var moduleRoot = Path.GetDirectoryName(Path.GetFullPath(subModulePath))
        ?? throw new ArgumentException("The SubModule.xml path has no parent directory.");
    var modulesRoot = options.TryGetValue("modules-root", out var configuredModulesRoot)
        ? Path.GetFullPath(SingleOption(configuredModulesRoot, "modules-root"))
        : Path.GetDirectoryName(moduleRoot) ?? throw new ArgumentException("The module root has no parent Modules directory.");
    var modules = ResolveModuleStack(moduleRoot, modulesRoot);
    var pieces = ResolveDeclaredXml(modules, "CraftingPieces");
    var templates = ResolveDeclaredXml(modules, "CraftingTemplates");
    var items = loadItems ? ResolveDeclaredXml(modules, "Items") : null;
    return new ResolvedCraftingData(
        ParsePieces(pieces.Document),
        ParseBuildOrders(templates.Document),
        ParseTemplates(templates.Document),
        new SourceFiles(pieces.Paths, templates.Paths),
        items?.Document,
        items?.Paths ?? []);
}

static WeaponRequest LoadCraftedItem(XDocument items, string craftedItemId)
{
    var craftedItem = items.Descendants("CraftedItem")
        .FirstOrDefault(item => string.Equals(item.Attribute("id")?.Value, craftedItemId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No CraftedItem with id '{craftedItemId}' was found in resolved Items XML.");
    var template = craftedItem.Attribute("crafting_template")?.Value;
    if (string.IsNullOrWhiteSpace(template))
        throw new ArgumentException($"CraftedItem '{craftedItemId}' has no crafting_template.");

    var pieces = craftedItem.Element("Pieces")?.Elements("Piece")
        .Select(piece =>
        {
            var scaleFactor = piece.Attribute("scale_factor")?.Value;
            if (scaleFactor is not null && !int.TryParse(scaleFactor, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                throw new ArgumentException($"CraftedItem '{craftedItemId}' has an invalid scale_factor '{scaleFactor}'.");
            return new PieceRequest(
                piece.Attribute("Type")?.Value ?? "",
                piece.Attribute("id")?.Value ?? "",
                scaleFactor is null ? null : int.Parse(scaleFactor, CultureInfo.InvariantCulture));
        })
        .ToList()
        ?? throw new ArgumentException($"CraftedItem '{craftedItemId}' has no Pieces.");
    if (pieces.Count == 0) throw new ArgumentException($"CraftedItem '{craftedItemId}' has no Pieces.");

    return new WeaponRequest(craftedItemId, template, pieces);
}

static List<ModuleSource> ResolveModuleStack(string targetModuleRoot, string modulesRoot)
{
    var resolved = new List<ModuleSource>();
    var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void Visit(string moduleRoot)
    {
        var subModulePath = Path.Combine(moduleRoot, "SubModule.xml");
        if (!File.Exists(subModulePath)) throw new FileNotFoundException("Could not find SubModule.xml for a dependent module.", subModulePath);
        var subModule = XDocument.Load(subModulePath);
        var moduleId = subModule.Root?.Element("Id")?.Attribute("value")?.Value
            ?? Path.GetFileName(moduleRoot);
        if (visited.Contains(moduleId)) return;
        if (!visiting.Add(moduleId)) throw new ArgumentException($"Module dependency cycle detected at '{moduleId}'.");

        foreach (var dependencyId in subModule.Root?.Element("DependedModules")?.Elements("DependedModule")
                     .Select(dependency => dependency.Attribute("Id")?.Value)
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Cast<string>()
                 ?? Enumerable.Empty<string>())
        {
            var dependencyRoot = FindModuleRoot(modulesRoot, dependencyId);
            Visit(dependencyRoot);
        }

        visiting.Remove(moduleId);
        visited.Add(moduleId);
        resolved.Add(new ModuleSource(Path.GetFullPath(moduleRoot), subModule));
    }

    Visit(targetModuleRoot);
    return resolved;
}

static string FindModuleRoot(string modulesRoot, string moduleId)
{
    var direct = Path.Combine(modulesRoot, moduleId);
    if (File.Exists(Path.Combine(direct, "SubModule.xml"))) return direct;

    var matchingModule = Directory.GetDirectories(modulesRoot)
        .FirstOrDefault(directory =>
        {
            var subModulePath = Path.Combine(directory, "SubModule.xml");
            if (!File.Exists(subModulePath)) return false;
            var subModule = XDocument.Load(subModulePath);
            return string.Equals(subModule.Root?.Element("Id")?.Attribute("value")?.Value, moduleId, StringComparison.OrdinalIgnoreCase);
        });
    return matchingModule ?? throw new FileNotFoundException($"Could not find dependent module '{moduleId}' under '{modulesRoot}'.", direct);
}

static ResolvedXml ResolveDeclaredXml(IReadOnlyList<ModuleSource> modules, string id)
{
    var declarations = modules
        .SelectMany(module => (module.SubModule.Root?.Element("Xmls")?.Elements("XmlNode").SelectMany(xmlNode => xmlNode.Elements("XmlName"))
            ?? Enumerable.Empty<XElement>())
            .Where(xmlName => string.Equals(xmlName.Attribute("id")?.Value, id, StringComparison.OrdinalIgnoreCase))
            .Select(xmlName => new XmlDeclaration(module.Root, xmlName.Attribute("path")?.Value)))
        .Where(declaration => !string.IsNullOrWhiteSpace(declaration.Path))
        .ToList();
    if (declarations.Count == 0)
        throw new ArgumentException($"The resolved module stack declares no {id} XML paths.");

    XDocument? accumulated = null;
    var paths = new List<string>();
    foreach (var declaration in declarations)
    {
        foreach (var source in ResolveDeclarationPaths(declaration.ModuleRoot, declaration.Path!))
        {
            if (accumulated is null)
            {
                if (source.XmlPath is null) continue;
                var first = XDocument.Load(source.XmlPath);
                accumulated = new XDocument(new XElement(first.Root!));
            }
            else
            {
                accumulated = ApplySiblingTransform(accumulated, source.TransformBasePath);
                if (source.XmlPath is not null)
                {
                    var later = XDocument.Load(source.XmlPath);
                    MergeElements(accumulated.Root!, later.Root!);
                }
            }
            if (source.XmlPath is not null) paths.Add(source.XmlPath);
        }
    }
    if (accumulated is null) throw new ArgumentException($"The resolved module stack resolved no {id} XML files.");
    return new ResolvedXml(accumulated, paths);
}

static IEnumerable<XmlSource> ResolveDeclarationPaths(string moduleRoot, string declaration)
{
    var moduleDataPath = Path.Combine(moduleRoot, "ModuleData", declaration);
    var xmlPath = Path.ChangeExtension(moduleDataPath, ".xml");
    if (File.Exists(xmlPath)) return [new XmlSource(Path.GetFullPath(xmlPath), Path.GetFullPath(xmlPath))];
    if (Directory.Exists(moduleDataPath))
        return Directory.GetFiles(moduleDataPath, "*.xml", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new XmlSource(Path.GetFullPath(path), Path.GetFullPath(path)))
            .ToArray();
    if (File.Exists(Path.ChangeExtension(moduleDataPath, ".xsl")) || File.Exists(Path.ChangeExtension(moduleDataPath, ".xslt")))
        return [new XmlSource(null, Path.GetFullPath(moduleDataPath))];
    throw new FileNotFoundException($"Could not resolve XML declaration path '{declaration}'.", xmlPath);
}

static XDocument ApplySiblingTransform(XDocument document, string laterPath)
{
    var xslPath = Path.ChangeExtension(laterPath, ".xsl");
    if (!File.Exists(xslPath))
    {
        xslPath = Path.ChangeExtension(laterPath, ".xslt");
        if (!File.Exists(xslPath)) return document;
    }

    var transform = new XslCompiledTransform();
    transform.Load(xslPath);
    var transformed = new XDocument();
    using var reader = document.CreateReader();
    using var writer = transformed.CreateWriter();
    transform.Transform(reader, null, writer);
    return transformed;
}

static void MergeElements(XElement target, XElement source)
{
    foreach (var attribute in source.Attributes()) target.SetAttributeValue(attribute.Name, attribute.Value);
    foreach (var sourceChild in source.Elements())
    {
        var targetChild = target.Elements().FirstOrDefault(candidate => SameIdentity(candidate, sourceChild));
        if (targetChild is null) target.Add(new XElement(sourceChild));
        else MergeElements(targetChild, sourceChild);
    }
}

static bool SameIdentity(XElement left, XElement right)
{
    var leftId = left.Attribute("id")?.Value;
    var rightId = right.Attribute("id")?.Value;
    if (leftId is not null || rightId is not null)
        return left.Name == right.Name && string.Equals(leftId, rightId, StringComparison.OrdinalIgnoreCase);
    return left.Name == right.Name && string.Equals(left.Attribute("piece_type")?.Value, right.Attribute("piece_type")?.Value, StringComparison.OrdinalIgnoreCase);
}

static CraftingPiece? ParsePiece(XElement element)
{
    var id = element.Attribute("id")?.Value;
    var pieceType = element.Attribute("piece_type")?.Value;
    if (id is null || pieceType is null) return null;

    float length;
    float distanceToNext;
    float distanceToPrevious;
    if (TryParseFloat(element.Attribute("length")?.Value, out length))
    {
        distanceToNext = length / 2f;
        distanceToPrevious = length / 2f;
    }
    else
    {
        TryParseFloat(element.Attribute("distance_to_next_piece")?.Value, out distanceToNext);
        TryParseFloat(element.Attribute("distance_to_previous_piece")?.Value, out distanceToPrevious);
        length = distanceToNext + distanceToPrevious;
    }

    var buildData = element.Element("BuildData");
    return new CraftingPiece(
        id,
        pieceType,
        element.Attribute("mesh")?.Value ?? id,
        length,
        distanceToNext,
        distanceToPrevious,
        ParseBuildData(buildData, "piece_offset"),
        ParseBuildData(buildData, "previous_piece_offset"),
        ParseBuildData(buildData, "next_piece_offset"));
}

static void WriteJson<T>(T value, IReadOnlyDictionary<string, List<string>> options)
{
    var json = JsonSerializer.Serialize(value, CliJson.Options);
    if (options.TryGetValue("out", out var outputPaths)) File.WriteAllText(SingleOption(outputPaths, "out"), json);
    else Console.WriteLine(json);
}

static string RequiredOption(IReadOnlyDictionary<string, List<string>> options, string name) =>
    options.TryGetValue(name, out var values) ? SingleOption(values, name) : throw new ArgumentException($"--{name} is required.");

static string SingleOption(IReadOnlyList<string> values, string name) =>
    values.Count == 1 ? values[0] : throw new ArgumentException($"--{name} may only be specified once.");

static Dictionary<string, List<string>> ReadOptions(string[] arguments)
{
    var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length;)
    {
        if (!arguments[index].StartsWith("--"))
            throw new ArgumentException($"Expected an option and value, got '{arguments[index]}'.");
        var name = arguments[index][2..];
        if (!options.TryGetValue(name, out var values)) options[name] = values = [];
        if (name.Equals("all-templates", StringComparison.OrdinalIgnoreCase))
        {
            values.Add("true");
            index++;
            continue;
        }
        if (index + 1 >= arguments.Length)
            throw new ArgumentException($"Expected a value for '--{name}'.");
        values.Add(arguments[index + 1]);
        index += 2;
    }
    return options;
}

static float ParseBuildData(XElement? buildData, string name)
{
    TryParseFloat(buildData?.Attribute(name)?.Value, out var value);
    return value;
}

static bool TryParseFloat(string? value, out float result) =>
    float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

static float ParseNonNegativeFloat(string value, string name)
{
    if (!TryParseFloat(value, out var result) || result < 0)
        throw new ArgumentException($"--{name} must be a non-negative number.");
    return result;
}

sealed record CraftingPiece(
    string Id,
    string PieceType,
    string Mesh,
    float Length,
    float DistanceToNextPiece,
    float DistanceToPreviousPiece,
    float PieceOffset,
    float PreviousPieceOffset,
    float NextPieceOffset);

sealed record ResolvedCraftingData(
    List<CraftingPiece> Pieces,
    Dictionary<string, BuildOrder[]> BuildOrders,
    Dictionary<string, TemplateData> Templates,
    SourceFiles SourceFiles,
    XDocument? Items,
    List<string> ItemPaths);

sealed record ResolvedXml(XDocument Document, List<string> Paths);
sealed record ModuleSource(string Root, XDocument SubModule);
sealed record XmlDeclaration(string ModuleRoot, string? Path);
sealed record XmlSource(string? XmlPath, string TransformBasePath);
sealed record SourceFiles(List<string> CraftingPieces, List<string> CraftingTemplates);
sealed record BuildOrder(string Type, int Order);
sealed record TemplateData(string Id, BuildOrder[] BuildOrder, List<string> UsablePieceIds);
sealed record WeaponRequest(string Id, string Template, List<PieceRequest> Pieces);
sealed record PieceRequest(string Type, string Id, int? ScaleFactor);
sealed record SelectedPiece(CraftingPiece Piece, int ScaleFactor);
sealed record PieceReport(
    string Type,
    string Id,
    string Mesh,
    int BuildOrder,
    int ScaleFactor,
    float Pivot,
    float Start,
    float End,
    float Length,
    float DistanceToNextPiece,
    float DistanceToPreviousPiece,
    float PieceOffset,
    float PreviousPieceOffset,
    float NextPieceOffset,
    float ScaledLength,
    float ScaledDistanceToNextPiece,
    float ScaledDistanceToPreviousPiece,
    float ScaledPieceOffset,
    float ScaledPreviousPieceOffset,
    float ScaledNextPieceOffset)
{
    public static PieceReport Create(string type, SelectedPiece selected, float pivot, int buildOrder)
    {
        var scale = selected.ScaleFactor * 0.01f;
        var piece = selected.Piece;
        var next = piece.DistanceToNextPiece * scale;
        var previous = piece.DistanceToPreviousPiece * scale;
        return new PieceReport(type, piece.Id, piece.Mesh, buildOrder, selected.ScaleFactor, pivot, pivot - previous, pivot + next,
            piece.Length, piece.DistanceToNextPiece, piece.DistanceToPreviousPiece, piece.PieceOffset, piece.PreviousPieceOffset, piece.NextPieceOffset,
            piece.Length * scale, next, previous, piece.PieceOffset * scale, piece.PreviousPieceOffset * scale, piece.NextPieceOffset * scale);
    }
}

sealed record JointReport(string GripSideType, string TipSideType, float Gap);
sealed record WeaponLayoutReport(string Id, string Template, BuildOrder[] BuildOrder, List<PieceReport> Pieces, List<JointReport> Joints, float? WeaponLength, float HandToBottom);
sealed record MeshBounds(float MinY, float MaxY);
sealed record MeshExtent(string Mesh, float? LocalMinY, float? LocalMaxY, float Pivot);
sealed record AuditSkip(string Template, string Reason, string? Joint);
sealed record BuildDataValues(float PieceOffset, float PreviousPieceOffset, float NextPieceOffset);
sealed record AuditCandidate(string Id, string Type, string Mesh, int ScalePercent, BuildDataValues BuildData);
sealed record AuditBaseline(string GripId, string TipId, float? VisualGap, bool Aligned, bool Complete);
sealed record CorrectionPlan(
    string TargetAttribute,
    float CurrentValue,
    float? DeltaToMatchBaseline,
    float? SuggestedValueToMatchBaseline,
    bool SafeToApply,
    string? Reason);
sealed record VisualAuditRecord(
    string Template,
    string GripId,
    string GripType,
    string TipId,
    string TipType,
    string CandidateSide,
    float? VisualGap,
    bool Aligned,
    List<string> MissingMeshes,
    MeshExtent Grip,
    MeshExtent Tip,
    AuditCandidate Candidate)
{
    public bool IsBaseline { get; init; }
    public AuditBaseline? Baseline { get; init; }
    public CorrectionPlan? CorrectionPlan { get; init; }
}

sealed record Bounds(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ);
sealed record RenderPiece(string Type, string Id, string Mesh, int ScaleFactor, float PivotXml, MeshGeometry3D? Geometry, float Scale, float Pivot, Bounds? Bounds);
sealed record BoundsReport(float[] Min, float[] Max);
sealed record LateralOffset(float X, float Z);
sealed record AxisExtent(float MinY, float MaxY);
sealed record RenderPieceReport(
    string Type,
    string Id,
    string Mesh,
    int ScaleFactor,
    float Pivot,
    bool MeshFound,
    BoundsReport? WorldBounds,
    float[]? Center,
    LateralOffset? LateralOffset,
    AxisExtent? AxisExtent);
sealed record RenderSeam(string GripType, string TipType, float AxisGap);

sealed class FbxMeshIndex
{
    private readonly Dictionary<string, string> _meshToFile = new(StringComparer.OrdinalIgnoreCase);

    public FbxMeshIndex(string folder)
    {
        using var context = new AssimpContext();
        foreach (var path in Directory.EnumerateFiles(folder, "*.fbx", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                var scene = context.ImportFile(path, PostProcessSteps.None);
                if (scene?.RootNode is not null) IndexNode(scene.RootNode, scene, path);
            }
            catch
            {
                // A malformed source FBX is reported as a missing mesh if a piece needs it.
            }
        }
    }

    private void IndexNode(Node node, Scene scene, string path)
    {
        AddName(node.Name, path);
        foreach (var meshIndex in node.MeshIndices)
            if (meshIndex >= 0 && meshIndex < scene.MeshCount)
                AddName(scene.Meshes[meshIndex].Name, path);
        foreach (var child in node.Children) IndexNode(child, scene, path);
    }

    private void AddName(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(name) || IsFilteredMeshName(name)) return;
        _meshToFile.TryAdd(name, path);
        if (name.EndsWith(".lod0", StringComparison.OrdinalIgnoreCase)) _meshToFile.TryAdd(name[..^5], path);
    }

    public MeshGeometry3D? LoadMesh(string meshName)
    {
        if (!_meshToFile.TryGetValue(meshName, out var path)) return null;
        try
        {
            using var context = new AssimpContext();
            var scene = context.ImportFile(path,
                PostProcessSteps.Triangulate | PostProcessSteps.GenerateNormals | PostProcessSteps.JoinIdenticalVertices);
            if (scene?.RootNode is null || !scene.HasMeshes) return null;
            var positions = new Point3DCollection();
            var normals = new Vector3DCollection();
            var indices = new Int32Collection();
            FindAndCollect(scene.RootNode, scene, meshName, positions, normals, indices, IdentityMatrix());
            if (positions.Count == 0) return null;
            var geometry = new MeshGeometry3D { Positions = positions, Normals = normals, TriangleIndices = indices };
            geometry.Freeze();
            return geometry;
        }
        catch
        {
            return null;
        }
    }

    private static bool FindAndCollect(Node node, Scene scene, string target,
        Point3DCollection pos, Vector3DCollection nrm, Int32Collection idx, Assimp.Matrix4x4 parent)
    {
        var world = parent * node.Transform;

        static bool NameMatches(string name, string target) =>
            string.Equals(name, target, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, target + ".lod0", StringComparison.OrdinalIgnoreCase);

        var byNodeName = NameMatches(node.Name, target);
        var byMeshName = !byNodeName && node.MeshIndices.Any(mi =>
            mi >= 0 && mi < scene.MeshCount && NameMatches(scene.Meshes[mi].Name, target));

        if (byNodeName || byMeshName)
        {
            foreach (var mi in node.MeshIndices)
            {
                if (mi < 0 || mi >= scene.MeshCount) continue;
                if (byMeshName && !NameMatches(scene.Meshes[mi].Name, target)) continue;
                var mesh = scene.Meshes[mi];
                var baseIdx = pos.Count;
                foreach (var v in mesh.Vertices)
                    pos.Add(new Point3D(
                        world.A1 * v.X + world.A2 * v.Y + world.A3 * v.Z + world.A4,
                        world.B1 * v.X + world.B2 * v.Y + world.B3 * v.Z + world.B4,
                        world.C1 * v.X + world.C2 * v.Y + world.C3 * v.Z + world.C4));
                if (mesh.HasNormals)
                    foreach (var n in mesh.Normals)
                        nrm.Add(new Vector3D(
                            world.A1 * n.X + world.A2 * n.Y + world.A3 * n.Z,
                            world.B1 * n.X + world.B2 * n.Y + world.B3 * n.Z,
                            world.C1 * n.X + world.C2 * n.Y + world.C3 * n.Z));
                else
                    for (var i = 0; i < mesh.VertexCount; i++) nrm.Add(new Vector3D(0, 1, 0));
                foreach (var face in mesh.Faces)
                    if (face.IndexCount == 3)
                    {
                        idx.Add(baseIdx + face.Indices[0]);
                        idx.Add(baseIdx + face.Indices[1]);
                        idx.Add(baseIdx + face.Indices[2]);
                    }
            }
            return true;
        }

        foreach (var child in node.Children)
            if (FindAndCollect(child, scene, target, pos, nrm, idx, world))
                return true;
        return false;
    }

    private static bool IsFilteredMeshName(string name)
    {
        var lower = name.ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(lower, @"_lod[1-9]") ||
               System.Text.RegularExpressions.Regex.IsMatch(lower, @"\blod[1-9]\b") ||
               lower.StartsWith("bo_") || lower.StartsWith("col_") || lower.StartsWith("ub_") ||
               lower == "armature" || lower == "rootnode";
    }

    private static Assimp.Matrix4x4 IdentityMatrix()
    {
        var matrix = new Assimp.Matrix4x4();
        matrix.A1 = matrix.B2 = matrix.C3 = matrix.D4 = 1f;
        return matrix;
    }
}

sealed class FbxBoundsIndex
{
    private readonly Dictionary<string, MeshBounds?> _bounds = new(StringComparer.OrdinalIgnoreCase);

    public FbxBoundsIndex(string folder)
    {
        using var context = new AssimpContext();
        foreach (var path in Directory.EnumerateFiles(folder, "*.fbx", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                var scene = context.ImportFile(path, PostProcessSteps.None);
                if (scene?.RootNode is not null) IndexNode(scene.RootNode, scene, IdentityMatrix());
            }
            catch
            {
                // A malformed source FBX is reported as a missing mesh if a piece needs it.
            }
        }
    }

    public MeshBounds? Get(string meshName)
    {
        return _bounds.TryGetValue(meshName, out var cached) ? cached : null;
    }

    private void IndexNode(Node node, Scene scene, Assimp.Matrix4x4 parent)
    {
        var world = parent * node.Transform;
        AddName(node.Name, BoundsForMeshes(node.MeshIndices, scene, world));
        foreach (var meshIndex in node.MeshIndices)
            if (meshIndex >= 0 && meshIndex < scene.MeshCount)
                AddName(scene.Meshes[meshIndex].Name, BoundsForMeshes([meshIndex], scene, world));
        foreach (var child in node.Children) IndexNode(child, scene, world);
    }

    private void AddName(string name, MeshBounds? bounds)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Equals("RootNode", StringComparison.OrdinalIgnoreCase) || IsFilteredMeshName(name)) return;
        _bounds.TryAdd(name, bounds);
        if (name.EndsWith(".lod0", StringComparison.OrdinalIgnoreCase)) _bounds.TryAdd(name[..^5], bounds);
    }

    private static MeshBounds? BoundsForMeshes(IEnumerable<int> meshIndices, Scene scene, Assimp.Matrix4x4 world)
    {
        var found = false;
        var minY = float.PositiveInfinity;
        var maxY = float.NegativeInfinity;
        foreach (var meshIndex in meshIndices)
        {
            if (meshIndex < 0 || meshIndex >= scene.MeshCount) continue;
            foreach (var vertex in scene.Meshes[meshIndex].Vertices)
            {
                var y = world.B1 * vertex.X + world.B2 * vertex.Y + world.B3 * vertex.Z + world.B4;
                minY = MathF.Min(minY, y);
                maxY = MathF.Max(maxY, y);
                found = true;
            }
        }
        return found ? new MeshBounds(minY, maxY) : null;
    }

    private static bool IsFilteredMeshName(string name)
    {
        var lower = name.ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(lower, @"_lod[1-9]") ||
               System.Text.RegularExpressions.Regex.IsMatch(lower, @"\blod[1-9]\b") ||
               lower.StartsWith("bo_") || lower.StartsWith("col_") || lower.StartsWith("ub_") || lower == "armature";
    }

    private static Assimp.Matrix4x4 IdentityMatrix()
    {
        var matrix = new Assimp.Matrix4x4();
        matrix.A1 = matrix.B2 = matrix.C3 = matrix.D4 = 1f;
        return matrix;
    }
}

static class CliJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}
