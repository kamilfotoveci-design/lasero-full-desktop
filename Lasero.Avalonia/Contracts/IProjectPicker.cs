using CSharpFunctionalExtensions;

namespace Lasero.Avalonia.Contracts;

public interface IProjectPicker
{
    Task<Result<string?>> PickOpen(CancellationToken cancellationToken = default);
}
