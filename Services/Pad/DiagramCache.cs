using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Drawn diagrams in memory, the least recently used dropped first (spec 2.4). Nothing is
    /// written to disk. Not thread-safe: the renderer locks around it.
    /// </summary>
    public sealed class DiagramCache
    {
        public const int DefaultCapacity = 64;

        private readonly int _capacity;
        private readonly Dictionary<string, LinkedListNode<(string Key, DiagramResult Result)>> _map = new();
        private readonly LinkedList<(string Key, DiagramResult Result)> _order = new();

        public DiagramCache(int capacity = DefaultCapacity) => _capacity = Math.Max(1, capacity);

        public int Count => _map.Count;

        /// <summary>The result for <paramref name="key"/>, which becomes the most recently used.</summary>
        public bool TryGet(string key, [MaybeNullWhen(false)] out DiagramResult result)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                result = node.Value.Result;
                return true;
            }
            result = null;
            return false;
        }

        /// <summary>Keeps <paramref name="result"/> as the most recently used, dropping the least recently used beyond the capacity.</summary>
        public void Add(string key, DiagramResult result)
        {
            if (_map.TryGetValue(key, out var old))
            {
                _order.Remove(old);
                _map.Remove(key);
            }
            _map[key] = _order.AddFirst((key, result));
            while (_map.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}
