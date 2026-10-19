using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

var tests = new CliTests();
tests.ExtractWritesNormalizedPieceData();
tests.InspectReturnsFlatReportForOneWeapon();
tests.InspectReturnsBatchForRepeatedWeaponOptions();
tests.InspectRejectsWeaponsManifestOption();
tests.InspectRejectsMissingWeaponOption();
tests.InspectRejectsMissingCraftedItemIds();
tests.InspectRejectsNonCraftedItemIds();
tests.InspectResolvesCraftingDataAcrossDependentModules();
tests.InspectIsPrimaryAndLayoutRemainsCompatibilityAlias();
tests.HelpDocumentsXmlDrivenInspectUsage();
tests.AuditRequiresFbxFolder();
tests.AuditMeasuresFbxBoundsAndReportsMissingMeshes();
tests.AuditExpandsAssetSourcesChildToSiblingFbxFolders();
tests.AuditEmitsBaselineAwareCorrectionPlan();
tests.AuditAllPairsRendersEveryCombinationAndPieceGallery();
tests.AuditAllPairsWithoutOutStillReportsMeasurementsOnStdout();
tests.AuditRejectsTheRemovedRenderDirOption();
tests.OutRejectsAFileNameButAcceptsAnExistingDottedDirectory();
tests.AuditCollapsesDefaultPolearmSlotsIntoHandleBladePairs();
tests.AuditDefaultsScalePercentTo100();
tests.AuditScalePercentPropagatesToLayoutAndPieceGallery();
tests.AuditRejectsInvalidScalePercentValues();
tests.RenderRequiresFbxFolder();
tests.RenderProducesImageAndMeasurements();
tests.RenderHonorsFbxUnitScaleMetadata();
tests.RenderPlacesTheHolsterMeshAtTheBladePivot();
tests.RenderSkipsTheHolsterForTemplatesThatSuppressIt();
tests.RenderReportsBladesWithoutAHolsterMeshAndKeepsThemOutOfThePieces();
tests.RenderHolsteredHidesTheTemplatesHiddenPieceTypes();
tests.RenderScalesNonFullScalePiecesAlongTheAxisOnly();
tests.ScabbardEligibilityRefusesSkeletonHolsters();
tests.ScabbardEligibilityAcceptsAPlainHolsterAndFlagsTheProbeAsAHeuristic();
tests.HolsteredPlacementFallsBackToTheOriginWhenTheTemplateNamesAMissingPieceType();
tests.ScabbardMeasurerReadsTheHolsterMouthAtItsLowEnd();
Console.WriteLine("34 tests passed.");

sealed class CliTests
{
    public void ExtractWritesNormalizedPieceData()
    {
        using var temp = new TempDirectory();
        var piecesPath = Path.Combine(temp.Path, "crafting_pieces.xml");
        File.WriteAllText(piecesPath, """
            <CraftingPieces><CraftingPiece id="blade_test" piece_type="Blade" length="120"><BuildData previous_piece_offset="3" /></CraftingPiece></CraftingPieces>
            """);

        var outDir = Path.Combine(temp.Path, "reports");

        var written = RunCli("extract", "--pieces", piecesPath, "--out", outDir);

        Assert.True(written.ExitCode == 0, $"extract failed: {written.Output}");
        var reportPath = Path.Combine(outDir, "pieces.json");
        Assert.True(File.Exists(reportPath), "extract did not write pieces.json into the --out directory");
        using var json = JsonDocument.Parse(File.ReadAllText(reportPath));
        var piece = json.RootElement.GetProperty("pieces")[0];
        Assert.True(piece.GetProperty("distance_to_next_piece").GetSingle() == 60, "length was not normalized to half-distance");
        Assert.True(piece.GetProperty("previous_piece_offset").GetSingle() == 3, "previous offset was not extracted");

        var piped = RunCli("extract", "--pieces", piecesPath);

        Assert.True(piped.ExitCode == 0, $"extract without --out failed: {piped.Output}");
        using var pipedJson = JsonDocument.Parse(piped.Output);
        Assert.True(pipedJson.RootElement.GetProperty("pieces").GetArrayLength() == 1, "extract without --out did not print the report to stdout");
    }

    public void InspectReturnsFlatReportForOneWeapon()
    {
        using var temp = new TempDirectory();
        var fixture = CreateCraftedItemsModule(temp.Path);

        var result = RunCli("inspect", "--submodule", fixture.SubModulePath, "--weapon", "first");

        Assert.True(result.ExitCode == 0, $"single weapon inspection failed: {result.Output}");
        using var json = JsonDocument.Parse(result.Output);
        Assert.True(json.RootElement.GetProperty("id").GetString() == "first", "single weapon report did not retain the flat id field");
        Assert.True(!json.RootElement.TryGetProperty("weapons", out _), "single weapon report unexpectedly became a batch");
        Assert.True(json.RootElement.GetProperty("source_files").GetProperty("items").EnumerateArray().Single().GetString() == fixture.ItemsPath, "single weapon report omitted Items source files");
    }

    public void InspectReturnsBatchForRepeatedWeaponOptions()
    {
        using var temp = new TempDirectory();
        var fixture = CreateCraftedItemsModule(temp.Path);

        var result = RunCli("inspect", "--submodule", fixture.SubModulePath, "--weapon", "first", "--weapon", "second", "--weapon", "first");

        Assert.True(result.ExitCode == 0, $"repeated --weapon inspection failed: {result.Output}");
        using var json = JsonDocument.Parse(result.Output);
        var weapons = json.RootElement.GetProperty("weapons").EnumerateArray().ToArray();
        Assert.True(weapons.Select(weapon => weapon.GetProperty("id").GetString()).SequenceEqual(["first", "second", "first"]), "repeated --weapon values did not produce an ordered batch report");
        Assert.True(json.RootElement.GetProperty("source_files").GetProperty("items").GetArrayLength() == 1, "batch report did not group shared source files");
    }

    public void InspectRejectsWeaponsManifestOption()
    {
        using var temp = new TempDirectory();
        var fixture = CreateCraftedItemsModule(temp.Path);
        var manifestPath = Path.Combine(temp.Path, "weapons.json");
        File.WriteAllText(manifestPath, "{ \"weapons\": [] }");

        var result = RunCli("inspect", "--submodule", fixture.SubModulePath, "--weapons", manifestPath);

        Assert.True(result.ExitCode == 2, "inspect accepted the removed --weapons manifest option");
        Assert.True(result.Output.Contains("--weapons is no longer supported", StringComparison.Ordinal), "inspect did not explain that --weapons was removed");
    }

    public void InspectRejectsMissingWeaponOption()
    {
        using var temp = new TempDirectory();
        var fixture = CreateCraftedItemsModule(temp.Path);

        var result = RunCli("inspect", "--submodule", fixture.SubModulePath);

        Assert.True(result.ExitCode == 2, "inspect accepted a request without --weapon");
        Assert.True(result.Output.Contains("requires at least one --weapon", StringComparison.Ordinal), "inspect did not explain that --weapon is required");
    }

    public void InspectRejectsMissingCraftedItemIds()
    {
        using var temp = new TempDirectory();
        var fixture = CreateCraftedItemsModule(temp.Path);

        var result = RunCli("inspect", "--submodule", fixture.SubModulePath, "--weapon", "missing");

        Assert.True(result.ExitCode == 2, "inspect accepted an unknown CraftedItem id");
        Assert.True(result.Output.Contains("No CraftedItem with id 'missing' was found", StringComparison.Ordinal), "inspect did not explain the missing CraftedItem id");
    }

    public void InspectRejectsNonCraftedItemIds()
    {
        using var temp = new TempDirectory();
        var fixture = CreateCraftedItemsModule(temp.Path, "<Item id=\"not_crafted\" />");

        var result = RunCli("inspect", "--submodule", fixture.SubModulePath, "--weapon", "not_crafted");

        Assert.True(result.ExitCode == 2, "inspect accepted a non-CraftedItem id");
        Assert.True(result.Output.Contains("No CraftedItem with id 'not_crafted' was found", StringComparison.Ordinal), "inspect did not explain the non-CraftedItem id");
    }

