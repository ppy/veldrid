// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Veldrid.MetalBindings;

namespace Veldrid.MTL
{
    internal class CommandBufferDictionary<T> : IEnumerable<(MTLCommandBuffer, T)>
    {
        private readonly List<(MTLCommandBuffer cb, T item)> items = new List<(MTLCommandBuffer, T)>();

        public void Add(MTLCommandBuffer cb, T value)
            => items.Add((cb, value));

        public bool TryRemove(MTLCommandBuffer cb, [NotNullWhen(true)] out T result)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (!items[i].cb.Equals(cb))
                    continue;

                result = items[i].item;
                items.RemoveAt(i);
                return true;
            }

            result = default;
            return false;
        }

        public bool TryGetValue(MTLCommandBuffer cb, [NotNullWhen(true)] out T result)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (!items[i].cb.Equals(cb))
                    continue;

                result = items[i].item;
                return true;
            }

            result = default;
            return false;
        }

        public List<(MTLCommandBuffer, T)>.Enumerator GetEnumerator()
            => items.GetEnumerator();

        IEnumerator<(MTLCommandBuffer, T)> IEnumerable<(MTLCommandBuffer, T)>.GetEnumerator()
            => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
            => GetEnumerator();

        public void Clear()
            => items.Clear();

        public int Count => items.Count;
    }
}
