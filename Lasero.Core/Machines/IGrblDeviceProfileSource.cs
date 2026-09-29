using Lasero.Core.Grbl;

namespace Lasero.Core.Machines;

/// <summary>Optional live GRBL profile feed. Keeps generic machine consumers independent of GRBL details.</summary>
public interface IGrblDeviceProfileSource
{
    GrblDeviceProfile? DeviceProfile { get; }
    event Action<GrblDeviceProfile?>? DeviceProfileChanged;
}