    public void InspectResolvesCraftingDataAcrossDependentModules()
    {
        using var temp = new TempDirectory();
        var modules = Directory.CreateDirectory(Path.Combine(temp.Path, "Modules"));
        var native = Directory.CreateDirectory(Path.Combine(modules.FullName, "Native"));
        var extension = Directory.CreateDirectory(Path.Combine(modules.FullName, "CraftingExtension"));
        var target = Directory.CreateDirectory(Path.Combine(modules.FullName, "TargetMod"));
        var nativeData = Directory.CreateDirectory(Path.Combine(native.FullName, "ModuleData"));
        var extensionData = Directory.CreateDirectory(Path.Combine(extension.FullName, "ModuleData"));
        var targetData = Directory.CreateDirectory(Path.Combine(target.FullName, "ModuleData"));
        var targetSubModule = Path.Combine(target.FullName, "SubModule.xml");

        File.WriteAllText(Path.Combine(native.FullName, "SubModule.xml"), """
            <Module><Id value="Native" /><Xmls><XmlNode><XmlName id="CraftingPieces" path="pieces" /><XmlName id="CraftingTemplates" path="templates" /></XmlNode></Xmls></Module>
            """);
        File.WriteAllText(Path.Combine(nativeData.FullName, "pieces.xml"), """
            <CraftingPieces><CraftingPiece id="handle" piece_type="Handle" length="10"><BuildData piece_offset="1" /></CraftingPiece></CraftingPieces>
            """);
        File.WriteAllText(Path.Combine(nativeData.FullName, "templates.xml"), """
            <CraftingTemplates><CraftingTemplate id="Custom"><PieceDatas><PieceData piece_type="Handle" build_order="0" /><PieceData piece_type="Blade" build_order="1" /></PieceDatas></CraftingTemplate></CraftingTemplates>
            """);
        File.WriteAllText(Path.Combine(extension.FullName, "SubModule.xml"), """
            <Module><Id value="CraftingExtension" /><DependedModules><DependedModule Id="Native" /></DependedModules><Xmls><XmlNode><XmlName id="CraftingPieces" path="pre" /></XmlNode></Xmls></Module>
            """);
        File.WriteAllText(Path.Combine(extensionData.FullName, "pre.xslt"), OffsetTransform(7));
        File.WriteAllText(targetSubModule, """
            <Module><Id value="TargetMod" /><DependedModules><DependedModule Id="CraftingExtension" /></DependedModules><Xmls><XmlNode><XmlName id="CraftingPieces" path="pieces" /><XmlName id="Items" path="items" /></XmlNode></Xmls></Module>
            """);
        File.WriteAllText(Path.Combine(targetData.FullName, "pieces.xml"), """
            <CraftingPieces><CraftingPiece id="blade" piece_type="Blade" length="20"><BuildData /></CraftingPiece></CraftingPieces>
            """);
        File.WriteAllText(Path.Combine(targetData.FullName, "items.xml"), """
            <Items><CraftedItem id="crafted_spear" crafting_template="Custom"><Pieces><Piece id="handle" Type="Handle" /><Piece id="blade" Type="Blade" /></Pieces></CraftedItem></Items>
            """);

        var result = RunCli("inspect", "--submodule", targetSubModule, "--weapon", "crafted_spear");

        Assert.True(result.ExitCode == 0, $"dependent-module inspect failed: {result.Output}");
        using var json = JsonDocument.Parse(result.Output);
        var handle = json.RootElement.GetProperty("pieces").EnumerateArray().Single(piece => piece.GetProperty("id").GetString() == "handle");
        Assert.True(handle.GetProperty("piece_offset").GetSingle() == 7, "XSLT-only declaration from a dependency was not applied to the accumulated XML");
    }

    public void InspectIsPrimaryAndLayoutRemainsCompatibilityAlias()
    {
        using var temp = new TempDirectory();
        var fixture = CreateCraftedItemsModule(temp.Path);

        var inspect = RunCli("inspect", "--submodule", fixture.SubModulePath, "--weapon", "first");
        var layout = RunCli("layout", "--submodule", fixture.SubModulePath, "--weapon", "first");

        Assert.True(inspect.ExitCode == 0, $"inspect failed: {inspect.Output}");
        Assert.True(layout.ExitCode == 0, $"layout compatibility alias failed: {layout.Output}");
        Assert.True(inspect.Output == layout.Output, "inspect and layout produced different reports");
    }

    public void HelpDocumentsXmlDrivenInspectUsage()
    {
        var result = RunCli("--help");

        Assert.True(result.ExitCode == 0, $"help failed: {result.Output}");
        Assert.True(result.Output.Contains("inspect --submodule <SubModule.xml> --weapon <CraftedItem-id> [--weapon <CraftedItem-id> ...]", StringComparison.Ordinal), "help did not document repeatable XML-driven --weapon usage");
        Assert.True(!result.Output.Contains("--weapons", StringComparison.Ordinal), "help still documents the removed manifest option");
        Assert.True(result.Output.Contains("--out is always an output directory", StringComparison.Ordinal), "help did not document --out as a directory for every command");
        Assert.True(!result.Output.Contains("--render-dir", StringComparison.Ordinal), "help still documents the removed --render-dir option");
    }

    public void AuditRejectsTheRemovedRenderDirOption()
    {
        using var temp = new TempDirectory();
        var fixture = CreatePairRenderModule(temp.Path);

        var result = RunCli(
            "audit",
            "--submodule", fixture.SubModulePath,
            "--fbx-folder", fixture.FbxFolder,
            "--template", "Visual",
            "--all-pairs",
            "--render-dir", Path.Combine(temp.Path, "visuals"));

        Assert.True(result.ExitCode == 2, "audit still accepted the removed --render-dir option");
        Assert.True(result.Output.Contains("--render-dir is no longer supported", StringComparison.Ordinal), "audit did not explain that --render-dir was replaced");
        Assert.True(result.Output.Contains("Use --out <dir> instead", StringComparison.Ordinal), "audit did not point at --out as the replacement");
    }

    public void OutRejectsAFileNameButAcceptsAnExistingDottedDirectory()
    {
        using var temp = new TempDirectory();
        var fixture = CreateCraftedItemsModule(temp.Path);
        var fileNamed = Path.Combine(temp.Path, "inspection.json");
        var preCreated = Directory.CreateDirectory(Path.Combine(temp.Path, "weird.json")).FullName;

        var rejected = RunCli("inspect", "--submodule", fixture.SubModulePath, "--weapon", "first", "--out", fileNamed);
        var accepted = RunCli("inspect", "--submodule", fixture.SubModulePath, "--weapon", "first", "--out", preCreated);

        Assert.True(rejected.ExitCode == 2, "--out accepted a value that names a .json file");
        Assert.True(rejected.Output.Contains("--out must be a directory", StringComparison.Ordinal), "--out did not explain that it needs a directory");
        Assert.True(rejected.Output.Contains("'" + fileNamed + "'", StringComparison.Ordinal), "--out did not name the rejected value");
        Assert.True(!Directory.Exists(fileNamed), "--out still created a directory for the rejected file name");
        Assert.True(accepted.ExitCode == 0, $"--out rejected a pre-created directory: {accepted.Output}");
        Assert.True(File.Exists(Path.Combine(preCreated, "inspect.json")), "--out did not write inspect.json into the pre-created directory");
    }

    public void AuditRequiresFbxFolder()
    {
        using var temp = new TempDirectory();
        var fixture = CreateCraftedItemsModule(temp.Path);

        var result = RunCli("audit", "--submodule", fixture.SubModulePath, "--all-templates");

        Assert.True(result.ExitCode == 2, "audit accepted a request without an FBX folder");
        Assert.True(result.Output.Contains("--fbx-folder is required", StringComparison.Ordinal), "audit did not explain that FBX geometry is required");
    }

    public void AuditMeasuresFbxBoundsAndReportsMissingMeshes()
    {
        using var temp = new TempDirectory();
        var fixture = CreateVisualAuditModule(temp.Path);

        var result = RunCli("audit", "--submodule", fixture.SubModulePath, "--fbx-folder", fixture.FbxFolder, "--template", "Visual");

        Assert.True(result.ExitCode == 0, $"FBX audit failed: {result.Output}");
        using var json = JsonDocument.Parse(result.Output);
        var records = json.RootElement.GetProperty("records").EnumerateArray().ToArray();
        Assert.True(records.Length == 3, "audit did not run the fixed-reference sweep for every usable guard piece");
        var aligned = records.Single(record => record.GetProperty("tip_id").GetString() == "guard_mesh");
        var visualGap = aligned.GetProperty("visual_gap");
        Assert.True(visualGap.ValueKind == JsonValueKind.Number, $"audit did not load the known FBX mesh: {result.Output}");
        Assert.True(visualGap.GetSingle() < -50, $"audit did not calculate the known FBX overlap (the grip top lies about 80 units past the tip base): {visualGap.GetSingle()}");
        Assert.True(!aligned.GetProperty("aligned").GetBoolean(), "audit marked the known FBX overlap as aligned");
        var missing = records.Single(record => record.GetProperty("tip_id").GetString() == "guard_missing");
        Assert.True(missing.GetProperty("missing_meshes").GetArrayLength() == 1, "audit did not report the missing FBX mesh");
        Assert.True(json.RootElement.GetProperty("summary").GetProperty("records_with_missing_meshes").GetInt32() == 1, "audit summary did not count missing-mesh records distinctly");
    }

