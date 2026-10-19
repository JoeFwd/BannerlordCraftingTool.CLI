/// What the render command says about the weapon's holster: whether it was drawn, and if so how well it fits.
sealed record ScabbardReport(
    string Status,
    string? Mesh,
    RenderPieceReport? Measurements,
    float? MouthOffset,
    float? TipCoverage,
    List<string> HiddenPieceTypes,
    List<string> Warnings);

/// The report plus the drawable piece behind it; the piece is null whenever the status is not "rendered".
sealed record ScabbardResult(ScabbardReport Report, RenderPiece? Piece);
