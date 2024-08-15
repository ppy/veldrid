// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Veldrid.MetalBindings;

namespace Veldrid.MTL
{
    internal class CommandBufferUsageList<T>
    {
        private readonly List<(MTLCommandBuffer buffer, T value)> items = new List<(MTLCommandBuffer buffer, T item)>();

        public void Add(MTLCommandBuffer cb, T value)
            => items.Add((cb, value));

        public ItemsEnumerator EnumerateItems()
            => new ItemsEnumerator(items);

        public RemovalEnumerator EnumerateAndRemove(MTLCommandBuffer cb)
            => new RemovalEnumerator(items, cb);

        public bool Contains(MTLCommandBuffer cb)
        {
            foreach (var (buffer, _) in items)
            {
                if (buffer.Equals(cb))
                    return true;
            }

            return false;
        }

        public void Clear()
            => items.Clear();

        /// <summary>
        /// This is a basic enumerator for the list.
        /// </summary>
        public struct ItemsEnumerator : IEnumerator<T>, IEnumerable
        {
            private readonly List<(MTLCommandBuffer buffer, T value)> list;
            private int index;

            public ItemsEnumerator(List<(MTLCommandBuffer buffer, T value)> list)
            {
                this.list = list;
            }

            public bool MoveNext()
            {
                if (index == list.Count)
                    return false;

                Current = list[index].value;
                index++;

                return true;
            }

            public void Reset()
            {
                index = 0;
            }

            public T Current { get; private set; }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }

            public ItemsEnumerator GetEnumerator() => this;

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>
        /// This is a combined enumerate + remove enumerator for the list.
        ///
        /// It works by duplicating the items that shall be retained to the end of the list
        /// and then moving them in-place to the front of the list upon disposal.
        ///
        /// The combined operation has therefore O(n) time complexity.
        /// </summary>
        public struct RemovalEnumerator : IEnumerator<T>, IEnumerable
        {
            private readonly List<(MTLCommandBuffer buffer, T value)> list;
            private readonly MTLCommandBuffer cb;
            private readonly int count;
            private int index;

            public RemovalEnumerator(List<(MTLCommandBuffer buffer, T value)> list, MTLCommandBuffer cb)
            {
                this.list = list;
                this.cb = cb;

                count = list.Count;
                list.EnsureCapacity(count * 2);
            }

            public bool MoveNext()
            {
                while (true)
                {
                    if (index == count)
                        return false;

                    if (list[index].buffer.Equals(cb))
                        break;

                    // Track the item to be kept.
                    list.Add(list[index]);
                    index++;
                }

                Current = list[index].value;
                index++;

                return true;
            }

            public void Reset()
            {
                index = 0;
            }

            public T Current { get; private set; }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
                if (list.Count == 0)
                    return;

                int toKeepItemCount = list.Count - count;
                var listSpan = CollectionsMarshal.AsSpan(list);

                listSpan.Slice(count, toKeepItemCount).CopyTo(listSpan);
                list.RemoveRange(toKeepItemCount, list.Count - toKeepItemCount);
            }

            public RemovalEnumerator GetEnumerator() => this;

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