    public void AuditExpandsAssetSourcesChildToSiblingFbxFolders()
    {
        using var temp = new TempDirectory();
        var fixture = CreateVisualAuditModule(temp.Path);
        var assetSources = Directory.CreateDirectory(Path.Combine(temp.Path, "AssetSources"));
        var weaponsFolder = Directory.CreateDirectory(Path.Combine(assetSources.FullName, "weapons"));
        var nativeFbxFolder = Directory.CreateDirectory(Path.Combine(assetSources.FullName, "native-fbx"));
        File.Copy(
            Path.Combine(fixture.FbxFolder, "test_meshes.fbx"),
            Path.Combine(nativeFbxFolder.FullName, "test_meshes.fbx"));

        var result = RunCli("audit", "--submodule", fixture.SubModulePath, "--fbx-folder", weaponsFolder.FullName, "--template", "Visual");

        Assert.True(result.ExitCode == 0, $"sibling FBX audit failed: {result.Output}");
        using var json = JsonDocument.Parse(result.Output);
        Assert.True(
            json.RootElement.GetProperty("fbx_folder").GetString() == assetSources.FullName,
            "audit did not report the expanded AssetSources search root");
        var knownMesh = json.RootElement.GetProperty("records").EnumerateArray()
            .Single(record => record.GetProperty("tip_id").GetString() == "guard_mesh");
        Assert.True(
            knownMesh.GetProperty("missing_meshes").GetArrayLength() == 0,
            "audit did not discover an FBX in an AssetSources sibling folder");
    }

    public void AuditEmitsBaselineAwareCorrectionPlan()
    {
        using var temp = new TempDirectory();
        var fixture = CreateVisualAuditModule(temp.Path);

        var unsafeResult = RunCli("audit", "--submodule", fixture.SubModulePath, "--fbx-folder", fixture.FbxFolder, "--template", "Visual");
        Assert.True(unsafeResult.ExitCode == 0, $"FBX audit failed: {unsafeResult.Output}");
        using var unsafeJson = JsonDocument.Parse(unsafeResult.Output);
        var unsafeRecords = unsafeJson.RootElement.GetProperty("records").EnumerateArray().ToArray();
        var baseline = unsafeRecords.Single(record => record.GetProperty("is_baseline").GetBoolean());
        Assert.True(baseline.GetProperty("baseline").GetProperty("tip_id").GetString() == "guard_mesh", "baseline did not identify the fixed reference pieces");
        Assert.True(!baseline.GetProperty("correction_plan").GetProperty("safe_to_apply").GetBoolean(), "the baseline was incorrectly offered as an automatic correction");
        Assert.True(baseline.GetProperty("correction_plan").GetProperty("reason").GetString() == "reference_baseline", "baseline correction plan lacked its reason");
        var unsafeCandidate = unsafeRecords.Single(record => record.GetProperty("tip_id").GetString() == "guard_adjusted");
        Assert.True(unsafeCandidate.GetProperty("candidate").GetProperty("build_data").GetProperty("previous_piece_offset").GetSingle() == 50, "candidate omitted its raw BuildData");
        Assert.True(unsafeCandidate.GetProperty("correction_plan").GetProperty("target_attribute").GetString() == "previous_piece_offset", "tip candidate targeted the wrong connector offset");
        Assert.True(!unsafeCandidate.GetProperty("correction_plan").GetProperty("safe_to_apply").GetBoolean(), "a misaligned reference baseline was incorrectly treated as safe");
        Assert.True(unsafeCandidate.GetProperty("correction_plan").GetProperty("reason").GetString() == "reference_baseline_is_misaligned", "misaligned baseline reason was not reported");

        var safeResult = RunCli("audit", "--submodule", fixture.SubModulePath, "--fbx-folder", fixture.FbxFolder, "--template", "Visual", "--tolerance", "100000");
        Assert.True(safeResult.ExitCode == 0, $"tolerant FBX audit failed: {safeResult.Output}");
        using var safeJson = JsonDocument.Parse(safeResult.Output);
        var safeCandidate = safeJson.RootElement.GetProperty("records").EnumerateArray()
            .Single(record => record.GetProperty("tip_id").GetString() == "guard_adjusted");
        var plan = safeCandidate.GetProperty("correction_plan");
        Assert.True(plan.GetProperty("safe_to_apply").GetBoolean(), "an aligned complete baseline did not permit its candidate plan");
        Assert.True(MathF.Abs(plan.GetProperty("delta_to_match_baseline").GetSingle() + 50) < 0.01f, "correction delta did not restore the candidate to the baseline seam");
        Assert.True(MathF.Abs(plan.GetProperty("suggested_value_to_match_baseline").GetSingle()) < 0.01f, "suggested connector offset did not restore the baseline value");
    }

    public void AuditAllPairsRendersEveryCombinationAndPieceGallery()
    {
        using var temp = new TempDirectory();
        var fixture = CreatePairRenderModule(temp.Path);
        var outDir = Path.Combine(temp.Path, "visuals");

        var result = RunCli(
            "audit",
            "--submodule", fixture.SubModulePath,
            "--fbx-folder", fixture.FbxFolder,
            "--template", "Visual",
            "--all-pairs",
            "--out", outDir);

        Assert.True(result.ExitCode == 0, $"all-pairs visual audit failed: {result.Output}");
        var reportPath = Path.Combine(outDir, "audit.json");
        Assert.True(File.Exists(reportPath), "all-pairs audit did not write audit.json into the --out directory");
        using var json = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.True(
            json.RootElement.GetProperty("audit_strategy").GetString() == "complete_player_selectable_physical_pair_sweep",
            "all-pairs audit reported the wrong strategy");
        var records = json.RootElement.GetProperty("records").EnumerateArray().ToArray();
        Assert.True(records.Length == 4, "all-pairs audit did not produce the 2 x 2 adjacent-piece cross product");
        var combinations = records
            .Select(record => $"{record.GetProperty("grip_id").GetString()}|{record.GetProperty("tip_id").GetString()}")
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(combinations.SetEquals(["handle_a|guard_a", "handle_a|guard_b", "handle_b|guard_a", "handle_b|guard_b"]),
            "all-pairs audit omitted or duplicated a player-selectable combination");
        Assert.True(records.All(record => File.Exists(record.GetProperty("image").GetString())),
            "all-pairs audit did not write a PNG for every combination");
        Assert.True(records.All(record => record.GetProperty("image").GetString()!.StartsWith(Path.GetFullPath(outDir), StringComparison.OrdinalIgnoreCase)),
            "all-pairs audit wrote its pair PNGs outside the --out directory");

        var pieceImages = json.RootElement.GetProperty("piece_images").EnumerateArray().ToArray();
        Assert.True(pieceImages.Length == 4, "declared-type gallery did not contain every usable piece");
        Assert.True(pieceImages.All(piece => File.Exists(piece.GetProperty("image").GetString())),
            "declared-type gallery did not write every piece PNG");
    }

    public void AuditAllPairsWithoutOutStillReportsMeasurementsOnStdout()
    {
        using var temp = new TempDirectory();
        var fixture = CreatePairRenderModule(temp.Path);

        var result = RunCli(
            "audit",
            "--submodule", fixture.SubModulePath,
            "--fbx-folder", fixture.FbxFolder,
            "--template", "Visual",
            "--all-pairs");

        Assert.True(result.ExitCode == 0, $"all-pairs audit without --out failed: {result.Output}");
        using var json = JsonDocument.Parse(result.Output);
        Assert.True(json.RootElement.GetProperty("records").GetArrayLength() == 4,
            "all-pairs audit without --out did not report every combination on stdout");
        Assert.True(json.RootElement.GetProperty("render_dir").ValueKind == JsonValueKind.Null,
            "all-pairs audit without --out claimed a render directory");
        Assert.True(json.RootElement.GetProperty("records").EnumerateArray()
                .All(record => record.GetProperty("image").ValueKind == JsonValueKind.Null),
            "all-pairs audit without --out still claimed pair images");
    }

