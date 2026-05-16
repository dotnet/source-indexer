using System;

namespace MiniRuntime;

/// <summary>
/// A trivial slice-like view over a <see cref="MiniArray{T}"/>.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public readonly struct MiniSpan<T>
{
    private readonly MiniArray<T> _source;
    private readonly int _start;
    private readonly int _length;

    /// <summary>
    /// Initializes a new <see cref="MiniSpan{T}"/> over the given array slice.
    /// </summary>
    /// <param name="source">The backing array.</param>
    /// <param name="start">The inclusive start index.</param>
    /// <param name="length">The slice length.</param>
    public MiniSpan(MiniArray<T> source, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(source);

        if ((uint)start > (uint)source.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if ((uint)length > (uint)(source.Count - start))
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _source = source;
        _start = start;
        _length = length;
    }

    /// <summary>Gets the length of the span.</summary>
    public int Length => _length;

    /// <summary>Gets a reference to the element at the given offset.</summary>
    /// <param name="index">The zero-based offset within the span.</param>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_length)
            {
                throw new IndexOutOfRangeException();
            }

            return _source[_start + index];
        }
    }

    /// <summary>Slices this span further.</summary>
    public MiniSpan<T> Slice(int start, int length) =>
        new MiniSpan<T>(_source, _start + start, length);
}
