using System;
using System.Diagnostics;

namespace IntellisenseTweaks.Utilities
{
    public ref struct BitSpan
    {
        private Span<int> data;

        public static int GetRequiredIntCount(int n_bits)
        {
            Debug.Assert(n_bits > 0);
            return (n_bits - 1) / 32 + 1;
        }

        public BitSpan(Span<int> data)
        {
            this.data = data;
        }

        public bool this[int index]
        {
            get => GetBit(index);
            set
            {
                if (value)
                {
                    SetBit(index);
                }
                else
                {
                    ClearBit(index);
                }
            }
        }

        public bool GetBit(int index)
        {
            var intIndex = Math.DivRem(index, 32, out var bitIndex);
            var mask = 1 << bitIndex;
            return (data[intIndex] & mask) == mask;
        }

        public void SetBit(int index)
        {
            var intIndex = Math.DivRem(index, 32, out var bitIndex);
            var mask = 1 << bitIndex;
            data[intIndex] |= mask;
        }

        public void ClearBit(int index)
        {
            var intIndex = Math.DivRem(index, 32, out var bitIndex);
            var mask = 1 << bitIndex;
            data[intIndex] &= ~mask;
        }
    }
}
