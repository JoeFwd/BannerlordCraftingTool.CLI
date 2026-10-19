/// Runs the holster pipeline in order: eligibility, then placement, then mesh, then measurements.
sealed class ScabbardComposer(IMeshCatalog meshes, ScabbardEligibility eligibility, IScabbardPlacement placement)
{
    /// The drawn view follows BuildHolsterMesh; the sheathed view follows BuildHolsterMeshWithWeapon.
    public static ScabbardComposer For(IMeshCatalog meshes, bool holstered) => holstered
        ? new ScabbardComposer(meshes, new ScabbardEligibility(meshes, honorsAlwaysShowHolsterWithWeapon: false), new HolsteredScabbardPlacement())
        : new ScabbardComposer(meshes, new ScabbardEligibility(meshes, honorsAlwaysShowHolsterWithWeapon: true), new DrawnScabbardPlacement());

    /// weaponPieces are measured against, so they must include the piece types the holster hides.
    public ScabbardResult Compose(
        WeaponLayoutReport layout,
        IReadOnlyList<RenderPiece> weaponPieces,
        IReadOnlyDictionary<string, CraftingPiece> piecesById,
        TemplateData? template,
        List<string> hiddenTypes)
    {
        var verdict = eligibility.Evaluate(layout, piecesById, template);
        var warnings = verdict.Warnings;
        ScabbardResult Skip(string status, string? mesh) =>
            new(new ScabbardReport(status, mesh, null, null, null, hiddenTypes, warnings), null);

        if (verdict.Status != ScabbardEligibility.Eligible) return Skip(verdict.Status, verdict.Mesh);
        var mesh = verdict.Mesh!;

        var located = placement.Locate(layout, piecesById, template);
        if (located is null) return Skip("invalid_pivot", mesh);
        if (located.Warning is not null) warnings.Add(located.Warning);

        var piece = RenderPieceFactory.Create(meshes, "Scabbard", mesh, mesh, located.ScaleFactor, located.Pivot, located.FullScale);
        if (piece.Bounds is null) return Skip("mesh_not_found", mesh);

        var (mouthOffset, tipCoverage) = ScabbardMeasurer.Measure(piece.Bounds, weaponPieces);
        return new ScabbardResult(
            new ScabbardReport("rendered", mesh, RenderPieceReport.From(piece), mouthOffset, tipCoverage, hiddenTypes, warnings),
            piece);
    }
}
