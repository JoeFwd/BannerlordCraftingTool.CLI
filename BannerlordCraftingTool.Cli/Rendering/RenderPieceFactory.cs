/// Turns a placed crafting piece into a world-space RenderPiece: mesh lookup plus the scaled bounds the engine would give it.
static class RenderPieceFactory
{
    public static List<RenderPiece> Assemble(IMeshCatalog meshes, IEnumerable<PieceReport> pieces) =>
        pieces.Select(piece => Create(meshes, piece.Type, piece.Id, piece.Mesh, piece.ScaleFactor, piece.Pivot, piece.FullScale)).ToList();

    public static RenderPiece Create(IMeshCatalog meshes, string type, string id, string mesh, int scaleFactor, float pivot, bool fullScale)
    {
        var geometry = meshes.LoadMesh(mesh);
        var scale = scaleFactor * 0.01f;
        // The engine only scales a piece across the crafting axis when it is flagged full_scale.
        var lateralScale = fullScale ? scale : 1f;
        Bounds? worldBounds = null;
        if (geometry is not null)
        {
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
            foreach (var point in geometry.Positions)
            {
                var x = (float)point.X * lateralScale;
                var y = (float)point.Y * scale + pivot;
                var z = (float)point.Z * lateralScale;
                minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
                minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y);
                minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z);
            }
            worldBounds = new Bounds(minX, minY, minZ, maxX, maxY, maxZ);
        }
        return new RenderPiece(type, id, mesh, scaleFactor, pivot, geometry, scale, pivot, worldBounds, lateralScale);
    }
}
