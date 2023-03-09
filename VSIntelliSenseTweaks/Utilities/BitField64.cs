using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VSIntelliSenseTweaks.Utilities
{
    internal struct BitField64
    {
        public ulong data;

        public bool GetBit(int index)
        {
            var mask = 1ul << index;
            return (data & mask) == mask;
        }

        public void SetBit(int index)
        {
            var mask = 1ul << index;
            data |= mask;
        }

        public void ClearBit(int index)
        {
            var mask = 1ul << index;
            data &= ~mask;
        }

        public static bool operator ==(BitField64 x, BitField64 y) => x.data == y.data;
        public static bool operator !=(BitField64 x, BitField64 y) => x.data != y.data;
        public static BitField64 operator &(BitField64 x, BitField64 y) => new BitField64 { data = x.data & y.data };
        public static BitField64 operator |(BitField64 x, BitField64 y) => new BitField64 { data = x.data | y.data };
        public static BitField64 operator ^(BitField64 x, BitField64 y) => new BitField64 { data = x.data ^ y.data };
        public static BitField64 operator ~(BitField64 x) => new BitField64 { data = ~x.data };
    }
}
