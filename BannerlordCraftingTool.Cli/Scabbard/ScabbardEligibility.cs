/// Decides whether a weapon has a holster to draw at all, and names the reason when it does not.
/// Mirrors the guard clauses of CraftedDataView.BuildHolsterMesh and BuildHolsterMeshWithWeapon.
sealed class ScabbardEligibility(IMeshCatalog meshes, bool honorsAlwaysShowHolsterWithWeapon)
{
    public const string Eligible = "eligible";

    public ScabbardEligibilityResult Evaluate(
        WeaponLayoutReport layout,
        IReadOnlyDictionary<string, CraftingPiece> piecesById,
        TemplateData? template)
    {
        var warnings = new List<string>();
        ScabbardEligibilityResult Skip(string status, string? mesh = null) => new(status, mesh, warnings);

        if (template?.UseWeaponAsHolsterMesh == true)
            return Skip("template_uses_weapon_as_holster_mesh");

        var blade = layout.Pieces.FirstOrDefault(piece => piece.Type.Equals("Blade", StringComparison.OrdinalIgnoreCase));
        if (blade is null || !piecesById.TryGetValue(blade.Id, out var bladePiece))
            return Skip("no_blade_piece");
        if (honorsAlwaysShowHolsterWithWeapon && template?.AlwaysShowHolsterWithWeapon == true)
            return Skip("template_always_shows_holster_with_weapon");

        var holsterMesh = bladePiece.HolsterMesh;
        if (string.IsNullOrWhiteSpace(holsterMesh))
            return Skip("no_holster_mesh");
        if (meshes.Contains(holsterMesh + "_skeleton"))
        {
            warnings.Add("The engine deforms this holster against a skeleton; quiver-style holsters cannot be placed offline.");
            return Skip("unsupported_skeleton_holster", holsterMesh);
        }

        warnings.Add("Skeleton holsters live in compiled assets, so probing the FBX index for a '_skeleton' model cannot rule a quiver out.");
        return new ScabbardEligibilityResult(Eligible, holsterMesh, warnings);
    }
}

/// The eligibility verdict: a skip status, or Eligible together with the holster mesh to draw.
sealed record ScabbardEligibilityResult(string Status, string? Mesh, List<string> Warnings);
