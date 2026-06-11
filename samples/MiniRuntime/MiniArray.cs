using System;
using System.Collections;
using System.Collections.Generic;

namespace MiniRuntime;

/// <summary>
/// A minimal heap-backed array wrapper used to demonstrate generics and
/// indexer documentation in the indexed HTML output.
/// </summary>
/// <typeparam name="T">The element type stored in the array.</typeparam>
public sealed class MiniArray<T> : IReadOnlyList<T>
{
    private readonly T[] _items;

    /// <summary>
    /// Initializes a new <see cref="MiniArray{T}"/> with the given length.
    /// </summary>
    /// <param name="length">The number of elements the array will hold.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="length"/> is negative.
    /// </exception>
    public MiniArray(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _items = new T[length];
    }

    /// <summary>
    /// Gets or sets the element at the given index.
    /// </summary>
    /// <param name="index">The zero-based index.</param>
    public T this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    /// <summary>
    /// Gets the number of elements in the array.
    /// </summary>
    public int Count => _items.Length;

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _items.Length; i++)
        {
            yield return _items[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal T[] UnsafeBuffer => _items;
}
