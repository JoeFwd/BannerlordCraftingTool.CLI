using System.Windows.Media.Media3D;

/// The mesh lookups the render pipeline needs, so the rules around a mesh can be exercised without loading FBX.
interface IMeshCatalog
{
    bool Contains(string meshName);

    MeshGeometry3D? LoadMesh(string meshName);
}
