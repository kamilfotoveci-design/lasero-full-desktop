namespace Lasero.Core.Grbl;

public readonly record struct Position(double X, double Y, double Z)
{
    public static readonly Position Zero = new(0, 0, 0);

    public Position Offset(Position by) => new(X + by.X, Y + by.Y, Z + by.Z);

    public Position Minus(Position other) => new(X - other.X, Y - other.Y, Z - other.Z);

    public override string ToString() => $"X{X:0.###} Y{Y:0.###} Z{Z:0.###}";
}
