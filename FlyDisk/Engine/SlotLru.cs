// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
namespace FlyDisk.Engine;

/// <summary>Array-backed exact LRU, oldest first. No per-hit allocation.</summary>
public sealed class SlotLru
{
    private readonly int[] previous, next;
    private int head = -1, tail = -1;
    public int Count { get; private set; }
    public SlotLru(int capacity) { previous = new int[capacity]; next = new int[capacity]; Clear(); }
    public void Clear() { Array.Fill(previous, -1); Array.Fill(next, -1); head = tail = -1; Count = 0; }
    public bool Contains(int slot) => slot == head || previous[slot] >= 0;
    public void Remove(int slot)
    {
        if (!Contains(slot)) return;
        int p = previous[slot], n = next[slot];
        if (p >= 0) next[p] = n; else head = n;
        if (n >= 0) previous[n] = p; else tail = p;
        previous[slot] = next[slot] = -1; Count--;
    }
    public void Touch(int slot)
    {
        if (slot == tail) return;
        Remove(slot); previous[slot] = tail;
        if (tail >= 0) next[tail] = slot; else head = slot;
        tail = slot; Count++;
    }
    public int PopOldest() { if (head < 0) throw new InvalidOperationException("Empty LRU"); int slot = head; Remove(slot); return slot; }
    public IEnumerable<int> OldestFirst() { for (int slot = head; slot >= 0; slot = next[slot]) yield return slot; }
}
