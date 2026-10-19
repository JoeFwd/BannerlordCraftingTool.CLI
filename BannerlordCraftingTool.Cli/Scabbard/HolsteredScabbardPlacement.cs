/// BuildHolsterMeshWithWeapon: the sheathed holster follows the template's piece_type_to_scale_holster_with,
/// and stays at the weapon origin when that piece type is Invalid or absent.
sealed class HolsteredScabbardPlacement : IScabbardPlacement
{
    public ScabbardPlacement? Locate(
        WeaponLayoutReport layout,
        IReadOnlyDictionary<string, CraftingPiece> piecesById,
        TemplateData? template)
    {
        var scaleType = template?.PieceTypeToScaleHolsterWith;
        if (string.IsNullOrWhiteSpace(scaleType) || scaleType.Equals("Invalid", StringComparison.OrdinalIgnoreCase))
            return ScabbardPlacement.AtOrigin();

        var reference = layout.Pieces.FirstOrDefault(piece => piece.Type.Equals(scaleType, StringComparison.OrdinalIgnoreCase));
        return reference is null
            ? ScabbardPlacement.AtOrigin($"The template scales its holster with '{scaleType}', which this weapon does not use; the holster stayed at pivot 0.")
            : ScabbardPlacement.For(reference, piecesById);
    }
}
