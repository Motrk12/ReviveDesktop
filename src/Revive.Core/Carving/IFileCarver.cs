using Revive.Core.IO;
using Revive.Core.Model;

namespace Revive.Core.Carving;

public sealed record CarveResult(long Length, string Extension, FileCategory Category, DateTime? Taken = null);

/// <summary>
/// Recognises one family of file formats by its header and works out where the file ends
/// by walking its internal structure.
/// </summary>
public interface IFileCarver
{
    /// <summary>First bytes this carver can start with; used to dispatch quickly.</summary>
    IReadOnlyList<byte> LeadBytes { get; }

    /// <summary>Cheap check against the first 512 bytes of a sector.</summary>
    bool Matches(ReadOnlySpan<byte> header);

    /// <summary>Walks the file starting at <paramref name="offset"/>. Returns null if it isn't a valid file.</summary>
    CarveResult? Carve(SourceReader reader, long offset);
}
