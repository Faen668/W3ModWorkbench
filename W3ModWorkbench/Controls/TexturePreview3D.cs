using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace W3ModWorkbench;

/// <summary>The shape a texture is put on in the 3D preview, as REDkit's texture viewer offers.</summary>
public enum PreviewShape { Sphere, Box, Cylinder, Plane }

/// <summary>
/// A texture put on a shape and lit, as REDkit's texture viewer shows it: a sphere, a box, a cylinder or a plane. A cube
/// map's six faces go round the shape by direction, each part of it showing the face it looks at. Drag to turn the shape, use the
/// wheel to come closer, and double-click to put it back.
/// </summary>
public sealed class TexturePreview3D : Border
{
    private readonly Viewport3D _viewport = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 45, NearPlaneDistance = 0.05, FarPlaneDistance = 100 };
    private readonly DirectionalLight _light = new(Color.FromRgb(210, 210, 210), new Vector3D(-1, -1, -1));
    private readonly Model3DGroup _shape = new();
    private readonly AxisAngleRotation3D _yaw = new(new Vector3D(0, 1, 0), -30);
    private readonly AxisAngleRotation3D _pitch = new(new Vector3D(1, 0, 0), 15);
    private PreviewShape _kind = PreviewShape.Sphere;
    private BitmapSource? _texture;
    private IReadOnlyList<BitmapSource>? _faces;
    private double _distance = 4.2, _lightAngle = 45;
    private Point _last;
    private bool _turning;

    public TexturePreview3D()
    {
        Background = Brushes.Transparent; // takes the mouse everywhere, not just over the shape
        ClipToBounds = true;
        Focusable = true;
        _viewport.Camera = _camera;
        _shape.Transform = new Transform3DGroup { Children = { new RotateTransform3D(_yaw), new RotateTransform3D(_pitch) } };
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(80, 80, 80)));
        scene.Children.Add(_light);
        scene.Children.Add(_shape);
        _viewport.Children.Add(new ModelVisual3D { Content = scene });
        Child = _viewport;
        PlaceCamera();
        PlaceLight();
    }

    /// <summary>The shape the texture is on. A cube map goes round it by direction, each part showing the face it looks at.</summary>
    public PreviewShape Shape
    {
        get => _kind;
        set { _kind = value; Rebuild(); }
    }

    /// <summary>Where the light comes from, in degrees round the shape (0 in front, 90 from the right).</summary>
    public double LightAngle
    {
        get => _lightAngle;
        set { _lightAngle = value; PlaceLight(); }
    }

    /// <summary>Puts a picture on the shape.</summary>
    public void ShowTexture(BitmapSource? texture)
    {
        _texture = texture;
        _faces = null;
        Rebuild();
    }

    /// <summary>Puts a cube map's faces round the shape, in the order the game keeps them: +X, −X, +Y, −Y, +Z, −Z.</summary>
    public void ShowCube(IReadOnlyList<BitmapSource> faces)
    {
        _faces = faces.Count == 6 ? faces : null;
        _texture = null;
        Rebuild();
    }

    private void Rebuild()
    {
        _shape.Children.Clear();
        if (_faces is not null)
        {
            if (_kind is PreviewShape.Sphere or PreviewShape.Box)
                for (int face = 0; face < 6; face++) _shape.Children.Add(Model(CubeFace(face, _kind == PreviewShape.Sphere), _faces[face]));
            else
            {
                // each part of the shape shows the face it looks at from the middle; the plane looks ahead, as through a window
                var faces = _kind == PreviewShape.Plane ? SplitByFace(Plane(), p => new Vector3D(p.X, p.Y, -1.2)) : SplitByFace(Cylinder(), p => (Vector3D)p);
                for (int face = 0; face < 6; face++)
                    if (faces[face].Positions.Count > 0) _shape.Children.Add(Model(faces[face], _faces[face]));
            }
            return;
        }
        if (_texture is null) return;
        var mesh = _kind switch
        {
            PreviewShape.Box => Box(),
            PreviewShape.Cylinder => Cylinder(),
            PreviewShape.Plane => Plane(),
            _ => Sphere(),
        };
        _shape.Children.Add(Model(mesh, _texture));
    }

    private static GeometryModel3D Model(MeshGeometry3D mesh, BitmapSource texture)
    {
        var brush = new ImageBrush(texture) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill };
        brush.Freeze();
        mesh.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        // the plane is seen from both sides
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    // ---------------------------------------------------------------- shapes, each round the middle

    /// <summary>Adds a triangle facing away from the middle of the shape, whichever way round its corners are given.</summary>
    private static void Triangle(MeshGeometry3D mesh, int a, int b, int c)
    {
        Point3D pa = mesh.Positions[a], pb = mesh.Positions[b], pc = mesh.Positions[c];
        var normal = Vector3D.CrossProduct(pb - pa, pc - pa);
        var outward = new Vector3D((pa.X + pb.X + pc.X) / 3, (pa.Y + pb.Y + pc.Y) / 3, (pa.Z + pb.Z + pc.Z) / 3);
        if (Vector3D.DotProduct(normal, outward) < 0) (b, c) = (c, b);
        mesh.TriangleIndices.Add(a);
        mesh.TriangleIndices.Add(b);
        mesh.TriangleIndices.Add(c);
    }

    /// <summary>A grid of quads, <paramref name="columns"/> by <paramref name="rows"/>, from a point for each corner.</summary>
    private static MeshGeometry3D Grid(int columns, int rows, Func<double, double, (Point3D Position, Vector3D Normal)> at, MeshGeometry3D? into = null)
    {
        var mesh = into ?? new MeshGeometry3D();
        int first = mesh.Positions.Count;
        for (int y = 0; y <= rows; y++)
            for (int x = 0; x <= columns; x++)
            {
                double u = (double)x / columns, v = (double)y / rows;
                var (position, normal) = at(u, v);
                mesh.Positions.Add(position);
                mesh.Normals.Add(normal);
                mesh.TextureCoordinates.Add(new Point(u, v));
            }
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                int i = first + y * (columns + 1) + x, below = i + columns + 1;
                Triangle(mesh, i, i + 1, below + 1);
                Triangle(mesh, i, below + 1, below);
            }
        return mesh;
    }

    private static MeshGeometry3D Sphere() => Grid(64, 32, (u, v) =>
    {
        // half a turn round, so the middle of the texture faces the front and its seam the back
        double around = u * 2 * Math.PI + Math.PI, down = v * Math.PI;
        var n = new Vector3D(-Math.Sin(down) * Math.Sin(around), Math.Cos(down), Math.Sin(down) * Math.Cos(around));
        return ((Point3D)n, n);
    });

    private static MeshGeometry3D Cylinder()
    {
        const double radius = 0.9, half = 1;
        var mesh = Grid(64, 1, (u, v) =>
        {
            double around = u * 2 * Math.PI + Math.PI;
            var n = new Vector3D(-Math.Sin(around), 0, Math.Cos(around));
            return (new Point3D(n.X * radius, half - v * 2 * half, n.Z * radius), n);
        });
        // the ends, with the texture laid flat across them
        foreach (double y in new[] { half, -half })
        {
            int centre = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(0, y, 0));
            mesh.Normals.Add(new Vector3D(0, Math.Sign(y), 0));
            mesh.TextureCoordinates.Add(new Point(0.5, 0.5));
            for (int i = 0; i <= 64; i++)
            {
                double around = i * 2 * Math.PI / 64;
                mesh.Positions.Add(new Point3D(-Math.Sin(around) * radius, y, Math.Cos(around) * radius));
                mesh.Normals.Add(new Vector3D(0, Math.Sign(y), 0));
                mesh.TextureCoordinates.Add(new Point(0.5 - Math.Sin(around) / 2, 0.5 + Math.Cos(around) / 2));
                if (i > 0) Triangle(mesh, centre, centre + i, centre + i + 1);
            }
        }
        return mesh;
    }

    private static MeshGeometry3D Plane() => Grid(1, 1, (u, v) => (new Point3D(u * 2.4 - 1.2, 1.2 - v * 2.4, 0), new Vector3D(0, 0, 1)));

    private static MeshGeometry3D Box()
    {
        var mesh = new MeshGeometry3D();
        // each face the whole texture, the right way up from outside
        (Vector3D Normal, Vector3D Right, Vector3D Up)[] faces =
        [
            (new(0, 0, 1), new(1, 0, 0), new(0, 1, 0)), (new(0, 0, -1), new(-1, 0, 0), new(0, 1, 0)),
            (new(1, 0, 0), new(0, 0, -1), new(0, 1, 0)), (new(-1, 0, 0), new(0, 0, 1), new(0, 1, 0)),
            (new(0, 1, 0), new(1, 0, 0), new(0, 0, -1)), (new(0, -1, 0), new(1, 0, 0), new(0, 0, 1)),
        ];
        foreach (var (normal, right, up) in faces)
            Grid(1, 1, (u, v) => ((Point3D)(normal + right * (u * 2 - 1) + up * (1 - v * 2)), normal), mesh);
        return mesh;
    }

    /// <summary>
    /// One face of a cube map round a sphere or a box: each point goes the way the face looks there, as the game reads a
    /// cube map (Direct3D's faces, turned round to WPF's right-handed space).
    /// </summary>
    private static MeshGeometry3D CubeFace(int face, bool round) => Grid(24, 24, (u, v) =>
    {
        double s = u * 2 - 1, t = v * 2 - 1;
        var d = face switch
        {
            0 => new Vector3D(1, -t, -s),
            1 => new Vector3D(-1, -t, s),
            2 => new Vector3D(s, 1, t),
            3 => new Vector3D(s, -1, -t),
            4 => new Vector3D(s, -t, 1),
            _ => new Vector3D(-s, -t, -1),
        };
        d.Z = -d.Z;
        if (round) d.Normalize();
        var normal = d;
        normal.Normalize();
        return ((Point3D)d, normal);
    });

    /// <summary>
    /// A shape cut up for a cube map: each triangle goes with the face it looks at (by <paramref name="look"/>, the way
    /// from the middle at a point), its corners placed on that face as the game reads a cube map.
    /// </summary>
    private static MeshGeometry3D[] SplitByFace(MeshGeometry3D shape, Func<Point3D, Vector3D> look)
    {
        var faces = Enumerable.Range(0, 6).Select(_ => new MeshGeometry3D()).ToArray();
        var indices = shape.TriangleIndices;
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            var corners = new[] { indices[i], indices[i + 1], indices[i + 2] };
            // Direct3D's faces are left-handed: turn WPF's way round first
            var ways = corners.Select(c => look(shape.Positions[c])).Select(v => new Vector3D(v.X, v.Y, -v.Z)).ToArray();
            var mid = ways[0] + ways[1] + ways[2];
            double ax = Math.Abs(mid.X), ay = Math.Abs(mid.Y), az = Math.Abs(mid.Z);
            int face = ax >= ay && ax >= az ? (mid.X > 0 ? 0 : 1) : ay >= az ? (mid.Y > 0 ? 2 : 3) : (mid.Z > 0 ? 4 : 5);
            var mesh = faces[face];
            foreach (var (corner, d) in corners.Zip(ways))
            {
                double major = Math.Max(1e-6, face switch { 0 or 1 => Math.Abs(d.X), 2 or 3 => Math.Abs(d.Y), _ => Math.Abs(d.Z) });
                var (u, v) = face switch
                {
                    0 => (-d.Z, -d.Y),
                    1 => (d.Z, -d.Y),
                    2 => (d.X, d.Z),
                    3 => (d.X, -d.Z),
                    4 => (d.X, -d.Y),
                    _ => (-d.X, -d.Y),
                };
                mesh.Positions.Add(shape.Positions[corner]);
                mesh.Normals.Add(shape.Normals[corner]);
                mesh.TextureCoordinates.Add(new Point(Math.Clamp((u / major + 1) / 2, 0, 1), Math.Clamp((v / major + 1) / 2, 0, 1)));
                mesh.TriangleIndices.Add(mesh.Positions.Count - 1);
            }
        }
        return faces;
    }

    // ---------------------------------------------------------------- camera, light and mouse

    private void PlaceCamera()
    {
        _camera.Position = new Point3D(0, 0, _distance);
        _camera.LookDirection = new Vector3D(0, 0, -_distance);
        _camera.UpDirection = new Vector3D(0, 1, 0);
    }

    private void PlaceLight()
    {
        double a = _lightAngle * Math.PI / 180;
        // from the front and a little above, round to the side the slider says
        _light.Direction = new Vector3D(-Math.Sin(a), -0.5, -Math.Cos(a));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2)
        {
            (_yaw.Angle, _pitch.Angle, _distance) = (-30, 15, 4.2);
            PlaceCamera();
            return;
        }
        _turning = true;
        _last = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_turning) return;
        var now = e.GetPosition(this);
        _yaw.Angle += (now.X - _last.X) * 0.5;
        _pitch.Angle = Math.Clamp(_pitch.Angle + (now.Y - _last.Y) * 0.5, -89, 89);
        _last = now;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _turning = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.9 : 1.1), 1.6, 20);
        PlaceCamera();
        e.Handled = true;
    }
}
