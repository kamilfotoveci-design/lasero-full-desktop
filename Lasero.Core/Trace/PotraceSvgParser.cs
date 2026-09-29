using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Core.Trace;

/// <summary>Reads Potrace's SVG cubic geometry into the authoritative editable VectorPath model.</summary>
internal static class PotraceSvgParser
{
    public static IReadOnlyList<TracedVectorObject> Parse(
        string svgPath, int pixelWidth, int pixelHeight, double mmPerPixel, RgbColor color)
    {
        using var reader = XmlReader.Create(svgPath, new XmlReaderSettings
        {
            // Potrace 1.16 emits an SVG 1.0 DOCTYPE. Parse its internal subset while
            // blocking network/file entity resolution via the null resolver below.
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            MaxCharactersInDocument = 64L * 1024 * 1024,
        });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("Potrace SVG is empty.");
        if (root.Name.LocalName != "svg") throw new InvalidDataException("Potrace output is not SVG.");
        var viewBox = ReadViewBox(root, pixelWidth, pixelHeight);
        var subpaths = new List<VectorSubpath>();
        ReadElements(root, Affine.Identity, viewBox, pixelWidth, pixelHeight, mmPerPixel, subpaths);
        if (subpaths.Count == 0) return [];
        // Each SVG subpath remains independent, including holes and islands. Their opposite
        // windings are preserved by the common affine transform, so compound fill semantics survive.
        return [new TracedVectorObject(new VectorPath { Subpaths = subpaths }, color)];
    }

    private static void ReadElements(
        XElement element, Affine parent, Rect viewBox,
        int pixelWidth, int pixelHeight, double mmPerPixel, List<VectorSubpath> result)
    {
        var transform = parent.After(ParseTransform((string?)element.Attribute("transform")));
        if (element.Name.LocalName == "path")
        {
            var data = (string?)element.Attribute("d") ?? "";
            foreach (var path in new PathReader(data).Read())
            {
                if (!path.IsClosed || path.Nodes.Count < 3) continue;
                var nodes = path.Nodes.Select(node => new VectorNode(
                    Map(node.Anchor),
                    node.HandleIn is { } hIn ? Map(hIn) : null,
                    node.HandleOut is { } hOut ? Map(hOut) : null,
                    node.Type)).ToArray();
                result.Add(new VectorSubpath { Nodes = nodes, IsClosed = true });
            }
        }
        foreach (var child in element.Elements())
            ReadElements(child, transform, viewBox, pixelWidth, pixelHeight, mmPerPixel, result);

        Position Map(Position point)
        {
            var transformed = transform.Apply(point);
            var x = (transformed.X - viewBox.X) * pixelWidth / viewBox.Width * mmPerPixel;
            var y = (transformed.Y - viewBox.Y) * pixelHeight / viewBox.Height * mmPerPixel;
            if (!double.IsFinite(x) || !double.IsFinite(y))
                throw new InvalidDataException("Potrace SVG contains non-finite geometry.");
            return new Position(x, y, 0);
        }
    }

