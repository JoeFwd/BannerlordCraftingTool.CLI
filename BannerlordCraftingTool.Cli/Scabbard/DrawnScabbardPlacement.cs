/// BuildHolsterMesh: the drawn holster hangs at the blade's own pivot and scale.
sealed class DrawnScabbardPlacement : IScabbardPlacement
{
    public ScabbardPlacement? Locate(
        WeaponLayoutReport layout,
        IReadOnlyDictionary<string, CraftingPiece> piecesById,
        TemplateData? template)
    {
        // Eligibility has already refused weapons without a blade, so this piece is always present.
        var blade = layout.Pieces.First(piece => piece.Type.Equals("Blade", StringComparison.OrdinalIgnoreCase));
        return ScabbardPlacement.For(blade, piecesById);
    }
}
