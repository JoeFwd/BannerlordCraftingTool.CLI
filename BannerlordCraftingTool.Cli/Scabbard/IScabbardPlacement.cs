/// Where the holster sits on the weapon. The drawn and sheathed engine paths pick different reference pieces.
interface IScabbardPlacement
{
    /// Null when the reference piece has a pivot the renderer cannot use.
    ScabbardPlacement? Locate(
        WeaponLayoutReport layout,
        IReadOnlyDictionary<string, CraftingPiece> piecesById,
        TemplateData? template);
}

/// The reference piece's pivot and scale, already resolved to what the holster mesh should be built with.
sealed record ScabbardPlacement(float Pivot, int ScaleFactor, bool FullScale, string? Warning)
{
    public static ScabbardPlacement? For(
        PieceReport reference,
        IReadOnlyDictionary<string, CraftingPiece> piecesById,
        string? warning = null)
    {
        if (!float.IsFinite(reference.Pivot)) return null;
        var fullScale = piecesById.TryGetValue(reference.Id, out var piece) && piece.FullScale;
        return new ScabbardPlacement(reference.Pivot, reference.ScaleFactor, fullScale, warning);
    }

    /// The engine's identity frame: an unscaled holster at the weapon origin.
    public static ScabbardPlacement AtOrigin(string? warning = null) => new(0f, 100, false, warning);
}