    private static Rect ReadViewBox(XElement root, int width, int height)
    {
        var text = (string?)root.Attribute("viewBox");
        if (string.IsNullOrWhiteSpace(text)) return new Rect(0, 0, width, height);
        var numbers = NumberList(text);
        if (numbers.Count != 4 || !double.IsFinite(numbers[0]) || !double.IsFinite(numbers[1]) ||
            !double.IsFinite(numbers[2]) || !double.IsFinite(numbers[3]) ||
            numbers[2] <= 0 || numbers[3] <= 0)
            throw new InvalidDataException("Potrace SVG has an invalid viewBox.");
        return new Rect(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    private static Affine ParseTransform(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return Affine.Identity;
        var output = Affine.Identity;
        var index = 0;
        while (index < source.Length)
        {
            SkipSeparators(source, ref index);
            if (index == source.Length) break;
            var start = index;
            while (index < source.Length && char.IsLetter(source[index])) index++;
            var operation = source[start..index];
            SkipSeparators(source, ref index);
            if (index >= source.Length || source[index++] != '(')
                throw new InvalidDataException("Unsupported Potrace SVG transform.");
            start = index;
            while (index < source.Length && source[index] != ')') index++;
            if (index == source.Length) throw new InvalidDataException("Unclosed Potrace SVG transform.");
            var args = NumberList(source[start..index++]);
            var next = operation switch
            {
                "translate" when args.Count is 1 or 2 => new Affine(1, 0, 0, 1, args[0], args.Count == 2 ? args[1] : 0),
                "scale" when args.Count is 1 or 2 => new Affine(args[0], 0, 0, args.Count == 2 ? args[1] : args[0], 0, 0),
                "matrix" when args.Count == 6 => new Affine(args[0], args[1], args[2], args[3], args[4], args[5]),
                _ => throw new InvalidDataException("Unsupported Potrace SVG transform: " + operation),
            };
            output = output.After(next);
        }
        return output;
    }

    private static List<double> NumberList(string input)
    {
        var values = new List<double>();
        var index = 0;
        while (index < input.Length)
        {
            SkipSeparators(input, ref index);
            if (index == input.Length) break;
            values.Add(ReadNumber(input, ref index));
        }
        return values;
    }

    private static void SkipSeparators(string input, ref int index)
    {
        while (index < input.Length && (char.IsWhiteSpace(input[index]) || input[index] == ',')) index++;
    }

    private static double ReadNumber(string input, ref int index)
    {
        SkipSeparators(input, ref index);
        var start = index;
        if (index < input.Length && input[index] is '-' or '+') index++;
        var digit = false;
        while (index < input.Length && char.IsDigit(input[index])) { index++; digit = true; }
        if (index < input.Length && input[index] == '.')
        {
            index++;
            while (index < input.Length && char.IsDigit(input[index])) { index++; digit = true; }
        }
        if (!digit) throw new InvalidDataException("Invalid Potrace SVG path number.");
        if (index < input.Length && input[index] is 'e' or 'E')
        {
            index++;
            if (index < input.Length && input[index] is '-' or '+') index++;
            var exponentStart = index;
            while (index < input.Length && char.IsDigit(input[index])) index++;
            if (index == exponentStart) throw new InvalidDataException("Invalid Potrace SVG exponent.");
        }
        if (!double.TryParse(input[start..index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value)) throw new InvalidDataException("Non-finite Potrace SVG path number.");
        return value;
    }

    private readonly record struct Rect(double X, double Y, double Width, double Height);

    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static Affine Identity => new(1, 0, 0, 1, 0, 0);
        public Position Apply(Position p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F, 0);
        // This ∘ next: SVG's `translate(...) scale(...)` applies scale first.
        public Affine After(Affine next) => new(
            A * next.A + C * next.B, B * next.A + D * next.B,
            A * next.C + C * next.D, B * next.C + D * next.D,
            A * next.E + C * next.F + E, B * next.E + D * next.F + F);
    }

    private sealed class PathReader(string data)
    {
        private int _index;
        private Position _cursor;
        private Position _start;
        private char _command;
        private List<VectorNode>? _nodes;
        private readonly List<VectorSubpath> _paths = [];

        public IReadOnlyList<VectorSubpath> Read()
        {
            while (true)
            {
                SkipSeparators(data, ref _index);
                if (_index >= data.Length) break;
                if (char.IsLetter(data[_index])) _command = data[_index++];
                else if (_command == '\0') throw new InvalidDataException("Potrace SVG path has no command.");
                switch (_command)
                {
                    case 'M': case 'm': Move(); break;
                    case 'L': case 'l': Line(); break;
                    case 'H': case 'h': Horizontal(); break;
                    case 'V': case 'v': Vertical(); break;
                    case 'C': case 'c': Cubic(); break;
                    case 'Z': case 'z': Close(); break;
                    default: throw new InvalidDataException("Unsupported Potrace SVG path command: " + _command);
                }
            }
            return _paths;
        }

        private void Move()
        {
            Flush(false);
            _cursor = Point(_command == 'm');
            _start = _cursor;
            _nodes = [VectorNode.CornerAt(_cursor)];
            _command = _command == 'm' ? 'l' : 'L';
        }

        private void Line() => AddLine(Point(_command == 'l'));

        private void Horizontal()
        {
            RequireOpen();
            var x = ReadNumber(data, ref _index);
            AddLine(new Position(_command == 'h' ? _cursor.X + x : x, _cursor.Y, 0));
        }

        private void Vertical()
        {
            RequireOpen();
            var y = ReadNumber(data, ref _index);
            AddLine(new Position(_cursor.X, _command == 'v' ? _cursor.Y + y : y, 0));
        }

        private void Cubic()
        {
            RequireOpen();
            var relative = _command == 'c';
            var origin = _cursor;
            var c1 = Pair(relative, origin);
            var c2 = Pair(relative, origin);
            var end = Pair(relative, origin);
            var last = _nodes![^1];
            _nodes[^1] = last with { HandleOut = c1 };
            // Preserve independent Potrace handles; a corner can also be reached by a cubic.
            // The editor may infer smooth nodes later from tangent alignment.
            _nodes.Add(new VectorNode(end, c2, null, VectorNodeType.Corner));
            _cursor = end;
        }

        private void AddLine(Position end)
        {
            RequireOpen();
            if (end != _cursor) _nodes!.Add(VectorNode.CornerAt(end));
            _cursor = end;
        }

        private Position Point(bool relative) => Pair(relative, _cursor);

        private Position Pair(bool relative, Position origin)
        {
            var x = ReadNumber(data, ref _index);
            var y = ReadNumber(data, ref _index);
            return new Position(relative ? origin.X + x : x, relative ? origin.Y + y : y, 0);
        }

        private void Close()
        {
            RequireOpen();
            Flush(true);
            _cursor = _start;
            _command = '\0';
        }

        private void RequireOpen()
        {
            if (_nodes is null) throw new InvalidDataException("Potrace SVG path does not start with a move.");
        }

        private void Flush(bool closed)
        {
            if (_nodes is null) return;
            if (closed && _nodes.Count > 1 && _nodes[^1].Anchor == _start)
            {
                var closing = _nodes[^1];
                _nodes[0] = _nodes[0] with { HandleIn = closing.HandleIn };
                _nodes.RemoveAt(_nodes.Count - 1);
            }
            if (_nodes.Count >= 2) _paths.Add(new VectorSubpath { Nodes = _nodes, IsClosed = closed });
            _nodes = null;
        }
    }
}
