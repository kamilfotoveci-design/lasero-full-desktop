using System.Globalization;

namespace Lasero.Core.Import.Svg;

/// <summary>
/// Tokenizes an SVG path `d` string into command letters and numbers.
/// SVG path syntax allows numbers packed with no separator ("1.5-2.3", "10.5.5"
/// meaning 10.5 then .5) and arc flags packed as single digits ("0110" = four
/// separate 0/1 flags) — a naive split-on-whitespace tokenizer breaks on real
/// paths from Inkscape/Illustrator, so this reads character-by-character.
/// </summary>
internal sealed class SvgPathTokenizer
{
    private readonly string _s;
    private int _i;

    public SvgPathTokenizer(string pathData)
    {
        _s = pathData;
        _i = 0;
    }

    private void SkipSeparators()
    {
        while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ','))
            _i++;
    }

    public bool TryReadCommand(out char command)
    {
        SkipSeparators();
        if (_i >= _s.Length || !char.IsLetter(_s[_i])) { command = '\0'; return false; }
        command = _s[_i];
        _i++;
        return true;
    }

    public bool HasMoreNumbers()
    {
        SkipSeparators();
        return _i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '-' || _s[_i] == '+' || _s[_i] == '.');
    }

    /// <summary>Reads a single SVG arc flag: exactly one character, '0' or '1', with no separator required.</summary>
    public double ReadFlag()
    {
        SkipSeparators();
        if (_i >= _s.Length) throw new FormatException("Unexpected end of path data while reading a flag.");
        var c = _s[_i];
        if (c != '0' && c != '1') throw new FormatException($"Expected 0/1 flag at position {_i}, got '{c}'.");
        _i++;
        return c - '0';
    }

    public double ReadNumber()
    {
        SkipSeparators();
        var start = _i;
        if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
        while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
        if (_i < _s.Length && _s[_i] == '.')
        {
            _i++;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
        }
        if (_i < _s.Length && (_s[_i] is 'e' or 'E'))
        {
            var expStart = _i;
            _i++;
            if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
            if (_i < _s.Length && char.IsDigit(_s[_i]))
            {
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            else
            {
                _i = expStart; // not actually an exponent
            }
        }

        if (_i == start)
            throw new FormatException($"Expected a number at position {_i} in path data.");

        return double.Parse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
