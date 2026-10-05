namespace Lasero.App.Controls.Motion;

/// <summary>One drawable piece of the wordmark: a letter (or the laser head) as closed contours in
/// wordmark pixel space. Each contour is a flat x0,y0,x1,y1,... array. Holes (the counters of A, R, O)
/// are separate contours; the fill uses the even-odd rule.</summary>
internal sealed record WordmarkGroup(string Name, double[][] Loops);