    public void AuditCollapsesDefaultPolearmSlotsIntoHandleBladePairs()
    {
        using var temp = new TempDirectory();
        var fixture = CreatePolearmPlaceholderModule(temp.Path);

        var result = RunCli(
            "audit",
            "--submodule", fixture.SubModulePath,
            "--fbx-folder", fixture.FbxFolder,
            "--template", "Polearm",
            "--all-pairs");

        Assert.True(result.ExitCode == 0, $"polearm physical-pair audit failed: {result.Output}");
        using var json = JsonDocument.Parse(result.Output);
        var records = json.RootElement.GetProperty("records").EnumerateArray().ToArray();
        Assert.True(records.Length == 4, "polearm audit did not produce the 2 x 2 handle-to-blade cross product");
        Assert.True(records.All(record =>
                record.GetProperty("grip_type").GetString() == "Handle"
                && record.GetProperty("tip_type").GetString() == "Blade"),
            "polearm audit retained a default placeholder as a physical seam");
        Assert.True(records.All(record => record.GetProperty("missing_meshes").GetArrayLength() == 0),
            "polearm physical pairs incorrectly reported the collapsed placeholders as missing meshes");

        var collapsed = json.RootElement.GetProperty("collapsed_placeholder_slots").EnumerateArray().ToArray();
        Assert.True(collapsed.Length == 2, "polearm audit did not report both collapsed placeholder slots");
        Assert.True(collapsed.Select(slot => slot.GetProperty("piece_type").GetString()).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["Guard", "Pommel"]),
            "polearm audit collapsed the wrong piece types");
    }

    public void AuditDefaultsScalePercentTo100()
    {
        using var temp = new TempDirectory();
        var fixture = CreateVisualAuditModule(temp.Path);

        var result = RunCli(
            "audit",
            "--submodule", fixture.SubModulePath,
            "--fbx-folder", fixture.FbxFolder,
            "--template", "Visual");

        Assert.True(result.ExitCode == 0, $"default-scale audit failed: {result.Output}");
        using var json = JsonDocument.Parse(result.Output);
        Assert.True(json.RootElement.GetProperty("scale_percent").GetInt32() == 100,
            "omitted --scale-percent did not default the root report to 100");
        Assert.True(json.RootElement.GetProperty("records").EnumerateArray()
                .All(record => record.GetProperty("candidate").GetProperty("scale_percent").GetInt32() == 100),
            "omitted --scale-percent did not preserve 100 in audit candidate layouts");
    }

    public void AuditScalePercentPropagatesToLayoutAndPieceGallery()
    {
        using var temp = new TempDirectory();
        var fixture = CreatePairRenderModule(temp.Path);
        var outDir = Path.Combine(temp.Path, "scaled-visuals");

        var result = RunCli(
            "audit",
            "--submodule", fixture.SubModulePath,
            "--fbx-folder", fixture.FbxFolder,
            "--template", "Visual",
            "--all-pairs",
            "--out", outDir,
            "--scale-percent", "90");

        Assert.True(result.ExitCode == 0, $"scaled visual audit failed: {result.Output}");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "audit.json")));
        Assert.True(json.RootElement.GetProperty("scale_percent").GetInt32() == 90,
            "scaled audit omitted the root scale_percent");
        var records = json.RootElement.GetProperty("records").EnumerateArray().ToArray();
        Assert.True(records.All(record =>
                record.GetProperty("candidate").GetProperty("scale_percent").GetInt32() == 90),
            "scaled audit candidate layouts did not retain 90 percent");
        Assert.True(records.SelectMany(record => record.GetProperty("rendered_pieces").EnumerateArray())
                .All(piece => piece.GetProperty("scale_factor").GetInt32() == 90),
            "scaled pair renders did not retain 90 percent in their piece reports");
        Assert.True(json.RootElement.GetProperty("piece_images").EnumerateArray()
                .All(piece => piece.GetProperty("measurements").GetProperty("scale_factor").GetInt32() == 90),
            "scaled piece-gallery renders did not retain 90 percent in their measurements");
    }

    public void AuditRejectsInvalidScalePercentValues()
    {
        using var temp = new TempDirectory();
        var fixture = CreateVisualAuditModule(temp.Path);
        var invalidValues = new[]
        {
            new[] { "--scale-percent", "0" },
            new[] { "--scale-percent", "-1" },
            new[] { "--scale-percent", "90.5" },
            new[] { "--scale-percent" },
            new[] { "--scale-percent", "90", "--scale-percent", "110" },
        };

        foreach (var invalidValue in invalidValues)
        {
            var result = RunCli(new[]
            {
                "audit", "--submodule", fixture.SubModulePath, "--fbx-folder", fixture.FbxFolder, "--template", "Visual",
            }.Concat(invalidValue).ToArray());

            Assert.True(result.ExitCode == 2,
                $"audit accepted invalid scale arguments '{string.Join(" ", invalidValue)}'");
            Assert.True(result.Output.Contains("--scale-percent", StringComparison.Ordinal),
                $"invalid scale arguments lacked a clear --scale-percent error: {result.Output}");
        }
    }

    public void RenderRequiresFbxFolder()
    {
        using var temp = new TempDirectory();
        var fixture = CreateRenderModule(temp.Path);

        var result = RunCli("render", "--submodule", fixture.SubModulePath, "--weapon", "render_test");

        Assert.True(result.ExitCode == 2, "render accepted a request without an FBX folder");
        Assert.True(result.Output.Contains("--fbx-folder is required", StringComparison.Ordinal), "render did not explain that FBX geometry is required");
    }

    public void RenderProducesImageAndMeasurements()
    {
        using var temp = new TempDirectory();
        var fixture = CreateRenderModule(temp.Path);
        var outDir = Path.Combine(temp.Path, "out");

        var result = RunCli("render", "--submodule", fixture.SubModulePath, "--weapon", "render_test", "--fbx-folder", fixture.FbxFolder, "--out", outDir);

        Assert.True(result.ExitCode == 0, $"render failed: {result.Output}");
        var imagePath = Path.Combine(outDir, "render.png");
        Assert.True(File.Exists(imagePath), "render did not produce render.png");
        Assert.True(new FileInfo(imagePath).Length > 10000, "render.png was blank or too small");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "render.json")));
        var pieces = json.RootElement.GetProperty("pieces").EnumerateArray().ToArray();
        Assert.True(pieces.Length == 2, "render did not assemble both weapon pieces");
        Assert.True(pieces.All(piece => piece.GetProperty("mesh_found").GetBoolean()), "render did not load both test meshes from the FBX folder");
    }

    public void RenderHonorsFbxUnitScaleMetadata()
    {
        using var temp = new TempDirectory();
        var fixture = CreateUnitScaledRenderModule(temp.Path);
        var outDir = Path.Combine(temp.Path, "out");

        var result = RunCli(
            "render",
            "--submodule", fixture.SubModulePath,
            "--weapon", "unit_scaled_hammer",
            "--fbx-folder", fixture.FbxFolder,
            "--out", outDir);

        Assert.True(result.ExitCode == 0, $"unit-scaled FBX render failed: {result.Output}");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "render.json")));
        var piece = json.RootElement.GetProperty("pieces").EnumerateArray().Single();
        Assert.True(piece.GetProperty("mesh_found").GetBoolean(), "unit-scaled FBX mesh was not found");
        var worldBounds = piece.GetProperty("world_bounds");
        var minY = worldBounds.GetProperty("min")[1].GetSingle();
        var maxY = worldBounds.GetProperty("max")[1].GetSingle();
        var minZ = worldBounds.GetProperty("min")[2].GetSingle();
        var maxZ = worldBounds.GetProperty("max")[2].GetSingle();
        Assert.True(
            maxY - minY > 1f,
            $"FBX UnitScaleFactor was ignored; expected a multi-unit hammer mesh but measured {maxY - minY}");
        Assert.True(
            maxY - minY > maxZ - minZ,
            "FBX UpAxis metadata was ignored; the hammer handle remained crosswise to the crafting axis");
    }

    public void RenderPlacesTheHolsterMeshAtTheBladePivot()
    {
        using var temp = new TempDirectory();
        var fixture = CreateHolsterRenderModule(temp.Path, "piece_type_to_scale_holster_with=\"Blade\" hidden_piece_types_on_holster=\"Blade\"", "test_tip_mesh");
        var outDir = Path.Combine(temp.Path, "out");

        var result = RunCli("render", "--submodule", fixture.SubModulePath, "--weapon", "sheathed_test", "--fbx-folder", fixture.FbxFolder, "--out", outDir);

        Assert.True(result.ExitCode == 0, $"holster render failed: {result.Output}");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "render.json")));
        var scabbard = json.RootElement.GetProperty("scabbard");
        Assert.True(scabbard.GetProperty("status").GetString() == "rendered", $"the blade holster mesh was not rendered: {scabbard}");
        Assert.True(scabbard.GetProperty("measurements").GetProperty("mesh_found").GetBoolean(), "the holster mesh was not loaded from the FBX folder");
        Assert.True(float.IsFinite(scabbard.GetProperty("mouth_offset").GetSingle()), "the holster did not measure its mouth against the blade base");
        Assert.True(float.IsFinite(scabbard.GetProperty("tip_coverage").GetSingle()), "the holster did not measure how far it covers the blade");

        var pieces = json.RootElement.GetProperty("pieces").EnumerateArray().ToArray();
        var blade = pieces.Single(piece => piece.GetProperty("type").GetString() == "Blade");
        Assert.True(scabbard.GetProperty("measurements").GetProperty("pivot").GetSingle() == blade.GetProperty("pivot").GetSingle(),
            "the holster was not placed at the blade pivot");
        Assert.True(pieces.Length == 3, "the holster leaked into the assembled weapon pieces");
        Assert.True(json.RootElement.GetProperty("seams").GetArrayLength() == 2, "the holster fabricated a seam against a weapon piece");
    }

    public void RenderSkipsTheHolsterForTemplatesThatSuppressIt()
    {
        var suppressions = new[]
        {
            ("use_weapon_as_holster_mesh=\"true\"", "template_uses_weapon_as_holster_mesh"),
            ("always_show_holster_with_weapon=\"true\"", "template_always_shows_holster_with_weapon"),
        };

        foreach (var (attributes, status) in suppressions)
        {
            using var temp = new TempDirectory();
            var fixture = CreateHolsterRenderModule(temp.Path, attributes, "test_tip_mesh");
            var outDir = Path.Combine(temp.Path, "out");

            var result = RunCli("render", "--submodule", fixture.SubModulePath, "--weapon", "sheathed_test", "--fbx-folder", fixture.FbxFolder, "--out", outDir);

            Assert.True(result.ExitCode == 0, $"render with '{attributes}' failed: {result.Output}");
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "render.json")));
            var scabbard = json.RootElement.GetProperty("scabbard");
            Assert.True(scabbard.GetProperty("status").GetString() == status, $"'{attributes}' did not report status '{status}': {scabbard}");
            Assert.True(scabbard.GetProperty("measurements").ValueKind == JsonValueKind.Null, $"'{attributes}' suppressed the holster but still measured it");
        }
    }

    public void RenderReportsBladesWithoutAHolsterMeshAndKeepsThemOutOfThePieces()
    {
        using var temp = new TempDirectory();
        var fixture = CreateHolsterRenderModule(temp.Path, "piece_type_to_scale_holster_with=\"Blade\"", holsterMesh: null);
        var outDir = Path.Combine(temp.Path, "out");

        var result = RunCli("render", "--submodule", fixture.SubModulePath, "--weapon", "sheathed_test", "--fbx-folder", fixture.FbxFolder, "--out", outDir);

        Assert.True(result.ExitCode == 0, $"holster-less render failed: {result.Output}");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "render.json")));
        Assert.True(json.RootElement.GetProperty("scabbard").GetProperty("status").GetString() == "no_holster_mesh",
            "a blade without holster_mesh did not explain the absent holster");
        var pieces = json.RootElement.GetProperty("pieces").EnumerateArray().ToArray();
        Assert.True(pieces.Length == 3, "the absent holster changed the assembled weapon pieces");
        Assert.True(pieces.All(piece => piece.GetProperty("type").GetString() != "Scabbard"), "a holster was reported as an assembled weapon piece");
    }

    public void RenderHolsteredHidesTheTemplatesHiddenPieceTypes()
    {
        using var temp = new TempDirectory();
        var fixture = CreateHolsterRenderModule(temp.Path, "piece_type_to_scale_holster_with=\"Blade\" hidden_piece_types_on_holster=\"Blade:Pommel\"", "test_tip_mesh");
        var outDir = Path.Combine(temp.Path, "out");

        var result = RunCli("render", "--submodule", fixture.SubModulePath, "--weapon", "sheathed_test", "--fbx-folder", fixture.FbxFolder, "--out", outDir, "--holstered");

        Assert.True(result.ExitCode == 0, $"holstered render failed: {result.Output}");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "render.json")));
        var pieces = json.RootElement.GetProperty("pieces").EnumerateArray().ToArray();
        Assert.True(pieces.Select(piece => piece.GetProperty("type").GetString()).SequenceEqual(["Handle", "Guard"]),
            "--holstered did not omit the piece types the template hides behind the holster");
        var scabbard = json.RootElement.GetProperty("scabbard");
        Assert.True(scabbard.GetProperty("status").GetString() == "rendered", $"the holstered view did not render the holster: {scabbard}");
        Assert.True(scabbard.GetProperty("hidden_piece_types").EnumerateArray().Select(type => type.GetString()).SequenceEqual(["Blade", "Pommel"]),
            "the colon-separated hidden piece types were not reported");
        Assert.True(float.IsFinite(scabbard.GetProperty("tip_coverage").GetSingle()),
            "tip coverage was dropped because the blade was hidden behind the holster");
    }

    public void RenderScalesNonFullScalePiecesAlongTheAxisOnly()
    {
        using var temp = new TempDirectory();
        var fixture = CreateScaledRenderModule(temp.Path);

        var full = RunCli("render", "--submodule", fixture.SubModulePath, "--weapon", "unscaled_pair", "--fbx-folder", fixture.FbxFolder, "--out", Path.Combine(temp.Path, "full"));
        var half = RunCli("render", "--submodule", fixture.SubModulePath, "--weapon", "scaled_pair", "--fbx-folder", fixture.FbxFolder, "--out", Path.Combine(temp.Path, "half"));

        Assert.True(full.ExitCode == 0, $"unscaled render failed: {full.Output}");
        Assert.True(half.ExitCode == 0, $"scaled render failed: {half.Output}");
        using var fullJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp.Path, "full", "render.json")));
        using var halfJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp.Path, "half", "render.json")));

        static (float Lateral, float Axis) Extent(JsonDocument json, string type)
        {
            var bounds = json.RootElement.GetProperty("pieces").EnumerateArray()
                .Single(piece => piece.GetProperty("type").GetString() == type)
                .GetProperty("world_bounds");
            return (
                bounds.GetProperty("max")[0].GetSingle() - bounds.GetProperty("min")[0].GetSingle(),
                bounds.GetProperty("max")[1].GetSingle() - bounds.GetProperty("min")[1].GetSingle());
        }

        var bladeFull = Extent(fullJson, "Blade");
        var bladeHalf = Extent(halfJson, "Blade");
        Assert.True(MathF.Abs(bladeHalf.Axis - bladeFull.Axis * 0.5f) < 0.01f,
            $"a piece without full_scale was not shortened along the crafting axis: {bladeFull.Axis} -> {bladeHalf.Axis}");
        Assert.True(MathF.Abs(bladeHalf.Lateral - bladeFull.Lateral) < 0.001f,
            $"a piece without full_scale was narrowed by its scale factor: {bladeFull.Lateral} -> {bladeHalf.Lateral}");

        var guardFull = Extent(fullJson, "Guard");
        var guardHalf = Extent(halfJson, "Guard");
        Assert.True(MathF.Abs(guardHalf.Lateral - guardFull.Lateral * 0.5f) < 0.01f,
            $"a full_scale piece was not narrowed by its scale factor: {guardFull.Lateral} -> {guardHalf.Lateral}");
    }

    public void ScabbardEligibilityRefusesSkeletonHolsters()
    {
        var blade = HolsterPiece("quiver_blade", "Blade", "quiver_scabbard");
        var layout = LayoutOf(blade);
        var pieces = new Dictionary<string, CraftingPiece>(StringComparer.OrdinalIgnoreCase) { [blade.Id] = blade };
        var catalog = new FakeMeshCatalog("quiver_scabbard_skeleton");

        var verdict = new ScabbardEligibility(catalog, honorsAlwaysShowHolsterWithWeapon: true).Evaluate(layout, pieces, template: null);

        Assert.True(verdict.Status == "unsupported_skeleton_holster", $"a skeleton-deformed holster was accepted: {verdict.Status}");
        Assert.True(verdict.Mesh == "quiver_scabbard", "the refused holster mesh was not named");
        Assert.True(verdict.Warnings.Any(warning => warning.Contains("quiver", StringComparison.OrdinalIgnoreCase)),
            "the skeleton refusal did not explain that quivers cannot be placed offline");
    }

    public void ScabbardEligibilityAcceptsAPlainHolsterAndFlagsTheProbeAsAHeuristic()
    {
        var blade = HolsterPiece("sword_blade", "Blade", "sword_scabbard");
        var pieces = new Dictionary<string, CraftingPiece>(StringComparer.OrdinalIgnoreCase) { [blade.Id] = blade };

        var verdict = new ScabbardEligibility(new FakeMeshCatalog(), honorsAlwaysShowHolsterWithWeapon: true)
            .Evaluate(LayoutOf(blade), pieces, template: null);

        Assert.True(verdict.Status == ScabbardEligibility.Eligible, $"a plain holster was refused: {verdict.Status}");
        Assert.True(verdict.Warnings.Any(warning => warning.Contains("cannot rule a quiver out", StringComparison.OrdinalIgnoreCase)),
            "the skeleton probe was reported as conclusive");
    }

    public void HolsteredPlacementFallsBackToTheOriginWhenTheTemplateNamesAMissingPieceType()
    {
        var blade = HolsterPiece("sword_blade", "Blade", "sword_scabbard");
        var pieces = new Dictionary<string, CraftingPiece>(StringComparer.OrdinalIgnoreCase) { [blade.Id] = blade };
        var template = new TemplateData("Bow", [], [], false, false, "Bowstring", []);

        var placement = new HolsteredScabbardPlacement().Locate(LayoutOf(blade), pieces, template);

        Assert.True(placement is not null, "a missing scale piece type aborted the holster instead of falling back");
        Assert.True(placement!.Pivot == 0f && placement.ScaleFactor == 100, $"the fallback holster was not left unscaled at the origin: {placement}");
        Assert.True(placement.Warning is not null, "the fallback holster placement was not explained");
    }

    public void ScabbardMeasurerReadsTheHolsterMouthAtItsLowEnd()
    {
        var guard = MeasuredPiece("Guard", -2f, 4f);
        var blade = MeasuredPiece("Blade", 3f, 100f);
        var scabbard = new Bounds(-2f, 5f, -2f, 2f, 103f, 2f);

        var (mouthOffset, tipCoverage) = ScabbardMeasurer.Measure(scabbard, [guard, blade]);

        Assert.True(mouthOffset == 2f, $"the mouth offset was not measured from the holster's low end to the blade base: {mouthOffset}");
        Assert.True(tipCoverage == 3f, $"tip coverage was not measured from the holster's closed end past the blade tip: {tipCoverage}");
    }

    private static CraftingPiece HolsterPiece(string id, string pieceType, string? holsterMesh) =>
        new(id, pieceType, id + "_mesh", 100f, 50f, 50f, 0f, 0f, 0f, holsterMesh, false);

    private static WeaponLayoutReport LayoutOf(CraftingPiece piece) =>
        new("weapon", "Template", [], [PieceReport.Create(piece.PieceType, new SelectedPiece(piece, 100), 0f, 0)], [], null, 0f);

    private static RenderPiece MeasuredPiece(string type, float minY, float maxY) =>
        new(type, type, type, 100, 0f, null, 1f, 0f, new Bounds(-1f, minY, -1f, 1f, maxY, 1f), 1f);

    private static CraftedItemsFixture CreateCraftedItemsModule(string root, string? extraItem = null)
    {
        var moduleData = Directory.CreateDirectory(Path.Combine(root, "ModuleData"));
        var subModulePath = Path.Combine(root, "SubModule.xml");
        var itemsPath = Path.Combine(moduleData.FullName, "items.xml");
        File.WriteAllText(subModulePath, """
            <Module><Xmls><XmlNode><XmlName id="CraftingPieces" path="pieces" /><XmlName id="CraftingTemplates" path="templates" /><XmlName id="Items" path="items" /></XmlNode></Xmls></Module>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "pieces.xml"), """
            <CraftingPieces><CraftingPiece id="handle" piece_type="Handle" length="10"><BuildData /></CraftingPiece></CraftingPieces>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "templates.xml"), """
            <CraftingTemplates><CraftingTemplate id="Custom"><PieceDatas><PieceData piece_type="Handle" build_order="0" /></PieceDatas></CraftingTemplate></CraftingTemplates>
            """);
        File.WriteAllText(itemsPath, $"""
            <Items>
              <CraftedItem id="first" crafting_template="Custom"><Pieces><Piece id="handle" Type="Handle" /></Pieces></CraftedItem>
              <CraftedItem id="second" crafting_template="Custom"><Pieces><Piece id="handle" Type="Handle" /></Pieces></CraftedItem>
              {extraItem}
            </Items>
            """);
        return new CraftedItemsFixture(subModulePath, itemsPath);
    }

    private static VisualAuditFixture CreateVisualAuditModule(string root)
    {
        var moduleData = Directory.CreateDirectory(Path.Combine(root, "ModuleData"));
        var fbxFolder = Directory.CreateDirectory(Path.Combine(root, "fbx"));
        var subModulePath = Path.Combine(root, "SubModule.xml");
        File.WriteAllText(subModulePath, """
            <Module><Xmls><XmlNode><XmlName id="CraftingPieces" path="pieces" /><XmlName id="CraftingTemplates" path="templates" /></XmlNode></Xmls></Module>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "pieces.xml"), """
            <CraftingPieces>
              <CraftingPiece id="handle" piece_type="Handle" mesh="test_grip_mesh" length="20"><BuildData /></CraftingPiece>
              <CraftingPiece id="guard_mesh" piece_type="Guard" mesh="test_tip_mesh" length="20"><BuildData /></CraftingPiece>
              <CraftingPiece id="guard_adjusted" piece_type="Guard" mesh="test_tip_mesh" length="20"><BuildData previous_piece_offset="50" /></CraftingPiece>
              <CraftingPiece id="guard_missing" piece_type="Guard" mesh="missing_mesh" length="20"><BuildData /></CraftingPiece>
            </CraftingPieces>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "templates.xml"), """
            <CraftingTemplates><CraftingTemplate id="Visual"><PieceDatas><PieceData piece_type="Handle" build_order="0" /><PieceData piece_type="Guard" build_order="1" /></PieceDatas><UsablePieces><UsablePiece piece_id="handle" /><UsablePiece piece_id="guard_mesh" /><UsablePiece piece_id="guard_adjusted" /><UsablePiece piece_id="guard_missing" /></UsablePieces></CraftingTemplate></CraftingTemplates>
            """);
        return new VisualAuditFixture(subModulePath, WriteTestMeshFbx(root));
    }

    private static VisualAuditFixture CreatePairRenderModule(string root)
    {
        var moduleData = Directory.CreateDirectory(Path.Combine(root, "ModuleData"));
        var subModulePath = Path.Combine(root, "SubModule.xml");
        File.WriteAllText(subModulePath, """
            <Module><Xmls><XmlNode><XmlName id="CraftingPieces" path="pieces" /><XmlName id="CraftingTemplates" path="templates" /></XmlNode></Xmls></Module>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "pieces.xml"), """
            <CraftingPieces>
              <CraftingPiece id="handle_a" piece_type="Handle" mesh="test_grip_mesh" length="20"><BuildData /></CraftingPiece>
              <CraftingPiece id="handle_b" piece_type="Handle" mesh="test_tip_mesh" length="20"><BuildData /></CraftingPiece>
              <CraftingPiece id="guard_a" piece_type="Guard" mesh="test_grip_mesh" length="20"><BuildData /></CraftingPiece>
              <CraftingPiece id="guard_b" piece_type="Guard" mesh="test_tip_mesh" length="20"><BuildData /></CraftingPiece>
            </CraftingPieces>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "templates.xml"), """
            <CraftingTemplates><CraftingTemplate id="Visual"><PieceDatas><PieceData piece_type="Handle" build_order="0" /><PieceData piece_type="Guard" build_order="1" /></PieceDatas><UsablePieces><UsablePiece piece_id="handle_a" /><UsablePiece piece_id="handle_b" /><UsablePiece piece_id="guard_a" /><UsablePiece piece_id="guard_b" /></UsablePieces></CraftingTemplate></CraftingTemplates>
            """);
        return new VisualAuditFixture(subModulePath, WriteTestMeshFbx(root));
    }

    private static VisualAuditFixture CreatePolearmPlaceholderModule(string root)
    {
        var moduleData = Directory.CreateDirectory(Path.Combine(root, "ModuleData"));
        var subModulePath = Path.Combine(root, "SubModule.xml");
        File.WriteAllText(subModulePath, """
            <Module><Xmls><XmlNode><XmlName id="CraftingPieces" path="pieces" /><XmlName id="CraftingTemplates" path="templates" /></XmlNode></Xmls></Module>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "pieces.xml"), """
            <CraftingPieces>
              <CraftingPiece id="default_polearm_pommel" piece_type="Pommel" mesh="" length="0"><BuildData /></CraftingPiece>
              <CraftingPiece id="handle_a" piece_type="Handle" mesh="test_grip_mesh" length="20"><BuildData /></CraftingPiece>
              <CraftingPiece id="handle_b" piece_type="Handle" mesh="test_tip_mesh" length="20"><BuildData /></CraftingPiece>
              <CraftingPiece id="default_polearm_guard" piece_type="Guard" mesh="" length="0"><BuildData /></CraftingPiece>
              <CraftingPiece id="blade_a" piece_type="Blade" mesh="test_grip_mesh" length="20"><BuildData /></CraftingPiece>
              <CraftingPiece id="blade_b" piece_type="Blade" mesh="test_tip_mesh" length="20"><BuildData /></CraftingPiece>
            </CraftingPieces>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "templates.xml"), """
            <CraftingTemplates><CraftingTemplate id="Polearm"><PieceDatas><PieceData piece_type="Pommel" build_order="-1" /><PieceData piece_type="Handle" build_order="0" /><PieceData piece_type="Guard" build_order="1" /><PieceData piece_type="Blade" build_order="2" /></PieceDatas><UsablePieces><UsablePiece piece_id="default_polearm_pommel" /><UsablePiece piece_id="handle_a" /><UsablePiece piece_id="handle_b" /><UsablePiece piece_id="default_polearm_guard" /><UsablePiece piece_id="blade_a" /><UsablePiece piece_id="blade_b" /></UsablePieces></CraftingTemplate></CraftingTemplates>
            """);
        return new VisualAuditFixture(subModulePath, WriteTestMeshFbx(root));
    }

    private static RenderFixture CreateRenderModule(string root)
    {
        var moduleData = Directory.CreateDirectory(Path.Combine(root, "ModuleData"));
        var subModulePath = Path.Combine(root, "SubModule.xml");
        File.WriteAllText(subModulePath, """
            <Module><Xmls><XmlNode><XmlName id="CraftingPieces" path="pieces" /><XmlName id="CraftingTemplates" path="templates" /><XmlName id="Items" path="items" /></XmlNode></Xmls></Module>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "pieces.xml"), """
            <CraftingPieces>
              <CraftingPiece id="handle" piece_type="Handle" mesh="test_grip_mesh" length="20"><BuildData /></CraftingPiece>
              <CraftingPiece id="guard" piece_type="Guard" mesh="test_tip_mesh" length="20"><BuildData /></CraftingPiece>
            </CraftingPieces>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "templates.xml"), """
            <CraftingTemplates><CraftingTemplate id="Visual"><PieceDatas><PieceData piece_type="Handle" build_order="0" /><PieceData piece_type="Guard" build_order="1" /></PieceDatas></CraftingTemplate></CraftingTemplates>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "items.xml"), """
            <Items><CraftedItem id="render_test" crafting_template="Visual"><Pieces><Piece id="handle" Type="Handle" /><Piece id="guard" Type="Guard" /></Pieces></CraftedItem></Items>
            """);
        return new RenderFixture(subModulePath, WriteTestMeshFbx(root));
    }

    private static RenderFixture CreateUnitScaledRenderModule(string root)
    {
        var moduleData = Directory.CreateDirectory(Path.Combine(root, "ModuleData"));
        var subModulePath = Path.Combine(root, "SubModule.xml");
        File.WriteAllText(subModulePath, """
            <Module><Xmls><XmlNode><XmlName id="CraftingPieces" path="pieces" /><XmlName id="CraftingTemplates" path="templates" /><XmlName id="Items" path="items" /></XmlNode></Xmls></Module>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "pieces.xml"), """
            <CraftingPieces><CraftingPiece id="hammer" piece_type="Blade" mesh="test_mallet_mesh" length="20"><BuildData /></CraftingPiece></CraftingPieces>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "templates.xml"), """
            <CraftingTemplates><CraftingTemplate id="Visual"><PieceDatas><PieceData piece_type="Blade" build_order="0" /></PieceDatas></CraftingTemplate></CraftingTemplates>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "items.xml"), """
            <Items><CraftedItem id="unit_scaled_hammer" crafting_template="Visual"><Pieces><Piece id="hammer" Type="Blade" /></Pieces></CraftedItem></Items>
            """);
        var fbxFolder = Directory.CreateDirectory(Path.Combine(root, "unit-scaled-fbx"));
        // A 0.04 x 0.04 x 0.3 box long along source Z, with UnitScaleFactor=100 and UpAxis=2 (Z up).
        // Honouring the metadata gives a 30 unit tall mesh along Y; ignoring the scale leaves 0.3, ignoring the up axis leaves 0.04.
        File.WriteAllText(
            Path.Combine(fbxFolder.FullName, "test_mallet.fbx"),
            BuildAsciiFbx(100, 2, TestBox("test_mallet_mesh", -0.02f, 0.02f, -0.02f, 0.02f, 0f, 0.3f)));
        return new RenderFixture(subModulePath, fbxFolder.FullName);
    }

    private sealed record TestMesh(string Name, IReadOnlyList<(float X, float Y, float Z)> Vertices, IReadOnlyList<int[]> Polygons);

    private static TestMesh TestBox(string name, float minX, float maxX, float minY, float maxY, float minZ, float maxZ) =>
        new(
            name,
            [
                (minX, minY, minZ), (maxX, minY, minZ), (maxX, maxY, minZ), (minX, maxY, minZ),
                (minX, minY, maxZ), (maxX, minY, maxZ), (maxX, maxY, maxZ), (minX, maxY, maxZ),
            ],
            [[0, 1, 2, 3], [7, 6, 5, 4], [0, 4, 5, 1], [1, 5, 6, 2], [2, 6, 7, 3], [3, 7, 4, 0]]);

    // A prism with an elliptical cross-section in X/Z, long along Y. The many facets shade differently,
    // so the rendered image has real content instead of a few flat colour blocks.
    private static TestMesh TestPrism(string name, int sides, float radiusX, float radiusZ, float minY, float maxY)
    {
        var vertices = new List<(float X, float Y, float Z)>();
        foreach (var y in new[] { minY, maxY })
            for (var i = 0; i < sides; i++)
            {
                var angle = 2d * Math.PI * i / sides;
                vertices.Add(((float)(radiusX * Math.Cos(angle)), y, (float)(radiusZ * Math.Sin(angle))));
            }

        var polygons = new List<int[]>();
        for (var i = 0; i < sides; i++)
        {
            var next = (i + 1) % sides;
            polygons.Add([i, next, sides + next, sides + i]);
        }
        polygons.Add(Enumerable.Range(0, sides).Reverse().ToArray());
        polygons.Add(Enumerable.Range(sides, sides).ToArray());
        return new TestMesh(name, vertices, polygons);
    }

    // Shared invented geometry for the audit and render fixtures: two prisms, both long along Y (the crafting axis).
    // The grip is 10 wide and 100 long; the tip is 20 wide and 40 long.
    private static string WriteTestMeshFbx(string root)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "fbx"));
        File.WriteAllText(
            Path.Combine(folder.FullName, "test_meshes.fbx"),
            BuildAsciiFbx(
                1,
                1,
                TestPrism("test_grip_mesh", 12, 5f, 5f, 0f, 100f),
                TestPrism("test_tip_mesh", 12, 10f, 2f, 0f, 40f)));
        return folder.FullName;
    }

    private static string BuildAsciiFbx(int unitScale, int upAxis, params TestMesh[] meshes)
    {
        static string N(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append("; FBX 7.4.0 project file\nFBXHeaderExtension:  {\n\tFBXHeaderVersion: 1003\n\tFBXVersion: 7400\n}\n");
        text.Append("GlobalSettings:  {\n\tVersion: 1000\n\tProperties70:  {\n");
        text.Append($"\t\tP: \"UpAxis\", \"int\", \"Integer\", \"\",{upAxis}\n\t\tP: \"UpAxisSign\", \"int\", \"Integer\", \"\",1\n");
        text.Append("\t\tP: \"FrontAxis\", \"int\", \"Integer\", \"\",1\n\t\tP: \"FrontAxisSign\", \"int\", \"Integer\", \"\",-1\n");
        text.Append("\t\tP: \"CoordAxis\", \"int\", \"Integer\", \"\",0\n\t\tP: \"CoordAxisSign\", \"int\", \"Integer\", \"\",1\n");
        text.Append($"\t\tP: \"UnitScaleFactor\", \"double\", \"Number\", \"\",{unitScale}\n\t}}\n}}\nObjects:  {{\n");
        for (var i = 0; i < meshes.Length; i++)
        {
            var mesh = meshes[i];
            var coordinates = string.Join(",", mesh.Vertices.SelectMany(v => new[] { N(v.X), N(v.Y), N(v.Z) }));
            // FBX marks the last index of each polygon by storing it bitwise-negated.
            var indices = mesh.Polygons.SelectMany(polygon => polygon.Select((index, position) => position == polygon.Length - 1 ? -index - 1 : index)).ToArray();
            text.Append($"\tGeometry: {1001 + i}, \"Geometry::{mesh.Name}\", \"Mesh\" {{\n");
            text.Append($"\t\tVertices: *{mesh.Vertices.Count * 3} {{\n\t\t\ta: {coordinates}\n\t\t}}\n");
            text.Append($"\t\tPolygonVertexIndex: *{indices.Length} {{\n\t\t\ta: {string.Join(",", indices)}\n\t\t}}\n");
            text.Append("\t\tGeometryVersion: 124\n\t}\n");
            text.Append($"\tModel: {2001 + i}, \"Model::{mesh.Name}\", \"Mesh\" {{\n\t\tVersion: 232\n\t\tProperties70:  {{\n\t\t}}\n\t}}\n");
        }
        text.Append("}\nConnections:  {\n");
        for (var i = 0; i < meshes.Length; i++)
            text.Append($"\tC: \"OO\",{2001 + i},0\n\tC: \"OO\",{1001 + i},{2001 + i}\n");
        text.Append("}\n");
        return text.ToString();
    }

    private static RenderFixture CreateHolsterRenderModule(string root, string templateAttributes, string? holsterMesh)
    {
        var moduleData = Directory.CreateDirectory(Path.Combine(root, "ModuleData"));
        var subModulePath = Path.Combine(root, "SubModule.xml");
        File.WriteAllText(subModulePath, """
            <Module><Xmls><XmlNode><XmlName id="CraftingPieces" path="pieces" /><XmlName id="CraftingTemplates" path="templates" /><XmlName id="Items" path="items" /></XmlNode></Xmls></Module>
            """);
        var bladeData = holsterMesh is null ? "<BladeData />" : $"<BladeData holster_mesh=\"{holsterMesh}\" />";
        File.WriteAllText(Path.Combine(moduleData.FullName, "pieces.xml"), $"""
            <CraftingPieces>
              <CraftingPiece id="handle" piece_type="Handle" mesh="test_grip_mesh" length="20"><BuildData /></CraftingPiece>
              <CraftingPiece id="guard" piece_type="Guard" mesh="test_tip_mesh" length="20"><BuildData /></CraftingPiece>
              <CraftingPiece id="blade" piece_type="Blade" mesh="test_grip_mesh" length="40"><BuildData />{bladeData}</CraftingPiece>
            </CraftingPieces>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "templates.xml"), $"""
            <CraftingTemplates><CraftingTemplate id="Visual" {templateAttributes}><PieceDatas><PieceData piece_type="Handle" build_order="0" /><PieceData piece_type="Guard" build_order="1" /><PieceData piece_type="Blade" build_order="2" /></PieceDatas></CraftingTemplate></CraftingTemplates>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "items.xml"), """
            <Items><CraftedItem id="sheathed_test" crafting_template="Visual"><Pieces><Piece id="handle" Type="Handle" /><Piece id="guard" Type="Guard" /><Piece id="blade" Type="Blade" /></Pieces></CraftedItem></Items>
            """);
        return new RenderFixture(subModulePath, WriteTestMeshFbx(root));
    }

    private static RenderFixture CreateScaledRenderModule(string root)
    {
        var moduleData = Directory.CreateDirectory(Path.Combine(root, "ModuleData"));
        var subModulePath = Path.Combine(root, "SubModule.xml");
        File.WriteAllText(subModulePath, """
            <Module><Xmls><XmlNode><XmlName id="CraftingPieces" path="pieces" /><XmlName id="CraftingTemplates" path="templates" /><XmlName id="Items" path="items" /></XmlNode></Xmls></Module>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "pieces.xml"), """
            <CraftingPieces>
              <CraftingPiece id="wide_guard" piece_type="Guard" mesh="test_tip_mesh" length="20"><BuildData /></CraftingPiece>
              <CraftingPiece id="axis_blade" piece_type="Blade" mesh="test_grip_mesh" length="40"><BuildData /></CraftingPiece>
            </CraftingPieces>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "templates.xml"), """
            <CraftingTemplates><CraftingTemplate id="Visual"><PieceDatas><PieceData piece_type="Guard" build_order="0" /><PieceData piece_type="Blade" build_order="1" /></PieceDatas></CraftingTemplate></CraftingTemplates>
            """);
        File.WriteAllText(Path.Combine(moduleData.FullName, "items.xml"), """
            <Items>
              <CraftedItem id="unscaled_pair" crafting_template="Visual"><Pieces><Piece id="wide_guard" Type="Guard" scale_factor="100" /><Piece id="axis_blade" Type="Blade" scale_factor="100" /></Pieces></CraftedItem>
              <CraftedItem id="scaled_pair" crafting_template="Visual"><Pieces><Piece id="wide_guard" Type="Guard" scale_factor="50" /><Piece id="axis_blade" Type="Blade" scale_factor="50" /></Pieces></CraftedItem>
            </Items>
            """);
        return new RenderFixture(subModulePath, WriteTestMeshFbx(root));
    }

    private static string OffsetTransform(int offset) => $"""
        <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:template match="@*|node()"><xsl:copy><xsl:apply-templates select="@*|node()" /></xsl:copy></xsl:template>
          <xsl:template match="BuildData"><BuildData piece_offset="{offset}"><xsl:copy-of select="@*[name() != 'piece_offset']" /></BuildData></xsl:template>
        </xsl:stylesheet>
        """;

    private static CommandResult RunCli(params string[] arguments)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the build configuration.");
        var cliAssembly = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BannerlordCraftingTool.Cli", "bin", configuration, "net8.0-windows", "BannerlordCraftingTool.Cli.dll"));
        var startInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        startInfo.ArgumentList.Add(cliAssembly);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new CommandResult(process.ExitCode, standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult());
    }
}

/// Stands in for the FBX index so the holster rules can be exercised without loading a single mesh.
sealed class FakeMeshCatalog(params string[] meshNames) : IMeshCatalog
{
    private readonly HashSet<string> _names = new(meshNames, StringComparer.OrdinalIgnoreCase);

    public bool Contains(string meshName) => _names.Contains(meshName);

    public System.Windows.Media.Media3D.MeshGeometry3D? LoadMesh(string meshName) => null;
}

sealed record CraftedItemsFixture(string SubModulePath, string ItemsPath);
sealed record VisualAuditFixture(string SubModulePath, string FbxFolder);
sealed record RenderFixture(string SubModulePath, string FbxFolder);
sealed record CommandResult(int ExitCode, string Output);

sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
