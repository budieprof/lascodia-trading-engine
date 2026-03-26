using System.Collections;

namespace LascodiaTradingEngine.Application.Common.Utilities;

/// <summary>
/// Zero-copy read-only view over a contiguous range of an existing <see cref="IReadOnlyList{T}"/>.
/// Avoids the O(n) allocation that <c>.Take(n).ToList()</c> incurs on every bar
/// during backtest simulation.
/// </summary>
internal sealed class ReadOnlyListSlice<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> _source;
    private readonly int _offset;

    public int Count { get; }

    public ReadOnlyListSlice(IReadOnlyList<T> source, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > source.Count)
            throw new ArgumentOutOfRangeException();

        _source = source;
        _offset = offset;
        Count = count;
    }

    public ReadOnlyListSlice(IReadOnlyList<T> source, int count)
        : this(source, 0, count) { }

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _source[_offset + index];
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
            yield return _source[_offset + i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
