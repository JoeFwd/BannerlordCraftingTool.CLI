/// Answers the fit question: does the holster mouth meet the guard, and does the holster enclose the blade tip.
/// The holster runs along the blade, so its mouth is its low end and its closed chape is its high end.
static class ScabbardMeasurer
{
    /// Measured against the blade base, not the guard. A guard's bounding box top is its highest
    /// point anywhere, so on a swept hilt it is the knucklebow tip sweeping up alongside the blade
    /// rather than the crossguard the mouth actually meets. Driving a guard-relative number to zero
    /// pushes the mouth to that tip. The blade base is where the blade truly enters the scabbard,
    /// so it is the one reference that holds for any guard shape.
    public static (float? MouthOffset, float? TipCoverage) Measure(Bounds scabbard, IReadOnlyList<RenderPiece> weaponPieces)
    {
        var blade = Find(weaponPieces, "Blade");
        return (
            blade is null ? null : scabbard.MinY - blade.MinY,
            blade is null ? null : scabbard.MaxY - blade.MaxY);
    }

    private static Bounds? Find(IReadOnlyList<RenderPiece> pieces, string type) =>
        pieces.FirstOrDefault(piece => piece.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && piece.Bounds is not null)?.Bounds;
}
